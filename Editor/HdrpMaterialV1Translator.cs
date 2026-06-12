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
		private const string VpeMetalShaderGraphPathSuffix = "/Assets/Resources/Graphs/Metal.shadergraph";
		private const string VpeRubberShaderGraphPathSuffix = "/Assets/Resources/Graphs/Rubber.shadergraph";
		private const string VpeDmdShaderGraphPathSuffix = "/Assets/Shaders/Srp/Display/DotMatrixDisplayGraph.shadergraph";

		private const string VpeBaseColorMap = "_VpeBaseColorMap";
		private const string VpeColor = "_VpeColor";
		private const string VpeColorTint = "_VpeColorTint";
		private const string VpeMaskMap = "_VpeMaskMap";
		private const string VpeMetallicIntensity = "_VpeMetallicIntensity";
		private const string VpeNormalMap = "_VpeNormalMap";
		private const string VpeNormalIntensity = "_VpeNormalIntensity";
		private const string VpeOcclusionIntensity = "_VpeOcclusionIntensity";
		private const string VpeOffset = "_VpeOffset";
		private const string VpeSmoothnessRemapMax = "_VpeSmoothnessRemapMax";
		private const string VpeSmoothnessRemapMin = "_VpeSmoothnessRemapMin";
		private const string VpeTiling = "_VpeTiling";

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

			var shaderName = material.shader.name;
			switch (shaderName) {
				case HdrpLitShaderName:
					return TranslateHdrpLit(material, ctx);
				case HdrpDecalShaderName:
					return TranslateHdrpDecal(material, ctx);
				case HdrpUnlitShaderName:
					return TranslateHdrpUnlit(material, ctx);
			}

			if (IsVpeRubberMaterial(material)) {
				return TranslateVpeRubberShaderGraph(material, ctx);
			}
			if (IsVpeMetalMaterial(material)) {
				return TranslateVpeMetalShaderGraph(material, ctx);
			}
			if (IsVpeDmdMaterial(material)) {
				return TranslateVpeDmdShaderGraph(material);
			}

			ctx.RegisterUnsupportedMaterial(shaderName, material.name);
			return null;
		}

		private static VpeMaterialProfileV1 TranslateHdrpLit(Material material, CaptureContext ctx)
		{
			// Every texture is cooked into the side-channel as a GPU-ready payload; the glb carries
			// no image data for captured materials. This is what makes runtime import fast: no PNG
			// decode, no runtime compression, no normal repack.
			var baseColorTexture = ctx.CaptureSideChannelTextureRef(material, "_BaseColorMap", VpeColorSpaces.SRgb);
			var baseColor = ResolveHdrpBaseColor(material);

			var lit = new VpeLitProfileV1 {
				BaseColor = {
					Color = baseColor,
					Texture = baseColorTexture,
				},
				Metallic = SafeGetFloat(material, "_Metallic", 0f),
				Smoothness = SafeGetFloat(material, "_Smoothness", 0.5f),
				OcclusionStrength = 1f,
				IridescenceMask = SafeGetFloat(material, "_IridescenceMask", 1f),
				IridescenceThickness = SafeGetFloat(material, "_IridescenceThickness", 1f),
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
				GeometricSpecularAa = SafeGetFloat(material, "_EnableGeometricSpecularAA", 0f) > 0.5f,
				SpecularAaScreenSpaceVariance = SafeGetFloat(material, "_SpecularAAScreenSpaceVariance", 0f),
				SpecularAaThreshold = SafeGetFloat(material, "_SpecularAAThreshold", 0f),
				SupportDecals = SafeGetFloat(material, "_SupportDecals", 1f) > 0.5f
					&& !material.IsKeywordEnabled("_DISABLE_DECALS"),
				NormalMap = ctx.CaptureSideChannelNormalMapRef(material, "_NormalMap",
					strength: SafeGetFloat(material, "_NormalScale", 1f)),
				Emissive = new VpeEmissiveV1 {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureSideChannelTextureRef(material, "_EmissiveColorMap", VpeColorSpaces.SRgb),
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
			var maskMap = CaptureShaderGraphTextureRef(ctx, material, source, VpeMaskMap, VpeColorSpaces.Linear)
				?? ctx.CaptureTextureAssetRef("Packages/org.visualpinball.unity.assets/Assets/Library/_Generic/Tileable Textures/tx_wear_scratches_heavy_mask_MADR.png", VpeColorSpaces.Linear);
			if (maskMap != null) {
				maskMap.Scale = SafeGetVector2(material, VpeTiling, SafeGetVector2(source, VpeTiling, maskMap.Scale));
				maskMap.Offset = SafeGetVector2(material, VpeOffset, SafeGetVector2(source, VpeOffset, maskMap.Offset));
			}

			var metallicIntensity = SafeGetFloat(material, VpeMetallicIntensity, SafeGetFloat(source, VpeMetallicIntensity, 1f));
			var occlusionIntensity = Mathf.Clamp01(SafeGetFloat(material, VpeOcclusionIntensity, SafeGetFloat(source, VpeOcclusionIntensity, 1f)));
			var lit = new VpeLitProfileV1 {
				BaseColor = {
					Color = SafeGetColor(material, VpeColor, SafeGetColor(source, VpeColor, ResolveHdrpBaseColor(material))),
				},
				Metallic = metallicIntensity,
				Smoothness = SafeGetFloat(material, "_Smoothness", SafeGetFloat(source, "_Smoothness", 0.5f)),
				OcclusionStrength = occlusionIntensity,
				MaskMap = maskMap,
				MaskPacking = VpeMaskPackings.HdrpMaskMap,
				MetallicRemap = new Vector2(0f, metallicIntensity),
				SmoothnessRemap = new Vector2(
					SafeGetFloat(material, VpeSmoothnessRemapMin, SafeGetFloat(source, VpeSmoothnessRemapMin, 0f)),
					SafeGetFloat(material, VpeSmoothnessRemapMax, SafeGetFloat(source, VpeSmoothnessRemapMax, 1f))),
				AoRemap = new Vector2(1f - occlusionIntensity, 1f),
				AlphaRemap = new Vector2(
					SafeGetFloat(material, "_AlphaRemapMin", SafeGetFloat(source, "_AlphaRemapMin", 0f)),
					SafeGetFloat(material, "_AlphaRemapMax", SafeGetFloat(source, "_AlphaRemapMax", 1f))),
				UvBase = 0,
				TexWorldScale = SafeGetFloat(material, "_TexWorldScale", SafeGetFloat(source, "_TexWorldScale", 1f)),
				InvTilingScale = SafeGetFloat(material, "_InvTilingScale", SafeGetFloat(source, "_InvTilingScale", 1f)),
				GeometricSpecularAa = SafeGetFloat(material, "_EnableGeometricSpecularAA", SafeGetFloat(source, "_EnableGeometricSpecularAA", 0f)) > 0.5f,
				SpecularAaScreenSpaceVariance = SafeGetFloat(material, "_SpecularAAScreenSpaceVariance", SafeGetFloat(source, "_SpecularAAScreenSpaceVariance", 0f)),
				SpecularAaThreshold = SafeGetFloat(material, "_SpecularAAThreshold", SafeGetFloat(source, "_SpecularAAThreshold", 0f)),
				Emissive = new VpeEmissiveV1 {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureSideChannelTextureRef(material, "_EmissiveColorMap", VpeColorSpaces.SRgb),
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
				Type = VpeMaterialTypes.Metal,
				Metal = CreateShaderGraphProfile(material),
				Lit = lit,
			};
		}

		private static VpeMaterialProfileV1 TranslateVpeDmdShaderGraph(Material material)
		{
			return new VpeMaterialProfileV1 {
				Type = VpeMaterialTypes.Dmd,
				Dmd = CreateShaderGraphProfile(material),
			};
		}

		private static VpeShaderGraphProfileV1 CreateShaderGraphProfile(Material material)
		{
			var source = ResolveSourceMaterialAsset(material);
			return new VpeShaderGraphProfileV1 {
				TemplateName = source ? source.name : material.name,
			};
		}

		private static VpeMaterialProfileV1 TranslateVpeRubberShaderGraph(Material material, CaptureContext ctx)
		{
			var source = ResolveSourceMaterialAsset(material);
			var baseColorMap = CaptureShaderGraphTextureRef(ctx, material, source, VpeBaseColorMap, VpeColorSpaces.SRgb);
			var normalMap = CaptureShaderGraphNormalMapRef(ctx, material, source, VpeNormalMap,
				SafeGetFloat(material, VpeNormalIntensity, SafeGetFloat(source, VpeNormalIntensity, 1f)));
			var maskMap = CaptureShaderGraphTextureRef(ctx, material, source, VpeMaskMap, VpeColorSpaces.Linear);

			var scale = SafeGetVector2(material, VpeTiling, SafeGetVector2(source, VpeTiling, Vector2.one));
			var offset = SafeGetVector2(material, VpeOffset, SafeGetVector2(source, VpeOffset, Vector2.zero));
			ApplyTextureTransform(baseColorMap, scale, offset);
			ApplyTextureTransform(normalMap, scale, offset);
			ApplyTextureTransform(maskMap, scale, offset);

			var occlusionIntensity = Mathf.Clamp01(SafeGetFloat(material, VpeOcclusionIntensity, SafeGetFloat(source, VpeOcclusionIntensity, 1f)));
			var lit = new VpeLitProfileV1 {
				BaseColor = {
					Color = SafeGetColor(material, VpeColorTint, SafeGetColor(source, VpeColorTint, ResolveHdrpBaseColor(material))),
					Texture = baseColorMap,
				},
				Metallic = SafeGetFloat(material, "_Metallic", SafeGetFloat(source, "_Metallic", 0f)),
				Smoothness = SafeGetFloat(material, "_Smoothness", SafeGetFloat(source, "_Smoothness", 0.5f)),
				OcclusionStrength = occlusionIntensity,
				MaskMap = maskMap,
				MaskPacking = VpeMaskPackings.HdrpMaskMap,
				MetallicRemap = new Vector2(0f, SafeGetFloat(material, "_Metallic", SafeGetFloat(source, "_Metallic", 0f))),
				SmoothnessRemap = new Vector2(
					SafeGetFloat(material, VpeSmoothnessRemapMin, SafeGetFloat(source, VpeSmoothnessRemapMin, 0f)),
					SafeGetFloat(material, VpeSmoothnessRemapMax, SafeGetFloat(source, VpeSmoothnessRemapMax, 1f))),
				AoRemap = new Vector2(1f - occlusionIntensity, 1f),
				AlphaRemap = new Vector2(
					SafeGetFloat(material, "_AlphaRemapMin", SafeGetFloat(source, "_AlphaRemapMin", 0f)),
					SafeGetFloat(material, "_AlphaRemapMax", SafeGetFloat(source, "_AlphaRemapMax", 1f))),
				UvBase = 0,
				TexWorldScale = SafeGetFloat(material, "_TexWorldScale", SafeGetFloat(source, "_TexWorldScale", 1f)),
				InvTilingScale = SafeGetFloat(material, "_InvTilingScale", SafeGetFloat(source, "_InvTilingScale", 1f)),
				GeometricSpecularAa = SafeGetFloat(material, "_EnableGeometricSpecularAA", SafeGetFloat(source, "_EnableGeometricSpecularAA", 0f)) > 0.5f,
				SpecularAaScreenSpaceVariance = SafeGetFloat(material, "_SpecularAAScreenSpaceVariance", SafeGetFloat(source, "_SpecularAAScreenSpaceVariance", 0f)),
				SpecularAaThreshold = SafeGetFloat(material, "_SpecularAAThreshold", SafeGetFloat(source, "_SpecularAAThreshold", 0f)),
				NormalMap = normalMap,
				Emissive = new VpeEmissiveV1 {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureSideChannelTextureRef(material, "_EmissiveColorMap", VpeColorSpaces.SRgb),
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
				Type = VpeMaterialTypes.Rubber,
				Rubber = CreateShaderGraphProfile(material),
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
				NormalMap = ctx.CaptureSideChannelNormalMapRef(material, "_NormalMap",
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

			// Captured material types carry all their texture data in the cooked side-channel, so
			// the glTF export must not duplicate any of it. Stripping every texture also stops
			// glTFast from encoding hundreds of megabytes of PNGs into table.glb, which used to
			// dominate both package size and load time. Unsupported shaders keep their textures so
			// the glTF fallback material still looks right.
			var shaderName = source.shader.name;
			var clone = shaderName switch {
				HdrpLitShaderName => CreateTextureFreeClone(source),
				HdrpDecalShaderName => CreateTextureFreeClone(source),
				_ => IsVpeRubberMaterial(source) || IsVpeMetalMaterial(source) || IsVpeDmdMaterial(source)
					? CreateTextureFreeClone(source)
					: null,
			};

			if (clone) {
				clone.name = source.name;
			}
			return clone;
		}

		private static Material CreateTextureFreeClone(Material source)
		{
			var hasAnyTexture = false;
			var propertyNames = source.GetTexturePropertyNames();
			foreach (var propertyName in propertyNames) {
				if (!string.IsNullOrWhiteSpace(propertyName) && source.GetTexture(propertyName)) {
					hasAnyTexture = true;
					break;
				}
			}
			if (!hasAnyTexture) {
				return null;
			}

			var clone = new Material(source);
			foreach (var propertyName in propertyNames) {
				if (!string.IsNullOrWhiteSpace(propertyName) && clone.GetTexture(propertyName)) {
					clone.SetTexture(propertyName, null);
				}
			}
			return clone;
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

		private static bool IsVpeDmdShaderGraph(Shader shader)
		{
			if (!shader) {
				return false;
			}

			var path = AssetDatabase.GetAssetPath(shader);
			return !string.IsNullOrWhiteSpace(path)
				&& path.Replace('\\', '/').EndsWith(VpeDmdShaderGraphPathSuffix, StringComparison.Ordinal);
		}

		private static bool IsVpeMetalMaterial(Material material)
		{
			return IsVpeMetalShaderGraph(material.shader);
		}

		private static bool IsVpeRubberMaterial(Material material)
		{
			return IsVpeRubberShaderGraph(material.shader);
		}

		private static bool IsVpeDmdMaterial(Material material)
		{
			return IsVpeDmdShaderGraph(material.shader);
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

			var measured = ResolveMeasuredMaterialAsset(normalizedName);
			if (measured) {
				return measured;
			}

			var roots = new[] {
				"Assets",
				"Packages/org.visualpinball.engine.unity.hdrp",
				"Packages/org.visualpinball.unity.assets",
			};
			return FindMaterialAssetByExactName(normalizedName, roots)
				?? FindMaterialAssetByExactName(normalizedName, null);
		}

		private static Material ResolveMeasuredMaterialAsset(string normalizedName)
		{
			var measuredRoots = new[] {
				"Packages/org.visualpinball.engine.unity.hdrp/Assets/Art/Materials/Measured/Metal",
				"Packages/org.visualpinball.engine.unity.hdrp/Assets/Art/Materials/Measured/Rubber",
				"Assets/Art/Materials/Measured/Metal",
				"Assets/Art/Materials/Measured/Rubber",
			};

			foreach (var root in measuredRoots) {
				var candidate = AssetDatabase.LoadAssetAtPath<Material>($"{root}/{normalizedName}.mat");
				if (candidate && string.Equals(NormalizeMaterialName(candidate.name), normalizedName, StringComparison.Ordinal)) {
					return candidate;
				}
			}

			return null;
		}

		private static Material FindMaterialAssetByExactName(string normalizedName, string[] roots)
		{
			var guids = roots == null
				? AssetDatabase.FindAssets("t:Material")
				: AssetDatabase.FindAssets("t:Material", roots);
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

		private enum TextureCookKind
		{
			Color,
			Normal,
		}

		private sealed class CaptureContext
		{
			private readonly Dictionary<(Texture2D, TextureCookKind), VpeTextureAssetV1> _assetsByTexture = new();
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
				if (!material) {
					return fallback;
				}
				var texture = material.HasProperty(property) ? material.GetTexture(property) as Texture2D : null;
				if (!texture) {
					return CaptureSerializedSideChannelTextureRef(material, property, colorSpace, fallback);
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

			private VpeTextureRefV1 CaptureSerializedSideChannelTextureRef(Material material, string property, string colorSpace, VpeTextureRefV1 fallback)
			{
				var texEnvs = new SerializedObject(material).FindProperty("m_SavedProperties.m_TexEnvs");
				if (texEnvs == null || !texEnvs.isArray) {
					return CaptureYamlSideChannelTextureRef(material, property, colorSpace, fallback);
				}

				for (var i = 0; i < texEnvs.arraySize; i++) {
					var entry = texEnvs.GetArrayElementAtIndex(i);
					var key = entry.FindPropertyRelative("first")?.stringValue;
					if (!string.Equals(key, property, StringComparison.Ordinal)) {
						continue;
					}

					var value = entry.FindPropertyRelative("second");
					var texture = value?.FindPropertyRelative("m_Texture")?.objectReferenceValue as Texture2D;
					if (!texture) {
						return CaptureYamlSideChannelTextureRef(material, property, colorSpace, fallback);
					}

					var asset = GetOrCaptureAsset(texture, colorSpace);
					if (asset == null) {
						return fallback;
					}

					return new VpeTextureRefV1 {
						TextureId = asset.Id,
						Offset = value.FindPropertyRelative("m_Offset")?.vector2Value ?? Vector2.zero,
						Scale = value.FindPropertyRelative("m_Scale")?.vector2Value ?? Vector2.one,
					};
				}

				return CaptureYamlSideChannelTextureRef(material, property, colorSpace, fallback);
			}

			private VpeTextureRefV1 CaptureYamlSideChannelTextureRef(Material material, string property, string colorSpace, VpeTextureRefV1 fallback)
			{
				var assetPath = AssetDatabase.GetAssetPath(material);
				if (string.IsNullOrWhiteSpace(assetPath)) {
					return fallback;
				}

				var fullPath = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
				if (!File.Exists(fullPath)) {
					return fallback;
				}

				var guid = ExtractTextureGuidFromMaterialYaml(fullPath, property);
				if (string.IsNullOrWhiteSpace(guid)) {
					return fallback;
				}

				var texturePath = AssetDatabase.GUIDToAssetPath(guid);
				if (string.IsNullOrWhiteSpace(texturePath)) {
					return fallback;
				}

				var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
				if (!texture) {
					return fallback;
				}

				var asset = GetOrCaptureAsset(texture, colorSpace);
				if (asset == null) {
					return fallback;
				}

				return new VpeTextureRefV1 {
					TextureId = asset.Id,
					Offset = Vector2.zero,
					Scale = Vector2.one,
				};
			}

			private static string ExtractTextureGuidFromMaterialYaml(string materialPath, string property)
			{
				var lines = File.ReadAllLines(materialPath);
				for (var i = 0; i < lines.Length; i++) {
					if (!lines[i].TrimStart().StartsWith($"- {property}:", StringComparison.Ordinal)) {
						continue;
					}

					for (var j = i + 1; j < lines.Length && j < i + 8; j++) {
						var line = lines[j];
						if (j > i + 1 && line.TrimStart().StartsWith("- ", StringComparison.Ordinal)) {
							break;
						}
						var marker = "guid:";
						var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
						if (markerIndex < 0) {
							continue;
						}

						var start = markerIndex + marker.Length;
						var end = line.IndexOf(',', start);
						var guid = (end >= 0 ? line.Substring(start, end - start) : line.Substring(start)).Trim();
						return string.Equals(guid, "0", StringComparison.Ordinal) ? null : guid;
					}
				}

				return null;
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

				var asset = GetOrCaptureAsset(texture, VpeColorSpaces.Linear, TextureCookKind.Normal);
				if (asset == null) {
					return null;
				}

				return new VpeNormalMapRefV1 {
					TextureId = asset.Id,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
					Strength = strength,
					// Cooked payloads are already in HDRP's AG layout, so runtime must not repack.
					// The PNG fallback ships plain RGB and keeps the runtime repack path.
					Packing = IsCooked(asset) ? VpeNormalPackings.Dxt5nm : VpeNormalPackings.Rgb,
					RuntimeCompress = !IsCooked(asset),
				};
			}

			public VpeTextureRefV1 CaptureTextureAssetRef(string assetPath, string colorSpace)
			{
				if (string.IsNullOrWhiteSpace(assetPath)) {
					return null;
				}

				var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
				if (!texture) {
					return null;
				}

				var asset = GetOrCaptureAsset(texture, colorSpace);
				if (asset == null) {
					return null;
				}

				return new VpeTextureRefV1 {
					TextureId = asset.Id,
					Offset = Vector2.zero,
					Scale = Vector2.one,
				};
			}

			private VpeTextureAssetV1 GetOrCaptureAsset(Texture2D texture, string colorSpace, TextureCookKind kind = TextureCookKind.Color)
			{
				if (_assetsByTexture.TryGetValue((texture, kind), out var existing)) {
					return existing;
				}

				var requestedColorSpace = string.Equals(colorSpace, VpeColorSpaces.Linear, StringComparison.OrdinalIgnoreCase)
					? VpeColorSpaces.Linear
					: VpeColorSpaces.SRgb;
				var linear = requestedColorSpace == VpeColorSpaces.Linear;

				var id = BuildId(texture);
				var asset = new VpeTextureAssetV1 {
					Id = id,
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

				// Cook into a GPU-ready payload (block-compressed, mips baked) so the runtime uploads
				// raw bytes instead of decoding PNGs and re-compressing. Falls back to the legacy PNG
				// side-channel if cooking fails.
				byte[] blobData;
				var cookSucceeded = kind == TextureCookKind.Normal
					? HdrpMaterialV1TextureEncoder.TryEncodeCookedNormal(texture, asset.GenerateMipMaps, out var cooked)
					: HdrpMaterialV1TextureEncoder.TryEncodeCooked(texture, linear, asset.GenerateMipMaps, out cooked);
				if (cookSucceeded && cooked.IsValid) {
					blobData = cooked.Data;
					asset.PixelFormat = cooked.PixelFormat;
					asset.MipCount = cooked.MipCount;
					asset.Width = cooked.Width;
					asset.Height = cooked.Height;
					asset.MimeType = "application/x-vpe-raw";
					asset.RuntimeCompress = false;
					asset.FileName = $"tex_{_nextIndex++:D4}.tex";
				} else {
					if (!HdrpMaterialV1TextureEncoder.TryEncode(texture, linear, out blobData)) {
						return null;
					}
					asset.FileName = $"tex_{_nextIndex++:D4}.png";
				}

				_assetsByTexture[(texture, kind)] = asset;
				_textureBlobs[asset.FileName] = blobData;
				return asset;
			}

			// True when the asset was cooked into a raw GPU payload (as opposed to a PNG fallback).
			private static bool IsCooked(VpeTextureAssetV1 asset) => !string.IsNullOrEmpty(asset?.PixelFormat);

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
