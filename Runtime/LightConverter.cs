﻿// Visual Pinball Engine
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

// ReSharper disable UnusedType.Global
// ReSharper disable CheckNamespace

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Engine.VPT.Light;
using VisualPinball.Unity;
using Light = UnityEngine.Light;

namespace VisualPinball.Engine.Unity.Hdrp
{
	public class LightConverter : ILightConverter
	{
		public void UpdateLight(Light light, LightData data, bool isInsert)
		{
			// retrieve hdrp light
			var hdLight = light.GetComponent<HDAdditionalLightData>();
			if (hdLight == null) {
				hdLight = light.gameObject.AddComponent<HDAdditionalLightData>();
				HDAdditionalLightData.InitDefaultHDAdditionalLightData(hdLight);
			}

			// color and position
			hdLight.color = data.Color2.ToUnityColor();
			if (!isInsert) {
				hdLight.intensity = data.Intensity / 4f;
				hdLight.range = data.Falloff * 0.001f;

				// TODO: vpe specific data for height
				light.transform.localPosition = new Vector3(0f, 0f, 25f);
			}

			hdLight.EnableShadows(false);
		}

		public void SetColor(Light light, Color color)
		{
			var hdLight = light.GetComponent<HDAdditionalLightData>();
			if (hdLight != null) {
				hdLight.color = color;
			}
		}

		public void SetIntensity(Light light, float intensityLumen)
		{
			var hdLight = light.GetComponent<HDAdditionalLightData>();
			if (hdLight != null) {
				hdLight.SetIntensity(intensityLumen, LightUnit.Lumen);
			}
		}

		public void SpotLight(Light light, float outer, float innerPercent)
		{
			var hdLight = light.GetComponent<HDAdditionalLightData>();
			if (hdLight != null) {
				hdLight.SetSpotAngle(outer, innerPercent);
			}
		}
	}
}
