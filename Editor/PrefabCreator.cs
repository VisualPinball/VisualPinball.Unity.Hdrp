// Visual Pinball Engine
// Copyright (C) 2020 freezy and VPE Team
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

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	public static class PrefabCreator
	{
		[MenuItem("GameObject/Visual Pinball/Editor Camera", false, 32)]
		private static void CreateEditorCamera()
		{
			Create(AssetPath.CameraPrefab, "Editor Camera", "Create Editor Camera");
		}

		[MenuItem("GameObject/Visual Pinball/Editor Post Processing", false, 33)]
		private static void CreatePostProcessing()
		{
			Create(AssetPath.PostProcessPrefab, "Editor Post Processing", "Create Editor Post Processing");
		}

		[MenuItem("GameObject/Visual Pinball/Editor Lighting", false, 34)]
		private static void CreateLighting()
		{
			Create(AssetPath.LightingPrefab, "Editor Lighting", "Create Editor Lighting");
		}

		private static void Create(string path, string name, string undoLabel)
		{
			var prefab = AssetDatabase.LoadAssetAtPath(path, typeof(GameObject));

			// Spawn the prefab in the scene.
			var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
			go.name = name;
			Undo.RegisterCreatedObjectUndo(go, undoLabel);

			// parent
			var parent = Selection.activeGameObject == null
				? go.transform.root
				: Selection.activeGameObject.transform;

			go.transform.SetParent(parent, true);
			Selection.activeGameObject = go;
		}
	}
}
