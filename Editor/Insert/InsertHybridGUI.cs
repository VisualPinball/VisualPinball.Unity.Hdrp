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
using UnityEditor.Rendering;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;
using VisualPinball.Unity;
using VisualPinball.Unity.Editor;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	/// <summary>
	/// Material inspector of the <c>Pinball/Insert Hybrid</c> shader.
	/// </summary>
	/// <remarks>
	/// HDRP's own surface and advanced options stay as they are. Between them,
	/// its flat list of exposed properties is replaced by three sections of our
	/// own: the plastic as it looks without the lamp, the lamp seen through it,
	/// and the maps the bake wrote. Each control says what it does, and is
	/// switched off wherever the shader would ignore it.
	/// </remarks>
	public class InsertHybridGUI : LightingShaderGraphGUI
	{
		public InsertHybridGUI()
		{
			var index = uiBlocks.FindIndex(block => block is ShaderGraphUIBlock);
			if (index < 0) {
				index = uiBlocks.Count;
			} else {
				uiBlocks.RemoveAt(index);
			}
			uiBlocks.Insert(index, new PlasticBlock());
			uiBlocks.Insert(index + 1, new LampBlock());
			uiBlocks.Insert(index + 2, new BakeBlock());
		}

		/// <summary>
		/// Unity disables the inspector of a package's material that can't be
		/// written to, but not of a locked asset library's, whose files can be:
		/// VPE leaves those alone too, so the inspector is disabled here as well.
		/// The read-only note in the Color Preset row has the way out.
		/// </summary>
		protected override void OnMaterialGUI(MaterialEditor materialEditor, MaterialProperty[] props)
		{
			var locked = materialEditor.targets.OfType<Material>().Any(InsertColorPresets.IsReadOnly);
			using (new EditorGUI.DisabledScope(locked)) {
				base.OnMaterialGUI(materialEditor, props);
			}
		}
	}

	/// <summary>
	/// A section with an HDRP header, drawn from the properties the shader
	/// exposes. The base class only hands out its header through an internal
	/// constructor, so the header is drawn here instead.
	/// </summary>
	internal abstract class InsertBlock : MaterialUIBlock
	{
		private readonly GUIContent _header;
		private readonly ExpandableBit _bit;

		protected InsertBlock(ExpandableBit bit, string header)
		{
			_bit = bit;
			_header = new GUIContent(header);
		}

		public override void LoadMaterialProperties() { }

		public override void OnGUI()
		{
			using var scope = new MaterialHeaderScope(_header, (uint)_bit, materialEditor);
			if (scope.expanded) {
				// Where the values come from, once per draw: the presets one material follows.
				_look = materials.Length == 1 ? InsertMaterialPresets.LinkedPreset(materials[0]) : null;
				_color = materials.Length == 1 ? InsertColorPresets.LinkedPreset(materials[0]) : null;
				Draw();
			}
		}

		protected abstract void Draw();

		private InsertMaterialPreset _look;
		private InsertColorPreset _color;

		/// <summary>The properties the Color preset owns; every other visible number is the Look's.</summary>
		private static readonly string[] ColorOwned = { "_BaseColor", "_InsertCavityColor", "_EmissiveColor", "_InsertLuminosity" };

		/// <summary>
		/// Draws a property with the shader's own control: slider, field, colour
		/// or HDR colour. A value a preset owns says so in its tooltip, and
		/// gets a mark on its label when the material has edited it.
		/// </summary>
		protected void Field(string name, string label, string tooltip, bool enabled = true)
		{
			var property = FindProperty(name);
			if (property == null) {
				return;
			}
			var origin = Origin(name, property, out var edited);
			if (origin != null) {
				tooltip = origin + " " + tooltip;
				if (edited) {
					label += " •";
				}
			}
			using (new EditorGUI.DisabledScope(!enabled)) {
				materialEditor.ShaderProperty(property, new GUIContent(label, tooltip));
			}
		}

		/// <summary>Which preset the property comes from, if one, and whether the material has edited it.</summary>
		private string Origin(string name, MaterialProperty property, out bool edited)
		{
			edited = false;
			if (System.Array.IndexOf(ColorOwned, name) >= 0) {
				if (_color == null) {
					return null;
				}
				edited = name switch {
					"_BaseColor" => property.colorValue != _color.Tint,
					"_InsertCavityColor" => property.colorValue != _color.CavityColor,
					"_EmissiveColor" => property.colorValue != _color.LampEmission,
					"_InsertLuminosity" => !Mathf.Approximately(property.floatValue, _color.MaxBrightness),
					_ => false,
				};
				return edited ? $"Edited here; the Color preset {_color.ColorName} has another value and overwrites this one when it changes." : $"From the Color preset {_color.ColorName}.";
			}
			var entry = _look != null ? _look.Find(name) : null;
			if (entry == null) {
				return null;
			}
			edited = !InsertMaterialPreset.Matches(materials[0], entry);
			return edited
				? $"Edited here; the Look preset {_look.name} has {(entry.Kind == InsertMaterialPreset.Kind.Float ? entry.Float.ToString("0.###") : entry.Vector.ToString())} and overwrites this one when it changes."
				: $"From the Look preset {_look.name}.";
		}

		protected float Float(string name) => FindProperty(name)?.floatValue ?? 0f;

		/// <summary>Whether the value differs across the selected materials, in which case nothing is switched off because of it.</summary>
		protected bool Mixed(string name) => FindProperty(name)?.hasMixedValue ?? false;

		protected bool Above(string name, float threshold) => Mixed(name) || Float(name) > threshold;
		protected bool Below(string name, float threshold) => Mixed(name) || Float(name) < threshold;

		// With several materials selected, a control stays live when any of
		// them reads it; a warning shows when any of them lacks something.
		protected bool HasTexture(string name) => materials.Any(material => material.HasProperty(name) && material.GetTexture(name) != null);

		/// <summary>Whether every selected material has the optical maps, without which neither the lamp nor the cavity has anything to work from.</summary>
		protected bool HasOptics => materials.All(material => material.GetTexture("_OpticalNormalMap") != null && material.GetTexture("_OpticalResponseMap") != null);

		/// <summary>Whether the baked mesh has a starburst, which is where facet contrast, mottle and the cavity ribs happen.</summary>
		/// <summary>Whether the bake found facets: a starburst of four sectors or more, a gem, whose sides are stored past two thousand, or ribs or rings, whose half pitch is stored negative.</summary>
		protected bool HasStarburst => materials.Any(material => material.HasProperty("_OpticalStarburst") && (material.GetVector("_OpticalStarburst").z >= 4f || material.GetVector("_OpticalStarburst").z < 0f));

		protected bool IsClear => Above("_InsertClarity", 0f);
		protected bool IsTinted => Below("_InsertClarity", 1f);

		/// <summary>Whether the clear body's ribs read the virtual lamp at all.</summary>
		protected bool CavityView => IsClear && HasStarburst && Above("_InsertCavityContrast", 0f);

		/// <summary>
		/// The insert color preset the selected materials follow, to link or
		/// unlink them, and what the preset then controls.
		/// </summary>
		protected void LinkField(bool cover, string what)
		{
			var links = materials.Select(InsertColorPresets.LinkedPreset).Distinct().ToArray();
			var readOnly = materials.Any(material => InsertBaker.IsReadOnly(AssetDatabase.GetAssetPath(material)));
			using (new EditorGUI.DisabledScope(readOnly || EditorApplication.isPlaying)) {
				EditorGUI.showMixedValue = links.Length > 1;
				EditorGUI.BeginChangeCheck();
				var picked = (InsertColorPreset)EditorGUILayout.ObjectField(new GUIContent("Color Preset",
					$"The insert color preset this material follows: {what} come from it, now and whenever it changes. None unlinks it, keeping the values it has."),
					links.Length == 1 ? links[0] : null, typeof(InsertColorPreset), false);
				EditorGUI.showMixedValue = false;
				if (EditorGUI.EndChangeCheck()) {
					InsertColorPresets.LinkAsking(materials.Select(material => (material, cover)).ToArray(), picked, materials.Length == 1, "This material's");
					GUIUtility.ExitGUI();
				}
			}
			if (readOnly) {
				ReadOnlyField();
			} else if (links.Length == 1 && links[0] != null) {
				var preset = links[0];
				EditorGUILayout.HelpBox(InsertBaker.IsReadOnly(AssetDatabase.GetAssetPath(preset))
					? $"{what} come from {preset.ColorName}, and changes made to them here are overwritten when the preset changes, a look is applied, or the preset is updated with its package, which can't be changed. To keep them, unlink this material by setting Color Preset to none, or link it to a color preset of your own."
					: $"{what} come from {preset.ColorName}, and changes made to them here are overwritten when the preset changes. Copy them to the preset to keep them.", MessageType.Info);
				using (new EditorGUI.DisabledScope(materials.Length != 1 || EditorApplication.isPlaying || InsertBaker.IsReadOnly(AssetDatabase.GetAssetPath(preset)))) {
					if (GUILayout.Button(new GUIContent($"Copy to {preset.ColorName}", $"Takes this material's {what} into the preset, and from there into every material linked to it."))) {
						InsertColorPresets.Capture(preset, cover ? null : materials[0], cover ? materials[0] : null);
					}
				}
			}
		}

		/// <summary>
		/// What a material of a package that can't be written to, like the
		/// asset library's, is: Unity shows its inspector disabled. The way to
		/// change it is a copy in the project, put on the inserts wearing it.
		/// </summary>
		private void ReadOnlyField()
		{
			var wearing = materials.SelectMany(InsertColorPresets.Wearing).Distinct().ToArray();
			// The selected inserts that wear it, when the material is shown in their inspector.
			var selected = wearing.Where(insert => Selection.gameObjects.Contains(insert.gameObject)).ToArray();
			EditorGUILayout.HelpBox("This material belongs to a package that can't be changed, like the installed asset library, or to a locked asset library, so its values can't be edited here. To change it, edit the inserts that wear it: they then get a copy of it in the project, which keeps following this material, and its color preset, until a value is changed.", MessageType.Info);
			// The inspector of a read-only asset is disabled; these buttons are the way out of it.
			var enabled = GUI.enabled;
			GUI.enabled = !EditorApplication.isPlaying;
			const string tooltip = "Makes a copy of the material in the project and puts it on {0}, along with copies of their other insert material.";
			using (new EditorGUILayout.HorizontalScope()) {
				if (wearing.Length == 0) {
					if (GUILayout.Button(new GUIContent("Copy to Project", "Makes a copy of the material in the project and highlights it. No insert in the open scenes wears this material."))) {
						foreach (var material in materials) {
							EditorGUIUtility.PingObject(InsertColorPresets.ProjectCopy(material));
						}
						AssetDatabase.SaveAssets();
						Exit(enabled);
					}
				} else {
					if (selected.Length > 0 && selected.Length < wearing.Length
						&& GUILayout.Button(new GUIContent(selected.Length == 1 ? "Edit This Insert" : $"Edit {selected.Length} Selected Inserts",
							string.Format(tooltip, selected.Length == 1 ? "the selected insert" : "the selected inserts") + " The other inserts wearing it keep it."))) {
						InsertColorPresets.CopyToProject(selected);
						Exit(enabled);
					}
					if (GUILayout.Button(new GUIContent(wearing.Length == 1 ? "Edit This Insert" : $"Edit All {wearing.Length} Inserts",
						string.Format(tooltip, "every insert in the open scenes that wears it") + " They stay alike: changing the copy changes all of them."))) {
						InsertColorPresets.CopyToProject(wearing);
						Exit(enabled);
					}
				}
			}
			GUI.enabled = enabled;
		}

		private static void Exit(bool enabled)
		{
			GUI.enabled = enabled;
			GUIUtility.ExitGUI();
		}

		/// <summary>
		/// The look the selected materials follow, to link or unlink them, with
		/// the way back to it once values were edited, and the way to keep them.
		/// </summary>
		protected void LookField()
		{
			var links = materials.Select(InsertMaterialPresets.LinkedPreset).Distinct().ToArray();
			var readOnly = materials.Any(material => InsertBaker.IsReadOnly(AssetDatabase.GetAssetPath(material)));
			using (new EditorGUI.DisabledScope(readOnly || EditorApplication.isPlaying)) {
				EditorGUI.showMixedValue = links.Length > 1;
				EditorGUI.BeginChangeCheck();
				var picked = (InsertMaterialPreset)EditorGUILayout.ObjectField(new GUIContent("Look Preset",
					"The look this material follows: every numeric setting below but Max Brightness and the lamps, now and whenever the preset changes. None unlinks it, keeping the values it has. A value marked • was edited here."),
					links.Length == 1 ? links[0] : null, typeof(InsertMaterialPreset), false);
				EditorGUI.showMixedValue = false;
				if (EditorGUI.EndChangeCheck()) {
					InsertMaterialPresets.LinkAsking(materials, picked, materials.Length == 1, "This material's");
					GUIUtility.ExitGUI();
				}
			}
			// A read-only material says so, and how to change it, in its color preset's row.
			if (!readOnly && links.Length == 1 && links[0] != null && materials.Length == 1) {
				var preset = links[0];
				var edited = preset.Edited(materials[0]);
				EditorGUILayout.HelpBox(edited == 0
					? $"The look comes from {preset.name}; every value marked • below was edited here and goes back to the preset's when it changes."
					: $"{edited} values differ from {preset.name}, marked • below. They go back to the preset's when it changes: copy them to the preset to keep them, or reset them.", MessageType.Info);
				using (new EditorGUILayout.HorizontalScope()) {
					using (new EditorGUI.DisabledScope(edited == 0 || EditorApplication.isPlaying || InsertBaker.IsReadOnly(AssetDatabase.GetAssetPath(preset)))) {
						if (GUILayout.Button(new GUIContent($"Copy to {preset.name}", "Takes this material's look into the preset, and from there into every material linked to it."))) {
							InsertMaterialPresets.Capture(preset, materials[0]);
						}
					}
					using (new EditorGUI.DisabledScope(edited == 0 || EditorApplication.isPlaying)) {
						if (GUILayout.Button(new GUIContent($"Reset to {preset.name}", "Gives this material the preset's values again."))) {
							Undo.RecordObject(materials[0], "Reset Insert Look");
							InsertMaterialPresets.Refresh(materials[0]);
						}
					}
				}
			}
		}

		protected static void SubHeader(string title)
		{
			EditorGUILayout.Space(2f);
			EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
		}
	}

	/// <summary>The body as room light shows it, lamp off or on.</summary>
	internal sealed class PlasticBlock : InsertBlock
	{
		public PlasticBlock() : base(ExpandableBit.User0, "Plastic") { }

		protected override void Draw()
		{
			LinkField(false, "Tint, Cavity Color, Lamp Emission and Max Brightness");
			LookField();
			if (!HasOptics) {
				EditorGUILayout.HelpBox("This material has no optical bake. Enable Bake Hybrid Optics on the insert and bake it; until then the lamp and the cavity have no maps to work from.", MessageType.Warning);
			}

			Field("_BaseColor", "Tint",
				"Colour of the plastic. It multiplies the whole unlit body, tinted or clear, and nothing of the lamp: the lit colour comes from Lamp Emission and the Insert Light.");
			Field("_InsertClarity", "Clarity",
				"Zero is tinted plastic, dark until the lamp comes up. One is clear plastic, which shows the lamp board or reflector under it lit by room light, lamp or no lamp. In between blends the two.");
			using (new EditorGUI.IndentLevelScope()) {
				Field("_InsertCavityColor", "Cavity Color",
					"What lies under a clear insert: the lamp board or reflector. A light grey for a white board.",
					IsClear);
				Field("_InsertCavitySpread", "Cavity Spread",
					"How far the cavity stays bright around the virtual lamp, in lamp radii. Wide keeps the ribs even; narrow darkens the faces that look past the lamp.",
					CavityView);
				Field("_InsertCavityContrast", "Cavity Contrast",
					"How much darker a starburst face gets when its view lands past the cavity. Keep it low for the soft ribs of real clear plastic.",
					IsClear && HasStarburst);
				Field("_InsertMinBrightness", "Min Brightness",
					"How much of the tint shows under room light with the lamp off. Low keeps a red insert dark until it is lit; it fades to one as the lamp comes up. Only the tinted share of the body uses it.",
					IsTinted);
			}

			SubHeader("Surface");
			Field("_Smoothness", "Smoothness",
				"How sharp the reflections of the lights and probes are on the body. With a clear cover on the insert, the cover carries the glossy top and the body can be duller.");
			Field("_NormalStrength", "Surface Normal Strength",
				"How much of the baked surface normal map shapes those reflections: rounded edges, bevels and grain. Keep it low; the facets are in the mesh already.",
				HasTexture("_NormalMap") && Below("_InsertFacetFlatten", 1f));
			Field("_InsertFacetFlatten", "Facet Flatten",
				"Turns the shading normal the lights and probes see towards the insert's flat top. Zero keeps the facets, which suits coloured inserts; around 0.85 stops a clear insert alternating bright and dark ribs under a key light. The lamp's optics are not affected.");
			Field("_OpticalGrain", "Grain",
				"Fine noise over the plastic, lit and unlit, fixed in insert space. Zero is a smooth finish. Variation Seed picks another pattern.");
			Field("_SurfaceGrain", "Surface Grain",
				"The fine texture of the moulded plastic in its reflections, drawn in insert space at any zoom instead of baked into the normal map: keep the bake's Grain Strength at zero with it. Zero is a smooth surface. Variation Seed picks another pattern.");
			Field("_SurfaceGrainSize", "Surface Grain Size (mm)",
				"How large the grain's cells are. The bake's grain was 1.5 VPX units, about 0.75 mm.",
				Above("_SurfaceGrain", 0f));
		}
	}

	/// <summary>The lamp as the plastic shows it: a virtual bulb refracted by the facets, plus the glow the bake found.</summary>
	internal sealed class LampBlock : InsertBlock
	{
		private static readonly GUIContent PositionLabel = new("Position (mm)",
			"Where the virtual lamp sits, in insert space: X across, Y up, so negative is below the top, Z along the length. The bake suggests a start below the middle. Move it sideways to shift the bright patch, lower it for more parallax.");
		private static readonly GUIContent RadiusLabel = new("Radius (mm)",
			"Size of the virtual lamps' soft image, shared by every bulb. Larger softens the moving hotspot; the cavity of a clear insert is measured in this radius too.");
		private static readonly GUIContent BulbsLabel = new("Bulbs",
			"How many bulbs light the insert: a banana has its three or four in a row. Each has a position of its own, and on the Insert Light a colour and intensity of its own; the radius, Max Brightness and the emission colour are shared.");
		private static readonly GUIContent[] BulbLabels = {
			PositionLabel,
			new("Bulb 2 (mm)", "Where the second bulb sits, in insert space."),
			new("Bulb 3 (mm)", "Where the third bulb sits, in insert space."),
			new("Bulb 4 (mm)", "Where the fourth bulb sits, in insert space."),
		};
		private static readonly GUIContent[] BulbCounts = { new("1"), new("2"), new("3"), new("4") };
		private static readonly int[] BulbCountValues = { 1, 2, 3, 4 };
		private static readonly string[] LampNames = { "_OpticalLamp", "_OpticalLampB", "_OpticalLampC", "_OpticalLampD" };

		public LampBlock() : base(ExpandableBit.User1, "Lamp") { }

		protected override void Draw()
		{
			Field("_InsertLuminosity", "Max Brightness",
				"Multiplies the emission and the spill light at Intensity one. Not a slider on purpose: 10, 50 or 100 are reasonable values to try.");
			var lit = Above("_InsertLuminosity", 0f);
			Field("_EmissiveColor", "Lamp Emission",
				"Nominal radiance of the lit insert, HDR. Keep it neutral: the lamp's colour comes from the Insert Light or from RGB lamp events.",
				lit);
			if (!lit) {
				EditorGUILayout.HelpBox(IsClear
					? "Max Brightness is zero, so the lamp never shows. Only what the clear body's cavity shares with it stays live: the lamp's position and radius, and the refraction."
					: "Max Brightness is zero, so the lamp never shows. Nothing below has an effect until it is raised.", MessageType.Info);
			}
			// The cavity of a clear insert looks through the same facets at the
			// same lamp, so those controls stay live without any lamp.
			var bulb = lit && Above("_OpticalLampGain", 0f);
			var scatter = lit && Above("_OpticalScatter", 0f);
			// The facets shape the lamp's image and, through Facet Contrast,
			// the scattered glow on the starburst floor.
			var facets = HasStarburst && (bulb || scatter);

			SubHeader("Virtual Lamp");
			// The bulbs' places shape the lamp's image, the glow's colour and
			// fall-off, and where an Insert Light's Tint With Lamp takes each
			// bulb's colour, lamp or no lamp: they are always live.
			DrawLamp();
			Field("_OpticalLampGain", "Strength",
				"How much of the lit look is the lamp seen through the facets, against the baked glow below. Zero leaves only the glow.",
				lit);
			Field("_OpticalIor", "Index of Refraction",
				"How strongly the facets bend the view towards the lamp. Acrylic is about 1.49. A clear insert's cavity look-through bends the same way.",
				bulb || CavityView);

			// The spread also bounds how far each bulb's colour reaches into
			// the tint, lamp or no lamp.
			Field("_OpticalSpread", "Lamp Spread (mm)",
				"How far from each virtual bulb the lit look reaches before it has fallen to half, to a fifth at twice that and a tenth at three times: a real insert is brightest around its bulb and dims away from it, and a banana's bulbs meet in a soft blend. Zero lights the whole body alike, which floods it as Max Brightness goes up. An Insert Light's Tint With Lamp takes each bulb's colour as far as this too.");
			using (new EditorGUI.DisabledScope(!lit)) {
				SubHeader("Glow");
				Field("_OpticalScatter", "Baked Scatter",
					"Broad light scattered through the plastic, from the bake's cavity irradiance. Too much washes out the starburst.");
				Field("_OpticalEdgeGlow", "Edge Glow",
					"Light caught by rounded edges, rims and hole walls, from the bake's edge mask. Keep it low, or every edge turns into a neon outline.");

				SubHeader("Facets");
				if (!HasStarburst) {
					EditorGUILayout.HelpBox("The bake has no starburst, so there are no facets to vary.", MessageType.Info);
				}
				Field("_InsertRoughness", "Roughness",
					"How rough the structure inside the plastic is: the facets, channels and holes the light comes up through. Zero couples the lamp into crisp ribs and rays; one diffuses it, so the ribs soften and the rays and the bulb's image go into the glow. The top's finish is the cover's, not this.",
					facets);
				Field("_OpticalContrast", "Facet Contrast",
					"How differently the two slopes of each rib couple to the lamp. One gives crisp alternating ribs; about a quarter suits clear plastic.",
					facets);
				Field("_OpticalVariation", "Mottle Amount",
					"Cloudy variation across the ribs, and small differences from rib to rib.",
					facets);
				Field("_OpticalMottleSize", "Mottle Size (mm)",
					"Spacing of the mottle's cells. Smaller is finer. It fades out once the cells are smaller than a pixel.",
					facets && Above("_OpticalVariation", 0f));
			}
			// The seed also scatters the grain, which is there lamp or no lamp.
			Field("_OpticalSeed", "Variation Seed",
				"Another pattern for the mottle, the rib-to-rib differences and the grain.",
				Above("_OpticalGrain", 0f) || (facets && Above("_OpticalVariation", 0f)));
		}

		private static float Round(float millimetres) => Mathf.Round(millimetres * 100f) / 100f;
		private static Vector3 Round(Vector3 millimetres) => new(Round(millimetres.x), Round(millimetres.y), Round(millimetres.z));

		private void DrawLamp()
		{
			var lamps = LampNames.Select(name => FindProperty(name)).ToArray();
			if (lamps[0] == null) {
				return;
			}
			var materials = lamps[0].targets.OfType<Material>().ToArray();
			// A bulb is on while it has a radius. A revert or reset can leave a
			// lower bulb off under a higher one that is on, which the shader
			// still lights: the lower one is switched back on, so what the
			// inspector shows is what the shader draws.
			foreach (var material in materials) {
				var highest = 0;
				for (var i = 1; i < lamps.Length; i++) {
					if (lamps[i] != null && material.GetVector(LampNames[i]).w > 0f) {
						highest = i;
					}
				}
				for (var i = 1; i < highest; i++) {
					if (material.GetVector(LampNames[i]).w <= 0f) {
						SwitchOn(material, i);
					}
				}
			}
			var count = 1;
			while (count < lamps.Length && lamps[count] != null && materials.Any(m => m.GetVector(LampNames[count]).w > 0f)) {
				count++;
			}

			// The count and the shared radius write several properties: every
			// one of them is scoped, so a variant reverts them together.
			for (var i = 0; i < lamps.Length; i++) {
				if (lamps[i] != null) {
					MaterialEditor.BeginProperty(lamps[i]);
				}
			}
			EditorGUI.showMixedValue = materials.Length > 1 && materials.Any(m => Count(m, lamps) != count);
			EditorGUI.BeginChangeCheck();
			var wanted = EditorGUILayout.IntPopup(BulbsLabel, count, BulbCounts, BulbCountValues);
			if (EditorGUI.EndChangeCheck()) {
				Undo.RecordObjects(materials, "Change Insert Bulbs");
				foreach (var material in materials) {
					for (var i = 1; i < lamps.Length; i++) {
						if (lamps[i] == null) {
							continue;
						}
						if (i < wanted) {
							SwitchOn(material, i);
						} else {
							// Off keeps its place for when it comes back.
							var value = material.GetVector(LampNames[i]);
							value.w = 0f;
							material.SetVector(LampNames[i], value);
						}
					}
				}
				count = wanted;
			}
			EditorGUI.showMixedValue = false;
			for (var i = lamps.Length - 1; i >= 0; i--) {
				if (lamps[i] != null) {
					MaterialEditor.EndProperty();
				}
			}

			var wide = EditorGUIUtility.wideMode;
			EditorGUIUtility.wideMode = true;
			for (var i = 0; i < count; i++) {
				var lamp = lamps[i];
				// Shown to a hundredth of a millimetre: the bake leaves float noise
				// in the metres that would otherwise print as scientific notation.
				var value = lamp.vectorValue * 1000f;
				var position = Round(new Vector3(value.x, value.y, value.z));
				MaterialEditor.BeginProperty(lamp);
				EditorGUI.showMixedValue = lamp.hasMixedValue;
				EditorGUI.BeginChangeCheck();
				position = EditorGUILayout.Vector3Field(BulbLabels[i], position);
				if (EditorGUI.EndChangeCheck()) {
					// Each material keeps its own radius on the bulb.
					Undo.RecordObjects(materials, "Move Insert Bulb");
					foreach (var material in materials) {
						var own = material.GetVector(LampNames[i]);
						material.SetVector(LampNames[i], new Vector4(position.x, position.y, position.z, Mathf.Max(own.w * 1000f, 0.1f)) / 1000f);
					}
				}
				EditorGUI.showMixedValue = false;
				MaterialEditor.EndProperty();
			}
			EditorGUIUtility.wideMode = wide;

			// One radius for every bulb that is on, kept on the first: each
			// material's own bulbs get it, their places untouched.
			for (var i = 0; i < count; i++) {
				MaterialEditor.BeginProperty(lamps[i]);
			}
			var radius = Round(lamps[0].vectorValue.w * 1000f);
			EditorGUI.showMixedValue = lamps[0].hasMixedValue;
			EditorGUI.BeginChangeCheck();
			radius = EditorGUILayout.FloatField(RadiusLabel, radius);
			if (EditorGUI.EndChangeCheck()) {
				radius = Mathf.Max(radius, 0.1f) / 1000f;
				Undo.RecordObjects(materials, "Change Insert Bulb Radius");
				foreach (var material in materials) {
					for (var i = 0; i < lamps.Length; i++) {
						if (lamps[i] == null) {
							continue;
						}
						var value = material.GetVector(LampNames[i]);
						if (i == 0 || value.w > 0f) {
							value.w = radius;
							material.SetVector(LampNames[i], value);
						}
					}
				}
			}
			EditorGUI.showMixedValue = false;
			for (var i = 0; i < count; i++) {
				MaterialEditor.EndProperty();
			}
		}

		/// <summary>How many bulbs the material has on: the first and those with a radius.</summary>
		private static int Count(Material material, MaterialProperty[] lamps)
		{
			var count = 1;
			while (count < lamps.Length && lamps[count] != null && material.GetVector(LampNames[count]).w > 0f) {
				count++;
			}
			return count;
		}

		/// <summary>
		/// Switches a bulb on with the first bulb's radius. A bulb that was on
		/// before comes back where it was; one never placed starts a couple of
		/// radii along from the bulb before it.
		/// </summary>
		private static void SwitchOn(Material material, int index)
		{
			var value = material.GetVector(LampNames[index]);
			var radius = material.GetVector(LampNames[0]).w;
			if (value.x == 0f && value.y == 0f && value.z == 0f) {
				var previous = material.GetVector(LampNames[index - 1]);
				value = new Vector4(previous.x, previous.y, previous.z + radius * 2.5f, radius);
			} else {
				value.w = radius;
			}
			material.SetVector(LampNames[index], value);
		}
	}

	/// <summary>
	/// What the insert's bake wrote into the material. Editable, so a material
	/// that had another's properties copied over it, for a colour of its own,
	/// can have its own maps put back.
	/// </summary>
	internal sealed class BakeBlock : InsertBlock
	{
		private static readonly GUIContent SurfaceLabel = new("Surface Normal", "Tangent-space normal map of the rounded edges and the grain, for the reflections.");
		private static readonly GUIContent OpticalLabel = new("Optical Normals", "Sharp facet normals in insert space, for the lamp's refraction.");
		private static readonly GUIContent ResponseLabel = new("Optical Response", "Cavity irradiance, edge mask and transmission mask, one per channel.");
		private static readonly GUIContent SectorsLabel = new("Starburst Sectors", "How many sectors the fan the bake found has; the facet variation follows it. Below four there is no starburst.");
		private static readonly GUIContent CentreLabel = new("Starburst Centre (mm)", "Where the fan's centre sits in insert space, X across and Z along the length.");

		public BakeBlock() : base(ExpandableBit.User2, "Baked Data") { }

		protected override void Draw()
		{
			EditorGUILayout.HelpBox("Written by the insert's bake, and kept in step with its mesh by Bake Again on the Insert component. Copying another material's properties over this one replaces them; put this insert's own back here.", MessageType.None);
			Texture(SurfaceLabel, "_NormalMap");
			Texture(OpticalLabel, "_OpticalNormalMap");
			Texture(ResponseLabel, "_OpticalResponseMap");
			var starburst = FindProperty("_OpticalStarburst");
			if (starburst != null && starburst.vectorValue.z >= 2000f) {
				var gem = starburst.vectorValue;
				EditorGUILayout.HelpBox($"A gem of {gem.z - 2000f:0} sides round ({gem.x * 1000f:0.#}, {gem.y * 1000f:0.#}) mm in insert space; the facet variation turns round it smoothly.", MessageType.None);
			} else if (starburst != null && starburst.vectorValue.z <= -1000f) {
				var rings = starburst.vectorValue;
				EditorGUILayout.HelpBox($"Rings {(-rings.z - 1000f) * 2000f:0.##} mm apart round ({rings.x * 1000f:0.#}, {rings.y * 1000f:0.#}) mm in insert space; the facet variation follows them.", MessageType.None);
			} else if (starburst != null && starburst.vectorValue.z < 0f) {
				var ribs = starburst.vectorValue;
				EditorGUILayout.HelpBox($"Ribs {-ribs.z * 2000f:0.##} mm apart, running across ({ribs.x:0.##}, {ribs.y:0.##}) in insert space; the facet variation follows them.", MessageType.None);
			} else if (starburst != null) {
				var fan = starburst.vectorValue;
				MaterialEditor.BeginProperty(starburst);
				EditorGUI.showMixedValue = starburst.hasMixedValue;
				EditorGUI.BeginChangeCheck();
				var sectors = EditorGUILayout.IntField(SectorsLabel, Mathf.RoundToInt(fan.z));
				var sectorsChanged = EditorGUI.EndChangeCheck();
				EditorGUI.BeginChangeCheck();
				var wide = EditorGUIUtility.wideMode;
				EditorGUIUtility.wideMode = true;
				var centre = EditorGUILayout.Vector2Field(CentreLabel, new Vector2(fan.x, fan.y) * 1000f);
				EditorGUIUtility.wideMode = wide;
				var centreChanged = EditorGUI.EndChangeCheck();
				EditorGUI.showMixedValue = false;
				MaterialEditor.EndProperty();
				if (sectorsChanged || centreChanged) {
					// Each material keeps what was not edited, its floor height in
					// w above all: the property's own setter would copy the first
					// material's whole vector onto every one selected.
					var selected = starburst.targets.OfType<Material>().ToArray();
					Undo.RecordObjects(selected, "Edit Starburst");
					foreach (var material in selected) {
						var value = material.GetVector("_OpticalStarburst");
						if (centreChanged) {
							value.x = centre.x / 1000f;
							value.y = centre.y / 1000f;
						}
						if (sectorsChanged) {
							value.z = Mathf.Max(0, sectors);
						}
						material.SetVector("_OpticalStarburst", value);
					}
				}
			}
		}

		private void Texture(GUIContent label, string name)
		{
			var property = FindProperty(name);
			if (property != null) {
				materialEditor.TexturePropertySingleLine(label, property);
			}
		}
	}
}
