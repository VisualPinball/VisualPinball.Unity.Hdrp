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
using NLog;
using UnityEngine;
using VisualPinball.Unity;
using Logger = NLog.Logger;

namespace VisualPinball.Engine.Unity.Hdrp
{
	// Registers the Player's HDRP resolver with VpeMaterialResolver before any scene loads.
	// Template materials live under Assets/Runtime/Resources/Materials/; Resources.Load pulls them
	// into the build automatically, which is exactly the leverage we need — the template's keyword
	// combination is what the build compiler keeps on HDRP/Lit.
	public static class VpeMaterialResolverBootstrap
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		private const string LitOpaqueTemplatePath = "Materials/VpeLitOpaqueTemplate";
		private const string LitTransparentTemplatePath = "Materials/VpeLitTransparentTemplate";
		private const string LitTranslucentThinTemplatePath = "Materials/VpeLitTranslucentThinTemplate";
		private const string LitTranslucentPlanarTemplatePath = "Materials/VpeLitTranslucentPlanarTemplate";
		private const string LitTranslucentSphereTemplatePath = "Materials/VpeLitTranslucentSphereTemplate";
		private const string FabricSilkTemplatePath = "Materials/VpeLitFabricSilkTemplate";
		private const string DecalTemplatePath = "Materials/VpeDecalTemplate";
		private const string InsertHybridTemplatePath = "Materials/VpeInsertHybridTemplate";
		private const string InsertCoverTemplatePath = "Materials/VpeInsertCoverTemplate";
		private const string MetalScratchedOverridePath = "Materials/VpeMeasured/MetalScratched";
		private const string RubberDirtWhiteOverridePath = "Materials/VpeMeasured/RubberDirt White";
		private const string DotMatrixDisplayOverridePath = "Materials/VpeMeasured/Dot Matrix Display (SRP)";

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register()
		{
			// Fully qualified: VisualPinball.Engine has a .Resources sub-namespace that shadows
			// UnityEngine.Resources for code living under VisualPinball.Engine.Player.
			var litOpaque = UnityEngine.Resources.Load<Material>(LitOpaqueTemplatePath);
			var litTransparent = UnityEngine.Resources.Load<Material>(LitTransparentTemplatePath);
			var litTranslucentThin = UnityEngine.Resources.Load<Material>(LitTranslucentThinTemplatePath);
			var litTranslucentPlanar = UnityEngine.Resources.Load<Material>(LitTranslucentPlanarTemplatePath);
			var litTranslucentSphere = UnityEngine.Resources.Load<Material>(LitTranslucentSphereTemplatePath);
			var fabricSilk = UnityEngine.Resources.Load<Material>(FabricSilkTemplatePath);
			var decalTemplate = UnityEngine.Resources.Load<Material>(DecalTemplatePath);
			var insertHybrid = UnityEngine.Resources.Load<Material>(InsertHybridTemplatePath);
			var insertCover = UnityEngine.Resources.Load<Material>(InsertCoverTemplatePath);
			var materialOverrides = new Dictionary<string, Material>(System.StringComparer.Ordinal);
			RegisterMaterialOverride(materialOverrides, "MetalScratched", MetalScratchedOverridePath);
			RegisterMaterialOverride(materialOverrides, "RubberDirt White", RubberDirtWhiteOverridePath);
			RegisterMaterialOverride(materialOverrides, "Dot Matrix Display (SRP)", DotMatrixDisplayOverridePath);
			if (!litOpaque) {
				// the templates ship with this package, so a missing one is a packaging bug
				Logger.Error("Could not load the material template {Path:l} from Resources. Imported tables will fall back to the glTF-imported materials.",
					LitOpaqueTemplatePath);
				return;
			}
			WarnIfMissing(litTransparent, LitTransparentTemplatePath, "Transparent surfaces will fall back to the opaque template.");
			WarnIfMissing(litTranslucentThin, LitTranslucentThinTemplatePath, "Thin translucent (ramp plastics) will fall back to plain transparent.");
			WarnIfMissing(litTranslucentPlanar, LitTranslucentPlanarTemplatePath, "Planar translucent (inserts, hard plastics) will fall back to plain transparent.");
			WarnIfMissing(litTranslucentSphere, LitTranslucentSphereTemplatePath, "Sphere translucent (posts, rounded plastics) will fall back to planar/thin.");
			WarnIfMissing(fabricSilk, FabricSilkTemplatePath, "vpe.fabric.silk profiles will fall back to glTF-imported materials.");
			WarnIfMissing(decalTemplate, DecalTemplatePath, "vpe.decal profiles will fall back to glTF-imported materials.");
			WarnIfMissing(insertHybrid, InsertHybridTemplatePath, "Insert bodies will use their glTF materials.");
			WarnIfMissing(insertCover, InsertCoverTemplatePath, "Insert covers will use their glTF materials.");
			if (Logger.IsDebugEnabled) {
				Logger.Debug(
					"Material templates loaded: opaque={Opaque}, transparent={Transparent}, translucent-thin={Thin}, translucent-planar={Planar}, " +
					"translucent-sphere={Sphere}, fabric-silk={Silk}, decal={Decal}, insert-hybrid={InsertHybrid}, insert-cover={InsertCover}, {Overrides} override(s).",
					litOpaque.name, NameOf(litTransparent), NameOf(litTranslucentThin), NameOf(litTranslucentPlanar), NameOf(litTranslucentSphere),
					NameOf(fabricSilk), NameOf(decalTemplate), NameOf(insertHybrid), NameOf(insertCover), materialOverrides.Count);
			}
			VpeMaterialResolver.Register(new HdrpMaterialResolver(
				litOpaque,
				litTransparent,
				litTranslucentThin,
				litTranslucentPlanar,
				litTranslucentSphere,
				fabricSilk,
				decalTemplate,
				materialOverrides,
				insertHybrid,
				insertCover
			));
		}

		private static void WarnIfMissing(Material template, string path, string consequence)
		{
			if (!template) {
				Logger.Warn("Could not load the material template {Path:l} from Resources. {Consequence:l}", path, consequence);
			}
		}

		private static string NameOf(Material material) => material ? material.name : "<missing>";

		private static void RegisterMaterialOverride(Dictionary<string, Material> overrides, string profileName, string resourcePath)
		{
			var material = UnityEngine.Resources.Load<Material>(resourcePath);
			if (material) {
				overrides[profileName] = material;
			}
		}
	}
}
