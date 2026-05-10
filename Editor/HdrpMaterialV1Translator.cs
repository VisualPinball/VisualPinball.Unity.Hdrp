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
using System.Linq;
using NLog;
using UnityEditor;
using UnityEngine;
using VisualPinball.Unity;
using VisualPinball.Unity.Editor;
using Logger = NLog.Logger;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	// Editor-only. Translates Unity Materials on a scene's renderers into a portable
	// VpeMaterialsPayloadV1 plus a set of PNG texture blobs keyed by stable ids.
	//
	// Only HDRP-aware mappings are implemented here; if VPE adopts additional pipelines the
	// translator fans out on shader name.
	internal static class HdrpMaterialV1Translator
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		private const string HdrpLitShaderName = "HDRP/Lit";
		private const string HdrpDecalShaderName = "HDRP/Decal";
		private const string HdrpUnlitShaderName = "HDRP/Unlit";
		private const string VpeMetalShaderGraphPathSuffix = "/Assets/Art/Graphs/Metal.shadergraph";
		private const string VpeRubberShaderGraphPathSuffix = "/Assets/Art/Graphs/Rubber.shadergraph";

		public static VpeMaterialCaptureResult Capture(Transform tableRoot, IEnumerable<Renderer> renderers)
		{
			var profiles = new Dictionary<string, VpeMaterialProfileV1>(StringComparer.Ordinal);
			var rendererStates = new List<VpeRendererStateV1>();
			var ctx = new CaptureContext();

			if (renderers != null) {
				foreach (var renderer in renderers) {
					if (!renderer) {
						continue;
					}

					if (tableRoot) {
						rendererStates.Add(CaptureRendererState(renderer, tableRoot));
					}

					foreach (var material in renderer.sharedMaterials) {
						if (!material) {
							continue;
						}
						var key = NormalizeMaterialName(material.name);
						if (string.IsNullOrWhiteSpace(key) || profiles.ContainsKey(key)) {
							continue;
						}

						var profile = TranslateMaterial(material, ctx);
						if (profile != null) {
							profile.Name = key;
							profiles[key] = profile;
						}
					}
				}
			}

			var payload = new VpeMaterialsPayloadV1 {
				FormatVersion = 1,
				WrittenBy = "HdrpMaterialV1Translator",
				Profiles = profiles.Values.ToArray(),
				Textures = ctx.BuildTextureAssets(),
				RendererStates = rendererStates.ToArray(),
			};
			ctx.LogUnsupportedMaterialsSummary();
			return new VpeMaterialCaptureResult(payload, ctx.TextureBlobs);
		}

		public static IDisposable PrepareGltfExport(IEnumerable<Renderer> renderers)
		{
			return new GltfExportMaterialScope(renderers);
		}

		private static VpeRendererStateV1 CaptureRendererState(Renderer renderer, Transform tableRoot)
		{
			return new VpeRendererStateV1 {
				Path = renderer.transform.GetPath(tableRoot),
				ShadowCastingMode = (int)renderer.shadowCastingMode,
				ReceiveShadows = renderer.receiveShadows,
				RenderingLayerMask = renderer.renderingLayerMask,
				RayTracingMode = (int)renderer.rayTracingMode,
			};
		}

		private static VpeMaterialProfileV1 TranslateMaterial(Material material, CaptureContext ctx)
		{
			if (!material || !material.shader) {
				return null;
			}

			if (IsVpeMetalMaterial(material)) {
				return TranslateVpeMetalShaderGraph(material, ctx);
			}
			if (IsVpeRubberMaterial(material)) {
				return TranslateVpeRubberShaderGraph(material, ctx);
			}

			var shaderName = material.shader.name;
			switch (shaderName) {
				case HdrpLitShaderName:
					return TranslateHdrpLit(material, ctx);
				case HdrpDecalShaderName:
					return TranslateHdrpDecal(material, ctx);
				case HdrpUnlitShaderName:
					return TranslateHdrpUnlit(material, ctx);
				default:
					ctx.RegisterUnsupportedMaterial(shaderName, material.name);
					return null;
			}
		}

		private static VpeMaterialProfileV1 TranslateHdrpLit(Material material, CaptureContext ctx)
		{
			// Keep opaque lit materials on the standard glTF path to avoid duplicating the largest
			// texture set in the package. For alpha-tested and transparent lit materials, the base
			// color alpha is load-bearing and must round-trip losslessly for inserts/plastics.
			var baseColorNeedsAlpha =
				SafeGetFloat(material, "_SurfaceType", 0f) > 0.5f /* Transparent */
				|| SafeGetFloat(material, "_AlphaCutoffEnable", 0f) > 0.5f /* AlphaTest */;
			var baseColorTexture = baseColorNeedsAlpha
				? ctx.CaptureSideChannelTextureRef(material, "_BaseColorMap", VpeColorSpaces.SRgb)
				: ctx.CaptureImportedTextureRef(material, "_BaseColorMap");
			var baseColor = ResolveHdrpBaseColor(material);

			var lit = new VpeLitProfileV1 {
				BaseColor = {
					Color = baseColor,
					Texture = baseColorTexture,
				},
				Metallic = SafeGetFloat(material, "_Metallic", 0f),
				Smoothness = SafeGetFloat(material, "_Smoothness", 0.5f),
				OcclusionStrength = 1f,
				// MaskMap packs HDRP-specific channels (R=metal, G=AO, B=detail, A=smooth). glTF
				// has no lossless equivalent, so this is the one texture that gets side-channeled.
				MaskMap = ctx.CaptureSideChannelTextureRef(material, "_MaskMap", VpeColorSpaces.Linear),
				MaskPacking = VpeMaskPackings.HdrpMaskMap,
				MetallicRemap = new Vector2(
					SafeGetFloat(material, "_MetallicRemapMin", 0f),
					SafeGetFloat(material, "_MetallicRemapMax", 1f)),
				SmoothnessRemap = new Vector2(
					SafeGetFloat(material, "_SmoothnessRemapMin", 0f),
					SafeGetFloat(material, "_SmoothnessRemapMax", 1f)),
				AoRemap = new Vector2(
					SafeGetFloat(material, "_AORemapMin", 0f),
					SafeGetFloat(material, "_AORemapMax", 1f)),
				AlphaRemap = new Vector2(
					SafeGetFloat(material, "_AlphaRemapMin", 0f),
					SafeGetFloat(material, "_AlphaRemapMax", 1f)),
				UvBase = Mathf.RoundToInt(SafeGetFloat(material, "_UVBase", 0f)),
				TexWorldScale = SafeGetFloat(material, "_TexWorldScale", 1f),
				InvTilingScale = SafeGetFloat(material, "_InvTilingScale", 1f),
				NormalMap = ctx.CaptureImportedNormalMapRef(material, "_NormalMap",
					strength: SafeGetFloat(material, "_NormalScale", 1f)),
				Emissive = new VpeEmissiveV1 {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureImportedTextureRef(material, "_EmissiveColorMap"),
					UseIntensity = SafeGetFloat(material, "_UseEmissiveIntensity", 0f) > 0.5f,
					Intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f),
					IntensityUnit = HdrpEmissiveIntensityUnitToString(
						SafeGetFloat(material, "_EmissiveIntensityUnit", 0f)),
					ExposureWeight = SafeGetFloat(material, "_EmissiveExposureWeight", 1f),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
				DoubleSidedGi = material.doubleSidedGI,
				CullMode = Mathf.RoundToInt(SafeGetFloat(material, "_CullMode", -1f)),
				CullModeForward = Mathf.RoundToInt(SafeGetFloat(material, "_CullModeForward", -1f)),
				OpaqueCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_OpaqueCullMode", -1f)),
				TransparentCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentCullMode", -1f)),
				TransparentBlendMode = Mathf.RoundToInt(SafeGetFloat(material, "_BlendMode", 0f)),
				TransparentSortPriority = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentSortPriority", 0f)),
				EnableFogOnTransparent = SafeGetFloat(material, "_EnableFogOnTransparent", 1f) > 0.5f
					|| material.IsKeywordEnabled("_ENABLE_FOG_ON_TRANSPARENT"),
				TransparentDepthPrepass = SafeGetFloat(material, "_TransparentDepthPrepassEnable", 0f) > 0.5f,
				TransparentDepthPostpass = SafeGetFloat(material, "_TransparentDepthPostpassEnable", 0f) > 0.5f,
				TransparentWritesMotionVectors = (SafeGetFloat(material, "_TransparentWritingMotionVec", 0f) > 0.5f
						|| material.IsKeywordEnabled("_TRANSPARENT_WRITES_MOTION_VEC"))
					&& (material.GetShaderPassEnabled("MOTIONVECTORS") || material.GetShaderPassEnabled("MotionVectors")),
				TransparentBackface = SafeGetFloat(material, "_TransparentBackfaceEnable", 0f) > 0.5f,
				DisableSsrTransparent = material.IsKeywordEnabled("_DISABLE_SSR_TRANSPARENT")
					|| SafeGetFloat(material, "_ReceivesSSRTransparent", 0f) < 0.5f,
				DisableSsr = material.IsKeywordEnabled("_DISABLE_SSR")
					|| SafeGetFloat(material, "_ReceivesSSR", 1f) < 0.5f,
				RayTracing = Mathf.RoundToInt(SafeGetFloat(material, "_RayTracing", -1f)),
				MaterialId = Mathf.RoundToInt(SafeGetFloat(material, "_MaterialID", -1f)),
				RenderQueueOverride = material.renderQueue,

				RefractionModel = HdrpRefractionModelToString(
					SafeGetFloat(material, "_RefractionModel", 0f),
					material),
				Ior = SafeGetFloat(material, "_Ior", 1f),
				// Authoring intent is encoded by the explicit HDRP translucent signals:
				// MaterialID==5 or the transmission keyword. Do not infer from _TransmissionEnable;
				// HDRP keeps that float at 1 on many non-translucent materials.
				HasTransmission = material.IsKeywordEnabled("_MATERIAL_FEATURE_TRANSMISSION")
					|| Mathf.Approximately(SafeGetFloat(material, "_MaterialID", 1f), 5f),
				TransmissionEnable = SafeGetFloat(material, "_TransmissionEnable", -1f),
				TransmissionMask = SafeGetFloat(material, "_TransmissionMask", -1f),
				DiffusionProfileHash = SafeGetFloat(material, "_DiffusionProfileHash", 0f),
				DiffusionProfileAsset = SafeGetVector(material, "_DiffusionProfileAsset", Vector4.zero),
				Thickness = SafeGetFloat(material, "_Thickness", 1f),
				ThicknessRemap = SafeGetVector2(material, "_ThicknessRemap", new Vector2(0f, 1f)),
				AbsorptionDistance = SafeGetFloat(material, "_ATDistance", 1f),
				TransmittanceColor = SafeGetColor(material, "_TransmittanceColor", Color.white),
				ThicknessMap = ctx.CaptureSideChannelTextureRef(material, "_ThicknessMap", VpeColorSpaces.Linear),
			};

			return new VpeMaterialProfileV1 {
				Type = VpeMaterialTypes.Lit,
				Lit = lit,
			};
		}

		private static VpeMaterialProfileV1 TranslateVpeMetalShaderGraph(Material material, CaptureContext ctx)
		{
			var source = ResolveSourceMaterialAsset(material);
			var maskMap = CaptureShaderGraphTextureRef(ctx, material, source, "Texture2D_1E4703A", VpeColorSpaces.Linear);
			if (maskMap != null) {
				maskMap.Scale = SafeGetVector2(material, "Vector2_3344450D", SafeGetVector2(source, "Vector2_3344450D", maskMap.Scale));
				maskMap.Offset = SafeGetVector2(material, "Vector2_7CFC7CEF", SafeGetVector2(source, "Vector2_7CFC7CEF", maskMap.Offset));
			}

			var metallicIntensity = SafeGetFloat(material, "Vector1_BD53BAF8", 1f);
			var occlusionIntensity = Mathf.Clamp01(SafeGetFloat(material, "Vector1_7EB7D62C", 1f));
			var lit = new VpeLitProfileV1 {
				BaseColor = {
					Color = SafeGetColor(material, "Color_AD8F67CE", ResolveHdrpBaseColor(material)),
				},
				Metallic = metallicIntensity,
				Smoothness = SafeGetFloat(material, "_Smoothness", 0.5f),
				OcclusionStrength = occlusionIntensity,
				MaskMap = maskMap,
				MaskPacking = VpeMaskPackings.HdrpMaskMap,
				MetallicRemap = new Vector2(0f, metallicIntensity),
				SmoothnessRemap = new Vector2(
					SafeGetFloat(material, "Vector1_E8712278", 0f),
					SafeGetFloat(material, "Vector1_2E810D38", 1f)),
				AoRemap = new Vector2(1f - occlusionIntensity, 1f),
				AlphaRemap = new Vector2(
					SafeGetFloat(material, "_AlphaRemapMin", 0f),
					SafeGetFloat(material, "_AlphaRemapMax", 1f)),
				UvBase = 0,
				TexWorldScale = SafeGetFloat(material, "_TexWorldScale", 1f),
				InvTilingScale = SafeGetFloat(material, "_InvTilingScale", 1f),
				Emissive = new VpeEmissiveV1 {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureImportedTextureRef(material, "_EmissiveColorMap"),
					UseIntensity = SafeGetFloat(material, "_UseEmissiveIntensity", 0f) > 0.5f,
					Intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f),
					IntensityUnit = HdrpEmissiveIntensityUnitToString(
						SafeGetFloat(material, "_EmissiveIntensityUnit", 0f)),
					ExposureWeight = SafeGetFloat(material, "_EmissiveExposureWeight", 1f),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
				DoubleSidedGi = material.doubleSidedGI,
				CullMode = Mathf.RoundToInt(SafeGetFloat(material, "_CullMode", -1f)),
				CullModeForward = Mathf.RoundToInt(SafeGetFloat(material, "_CullModeForward", -1f)),
				OpaqueCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_OpaqueCullMode", -1f)),
				TransparentCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentCullMode", -1f)),
				DisableSsr = material.IsKeywordEnabled("_DISABLE_SSR")
					|| SafeGetFloat(material, "_ReceivesSSR", 1f) < 0.5f,
				RayTracing = Mathf.RoundToInt(SafeGetFloat(material, "_RayTracing", -1f)),
				MaterialId = Mathf.RoundToInt(SafeGetFloat(material, "_MaterialID", -1f)),
				HasTransmission = material.IsKeywordEnabled("_MATERIAL_FEATURE_TRANSMISSION")
					|| Mathf.Approximately(SafeGetFloat(material, "_MaterialID", 1f), 5f),
				TransmissionEnable = SafeGetFloat(material, "_TransmissionEnable", -1f),
				TransmissionMask = SafeGetFloat(material, "_TransmissionMask", -1f),
				RenderQueueOverride = material.renderQueue,
			};

			return new VpeMaterialProfileV1 {
				Type = VpeMaterialTypes.Lit,
				Lit = lit,
			};
		}

		private static VpeMaterialProfileV1 TranslateVpeRubberShaderGraph(Material material, CaptureContext ctx)
		{
			var source = ResolveSourceMaterialAsset(material);
			var baseColorMap = CaptureShaderGraphTextureRef(ctx, material, source, "Texture2D_B9CEC4F9", VpeColorSpaces.SRgb);
			var normalMap = CaptureShaderGraphNormalMapRef(ctx, material, source, "Texture2D_524261BE",
				SafeGetFloat(material, "Vector1_29D01071", SafeGetFloat(source, "Vector1_29D01071", 1f)));
			var maskMap = CaptureShaderGraphTextureRef(ctx, material, source, "Texture2D_1E4703A", VpeColorSpaces.Linear);

			var scale = SafeGetVector2(material, "Vector2_3344450D", SafeGetVector2(source, "Vector2_3344450D", Vector2.one));
			var offset = SafeGetVector2(material, "Vector2_7CFC7CEF", SafeGetVector2(source, "Vector2_7CFC7CEF", Vector2.zero));
			ApplyTextureTransform(baseColorMap, scale, offset);
			ApplyTextureTransform(normalMap, scale, offset);
			ApplyTextureTransform(maskMap, scale, offset);

			var occlusionIntensity = Mathf.Clamp01(SafeGetFloat(material, "Vector1_7EB7D62C", SafeGetFloat(source, "Vector1_7EB7D62C", 1f)));
			var lit = new VpeLitProfileV1 {
				BaseColor = {
					Color = SafeGetColor(material, "Color_9A170B2D", SafeGetColor(source, "Color_9A170B2D", ResolveHdrpBaseColor(material))),
					Texture = baseColorMap,
				},
				Metallic = SafeGetFloat(material, "_Metallic", SafeGetFloat(source, "_Metallic", 0f)),
				Smoothness = SafeGetFloat(material, "_Smoothness", SafeGetFloat(source, "_Smoothness", 0.5f)),
				OcclusionStrength = occlusionIntensity,
				MaskMap = maskMap,
				MaskPacking = VpeMaskPackings.HdrpMaskMap,
				MetallicRemap = new Vector2(0f, SafeGetFloat(material, "_Metallic", SafeGetFloat(source, "_Metallic", 0f))),
				SmoothnessRemap = new Vector2(
					SafeGetFloat(material, "Vector1_E8712278", SafeGetFloat(source, "Vector1_E8712278", 0f)),
					SafeGetFloat(material, "Vector1_2E810D38", SafeGetFloat(source, "Vector1_2E810D38", 1f))),
				AoRemap = new Vector2(1f - occlusionIntensity, 1f),
				AlphaRemap = new Vector2(
					SafeGetFloat(material, "_AlphaRemapMin", SafeGetFloat(source, "_AlphaRemapMin", 0f)),
					SafeGetFloat(material, "_AlphaRemapMax", SafeGetFloat(source, "_AlphaRemapMax", 1f))),
				UvBase = 0,
				TexWorldScale = SafeGetFloat(material, "_TexWorldScale", SafeGetFloat(source, "_TexWorldScale", 1f)),
				InvTilingScale = SafeGetFloat(material, "_InvTilingScale", SafeGetFloat(source, "_InvTilingScale", 1f)),
				NormalMap = normalMap,
				Emissive = new VpeEmissiveV1 {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureImportedTextureRef(material, "_EmissiveColorMap"),
					UseIntensity = SafeGetFloat(material, "_UseEmissiveIntensity", 0f) > 0.5f,
					Intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f),
					IntensityUnit = HdrpEmissiveIntensityUnitToString(
						SafeGetFloat(material, "_EmissiveIntensityUnit", 0f)),
					ExposureWeight = SafeGetFloat(material, "_EmissiveExposureWeight", 1f),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
				DoubleSidedGi = material.doubleSidedGI,
				CullMode = Mathf.RoundToInt(SafeGetFloat(material, "_CullMode", -1f)),
				CullModeForward = Mathf.RoundToInt(SafeGetFloat(material, "_CullModeForward", -1f)),
				OpaqueCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_OpaqueCullMode", -1f)),
				TransparentCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentCullMode", -1f)),
				DisableSsr = material.IsKeywordEnabled("_DISABLE_SSR")
					|| SafeGetFloat(material, "_ReceivesSSR", 1f) < 0.5f,
				RayTracing = Mathf.RoundToInt(SafeGetFloat(material, "_RayTracing", -1f)),
				MaterialId = Mathf.RoundToInt(SafeGetFloat(material, "_MaterialID", -1f)),
				RenderQueueOverride = material.renderQueue,
			};

			return new VpeMaterialProfileV1 {
				Type = VpeMaterialTypes.Lit,
				Lit = lit,
			};
		}

		private static VpeMaterialProfileV1 TranslateHdrpDecal(Material material, CaptureContext ctx)
		{
			var decal = new VpeDecalProfileV1 {
				BaseColor = {
					Color = SafeGetColor(material, "_BaseColor", Color.white),
					// Decal albedo alpha is load-bearing (where the decal applies). Exporting through
					// glTF can convert this map to JPEG and drop alpha, so always side-channel it.
					Texture = ctx.CaptureSideChannelTextureRef(material, "_BaseColorMap", VpeColorSpaces.SRgb),
				},
				NormalMap = ctx.CaptureImportedNormalMapRef(material, "_NormalMap",
					strength: SafeGetFloat(material, "_NormalScale", 1f)),
				MaskMap = ctx.CaptureSideChannelTextureRef(material, "_MaskMap", VpeColorSpaces.Linear),
				MaskPacking = VpeMaskPackings.HdrpMaskMap,
				AffectAlbedo = material.IsKeywordEnabled("_MATERIAL_AFFECTS_ALBEDO")
					|| SafeGetFloat(material, "_AffectAlbedo", 1f) > 0.5f,
				AffectNormal = material.IsKeywordEnabled("_MATERIAL_AFFECTS_NORMAL")
					|| SafeGetFloat(material, "_AffectNormal", 1f) > 0.5f,
				AffectMask = material.IsKeywordEnabled("_MATERIAL_AFFECTS_MASKMAP")
					|| SafeGetFloat(material, "_AffectMaskmap", 0f) > 0.5f,
				DecalBlend = SafeGetFloat(material, "_DecalBlend", 1f),
				NormalBlendSrc = SafeGetFloat(material, "_NormalBlendSrc", 1f),
				MaskBlendSrc = SafeGetFloat(material, "_MaskBlendSrc", 1f),
				Smoothness = SafeGetFloat(material, "_DecalSmoothness", 0.5f),
				Metallic = SafeGetFloat(material, "_DecalMetallic", 0f),
				AmbientOcclusion = SafeGetFloat(material, "_DecalAO", 1f),
			};

			return new VpeMaterialProfileV1 {
				Type = VpeMaterialTypes.Decal,
				Decal = decal,
			};
		}

		private static VpeMaterialProfileV1 TranslateHdrpUnlit(Material material, CaptureContext ctx)
		{
			var unlit = new VpeUnlitProfileV1 {
				BaseColor = {
					Color = SafeGetColor(material, "_UnlitColor", SafeGetColor(material, "_BaseColor", Color.white)),
					Texture = ctx.CaptureImportedTextureRef(material, "_UnlitColorMap")
						?? ctx.CaptureImportedTextureRef(material, "_BaseColorMap"),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
			};
			return new VpeMaterialProfileV1 {
				Type = VpeMaterialTypes.Unlit,
				Unlit = unlit,
			};
		}

		private static string HdrpSurfaceTypeToString(float surfaceType, float alphaCutoffEnable)
		{
			if (surfaceType > 0.5f) {
				return VpeSurfaceTypes.Transparent;
			}
			return alphaCutoffEnable > 0.5f ? VpeSurfaceTypes.AlphaTest : VpeSurfaceTypes.Opaque;
		}

		private static string HdrpEmissiveIntensityUnitToString(float value)
		{
			// HDRP: 0 = Nits, 1 = EV100.
			return value > 0.5f ? VpeEmissiveIntensityUnits.Ev100 : VpeEmissiveIntensityUnits.Nits;
		}

		private static Material CreateSanitizedGltfExportMaterial(Material source)
		{
			if (!source || !source.shader) {
				return null;
			}

			var shaderName = source.shader.name;
			var clone = shaderName switch {
				HdrpLitShaderName => CreateSanitizedHdrpLitMaterial(source),
				HdrpDecalShaderName => CreateSanitizedHdrpDecalMaterial(source),
				_ => null,
			};

			if (clone) {
				clone.name = source.name;
			}
			return clone;
		}

		private static Material CreateSanitizedHdrpLitMaterial(Material source)
		{
			var stripBaseColor =
				SafeGetFloat(source, "_SurfaceType", 0f) > 0.5f
				|| SafeGetFloat(source, "_AlphaCutoffEnable", 0f) > 0.5f;
			var stripMaskMap = HasTexture(source, "_MaskMap");
			var stripThicknessMap = HasTexture(source, "_ThicknessMap");
			if (!stripBaseColor && !stripMaskMap && !stripThicknessMap) {
				return null;
			}

			var clone = new Material(source);
			if (stripBaseColor && clone.HasProperty("_BaseColorMap")) {
				clone.SetTexture("_BaseColorMap", null);
			}
			if (stripMaskMap && clone.HasProperty("_MaskMap")) {
				clone.SetTexture("_MaskMap", null);
			}
			if (stripThicknessMap && clone.HasProperty("_ThicknessMap")) {
				clone.SetTexture("_ThicknessMap", null);
			}
			return clone;
		}

		private static Material CreateSanitizedHdrpDecalMaterial(Material source)
		{
			var stripBaseColor = HasTexture(source, "_BaseColorMap");
			var stripMaskMap = HasTexture(source, "_MaskMap");
			if (!stripBaseColor && !stripMaskMap) {
				return null;
			}

			var clone = new Material(source);
			if (stripBaseColor && clone.HasProperty("_BaseColorMap")) {
				clone.SetTexture("_BaseColorMap", null);
			}
			if (stripMaskMap && clone.HasProperty("_MaskMap")) {
				clone.SetTexture("_MaskMap", null);
			}
			return clone;
		}

		private static bool HasTexture(Material material, string property)
		{
			return material
				&& material.HasProperty(property)
				&& material.GetTexture(property);
		}

		private static bool IsVpeMetalShaderGraph(Shader shader)
		{
			if (!shader) {
				return false;
			}

			var path = AssetDatabase.GetAssetPath(shader);
			return !string.IsNullOrWhiteSpace(path)
				&& path.Replace('\\', '/').EndsWith(VpeMetalShaderGraphPathSuffix, StringComparison.Ordinal);
		}

		private static bool IsVpeRubberShaderGraph(Shader shader)
		{
			if (!shader) {
				return false;
			}

			var path = AssetDatabase.GetAssetPath(shader);
			return !string.IsNullOrWhiteSpace(path)
				&& path.Replace('\\', '/').EndsWith(VpeRubberShaderGraphPathSuffix, StringComparison.Ordinal);
		}

		private static bool IsVpeMetalMaterial(Material material)
		{
			return IsVpeMetalShaderGraph(material.shader)
				|| HasTexture(material, "Texture2D_1E4703A")
				|| HasAnyProperty(material, "Color_AD8F67CE", "Vector1_E8712278", "Vector1_2E810D38");
		}

		private static bool IsVpeRubberMaterial(Material material)
		{
			return IsVpeRubberShaderGraph(material.shader)
				|| HasAnyProperty(material, "Color_9A170B2D", "Texture2D_B9CEC4F9", "Vector1_29D01071");
		}

		// HDRP _RefractionModel float: 0=None, 1=Plane, 2=Sphere, 3=Thin. We also check keywords
		// since the float is sometimes left at a stale value while the keyword tells the real story.
		private static string HdrpRefractionModelToString(float value, Material material)
		{
			if (material.IsKeywordEnabled("_REFRACTION_PLANE")) {
				return VpeRefractionModels.Planar;
			}
			if (material.IsKeywordEnabled("_REFRACTION_SPHERE")) {
				return VpeRefractionModels.Sphere;
			}
			if (material.IsKeywordEnabled("_REFRACTION_THIN")) {
				return VpeRefractionModels.Thin;
			}
			var mode = Mathf.RoundToInt(value);
			return mode switch {
				1 => VpeRefractionModels.Planar,
				2 => VpeRefractionModels.Sphere,
				3 => VpeRefractionModels.Thin,
				_ => VpeRefractionModels.None,
			};
		}

		private static float SafeGetFloat(Material material, string property, float fallback)
		{
			return material && material.HasProperty(property) ? material.GetFloat(property) : fallback;
		}

		private static Color SafeGetColor(Material material, string property, Color fallback)
		{
			return material && material.HasProperty(property) ? material.GetColor(property) : fallback;
		}

		private static Color ResolveHdrpBaseColor(Material material)
		{
			var baseColor = SafeGetColor(material, "_BaseColor", Color.white);
			var color = SafeGetColor(material, "_Color", baseColor);

			// Some HDRP material variants store their visible color override in _Color only.
			// Prefer that override when _BaseColor still looks like the inherited/default value.
			if (material.HasProperty("_Color")
				&& !Approximately(color, baseColor)
				&& RgbApproximately(baseColor, Color.white)
				&& Mathf.Approximately(baseColor.a, color.a)) {
				return color;
			}

			return baseColor;
		}

		private static Color ResolveHdrpEmissiveLdrColor(Material material)
		{
			var ldr = SafeGetColor(material, "_EmissiveColorLDR",
				SafeGetColor(material, "_EmissionColor", Color.black));
			if (!Approximately(ldr, Color.black)) {
				return ldr;
			}

			var intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f);
			var hdr = SafeGetColor(material, "_EmissiveColor", Color.black);
			if (intensity > 0.000001f && !Approximately(hdr, Color.black)) {
				return hdr / intensity;
			}

			return ldr;
		}

		private static bool HasAnyProperty(Material material, params string[] properties)
		{
			for (var i = 0; i < properties.Length; i++) {
				if (material.HasProperty(properties[i])) {
					return true;
				}
			}

			return false;
		}

		private static bool Approximately(Color a, Color b)
		{
			return Mathf.Approximately(a.r, b.r)
				&& Mathf.Approximately(a.g, b.g)
				&& Mathf.Approximately(a.b, b.b)
				&& Mathf.Approximately(a.a, b.a);
		}

		private static bool RgbApproximately(Color a, Color b)
		{
			return Mathf.Approximately(a.r, b.r)
				&& Mathf.Approximately(a.g, b.g)
				&& Mathf.Approximately(a.b, b.b);
		}

		private static Vector2 SafeGetVector2(Material material, string property, Vector2 fallback)
		{
			if (!material || !material.HasProperty(property)) {
				return fallback;
			}

			var value = material.GetVector(property);
			return new Vector2(value.x, value.y);
		}

		private static Vector4 SafeGetVector(Material material, string property, Vector4 fallback)
		{
			return material && material.HasProperty(property) ? material.GetVector(property) : fallback;
		}

		private static Material ResolveSourceMaterialAsset(Material material)
		{
			if (!material) {
				return null;
			}

			var path = AssetDatabase.GetAssetPath(material);
			if (!string.IsNullOrWhiteSpace(path)) {
				return material;
			}

			var normalizedName = NormalizeMaterialName(material.name);
			if (string.IsNullOrWhiteSpace(normalizedName)) {
				return null;
			}

			var guids = AssetDatabase.FindAssets(
				$"{normalizedName} t:Material",
				new[] {
					"Packages/org.visualpinball.engine.unity.hdrp/Assets/Art/Materials",
					"Assets/Art/Materials",
				});
			foreach (var guid in guids) {
				var assetPath = AssetDatabase.GUIDToAssetPath(guid);
				var candidate = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
				if (candidate && string.Equals(NormalizeMaterialName(candidate.name), normalizedName, StringComparison.Ordinal)) {
					return candidate;
				}
			}

			return null;
		}

		private static VpeTextureRefV1 CaptureShaderGraphTextureRef(
			CaptureContext ctx,
			Material material,
			Material source,
			string property,
			string colorSpace)
		{
			return ctx.CaptureSideChannelTextureRef(material, property, colorSpace)
				?? ctx.CaptureSideChannelTextureRef(source, property, colorSpace);
		}

		private static VpeNormalMapRefV1 CaptureShaderGraphNormalMapRef(
			CaptureContext ctx,
			Material material,
			Material source,
			string property,
			float strength)
		{
			return ctx.CaptureSideChannelNormalMapRef(material, property, strength)
				?? ctx.CaptureSideChannelNormalMapRef(source, property, strength);
		}

		private static void ApplyTextureTransform(VpeTextureRefV1 texture, Vector2 scale, Vector2 offset)
		{
			if (texture == null) {
				return;
			}

			texture.Scale = scale;
			texture.Offset = offset;
		}

		private static void ApplyTextureTransform(VpeNormalMapRefV1 texture, Vector2 scale, Vector2 offset)
		{
			if (texture == null) {
				return;
			}

			texture.Scale = scale;
			texture.Offset = offset;
		}

		public static string NormalizeMaterialName(string materialName)
			=> VpeMaterialNameUtil.NormalizeMaterialName(materialName);

		private sealed class CaptureContext
		{
			private readonly Dictionary<Texture2D, VpeTextureAssetV1> _assetsByTexture = new();
			private readonly Dictionary<string, byte[]> _textureBlobs = new(StringComparer.Ordinal);
			private readonly Dictionary<string, UnsupportedShaderUsage> _unsupportedShaders = new(StringComparer.Ordinal);
			private int _nextIndex;

			public IReadOnlyDictionary<string, byte[]> TextureBlobs => _textureBlobs;

			public VpeTextureAssetV1[] BuildTextureAssets()
			{
				var assets = new VpeTextureAssetV1[_assetsByTexture.Count];
				var i = 0;
				foreach (var asset in _assetsByTexture.Values) {
					assets[i++] = asset;
				}
				return assets;
			}

			public void RegisterUnsupportedMaterial(string shaderName, string materialName)
			{
				if (string.IsNullOrWhiteSpace(shaderName)) {
					shaderName = "<unknown>";
				}

				if (!_unsupportedShaders.TryGetValue(shaderName, out var usage)) {
					usage = new UnsupportedShaderUsage();
					_unsupportedShaders[shaderName] = usage;
				}

				usage.Count++;
				if (!string.IsNullOrWhiteSpace(materialName) && usage.MaterialSamples.Count < 4) {
					usage.MaterialSamples.Add(materialName);
				}
			}

			public void LogUnsupportedMaterialsSummary()
			{
				if (_unsupportedShaders.Count == 0) {
					return;
				}

				var totalMaterials = 0;
				var shaderSummaries = new List<string>();
				foreach (var entry in _unsupportedShaders.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal)) {
					totalMaterials += entry.Value.Count;
					var materialSamples = entry.Value.MaterialSamples.Count > 0
						? $" [{string.Join(", ", entry.Value.MaterialSamples)}{(entry.Value.Count > entry.Value.MaterialSamples.Count ? ", ..." : string.Empty)}]"
						: string.Empty;
					shaderSummaries.Add($"{entry.Key} x{entry.Value.Count}{materialSamples}");
				}

				Logger.Info(
					$"Skipped v1 material translation for {totalMaterials} material(s) across {_unsupportedShaders.Count} unsupported shader(s). " +
					$"These materials will fall back to the glTF-imported material at runtime. " +
					$"Shaders: {string.Join("; ", shaderSummaries)}");
			}

			// Captures a texture reference whose pixel data must be shipped in the side-channel
			// (i.e. is not losslessly reproduced by the glb). Use for HDRP-specific packings like
			// MaskMap where channel semantics differ from glTF's PBR textures.
			public VpeTextureRefV1 CaptureSideChannelTextureRef(Material material, string property, string colorSpace, VpeTextureRefV1 fallback = null)
			{
				if (!material || !material.HasProperty(property)) {
					return fallback;
				}
				var texture = material.GetTexture(property) as Texture2D;
				if (!texture) {
					return fallback;
				}

				var asset = GetOrCaptureAsset(texture, colorSpace);
				if (asset == null) {
					return fallback;
				}

				return new VpeTextureRefV1 {
					TextureId = asset.Id,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
				};
			}

			// Captures tiling only — no TextureId, no side-channel bytes. Pixel data is read at
			// runtime from the gltFast-imported material by matching property-name aliases.
			public VpeTextureRefV1 CaptureImportedTextureRef(Material material, string property)
			{
				if (!material.HasProperty(property)) {
					return null;
				}
				var texture = material.GetTexture(property);
				if (!texture) {
					return null;
				}
				return new VpeTextureRefV1 {
					TextureId = null,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
				};
			}

			public VpeNormalMapRefV1 CaptureSideChannelNormalMapRef(Material material, string property, float strength)
			{
				if (!material || !material.HasProperty(property)) {
					return null;
				}
				var texture = material.GetTexture(property) as Texture2D;
				if (!texture) {
					return null;
				}

				var asset = GetOrCaptureAsset(texture, VpeColorSpaces.Linear);
				if (asset == null) {
					return null;
				}

				return new VpeNormalMapRefV1 {
					TextureId = asset.Id,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
					Strength = strength,
					Packing = VpeNormalPackings.Rgb,
				};
			}

			public VpeNormalMapRefV1 CaptureImportedNormalMapRef(Material material, string property, float strength)
			{
				if (!material.HasProperty(property)) {
					return null;
				}
				var texture = material.GetTexture(property);
				if (!texture) {
					return null;
				}
				return new VpeNormalMapRefV1 {
					TextureId = null,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
					Strength = strength,
					// Runtime imports may arrive as plain RGB (glTFast doesn't carry Unity's normal
					// map import flag). The resolver re-packs as needed.
					Packing = VpeNormalPackings.Rgb,
				};
			}

			private VpeTextureAssetV1 GetOrCaptureAsset(Texture2D texture, string colorSpace)
			{
				if (_assetsByTexture.TryGetValue(texture, out var existing)) {
					return existing;
				}

				var requestedColorSpace = string.Equals(colorSpace, VpeColorSpaces.Linear, StringComparison.OrdinalIgnoreCase)
					? VpeColorSpaces.Linear
					: VpeColorSpaces.SRgb;
				var linear = requestedColorSpace == VpeColorSpaces.Linear;
				if (!HdrpMaterialV1TextureEncoder.TryEncode(texture, linear, out var pngData)) {
					return null;
				}

				var id = BuildId(texture);
				var fileName = $"tex_{_nextIndex++:D4}.png";
				var asset = new VpeTextureAssetV1 {
					Id = id,
					FileName = fileName,
					ColorSpace = requestedColorSpace,
					WrapMode = (int)texture.wrapMode,
					FilterMode = (int)texture.filterMode,
					AnisoLevel = Mathf.Max(1, texture.anisoLevel),
					GenerateMipMaps = true,
					SourceName = texture.name,
					Width = texture.width,
					Height = texture.height,
				};

				// Ask the Editor TextureImporter for canonical sampling settings when available. The
				// semantic slot already decided ColorSpace above (for example MaskMap/ThicknessMap
				// must stay linear even if the source asset is imported as a regular sRGB texture),
				// but wrap/filter/mip intent should still follow the authored asset settings.
				var assetPath = AssetDatabase.GetAssetPath(texture);
				if (!string.IsNullOrEmpty(assetPath) && AssetImporter.GetAtPath(assetPath) is TextureImporter importer) {
					asset.GenerateMipMaps = importer.mipmapEnabled;
					asset.AnisoLevel = Mathf.Max(asset.AnisoLevel, importer.anisoLevel);
					asset.WrapMode = (int)importer.wrapMode;
					asset.FilterMode = (int)importer.filterMode;
				}

				_assetsByTexture[texture] = asset;
				_textureBlobs[fileName] = pngData;
				return asset;
			}

			private string BuildId(Texture2D texture)
			{
				var raw = string.IsNullOrWhiteSpace(texture.name) ? $"tex{_nextIndex}" : texture.name;
				// Normalize so the id is stable across exports regardless of editor instance suffixes.
				return VpeMaterialNameUtil.NormalizeTextureName(raw);
			}

			private sealed class UnsupportedShaderUsage
			{
				public int Count;
				public readonly List<string> MaterialSamples = new();
			}
		}

		private sealed class GltfExportMaterialScope : IDisposable
		{
			private readonly List<RendererMaterialState> _rendererStates = new();
			private readonly List<Material> _clonedMaterials = new();

			public GltfExportMaterialScope(IEnumerable<Renderer> renderers)
			{
				if (renderers == null) {
					return;
				}

				var sanitizedBySource = new Dictionary<Material, Material>();
				foreach (var renderer in renderers) {
					if (!renderer) {
						continue;
					}

					var originalMaterials = renderer.sharedMaterials;
					if (originalMaterials == null || originalMaterials.Length == 0) {
						continue;
					}

					Material[] sanitizedMaterials = null;
					for (var index = 0; index < originalMaterials.Length; index++) {
						var source = originalMaterials[index];
						if (!source) {
							continue;
						}

						if (!sanitizedBySource.TryGetValue(source, out var sanitized)) {
							sanitized = CreateSanitizedGltfExportMaterial(source) ?? source;
							sanitizedBySource[source] = sanitized;
							if (sanitized != source) {
								_clonedMaterials.Add(sanitized);
							}
						}

						if (sanitized == source) {
							continue;
						}

						sanitizedMaterials ??= (Material[])originalMaterials.Clone();
						sanitizedMaterials[index] = sanitized;
					}

					if (sanitizedMaterials == null) {
						continue;
					}

					_rendererStates.Add(new RendererMaterialState(renderer, originalMaterials));
					renderer.sharedMaterials = sanitizedMaterials;
				}
			}

			public void Dispose()
			{
				foreach (var state in _rendererStates) {
					if (state.Renderer) {
						state.Renderer.sharedMaterials = state.Materials;
					}
				}

				foreach (var material in _clonedMaterials) {
					if (material) {
						UnityEngine.Object.DestroyImmediate(material);
					}
				}

				_rendererStates.Clear();
				_clonedMaterials.Clear();
			}

			private readonly struct RendererMaterialState
			{
				public readonly Renderer Renderer;
				public readonly Material[] Materials;

				public RendererMaterialState(Renderer renderer, Material[] materials)
				{
					Renderer = renderer;
					Materials = materials;
				}
			}
		}
	}

	[InitializeOnLoad]
	internal static class HdrpMaterialV1TranslatorRegistration
	{
		static HdrpMaterialV1TranslatorRegistration()
		{
			VpeMaterialV1Translator.Register(new Adapter());
		}

		private sealed class Adapter : IVpeMaterialV1Translator, IVpeMaterialGltfExportPreprocessor
		{
			public VpeMaterialCaptureResult Capture(Transform tableRoot, IEnumerable<Renderer> renderers)
			{
				return HdrpMaterialV1Translator.Capture(tableRoot, renderers);
			}

			public IDisposable PrepareGltfExport(IEnumerable<Renderer> renderers)
			{
				return HdrpMaterialV1Translator.PrepareGltfExport(renderers);
			}
		}
	}
}
