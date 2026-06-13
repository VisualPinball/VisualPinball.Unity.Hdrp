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
	// Editor-only. Translates Unity Materials on a scene's renderers into the portable material
	// payload (schema v2) plus a set of source texture blobs keyed by file name. Portable intent
	// goes to the top of each profile; everything HDRP-specific lands in the profile's Hdrp block.
	//
	// Only HDRP-aware mappings are implemented here; if VPE adopts additional pipelines the
	// translator fans out on shader name.
	internal static class HdrpMaterialTranslator
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

		public static VpeMaterialCaptureResult Capture(Transform tableRoot, IEnumerable<Renderer> renderers, Func<Transform, string> nodeId)
		{
			var profiles = new Dictionary<string, VpeMaterialProfile>(StringComparer.Ordinal);
			var rendererStates = new List<VpeRendererState>();
			var ctx = new CaptureContext();

			if (renderers != null) {
				foreach (var renderer in renderers) {
					if (!renderer) {
						continue;
					}

					if (tableRoot) {
						rendererStates.Add(CaptureRendererState(renderer, tableRoot, nodeId));
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

			var payload = new VpeMaterialsPayload {
				FormatVersion = VpeMaterialSchema.Version,
				WrittenBy = "HdrpMaterialTranslator",
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

		private static VpeRendererState CaptureRendererState(Renderer renderer, Transform tableRoot, Func<Transform, string> nodeId)
		{
			var id = nodeId?.Invoke(renderer.transform);
			return new VpeRendererState {
				NodeId = id,
				CastShadows = VpeMaterialEnums.ToShadowCastingMode(renderer.shadowCastingMode),
				ReceiveShadows = renderer.receiveShadows,
				RenderingLayerMask = renderer.renderingLayerMask,
				Hdrp = new VpeHdrpRendererHints {
					RayTracingMode = (int)renderer.rayTracingMode,
				},
			};
		}

		private static VpeMaterialProfile TranslateMaterial(Material material, CaptureContext ctx)
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

		private static VpeMaterialProfile TranslateHdrpLit(Material material, CaptureContext ctx)
		{
			// Every texture is shipped in the source layer; the glb carries no image data for
			// captured materials. This is what makes runtime import fast: no PNG decode in the glb
			// path, the player cooks the sources once and caches GPU-ready payloads.
			var baseColorTexture = ctx.CaptureSideChannelTextureRef(material, "_BaseColorMap", VpeColorSpaces.SRgb);
			var baseColor = ResolveHdrpBaseColor(material);

			var lit = new VpeLitProfile {
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
				// has no lossless equivalent, so the packing is declared via MaskPacking.
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
				NormalMap = ctx.CaptureSideChannelNormalMapRef(material, "_NormalMap",
					strength: SafeGetFloat(material, "_NormalScale", 1f)),
				Emissive = new VpeEmissive {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureSideChannelTextureRef(material, "_EmissiveColorMap", VpeColorSpaces.SRgb),
					UseIntensity = SafeGetFloat(material, "_UseEmissiveIntensity", 0f) > 0.5f,
					Intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f),
					IntensityUnit = HdrpEmissiveIntensityUnitToString(
						SafeGetFloat(material, "_EmissiveIntensityUnit", 0f)),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
				DoubleSidedGi = material.doubleSidedGI,
				BlendMode = VpeMaterialEnums.ToBlendMode(Mathf.RoundToInt(SafeGetFloat(material, "_BlendMode", 0f))),
				SortPriority = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentSortPriority", 0f)),

				RefractionModel = HdrpRefractionModelToString(
					SafeGetFloat(material, "_RefractionModel", 0f),
					material),
				Ior = SafeGetFloat(material, "_Ior", 1f),
				// Authoring intent is encoded by the explicit HDRP translucent signals:
				// MaterialID==5 or the transmission keyword. Do not infer from _TransmissionEnable;
				// HDRP keeps that float at 1 on many non-translucent materials.
				HasTransmission = material.IsKeywordEnabled("_MATERIAL_FEATURE_TRANSMISSION")
					|| Mathf.Approximately(SafeGetFloat(material, "_MaterialID", 1f), 5f),
				Thickness = SafeGetFloat(material, "_Thickness", 1f),
				ThicknessRemap = SafeGetVector2(material, "_ThicknessRemap", new Vector2(0f, 1f)),
				AbsorptionDistance = SafeGetFloat(material, "_ATDistance", 1f),
				TransmittanceColor = SafeGetColor(material, "_TransmittanceColor", Color.white),
				ThicknessMap = ctx.CaptureSideChannelTextureRef(material, "_ThicknessMap", VpeColorSpaces.Linear),

				Hdrp = new VpeHdrpLitHints {
					TexWorldScale = SafeGetFloat(material, "_TexWorldScale", 1f),
					InvTilingScale = SafeGetFloat(material, "_InvTilingScale", 1f),
					GeometricSpecularAa = SafeGetFloat(material, "_EnableGeometricSpecularAA", 0f) > 0.5f,
					SpecularAaScreenSpaceVariance = SafeGetFloat(material, "_SpecularAAScreenSpaceVariance", 0f),
					SpecularAaThreshold = SafeGetFloat(material, "_SpecularAAThreshold", 0f),
					SupportDecals = SafeGetFloat(material, "_SupportDecals", 1f) > 0.5f
						&& !material.IsKeywordEnabled("_DISABLE_DECALS"),
					CullMode = Mathf.RoundToInt(SafeGetFloat(material, "_CullMode", -1f)),
					CullModeForward = Mathf.RoundToInt(SafeGetFloat(material, "_CullModeForward", -1f)),
					OpaqueCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_OpaqueCullMode", -1f)),
					TransparentCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentCullMode", -1f)),
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
					TransmissionEnable = SafeGetFloat(material, "_TransmissionEnable", -1f),
					TransmissionMask = SafeGetFloat(material, "_TransmissionMask", -1f),
					DiffusionProfileHash = SafeGetFloat(material, "_DiffusionProfileHash", 0f),
					DiffusionProfileAsset = SafeGetVector(material, "_DiffusionProfileAsset", Vector4.zero),
					EmissiveExposureWeight = SafeGetFloat(material, "_EmissiveExposureWeight", 1f),
				},
			};

			return new VpeMaterialProfile {
				Type = VpeMaterialTypes.Lit,
				Lit = lit,
			};
		}

		private static VpeMaterialProfile TranslateVpeMetalShaderGraph(Material material, CaptureContext ctx)
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
			var lit = new VpeLitProfile {
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
				Emissive = new VpeEmissive {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureSideChannelTextureRef(material, "_EmissiveColorMap", VpeColorSpaces.SRgb),
					UseIntensity = SafeGetFloat(material, "_UseEmissiveIntensity", 0f) > 0.5f,
					Intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f),
					IntensityUnit = HdrpEmissiveIntensityUnitToString(
						SafeGetFloat(material, "_EmissiveIntensityUnit", 0f)),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
				DoubleSidedGi = material.doubleSidedGI,
				HasTransmission = material.IsKeywordEnabled("_MATERIAL_FEATURE_TRANSMISSION")
					|| Mathf.Approximately(SafeGetFloat(material, "_MaterialID", 1f), 5f),

				Hdrp = new VpeHdrpLitHints {
					TexWorldScale = SafeGetFloat(material, "_TexWorldScale", SafeGetFloat(source, "_TexWorldScale", 1f)),
					InvTilingScale = SafeGetFloat(material, "_InvTilingScale", SafeGetFloat(source, "_InvTilingScale", 1f)),
					GeometricSpecularAa = SafeGetFloat(material, "_EnableGeometricSpecularAA", SafeGetFloat(source, "_EnableGeometricSpecularAA", 0f)) > 0.5f,
					SpecularAaScreenSpaceVariance = SafeGetFloat(material, "_SpecularAAScreenSpaceVariance", SafeGetFloat(source, "_SpecularAAScreenSpaceVariance", 0f)),
					SpecularAaThreshold = SafeGetFloat(material, "_SpecularAAThreshold", SafeGetFloat(source, "_SpecularAAThreshold", 0f)),
					CullMode = Mathf.RoundToInt(SafeGetFloat(material, "_CullMode", -1f)),
					CullModeForward = Mathf.RoundToInt(SafeGetFloat(material, "_CullModeForward", -1f)),
					OpaqueCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_OpaqueCullMode", -1f)),
					TransparentCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentCullMode", -1f)),
					DisableSsr = material.IsKeywordEnabled("_DISABLE_SSR")
						|| SafeGetFloat(material, "_ReceivesSSR", 1f) < 0.5f,
					RayTracing = Mathf.RoundToInt(SafeGetFloat(material, "_RayTracing", -1f)),
					MaterialId = Mathf.RoundToInt(SafeGetFloat(material, "_MaterialID", -1f)),
					RenderQueueOverride = material.renderQueue,
					TransmissionEnable = SafeGetFloat(material, "_TransmissionEnable", -1f),
					TransmissionMask = SafeGetFloat(material, "_TransmissionMask", -1f),
				},
			};

			return new VpeMaterialProfile {
				Type = VpeMaterialTypes.Metal,
				Metal = CreateShaderGraphProfile(material),
				Lit = lit,
			};
		}

		private static VpeMaterialProfile TranslateVpeDmdShaderGraph(Material material)
		{
			return new VpeMaterialProfile {
				Type = VpeMaterialTypes.Dmd,
				Dmd = CreateShaderGraphProfile(material),
			};
		}

		private static VpeShaderGraphProfile CreateShaderGraphProfile(Material material)
		{
			var source = ResolveSourceMaterialAsset(material);
			return new VpeShaderGraphProfile {
				TemplateName = source ? source.name : material.name,
			};
		}

		private static VpeMaterialProfile TranslateVpeRubberShaderGraph(Material material, CaptureContext ctx)
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
			var lit = new VpeLitProfile {
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
				NormalMap = normalMap,
				Emissive = new VpeEmissive {
					Color = SafeGetColor(material, "_EmissiveColor", Color.black),
					HasLdrColor = HasAnyProperty(material, "_EmissiveColorLDR", "_EmissionColor"),
					LdrColor = ResolveHdrpEmissiveLdrColor(material),
					Texture = ctx.CaptureSideChannelTextureRef(material, "_EmissiveColorMap", VpeColorSpaces.SRgb),
					UseIntensity = SafeGetFloat(material, "_UseEmissiveIntensity", 0f) > 0.5f,
					Intensity = SafeGetFloat(material, "_EmissiveIntensity", 0f),
					IntensityUnit = HdrpEmissiveIntensityUnitToString(
						SafeGetFloat(material, "_EmissiveIntensityUnit", 0f)),
				},
				SurfaceType = HdrpSurfaceTypeToString(
					SafeGetFloat(material, "_SurfaceType", 0f),
					SafeGetFloat(material, "_AlphaCutoffEnable", 0f)),
				AlphaCutoff = SafeGetFloat(material, "_AlphaCutoff", 0.5f),
				DoubleSided = SafeGetFloat(material, "_DoubleSidedEnable", 0f) > 0.5f,
				DoubleSidedGi = material.doubleSidedGI,

				Hdrp = new VpeHdrpLitHints {
					TexWorldScale = SafeGetFloat(material, "_TexWorldScale", SafeGetFloat(source, "_TexWorldScale", 1f)),
					InvTilingScale = SafeGetFloat(material, "_InvTilingScale", SafeGetFloat(source, "_InvTilingScale", 1f)),
					GeometricSpecularAa = SafeGetFloat(material, "_EnableGeometricSpecularAA", SafeGetFloat(source, "_EnableGeometricSpecularAA", 0f)) > 0.5f,
					SpecularAaScreenSpaceVariance = SafeGetFloat(material, "_SpecularAAScreenSpaceVariance", SafeGetFloat(source, "_SpecularAAScreenSpaceVariance", 0f)),
					SpecularAaThreshold = SafeGetFloat(material, "_SpecularAAThreshold", SafeGetFloat(source, "_SpecularAAThreshold", 0f)),
					CullMode = Mathf.RoundToInt(SafeGetFloat(material, "_CullMode", -1f)),
					CullModeForward = Mathf.RoundToInt(SafeGetFloat(material, "_CullModeForward", -1f)),
					OpaqueCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_OpaqueCullMode", -1f)),
					TransparentCullMode = Mathf.RoundToInt(SafeGetFloat(material, "_TransparentCullMode", -1f)),
					DisableSsr = material.IsKeywordEnabled("_DISABLE_SSR")
						|| SafeGetFloat(material, "_ReceivesSSR", 1f) < 0.5f,
					RayTracing = Mathf.RoundToInt(SafeGetFloat(material, "_RayTracing", -1f)),
					MaterialId = Mathf.RoundToInt(SafeGetFloat(material, "_MaterialID", -1f)),
					RenderQueueOverride = material.renderQueue,
				},
			};

			return new VpeMaterialProfile {
				Type = VpeMaterialTypes.Rubber,
				Rubber = CreateShaderGraphProfile(material),
				Lit = lit,
			};
		}

		private static VpeMaterialProfile TranslateHdrpDecal(Material material, CaptureContext ctx)
		{
			var decal = new VpeDecalProfile {
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

			return new VpeMaterialProfile {
				Type = VpeMaterialTypes.Decal,
				Decal = decal,
			};
		}

		private static VpeMaterialProfile TranslateHdrpUnlit(Material material, CaptureContext ctx)
		{
			var unlit = new VpeUnlitProfile {
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
			return new VpeMaterialProfile {
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

			// Captured material types carry all their texture data in the package's source layer,
			// so the glTF export must not duplicate any of it. Stripping every texture also stops
			// glTFast from encoding hundreds of megabytes of images into table.glb, which used to
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

		private static VpeTextureRef CaptureShaderGraphTextureRef(
			CaptureContext ctx,
			Material material,
			Material source,
			string property,
			string colorSpace)
		{
			return ctx.CaptureSideChannelTextureRef(material, property, colorSpace)
				?? ctx.CaptureSideChannelTextureRef(source, property, colorSpace);
		}

		private static VpeNormalMapRef CaptureShaderGraphNormalMapRef(
			CaptureContext ctx,
			Material material,
			Material source,
			string property,
			float strength)
		{
			return ctx.CaptureSideChannelNormalMapRef(material, property, strength)
				?? ctx.CaptureSideChannelNormalMapRef(source, property, strength);
		}

		private static void ApplyTextureTransform(VpeTextureRef texture, Vector2 scale, Vector2 offset)
		{
			if (texture == null) {
				return;
			}

			texture.Scale = scale;
			texture.Offset = offset;
		}

		private static void ApplyTextureTransform(VpeNormalMapRef texture, Vector2 scale, Vector2 offset)
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
			private readonly Dictionary<Texture2D, VpeTexture> _assetsByTexture = new();
			private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
			private readonly Dictionary<string, byte[]> _textureBlobs = new(StringComparer.Ordinal);
			private readonly Dictionary<string, UnsupportedShaderUsage> _unsupportedShaders = new(StringComparer.Ordinal);

			public IReadOnlyDictionary<string, byte[]> TextureBlobs => _textureBlobs;

			public VpeTexture[] BuildTextureAssets()
			{
				var assets = new VpeTexture[_assetsByTexture.Count];
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
					$"Skipped material translation for {totalMaterials} material(s) across {_unsupportedShaders.Count} unsupported shader(s). " +
					$"These materials will fall back to the glTF-imported material at runtime. " +
					$"Shaders: {string.Join("; ", shaderSummaries)}");
			}

			// Captures a texture reference whose pixel data must be shipped in the source layer
			// (i.e. is not losslessly reproduced by the glb). Use for HDRP-specific packings like
			// MaskMap where channel semantics differ from glTF's PBR textures.
			public VpeTextureRef CaptureSideChannelTextureRef(Material material, string property, string colorSpace, VpeTextureRef fallback = null)
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

				return new VpeTextureRef {
					TextureId = asset.Id,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
				};
			}

			private VpeTextureRef CaptureSerializedSideChannelTextureRef(Material material, string property, string colorSpace, VpeTextureRef fallback)
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

					return new VpeTextureRef {
						TextureId = asset.Id,
						Offset = value.FindPropertyRelative("m_Offset")?.vector2Value ?? Vector2.zero,
						Scale = value.FindPropertyRelative("m_Scale")?.vector2Value ?? Vector2.one,
					};
				}

				return CaptureYamlSideChannelTextureRef(material, property, colorSpace, fallback);
			}

			private VpeTextureRef CaptureYamlSideChannelTextureRef(Material material, string property, string colorSpace, VpeTextureRef fallback)
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

				return new VpeTextureRef {
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

			// Captures tiling only — no TextureId, no source bytes. Pixel data is read at
			// runtime from the gltFast-imported material by matching property-name aliases.
			public VpeTextureRef CaptureImportedTextureRef(Material material, string property)
			{
				if (!material.HasProperty(property)) {
					return null;
				}
				var texture = material.GetTexture(property);
				if (!texture) {
					return null;
				}
				return new VpeTextureRef {
					TextureId = null,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
				};
			}

			public VpeNormalMapRef CaptureSideChannelNormalMapRef(Material material, string property, float strength)
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

				return new VpeNormalMapRef {
					TextureId = asset.Id,
					Offset = material.GetTextureOffset(property),
					Scale = material.GetTextureScale(property),
					Strength = strength,
					// The package carries plain-RGB source normals. The runtime cook re-packs them
					// into HDRP's AG layout for its local cache; the uncached fallback path keeps
					// the runtime repack behavior.
					Packing = VpeNormalPackings.Rgb,
				};
			}

			public VpeTextureRef CaptureTextureAssetRef(string assetPath, string colorSpace)
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

				return new VpeTextureRef {
					TextureId = asset.Id,
					Offset = Vector2.zero,
					Scale = Vector2.one,
				};
			}

			private VpeTexture GetOrCaptureAsset(Texture2D texture, string colorSpace)
			{
				if (_assetsByTexture.TryGetValue(texture, out var existing)) {
					return existing;
				}

				var requestedColorSpace = string.Equals(colorSpace, VpeColorSpaces.Linear, StringComparison.OrdinalIgnoreCase)
					? VpeColorSpaces.Linear
					: VpeColorSpaces.SRgb;
				var linear = requestedColorSpace == VpeColorSpaces.Linear;

				var id = BuildId(texture);
				var asset = new VpeTexture {
					Id = id,
					ColorSpace = requestedColorSpace,
					WrapMode = VpeMaterialEnums.ToWrapMode(texture.wrapMode),
					FilterMode = VpeMaterialEnums.ToFilterMode(texture.filterMode),
					AnisoLevel = Mathf.Max(1, texture.anisoLevel),
					GenerateMipMaps = true,
					SourceName = texture.name,
					// Imported dimensions, i.e. the authored intent after any importer max-size
					// clamp. The runtime cook downsizes larger source files to fit these.
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
					asset.WrapMode = VpeMaterialEnums.ToWrapMode(importer.wrapMode);
					asset.FilterMode = VpeMaterialEnums.ToFilterMode(importer.filterMode);
				}

				// The package carries the lossless source layer: the original asset file bytes,
				// untouched, whenever the source is a format the runtime cook can decode. Anything
				// else (no source file, runtime-generated, exotic formats) falls back to a lossless
				// PNG of the imported pixels. GPU-ready payloads are never written into the package;
				// the player cooks and caches them locally.
				byte[] blobData = null;
				string extension = null;
				if (!string.IsNullOrEmpty(assetPath) && File.Exists(assetPath)) {
					var sourceExtension = Path.GetExtension(assetPath);
					if (string.Equals(sourceExtension, ".png", StringComparison.OrdinalIgnoreCase)) {
						blobData = ReadPngForPackaging(assetPath);
						asset.MimeType = "image/png";
						extension = ".png";
					} else if (string.Equals(sourceExtension, ".jpg", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(sourceExtension, ".jpeg", StringComparison.OrdinalIgnoreCase)) {
						blobData = File.ReadAllBytes(assetPath);
						asset.MimeType = "image/jpeg";
						extension = ".jpg";
					}
				}
				if (blobData == null) {
					if (!HdrpMaterialTextureEncoder.TryEncode(texture, linear, out blobData)) {
						return null;
					}
					asset.MimeType = "image/png";
					extension = ".png";
				}
				asset.FileName = BuildFileName(id, extension);

				_assetsByTexture[texture] = asset;
				_textureBlobs[asset.FileName] = blobData;
				return asset;
			}

			// Reads a source PNG for packing. 8-bit PNGs are packed untouched (no re-encode). 16-bit
			// PNGs are downconverted to 8-bit: the player cook produces 8-bit BC7 regardless, so the
			// extra precision is dead weight that roughly doubles file size and decode time (16-bit PNG
			// decode is the cook's main-thread bottleneck — the 50 MB cabinet normals). Re-encode loss
			// is acceptable here per the load-time tradeoff (decided 2026-06-13).
			private static byte[] ReadPngForPackaging(string assetPath)
			{
				var bytes = File.ReadAllBytes(assetPath);
				if (!IsPng16Bit(bytes)) {
					return bytes;
				}

				Texture2D tmp = null;
				try {
					tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear: true) { hideFlags = HideFlags.HideAndDontSave };
					// LoadImage stores the decoded bytes (16->8 high-byte downconvert) and EncodeToPNG
					// writes them back unchanged — no gamma shift, just a bit-depth reduction.
					if (ImageConversion.LoadImage(tmp, bytes, markNonReadable: false)) {
						var reduced = ImageConversion.EncodeToPNG(tmp);
						if (reduced != null && reduced.Length > 0) {
							return reduced;
						}
					}
				} catch (Exception ex) {
					Logger.Warn(ex, $"HdrpMaterialTranslator: failed downconverting 16-bit PNG '{assetPath}'; packing original.");
				} finally {
					if (tmp) {
						UnityEngine.Object.DestroyImmediate(tmp);
					}
				}
				return bytes;
			}

			// PNG layout: 8-byte signature, then the IHDR chunk (4 length + 4 "IHDR" + 4 width + 4
			// height + 1 bit depth). Bit depth therefore sits at byte 24.
			private static bool IsPng16Bit(byte[] png)
			{
				return png != null && png.Length > 25
					&& png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47
					&& png[24] == 16;
			}

			// Texture names are not unique across a table; two distinct textures sharing a name
			// must still get distinct ids (and file names), otherwise one would shadow the other
			// in the payload's texture table.
			private string BuildId(Texture2D texture)
			{
				var raw = string.IsNullOrWhiteSpace(texture.name) ? "tex" : texture.name;
				// Normalize so the id is stable across exports regardless of editor instance suffixes.
				var id = VpeMaterialNameUtil.NormalizeTextureName(raw);
				if (string.IsNullOrWhiteSpace(id)) {
					id = "tex";
				}
				if (_usedIds.Add(id)) {
					return id;
				}
				var n = 2;
				string candidate;
				do {
					candidate = $"{id}-{n++}";
				} while (!_usedIds.Add(candidate));
				return candidate;
			}

			private string BuildFileName(string id, string extension)
			{
				var invalid = Path.GetInvalidFileNameChars();
				var chars = id.ToCharArray();
				for (var i = 0; i < chars.Length; i++) {
					if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\') {
						chars[i] = '_';
					}
				}
				var stem = new string(chars);
				var fileName = $"{stem}{extension}";
				// Sanitization can collapse two distinct ids onto the same file name.
				var n = 2;
				while (_textureBlobs.ContainsKey(fileName)) {
					fileName = $"{stem}-{n++}{extension}";
				}
				return fileName;
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
	internal static class HdrpMaterialTranslatorRegistration
	{
		static HdrpMaterialTranslatorRegistration()
		{
			VpeMaterialTranslator.Register(new Adapter());
		}

		private sealed class Adapter : IVpeMaterialTranslator, IVpeMaterialGltfExportPreprocessor
		{
			public VpeMaterialCaptureResult Capture(Transform tableRoot, IEnumerable<Renderer> renderers, Func<Transform, string> nodeId)
			{
				return HdrpMaterialTranslator.Capture(tableRoot, renderers, nodeId);
			}

			public IDisposable PrepareGltfExport(IEnumerable<Renderer> renderers)
			{
				return HdrpMaterialTranslator.PrepareGltfExport(renderers);
			}
		}
	}
}
