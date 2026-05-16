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
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity.Editor;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	[InitializeOnLoad]
	internal static class HdrpPackageScreenshotEnvironmentRegistration
	{
		static HdrpPackageScreenshotEnvironmentRegistration()
		{
			PackageScreenshotEnvironmentProvider.Register(new Provider());
		}

		private sealed class Provider : IPackageScreenshotEnvironmentProvider
		{
			public IDisposable CreateEnvironmentScope(Transform tableRoot, Cubemap hdriCubemap)
			{
				return new TemporaryHdrpEnvironmentScope(tableRoot, hdriCubemap);
			}
		}
	}

	internal sealed class TemporaryHdrpEnvironmentScope : IDisposable
	{
		private readonly GameObject _directionalLightObject;

		public TemporaryHdrpEnvironmentScope(Transform tableRoot, Cubemap hdriCubemap)
		{
			_directionalLightObject = CloneSceneDirectionalLight(tableRoot);
		}

		public void Dispose()
		{
			if (_directionalLightObject) {
				UnityEngine.Object.DestroyImmediate(_directionalLightObject);
			}
		}

		private static GameObject CloneSceneDirectionalLight(Transform tableRoot)
		{
			var sourceLight = FindSceneDirectionalLight(tableRoot);
			if (!sourceLight) {
				return null;
			}

			var lightObject = new GameObject("Package Screenshot Directional Light") {
				hideFlags = HideFlags.HideAndDontSave,
			};
			lightObject.transform.SetPositionAndRotation(Vector3.zero, sourceLight.transform.rotation);

			var clonedLight = lightObject.AddComponent<Light>();
			EditorUtility.CopySerialized(sourceLight, clonedLight);
			clonedLight.enabled = true;

			var sourceHdLight = sourceLight.GetComponent<HDAdditionalLightData>();
			if (sourceHdLight) {
				var clonedHdLight = lightObject.AddComponent<HDAdditionalLightData>();
				EditorUtility.CopySerialized(sourceHdLight, clonedHdLight);
			}

			return lightObject;
		}

		private static Light FindSceneDirectionalLight(Transform tableRoot)
		{
			var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
			Light bestMatch = null;
			var bestIntensity = float.NegativeInfinity;

			foreach (var light in lights) {
				if (!light || light.type != LightType.Directional || !light.enabled || !light.gameObject.activeInHierarchy) {
					continue;
				}
				if (tableRoot && light.transform.IsChildOf(tableRoot)) {
					continue;
				}

				if (RenderSettings.sun == light) {
					return light;
				}

				if (light.intensity > bestIntensity) {
					bestIntensity = light.intensity;
					bestMatch = light;
				}
			}

			return bestMatch;
		}

	}
}
