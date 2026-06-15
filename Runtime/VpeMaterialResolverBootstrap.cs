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
using UnityEngine;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	// Registers the Player's HDRP resolver with VpeMaterialResolver before any scene loads.
	// Template materials live under Assets/Runtime/Resources/Materials/; Resources.Load pulls them
	// into the build automatically, which is exactly the leverage we need — the template's keyword
	// combination is what the build compiler keeps on HDRP/Lit.
	public static class VpeMaterialResolverBootstrap
	{
		private const string LitOpaqueTemplatePath = "Materials/VpeLitOpaqueTemplate";
		private const string LitTransparentTemplatePath = "Materials/VpeLitTransparentTemplate";
		private const string LitTranslucentThinTemplatePath = "Materials/VpeLitTranslucentThinTemplate";
		private const string LitTranslucentPlanarTemplatePath = "Materials/VpeLitTranslucentPlanarTemplate";
		private const string LitTranslucentSphereTemplatePath = "Materials/VpeLitTranslucentSphereTemplate";
		private const string FabricSilkTemplatePath = "Materials/VpeLitFabricSilkTemplate";
		private const string DecalTemplatePath = "Materials/VpeDecalTemplate";
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
			var materialOverrides = new Dictionary<string, Material>(System.StringComparer.Ordinal);
			RegisterMaterialOverride(materialOverrides, "MetalScratched", MetalScratchedOverridePath);
			RegisterMaterialOverride(materialOverrides, "RubberDirt White", RubberDirtWhiteOverridePath);
			RegisterMaterialOverride(materialOverrides, "Dot Matrix Display (SRP)", DotMatrixDisplayOverridePath);
			if (!litOpaque) {
				Debug.LogError(
					$"VpeMaterialResolverBootstrap: could not load '{LitOpaqueTemplatePath}' from Resources. " +
					"Imported tables will fall back to the glTF-imported materials.");
				return;
			}
			if (!litTransparent) {
				Debug.LogWarning(
					$"VpeMaterialResolverBootstrap: could not load '{LitTransparentTemplatePath}' from Resources. " +
					"Transparent surfaces will fall back to the opaque template.");
			}
			if (!litTranslucentThin) {
				Debug.LogWarning(
					$"VpeMaterialResolverBootstrap: could not load '{LitTranslucentThinTemplatePath}' from Resources. " +
					"Thin translucent (ramp plastics) will fall back to plain transparent.");
			}
			if (!litTranslucentPlanar) {
				Debug.LogWarning(
					$"VpeMaterialResolverBootstrap: could not load '{LitTranslucentPlanarTemplatePath}' from Resources. " +
					"Planar translucent (inserts, hard plastics) will fall back to plain transparent.");
			}
			if (!litTranslucentSphere) {
				Debug.LogWarning(
					$"VpeMaterialResolverBootstrap: could not load '{LitTranslucentSphereTemplatePath}' from Resources. " +
					"Sphere translucent (posts, rounded plastics) will fall back to planar/thin.");
			}
			if (!fabricSilk) {
				Debug.LogWarning(
					$"VpeMaterialResolverBootstrap: could not load '{FabricSilkTemplatePath}' from Resources. " +
					"vpe.fabric.silk profiles will fall back to glTF-imported materials.");
			}
			if (!decalTemplate) {
				Debug.LogWarning(
					$"VpeMaterialResolverBootstrap: could not load '{DecalTemplatePath}' from Resources. " +
					"vpe.decal profiles will fall back to glTF-imported materials.");
			}
			Debug.Log(
				$"VpeMaterialResolverBootstrap: templates loaded. " +
				$"opaque={litOpaque.name}, " +
				$"transparent={(litTransparent ? litTransparent.name : "<missing>")}, " +
				$"translucent-thin={(litTranslucentThin ? litTranslucentThin.name : "<missing>")}, " +
				$"translucent-planar={(litTranslucentPlanar ? litTranslucentPlanar.name : "<missing>")}, " +
				$"translucent-sphere={(litTranslucentSphere ? litTranslucentSphere.name : "<missing>")}, " +
				$"fabric-silk={(fabricSilk ? fabricSilk.name : "<missing>")}, " +
				$"decal={(decalTemplate ? decalTemplate.name : "<missing>")}.");
			VpeMaterialResolver.Register(new HdrpMaterialResolver(
				litOpaque,
				litTransparent,
				litTranslucentThin,
				litTranslucentPlanar,
				litTranslucentSphere,
				fabricSilk,
				decalTemplate,
				materialOverrides
			));
		}

		private static void RegisterMaterialOverride(Dictionary<string, Material> overrides, string profileName, string resourcePath)
		{
			var material = UnityEngine.Resources.Load<Material>(resourcePath);
			if (material) {
				overrides[profileName] = material;
			}
		}
	}
}
