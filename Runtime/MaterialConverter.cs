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
// ReSharper disable UnusedType.Global
// ReSharper disable CheckNamespace

using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using NLog;
using UnityEditor;
using UnityEngine;
using VisualPinball.Engine.VPT;
using VisualPinball.Unity;
using Logger = NLog.Logger;
using Material = UnityEngine.Material;
using Object = UnityEngine.Object;

namespace VisualPinball.Engine.Unity.Hdrp
{
	public class MaterialConverter : IMaterialConverter
	{
		public Material DotMatrixDisplay => UnityEngine.Resources.Load<Material>("Materials/Dot Matrix Display (SRP)");
		public Material SegmentDisplay => UnityEngine.Resources.Load<Material>("Materials/Segment Display (SRP)");

		public int NormalMapProperty => NormalMap;

		#region Shader Properties

		private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
		private static readonly int Metallic = Shader.PropertyToID("_Metallic");
		private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
		private static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap");
		private static readonly int NormalMap = Shader.PropertyToID("_NormalMap");
		private static readonly int UVChannelVertices = Shader.PropertyToID("_UVChannelVertices");
		private static readonly int UVChannelNormals = Shader.PropertyToID("_UVChannelNormals");
		private static readonly int DiffusionProfileAsset = Shader.PropertyToID("_DiffusionProfileAsset");
		private static readonly int DiffusionProfileHash = Shader.PropertyToID("_DiffusionProfileHash");
		private static readonly int MaterialID = Shader.PropertyToID("_MaterialID");
		private static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");

		#endregion

		private const string DiffusionProfilePlastic = "Packages/org.visualpinball.unity.assetlibrary/Assets/Settings/DiffusionProfiles/Plastic/PET_PETG.asset";

		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();


		public Shader GetShader()
		{
			return Shader.Find("HDRP/Lit");
		}

		private Shader GetShader(PbrMaterial vpxMaterial)
		{
			return GetShader();
		}

		public static Material GetDefaultMaterial(BlendMode blendMode)
		{
			switch (blendMode)
			{
				case BlendMode.Opaque:
					return UnityEngine.Resources.Load<Material>("Materials/TableOpaque");
				case BlendMode.Cutout:
					return UnityEngine.Resources.Load<Material>("Materials/TableCutout");
				case BlendMode.Translucent:
					return UnityEngine.Resources.Load<Material>("Materials/TableTranslucent");
				default:
					throw new ArgumentOutOfRangeException( "Undefined blend mode " + blendMode);
			}
		}

		public Material CreateMaterial(PbrMaterial vpxMaterial, ITextureProvider textureProvider, StringBuilder debug = null)
		{
			Material defaultMaterial = GetDefaultMaterial(vpxMaterial.MapBlendMode);

			var unityMaterial = new Material(GetShader(vpxMaterial))
			{
				name = vpxMaterial.Id
			};

			unityMaterial.CopyPropertiesFromMaterial(defaultMaterial);
			unityMaterial.name = vpxMaterial.Id;

			// apply some basic manipulations to the color. this just makes very
			// very white colors be clipped to 0.8204 aka 204/255 is 0.8
			// this is to give room to lighting values. so there is more modulation
			// of brighter colors when being lit without blow outs too soon.
			var col = vpxMaterial.Color.ToUnityColor();
			if (vpxMaterial.Color.IsGray() && col.grayscale > 0.8)
			{
				debug?.AppendLine("Color manipulation performed, brightness reduced.");
				col.r = col.g = col.b = 0.8f;
			}


			if (vpxMaterial.MapBlendMode == BlendMode.Translucent)
			{
				col.a = Mathf.Min(1, Mathf.Max(0, vpxMaterial.Opacity));
			}
			unityMaterial.SetColor(BaseColor, col);

			// validate IsMetal. if true, set the metallic value.
			// found VPX authors setting metallic as well as translucent at the
			// same time, which does not render correctly in unity so we have
			// to check if this value is true and also if opacity <= 1.
			float metallicValue = 0f;
			if (vpxMaterial.IsMetal && (!vpxMaterial.IsOpacityActive || vpxMaterial.Opacity >= 1))
			{
				metallicValue = 1f;
				debug?.AppendLine("Metallic set to 1.");
			}

			unityMaterial.SetFloat(Metallic, metallicValue);

			SetSmoothness(unityMaterial, vpxMaterial.Roughness);

			// map
			if (vpxMaterial.HasMap && textureProvider != null) {
				unityMaterial.SetTexture(BaseColorMap,textureProvider.GetTexture(vpxMaterial.Map.Name));
			}

			// normal map
			if (vpxMaterial.HasNormalMap && textureProvider != null) {
				unityMaterial.EnableKeyword("_NORMALMAP");
				unityMaterial.EnableKeyword("_NORMALMAP_TANGENT_SPACE");

				unityMaterial.SetTexture( NormalMap, textureProvider.GetTexture(vpxMaterial.NormalMap.Name));
			}

			// diffusion profile
			if (vpxMaterial.DiffusionProfile != DiffusionProfileTemplate.None) {
				SetDiffusionProfile(unityMaterial, vpxMaterial.DiffusionProfile);
			}

			SetMaterialType(unityMaterial, vpxMaterial.MaterialType);

			return unityMaterial;
		}

		public void SetSmoothness(Material unityMaterial, float smoothness)
		{
			unityMaterial.SetFloat(Smoothness, smoothness);
		}

		public Material MergeMaterials(PbrMaterial vpxMaterial, Material texturedMaterial)
		{
			var nonTexturedMaterial = CreateMaterial(vpxMaterial, null, null);
			var mergedMaterial = new Material(GetShader());
			mergedMaterial.CopyPropertiesFromMaterial(texturedMaterial);

			mergedMaterial.name = nonTexturedMaterial.name;
			mergedMaterial.SetColor(BaseColor, nonTexturedMaterial.GetColor(BaseColor));
			mergedMaterial.SetFloat(Metallic, nonTexturedMaterial.GetFloat(Metallic));
			mergedMaterial.SetFloat(Smoothness, nonTexturedMaterial.GetFloat(Smoothness));

			return mergedMaterial;
		}

		public void SetDiffusionProfile(Material material, DiffusionProfileTemplate template)
		{
			#if UNITY_EDITOR

			var diffusionProfilePath = template switch {
				DiffusionProfileTemplate.Plastics => DiffusionProfilePlastic,
				_ => throw new ArgumentOutOfRangeException(nameof(template), template, "Invalid diffusion profile.")
			};

			// unity, why tf would you make DiffusionProfileSettings internal!?!
			var diffusionProfile = AssetDatabase.LoadAssetAtPath<Object>(diffusionProfilePath);

			if (diffusionProfile != null) {

				// need to get those through reflection..
				var profile = diffusionProfile.GetType().GetRuntimeFields().First(f => f.Name == "profile").GetValue(diffusionProfile);
				var profileHash = (uint)profile.GetType().GetRuntimeFields().First(f => f.Name == "hash").GetValue(profile);

				var guid = AssetDatabase.AssetPathToGUID(diffusionProfilePath);
				var newGuid = ConvertGUIDToVector4(guid);
				var hash = AsFloat(profileHash);

				// encode back GUID and it's hash
				material.SetVector(DiffusionProfileAsset, newGuid);
				material.SetFloat(DiffusionProfileHash, hash);

			} else {
				Logger.Warn($"Could not load diffusion profile at {diffusionProfilePath}");
			}

			#endif
		}

		public void SetMaterialType(Material material, MaterialType materialType)
		{
			material.SetFloat(MaterialID, (int)materialType);
		}

		public void SetEmissiveColor(MaterialPropertyBlock propBlock, Color color)
		{
			propBlock.SetColor(EmissiveColor, color);
		}

		public Color? GetEmissiveColor(Material material)
		{
			return material.GetColor(EmissiveColor);
		}

		private static Vector4 ConvertGUIDToVector4(string guid)
		{
			Vector4 vector;
			byte[]  bytes = new byte[16];

			for (int i = 0; i < 16; i++)
				bytes[i] = byte.Parse(guid.Substring(i * 2, 2), NumberStyles.HexNumber);

			unsafe
			{
				fixed (byte * b = bytes)
					vector = *(Vector4 *)b;
			}

			return vector;
		}

		private static float AsFloat(uint val) { unsafe { return *((float*)&val); } }
	}
}
