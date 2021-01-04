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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using VisualPinball.Unity;
using VisualPinball.Unity.AssetLibrary.Hdrp;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	public static class BlueprintProjector
	{
		[MenuItem("GameObject/Visual Pinball/Blueprint Projector", false, 12)]
		private static void CreateBlueprintProjector()
		{
			// TODO: Move post-instantiation logic to BP authoring component. Extend to make it simpler to swap projections.
			var decalProjector = AssetDatabase.LoadAssetAtPath(HdrpAssetPath.BlueprintPrefab, typeof(GameObject));

			// Spawn the prefab in the scene.
			var bluePrintProjector = (GameObject)PrefabUtility.InstantiatePrefab(decalProjector);
			bluePrintProjector.name = "Blueprint Projector";
			bluePrintProjector.transform.localScale = Vector3.one;
			Undo.RegisterCreatedObjectUndo(bluePrintProjector, "Blueprint Decal");

			// find table and parent
			TableAuthoring ta;
			Transform parent;

			// if nothing selected, use active table
			if (Selection.activeGameObject == null) {
				ta = TableSelector.Instance.SelectedTable;
				parent = bluePrintProjector.transform.root;

			} else {
				// otherwise, find parent in hierarchy
				ta = Selection.activeGameObject.GetComponentInParent<TableAuthoring>();
				parent = Selection.activeGameObject.transform;

				// if none in hierarchy, fall back to active table
				if (ta == null) {
					ta = TableSelector.Instance.SelectedTable;
				}
			}
			if (ta == null) {
				EditorUtility.DisplayDialog(
					"Visual Pinball Layouts",
					"Layouts added. You can switch between them using the drop down in the top right corner of the editor.",
					"Got it!");
				return;
			}

			// adjust parameters
			var playfieldAuthoring = ta.GetComponentInChildren<PlayfieldAuthoring>();
			bluePrintProjector.transform.SetParent(parent, true);
			var extents = playfieldAuthoring.GetComponent<MeshRenderer>().bounds.extents;
			var center = playfieldAuthoring.GetComponent<MeshRenderer>().bounds.center;
			bluePrintProjector.transform.position = center + new Vector3(0, 1, 0);
			bluePrintProjector.GetComponent<DecalProjector>().size = new Vector3(extents.x, extents.z, 1) * 2.0f;

			// set selection to created object
			Selection.activeGameObject = bluePrintProjector;
			EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
		}
	}
}
