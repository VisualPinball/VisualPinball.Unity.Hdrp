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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;
using VisualPinball.Unity.Editor;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	/// <summary>
	/// Shoots the "Insert Showcase" in the scene straight from above, lit and
	/// as the geometry gizmo, each on black and on white so the background can
	/// be matted out, for the overview page of the inserts documentation.
	/// </summary>
	public static class AutomationShowcase
	{
		private const string Showcase = "Insert Showcase";
		/// <summary>The layer the gizmo meshes are rendered on, alone.</summary>
		private const int Layer = 30;
		private const int Width = 8192;

		private static string Folder => Automation.Folder("showcase");

		private static GameObject Root => GameObject.Find(Showcase) ?? throw new System.InvalidOperationException($"There is no active GameObject named \"{Showcase}\" in the open scenes.");

		// Active inserts with a body material: one without a material is left out.
		private static InsertComponent[] Inserts => Root.GetComponentsInChildren<InsertComponent>(false)
			.Where(insert => insert.gameObject.activeInHierarchy && insert.GetComponent<MeshRenderer>().sharedMaterial != null)
			.ToArray();

		private static void Log(string file, string text) => File.WriteAllText(Path.Combine(Folder, file), text);

		/// <summary>Lists the showcase's inserts with their materials and lamps into "list.txt".</summary>
		[MenuItem(Automation.Menu + "Showcase/List")]
		public static void List()
		{
			var sb = new StringBuilder();
			foreach (var insert in Inserts) {
				var light = insert.GetComponent<InsertLightComponent>();
				var body = insert.GetComponent<MeshRenderer>().sharedMaterial;
				var brightness = body != null && body.HasProperty("_InsertLuminosity") ? body.GetFloat("_InsertLuminosity") : -1f;
				sb.AppendLine($"{insert.name} | pos {insert.transform.position} | body {body?.name} ({AssetDatabase.GetAssetPath(body)}) color {InsertColorPresets.LinkedPreset(body)?.ColorName ?? "-"} max brightness {brightness} | cover {insert.CoverMaterial?.name} | lamp {light?.Lamp} intensity {light?.Intensity} output {light?.Output} | bake fresh {insert.IsBakeFresh}");
			}
			var cam = Camera.main;
			sb.AppendLine($"main camera {(cam != null ? cam.name : "none")} ortho {(cam != null && cam.orthographic)}");
			Log("list.txt", sb.ToString());
		}

		/// <summary>
		/// Gives every insert of the showcase the red color and the incandescent
		/// lamp at full intensity, so they compare alike. The inserts' own
		/// materials are linked to the red color too, for one Max Brightness and
		/// emission.
		/// </summary>
		[MenuItem(Automation.Menu + "Showcase/Align Colors")]
		public static void Align()
		{
			var red = InsertColorPresets.Find().First(preset => preset.ColorName == "Red");
			var lamp = InsertLampPresets.Find().First(preset => preset.LampName == "Incandescent");
			var sb = new StringBuilder();
			foreach (var insert in Inserts) {
				var applied = InsertColorPresets.CanApply(insert) && InsertColorPresets.Apply(red, insert, false);
				var body = insert.GetComponent<MeshRenderer>().sharedMaterial;
				if (InsertColorPresets.CanLink(body, false) && InsertColorPresets.LinkedPreset(body) != red) {
					InsertColorPresets.Link(body, red, false);
				}
				if (insert.CoverMaterial != null && InsertColorPresets.CanLink(insert.CoverMaterial, true) && InsertColorPresets.LinkedPreset(insert.CoverMaterial) != red) {
					InsertColorPresets.Link(insert.CoverMaterial, red, true);
				}
				var light = insert.GetComponent<InsertLightComponent>();
				if (light != null) {
					InsertLampPresets.Apply(lamp, new[] { light });
					light.Intensity = 1f;
					light.Apply();
				}
				sb.AppendLine($"{insert.name}: red {applied} -> {insert.GetComponent<MeshRenderer>().sharedMaterial?.name} / {insert.CoverMaterial?.name}; lamp {light?.Lamp}");
			}
			AssetDatabase.SaveAssets();
			Log("align.txt", sb.ToString());
		}

		/// <summary>The lit showcase into "lit-black.png" and "lit-white.png".</summary>
		[MenuItem(Automation.Menu + "Showcase/Shoot Lit")]
		public static void ShootLit()
		{
			var bounds = ShowcaseBounds();
			var cam = MakeCamera(bounds, Width, out var height, ~(1 << Layer));
			var hd = cam.GetComponent<HDAdditionalCameraData>();
			try {
				Render(cam, hd, Color.black, Width, height, "lit-black.png");
				Render(cam, hd, Color.white, Width, height, "lit-white.png");
			} finally {
				Object.DestroyImmediate(cam.gameObject);
			}
			Log("lit-done.txt", $"{bounds} {Width}x{height}");
		}

		/// <summary>
		/// The gizmo into "gizmo-black.png" and "gizmo-white.png", rebuilt as
		/// meshes: the body as a see-through yellow fill, the wires as lines.
		/// </summary>
		[MenuItem(Automation.Menu + "Showcase/Shoot Gizmo")]
		public static void ShootGizmo()
		{
			var bounds = ShowcaseBounds();
			var temps = new List<GameObject>();
			var shader = Shader.Find("HDRP/Unlit");
			var fill = new Material(shader);
			fill.SetColor("_UnlitColor", new Color(1f, 0.85f, 0.1f, 0.3f));
			HDMaterial.SetSurfaceType(fill, true);
			HDMaterial.ValidateMaterial(fill);
			var wire = new Material(shader);
			wire.SetColor("_UnlitColor", new Color(1f, 0.85f, 0.1f, 1f));
			HDMaterial.ValidateMaterial(wire);
			var edgesField = typeof(InsertComponent).GetField("_gizmoEdges", BindingFlags.NonPublic | BindingFlags.Instance);
			var sb = new StringBuilder();
			Camera cam = null;
			try {
				foreach (var insert in Inserts) {
					var mesh = insert.GetComponent<MeshFilter>().sharedMesh;
					// The component keeps its preview edges only while a bake is stale: the creases and contours come from the mesh instead.
					var edges = (Vector3[])edgesField?.GetValue(insert) ?? CreaseEdges(mesh);
					var fillMesh = new Mesh { vertices = mesh.vertices, triangles = mesh.GetTriangles(0) };
					fillMesh.RecalculateBounds();
					temps.Add(Twin(insert.transform, "fill", fillMesh, fill));
					if (edges.Length > 1) {
						var lines = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = edges };
						lines.SetIndices(Enumerable.Range(0, edges.Length).ToArray(), MeshTopology.Lines, 0);
						lines.RecalculateBounds();
						temps.Add(Twin(insert.transform, "wires", lines, wire));
					}
					sb.AppendLine($"{insert.name}: {mesh.triangles.Length / 3} tris, {edges.Length / 2} wires");
				}
				cam = MakeCamera(bounds, Width, out var height, 1 << Layer);
				var hd = cam.GetComponent<HDAdditionalCameraData>();
				Render(cam, hd, Color.black, Width, height, "gizmo-black.png");
				Render(cam, hd, Color.white, Width, height, "gizmo-white.png");
			} finally {
				if (cam != null) {
					Object.DestroyImmediate(cam.gameObject);
				}
				foreach (var go in temps) {
					Object.DestroyImmediate(go.GetComponent<MeshFilter>().sharedMesh);
					Object.DestroyImmediate(go);
				}
				Object.DestroyImmediate(fill);
				Object.DestroyImmediate(wire);
			}
			Log("gizmo-done.txt", sb.ToString());
		}

		/// <summary>An object on the gizmo layer, placed like the insert, showing the mesh.</summary>
		private static GameObject Twin(Transform insert, string name, Mesh mesh, Material material)
		{
			var go = new GameObject(name) { layer = Layer };
			go.transform.SetPositionAndRotation(insert.position, insert.rotation);
			go.transform.localScale = insert.lossyScale;
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			go.AddComponent<MeshRenderer>().sharedMaterial = material;
			return go;
		}

		private static Bounds ShowcaseBounds()
		{
			var inserts = Inserts;
			var bounds = new Bounds(inserts[0].transform.position, Vector3.zero);
			foreach (var insert in inserts) {
				bounds.Encapsulate(insert.GetComponent<Renderer>().bounds);
			}
			return bounds;
		}

		/// <summary>An orthographic camera looking straight down on the bounds, copied from the main camera for its exposure and volumes.</summary>
		private static Camera MakeCamera(Bounds bounds, int width, out int height, int cullingMask)
		{
			var go = new GameObject("Automation Showcase Camera");
			var cam = go.AddComponent<Camera>();
			var main = Camera.main;
			if (main != null) {
				cam.CopyFrom(main);
			}
			var hd = go.AddComponent<HDAdditionalCameraData>();
			var mainHd = main != null ? main.GetComponent<HDAdditionalCameraData>() : null;
			if (mainHd != null) {
				mainHd.CopyTo(hd);
			}
			const float margin = 0.03f;
			var w = bounds.size.x + 2 * margin;
			var h = bounds.size.z + 2 * margin;
			height = Mathf.RoundToInt(width * h / w);
			cam.orthographic = true;
			cam.aspect = w / h;
			cam.orthographicSize = h / 2f;
			cam.nearClipPlane = 0.01f;
			cam.farClipPlane = 5f;
			cam.cullingMask = cullingMask;
			go.transform.position = bounds.center + Vector3.up * 1f;
			go.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
			hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
			hd.volumeLayerMask = mainHd != null ? mainHd.volumeLayerMask : (LayerMask)~0;
			return cam;
		}

		private static void Render(Camera cam, HDAdditionalCameraData hd, Color background, int width, int height, string file)
		{
			hd.backgroundColorHDR = background;
			var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
			var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
			try {
				cam.targetTexture = rt;
				// The first frame settles exposure and history.
				cam.Render();
				cam.Render();
				RenderTexture.active = rt;
				tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				tex.Apply();
				File.WriteAllBytes(Path.Combine(Folder, file), tex.EncodeToPNG());
			} finally {
				RenderTexture.active = null;
				cam.targetTexture = null;
				Object.DestroyImmediate(tex);
				rt.Release();
				Object.DestroyImmediate(rt);
			}
		}

		/// <summary>Every edge on a boundary or between faces that meet at an angle: the contours, the wall corners, the facets and the hole walls, without the diagonals of flat faces.</summary>
		private static Vector3[] CreaseEdges(Mesh mesh)
		{
			var v = mesh.vertices;
			var tris = mesh.GetTriangles(0);
			// Welded by position: the generator splits vertices along creases for shading.
			var index = new Dictionary<Vector3, int>();
			var ids = new int[v.Length];
			for (var i = 0; i < v.Length; i++) {
				var key = new Vector3(Mathf.Round(v[i].x * 1e5f), Mathf.Round(v[i].y * 1e5f), Mathf.Round(v[i].z * 1e5f));
				if (!index.TryGetValue(key, out var id)) {
					id = index.Count;
					index[key] = id;
				}
				ids[i] = id;
			}
			var faces = new Dictionary<(int, int), List<Vector3>>();
			for (var t = 0; t < tris.Length; t += 3) {
				var a = v[tris[t]];
				var b = v[tris[t + 1]];
				var c = v[tris[t + 2]];
				var n = Vector3.Cross(b - a, c - a);
				if (n.sqrMagnitude < 1e-16f) {
					continue;
				}
				n.Normalize();
				for (var k = 0; k < 3; k++) {
					var i0 = ids[tris[t + k]];
					var i1 = ids[tris[t + (k + 1) % 3]];
					if (i0 == i1) {
						continue;
					}
					var key = i0 < i1 ? (i0, i1) : (i1, i0);
					if (!faces.TryGetValue(key, out var list)) {
						list = new List<Vector3>();
						faces[key] = list;
					}
					list.Add(n);
				}
			}
			var positions = new Vector3[index.Count];
			for (var i = 0; i < v.Length; i++) {
				positions[ids[i]] = v[i];
			}
			var edges = new List<Vector3>();
			foreach (var (key, normals) in faces) {
				if (normals.Count != 2 || Vector3.Angle(normals[0], normals[1]) > 2f) {
					edges.Add(positions[key.Item1]);
					edges.Add(positions[key.Item2]);
				}
			}
			return edges.ToArray();
		}
	}
}
