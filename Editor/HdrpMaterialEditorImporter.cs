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

using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VisualPinball.Unity;
using VisualPinball.Unity.Editor;
using Logger = NLog.Logger;
using Object = UnityEngine.Object;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	// Editor-side .vpe material reconstruction. Drives the same HdrpMaterialResolver the player
	// uses — so property mapping has a single home — but feeds it the freshly imported texture
	// assets and persists the resulting materials as .mat assets.
	internal sealed class HdrpMaterialEditorImporter : IVpeMaterialEditorImporter
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		public VpeMaterialImportResult Apply(
			Transform tableRoot,
			VpeMaterialsPayload payload,
			IReadOnlyDictionary<string, Texture2D> texturesById,
			string materialAssetFolder,
			Func<string, Transform> resolveNode)
		{
			if (!tableRoot || payload?.Profiles == null) {
				return new VpeMaterialImportResult();
			}

			// The imported textures are real Unity assets: normal maps come back as proper
			// normal-map imports that HDRP samples natively, so the resolver must not re-pack them.
			foreach (var profile in payload.Profiles) {
				SetAssetNormalPacking(profile?.Lit?.NormalMap);
				SetAssetNormalPacking(profile?.Fabric?.Lit?.NormalMap);
				SetAssetNormalPacking(profile?.Decal?.NormalMap);
			}

			var resolver = new HdrpMaterialResolver(
				NewLitTemplate("VpeImportLitOpaque"),
				NewLitTemplate("VpeImportLitTransparent"),
				litTranslucentThinTemplate: NewLitTemplate("VpeImportLitTranslucent"),
				fabricSilkTemplate: NewFabricSilkTemplate(),
				decalTemplate: NewDecalTemplate(),
				materialOverrides: FindShaderGraphTemplates(payload),
				insertHybridTemplate: NewInsertTemplate("Visual Pinball/Insert Hybrid"),
				insertCoverTemplate: NewInsertTemplate("Visual Pinball/Insert Cover"));

			var provider = new AssetTextureProvider(texturesById);
			var profilesByName = new Dictionary<string, VpeMaterialProfile>(StringComparer.Ordinal);
			var profilesById = new Dictionary<string, VpeMaterialProfile>(StringComparer.Ordinal);
			var boundIds = new HashSet<string>(StringComparer.Ordinal);
			foreach (var binding in payload.Bindings ?? Array.Empty<VpeMaterialBinding>()) {
				if (!string.IsNullOrEmpty(binding?.ProfileId)) { boundIds.Add(binding.ProfileId); }
			}
			foreach (var profile in payload.Profiles) {
				if (profile != null && !string.IsNullOrWhiteSpace(profile.Name)
					&& (boundIds.Count == 0 || boundIds.Contains(profile.Id))) {
					profilesByName[VpeMaterialNameUtil.NormalizeMaterialName(profile.Name)] = profile;
				}
				if (profile != null && !string.IsNullOrEmpty(profile.Id)) { profilesById[profile.Id] = profile; }
			}
			var profilesBySlot = new Dictionary<(Renderer, int), VpeMaterialProfile>();
			if (payload.Bindings != null) {
				foreach (var binding in payload.Bindings) {
					if (binding == null || string.IsNullOrEmpty(binding.ProfileId)
						|| !profilesById.TryGetValue(binding.ProfileId, out var profile)) { continue; }
					var node = resolveNode?.Invoke(binding.NodeId);
					if (node && node.TryGetComponent<Renderer>(out var renderer)) {
						profilesBySlot[(renderer, binding.Slot)] = profile;
					}
				}
			}

			var materialsByProfile = new Dictionary<VpeMaterialProfile, Material>();
			var materialsById = new Dictionary<string, Material>(StringComparer.Ordinal);
			var appliedSlots = 0;
			foreach (var renderer in tableRoot.GetComponentsInChildren<Renderer>(true)) {
				if (!renderer) {
					continue;
				}
				var materials = renderer.sharedMaterials;
				var modified = false;
				for (var i = 0; i < materials.Length; i++) {
					var imported = materials[i];
					if (!imported) {
						continue;
					}
					var key = VpeMaterialNameUtil.NormalizeMaterialName(imported.name);
					if (!profilesBySlot.TryGetValue((renderer, i), out var profile)
						&& !profilesByName.TryGetValue(key, out profile)) {
						continue;
					}

					if (!materialsByProfile.TryGetValue(profile, out var material)) {
						material = resolver.CreateMaterial(profile, provider, imported);
						if (material) {
							var assetPath = $"{materialAssetFolder}/{SanitizeFileName(profile.Name ?? "Material")}-{SanitizeFileName(profile.Id ?? "material")}.mat";
							AssetDatabase.CreateAsset(material, assetPath);
							if (!string.IsNullOrEmpty(profile.Id)) { materialsById[profile.Id] = material; }
						}
						materialsByProfile[profile] = material;
					}
					if (!material) {
						continue;
					}

					materials[i] = material;
					modified = true;
					appliedSlots++;
				}
				if (modified) {
					renderer.sharedMaterials = materials;
				}
			}
			// The editor sidecar can reference a pre-bake or bake-preview material that is
			// no longer assigned to a renderer. Persist those profiles as assets too.
			foreach (var profile in payload.Profiles) {
				if (profile == null || materialsByProfile.ContainsKey(profile)) { continue; }
				var material = resolver.CreateMaterial(profile, provider, null);
				if (!material) { continue; }
				var assetPath = $"{materialAssetFolder}/{SanitizeFileName(profile.Name ?? "Material")}-{SanitizeFileName(profile.Id ?? "material")}.mat";
				AssetDatabase.CreateAsset(material, assetPath);
				if (!string.IsNullOrEmpty(profile.Id)) { materialsById[profile.Id] = material; }
			}

			ApplyRendererStates(tableRoot, payload, resolveNode);
			AssetDatabase.SaveAssets();
			return new VpeMaterialImportResult { AppliedSlots = appliedSlots, MaterialsById = materialsById };
		}

		private static void SetAssetNormalPacking(VpeNormalMapRef normalMap)
		{
			if (normalMap != null && !string.IsNullOrEmpty(normalMap.TextureId)) {
				normalMap.Packing = VpeNormalPackings.Dxt5nm;
				normalMap.RuntimeCompress = false;
			}
		}

		private static Material NewLitTemplate(string name)
		{
			var shader = Shader.Find("HDRP/Lit");
			return shader ? new Material(shader) { name = name } : null;
		}

		private static Material NewInsertTemplate(string shaderName)
		{
			var shader = Shader.Find(shaderName);
			return shader ? new Material(shader) { name = $"VpeImport{shaderName.Split('/')[1]}" } : null;
		}

		private static Material NewDecalTemplate()
		{
			var shader = Shader.Find("HDRP/Decal");
			return shader ? new Material(shader) { name = "VpeImportDecal" } : null;
		}

		private static Material NewFabricSilkTemplate()
		{
			var shader = Shader.Find("HDRP/Fabric/Fabric Silk");
			return shader ? new Material(shader) { name = "VpeImportFabricSilk" } : null;
		}

		// Locates shader-graph template materials (metal/rubber/DMD) by their captured template
		// names, searching the HDRP package and the project.
		private static Dictionary<string, Material> FindShaderGraphTemplates(VpeMaterialsPayload payload)
		{
			var overrides = new Dictionary<string, Material>(StringComparer.Ordinal);
			foreach (var profile in payload.Profiles) {
				var templateName = profile?.Metal?.TemplateName ?? profile?.Rubber?.TemplateName ?? profile?.Dmd?.TemplateName;
				if (string.IsNullOrWhiteSpace(templateName) || overrides.ContainsKey(templateName)) {
					continue;
				}
				foreach (var guid in AssetDatabase.FindAssets($"t:Material {templateName}")) {
					var assetPath = AssetDatabase.GUIDToAssetPath(guid);
					var candidate = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
					if (candidate && string.Equals(candidate.name, templateName, StringComparison.Ordinal)) {
						overrides[templateName] = candidate;
						break;
					}
				}
			}
			return overrides;
		}

		private static void ApplyRendererStates(Transform tableRoot, VpeMaterialsPayload payload, Func<string, Transform> resolveNode)
		{
			if (payload.RendererStates == null) {
				return;
			}
			foreach (var state in payload.RendererStates) {
				if (state == null) {
					continue;
				}
				Transform target = null;
				if (!string.IsNullOrEmpty(state.NodeId) && resolveNode != null) {
					target = resolveNode(state.NodeId);
				}
				if (!target || !target.TryGetComponent<Renderer>(out var renderer)) {
					continue;
				}
				renderer.shadowCastingMode = VpeMaterialEnums.ParseShadowCastingMode(state.CastShadows);
				renderer.receiveShadows = state.ReceiveShadows;
				renderer.renderingLayerMask = state.RenderingLayerMask;
				if (state.Hdrp != null && state.Hdrp.RayTracingMode >= 0) {
					renderer.rayTracingMode = (UnityEngine.Experimental.Rendering.RayTracingMode)state.Hdrp.RayTracingMode;
				}
			}
		}

		private static string SanitizeFileName(string name)
		{
			var invalid = Path.GetInvalidFileNameChars();
			var chars = name.ToCharArray();
			for (var i = 0; i < chars.Length; i++) {
				if (Array.IndexOf(invalid, chars[i]) >= 0) {
					chars[i] = '_';
				}
			}
			return new string(chars);
		}

		private sealed class AssetTextureProvider : IVpeTextureProvider
		{
			private readonly IReadOnlyDictionary<string, Texture2D> _texturesById;

			public AssetTextureProvider(IReadOnlyDictionary<string, Texture2D> texturesById)
			{
				_texturesById = texturesById ?? new Dictionary<string, Texture2D>();
			}

			public Texture2D Get(string textureId)
			{
				return !string.IsNullOrEmpty(textureId) && _texturesById.TryGetValue(textureId, out var texture)
					? texture
					: null;
			}
		}
	}

	[InitializeOnLoad]
	internal static class HdrpMaterialEditorImporterRegistration
	{
		static HdrpMaterialEditorImporterRegistration()
		{
			VpeMaterialEditorImport.Register(new HdrpMaterialEditorImporter());
		}
	}
}
