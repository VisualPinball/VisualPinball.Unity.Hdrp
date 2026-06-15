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

// ReSharper disable CheckNamespace

using GLTFast.Materials;
using UnityEngine;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	public sealed class HdrpGltfMaterialGenerator : HighDefinitionRPMaterialGenerator
	{
		private static Shader _hdrpLit;

		private static Shader HdrpLit
		{
			get
			{
				if (!_hdrpLit) {
					_hdrpLit = Shader.Find("HDRP/Lit");
				}
				return _hdrpLit;
			}
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void Register()
		{
			RuntimeGltfMaterialGenerator.Factory = () => new HdrpGltfMaterialGenerator();
		}

		// gltFast's own glTF HDRP shadergraphs (glTF-pbrMetallicRoughness[StackLit]) ship broken
		// ray-tracing passes that fail to compile in player builds, so we keep them out of the build.
		// Every imported glTF material is replaced at runtime by HdrpMaterialResolver (matched by
		// name), so what gltFast builds here is a transient placeholder that only needs to be a
		// valid, correctly-named HDRP material. Use stock HDRP/Lit; no gltFast shadergraph required.
		protected override Shader GetMetallicShader(MetallicShaderFeatures features)
		{
			return HdrpLit ? HdrpLit : base.GetMetallicShader(features);
		}

		protected override Material GenerateDefaultMaterial(bool pointsSupport = false)
		{
			return HdrpLit
				? new Material(HdrpLit) { name = DefaultMaterialName }
				: base.GenerateDefaultMaterial(pointsSupport);
		}
	}
}
