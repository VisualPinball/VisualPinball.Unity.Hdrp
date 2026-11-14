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
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Applies the host app's <see cref="VpeGraphicsSettings"/> to HDRP at runtime: camera anti-aliasing,
	/// ball reflections, the shadows of the lights around the table and the live Volume effects (SSR/RT
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
			VpeGraphics.UpscalerQualitySupport = IsUpscalerQualitySupported;
			_fsr2PerformanceCrashes = SystemInfo.graphicsDeviceVendorID == 0x10DE
				&& SystemInfo.graphicsDeviceName.IndexOf("RTX", System.StringComparison.OrdinalIgnoreCase) < 0;
			VpeGraphics.RenderScaleSource = RenderScale;
			VpeGraphics.BallReflectionsSupported = true;
			VpeGraphics.MotionBlurSupported = true;
			VpeGraphics.DepthOfFieldHandler = SetDepthOfField;
			VpeGraphics.ReleaseRenderBuffersHandler = ReleaseRenderBuffers;
			VpeGraphics.EffectSupport = IsEffectSupported;
			VpeGraphics.EffectiveSettingsSource = DescribeEffective;
			_shadows = VpeShadows.High;
			ShadowLights.Clear();
			Originals.Clear();
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
			foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude)) {
				if (light.enabled && light.shadows != LightShadows.None) {
					shadowLights++;
				}
			}
			return string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"asset {0}; aa {1}; bloom {2:0.###} (frame {3}); ao {4:0.###} (frame {5}); ssr {6}/transparent {7} quality {8} (frame {9}); ssgi {10} (frame {11}); "
				+ "shadows {12} light(s), {13} cascade(s) to {14:0.##} m (frame {15}); motion blur {16:0.##} (frame {17}); scale {18:0.###}",
				GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none",
				cam.TryGetComponent<HDAdditionalCameraData>(out var data) ? data.antialiasing.ToString() : "?",
				bloom.intensity.value, On(frame.IsEnabled(FrameSettingsField.Bloom)),
				ao.intensity.value, On(frame.IsEnabled(FrameSettingsField.SSAO)),
				On(ssr.enabled.value), On(ssr.enabledTransparent.value), ssr.quality.value, On(frame.IsEnabled(FrameSettingsField.SSR)),
				On(gi.enable.value), On(frame.IsEnabled(FrameSettingsField.SSGI)),
				shadowLights, shadows.cascadeShadowSplitCount.value, shadows.maxShadowDistance.value, On(frame.IsEnabled(FrameSettingsField.ShadowMaps)),
				blur.intensity.value, On(frame.IsEnabled(FrameSettingsField.MotionBlur)),
				RenderScale())
				+ DescribeCost(frame, ssr, gi, ao, bloom);
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
		private static bool IsEffectSupported(VpeEffect effect)
		{
			if (GraphicsSettings.currentRenderPipeline is not HDRenderPipelineAsset asset) {
				return false;
			}
			var settings = asset.currentPlatformRenderPipelineSettings;
			return effect switch {
				VpeEffect.ScreenSpaceReflections => settings.supportSSR,
				VpeEffect.AmbientOcclusion => settings.supportSSAO,
				VpeEffect.ScreenSpaceGlobalIllumination => settings.supportSSGI,
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
			ApplyCameraAntialiasing(Camera.main);
			ApplyBallReflections(Camera.main, settings.BallReflections);
			ApplyCameraMotionBlur(Camera.main, settings.MotionBlur != (int)VpeMotionBlur.Off);
			ApplyVolume(settings);
			Logger.Info("Upscaling {Upscaler} {Quality} (DLSS detected: {Dlss}, FSR 2 detected: {Fsr2})",
				_upscaler, _upscalerQuality, HDDynamicResolutionPlatformCapabilities.DLSSDetected, HDDynamicResolutionPlatformCapabilities.FSR2Detected);
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

		// DLSS needs the NVIDIA plugin and an RTX GPU, FSR 2 a DX12 or Vulkan device; HDRP detects both.
		private static bool IsUpscalerSupported(VpeUpscaler upscaler) => upscaler switch {
			VpeUpscaler.Dlss => HDDynamicResolutionPlatformCapabilities.DLSSDetected,
			VpeUpscaler.Fsr2 => HDDynamicResolutionPlatformCapabilities.FSR2Detected,
			VpeUpscaler.Off => true,
			_ => false,
		};

		// AMD's plugin crashes in FSR 2's Performance mode on NVIDIA GPUs without RTX: on a GTX 1080 (Windows
		// 10, D3D12) it throws on the graphics thread while it sets the upscaler up, every time, right after the
		// table loads. Its other modes run there, and so does Performance on an RTX card.
		private static bool _fsr2PerformanceCrashes;

		private static bool IsUpscalerQualitySupported(VpeUpscaler upscaler, VpeUpscalerQuality quality)
			=> !(upscaler == VpeUpscaler.Fsr2 && quality == VpeUpscalerQuality.Performance && _fsr2PerformanceCrashes);

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
			// an upscaler does the anti-aliasing itself; HDRP would still run FXAA after it (it jitters regardless)
			hd.antialiasing = ApplyCameraUpscaler(cam, hd)
				? HDAdditionalCameraData.AntialiasingMode.None
				: ToHdrp(CabinetProjection.EffectiveAntiAliasing(cam, requested));
		}

		// The upscalers are temporal like TAA: where the cabinet projection rules out TAA, they're off too.
		// The pipeline assets list DLSS and FSR 2; the camera's flags pick one, at the scale its mode implies.
		// Returns whether one is active.
		private static bool ApplyCameraUpscaler(Camera cam, HDAdditionalCameraData hd)
		{
			var upscaler = _upscaler;
			if (!IsUpscalerSupported(upscaler) || !AssetOffers(upscaler) || CabinetProjection.EffectiveAntiAliasing(cam, VpeAntiAliasing.Taa) != VpeAntiAliasing.Taa) {
				upscaler = VpeUpscaler.Off;
			}
			hd.allowDynamicResolution = upscaler != VpeUpscaler.Off;

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
			return upscaler != VpeUpscaler.Off;
		}

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
			var name = upscaler == VpeUpscaler.Dlss ? "DLSS" : "FSR2";
			return drs.enabled && drs.advancedUpscalerNames != null && drs.advancedUpscalerNames.Contains(name);
		}

		// NVIDIA's DLSSQuality: MaximumPerformance 0, Balanced 1, MaximumQuality 2, UltraPerformance 3.
		private static uint DlssQuality(VpeUpscalerQuality quality) => quality switch {
			VpeUpscalerQuality.Quality => 2,
			VpeUpscalerQuality.Balanced => 1,
			VpeUpscalerQuality.Performance => 0,
			_ => 3,
		};

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
			ApplyLightShadows();
			var volumes = FindGlobalVolumes(Camera.main);
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

			// ray-traced effects only where the active pipeline asset (the Ray Tracing quality level) can trace
			var rayTracing = IsRayTracingAvailable();
			var rtReflections = s.RayTracedReflections && rayTracing;
			var rtGlobalIllumination = s.RayTracedGlobalIllumination && rayTracing;
			var recursive = s.RayTracedRecursiveRendering && rayTracing;
			var tracing = rtReflections || rtGlobalIllumination || recursive;

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
					// the player build strips the visibility rays ray-traced AO needs (RayTracingPassStripper)
					ao.rayTracing.Override(false);
				}
				if (profile.TryGet<SubSurfaceScattering>(out var sss)) {
					// same for ray-traced subsurface scattering
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
			if (rayTracing) {
				foreach (var light in Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include)) {
					// ray-traced shadows and contact shadows trace the visibility rays the build strips
					light.useRayTracedShadows = false;
					light.rayTraceContactShadow = false;
					// A lamp lights what's around it in the reflections too. The bulb models used to leave their
					// light out of ray tracing, and packages exported with them still do.
					if (light.GetComponentInParent<LightComponent>(true) != null) {
						light.includeForRayTracing = true;
					}
				}
			}

			Logger.Info("Ray tracing {State} (GPU support: {Gpu}, pipeline asset: {Asset}, volumes: {Volumes}); reflections {Reflections}, GI {Gi}, recursive {Recursive}.",
				rayTracing ? "available" : "unavailable", SystemInfo.supportsRayTracing,
				GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none",
				string.Join(", ", volumes.ConvertAll(v => v.name)), rtReflections, rtGlobalIllumination, recursive);
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
