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
using UnityEngine.Rendering;
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
			public IDisposable CreateEnvironmentScope(Transform tableRoot, Cubemap hdriCubemap, float hdriExposure, bool includeDirectionalLight)
			{
				return new TemporaryHdrpEnvironmentScope(tableRoot, hdriCubemap, hdriExposure, includeDirectionalLight);
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
		private readonly GameObject _temporaryHdriVolume;
		private readonly VolumeProfile _temporaryHdriProfile;

		public TemporaryHdrpEnvironmentScope(Transform tableRoot, Cubemap hdriCubemap, float hdriExposure, bool includeDirectionalLight)
		{
			_originalSun = RenderSettings.sun;

			if (includeDirectionalLight) {
				_temporaryDirectionalLight = InstantiateDirectionalLight();
				if (_temporaryDirectionalLight) {
					var light = _temporaryDirectionalLight.GetComponentInChildren<Light>(true);
					if (light) {
						RenderSettings.sun = light;
					}
				}
			}

			DisableNonTableLights(tableRoot);
			_temporaryHdriVolume = CreateTemporaryHdriOverride(hdriCubemap, hdriExposure, out _temporaryHdriProfile);
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

			if (_temporaryHdriVolume) {
				UnityEngine.Object.DestroyImmediate(_temporaryHdriVolume);
			}

			if (_temporaryHdriProfile) {
				UnityEngine.Object.DestroyImmediate(_temporaryHdriProfile);
			}
		}

		// Replace the scene's HDRI sky with the one configured on the table (plus
		// its exposure) for the duration of the screenshot, via a top-priority
		// global volume that only overrides the HDRI texture and exposure.
		private static GameObject CreateTemporaryHdriOverride(Cubemap hdriCubemap, float hdriExposure, out VolumeProfile profile)
		{
			profile = null;
			if (!hdriCubemap) {
				return null;
			}

			var go = new GameObject("Package Screenshot HDRI") {
				hideFlags = HideFlags.HideAndDontSave
			};

			var volume = go.AddComponent<Volume>();
			volume.isGlobal = true;
			// Higher than any scene volume so this HDRI always wins.
			volume.priority = float.MaxValue;

			profile = ScriptableObject.CreateInstance<VolumeProfile>();
			profile.hideFlags = HideFlags.HideAndDontSave;

			// overrides:false so only the parameters we touch below are applied;
			// rotation and everything else still come from the scene's sky volume.
			var sky = profile.Add<HDRISky>(false);
			sky.hdriSky.overrideState = true;
			sky.hdriSky.value = hdriCubemap;
			sky.skyIntensityMode.overrideState = true;
			sky.skyIntensityMode.value = SkyIntensityMode.Exposure;
			sky.exposure.overrideState = true;
			sky.exposure.value = hdriExposure;

			volume.sharedProfile = profile;
			return go;
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
