// Visual Pinball Engine
// Copyright (C) 2022 freezy and VPE Team
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

using UnityEditor;
using UnityEngine;

namespace VisualPinball.Unity.Library.Editor
{

	[CustomEditor(typeof(ThumbGenerator))]
	public class ThumbGeneratorInspector : UnityEditor.Editor
	{
		private ThumbGenerator _generator;
		private SerializedProperty _assetLibraryProperty;
		private SerializedProperty _defaultEnvironmentProperty;

		private void OnEnable()
		{
			_generator = target as ThumbGenerator;

			_assetLibraryProperty = serializedObject.FindProperty(nameof(ThumbGenerator.AssetLibrary));
			_defaultEnvironmentProperty = serializedObject.FindProperty(nameof(ThumbGenerator.DefaultEnvironment));
		}

		public override void OnInspectorGUI()
		{
			serializedObject.Update();
			
			EditorGUILayout.PropertyField(_assetLibraryProperty);
			EditorGUILayout.PropertyField(_defaultEnvironmentProperty);
			
			serializedObject.ApplyModifiedProperties();

			GUI.enabled = !_generator.IsProcessing;
			if (GUILayout.Button("Process All")) {
				_generator.StartProcessing();
			}
			if (GUILayout.Button("Process New")) {
				_generator.StartProcessing(true);
			}
			if (GUILayout.Button("Process Selected")) {
				_generator.StartProcessing(selectedOnly: true);
			}

			GUI.enabled = _generator.IsProcessing;
			if (GUILayout.Button("Stop Processing")) {
				_generator.StopProcessing();
			}

			GUI.enabled = true;

			if (_generator.IsProcessing) {
				GUILayout.Label($"Processing {_generator.NumProcessed}/{_generator.TotalProcessing}");
			}
		}
	}
}
