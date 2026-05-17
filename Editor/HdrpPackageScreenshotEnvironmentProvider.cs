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
using UnityEditor;
using UnityEngine;
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
			public IDisposable CreateEnvironmentScope(Transform tableRoot, Cubemap hdriCubemap, float hdriExposure)
			{
				return new TemporaryHdrpEnvironmentScope(tableRoot);
			}
		}
	}

	internal sealed class TemporaryHdrpEnvironmentScope : IDisposable
	{
		private const string DirectionalLightPrefabPath =
			"Packages/org.visualpinball.engine.unity.hdrp/Assets/EditorResources/Prefabs/Screenshot/DirectionalLight.prefab";

		private readonly GameObject _temporaryDirectionalLight;
		private readonly Light _originalSun;
		private readonly List<DisabledLightState> _disabledLights = new();

		public TemporaryHdrpEnvironmentScope(Transform tableRoot)
		{
			_originalSun = RenderSettings.sun;

			_temporaryDirectionalLight = InstantiateDirectionalLight();
			if (_temporaryDirectionalLight) {
				var light = _temporaryDirectionalLight.GetComponentInChildren<Light>(true);
				if (light) {
					RenderSettings.sun = light;
				}
			}

			DisableNonTableLights(tableRoot);
		}

		public void Dispose()
		{
			for (var i = _disabledLights.Count - 1; i >= 0; i--) {
				var disabledLightState = _disabledLights[i];
				if (disabledLightState.Light) {
					disabledLightState.Light.enabled = disabledLightState.WasEnabled;
				}
			}

			RenderSettings.sun = _originalSun;

			if (_temporaryDirectionalLight) {
				UnityEngine.Object.DestroyImmediate(_temporaryDirectionalLight);
			}
		}

		// The screenshot directional light is a fully authored prefab (HDRP light
		// data, intensity, colour temperature, flares, shadow resolution, …). Using
		// it directly keeps screenshot lighting coherent across tables and avoids
		// re-deriving HDRP light settings in code.
		private static GameObject InstantiateDirectionalLight()
		{
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DirectionalLightPrefabPath);
			if (!prefab) {
				Debug.LogError($"Could not find screenshot directional light prefab at path: {DirectionalLightPrefabPath}");
				return null;
			}

			var instance = UnityEngine.Object.Instantiate(prefab);
			instance.name = "Package Screenshot Directional Light";
			instance.hideFlags = HideFlags.HideAndDontSave;
			return instance;
		}

		private void DisableNonTableLights(Transform tableRoot)
		{
			var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
			foreach (var light in lights) {
				if (!light) {
					continue;
				}
				if (_temporaryDirectionalLight && light.transform.IsChildOf(_temporaryDirectionalLight.transform)) {
					continue;
				}
				if (!light.gameObject.scene.IsValid() || light.hideFlags != HideFlags.None) {
					continue;
				}
				if (tableRoot && light.transform.IsChildOf(tableRoot)) {
					continue;
				}

				_disabledLights.Add(new DisabledLightState(light, light.enabled));
				light.enabled = false;
			}
		}

		private readonly struct DisabledLightState
		{
			public readonly Light Light;
			public readonly bool WasEnabled;

			public DisabledLightState(Light light, bool wasEnabled)
			{
				Light = light;
				WasEnabled = wasEnabled;
			}
		}
	}
}
