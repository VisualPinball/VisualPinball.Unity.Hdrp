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
	/// Applies the host app's <see cref="VpeGraphicsSettings"/> to HDRP at runtime: camera anti-aliasing
	/// and the live Volume effects (SSR/RT reflections, SSAO, SSGI/RT-GI, recursive ray tracing, Bloom).
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
			VpeGraphics.RenderScaleSource = RenderScale;
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
			_upscaler = (VpeUpscaler)Mathf.Clamp(settings.Upscaler, 0, 2);
			_upscalerQuality = (VpeUpscalerQuality)Mathf.Clamp(settings.UpscalerQuality, 0, 3);
			ApplyCameraAntialiasing(Camera.main);
			ApplyVolume(settings);
			Logger.Info("Upscaling {Upscaler} {Quality} (DLSS detected: {Dlss}, FSR 2 detected: {Fsr2})",
				_upscaler, _upscalerQuality, HDDynamicResolutionPlatformCapabilities.DLSSDetected, HDDynamicResolutionPlatformCapabilities.FSR2Detected);
		}

		// DLSS needs the NVIDIA plugin and an RTX GPU, FSR 2 a DX12 or Vulkan device; HDRP detects both.
		private static bool IsUpscalerSupported(VpeUpscaler upscaler) => upscaler switch {
			VpeUpscaler.Dlss => HDDynamicResolutionPlatformCapabilities.DLSSDetected,
			VpeUpscaler.Fsr2 => HDDynamicResolutionPlatformCapabilities.FSR2Detected,
			_ => true,
		};

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
			if (!IsUpscalerSupported(upscaler) || CabinetProjection.EffectiveAntiAliasing(cam, VpeAntiAliasing.Taa) != VpeAntiAliasing.Taa) {
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

		private static void ApplyVolume(VpeGraphicsSettings s)
		{
			// All active global volumes blend, and the table's and the player's can share a priority, so
			// the settings go to every one of them that has the effect, not just the one that happens to
			// win the tie (which made the result depend on load order).
			ReleaseOrphanedProfiles();
			var volumes = FindGlobalVolumes();
			if (volumes.Count == 0) {
				return;
			}
			var primary = PrimaryVolume(volumes);

			// ray-traced effects only where the active pipeline asset (the Ray Tracing quality level) can trace
			var rayTracing = IsRayTracingAvailable();
			var rtReflections = s.RayTracedReflections && rayTracing;
			var rtGlobalIllumination = s.RayTracedGlobalIllumination && rayTracing;
			var recursive = s.RayTracedRecursiveRendering && rayTracing;

			// a table's profile may lack the component a ray-traced effect runs on
			var primaryProfile = ProfileOf(primary);
			if (rtReflections && !volumes.Exists(v => ProfileOf(v).Has<ScreenSpaceReflection>())) {
				primaryProfile.Add<ScreenSpaceReflection>();
			}
			if (rtGlobalIllumination && !volumes.Exists(v => ProfileOf(v).Has<GlobalIllumination>())) {
				primaryProfile.Add<GlobalIllumination>();
			}
			if (recursive && !volumes.Exists(v => ProfileOf(v).Has<RecursiveRendering>())) {
				primaryProfile.Add<RecursiveRendering>();
			}

			foreach (var volume in volumes) {
				var profile = ProfileOf(volume);
				if (profile.TryGet<ScreenSpaceReflection>(out var ssr)) {
					var on = s.ScreenSpaceReflections || rtReflections;
					ssr.active = on;
					ssr.enabled.Override(on);
					ssr.tracing.Override(rtReflections ? RayCastingMode.RayTracing : RayCastingMode.RayMarching);
				}
				if (profile.TryGet<ScreenSpaceAmbientOcclusion>(out var ao)) {
					ao.active = s.AmbientOcclusion;
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
					bloom.active = s.Bloom;
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
			if (rayTracing) {
				// ray-traced shadows and contact shadows trace the visibility rays the build strips
				foreach (var light in Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include)) {
					light.useRayTracedShadows = false;
					light.rayTraceContactShadow = false;
				}
			}

			Logger.Info("Ray tracing {State} (GPU support: {Gpu}, pipeline asset: {Asset}, volumes: {Volumes}); reflections {Reflections}, GI {Gi}, recursive {Recursive}.",
				rayTracing ? "available" : "unavailable", SystemInfo.supportsRayTracing,
				GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none",
				string.Join(", ", volumes.ConvertAll(v => v.name)), rtReflections, rtGlobalIllumination, recursive);
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
		private static List<Volume> FindGlobalVolumes()
		{
			var result = new List<Volume>();
			foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude)) {
				if (v.isGlobal && v.enabled && v.gameObject.activeInHierarchy && (v.sharedProfile != null || v.HasInstantiatedProfile())) {
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
				if (!v.isGlobal || !v.enabled || !v.gameObject.activeInHierarchy || (layerMask & (1 << v.gameObject.layer)) == 0) {
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
