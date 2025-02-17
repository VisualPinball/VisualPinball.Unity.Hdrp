// Visual Pinball Engine
// Copyright (C) 2020 freezy and VPE Team
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

// ReSharper disable StringLiteralTypo
// ReSharper disable CheckNamespace

using UnityEngine;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	public class MaterialAdapter : IMaterialAdapter
	{
		#region Shader Properties

		private static readonly int SurfaceType = Shader.PropertyToID("_SurfaceType");
		private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
		private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
		private static readonly int DoubleSidedEnable = Shader.PropertyToID("_DoubleSidedEnable");
		private static readonly int DoubleSidedNormalMode = Shader.PropertyToID("_DoubleSidedNormalMode");
		private static readonly int CullMode = Shader.PropertyToID("_CullMode");
		private static readonly int CullModeForward = Shader.PropertyToID("_CullModeForward");
		private static readonly int TransparentDepthPrepassEnable = Shader.PropertyToID("_TransparentDepthPrepassEnable");
		private static readonly int AlphaDstBlend = Shader.PropertyToID("_AlphaDstBlend");
		private static readonly int ZTestModeDistortion = Shader.PropertyToID("_ZTestModeDistortion");
		private static readonly int AlphaCutoff = Shader.PropertyToID("_AlphaCutoff");
		private static readonly int AlphaCutoffEnable = Shader.PropertyToID("_AlphaCutoffEnable");
		private static readonly int NormalMap = Shader.PropertyToID("_NormalMap");
		private static readonly int Metallic = Shader.PropertyToID("_Metallic");

		#endregion

		public void SetOpaque(GameObject gameObject)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;

			material.SetFloat(SurfaceType, 0);

			material.SetFloat(DstBlend, 0);
			material.SetFloat(ZWrite, 1);

			material.DisableKeyword("_ALPHATEST_ON");
			material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
			material.DisableKeyword("_BLENDMODE_PRE_MULTIPLY");
			material.DisableKeyword("_BLENDMODE_PRESERVE_SPECULAR_LIGHTING");
		}

		public void SetDoubleSided(GameObject gameObject)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;

			material.EnableKeyword("_DOUBLESIDED_ON");
			material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");

			material.SetInt(DoubleSidedEnable, 1);
			material.SetInt(DoubleSidedNormalMode, 1);

			material.SetInt(CullMode, 0);
			material.SetInt(CullModeForward, 0);
		}

		public void SetTransparentDepthPrepassEnabled(GameObject gameObject)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;

			material.EnableKeyword("_DISABLE_SSR_TRANSPARENT");

			material.SetInt(TransparentDepthPrepassEnable, 1);
			material.SetInt(AlphaDstBlend, 10);
			material.SetInt(ZTestModeDistortion, 4);

			material.SetShaderPassEnabled("TransparentDepthPrepass", true);
			material.SetShaderPassEnabled("RayTracingPrepass", true);

		}

		public void SetAlphaCutOff(GameObject gameObject, float value)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;

			// enable the property
			SetAlphaCutOffEnabled(gameObject);

			// set the cut-off value
			material.SetFloat(AlphaCutoff, value);
		}

		public void SetAlphaCutOffEnabled(GameObject gameObject)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;

			material.EnableKeyword("_ALPHATEST_ON");
			material.SetInt(AlphaCutoffEnable, 1);
		}

		public void SetNormalMapDisabled(GameObject gameObject)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;

			material.SetTexture(NormalMap, null);
			material.DisableKeyword("_NORMALMAP");
		}

		public void SetMetallic(GameObject gameObject, float value)
		{
			var material = gameObject.GetComponent<Renderer>().sharedMaterial;
			material.SetFloat(Metallic, value);
		}
	}
}
