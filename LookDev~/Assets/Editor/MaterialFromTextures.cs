// MaterialFromTextures.cs
// Place this script inside an "Editor" folder in a Unity project running **HDRP**.
// Adds **Assets ▸ Create ▸ Generate HDRP Materials From Selection** to the Project View
// whenever one or more **Texture2D** assets are selected. It converts those textures
// into HDRP/Lit materials based on suffixes:
//   <BaseName>_BaseMap.<ext>  (required)
//   <BaseName>_MaskMap.<ext>  (optional)
//   <BaseName>_Normal.<ext>   (optional)
// The resulting material **<BaseName>.mat** is written next to its BaseMap texture.

using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;

namespace EditorTools
{
	public static class MaterialFromTextures
	{
		private const string BaseSuffix = "BaseMap";
		private const string MaskSuffix = "MaskMap";
		private const string NormalSuffix = "Normal";

		//------------------------------------------------------------------
		// Menu item
		//------------------------------------------------------------------
		[MenuItem("Assets/Create/Generate HDRP Materials From Selection", priority = 2050)]
		private static void Generate()
		{
			var selectedTextures = Selection.GetFiltered<Texture2D>(SelectionMode.Assets);
			if (selectedTextures.Length == 0) {
				EditorUtility.DisplayDialog("Generate HDRP Materials", "Select one or more texture assets first.", "OK");
				return;
			}

			// Build groups keyed by base name (local dictionary)
			var groups = new Dictionary<string, MaterialTextures>();

			foreach (var tex in selectedTextures) {
				var path = AssetDatabase.GetAssetPath(tex);
				var fileName = Path.GetFileNameWithoutExtension(path);

				AddTextureToGroup(fileName, tex, path, BaseSuffix, groups, (g, t) => g.baseMap = t, (g, p) => g.baseFolder = Path.GetDirectoryName(p));
				AddTextureToGroup(fileName, tex, path, MaskSuffix, groups, (g, t) => g.maskMap = t);
				AddTextureToGroup(fileName, tex, path, NormalSuffix, groups, (g, t) => g.normalMap = t);
			}

			var hdrpShader = Shader.Find("HDRP/Lit");
			if (hdrpShader == null) {
				EditorUtility.DisplayDialog("Generate HDRP Materials", "Shader 'HDRP/Lit' not found. Is HDRP installed?", "OK");
				return;
			}

			var created = 0;
			foreach (var kvp in groups) {

				var baseName = kvp.Key;
				var maps = kvp.Value;
				if (maps.baseMap == null || string.IsNullOrEmpty(maps.baseFolder)) {
					continue; // Need at least a BaseMap and its location
				}

				var matPath = Path.Combine(maps.baseFolder, baseName + ".mat").Replace("\\", "/");
				var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
				var isNew = mat == null;

				if (isNew) {
					mat = new Material(hdrpShader);
					AssetDatabase.CreateAsset(mat, matPath);
					created++;
				}

				AssignHDRPTextures(mat, maps);
				EditorUtility.SetDirty(mat);
			}

			AssetDatabase.SaveAssets();
			AssetDatabase.Refresh();
			EditorUtility.DisplayDialog("Generate HDRP Materials", $"Created/updated {created} material(s).", "OK");
		}

		//------------------------------------------------------------------
		// Validation – show the menu item only when textures are selected
		//------------------------------------------------------------------
		[MenuItem("Assets/Create/Generate HDRP Materials From Selection", validate = true)]
		private static bool ValidateGenerate() => Selection.GetFiltered<Texture2D>(SelectionMode.Assets).Length > 0;

		//------------------------------------------------------------------
		// Helpers
		//------------------------------------------------------------------
		private static void AddTextureToGroup(string fileName,
			Texture2D tex,
			string path,
			string suffix,
			Dictionary<string, MaterialTextures> groups,
			System.Action<MaterialTextures, Texture2D> texSetter,
			System.Action<MaterialTextures, string> folderSetter = null)
		{
			if (!fileName.EndsWith(suffix)) return;

			var baseName = TrimSuffix(fileName, suffix);
			var group = GetOrCreateGroup(groups, baseName);

			texSetter(group, tex);
			folderSetter?.Invoke(group, path);
		}

		private static string TrimSuffix(string name, string suffix) => name[..^suffix.Length].TrimEnd('_', ' ', '-');

		private class MaterialTextures
		{
			public Texture2D baseMap;
			public Texture2D maskMap;
			public Texture2D normalMap;
			public string baseFolder;
		}

		private static MaterialTextures GetOrCreateGroup(Dictionary<string, MaterialTextures> dict, string baseName)
		{
			if (!dict.TryGetValue(baseName, out var g)) {
				g = new MaterialTextures();
				dict[baseName] = g;
			}

			return g;
		}

		// Assign maps to an HDRP/Lit material
		private static void AssignHDRPTextures(Material mat, MaterialTextures m)
		{
			if (m.baseMap && mat.HasProperty("_BaseColorMap")) mat.SetTexture("_BaseColorMap", m.baseMap);
			if (m.maskMap && mat.HasProperty("_MaskMap")) mat.SetTexture("_MaskMap", m.maskMap);
			if (m.normalMap && mat.HasProperty("_NormalMap")) mat.SetTexture("_NormalMap", m.normalMap);
		}
	}
}
