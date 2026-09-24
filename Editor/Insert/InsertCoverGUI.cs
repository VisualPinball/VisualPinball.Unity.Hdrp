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

using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEditor.SceneManagement;
using UnityEngine;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	/// <summary>
	/// Material inspector of the <c>Visual Pinball/Insert Cover</c> shader: the
	/// flat clear top that closes an insert at its rim.
	/// </summary>
	/// <remarks>
	/// Two sections between HDRP's surface and advanced options: the haze the
	/// cover lays over the body, and the top's reflections. An insert whose
	/// Use Playfield Normals is on replaces the normal map and its tiling on its
	/// own renderer, so the inspector says when that is the case in the open
	/// scenes rather than pretending the map here is what shows.
	/// </remarks>
	public class InsertCoverGUI : LightingShaderGraphGUI
	{
		public InsertCoverGUI()
		{
			var index = uiBlocks.FindIndex(block => block is ShaderGraphUIBlock);
			if (index < 0) {
				index = uiBlocks.Count;
			} else {
				uiBlocks.RemoveAt(index);
			}
			uiBlocks.Insert(index, new HazeBlock());
			uiBlocks.Insert(index + 1, new CoverSurfaceBlock());
		}
	}

	/// <summary>What the cover blends over the body.</summary>
	internal sealed class HazeBlock : InsertBlock
	{
		public HazeBlock() : base(ExpandableBit.User0, "Haze") { }

		protected override void Draw()
		{
			Field("_CoverOpacity", "Opacity",
				"How much of the haze shows over the body. A hundredth is a clean top with only its reflection; five to ten hundredths a milky veil that softens the ribs like a worn, slightly frosted top. The reflections stay at full strength whatever the value.");
			Field("_HazeColor", "Haze Color",
				"The colour of that veil. A light grey for worn clear plastic; darker greys dull the body, white brightens it.",
				Above("_CoverOpacity", 0f));
		}
	}

	/// <summary>The top's reflections and the fine detail that shapes them.</summary>
	internal sealed class CoverSurfaceBlock : InsertBlock
	{
		private static readonly GUIContent NormalMapLabel = new("Normal Map",
			"Scratches, grain or the playfield's own texture on the top, laid out in the cover's 0-1 UVs. An insert with Use Playfield Normals on takes the playfield's map and UVs on its own renderer instead.");

		public CoverSurfaceBlock() : base(ExpandableBit.User1, "Surface") { }

		protected override void Draw()
		{
			Field("_Smoothness", "Smoothness",
				"How sharp the reflections of the lights and probes are on the top. Match the playfield's for a highlight that runs across cover and playfield alike.");

			var map = FindProperty("_CoverNormalMap");
			if (map != null) {
				materialEditor.TexturePropertySingleLine(NormalMapLabel, map);
				using (new EditorGUI.IndentLevelScope()) {
					using (new EditorGUI.DisabledScope(!HasTexture("_CoverNormalMap"))) {
						materialEditor.TextureScaleOffsetProperty(map);
					}
				}
			}
			var fromPlayfield = InsertsUsingPlayfieldNormals();
			Field("_CoverNormalStrength", "Normal Strength",
				"Scales the normal map. With Use Playfield Normals on the insert it multiplies the playfield material's own normal strength; one matches it.",
				HasTexture("_CoverNormalMap") || fromPlayfield > 0);
			if (fromPlayfield > 0) {
				EditorGUILayout.HelpBox(fromPlayfield == 1
					? "One insert in the open scenes takes this cover's normal map and tiling from its playfield instead. Normal Strength still applies there."
					: $"{fromPlayfield} inserts in the open scenes take this cover's normal map and tiling from their playfield instead. Normal Strength still applies there.", MessageType.Info);
			}
		}

		private int _fromPlayfield;

		/// <summary>
		/// How many inserts in the open scenes, or the prefab being edited, wear
		/// one of the selected materials as a cover, or a variant of one, and are
		/// actually taking their normals from the playfield right now.
		/// </summary>
		/// <remarks>
		/// Counted once per layout pass, and read back for the repaint that follows.
		/// </remarks>
		private int InsertsUsingPlayfieldNormals()
		{
			if (Event.current.type != EventType.Layout) {
				return _fromPlayfield;
			}
			var stage = PrefabStageUtility.GetCurrentPrefabStage();
			var inserts = stage != null
				? stage.prefabContentsRoot.GetComponentsInChildren<InsertComponent>(true)
				: Object.FindObjectsByType<InsertComponent>(FindObjectsInactive.Include);
			// CoverWarning is set whenever the component fell back to the
			// material's own map, so a warning-free insert is one whose override
			// is really on its renderer.
			_fromPlayfield = inserts.Count(insert => insert.UsePlayfieldNormals && insert.CoverWarning == null && Wears(insert.CoverMaterial));
			return _fromPlayfield;
		}

		/// <summary>Whether the material is one of the selected ones, or a variant descending from one, which inherits what is edited here.</summary>
		private bool Wears(Material worn)
		{
			for (var material = worn; material != null; material = material.parent) {
				if (materials.Contains(material)) {
					return true;
				}
			}
			return false;
		}
	}
}
