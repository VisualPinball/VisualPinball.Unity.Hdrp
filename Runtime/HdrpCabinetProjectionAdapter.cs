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
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Lets HDRP render a camera through a cabinet projection (an off-centre frustum).
	/// </summary>
	///
	/// <remarks>
	/// <para>
	/// HDRP's light loop and volumetrics read a "non-oblique" projection through
	/// <see cref="HDAdditionalCameraData.nonObliqueProjectionGetter"/>, whose default rebuilds a symmetric
	/// frustum from the camera's field of view. The adapter hands it the actual off-centre projection.
	/// </para>
	/// <para>
	/// Planar reflection probes build their own symmetric projection from the camera's field of view and
	/// aspect, so those are set to a symmetric frustum enclosing the off-centre one.
	/// </para>
	/// <para>
	/// HDRP 17.5 sizes TAA's jitter with <c>abs(left) + abs(right)</c> and <c>abs(bottom) + abs(top)</c>,
	/// which is wrong when both bounds have the same sign, as they do for a cabinet view looking down at
	/// the screen. Until that is patched, TAA falls back to SMAA while the projection is active.
	/// </para>
	/// </remarks>
	public sealed class HdrpCabinetProjectionAdapter : ICabinetProjectionAdapter
	{
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register() => CabinetProjection.Register(new HdrpCabinetProjectionAdapter());

		private sealed class State
		{
			public HDAdditionalCameraData Data;
			public HDAdditionalCameraData.NonObliqueProjectionGetter PreviousGetter;
			public HDAdditionalCameraData.NonObliqueProjectionGetter Getter;
			public Matrix4x4 Projection;
			public HDAdditionalCameraData.AntialiasingMode AntiAliasing;
		}

		private readonly Dictionary<Camera, State> _states = new();

		public void Attach(Camera camera)
		{
			if (camera == null || _states.ContainsKey(camera)) {
				return;
			}
			var state = new State { Data = camera.GetComponent<HDAdditionalCameraData>(), Projection = camera.projectionMatrix };
			if (state.Data != null) {
				state.AntiAliasing = state.Data.antialiasing;
				state.PreviousGetter = state.Data.nonObliqueProjectionGetter;
				state.Getter = _ => state.Projection; // allocated once per attach
				state.Data.nonObliqueProjectionGetter = state.Getter;
			}
			_states[camera] = state;
			// the anti-aliasing may be restricted now
			HdrpGraphicsApplier.ApplyCameraAntialiasing(camera, state.AntiAliasing);
		}

		public void ApplyMetadata(Camera camera, in CabinetProjectionResult projection)
		{
			camera.fieldOfView = projection.EnclosingFieldOfView;
			camera.aspect = projection.EnclosingAspect;
		}

		public void ProjectionChanged(Camera camera, in CabinetProjectionResult projection, bool discontinuous)
		{
			if (_states.TryGetValue(camera, out var state)) {
				state.Projection = projection.Projection;
			}
			if (discontinuous) {
				HDCamera.GetOrCreate(camera).Reset();
			}
		}

		public void Detach(Camera camera)
		{
			if (camera == null || !_states.Remove(camera, out var state)) {
				return;
			}
			if (state.Data != null && state.Data.nonObliqueProjectionGetter == state.Getter) {
				state.Data.nonObliqueProjectionGetter = state.PreviousGetter;
			}
			HDCamera.GetOrCreate(camera).Reset();
			HdrpGraphicsApplier.ApplyCameraAntialiasing(camera, state.AntiAliasing);
		}

		public VpeAntiAliasing EffectiveAntiAliasing(VpeAntiAliasing requested, out string reason)
		{
			if (requested == VpeAntiAliasing.Taa) {
				reason = "HDRP's TAA jitters off-centre cabinet views incorrectly, so the cabinet view uses SMAA.";
				return VpeAntiAliasing.Smaa;
			}
			reason = null;
			return requested;
		}
	}
}
