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

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Applies the host app's <see cref="VpeGraphicsSettings"/> to HDRP at runtime: camera anti-aliasing
	/// and the live Volume effects (SSR, SSAO, SSGI/RT-GI, Bloom). Registered before any scene loads, the
	/// same pattern as <c>HdrpMaterialResolver</c>. Engine-level settings (resolution, vsync, quality level)
	/// are applied host-side and don't pass through here.
	/// </summary>
	public sealed class HdrpGraphicsApplier : IVpeGraphicsApplier
	{
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register() => VpeGraphics.Register(new HdrpGraphicsApplier());

		public void Apply(VpeGraphicsSettings settings)
		{
			ApplyCameraAntialiasing(settings.AntiAliasing);
			ApplyVolume(settings);
		}

		private static void ApplyCameraAntialiasing(int aa)
		{
			var cam = Camera.main;
			if (cam == null) {
				return;
			}
			var hd = cam.GetComponent<HDAdditionalCameraData>();
			if (hd != null) {
				hd.antialiasing = (HDAdditionalCameraData.AntialiasingMode)Mathf.Clamp(aa, 0, 3);
			}
		}

		private static void ApplyVolume(VpeGraphicsSettings s)
		{
			var volume = FindGlobalVolume();
			var profile = volume != null ? volume.profile : null;
			if (profile == null) {
				return;
			}
			if (profile.TryGet<ScreenSpaceReflection>(out var ssr)) {
				ssr.active = s.ScreenSpaceReflections;
				ssr.tracing.value = s.RayTracedReflections ? RayCastingMode.RayTracing : RayCastingMode.RayMarching;
			}
			if (profile.TryGet<ScreenSpaceAmbientOcclusion>(out var ao)) {
				ao.active = s.AmbientOcclusion;
			}
			if (profile.TryGet<Bloom>(out var bloom)) {
				bloom.active = s.Bloom;
			}
			if (profile.TryGet<GlobalIllumination>(out var gi)) {
				gi.active = s.ScreenSpaceGlobalIllumination || s.RayTracedGlobalIllumination;
				gi.tracing.value = s.RayTracedGlobalIllumination ? RayCastingMode.RayTracing : RayCastingMode.RayMarching;
			}
		}

		// The active global Volume with the highest priority (we read its .profile instance, so we don't
		// dirty the shared asset).
		private static Volume FindGlobalVolume()
		{
			var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
			Volume best = null;
			foreach (var v in volumes) {
				if (!v.isGlobal || !v.enabled || !v.gameObject.activeInHierarchy) {
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
