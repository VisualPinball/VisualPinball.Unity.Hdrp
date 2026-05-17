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
using System.Reflection;
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
		private const float ScreenshotAngularDiameter = 4.04f;
		private const float ScreenshotColorTemperature = 4900f;
		private const float ScreenshotIntensity = 613.6931f;
		private const float ScreenshotFlareSize = 2f;
		private const float ScreenshotFlareFalloff = 4f;
		private const float ScreenshotFlareMultiplier = 1f;
		private const int ScreenshotShadowResolution = 256;

		private readonly Light _temporaryDirectionalLight;
		private readonly Light _originalSun;
		private readonly List<DisabledLightState> _disabledLights = new();

		public TemporaryHdrpEnvironmentScope(Transform tableRoot)
		{
			_originalSun = RenderSettings.sun;
			var sourceDirectionalLight = FindDirectionalLight();
			_temporaryDirectionalLight = CreateTemporaryDirectionalLight(sourceDirectionalLight);
			if (_temporaryDirectionalLight) {
				RenderSettings.sun = _temporaryDirectionalLight;
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
				UnityEngine.Object.DestroyImmediate(_temporaryDirectionalLight.gameObject);
			}
		}

		private void DisableNonTableLights(Transform tableRoot)
		{
			var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
			foreach (var light in lights) {
				if (!light || light == _temporaryDirectionalLight) {
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

		private static Light FindDirectionalLight()
		{
			if (RenderSettings.sun && RenderSettings.sun.type == LightType.Directional) {
				return RenderSettings.sun;
			}

			var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
			foreach (var light in lights) {
				if (!light || light.type != LightType.Directional) {
					continue;
				}
				if (!light.gameObject.scene.IsValid() || light.hideFlags != HideFlags.None) {
					continue;
				}
				return light;
			}

			return null;
		}

		private static Light CreateTemporaryDirectionalLight(Light sourceDirectionalLight)
		{
			var go = new GameObject("Package Screenshot Directional Light") {
				hideFlags = HideFlags.HideAndDontSave
			};

			var light = go.AddComponent<Light>();
			if (sourceDirectionalLight) {
				go.transform.SetPositionAndRotation(sourceDirectionalLight.transform.position, sourceDirectionalLight.transform.rotation);
			}

			ConfigureBasicDirectionalLight(light, sourceDirectionalLight);
			ConfigureHdDirectionalLight(sourceDirectionalLight, go);
			ApplyShadowResolutionOverride(go, light);
			return light;
		}

		private static void ConfigureBasicDirectionalLight(Light light, Light sourceDirectionalLight)
		{
			light.type = LightType.Directional;
			light.enabled = true;
			light.lightmapBakeType = LightmapBakeType.Realtime;
			light.shadows = LightShadows.Soft;
			light.useColorTemperature = true;
			light.colorTemperature = ScreenshotColorTemperature;
			light.intensity = ScreenshotIntensity;
			light.bounceIntensity = 1f;
			light.cookie = null;

			if (!sourceDirectionalLight) {
				return;
			}

			light.color = sourceDirectionalLight.color;
			light.shadowStrength = sourceDirectionalLight.shadowStrength;
			light.shadowBias = sourceDirectionalLight.shadowBias;
			light.shadowNormalBias = sourceDirectionalLight.shadowNormalBias;
			light.shadowNearPlane = sourceDirectionalLight.shadowNearPlane;
			light.cullingMask = sourceDirectionalLight.cullingMask;
			light.renderingLayerMask = sourceDirectionalLight.renderingLayerMask;
		}

		private static void ConfigureHdDirectionalLight(Light sourceDirectionalLight, GameObject destination)
		{
			var hdLightType = Type.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData, Unity.RenderPipelines.HighDefinition.Runtime");
			if (hdLightType == null) {
				return;
			}

			var destinationHdLight = destination.GetComponent(hdLightType) ?? destination.AddComponent(hdLightType);
			InitializeHdAdditionalLightData(destinationHdLight, hdLightType);

			// InitDefaultHDAdditionalLightData resets the directional intensity to
			// HDRP's default (100000 lux), overwriting what ConfigureBasicDirectionalLight
			// set on the Light. Re-apply it through the HD data so it actually sticks.
			SetHdLightProperty(destinationHdLight, "intensity", ScreenshotIntensity);

			if (!sourceDirectionalLight) {
				return;
			}

			var sourceHdLight = sourceDirectionalLight.gameObject.GetComponent(hdLightType);
			SetHdLightProperty(destinationHdLight, "angularDiameter", ScreenshotAngularDiameter);
			SetHdLightProperty(destinationHdLight, "interactsWithSky", true);
			SetHdLightProperty(destinationHdLight, "lightDimmer", 1f);
			SetHdLightProperty(destinationHdLight, "volumetricDimmer", 1f);
			SetHdLightProperty(destinationHdLight, "shadowDimmer", 1f);
			SetHdLightProperty(destinationHdLight, "volumetricShadowDimmer", 1f);
			SetHdLightProperty(destinationHdLight, "affectDiffuse", true);
			SetHdLightProperty(destinationHdLight, "affectSpecular", true);
			SetHdLightProperty(destinationHdLight, "useRayTracedShadows", false);
			SetHdLightProperty(destinationHdLight, "flareSize", ScreenshotFlareSize);
			SetHdLightProperty(destinationHdLight, "flareFalloff", ScreenshotFlareFalloff);
			SetHdLightProperty(destinationHdLight, "flareMultiplier", ScreenshotFlareMultiplier);

			if (sourceHdLight) {
				CopyHdLightProperty(sourceHdLight, destinationHdLight, "flareTint");
				CopyHdLightProperty(sourceHdLight, destinationHdLight, "surfaceTexture");
				CopyHdLightProperty(sourceHdLight, destinationHdLight, "surfaceTint");
			}
		}

		private static void InitializeHdAdditionalLightData(Component hdAdditionalLightData, Type hdLightType)
		{
			var initMethod = hdLightType.GetMethod(
				"InitDefaultHDAdditionalLightData",
				BindingFlags.Public | BindingFlags.Static,
				null,
				new[] { hdLightType },
				null
			);
			initMethod?.Invoke(null, new object[] { hdAdditionalLightData });
		}

		private static void ApplyShadowResolutionOverride(GameObject lightGameObject, Light fallbackLight)
		{
			var hdLightType = Type.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData, Unity.RenderPipelines.HighDefinition.Runtime");
			var hdAdditionalLightData = hdLightType == null ? null : lightGameObject.GetComponent(hdLightType);
			if (hdAdditionalLightData != null && TryApplyHdrpShadowResolutionOverride(hdAdditionalLightData)) {
				return;
			}

			if (fallbackLight) {
				fallbackLight.shadows = LightShadows.Soft;
			}
		}

		private static bool TryApplyHdrpShadowResolutionOverride(Component hdAdditionalLightData)
		{
			var hdLightType = hdAdditionalLightData.GetType();
			var setShadowResolutionOverrideMethod = hdLightType.GetMethod("SetShadowResolutionOverride", BindingFlags.Public | BindingFlags.Instance);
			var setShadowResolutionMethod = hdLightType.GetMethod("SetShadowResolution", BindingFlags.Public | BindingFlags.Instance);
			if (setShadowResolutionOverrideMethod != null && setShadowResolutionMethod != null) {
				setShadowResolutionOverrideMethod.Invoke(hdAdditionalLightData, new object[] { true });
				setShadowResolutionMethod.Invoke(hdAdditionalLightData, new object[] { ScreenshotShadowResolution });
				return true;
			}

			if (!TryGetShadowResolutionOverride(hdAdditionalLightData, out var shadowResolutionValue, out var useOverrideProperty, out var overrideProperty)) {
				return false;
			}

			useOverrideProperty.SetValue(shadowResolutionValue, true);
			overrideProperty.SetValue(shadowResolutionValue, ScreenshotShadowResolution);
			return true;
		}

		private static void CopyHdLightProperty(Component sourceHdLight, Component destinationHdLight, string propertyName)
		{
			var property = sourceHdLight.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
			if (property == null || !property.CanRead || !property.CanWrite) {
				return;
			}

			property.SetValue(destinationHdLight, property.GetValue(sourceHdLight));
		}

		private static void SetHdLightProperty(Component destinationHdLight, string propertyName, object value)
		{
			var property = destinationHdLight.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
			if (property == null || !property.CanWrite) {
				return;
			}

			property.SetValue(destinationHdLight, value);
		}

		private static bool TryGetShadowResolutionOverride(Component hdAdditionalLightData, out object shadowResolutionValue, out PropertyInfo useOverrideProperty, out PropertyInfo overrideProperty)
		{
			shadowResolutionValue = null;
			useOverrideProperty = null;
			overrideProperty = null;

			var shadowResolutionProperty = hdAdditionalLightData.GetType().GetProperty("shadowResolution");
			shadowResolutionValue = shadowResolutionProperty?.GetValue(hdAdditionalLightData);
			if (shadowResolutionValue == null) {
				return false;
			}

			var scalableValueType = shadowResolutionValue.GetType();
			useOverrideProperty = scalableValueType.GetProperty("useOverride");
			overrideProperty = scalableValueType.GetProperty("override");
			return useOverrideProperty != null && overrideProperty != null;
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
