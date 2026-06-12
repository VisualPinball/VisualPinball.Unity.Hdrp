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
using System.Text;
using NLog;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Logger = NLog.Logger;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace VisualPinball.Engine.Unity.Hdrp
{
	// Turns portable material profiles (schema v2; legacy v1 payloads are upgraded before they
	// reach the resolver) into live HDRP Materials in the Player.
	//
	// The resolver clones a set of template materials shipped with the Player project. Each template
	// is an authored HDRP/Lit material whose keyword combination the build compiler preserves; cloning
	// inherits that compiled variant, so runtime keyword flipping never hits a stripped variant.
	//
	// To support additional surface/material types later: add a template asset + a new case in
	// CreateMaterial / Supports + apply method. No changes to the .vpe format required.
	public sealed class HdrpMaterialResolver : IVpeMaterialResolver, IVpeMaterialResolverDiagnostics
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
		private const bool VerboseResolverLogs = false;
		private readonly Material _litOpaqueTemplate;
		private readonly Material _litTransparentTemplate;
		private readonly Material _litTranslucentThinTemplate;
		private readonly Material _litTranslucentPlanarTemplate;
		private readonly Material _litTranslucentSphereTemplate;
		private readonly Material _decalTemplate;
		private readonly Dictionary<string, Material> _materialOverrides;
		private static readonly int MainTextureProperty = Shader.PropertyToID("_MainTex");
		private const string NormalRepackShaderResourcePath = "VpePackNormalForHdrp";

		// Re-packed normal maps are shared across every material slot that samples the same source.
		// gltFast-imported textures and side-channel textures both pass through here; the cache is
		// keyed on the source Texture2D so identity alone determines reuse.
		private readonly Dictionary<Texture2D, Texture> _repackedNormalCache = new();
		private static readonly HashSet<string> _loggedMissingBaseColorMap = new();
		private static readonly HashSet<string> _loggedApronBaseAssignments = new();
		private static readonly HashSet<string> _loggedApronImportedDump = new();
		private static readonly Dictionary<string, DiffusionProfileSettings> _diffusionProfilesByName = new(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<int, float> _diffusionProfileHashByInstance = new();
		private static readonly HashSet<string> _loggedDiffusionAssignments = new();
		private static readonly System.Reflection.FieldInfo _diffusionProfileField =
			typeof(DiffusionProfileSettings).GetField("profile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		private static readonly System.Reflection.FieldInfo _diffusionHashField =
			_diffusionProfileField?.FieldType?.GetField("hash", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		private static Material _normalRepackMaterial;
		private static bool _normalRepackMaterialQueried;
		private static bool _loggedMissingNormalRepackShader;
		private static bool _loggedNormalRepackGpuFallback;
		private readonly ResolverDiagnostics _diagnostics = new();

		public HdrpMaterialResolver(
			Material litOpaqueTemplate,
			Material litTransparentTemplate,
			Material litTranslucentThinTemplate = null,
			Material litTranslucentPlanarTemplate = null,
			Material litTranslucentSphereTemplate = null,
			Material decalTemplate = null,
			Dictionary<string, Material> materialOverrides = null)
		{
			_litOpaqueTemplate = litOpaqueTemplate;
			_litTransparentTemplate = litTransparentTemplate;
			_litTranslucentThinTemplate = litTranslucentThinTemplate;
			_litTranslucentPlanarTemplate = litTranslucentPlanarTemplate;
			_litTranslucentSphereTemplate = litTranslucentSphereTemplate;
			_decalTemplate = decalTemplate;
			_materialOverrides = materialOverrides ?? new Dictionary<string, Material>(StringComparer.Ordinal);
		}

		public bool Supports(string materialType)
		{
			return materialType switch {
				VpeMaterialTypes.Lit => _litOpaqueTemplate,
				VpeMaterialTypes.Decal => _decalTemplate,
				VpeMaterialTypes.Metal => true,
				VpeMaterialTypes.Rubber => true,
				VpeMaterialTypes.Dmd => true,
				_ => false,
			};
		}

		public Material CreateMaterial(VpeMaterialProfile profile, IVpeTextureProvider textures, Material importedMaterial)
		{
			if (profile == null) {
				return null;
			}

			_diagnostics.CreateCalls++;

			return profile.Type switch {
				VpeMaterialTypes.Lit => BuildLit(profile, textures, importedMaterial),
				VpeMaterialTypes.Decal => BuildDecal(profile, textures, importedMaterial),
				VpeMaterialTypes.Metal => BuildShaderGraphMaterial(profile, profile.Metal) ?? BuildLit(profile, textures, importedMaterial),
				VpeMaterialTypes.Rubber => BuildShaderGraphMaterial(profile, profile.Rubber) ?? BuildLit(profile, textures, importedMaterial),
				VpeMaterialTypes.Dmd => BuildShaderGraphMaterial(profile, profile.Dmd),
				_ => null,
			};
		}

		private Material BuildShaderGraphMaterial(VpeMaterialProfile profile, VpeShaderGraphProfile shaderGraph)
		{
			var templateName = shaderGraph?.TemplateName;
			if (string.IsNullOrWhiteSpace(templateName)) {
				templateName = profile.Name;
			}

			if (string.IsNullOrWhiteSpace(templateName)
				|| !_materialOverrides.TryGetValue(templateName, out var template)
				|| !template) {
				return null;
			}

			var cloneStopwatch = Stopwatch.StartNew();
			var material = new Material(template) { name = profile.Name };
			cloneStopwatch.Stop();
			_diagnostics.MaterialCloneMilliseconds += cloneStopwatch.ElapsedMilliseconds;
			material.enableInstancing = true;
			return material;
		}

		public void ResetDiagnostics()
		{
			PruneRepackedNormalCache();
			_diagnostics.Reset();
		}

		public string GetDiagnosticsSummary()
		{
			return _diagnostics.ToSummaryString();
		}

		private Material BuildDecal(VpeMaterialProfile profile, IVpeTextureProvider textures, Material imported)
		{
			_diagnostics.DecalBuilds++;
			var decal = profile.Decal;
			if (decal == null || !_decalTemplate) {
				return null;
			}

			var cloneStopwatch = Stopwatch.StartNew();
			var material = new Material(_decalTemplate) { name = profile.Name };
			cloneStopwatch.Stop();
			_diagnostics.MaterialCloneMilliseconds += cloneStopwatch.ElapsedMilliseconds;

			SetColor(material, "_BaseColor", decal.BaseColor.Color);
			SetTexture(material, "_BaseColorMap", decal.BaseColor.Texture, textures, imported);

			var normalStopwatch = Stopwatch.StartNew();
			SetNormalMap(material, "_NormalMap", decal.NormalMap, textures, imported);
			normalStopwatch.Stop();
			_diagnostics.NormalMapMilliseconds += normalStopwatch.ElapsedMilliseconds;
			if (decal.NormalMap != null) {
				SetFloat(material, "_NormalScale", decal.NormalMap.Strength);
			}

			// Decal MaskMap is pipeline-specific channel packing; only use side-channel data.
			SetTexture(material, "_MaskMap", decal.MaskMap, textures, importedMaterial: null);

			SetFloat(material, "_DecalBlend", decal.DecalBlend);
			SetFloat(material, "_NormalBlendSrc", decal.NormalBlendSrc);
			SetFloat(material, "_MaskBlendSrc", decal.MaskBlendSrc);
			SetFloat(material, "_DecalSmoothness", decal.Smoothness);
			SetFloat(material, "_DecalMetallic", decal.Metallic);
			SetFloat(material, "_DecalAO", decal.AmbientOcclusion);
			SetFloat(material, "_AffectAlbedo", decal.AffectAlbedo ? 1f : 0f);
			SetFloat(material, "_AffectNormal", decal.AffectNormal ? 1f : 0f);
			SetFloat(material, "_AffectMaskmap", decal.AffectMask ? 1f : 0f);

			ApplyDecalAffectKeyword(material, "_MATERIAL_AFFECTS_ALBEDO", decal.AffectAlbedo);
			ApplyDecalAffectKeyword(material, "_MATERIAL_AFFECTS_NORMAL", decal.AffectNormal);
			ApplyDecalAffectKeyword(material, "_MATERIAL_AFFECTS_MASKMAP", decal.AffectMask);

			material.enableInstancing = true;
			var validateStopwatch = Stopwatch.StartNew();
			HDMaterial.ValidateMaterial(material);
			validateStopwatch.Stop();
			_diagnostics.ValidateMilliseconds += validateStopwatch.ElapsedMilliseconds;
			return material;
		}

		private static void ApplyDecalAffectKeyword(Material material, string keyword, bool enabled)
		{
			if (enabled) {
				material.EnableKeyword(keyword);
			} else {
				material.DisableKeyword(keyword);
			}
		}

		private Material BuildLit(VpeMaterialProfile profile, IVpeTextureProvider textures, Material imported)
		{
			_diagnostics.LitBuilds++;
			var lit = profile.Lit;
			if (lit == null) {
				return null;
			}
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();

			if (_materialOverrides.TryGetValue(profile.Name, out var overrideTemplate) && overrideTemplate) {
				return new Material(overrideTemplate) { name = profile.Name };
			}

			var template = PickLitTemplate(lit, profile.Name);
			if (!template) {
				return null;
			}

			var cloneStopwatch = Stopwatch.StartNew();
			var material = new Material(template) { name = profile.Name };
			cloneStopwatch.Stop();
			_diagnostics.MaterialCloneMilliseconds += cloneStopwatch.ElapsedMilliseconds;
			LogApronImportedMaterial(profile.Name, imported);

			SetColor(material, "_BaseColor", lit.BaseColor.Color);
			SetColor(material, "_Color", lit.BaseColor.Color);
			SetTexture(material, "_BaseColorMap", lit.BaseColor.Texture, textures, imported);

			SetFloat(material, "_Metallic", lit.Metallic);
			SetFloat(material, "_Smoothness", lit.Smoothness);
			SetFloat(material, "_IridescenceMask", lit.IridescenceMask);
			SetFloat(material, "_IridescenceThickness", lit.IridescenceThickness);
			SetFloat(material, "_MetallicRemapMin", lit.MetallicRemap.x);
			SetFloat(material, "_MetallicRemapMax", lit.MetallicRemap.y);
			SetFloat(material, "_SmoothnessRemapMin", lit.SmoothnessRemap.x);
			SetFloat(material, "_SmoothnessRemapMax", lit.SmoothnessRemap.y);
			SetFloat(material, "_AORemapMin", lit.AoRemap.x);
			SetFloat(material, "_AORemapMax", lit.AoRemap.y);
			SetFloat(material, "_AlphaRemapMin", lit.AlphaRemap.x);
			SetFloat(material, "_AlphaRemapMax", lit.AlphaRemap.y);
			SetFloat(material, "_UVBase", lit.UvBase);
			SetFloat(material, "_TexWorldScale", hdrp.TexWorldScale);
			SetFloat(material, "_InvTilingScale", hdrp.InvTilingScale);
			SetFloat(material, "_EnableGeometricSpecularAA", hdrp.GeometricSpecularAa ? 1f : 0f);
			SetFloat(material, "_SpecularAAScreenSpaceVariance", hdrp.SpecularAaScreenSpaceVariance);
			SetFloat(material, "_SpecularAAThreshold", hdrp.SpecularAaThreshold);
			if (hdrp.GeometricSpecularAa) {
				material.EnableKeyword("_ENABLE_GEOMETRIC_SPECULAR_AA");
			} else {
				material.DisableKeyword("_ENABLE_GEOMETRIC_SPECULAR_AA");
			}
			SetFloat(material, "_SupportDecals", hdrp.SupportDecals ? 1f : 0f);
			if (hdrp.SupportDecals) {
				material.DisableKeyword("_DISABLE_DECALS");
			} else {
				material.EnableKeyword("_DISABLE_DECALS");
			}
			if (hdrp.RayTracing >= 0) {
				SetFloat(material, "_RayTracing", hdrp.RayTracing);
			}
			ApplyMaterialFeatureState(material, lit);

			// MaskMap always side-channel; never fall back to imported (channel packing differs).
			SetTexture(material, "_MaskMap", lit.MaskMap, textures, importedMaterial: null);
			var normalStopwatch = Stopwatch.StartNew();
			SetNormalMap(material, "_NormalMap", lit.NormalMap, textures, imported);
			normalStopwatch.Stop();
			_diagnostics.NormalMapMilliseconds += normalStopwatch.ElapsedMilliseconds;
			if (lit.NormalMap != null) {
				SetFloat(material, "_NormalScale", lit.NormalMap.Strength);
			}

			SetColor(material, "_EmissiveColor", lit.Emissive.Color);
			var emissiveLdrColor = lit.Emissive.HasLdrColor
				? (Color)lit.Emissive.LdrColor
				: ResolveLegacyEmissiveLdrColor(lit.Emissive);
			SetColor(material, "_EmissiveColorLDR", emissiveLdrColor);
			SetColor(material, "_EmissionColor", emissiveLdrColor);
			SetTexture(material, "_EmissiveColorMap", lit.Emissive.Texture, textures, imported);
			SetFloat(material, "_UseEmissiveIntensity", lit.Emissive.UseIntensity ? 1f : 0f);
			SetFloat(material, "_EmissiveIntensity", lit.Emissive.Intensity);
			SetFloat(material, "_EmissiveIntensityUnit", lit.Emissive.IntensityUnit == VpeEmissiveIntensityUnits.Ev100 ? 1f : 0f);
			SetFloat(material, "_EmissiveExposureWeight", hdrp.EmissiveExposureWeight);

			ApplySurfaceState(material, lit);
			ApplyDoubleSidedState(material, lit);
			ApplyTranslucencyState(material, lit, textures);
			ApplySsrTransparentState(material, lit);

			material.doubleSidedGI = lit.DoubleSidedGi;
			material.enableInstancing = true;

			if (hdrp.RenderQueueOverride >= 0) {
				material.renderQueue = hdrp.RenderQueueOverride;
			}

			// Let HDRP reconcile derived render state (passes, stencil refs, blend state, etc.)
			// from the final property/keyword set. Without this, alpha-test and transparent
			// materials can inherit stale template pass-state in player builds.
			var validateStopwatch = Stopwatch.StartNew();
			HDMaterial.ValidateMaterial(material);
			validateStopwatch.Stop();
			_diagnostics.ValidateMilliseconds += validateStopwatch.ElapsedMilliseconds;
			if (hdrp.RenderQueueOverride >= 0) {
				material.renderQueue = hdrp.RenderQueueOverride;
			}
			if (hdrp.RayTracing >= 0) {
				SetFloat(material, "_RayTracing", hdrp.RayTracing);
			}
			ApplyMaterialFeatureState(material, lit);
			var diffusionStopwatch = Stopwatch.StartNew();
			ApplyTransmissionDiffusionProfile(material, profile.Name, lit);
			diffusionStopwatch.Stop();
			_diagnostics.DiffusionMilliseconds += diffusionStopwatch.ElapsedMilliseconds;

			LogFirstOfEach(lit.SurfaceType, lit.RefractionModel, profile.Name, template.name, material);

			return material;
		}

		private static void ApplyTransmissionDiffusionProfile(Material material, string profileName, VpeLitProfile lit)
		{
			if (material == null || lit == null || !lit.HasTransmission) {
				return;
			}
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();

			if (Mathf.Abs(hdrp.DiffusionProfileHash) > 0.000001f) {
				SetFloat(material, "_DiffusionProfileHash", hdrp.DiffusionProfileHash);
				SetVector(material, "_DiffusionProfileAsset", hdrp.DiffusionProfileAsset);
				return;
			}

			if (material.HasProperty("_DiffusionProfileHash")
				&& Mathf.Abs(material.GetFloat("_DiffusionProfileHash")) > 0.000001f) {
				return;
			}

			DiffusionProfileSettings profile = null;
			foreach (var candidate in GetPreferredDiffusionProfileNames(profileName)) {
				profile = GetDiffusionProfileByName(candidate);
				if (profile) {
					break;
				}
			}

			if (!profile) {
				return;
			}

			var setAssetVector = SetDiffusionProfileHashOnly(material, profile);
			var key = $"{profileName ?? "<unnamed>"}|{profile.name}";
			if (VerboseResolverLogs && _loggedDiffusionAssignments.Add(key)) {
				Debug.Log(
					$"HdrpMaterialResolver: assigned diffusion profile '{profile.name}' to '{profileName}' " +
					$"(v1 payload does not serialize diffusion profile binding; hash-only runtime assignment, " +
					$"assetVector={(setAssetVector ? "set" : "missing")}).");
			}
		}

		private static bool SetDiffusionProfileHashOnly(Material material, DiffusionProfileSettings profile)
		{
			if (material == null || !material.HasProperty("_DiffusionProfileHash")) {
				return false;
			}

			var hash = GetDiffusionProfileHash(profile);
			material.SetFloat("_DiffusionProfileHash", hash);
			return TryApplyDiffusionProfileAssetVector(material, profile);
		}

		private static float GetDiffusionProfileHash(DiffusionProfileSettings profile)
		{
			if (!profile) {
				return 0f;
			}

			var id = profile.GetInstanceID();
			if (_diffusionProfileHashByInstance.TryGetValue(id, out var cached)) {
				return cached;
			}

			float hashAsFloat = 0f;
			try {
				if (_diffusionProfileField != null && _diffusionHashField != null) {
					var profileStruct = _diffusionProfileField.GetValue(profile);
					if (profileStruct != null) {
						var rawHash = _diffusionHashField.GetValue(profileStruct);
						uint hash = rawHash switch {
							uint u => u,
							int i => unchecked((uint)i),
							long l => unchecked((uint)l),
							_ => 0u,
						};
						hashAsFloat = BitConverter.ToSingle(BitConverter.GetBytes(hash), 0);
					}
				}
			} catch {
				hashAsFloat = 0f;
			}

			_diffusionProfileHashByInstance[id] = hashAsFloat;
			return hashAsFloat;
		}

		private static bool TryApplyDiffusionProfileAssetVector(Material material, DiffusionProfileSettings profile)
		{
			if (material == null
				|| profile == null
				|| !material.HasProperty("_DiffusionProfileAsset")) {
				return false;
			}

#if UNITY_EDITOR
			var assetPath = AssetDatabase.GetAssetPath(profile);
			if (string.IsNullOrWhiteSpace(assetPath)) {
				return false;
			}

			var guid = AssetDatabase.AssetPathToGUID(assetPath);
			if (string.IsNullOrWhiteSpace(guid) || guid.Length != 32) {
				return false;
			}

			material.SetVector("_DiffusionProfileAsset", ConvertGuidToVector4(guid));
			return true;
#else
			return false;
#endif
		}

		private static Vector4 ConvertGuidToVector4(string guid)
		{
			var bytes = new byte[16];
			for (var i = 0; i < 16; i++) {
				bytes[i] = byte.Parse(
					guid.Substring(i * 2, 2),
					System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture);
			}

			var floats = new float[4];
			Buffer.BlockCopy(bytes, 0, floats, 0, 16);
			return new Vector4(floats[0], floats[1], floats[2], floats[3]);
		}

		private static IEnumerable<string> GetPreferredDiffusionProfileNames(string profileName)
		{
			if (ContainsIgnoreCase(profileName, "plastic")) {
				yield return "Plastics";
				yield return "Translite";
				yield break;
			}

			if (ContainsIgnoreCase(profileName, "label")
				|| ContainsIgnoreCase(profileName, "decal")
				|| ContainsIgnoreCase(profileName, "translite")
				|| ContainsIgnoreCase(profileName, "hat")) {
				yield return "Translite";
				yield return "Plastics";
				yield break;
			}

			if (ContainsIgnoreCase(profileName, "insert")) {
				yield return "Plastics";
				yield return "Translite";
				yield break;
			}

			yield return "Translite";
			yield return "Plastics";
		}

		private static bool ContainsIgnoreCase(string value, string needle)
		{
			return !string.IsNullOrWhiteSpace(value)
				&& !string.IsNullOrWhiteSpace(needle)
				&& value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static DiffusionProfileSettings GetDiffusionProfileByName(string name)
		{
			if (string.IsNullOrWhiteSpace(name)) {
				return null;
			}

			if (_diffusionProfilesByName.TryGetValue(name, out var cached)) {
				return cached;
			}

			var allProfiles = UnityEngine.Resources.FindObjectsOfTypeAll<DiffusionProfileSettings>();
			for (var i = 0; i < allProfiles.Length; i++) {
				var profile = allProfiles[i];
				if (!profile || !string.Equals(profile.name, name, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				_diffusionProfilesByName[name] = profile;
				return profile;
			}

			_diffusionProfilesByName[name] = null;
			return null;
		}

		// Re-asserts the HDRP blend/depth/queue state we expect for the requested surface type.
		// The template carries these values, but being explicit here guards against template drift
		// and against future HDMaterial.ValidateMaterial calls that might overwrite silently.
		private static void ApplySurfaceState(Material material, VpeLitProfile lit)
		{
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();
			switch (lit.SurfaceType) {
				case VpeSurfaceTypes.Transparent:
					SetFloat(material, "_SurfaceType", 1f);
					SetFloat(material, "_BlendMode", VpeMaterialEnums.ToHdrpBlendMode(lit.BlendMode));
					SetFloat(material, "_TransparentSortPriority", lit.SortPriority);
					SetFloat(material, "_SrcBlend", 1f);        // One
					SetFloat(material, "_DstBlend", 10f);       // OneMinusSrcAlpha
					SetFloat(material, "_AlphaSrcBlend", 1f);
					SetFloat(material, "_AlphaDstBlend", 10f);
					SetFloat(material, "_ZWrite", 0f);
					SetFloat(material, "_TransparentZWrite", 0f);
					SetFloat(material, "_TransparentDepthPrepassEnable", hdrp.TransparentDepthPrepass ? 1f : 0f);
					SetFloat(material, "_TransparentDepthPostpassEnable", hdrp.TransparentDepthPostpass ? 1f : 0f);
					SetFloat(material, "_TransparentWritingMotionVec", hdrp.TransparentWritesMotionVectors ? 1f : 0f);
					SetFloat(material, "_TransparentBackfaceEnable", hdrp.TransparentBackface ? 1f : 0f);
					SetFloat(material, "_EnableFogOnTransparent", hdrp.EnableFogOnTransparent ? 1f : 0f);
					SetFloat(material, "_AlphaCutoffEnable", 0f);
					material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
					if (hdrp.EnableFogOnTransparent) {
						material.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
					} else {
						material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
					}
					if (hdrp.TransparentWritesMotionVectors) {
						material.EnableKeyword("_TRANSPARENT_WRITES_MOTION_VEC");
					} else {
						material.DisableKeyword("_TRANSPARENT_WRITES_MOTION_VEC");
					}
					material.DisableKeyword("_ALPHATEST_ON");
					SetShaderPassEnabledSafe(material, "MOTIONVECTORS", hdrp.TransparentWritesMotionVectors);
					SetShaderPassEnabledSafe(material, "MotionVectors", hdrp.TransparentWritesMotionVectors);
					SetShaderPassEnabledSafe(material, "TransparentDepthPrepass", hdrp.TransparentDepthPrepass);
					SetShaderPassEnabledSafe(material, "TransparentDepthPostpass", hdrp.TransparentDepthPostpass);
					if (material.renderQueue < 3000) {
						material.renderQueue = 3000;
					}
					break;

				case VpeSurfaceTypes.AlphaTest:
					SetFloat(material, "_SurfaceType", 0f);
					SetFloat(material, "_AlphaCutoffEnable", 1f);
					SetFloat(material, "_AlphaCutoff", lit.AlphaCutoff);
					SetFloat(material, "_Cutoff", lit.AlphaCutoff);
					material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
					material.EnableKeyword("_ALPHATEST_ON");
					SetShaderPassEnabledSafe(material, "MOTIONVECTORS", true);
					SetShaderPassEnabledSafe(material, "MotionVectors", true);
					if (material.renderQueue is < 2450 or > 2499) {
						material.renderQueue = 2450;
					}
					break;

				default: // Opaque
					SetFloat(material, "_SurfaceType", 0f);
					SetFloat(material, "_AlphaCutoffEnable", 0f);
					material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
					material.DisableKeyword("_ALPHATEST_ON");
					material.DisableKeyword("_TRANSPARENT_WRITES_MOTION_VEC");
					material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
					SetFloat(material, "_TransparentDepthPrepassEnable", 0f);
					SetFloat(material, "_TransparentDepthPostpassEnable", 0f);
					SetFloat(material, "_TransparentWritingMotionVec", 0f);
					SetFloat(material, "_TransparentBackfaceEnable", 0f);
					SetFloat(material, "_TransparentSortPriority", 0f);
					break;
			}
		}

		private static void SetShaderPassEnabledSafe(Material material, string passName, bool enabled)
		{
			// Unknown pass names are ignored by Unity; we still guard to avoid resolver hard-failures
			// if HDRP renames a pass across major versions.
			try {
				material.SetShaderPassEnabled(passName, enabled);
			} catch {
				// Intentionally ignore; pass name not available in this shader version.
			}
		}

		private static void ApplyMaterialFeatureState(Material material, VpeLitProfile lit)
		{
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();
			if (hdrp.MaterialId >= 0) {
				SetFloat(material, "_MaterialID", hdrp.MaterialId);
			}
			if (hdrp.TransmissionEnable >= 0f) {
				SetFloat(material, "_TransmissionEnable", hdrp.TransmissionEnable);
			}
			if (hdrp.TransmissionMask >= 0f) {
				SetFloat(material, "_TransmissionMask", hdrp.TransmissionMask);
			}

			if (lit.HasTransmission) {
				material.EnableKeyword("_MATERIAL_FEATURE_TRANSMISSION");
			} else {
				material.DisableKeyword("_MATERIAL_FEATURE_TRANSMISSION");
			}
		}

		private static void ApplySsrTransparentState(Material material, VpeLitProfile lit)
		{
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();
			if (hdrp.DisableSsrTransparent) {
				SetFloat(material, "_ReceivesSSRTransparent", 0f);
				material.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
			} else {
				SetFloat(material, "_ReceivesSSRTransparent", 1f);
				material.DisableKeyword("_DISABLE_SSR_TRANSPARENT");
			}
		}

		private static void ApplyDoubleSidedState(Material material, VpeLitProfile lit)
		{
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();
			if (lit.DoubleSided) {
				SetFloat(material, "_DoubleSidedEnable", 1f);
				SetFloat(material, "_CullMode", hdrp.CullMode >= 0 ? hdrp.CullMode : 0f);
				SetFloat(material, "_CullModeForward", hdrp.CullModeForward >= 0 ? hdrp.CullModeForward : 0f);
				SetFloat(material, "_OpaqueCullMode", hdrp.OpaqueCullMode >= 0 ? hdrp.OpaqueCullMode : 0f);
				SetFloat(material, "_TransparentCullMode", hdrp.TransparentCullMode >= 0 ? hdrp.TransparentCullMode : 0f);
				material.EnableKeyword("_DOUBLESIDED_ON");
				material.doubleSidedGI = true;
			} else {
				SetFloat(material, "_DoubleSidedEnable", 0f);
				if (hdrp.CullMode >= 0) {
					SetFloat(material, "_CullMode", hdrp.CullMode);
				}
				if (hdrp.CullModeForward >= 0) {
					SetFloat(material, "_CullModeForward", hdrp.CullModeForward);
				}
				if (hdrp.OpaqueCullMode >= 0) {
					SetFloat(material, "_OpaqueCullMode", hdrp.OpaqueCullMode);
				}
				if (hdrp.TransparentCullMode >= 0) {
					SetFloat(material, "_TransparentCullMode", hdrp.TransparentCullMode);
				}
				material.DisableKeyword("_DOUBLESIDED_ON");
			}
		}

		private void ApplyTranslucencyState(Material material, VpeLitProfile lit, IVpeTextureProvider textures)
		{
			var hdrp = lit.Hdrp ?? new VpeHdrpLitHints();
			// HDRP Translucent material archetype (MaterialID=5) combined with a refraction model.
			// VPE schema treats transmission/refraction as transparent-surface features; applying
			// them to alpha-test materials can push them into odd mixed variants.
			if (lit.SurfaceType != VpeSurfaceTypes.Transparent) {
				return;
			}

			// Start from a known non-translucent baseline so template defaults don't leak into
			// profiles that don't request transmission/refraction. Keep explicit material feature
			// scalars from the source material; transparent refraction is valid on Standard Lit
			// plastics and must not be promoted to Translucent unless transmission was requested.
			SetFloat(material, "_MaterialID", hdrp.MaterialId >= 0 ? hdrp.MaterialId : 1f);
			SetFloat(material, "_TransmissionEnable", hdrp.TransmissionEnable >= 0f ? hdrp.TransmissionEnable : 0f);
			SetFloat(material, "_TransmissionMask", hdrp.TransmissionMask >= 0f ? hdrp.TransmissionMask : 0f);
			material.DisableKeyword("_MATERIAL_FEATURE_TRANSMISSION");
			material.DisableKeyword("_THICKNESSMAP");
			material.DisableKeyword("_REFRACTION_PLANE");
			material.DisableKeyword("_REFRACTION_SPHERE");
			material.DisableKeyword("_REFRACTION_THIN");
			SetFloat(material, "_RefractionModel", 0f);

			if (!lit.HasTransmission && lit.RefractionModel == VpeRefractionModels.None) {
				return;
			}

			SetFloat(material, "_MaterialID", lit.HasTransmission && hdrp.MaterialId < 0 ? 5f : hdrp.MaterialId >= 0 ? hdrp.MaterialId : 1f);
			if (lit.HasTransmission) {
				SetFloat(material, "_TransmissionEnable", 1f);
				SetFloat(material, "_TransmissionMask", 1f);
				material.EnableKeyword("_MATERIAL_FEATURE_TRANSMISSION");
			}

			SetFloat(material, "_Thickness", lit.Thickness);
			SetVector(material, "_ThicknessRemap", new Vector4(lit.ThicknessRemap.x, lit.ThicknessRemap.y, 0f, 0f));
			SetFloat(material, "_ATDistance", lit.AbsorptionDistance);
			SetFloat(material, "_Ior", Mathf.Max(1f, lit.Ior));
			SetColor(material, "_TransmittanceColor", lit.TransmittanceColor);

			// Refraction keyword — exactly one must win. The template already pre-compiled one
			// variant; we flip keywords here to match the profile's intent, trusting that the
			// same three REFRACTION_* variants are present via our thin + planar templates.
			switch (lit.RefractionModel) {
				case VpeRefractionModels.Planar:
					material.EnableKeyword("_REFRACTION_PLANE");
					SetFloat(material, "_RefractionModel", 1f);
					break;
				case VpeRefractionModels.Sphere:
					material.EnableKeyword("_REFRACTION_SPHERE");
					SetFloat(material, "_RefractionModel", 2f);
					break;
				case VpeRefractionModels.Thin:
					material.EnableKeyword("_REFRACTION_THIN");
					SetFloat(material, "_RefractionModel", 3f);
					break;
				default:
					SetFloat(material, "_RefractionModel", 0f);
					break;
			}

			// Thickness map — HDRP packs thickness channels specifically; side-channel only.
			if (lit.ThicknessMap != null && !string.IsNullOrWhiteSpace(lit.ThicknessMap.TextureId)) {
				var tex = textures?.Get(lit.ThicknessMap.TextureId);
				if (tex && material.HasProperty("_ThicknessMap")) {
					material.SetTexture("_ThicknessMap", tex);
					material.SetTextureOffset("_ThicknessMap", lit.ThicknessMap.Offset);
					material.SetTextureScale("_ThicknessMap", lit.ThicknessMap.Scale);
					material.EnableKeyword("_THICKNESSMAP");
				}
			}
		}

		// Emits a single log per distinct (surfaceType, refractionModel, templateName) triple so we
		// can confirm the resolver picked the right template without spamming 149 lines per load.
		private static readonly HashSet<string> _loggedSurfaceTemplates = new();

		private static void LogFirstOfEach(string surfaceType, string refractionModel, string profileName, string templateName, Material material)
		{
			if (!VerboseResolverLogs) {
				return;
			}

			var key = $"{surfaceType}|{refractionModel}|{templateName}";
			if (!_loggedSurfaceTemplates.Add(key)) {
				return;
			}
			var baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : default;
			var surfType = material.HasProperty("_SurfaceType") ? material.GetFloat("_SurfaceType") : -1f;
			var matId = material.HasProperty("_MaterialID") ? material.GetFloat("_MaterialID") : -1f;
			var refMode = material.HasProperty("_RefractionModel") ? material.GetFloat("_RefractionModel") : -1f;
			var ior = material.HasProperty("_Ior") ? material.GetFloat("_Ior") : -1f;
			var thickness = material.HasProperty("_Thickness") ? material.GetFloat("_Thickness") : -1f;
			var tx = material.HasProperty("_TransmissionEnable") ? material.GetFloat("_TransmissionEnable") : -1f;
			var mvPass = material.GetShaderPassEnabled("MOTIONVECTORS") || material.GetShaderPassEnabled("MotionVectors");
			var prePass = material.GetShaderPassEnabled("TransparentDepthPrepass");
			var postPass = material.GetShaderPassEnabled("TransparentDepthPostpass");
			var kws = string.Join(",", material.shaderKeywords);
			Debug.Log(
				$"HdrpMaterialResolver first sample [{surfaceType}|{refractionModel}] '{profileName}' on template '{templateName}':\n" +
				$"  baseColor={baseColor} queue={material.renderQueue} shader={material.shader.name}\n" +
				$"  _SurfaceType={surfType} _MaterialID={matId} _RefractionModel={refMode} _Ior={ior} _Thickness={thickness} _TransmissionEnable={tx}\n" +
				$"  passState: MOTIONVECTORS={mvPass} TransparentDepthPrepass={prePass} TransparentDepthPostpass={postPass}\n" +
				$"  keywords=[{kws}]");
		}

		private Material PickLitTemplate(VpeLitProfile lit, string profileName)
		{
			switch (lit.SurfaceType) {
				case VpeSurfaceTypes.Transparent:
					// Translucent archetype (pinball inserts, plastic ramps): needs refraction +
					// transmission. Falls back through thin → planar → plain transparent so a
					// missing template never turns the material opaque-magenta.
					if (lit.HasTransmission) {
						var translucent = PickTranslucentTemplate(lit.RefractionModel);
						if (translucent) {
							return translucent;
						}
						Debug.LogWarning(
							$"HdrpMaterialResolver: '{profileName}' requests transmission/refraction ({lit.RefractionModel}) " +
							"but no translucent template is configured. Falling back to simple transparent.");
					}
					if (_litTransparentTemplate) {
						return _litTransparentTemplate;
					}
					Debug.LogWarning($"HdrpMaterialResolver: '{profileName}' transparent but no transparent template; using opaque.");
					return _litOpaqueTemplate;

				case VpeSurfaceTypes.AlphaTest:
					return _litOpaqueTemplate;

				default:
					if (!_litOpaqueTemplate) {
						Debug.LogWarning($"HdrpMaterialResolver: no lit-opaque template configured; profile '{profileName}' not applied.");
					}
					return _litOpaqueTemplate;
			}
		}

		private Material PickTranslucentTemplate(string refractionModel)
		{
			switch (refractionModel) {
				case VpeRefractionModels.Thin:
					return _litTranslucentThinTemplate ?? _litTranslucentSphereTemplate ?? _litTranslucentPlanarTemplate;
				case VpeRefractionModels.Planar:
					return _litTranslucentPlanarTemplate ?? _litTranslucentSphereTemplate ?? _litTranslucentThinTemplate;
				case VpeRefractionModels.Sphere:
					return _litTranslucentSphereTemplate ?? _litTranslucentPlanarTemplate ?? _litTranslucentThinTemplate;
				default:
					// HasTransmission without a named refraction model still wants the translucent
					// archetype; pick thin as the generic default.
					return _litTranslucentThinTemplate ?? _litTranslucentPlanarTemplate ?? _litTranslucentSphereTemplate;
			}
		}

		private static void SetFloat(Material material, string property, float value)
		{
			if (material.HasProperty(property)) {
				material.SetFloat(property, value);
			}
		}

		private static void SetColor(Material material, string property, Color value)
		{
			if (material.HasProperty(property)) {
				material.SetColor(property, value);
			}
		}

		private static Color ResolveLegacyEmissiveLdrColor(VpeEmissive emissive)
		{
			if (emissive == null) {
				return Color.black;
			}

			var hdr = (Color)emissive.Color;
			if (emissive.Intensity > 0.000001f && !Approximately(hdr, Color.black)) {
				return hdr / emissive.Intensity;
			}

			return hdr;
		}

		private static bool Approximately(Color a, Color b)
		{
			return Mathf.Approximately(a.r, b.r)
				&& Mathf.Approximately(a.g, b.g)
				&& Mathf.Approximately(a.b, b.b)
				&& Mathf.Approximately(a.a, b.a);
		}

		private static void SetVector(Material material, string property, Vector4 value)
		{
			if (material.HasProperty(property)) {
				material.SetVector(property, value);
			}
		}

		private static void SetTexture(Material material, string property, VpeTextureRef textureRef, IVpeTextureProvider textures, Material importedMaterial)
		{
			if (textureRef == null || !material.HasProperty(property)) {
				return;
			}

			Texture texture = null;
			var fromImported = false;
			if (!string.IsNullOrWhiteSpace(textureRef.TextureId)) {
				texture = textures?.Get(textureRef.TextureId);
			}
			if (!texture && importedMaterial) {
				texture = GetImportedTexture(importedMaterial, property);
				fromImported = texture;
			}
			if (!texture) {
				if (property == "_BaseColorMap" && importedMaterial) {
					LogMissingImportedBaseColorMap(importedMaterial, material.name);
				}
				return;
			}

			if (property == "_BaseColorMap" && material.name.IndexOf("Apron", System.StringComparison.OrdinalIgnoreCase) >= 0) {
				LogApronBaseAssignment(material.name, importedMaterial, texture, fromImported);
			}

			material.SetTexture(property, texture);
			material.SetTextureOffset(property, textureRef.Offset);
			material.SetTextureScale(property, textureRef.Scale);
		}

		private static void LogMissingImportedBaseColorMap(Material importedMaterial, string profileName)
		{
			var importedName = importedMaterial ? importedMaterial.name : "<null>";
			var key = $"{profileName}|{importedName}";
			if (!_loggedMissingBaseColorMap.Add(key)) {
				return;
			}

			var shaderName = importedMaterial && importedMaterial.shader ? importedMaterial.shader.name : "<null>";
			var hasMainTex = importedMaterial && importedMaterial.mainTexture;
			var props = importedMaterial ? importedMaterial.GetTexturePropertyNames() : System.Array.Empty<string>();
			var propDump = props != null && props.Length > 0 ? string.Join(", ", props) : "<none>";
			Logger.Warn(
				$"HdrpMaterialResolver: failed to resolve imported _BaseColorMap for profile '{profileName}' " +
				$"from source material '{importedName}' (shader='{shaderName}', mainTexture={(hasMainTex ? "yes" : "no")}, textureProps=[{propDump}]).");
		}

		private static void LogApronBaseAssignment(string profileName, Material importedMaterial, Texture assignedTexture, bool fromImported)
		{
			if (!VerboseResolverLogs) {
				return;
			}

			var importedName = importedMaterial ? importedMaterial.name : "<null>";
			var textureName = assignedTexture ? assignedTexture.name : "<null>";
			var key = $"{profileName}|{importedName}|{textureName}|{fromImported}";
			if (!_loggedApronBaseAssignments.Add(key)) {
				return;
			}

			var shaderName = importedMaterial && importedMaterial.shader ? importedMaterial.shader.name : "<null>";
			var sourceProps = System.Array.Empty<string>();
			if (fromImported && importedMaterial && assignedTexture) {
				var names = importedMaterial.GetTexturePropertyNames();
				var matched = new List<string>();
				for (var i = 0; i < names.Length; i++) {
					var prop = names[i];
					if (string.IsNullOrWhiteSpace(prop)) {
						continue;
					}
					var t = importedMaterial.GetTexture(prop);
					if (t == assignedTexture) {
						matched.Add(prop);
					}
				}
				sourceProps = matched.ToArray();
			}
			var src = sourceProps.Length > 0 ? string.Join(", ", sourceProps) : "<unknown>";
			Logger.Warn(
				$"HdrpMaterialResolver apron base assignment: profile='{profileName}', imported='{importedName}', " +
				$"shader='{shaderName}', texture='{textureName}', fromImported={fromImported}, sourceProps=[{src}]");
		}

		private static void LogApronImportedMaterial(string profileName, Material imported)
		{
			if (!VerboseResolverLogs) {
				return;
			}

			if (profileName == null || profileName.IndexOf("Apron", System.StringComparison.OrdinalIgnoreCase) < 0) {
				return;
			}
			if (!imported) {
				Logger.Warn($"HdrpMaterialResolver apron imported material is null for profile '{profileName}'.");
				return;
			}

			var key = $"{profileName}|{imported.name}";
			if (!_loggedApronImportedDump.Add(key)) {
				return;
			}

			var shader = imported.shader ? imported.shader.name : "<null>";
			var props = imported.GetTexturePropertyNames();
			var rows = new List<string>();
			for (var i = 0; i < props.Length; i++) {
				var p = props[i];
				if (string.IsNullOrWhiteSpace(p)) {
					continue;
				}
				var t = imported.GetTexture(p);
				rows.Add($"{p}={(t ? t.name : "<null>")}");
			}
			var dump = rows.Count > 0 ? string.Join(", ", rows) : "<none>";
			Logger.Warn(
				$"HdrpMaterialResolver apron imported material: profile='{profileName}', imported='{imported.name}', " +
				$"shader='{shader}', mainTexture={(imported.mainTexture ? imported.mainTexture.name : "<null>")}, textures=[{dump}]");
		}

		private void SetNormalMap(Material material, string property, VpeNormalMapRef textureRef, IVpeTextureProvider textures, Material importedMaterial)
		{
			if (textureRef == null || !material.HasProperty(property)) {
				return;
			}

			Texture texture = null;
			var fromImportedMaterial = false;
			if (!string.IsNullOrWhiteSpace(textureRef.TextureId)) {
				texture = textures?.Get(textureRef.TextureId);
			}
			if (!texture && importedMaterial) {
				texture = GetImportedTexture(importedMaterial, property);
				fromImportedMaterial = texture;
			}
			if (!texture) {
				return;
			}

			// HDRP's _NormalMap expects a Unity "Normal Map" imported texture (DXT5nm-like swizzle
			// path in UnpackNormalmapRGorAG). Textures arriving via the glb or the side-channel are
			// plain RGB PNGs (see VpeNormalPackings.Rgb) because runtime-loaded textures can't carry
			// Unity's import-time normal flag. Re-pack (A=R, G=G) so HDRP samples correctly.
			var src = texture as Texture2D;
			Texture repacked;
			if (!src) {
				repacked = texture;
			} else if (_repackedNormalCache.TryGetValue(src, out var cached)) {
				repacked = cached;
			} else {
				cached = fromImportedMaterial
					? RepackNormalMapForHdrpCpuFallback(src, runtimeCompress: textureRef.RuntimeCompress)
					: RepackNormalMapForHdrp(src, textureRef.Packing, textureRef.RuntimeCompress);
				_repackedNormalCache[src] = cached;
				repacked = cached;
			}
			material.SetTexture(property, repacked);
			material.SetTextureOffset(property, textureRef.Offset);
			material.SetTextureScale(property, textureRef.Scale);
		}

		// Maps v1 intent property names → a priority list of aliases likely to be present on a
		// glTFast-imported material. glTFast uses different names depending on its target pipeline
		// graph, and legacy Built-in materials may reuse yet other names.
		private static Texture GetImportedTexture(Material imported, string vpeProperty)
		{
			string[] aliases = vpeProperty switch {
				"_BaseColorMap" => new[] {
					"_BaseColorMap",
					"_BaseMap",
					"_MainTex",
					"baseColorTexture",
					"baseColorMap",
					"_BaseColorTexture",
					"_ColorMap",
					"_AlbedoMap",
					"_DiffuseMap",
					"_ColorTexture",
				},
				"_NormalMap" => new[] {
					"_NormalMap",
					"_BumpMap",
					"normalTexture",
					"_NormalTexture",
				},
				"_EmissiveColorMap" => new[] {
					"_EmissiveColorMap",
					"_EmissionMap",
					"_EmissiveMap",
					"emissiveTexture",
					"emissionTexture",
					"_EmissionColorMap",
				},
				_ => System.Array.Empty<string>(),
			};
			foreach (var alias in aliases) {
				if (!imported.HasProperty(alias)) {
					continue;
				}
				var t = imported.GetTexture(alias);
				if (t) {
					return t;
				}
			}

			// Heuristic fallback for importer/pipeline-specific property names we don't explicitly
			// know. This keeps opaque materials (like the apron base) from turning white if the glTF
			// importer renamed the base map property.
			string[] includeNeedles = vpeProperty switch {
				"_BaseColorMap" => new[] { "base", "albedo", "diffuse", "color", "main" },
				"_NormalMap" => new[] { "normal", "bump" },
				"_EmissiveColorMap" => new[] { "emiss", "emission" },
				_ => null,
			};
			if (includeNeedles != null && includeNeedles.Length > 0) {
				var excludeNeedles = vpeProperty == "_BaseColorMap"
					? new[] { "normal", "bump", "mask", "metal", "rough", "smooth", "ao", "occlusion", "height", "parallax", "emiss", "emission" }
					: System.Array.Empty<string>();
				var propNames = imported.GetTexturePropertyNames();
				for (var i = 0; i < propNames.Length; i++) {
					var prop = propNames[i];
					if (string.IsNullOrWhiteSpace(prop)) {
						continue;
					}
					var lowered = prop.ToLowerInvariant();
					var include = false;
					for (var n = 0; n < includeNeedles.Length; n++) {
						if (lowered.Contains(includeNeedles[n])) {
							include = true;
							break;
						}
					}
					if (!include) {
						continue;
					}
					var excluded = false;
					for (var n = 0; n < excludeNeedles.Length; n++) {
						if (lowered.Contains(excludeNeedles[n])) {
							excluded = true;
							break;
						}
					}
					if (excluded) {
						continue;
					}
					var t = imported.GetTexture(prop);
					if (t) {
						return t;
					}
				}
			}

			// Last-resort safety net for unknown shader graphs: use mainTexture only. Avoid selecting
			// arbitrary texture properties here (mask/ORM maps can make surfaces appear white).
			if (vpeProperty == "_BaseColorMap") {
				var main = GetMainTextureSafe(imported);
				if (main) {
					return main;
				}
			}
			return null;
		}

		private static Texture GetMainTextureSafe(Material material)
		{
			if (!material) {
				return null;
			}

			try {
				return material.mainTexture;
			} catch {
				return null;
			}
		}

		private static Texture RepackNormalMapForHdrp(Texture2D source, string packing, bool runtimeCompress)
		{
			// Only need to re-pack plain-RGB normals. If the source was already dxt5nm/rg, leave it.
			if (packing != VpeNormalPackings.Rgb || !source) {
				return source;
			}

			// Temporary safety valve while we verify that the GPU repack path preserves
			// the same relief response as Unity's CPU-side runtime repack.
			return RepackNormalMapForHdrpCpuFallback(source, runtimeCompress: runtimeCompress);

			var repackMaterial = GetOrCreateNormalRepackMaterial();
			if (!repackMaterial) {
				return RepackNormalMapForHdrpCpuFallback(source, runtimeCompress: runtimeCompress);
			}

			var repacked = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) {
				name = $"{source.name} (NormalRepack)",
				wrapMode = source.wrapMode,
				filterMode = source.filterMode,
				anisoLevel = Mathf.Max(1, source.anisoLevel),
				useMipMap = true,
				autoGenerateMips = false,
				hideFlags = HideFlags.HideAndDontSave,
			};
			repacked.Create();
			try {
				repackMaterial.SetTexture(MainTextureProperty, source);
				Graphics.Blit(source, repacked, repackMaterial, 0);
				repacked.GenerateMips();
				return repacked;
			} catch (Exception e) {
				DestroyRuntimeObject(repacked);
				if (!_loggedNormalRepackGpuFallback) {
					_loggedNormalRepackGpuFallback = true;
					Logger.Warn(e, "HdrpMaterialResolver: GPU normal repack failed. Falling back to CPU normal repack.");
				}
				return RepackNormalMapForHdrpCpuFallback(source, runtimeCompress: runtimeCompress);
			}
		}

		private void PruneRepackedNormalCache()
		{
			if (_repackedNormalCache.Count == 0) {
				return;
			}

			var staleKeys = new List<Texture2D>();
			foreach (var pair in _repackedNormalCache) {
				if (pair.Key) {
					continue;
				}

				DestroyRuntimeObject(pair.Value);
				staleKeys.Add(pair.Key);
			}

			for (var i = 0; i < staleKeys.Count; i++) {
				_repackedNormalCache.Remove(staleKeys[i]);
			}
		}

		private static Material GetOrCreateNormalRepackMaterial()
		{
			if (_normalRepackMaterial) {
				return _normalRepackMaterial;
			}
			if (_normalRepackMaterialQueried) {
				return null;
			}

			_normalRepackMaterialQueried = true;
			var shader = UnityEngine.Resources.Load<Shader>(NormalRepackShaderResourcePath)
				?? Shader.Find("Hidden/VPE/PackNormalForHdrp");
			if (!shader) {
				if (!_loggedMissingNormalRepackShader) {
					_loggedMissingNormalRepackShader = true;
					Logger.Warn($"HdrpMaterialResolver: failed to load Resources/{NormalRepackShaderResourcePath}. Falling back to CPU normal repack.");
				}
				return null;
			}

			_normalRepackMaterial = new Material(shader) {
				name = "VPE HDRP Normal Repack",
				hideFlags = HideFlags.HideAndDontSave,
			};
			return _normalRepackMaterial;
		}

		private static Texture2D RepackNormalMapForHdrpCpuFallback(
			Texture2D source,
			bool flipRed = false,
			bool flipGreen = false,
			bool runtimeCompress = true)
		{
			var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
			var previous = RenderTexture.active;
			try {
				Graphics.Blit(source, rt);
				RenderTexture.active = rt;
				var repacked = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true, true) {
					name = $"{source.name} (NormalRepack)",
					wrapMode = source.wrapMode,
					filterMode = source.filterMode,
					anisoLevel = Mathf.Max(1, source.anisoLevel),
					hideFlags = HideFlags.HideAndDontSave,
				};
				var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
				pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
				pixels.Apply(false, false);
				var raw = pixels.GetPixels32();
				for (var i = 0; i < raw.Length; i++) {
					var p = raw[i];
					var x = flipRed ? (byte)(255 - p.r) : p.r;
					var y = flipGreen ? (byte)(255 - p.g) : p.g;
					// DXT5nm-style payload for Unity/HDRP unpack: X lives in A, Y in G, and R must be 1
					// so UnpackNormalmapRGorAG's "packednormal.x *= packednormal.w" reconstructs X.
					raw[i] = new Color32(255, y, 255, x);
				}
				repacked.SetPixels32(raw);
				repacked.Apply(true, false);
				if (runtimeCompress) {
					try {
						repacked.Compress(highQuality: true);
					} catch (Exception e) {
						Logger.Warn(e, $"HdrpMaterialResolver: failed compressing normal map '{repacked.name}'. Keeping uncompressed texture.");
					}
				}
				repacked.Apply(true, true);
				DestroyRuntimeObject(pixels);
				return repacked;
			} finally {
				RenderTexture.active = previous;
				RenderTexture.ReleaseTemporary(rt);
			}
		}

		private static void DestroyRuntimeObject(UnityEngine.Object obj)
		{
			if (!obj) {
				return;
			}

			if (Application.isPlaying) {
				UnityEngine.Object.Destroy(obj);
			} else {
				UnityEngine.Object.DestroyImmediate(obj);
			}
		}

		private sealed class ResolverDiagnostics
		{
			public int CreateCalls;
			public int LitBuilds;
			public int DecalBuilds;
			public long MaterialCloneMilliseconds;
			public long ValidateMilliseconds;
			public long NormalMapMilliseconds;
			public long DiffusionMilliseconds;

			public void Reset()
			{
				CreateCalls = 0;
				LitBuilds = 0;
				DecalBuilds = 0;
				MaterialCloneMilliseconds = 0;
				ValidateMilliseconds = 0;
				NormalMapMilliseconds = 0;
				DiffusionMilliseconds = 0;
			}

			public string ToSummaryString()
			{
				var builder = new StringBuilder(128);
				builder.Append("createCalls=").Append(CreateCalls)
					.Append(", lit=").Append(LitBuilds)
					.Append(", decal=").Append(DecalBuilds)
					.Append(", cloneMs=").Append(MaterialCloneMilliseconds)
					.Append(", validateMs=").Append(ValidateMilliseconds)
					.Append(", normalMs=").Append(NormalMapMilliseconds)
					.Append(", diffusionMs=").Append(DiffusionMilliseconds);
				return builder.ToString();
			}
		}
	}
}
