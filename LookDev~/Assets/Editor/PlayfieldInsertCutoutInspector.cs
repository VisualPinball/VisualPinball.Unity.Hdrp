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

// ReSharper disable InconsistentNaming

using UnityEditor;
using UnityEngine;
using VisualPinball.Unity.LookDev;

namespace VisualPinball.Unity.Library.Editor
{
	[CustomEditor(typeof(PlayfieldInsertCutout))]
	public sealed class PlayfieldInsertCutoutInspector : UnityEditor.Editor
	{
		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();
			var cutout = (PlayfieldInsertCutout)target;
			if (!string.IsNullOrEmpty(cutout.Error)) {
				EditorGUILayout.HelpBox(cutout.Error, MessageType.Warning);
			}
			EditorGUILayout.HelpBox("Projects the insert's outer outline along world Y. Moving or reshaping the insert updates this preview. Surface UVs are preserved; the hole walls inherit the outside edge material, UV orientation and density.", MessageType.Info);
			if (GUILayout.Button("Rebuild Cutout")) {
				cutout.Rebuild();
				SceneView.RepaintAll();
			}
		}
	}
}
