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
using UnityEngine.Rendering.HighDefinition;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Frame settings that components switch on for their camera while they run. Each one is counted per
	/// camera, so a component switching off leaves it on for another that still needs it, and the camera
	/// gets its own setting back once none does.
	/// </summary>
	internal static class CameraFrameSettings
	{
		private sealed class Saved
		{
			public bool CustomRenderingSettings;
			public readonly Dictionary<FrameSettingsField, (bool overridden, bool enabled, int users)> Fields = new();
		}

		private static readonly Dictionary<HDAdditionalCameraData, Saved> Cameras = new();

		public static void Enable(HDAdditionalCameraData camera, FrameSettingsField field)
		{
			if (!Cameras.TryGetValue(camera, out var saved)) {
				saved = new Saved { CustomRenderingSettings = camera.customRenderingSettings };
				Cameras[camera] = saved;
			}
			if (saved.Fields.TryGetValue(field, out var state)) {
				saved.Fields[field] = (state.overridden, state.enabled, state.users + 1);
			} else {
				saved.Fields[field] = (camera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field],
					camera.renderingPathCustomFrameSettings.IsEnabled(field), 1);
			}
			// overriding only this field leaves all others at their defaults
			camera.customRenderingSettings = true;
			camera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = true;
			camera.renderingPathCustomFrameSettings.SetEnabled(field, true);
		}

		/// <summary>Gives up one <see cref="Enable"/> of the field. A destroyed camera has nothing to restore.</summary>
		public static void Release(HDAdditionalCameraData camera, FrameSettingsField field)
		{
			if (ReferenceEquals(camera, null)) {
				return;
			}
			if (camera == null) {
				Cameras.Remove(camera); // destroyed, but it still matches its own key
				return;
			}
			if (!Cameras.TryGetValue(camera, out var saved) || !saved.Fields.TryGetValue(field, out var state)) {
				return;
			}
			if (state.users > 1) {
				saved.Fields[field] = (state.overridden, state.enabled, state.users - 1);
				return;
			}
			saved.Fields.Remove(field);
			camera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = state.overridden;
			camera.renderingPathCustomFrameSettings.SetEnabled(field, state.enabled);
			if (saved.Fields.Count == 0) {
				camera.customRenderingSettings = saved.CustomRenderingSettings;
				Cameras.Remove(camera);
			}
		}
	}
}
