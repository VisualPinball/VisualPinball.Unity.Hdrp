// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Applies the host app's <see cref="VpeGraphicsSettings"/> to HDRP at runtime: camera anti-aliasing,
	/// ball reflections, the shadows of the lights around the table, ray traced or not, and the live Volume effects (SSR/RT
	/// reflections and their quality, SSAO, SSGI/RT-GI, recursive ray tracing, Bloom).
	/// Registered before any scene loads, the same pattern as <c>HdrpMaterialResolver</c>. Engine-level
	/// settings (resolution, vsync, quality level) are applied host-side and don't pass through here.
	/// </summary>
	public sealed class HdrpGraphicsApplier : IVpeGraphicsApplier
	{
		private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register()
		{
			VpeGraphics.Register(new HdrpGraphicsApplier());
			VpeGraphics.UpscalerSupport = IsUpscalerSupported;
			DynamicResolutionHandler.SetDynamicResScaler(() => _stpPercentage, DynamicResScalePolicyType.ReturnsPercentage);
			_fsr2Crashes = SystemInfo.graphicsDeviceVendorID == 0x10DE
				&& SystemInfo.graphicsDeviceName.IndexOf("RTX", System.StringComparison.OrdinalIgnoreCase) < 0;
			VpeGraphics.RenderScaleSource = RenderScale;
			VpeGraphics.BallReflectionsSupported = true;
			VpeGraphics.MotionBlurSupported = true;
			VpeGraphics.DepthOfFieldHandler = SetDepthOfField;
			VpeGraphics.ReleaseRenderBuffersHandler = ReleaseRenderBuffers;
			VpeGraphics.EffectSupport = IsEffectSupported;
			VpeGraphics.TierEffectSupport = IsEffectSupportedAt;
			VpeGraphics.EffectiveSettingsSource = DescribeEffective;
			_shadows = VpeShadows.High;
			_rayTracedShadows = false;
			_tracing = false;
			// without a domain reload, the last play session's holds are still counted
			ReleaseCameraRayTracing();
			ShadowLights.Clear();
			Originals.Clear();
#if ENABLE_NVIDIA && ENABLE_NVIDIA_MODULE
			ApplyDlssPresets();
#endif
		}

		// What the camera's volume stack and frame settings come to, which is what HDRP renders with. The
		// stack is the one HDRP updated for the camera's last frame.
		private static string DescribeEffective(Camera cam)
		{
			var hd = HDCamera.GetOrCreate(cam);
			var stack = hd.volumeStack;
			if (stack == null) {
				return "";
			}
			var frame = hd.frameSettings;
			var bloom = stack.GetComponent<Bloom>();
			var ao = stack.GetComponent<ScreenSpaceAmbientOcclusion>();
			var ssr = stack.GetComponent<ScreenSpaceReflection>();
			var gi = stack.GetComponent<GlobalIllumination>();
			var shadows = stack.GetComponent<HDShadowSettings>();
			var blur = stack.GetComponent<MotionBlur>();
			string On(bool on) => on ? "on" : "off";
			var shadowLights = 0;
			var tracedLights = 0; // of them, the ones whose shadows are ray traced
			foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude)) {
				if (light.enabled && light.shadows != LightShadows.None) {
					shadowLights++;
					if (frame.IsEnabled(FrameSettingsField.RayTracing) && frame.IsEnabled(FrameSettingsField.ScreenSpaceShadows)
						&& light.TryGetComponent<HDAdditionalLightData>(out var hdLight) && hdLight.useRayTracedShadows
						&& (light.type != LightType.Directional || hdLight.useScreenSpaceShadows)) {
						tracedLights++;
					}
				}
			}
			return string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"asset {0}; aa {1}; bloom {2:0.###} (frame {3}); ao {4:0.###} (frame {5}); ssr {6}/transparent {7} quality {8} (frame {9}); ssgi {10} (frame {11}); "
				+ "shadows {12} light(s), {19} ray traced, {13} cascade(s) to {14:0.##} m (frame {15}); motion blur {16:0.##} (frame {17}); scale {18:0.###}",
				GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none",
				cam.TryGetComponent<HDAdditionalCameraData>(out var data) ? data.antialiasing.ToString() : "?",
				bloom.intensity.value, On(frame.IsEnabled(FrameSettingsField.Bloom)),
				ao.intensity.value, On(frame.IsEnabled(FrameSettingsField.SSAO)),
				On(ssr.enabled.value), On(ssr.enabledTransparent.value), ssr.quality.value, On(frame.IsEnabled(FrameSettingsField.SSR)),
				On(gi.enable.value), On(frame.IsEnabled(FrameSettingsField.SSGI)),
				shadowLights, shadows.cascadeShadowSplitCount.value, shadows.maxShadowDistance.value, On(frame.IsEnabled(FrameSettingsField.ShadowMaps)),
				blur.intensity.value, On(frame.IsEnabled(FrameSettingsField.MotionBlur)),
				RenderScale(), tracedLights)
				+ DescribeCost(frame, ssr, gi, ao, bloom)
				+ DescribeDlss(data);
		}

		// HDRP's own word on whether DLSS rendered the camera's last frame: allowing it isn't enough, the asset
		// can rank another upscaler first and path tracing goes without.
		private static readonly System.Reflection.FieldInfo CameraCanRenderDlss = typeof(HDAdditionalCameraData).GetField("cameraCanRenderDLSS",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

		// The DLSS model that ran, which its cost hangs on more than on anything else.
		private static string DescribeDlss(HDAdditionalCameraData data)
		{
#if ENABLE_NVIDIA && ENABLE_NVIDIA_MODULE
			if (data == null || !data.allowDeepLearningSuperSampling || GraphicsSettings.currentRenderPipeline is not HDRenderPipelineAsset asset) {
				return "";
			}
			if (CameraCanRenderDlss?.GetValue(data) is false) {
				return "";
			}
			var preset = DlssPresetOf(asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings, _upscalerQuality);
			return "; dlss preset " + PresetName(preset);
#else
			return "";
#endif
		}

		// What the effects' cost hangs on: the size they work at, the steps their rays take and their
		// denoising, as the volumes and the pipeline asset's quality levels resolve them.
		private static string DescribeCost(FrameSettings frame, ScreenSpaceReflection ssr, GlobalIllumination gi, ScreenSpaceAmbientOcclusion ao, Bloom bloom)
		{
			var shadows = GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset
				? asset.currentPlatformRenderPipelineSettings.hdShadowInitParams.punctualShadowFilteringQuality.ToString()
				: "?";
			return string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"; lit {0}; ssr {1}, {2} steps; ssgi {3} size, {4} steps, denoise {5}/{6} size/second {7}; ao {8} size, {9} steps x {10}; bloom {11}; punctual shadow filter {12}",
				frame.litShaderMode,
				ssr.usedAlgorithm.value, ssr.rayMaxIterations,
				gi.fullResolutionSS.value ? "full" : "half", gi.maxRaySteps,
				gi.denoiseSS ? "on" : "off", gi.halfResolutionDenoiserSS ? "half" : "full", gi.secondDenoiserPassSS ? "on" : "off",
				ao.fullResolution ? "full" : "half", ao.stepCount, ao.directionCount,
				bloom.resolution, shadows);
		}

		// A pipeline asset can leave an effect out (the Performant tier has no SSR and no SSGI): its volume
		// component and frame setting then do nothing.
		private static bool IsEffectSupported(VpeEffect effect) => IsEffectSupported(GraphicsSettings.currentRenderPipeline, effect);

		// The same for a tier that isn't the active one: its asset says what it renders without a switch to it.
		private static bool IsEffectSupportedAt(int qualityLevel, VpeEffect effect)
			=> qualityLevel >= 0 && qualityLevel < QualitySettings.names.Length
				&& IsEffectSupported(QualitySettings.GetRenderPipelineAssetAt(qualityLevel) ?? GraphicsSettings.defaultRenderPipeline, effect);

		private static bool IsEffectSupported(RenderPipelineAsset pipeline, VpeEffect effect)
		{
			if (pipeline is not HDRenderPipelineAsset asset) {
				return false;
			}
			var settings = asset.currentPlatformRenderPipelineSettings;
			return effect switch {
				VpeEffect.ScreenSpaceReflections => settings.supportSSR,
				VpeEffect.AmbientOcclusion => settings.supportSSAO,
				VpeEffect.ScreenSpaceGlobalIllumination => settings.supportSSGI,
				// traced shadows are drawn into the screen-space shadow buffer, which the asset has to have as well
				VpeEffect.RayTracedShadows => SystemInfo.supportsRayTracing && settings.supportRayTracing
					&& settings.hdShadowInitParams.supportScreenSpaceShadows,
				_ => true,
			};
		}

		// HDRP sizes its buffers for the largest camera it rendered and keeps them; after a larger
		// off-screen target is gone, they go back to the screen's size.
		private static void ReleaseRenderBuffers(int width, int height)
		{
			if (RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline && width > 0 && height > 0) {
				pipeline.ResetRTHandleReferenceSize(width, height);
			}
		}

		// HDRP resets the active DynamicResolutionHandler after each render, so read the main camera's own size
		private static float RenderScale()
		{
			var cam = Camera.main;
			if (cam == null || cam.pixelWidth <= 0) {
				return 1f;
			}
			var hd = HDCamera.GetOrCreate(cam);
			return hd.actualWidth > 0 ? Mathf.Min(1f, hd.actualWidth / (float)cam.pixelWidth) : 1f;
		}

		// the user's choice, kept so the camera can be reapplied when the cabinet view restricts it (-1 = none yet)
		private static int _antiAliasing = -1;
		private static VpeUpscaler _upscaler = VpeUpscaler.Off;
		private static VpeUpscalerQuality _upscalerQuality = VpeUpscalerQuality.Quality;

		public void Apply(VpeGraphicsSettings settings)
		{
			_antiAliasing = Mathf.Clamp(settings.AntiAliasing, 0, 3);
			_upscaler = (VpeUpscaler)settings.Upscaler;
			var quality = (VpeUpscalerQuality)Mathf.Clamp(settings.UpscalerQuality, 0, 3);
			_upscalerQuality = VpeGraphics.SupportedUpscalerQuality(_upscaler, quality);
			if (_upscalerQuality != quality) {
				Logger.Info("{Upscaler} doesn't run in {Quality} mode on the {Gpu:l}, {Instead} instead.", _upscaler, quality, SystemInfo.graphicsDeviceName, _upscalerQuality);
			}
			var upscaling = _upscaler;
			if (!IsUpscalerSupported(_upscaler)) {
				Logger.Info("{Upscaler} doesn't run on the {Gpu:l}, so nothing upscales.", _upscaler, SystemInfo.graphicsDeviceName);
				upscaling = VpeUpscaler.Off;
			}
			ApplyCameraAntialiasing(Camera.main);
			ApplyBallReflections(Camera.main, settings.BallReflections);
			ApplyCameraMotionBlur(Camera.main, settings.MotionBlur != (int)VpeMotionBlur.Off);
			ApplyVolume(settings);
			ApplyCameraRayTracing(Camera.main, _tracing);
			HdrpShadowCache.Refresh();
			Logger.Info("Upscaling {Upscaler} {Quality} (DLSS detected: {Dlss}, FSR 2 detected: {Fsr2})",
				upscaling, _upscalerQuality, HDDynamicResolutionPlatformCapabilities.DLSSDetected, HDDynamicResolutionPlatformCapabilities.FSR2Detected);
		}

		// HDRP only blurs where the camera's frame settings allow it, whatever the volume says, and a camera with
		// its own frame settings can have it off (the player's does). The balls move while the camera stands
		// still, so the blur also needs the objects' motion vectors; those stay on once it was wanted.
		private static void ApplyCameraMotionBlur(Camera cam, bool on)
		{
			if (cam == null || !cam.TryGetComponent<HDAdditionalCameraData>(out var hd) || !hd.customRenderingSettings) {
				return;
			}
			ref var mask = ref hd.renderingPathCustomFrameSettingsOverrideMask;
			ref var frame = ref hd.renderingPathCustomFrameSettings;
			mask.mask[(uint)FrameSettingsField.MotionBlur] = true;
			frame.SetEnabled(FrameSettingsField.MotionBlur, on);
			if (on) {
				foreach (var field in new[] { FrameSettingsField.MotionVectors, FrameSettingsField.ObjectMotionVectors }) {
					if (mask.mask[(uint)field] && !frame.IsEnabled(field)) {
						frame.SetEnabled(field, true);
					}
				}
			}
		}

		// With ray tracing in its frame settings, a camera builds the acceleration structure once a light or a
		// volume asks for rays, and leaves out every light that isn't included for ray tracing. So the camera
		// only traces while a setting wants it: with all of them off, it renders as on an asset without ray
		// tracing. Traced shadows are drawn into the screen-space shadow buffer, which the camera needs too.
		// Both go through CameraFrameSettings, which other components (autofocus, ball reflections) share: one
		// of them giving the camera its own settings back would otherwise drop these too.
		private static void ApplyCameraRayTracing(Camera cam, bool on)
		{
			HDAdditionalCameraData hd = null;
			if (cam != null) {
				cam.TryGetComponent(out hd);
			}
			// nothing to keep off where the asset doesn't trace
			if (!IsRayTracingAvailable()) {
				hd = null;
			}
			// by reference: a destroyed camera equals null, and still has to be released
			if (!ReferenceEquals(_rayTracingCamera, hd)) {
				ReleaseCameraRayTracing();
				if (hd == null) {
					return;
				}
				CameraFrameSettings.Enable(hd, FrameSettingsField.RayTracing);
				_rayTracingCamera = hd;
			}
			if (hd == null) {
				return;
			}
			CameraFrameSettings.Set(hd, FrameSettingsField.RayTracing, on);
			if (on != _screenSpaceShadowsHeld) {
				if (on) {
					CameraFrameSettings.Enable(hd, FrameSettingsField.ScreenSpaceShadows);
				} else {
					CameraFrameSettings.Release(hd, FrameSettingsField.ScreenSpaceShadows);
				}
				_screenSpaceShadowsHeld = on;
			}
		}

		private static void ReleaseCameraRayTracing()
		{
			if (!ReferenceEquals(_rayTracingCamera, null)) {
				if (_screenSpaceShadowsHeld) {
					CameraFrameSettings.Release(_rayTracingCamera, FrameSettingsField.ScreenSpaceShadows);
				}
				CameraFrameSettings.Release(_rayTracingCamera, FrameSettingsField.RayTracing);
			}
			_rayTracingCamera = null;
			_screenSpaceShadowsHeld = false;
		}

		// the camera whose ray tracing frame setting the applier holds, and whether it holds its screen-space shadows
		private static HDAdditionalCameraData _rayTracingCamera;
		private static bool _screenSpaceShadowsHeld;

		// The component lives on the camera that renders the balls; it's only added once it's wanted.
		private static void ApplyBallReflections(Camera cam, bool on)
		{
			if (cam == null) {
				return;
			}
			var reflections = cam.GetComponent<BallReflectionsComponent>();
			if (reflections == null) {
				if (!on) {
					return;
				}
				reflections = cam.gameObject.AddComponent<BallReflectionsComponent>();
			}
			reflections.enabled = on;
		}

		// The auto focus also lives on the main camera once it's wanted. Restarting it makes it look for the
		// volume again, which a table load replaces. Off goes to every volume the camera sees, since a table's
		// profile can bring a depth of field of its own, which the auto focus never touched.
		private static void SetDepthOfField(bool on)
		{
			var cam = Camera.main;
			if (cam == null) {
				return;
			}
			var focus = cam.GetComponent<AutoFocusComponent>();
			if (focus == null && on) {
				focus = cam.gameObject.AddComponent<AutoFocusComponent>();
				focus.FocusSpeed = 20f; // refocuses as quickly as the tables set it
			} else if (focus != null) {
				focus.enabled = false;
				focus.enabled = on;
			}
			if (!on) {
				foreach (var volume in FindGlobalVolumes(cam)) {
					if (ProfileOf(volume).TryGet<DepthOfField>(out var dof)) {
						dof.focusMode.Override(DepthOfFieldMode.Off);
					}
				}
			}
		}

		// DLSS needs the NVIDIA plugin and an RTX GPU, FSR 2 a DX12 or Vulkan device; HDRP detects both. STP is
		// HDRP's own and needs hardware dynamic resolution, which HDRP has on every API but these.
		private static bool IsUpscalerSupported(VpeUpscaler upscaler) => upscaler switch {
			VpeUpscaler.Dlss => HDDynamicResolutionPlatformCapabilities.DLSSDetected,
			VpeUpscaler.Fsr2 => HDDynamicResolutionPlatformCapabilities.FSR2Detected && !_fsr2Crashes,
			VpeUpscaler.Stp => SystemInfo.graphicsDeviceType is not (GraphicsDeviceType.Direct3D11 or GraphicsDeviceType.OpenGLES3
				or GraphicsDeviceType.OpenGLCore or GraphicsDeviceType.WebGPU),
			VpeUpscaler.Off => true,
			_ => false,
		};

		// AMD's plugin crashes FSR 2 on NVIDIA GPUs without RTX: on a GTX 1080 (Windows 10, D3D12) it throws on
		// the graphics thread while it sets the upscaler up, in Performance mode after a table loads, and in
		// Balanced mode on the menu's first frames. It runs on RTX cards; STP is there for the others.
		private static bool _fsr2Crashes;

		/// <summary>
		/// The chosen anti-aliasing, or what the camera's cabinet projection allows instead. Without a choice
		/// from the host, <paramref name="unconfigured"/> stands in for it.
		/// </summary>
		internal static void ApplyCameraAntialiasing(Camera cam, HDAdditionalCameraData.AntialiasingMode? unconfigured = null)
		{
			if (cam == null) {
				return;
			}
			var hd = cam.GetComponent<HDAdditionalCameraData>();
			if (hd == null) {
				return;
			}
			var requested = _antiAliasing >= 0 ? (VpeAntiAliasing)_antiAliasing : ToVpe(unconfigured ?? hd.antialiasing);
			// DLSS and FSR 2 do the anti-aliasing themselves; HDRP would still run FXAA after them (it jitters
			// regardless). STP is HDRP's TAA upsampling, and only runs where the camera has TAA.
			hd.antialiasing = ApplyCameraUpscaler(cam, hd) switch {
				VpeUpscaler.Stp => HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing,
				VpeUpscaler.Off => ToHdrp(CabinetProjection.EffectiveAntiAliasing(cam, requested)),
				_ => HDAdditionalCameraData.AntialiasingMode.None,
			};
		}

		// The upscalers are temporal like TAA: where the cabinet projection rules out TAA, they're off too.
		// The pipeline assets list DLSS, FSR 2 and STP, in that order, and HDRP takes the first the camera
		// allows: DLSS and FSR 2 by the camera's flags, at the scale their mode implies, and STP when neither
		// is allowed, at the scale the dynamic resolution scaler returns. Returns the one that's active.
		private static VpeUpscaler ApplyCameraUpscaler(Camera cam, HDAdditionalCameraData hd)
		{
			var upscaler = _upscaler;
			if (!IsUpscalerSupported(upscaler) || !AssetOffers(upscaler) || CabinetProjection.EffectiveAntiAliasing(cam, VpeAntiAliasing.Taa) != VpeAntiAliasing.Taa) {
				upscaler = VpeUpscaler.Off;
			}
			hd.allowDynamicResolution = upscaler != VpeUpscaler.Off;
			if (upscaler == VpeUpscaler.Stp) {
				_stpPercentage = 100f * StpScale(_upscalerQuality);
			}

			hd.allowDeepLearningSuperSampling = upscaler == VpeUpscaler.Dlss;
			hd.deepLearningSuperSamplingUseCustomQualitySettings = true;
			hd.deepLearningSuperSamplingQuality = DlssQuality(_upscalerQuality);
			hd.deepLearningSuperSamplingUseCustomAttributes = true;
			hd.deepLearningSuperSamplingUseOptimalSettings = true;

			hd.allowFidelityFX2SuperResolution = upscaler == VpeUpscaler.Fsr2;
			hd.fidelityFX2SuperResolutionUseCustomQualitySettings = true;
			hd.fidelityFX2SuperResolutionQuality = (uint)_upscalerQuality; // AMD FSR2Quality has the same order
			hd.fidelityFX2SuperResolutionUseCustomAttributes = true;
			hd.fidelityFX2SuperResolutionUseOptimalSettings = true;
			return upscaler;
		}

		// What the dynamic resolution scaler returns, for every camera: STP's screen percentage. DLSS and FSR 2
		// set their own with their optimal settings, and a camera without an upscaler doesn't scale at all.
		private static float _stpPercentage = 100f;

		// The share of the output STP renders per mode, as URP's STP and the other upscalers have it.
		private static float StpScale(VpeUpscalerQuality quality) => quality switch {
			VpeUpscalerQuality.Quality => 1f / 1.5f,
			VpeUpscalerQuality.Balanced => 1f / 1.7f,
			VpeUpscalerQuality.Performance => 0.5f,
			_ => 1f / 3f,
		};

		// HDRP only runs an upscaler the active pipeline asset lists, with its dynamic resolution on; without that,
		// asking the camera for one would still turn its own anti-aliasing off.
		private static bool AssetOffers(VpeUpscaler upscaler)
		{
			if (upscaler == VpeUpscaler.Off) {
				return true;
			}
			if (GraphicsSettings.currentRenderPipeline is not HDRenderPipelineAsset asset) {
				return false;
			}
			var drs = asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings;
			var name = upscaler switch { VpeUpscaler.Dlss => "DLSS", VpeUpscaler.Fsr2 => "FSR2", _ => "STP" };
			// STP also needs the asset's dynamic resolution to be the hardware one (or a forced percentage)
			if (upscaler == VpeUpscaler.Stp && drs.dynResType != DynamicResolutionType.Hardware && !drs.forceResolution) {
				return false;
			}
			return drs.enabled && drs.advancedUpscalerNames != null && drs.advancedUpscalerNames.Contains(name);
		}

		// NVIDIA's DLSSQuality: MaximumPerformance 0, Balanced 1, MaximumQuality 2, UltraPerformance 3.
		private static uint DlssQuality(VpeUpscalerQuality quality) => quality switch {
			VpeUpscalerQuality.Quality => 2,
			VpeUpscalerQuality.Balanced => 1,
			VpeUpscalerQuality.Performance => 0,
			_ => 3,
		};

#if ENABLE_NVIDIA && ENABLE_NVIDIA_MODULE
		// The DLSS model per mode. NVIDIA's defaults in the DLSS that comes with Unity 6.5 (K for Quality, Balanced
		// and DLAA, M for Performance, L for Ultra Performance) are made for RTX 40 and newer. RTX 20 and 30 cards
		// lack the FP8 units they lean on: there M and L cost two to three times what K does, and K nearly three
		// times the older CNN model F. On an RTX 2080 Ti at 4K, F takes 1.3 ms, K 3.5, M 7.5 and L 10.6 (NVIDIA's
		// programming guide), of the 8.3 ms a frame at 120 Hz has, so those cards take the cheapest model a mode
		// has: F where it's offered (Ultra Performance and DLAA), K elsewhere (Ada and Blackwell workstation
		// cards, called RTX 2000 Ada and the like, have FP8). Newer cards get NVIDIA's
		// defaults by name, so that a benchmark can say which model ran. The command line can set one preset
		// for every mode instead: --dlss-preset=f|j|k|l|m|default.
		private static readonly System.Text.RegularExpressions.Regex RtxWithoutFp8 = new(
			@"\bRTX\s*(20|30)\d\d\b|\bTITAN RTX\b|\bQuadro RTX\b|\bRTX\s*A\d{3,4}\b",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		private static readonly System.Text.RegularExpressions.Regex RtxWithFp8 = new(
			@"\bAda\b|\bBlackwell\b|\bRTX PRO\b",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);

		// Only in a player, before the first frame: a new setting for the active pipeline asset makes HDRP build
		// the pipeline again, and in the editor the assets would keep what was set.
		private static void ApplyDlssPresets()
		{
			if (Application.isEditor || SystemInfo.graphicsDeviceVendorID != 0x10DE) {
				return;
			}
			var forced = CommandLineDlssPreset();
			var name = SystemInfo.graphicsDeviceName;
			var older = RtxWithoutFp8.IsMatch(name) && !RtxWithFp8.IsMatch(name);
			var offered = new List<string>();
			UnityEngine.NVIDIA.DLSSPreset For(UnityEngine.NVIDIA.DLSSQuality mode)
			{
				// the DLSS knows which models it has for a mode (in 310.5, F only for Ultra Performance and DLAA);
				// where it can't tell, the first choice stands
				var available = UnityEngine.NVIDIA.GraphicsDevice.GetAvailableDLSSPresetsForQuality(mode);
				bool Has(UnityEngine.NVIDIA.DLSSPreset preset) => available == 0 || (available & (uint)preset) != 0;
				offered.Add($"{mode} {(available == 0 ? "?" : string.Join("", System.Enum.GetValues(typeof(UnityEngine.NVIDIA.DLSSPreset)).Cast<UnityEngine.NVIDIA.DLSSPreset>().Where(p => p != UnityEngine.NVIDIA.DLSSPreset.Preset_Default && (available & (uint)p) != 0).Select(PresetName)))}");
				var choices = forced is { } one ? new[] { one }
					: older ? new[] { UnityEngine.NVIDIA.DLSSPreset.Preset_F, UnityEngine.NVIDIA.DLSSPreset.Preset_K } // the cheapest there is
					: new[] { mode switch {
						UnityEngine.NVIDIA.DLSSQuality.MaximumPerformance => UnityEngine.NVIDIA.DLSSPreset.Preset_M,
						UnityEngine.NVIDIA.DLSSQuality.UltraPerformance => UnityEngine.NVIDIA.DLSSPreset.Preset_L,
						_ => UnityEngine.NVIDIA.DLSSPreset.Preset_K,
					} };
				foreach (var preset in choices) {
					if (preset == UnityEngine.NVIDIA.DLSSPreset.Preset_Default || Has(preset)) {
						return preset;
					}
				}
				Logger.Warn("DLSS has no preset {Presets} for {Mode} here, NVIDIA's default instead.", string.Join(" or ", choices.Select(PresetName)), mode);
				return UnityEngine.NVIDIA.DLSSPreset.Preset_Default;
			}
			var quality = For(UnityEngine.NVIDIA.DLSSQuality.MaximumQuality);
			var balanced = For(UnityEngine.NVIDIA.DLSSQuality.Balanced);
			var performance = For(UnityEngine.NVIDIA.DLSSQuality.MaximumPerformance);
			var ultra = For(UnityEngine.NVIDIA.DLSSQuality.UltraPerformance);
			var dlaa = For(UnityEngine.NVIDIA.DLSSQuality.DLAA);

			var assets = new List<HDRenderPipelineAsset>();
			for (var i = 0; i < QualitySettings.names.Length; i++) {
				if (QualitySettings.GetRenderPipelineAssetAt(i) is HDRenderPipelineAsset asset && !assets.Contains(asset)) {
					assets.Add(asset);
				}
			}
			if (GraphicsSettings.defaultRenderPipeline is HDRenderPipelineAsset fallback && !assets.Contains(fallback)) {
				assets.Add(fallback);
			}
			foreach (var asset in assets) {
				var settings = asset.currentPlatformRenderPipelineSettings;
				var drs = settings.dynamicResolutionSettings;
				if (drs.DLSSRenderPresetForQuality == (uint)quality && drs.DLSSRenderPresetForBalanced == (uint)balanced
					&& drs.DLSSRenderPresetForPerformance == (uint)performance && drs.DLSSRenderPresetForUltraPerformance == (uint)ultra
					&& drs.DLSSRenderPresetForDLAA == (uint)dlaa) {
					continue;
				}
				drs.DLSSRenderPresetForQuality = (uint)quality;
				drs.DLSSRenderPresetForBalanced = (uint)balanced;
				drs.DLSSRenderPresetForPerformance = (uint)performance;
				drs.DLSSRenderPresetForUltraPerformance = (uint)ultra;
				drs.DLSSRenderPresetForDLAA = (uint)dlaa;
				settings.dynamicResolutionSettings = drs;
				asset.currentPlatformRenderPipelineSettings = settings;
			}
			Logger.Info("DLSS presets on the {Gpu:l} ({Why:l}): quality {Quality:l}, balanced {Balanced:l}, performance {Performance:l}, ultra performance {Ultra:l}, DLAA {Dlaa:l} (offered: {Offered:l})",
				name, forced != null ? "--dlss-preset" : older ? "RTX 20 or 30, the cheapest model" : "NVIDIA's defaults",
				PresetName(quality), PresetName(balanced), PresetName(performance), PresetName(ultra), PresetName(dlaa), string.Join(", ", offered));
		}

		// --dlss-preset=k or --dlss-preset k; null where the command line doesn't set one.
		private static UnityEngine.NVIDIA.DLSSPreset? CommandLineDlssPreset()
		{
			var args = System.Environment.GetCommandLineArgs();
			for (var i = 0; i < args.Length; i++) {
				string value = null;
				if (args[i].StartsWith("--dlss-preset=", System.StringComparison.OrdinalIgnoreCase)) {
					value = args[i].Substring("--dlss-preset=".Length);
				} else if (string.Equals(args[i], "--dlss-preset", System.StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) {
					value = args[i + 1];
				}
				if (value == null) {
					continue;
				}
				value = value.Trim();
				if (string.Equals(value, "default", System.StringComparison.OrdinalIgnoreCase)) {
					return UnityEngine.NVIDIA.DLSSPreset.Preset_Default;
				}
				if (System.Enum.TryParse<UnityEngine.NVIDIA.DLSSPreset>("Preset_" + value.ToUpperInvariant(), out var preset)) {
					return preset;
				}
				Logger.Warn("--dlss-preset: no preset \"{Value:l}\" (f, j, k, l, m or default).", value);
			}
			return null;
		}

		private static UnityEngine.NVIDIA.DLSSPreset DlssPresetOf(GlobalDynamicResolutionSettings drs, VpeUpscalerQuality quality) => (UnityEngine.NVIDIA.DLSSPreset)(quality switch {
			VpeUpscalerQuality.Quality => drs.DLSSRenderPresetForQuality,
			VpeUpscalerQuality.Balanced => drs.DLSSRenderPresetForBalanced,
			VpeUpscalerQuality.Performance => drs.DLSSRenderPresetForPerformance,
			_ => drs.DLSSRenderPresetForUltraPerformance,
		});

		private static string PresetName(UnityEngine.NVIDIA.DLSSPreset preset)
			=> preset == UnityEngine.NVIDIA.DLSSPreset.Preset_Default ? "default" : preset.ToString().Replace("Preset_", "");
#endif

		// HDRP orders its modes None, FXAA, TAA, SMAA; VPE's are None, FXAA, SMAA, TAA.
		private static HDAdditionalCameraData.AntialiasingMode ToHdrp(VpeAntiAliasing aa) => aa switch {
			VpeAntiAliasing.Fxaa => HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing,
			VpeAntiAliasing.Smaa => HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing,
			VpeAntiAliasing.Taa => HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing,
			_ => HDAdditionalCameraData.AntialiasingMode.None,
		};

		private static VpeAntiAliasing ToVpe(HDAdditionalCameraData.AntialiasingMode aa) => aa switch {
			HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing => VpeAntiAliasing.Fxaa,
			HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing => VpeAntiAliasing.Smaa,
			HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing => VpeAntiAliasing.Taa,
			_ => VpeAntiAliasing.None,
		};

		// The share of the frame time the shutter stays open (HDRP's own default is 0.5). At 60 frames a second a
		// fast ball crosses about its own width per frame, so High streaks it by less than half of that.
		private static float MotionBlurIntensity(int level) => (VpeMotionBlur)level switch {
			VpeMotionBlur.Low => 0.3f,
			VpeMotionBlur.Medium => 0.6f,
			VpeMotionBlur.High => 1.2f,
			_ => 0f,
		};

		// A volume with what its profile holds, for the log: the components, and for the effects the settings
		// switch, the value that says whether they render (a star marks one the profile overrides).
		private static string DescribeVolume(Volume volume)
			=> $"{volume.name} (priority {volume.priority.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}{(volume.HasInstantiatedProfile() ? "" : ", shared profile")}) {DescribeProfile(volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile)}";

		private static string DescribeProfile(VolumeProfile profile, bool effectsOnly = false)
		{
			if (profile == null) {
				return "none";
			}
			string Mark(VolumeParameter parameter) => parameter.overrideState ? "*" : "";
			var parts = new List<string>();
			foreach (var component in profile.components) {
				if (component == null) {
					continue;
				}
				var text = component.GetType().Name + (component.active ? "" : " (inactive)");
				if (effectsOnly && component is not (Bloom or ScreenSpaceAmbientOcclusion or ScreenSpaceReflection or GlobalIllumination)) {
					continue; // a default profile has every component there is
				}
				switch (component) {
					case Bloom bloom:
						text += $" intensity {bloom.intensity.value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{Mark(bloom.intensity)}";
						break;
					case ScreenSpaceAmbientOcclusion ao:
						text += $" intensity {ao.intensity.value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{Mark(ao.intensity)}";
						break;
					case ScreenSpaceReflection ssr:
						text += $" enabled {ssr.enabled.value}{Mark(ssr.enabled)} transparent {ssr.enabledTransparent.value}{Mark(ssr.enabledTransparent)}";
						break;
					case GlobalIllumination gi:
						text += $" enable {gi.enable.value}{Mark(gi.enable)}";
						break;
				}
				parts.Add(text);
			}
			return $"{profile.name} [{string.Join(", ", parts)}]";
		}

		// What a volume parameter held before a setting overrode it, with the component it belongs to: a
		// setting that's turned up again gives the volume's own value back.
		private static readonly Dictionary<VolumeParameter, (VolumeComponent Owner, bool Overridden, object Value)> Originals = new(new ByReference());

		// A volume parameter equals another one with the same value, and its hash changes with its value: as a
		// key it has to be the object itself.
		private sealed class ByReference : IEqualityComparer<VolumeParameter>
		{
			public bool Equals(VolumeParameter a, VolumeParameter b) => ReferenceEquals(a, b);
			public int GetHashCode(VolumeParameter parameter) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(parameter);
		}

		private static void Force<T>(VolumeComponent owner, VolumeParameter<T> parameter, T value)
		{
			if (!Originals.ContainsKey(parameter)) {
				Originals[parameter] = (owner, parameter.overrideState, parameter.value);
			}
			parameter.Override(value);
		}

		private static void Release<T>(VolumeParameter<T> parameter)
		{
			if (Originals.Remove(parameter, out var original)) {
				parameter.value = (T)original.Value;
				parameter.overrideState = original.Overridden;
			}
		}

		private static void ForgetOrphanedOriginals()
		{
			List<VolumeParameter> orphaned = null;
			foreach (var entry in Originals) {
				if (entry.Value.Owner == null) {
					(orphaned ??= new List<VolumeParameter>()).Add(entry.Key);
				}
			}
			if (orphaned != null) {
				foreach (var parameter in orphaned) {
					Originals.Remove(parameter);
				}
			}
		}

		private static void ApplyVolume(VpeGraphicsSettings s)
		{
			// All active global volumes blend, and the table's and the player's can share a priority, so
			// the settings go to every one of them that has the effect, not just the one that happens to
			// win the tie (which made the result depend on load order).
			ReleaseOrphanedProfiles();
			ForgetOrphanedOriginals();
			_shadows = (VpeShadows)Mathf.Clamp(s.Shadows, 0, 2);
			// ray-traced effects only where the active pipeline asset can trace
			var rayTracing = IsRayTracingAvailable();
			_rayTracedShadows = s.RayTracedShadows && _shadows != VpeShadows.Off && IsEffectSupported(VpeEffect.RayTracedShadows);
			var volumes = FindGlobalVolumes(Camera.main);
			// the other ray-traced effects run on a volume; traced shadows are the lights' own
			var rtReflections = s.RayTracedReflections && rayTracing && volumes.Count > 0;
			var rtGlobalIllumination = s.RayTracedGlobalIllumination && rayTracing && volumes.Count > 0;
			var recursive = s.RayTracedRecursiveRendering && rayTracing && volumes.Count > 0;
			// what needs the light cluster; traced shadows don't
			var tracing = rtReflections || rtGlobalIllumination || recursive;
			_tracing = _rayTracedShadows || tracing;
			ApplyLightShadows();
			if (volumes.Count == 0) {
				// no volume runs the recursive rendering, so no glass may stay flagged for it (it wouldn't draw)
				ApplyRecursiveMaterials(false);
				FittedClusters.Clear();
				FittedShadows.Clear();
				FitToPlayfield();
				return;
			}
			var primary = PrimaryVolume(volumes);
			Logger.Info("Volumes the settings go to: {Volumes:l}; default profile {Default:l}, quality profile {Quality:l}",
				string.Join("; ", volumes.ConvertAll(DescribeVolume)), DescribeProfile(VolumeManager.instance.globalDefaultProfile, true), DescribeProfile(VolumeManager.instance.qualityDefaultProfile, true));

			// a table's profile may lack the component a ray-traced effect runs on
			var primaryProfile = ProfileOf(primary);
			if (tracing && !volumes.Exists(v => ProfileOf(v).Has<LightCluster>())) {
				primaryProfile.Add<LightCluster>();
			}
			if (rtReflections && !volumes.Exists(v => ProfileOf(v).Has<ScreenSpaceReflection>())) {
				primaryProfile.Add<ScreenSpaceReflection>();
			}
			if (rtGlobalIllumination && !volumes.Exists(v => ProfileOf(v).Has<GlobalIllumination>())) {
				primaryProfile.Add<GlobalIllumination>();
			}
			if (recursive && !volumes.Exists(v => ProfileOf(v).Has<RecursiveRendering>())) {
				primaryProfile.Add<RecursiveRendering>();
			}

			// HDRP's default profile blurs at half the frame time, so off needs a component that says so too
			if (!volumes.Exists(v => ProfileOf(v).Has<MotionBlur>())) {
				primaryProfile.Add<MotionBlur>();
			}
			// The same goes for the other effects: where no volume has one, the default profile's applies, and
			// an inactive component hands over to it as well. So an effect that's off needs a component that
			// stays active and says so.
			var reflections = s.ScreenSpaceReflections || rtReflections;
			if (!reflections && !volumes.Exists(v => ProfileOf(v).Has<ScreenSpaceReflection>())) {
				primaryProfile.Add<ScreenSpaceReflection>();
			}
			if (!s.AmbientOcclusion && !volumes.Exists(v => ProfileOf(v).Has<ScreenSpaceAmbientOcclusion>())) {
				primaryProfile.Add<ScreenSpaceAmbientOcclusion>();
			}
			if (!s.Bloom && !volumes.Exists(v => ProfileOf(v).Has<Bloom>())) {
				primaryProfile.Add<Bloom>();
			}
			// one cascade that ends behind the table, instead of the default profile's four over 150 m
			if (!volumes.Exists(v => ProfileOf(v).Has<HDShadowSettings>())) {
				primaryProfile.Add<HDShadowSettings>();
			}
			var reflectionQuality = (VpeReflectionQuality)Mathf.Clamp(s.ReflectionQuality, 0, 2);

			FittedClusters.Clear();
			FittedShadows.Clear();
			foreach (var volume in volumes) {
				var profile = ProfileOf(volume);
				if (profile.TryGet<HDShadowSettings>(out var shadows)) {
					shadows.active = true;
					shadows.cascadeShadowSplitCount.Override(1);
					shadows.maxShadowDistance.Override(DefaultShadowDistance);
					FittedShadows.Add(shadows);
				}
				if (profile.TryGet<MotionBlur>(out var motionBlur)) {
					motionBlur.active = true;
					motionBlur.intensity.Override(MotionBlurIntensity(s.MotionBlur));
				}
				if (tracing && profile.TryGet<LightCluster>(out var cluster)) {
					FittedClusters.Add(cluster);
				}
				if (profile.TryGet<ScreenSpaceReflection>(out var ssr)) {
					ssr.active = true;
					ssr.enabled.Override(reflections);
					ssr.tracing.Override(rtReflections ? RayCastingMode.RayTracing : RayCastingMode.RayMarching);
					if (!reflections) {
						// transparent surfaces trace on their own switch
						Force(ssr, ssr.enabledTransparent, false);
					} else if (reflectionQuality == VpeReflectionQuality.High || rtReflections) {
						Release(ssr.enabledTransparent);
						Release(ssr.quality);
					} else {
						// fewer steps per ray, and no second trace for what shows through glass and plastics
						Force(ssr, ssr.enabledTransparent, false);
						Force(ssr, ssr.quality, (int)reflectionQuality);
					}
				}
				if (profile.TryGet<ScreenSpaceAmbientOcclusion>(out var ao)) {
					ao.active = true;
					if (s.AmbientOcclusion) {
						Release(ao.intensity);
					} else {
						Force(ao, ao.intensity, 0f);
					}
					// no setting asks for ray-traced occlusion, whatever a table's profile says
					ao.rayTracing.Override(false);
				}
				if (profile.TryGet<SubSurfaceScattering>(out var sss)) {
					// the player build strips the passes of ray-traced subsurface scattering (RayTracingPassStripper)
					sss.rayTracing.Override(false);
				}
				if (profile.TryGet<PathTracing>(out var pathTracing)) {
					// and path tracing
					pathTracing.enable.Override(false);
				}
				if (profile.TryGet<Bloom>(out var bloom)) {
					bloom.active = true;
					if (s.Bloom) {
						Release(bloom.intensity);
					} else {
						Force(bloom, bloom.intensity, 0f);
					}
				}
				if (profile.TryGet<GlobalIllumination>(out var gi)) {
					var on = s.ScreenSpaceGlobalIllumination || rtGlobalIllumination;
					gi.active = on;
					// the component does nothing until its own switch is on, and profiles can have it off
					gi.enable.Override(on);
					gi.tracing.Override(rtGlobalIllumination ? RayCastingMode.RayTracing : RayCastingMode.RayMarching);
				}
				if (profile.TryGet<RecursiveRendering>(out var rr)) {
					rr.active = recursive;
					rr.enable.Override(recursive);
					// one mask for all of them, since which materials can be flagged depends on it
					rr.layerMask.Override(~0);
				}
			}
			ApplyRecursiveMaterials(recursive);
			if (!tracing) {
				FittedClusters.Clear();
			}
			FitToPlayfield();
			Logger.Info("Ray tracing {State} (GPU support: {Gpu}, pipeline asset: {Asset}, volumes: {Volumes}); shadows {Shadows}, reflections {Reflections}, GI {Gi}, recursive {Recursive}.",
				rayTracing ? "available" : "unavailable", SystemInfo.supportsRayTracing,
				GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none",
				string.Join(", ", volumes.ConvertAll(v => v.name)), _rayTracedShadows, rtReflections, rtGlobalIllumination, recursive);
		}

		// HDRP's ray tracing light cluster is a fixed 64x32x64 grid around the camera, as wide as the LightCluster
		// range and clipped to the lights' bounds. The room's work lights reach 10 m, so at the default range the
		// whole playfield fell into a few 30 cm cells, many with more lamps than a cell holds (HDRP leaves the rest
		// out of the reflections). Every frame, the range shrinks to the smallest cube around the camera that still
		// holds the playfield up to the glass, which keeps the cells a few centimetres wide wherever the camera is.
		//
		// The shadows are fitted the same way. A directional light's shadow map covers what the camera sees up
		// to the maximum shadow distance. The default profile spreads four cascades over 150 m, which renders
		// the table four times and gives it no more texels than one cascade that ends right behind it.
		private const float ClusterRangeMargin = 0.1f;
		private const float ShadowDistanceMargin = 0.25f;
		private const float DefaultShadowDistance = 8f; // without a table to fit to
		private static readonly List<LightCluster> FittedClusters = new List<LightCluster>();
		private static readonly List<HDShadowSettings> FittedShadows = new List<HDShadowSettings>();
		private static PlayfieldComponent _clusterPlayfield;

		private static void FitToPlayfield()
		{
			RenderPipelineManager.beginContextRendering -= FitToPlayfield;
			var on = FittedClusters.Count > 0 || FittedShadows.Count > 0 && _shadows != VpeShadows.Off;
			_clusterPlayfield = on ? Object.FindAnyObjectByType<PlayfieldComponent>() : null;
			if (on && _clusterPlayfield != null) {
				// before HDRP evaluates the cameras' volumes, and after the camera has moved for the frame
				RenderPipelineManager.beginContextRendering += FitToPlayfield;
			}
		}

		private static void FitToPlayfield(ScriptableRenderContext context, List<Camera> cameras)
		{
			var cam = Camera.main;
			if (cam == null || _clusterPlayfield == null) {
				return;
			}
			if (FittedClusters.Count > 0) {
				var range = ClusterRange(cam.transform.position, _clusterPlayfield);
				foreach (var cluster in FittedClusters) {
					if (cluster != null) {
						cluster.cameraClusterRange.Override(range);
					}
				}
			}
			if (FittedShadows.Count > 0) {
				var distance = ShadowDistance(cam.transform.position, _clusterPlayfield);
				foreach (var shadows in FittedShadows) {
					if (shadows != null) {
						shadows.maxShadowDistance.Override(distance);
					}
				}
			}
		}

		// How far the playfield's farthest corner is from the camera, surface to glass.
		private static float ShadowDistance(Vector3 cameraPosition, PlayfieldComponent playfield)
		{
			var toWorld = playfield.transform.localToWorldMatrix * (Matrix4x4)VisualPinball.Unity.Physics.VpxToWorld;
			var distance = 0f;
			for (var i = 0; i < 8; i++) {
				var corner = toWorld.MultiplyPoint3x4(new Vector3(
					(i & 1) == 0 ? playfield.Left : playfield.Right,
					(i & 2) == 0 ? playfield.Top : playfield.Bottom,
					(i & 4) == 0 ? 0f : playfield.GlassHeight));
				distance = Mathf.Max(distance, Vector3.Distance(corner, cameraPosition));
			}
			return Mathf.Clamp(distance + ShadowDistanceMargin, 1f, 50f);
		}

		// The lights around the table (the room's and the studio setups') are the only ones that cast shadows:
		// the lamps on the table never do. Off takes their shadows away and Low gives them a small map; what
		// they were set up with is kept, for High to give back.
		private const int LowShadowResolution = 256;
		private static VpeShadows _shadows = VpeShadows.High;
		// whether the directional light traces its shadows, and whether anything traces at all
		private static bool _rayTracedShadows;
		private static bool _tracing;
		private static readonly Dictionary<Light, (LightShadows Shadows, bool UseOverride, int Override, int Level)> ShadowLights = new();

		private static void ApplyLightShadows()
		{
			List<Light> gone = null;
			foreach (var light in ShadowLights.Keys) {
				if (light == null) {
					(gone ??= new List<Light>()).Add(light);
				}
			}
			if (gone != null) {
				foreach (var light in gone) {
					ShadowLights.Remove(light);
				}
			}
			foreach (var light in Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include)) {
				ApplyLightShadows(light);
			}
		}

		/// <summary>
		/// Gives a light the shadows the graphics settings allow. For lights set up after the settings were
		/// applied, such as a studio setup's.
		/// </summary>
		internal static void ApplyLightShadows(HDAdditionalLightData hd)
		{
			if (hd == null || !hd.TryGetComponent<Light>(out var light)) {
				return;
			}
			ApplyShadowMaps(hd, light);
			ApplyRayTracedShadows(hd, light);
		}

		// How many rays a pixel sends towards the light, and how far the denoiser looks, in pixels. A traced
		// light costs both per pixel: on an RTX 4090 at 3840 x 2160, 0.8 ms per ray, and 1.3 ms to denoise at
		// 8 pixels (2.2 ms at HDRP's 16). The table stands still under a camera that does too, so the
		// denoiser's history holds: one ray with half the reach gives the same still picture as four with
		// all of it, at well under half the time.
		private const int ShadowRays = 1;
		private const int ShadowFilterSize = 8;

		// Only the directional light traces its shadows: it is the one whose shadows show most, and every
		// traced light costs the same again (the room's four together took a 4K frame from 6 to 16 ms). The
		// spots keep their shadow maps. Whether a light traces is the setting's to say, not the table's: a
		// light a package brought with traced shadows would otherwise trace on its own. The shadow is as soft
		// as the light's angular diameter makes it, and coloured by what is transparent on the way, so clear
		// plastics and ramps still cast one. A directional light only traces into the screen-space shadow
		// buffer, and would be rasterized into it when it doesn't trace. A camera that traces also leaves out
		// the lights that aren't included for ray tracing, in what it rasterizes too: the bulb models used to
		// leave their light out, and packages exported with them still do, so every light is included: no
		// lamp may go dark with the setting.
		private static void ApplyRayTracedShadows(HDAdditionalLightData hd, Light light)
		{
			var directional = light.type == LightType.Directional;
			var trace = _rayTracedShadows && directional && light.shadows != LightShadows.None;
			hd.useRayTracedShadows = trace;
			hd.rayTraceContactShadow = false;
			if (directional) {
				hd.useScreenSpaceShadows = trace;
			}
			if (_tracing) {
				hd.includeForRayTracing = true;
			}
			if (!trace) {
				return;
			}
			hd.numRayTracingSamples = ShadowRays;
			hd.filterTracedShadow = true;
			hd.filterSizeTraced = ShadowFilterSize;
			hd.colorShadow = true;
		}

		private static void ApplyShadowMaps(HDAdditionalLightData hd, Light light)
		{
			if (!ShadowLights.TryGetValue(light, out var original)) {
				if (light.shadows == LightShadows.None || _shadows == VpeShadows.High) {
					return; // casts none, or keeps what it has
				}
				var resolution = hd.shadowResolution;
				original = (light.shadows, resolution.useOverride, resolution.@override, resolution.level);
				ShadowLights[light] = original;
			}
			switch (_shadows) {
				case VpeShadows.Off:
					light.shadows = LightShadows.None;
					break;
				case VpeShadows.Low:
					light.shadows = original.Shadows;
					hd.SetShadowResolutionOverride(true);
					hd.SetShadowResolution(LowShadowResolution);
					break;
				default:
					light.shadows = original.Shadows;
					hd.shadowResolution.level = original.Level;
					hd.shadowResolution.@override = original.Override;
					hd.SetShadowResolutionOverride(original.UseOverride);
					ShadowLights.Remove(light);
					break;
			}
		}

		/// <summary>
		/// A light was set up anew, which says whether it casts shadows: that replaces what the settings
		/// kept of it. Setting it up doesn't touch its shadow resolution, so where the settings lowered that,
		/// what is kept is still the one to give back.
		/// </summary>
		internal static void LightConfigured(HDAdditionalLightData hd)
		{
			if (hd != null && hd.TryGetComponent<Light>(out var light) && ShadowLights.TryGetValue(light, out var kept)) {
				ShadowLights[light] = (light.shadows, kept.UseOverride, kept.Override, kept.Level);
			}
			ApplyLightShadows(hd);
		}

		// The largest distance along any axis from the camera to a corner of the playfield's box, surface to glass.
		private static float ClusterRange(Vector3 cameraPosition, PlayfieldComponent playfield)
		{
			var toWorld = playfield.transform.localToWorldMatrix * (Matrix4x4)VisualPinball.Unity.Physics.VpxToWorld;
			var range = 0f;
			for (var i = 0; i < 8; i++) {
				var corner = toWorld.MultiplyPoint3x4(new Vector3(
					(i & 1) == 0 ? playfield.Left : playfield.Right,
					(i & 2) == 0 ? playfield.Top : playfield.Bottom,
					(i & 4) == 0 ? 0f : playfield.GlassHeight));
				var d = corner - cameraPosition;
				range = Mathf.Max(range, Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z))));
			}
			return Mathf.Min(range + ClusterRangeMargin, 50f);
		}

		// Profiles we instantiated (Volume.profile clones the shared one on first access), so they and their
		// components can be destroyed once their volume is gone; nothing unloads them otherwise.
		private static readonly Dictionary<Volume, VolumeProfile> OwnedProfiles = new Dictionary<Volume, VolumeProfile>();

		private static VolumeProfile ProfileOf(Volume volume)
		{
			if (OwnedProfiles.TryGetValue(volume, out var owned)) {
				return owned;
			}
			if (volume.HasInstantiatedProfile()) {
				return volume.profile; // someone else's clone
			}
			var profile = volume.profile;
			OwnedProfiles[volume] = profile;
			return profile;
		}

		private static void ReleaseOrphanedProfiles()
		{
			var orphaned = new List<Volume>();
			foreach (var entry in OwnedProfiles) {
				if (entry.Key == null) {
					orphaned.Add(entry.Key);
					if (entry.Value != null) {
						foreach (var component in entry.Value.components) {
							Object.Destroy(component);
						}
						Object.Destroy(entry.Value);
					}
				}
			}
			foreach (var volume in orphaned) {
				OwnedProfiles.Remove(volume);
			}
		}

		// The active global volumes with a profile.
		// Only the volumes the camera sees: a zero-weight one, or one on a layer the camera leaves out, would
		// otherwise count as already having an effect and keep it from being added where it shows.
		private static List<Volume> FindGlobalVolumes(Camera cam)
		{
			var mask = cam != null && cam.TryGetComponent<HDAdditionalCameraData>(out var hd) ? (int)hd.volumeLayerMask : ~0;
			var result = new List<Volume>();
			foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude)) {
				if (v.isGlobal && v.enabled && v.gameObject.activeInHierarchy && v.weight > 0f && (mask & (1 << v.gameObject.layer)) != 0
					&& !HdrpEnvironmentApplier.Owns(v)
					&& (v.sharedProfile != null || v.HasInstantiatedProfile())) {
					result.Add(v);
				}
			}
			return result;
		}

		// Where missing components go: the highest priority, and on a tie the one with the effects.
		private static Volume PrimaryVolume(List<Volume> volumes)
		{
			Volume best = null;
			var bestScore = 0;
			foreach (var v in volumes) {
				var p = ProfileOf(v);
				var score = (p.Has<ScreenSpaceReflection>() ? 1 : 0) + (p.Has<GlobalIllumination>() ? 1 : 0);
				if (best == null || v.priority > best.priority || (Mathf.Approximately(v.priority, best.priority) && score > bestScore)) {
					best = v;
					bestScore = score;
				}
			}
			return best;
		}

		/// <summary>
		/// Whether the active pipeline asset supports ray tracing on this GPU. Reads the asset rather than
		/// the running pipeline, since a quality-level swap only rebuilds the pipeline on the next frame.
		/// </summary>
		internal static bool IsRayTracingAvailable()
			=> SystemInfo.supportsRayTracing
				&& GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset hdrp
				&& hdrp.currentPlatformRenderPipelineSettings.supportRayTracing;

		// Recursive rendering only draws materials flagged for it: HDRP checks the _RayTracing property and
		// renders the material's RayTracingPrepass pass (which the editor's material validation normally
		// toggles). We flag the clear, refractive materials, the ones whose screen-space refraction gets it
		// most wrong, and restore the ones we flagged when it's turned off. Insert covers keep their own optics.
		private const string RayTracingProperty = "_RayTracing";
		private const string RayTracingPrepassPass = "RayTracingPrepass";
		private static readonly HashSet<Material> RecursiveMaterials = new HashSet<Material>();

		private static void ApplyRecursiveMaterials(bool on)
		{
			// restore what we flagged before, then decide again (renderers may have changed)
			foreach (var material in RecursiveMaterials) {
				if (material != null) {
					SetRecursive(material, false);
				}
			}
			RecursiveMaterials.Clear();
			if (!on) {
				return;
			}
			// A flagged material isn't rasterized anymore, so a renderer outside the acceleration structure
			// would disappear: such a renderer's materials are left alone.
			var candidates = new HashSet<Material>();
			var excluded = new HashSet<Material>();
			var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
			foreach (var renderer in renderers) {
				var traced = renderer.rayTracingMode != UnityEngine.Experimental.Rendering.RayTracingMode.Off;
				foreach (var material in renderer.sharedMaterials) {
					if (IsRecursiveCandidate(material)) {
						(traced ? candidates : excluded).Add(material);
					}
				}
			}
			foreach (var material in candidates) {
				if (!excluded.Contains(material) && RecursiveMaterials.Add(material)) {
					SetRecursive(material, true);
				}
			}
			Logger.Info("Recursive ray tracing on {Count} material(s).", RecursiveMaterials.Count);
		}

		private static bool IsRecursiveCandidate(Material material)
			=> material != null && material.shader != null
				&& material.HasProperty(RayTracingProperty) && material.GetFloat(RayTracingProperty) < 0.5f
				&& material.HasProperty("_RefractionModel") && material.GetFloat("_RefractionModel") > 0.5f
				&& (material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") || material.renderQueue > (int)RenderQueue.GeometryLast)
				&& material.shader.name.IndexOf("Insert", System.StringComparison.OrdinalIgnoreCase) < 0;

		private static void SetRecursive(Material material, bool on)
		{
			material.SetFloat(RayTracingProperty, on ? 1f : 0f);
			material.SetShaderPassEnabled(RayTracingPrepassPass, on);
		}

		// The active global Volume with the highest priority (we read its .profile instance, so we don't
		// dirty the shared asset).
		internal static Volume FindGlobalVolume(int layerMask = ~0)
		{
			var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
			Volume best = null;
			foreach (var v in volumes) {
				if (!v.isGlobal || !v.enabled || !v.gameObject.activeInHierarchy || (layerMask & (1 << v.gameObject.layer)) == 0
					|| HdrpEnvironmentApplier.Owns(v)) {
					continue;
				}
				if (best == null || v.priority > best.priority) {
					best = v;
				}
			}
			return best;
		}
	}
}
