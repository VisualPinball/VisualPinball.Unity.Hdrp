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


using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Lights the table's surroundings for the host app under HDRP: scales the sky through a volume of its
	/// own on top of the scene's, and sets up room lights in HDRP's physical units.
	/// </summary>
	public sealed class HdrpEnvironmentApplier : IVpeEnvironmentApplier
	{
		// above the scene's and a table's own volumes
		private const float VolumePriority = 1000f;

		private static HdrpEnvironmentApplier _instance;

		private Volume _volume;
		private VolumeProfile _profile;
		private SkySettings _override;
		private VolumeStack _stack; // the volumes without this one's, to read the sky as the scene and the table set it
		private int _layer;         // one the game camera's volume layer mask lets through

		/// <summary>
		/// Whether a volume is the one that scales the sky. It holds nothing but that override and is switched
		/// off whenever the sky is left as it is, so nobody else's settings belong in it.
		/// </summary>
		internal static bool Owns(Volume volume) => _instance != null && volume != null && _instance._volume == volume;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register()
		{
			_instance?.Dispose();
			_instance = new HdrpEnvironmentApplier();
			VpeEnvironment.Register(_instance);
#if UNITY_EDITOR
			UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeChanged;
			UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeChanged;
#endif
		}

#if UNITY_EDITOR
		private static void OnEditorPlayModeChanged(UnityEditor.PlayModeStateChange state)
		{
			if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) {
				_instance?.Dispose();
			}
		}
#endif

		public void SetSkyIntensity(float multiplier)
		{
			// out of the way first: the sky is read as it is without this volume
			if (_volume != null) {
				_volume.enabled = false;
			}
			var source = Mathf.Approximately(multiplier, 1f) ? null : ResolveSky();
			if (source == null) {
				return;
			}
			EnsureVolume(source.GetType());

			// Only the value the sky's intensity mode reads is overridden; the rest still comes from the scene.
			_override.exposure.overrideState = false;
			_override.multiplier.overrideState = false;
			_override.desiredLuxValue.overrideState = false;
			switch (source.skyIntensityMode.value) {
				case SkyIntensityMode.Multiplier:
					_override.multiplier.Override(source.multiplier.value * multiplier);
					break;
				case SkyIntensityMode.Lux:
					_override.desiredLuxValue.Override(source.desiredLuxValue.value * multiplier);
					break;
				default:
					// stops; a sky can't go fully black this way, so zero is ten stops down
					_override.exposure.Override(source.exposure.value + Mathf.Log(Mathf.Max(multiplier, 1f / 1024f), 2f));
					break;
			}
			_volume.enabled = true;
		}

		public void ConfigureLight(Light light, in VpeEnvironmentLight spec)
		{
			var hdLight = light.GetComponent<HDAdditionalLightData>();
			if (hdLight == null) {
				hdLight = light.gameObject.AddComponent<HDAdditionalLightData>();
				HDAdditionalLightData.InitDefaultHDAdditionalLightData(hdLight);
			}
			light.type = spec.Directional ? LightType.Directional : LightType.Spot;
			light.lightUnit = spec.Directional ? LightUnit.Lux : LightUnit.Candela;
			light.useColorTemperature = false;
			light.color = spec.Color;
			light.intensity = spec.Intensity;
			if (!spec.Directional) {
				light.spotAngle = spec.SpotAngle;
				light.innerSpotAngle = spec.SpotAngle * spec.InnerSpotPercent / 100f;
				light.range = spec.Range;
				// a studio light is a wide source: its shadows and highlights are soft
				light.shapeRadius = 0.15f;
			}
			hdLight.affectsVolumetric = false;
			hdLight.EnableShadows(spec.Shadows);
			if (spec.Shadows) {
				hdLight.SetShadowUpdateMode(ShadowUpdateMode.EveryFrame);
			}
		}

		// The sky that shows, with the values the volumes blend to for the game camera (this one's left out,
		// the caller has switched it off): a table can bring a volume that overrides only some of them.
		private SkySettings ResolveSky()
		{
			var manager = VolumeManager.instance;
			if (manager == null || !manager.isInitialized) {
				return null;
			}
			// a change of the quality level replaces the pipeline, which disposes the stacks made before
			if (_stack == null || !_stack.isValid) {
				_stack = manager.CreateStack();
			}
			var camera = Camera.main;
			var data = camera != null ? camera.GetComponent<HDAdditionalCameraData>() : null;
			var anchor = data != null && data.volumeAnchorOverride != null ? data.volumeAnchorOverride : camera != null ? camera.transform : null;
			var mask = data != null ? (int)data.volumeLayerMask : ~0;
			// the override goes where that camera looks for volumes: the lowest of its layers
			_layer = 0;
			while (_layer < 31 && (mask & (1 << _layer)) == 0) {
				_layer++;
			}
			manager.Update(_stack, anchor, (LayerMask)mask);
			var environment = _stack.GetComponent<VisualEnvironment>();
			if (environment == null) {
				return null;
			}
			return (SkyType)environment.skyType.value switch {
				SkyType.HDRI => _stack.GetComponent<HDRISky>(),
				SkyType.Gradient => _stack.GetComponent<GradientSky>(),
				SkyType.PhysicallyBased => _stack.GetComponent<PhysicallyBasedSky>(),
				_ => null, // no sky, or one of a kind unknown here
			};
		}

		private void EnsureVolume(System.Type skyType)
		{
			if (_volume == null) {
				var go = new GameObject("VPE Environment Intensity");
				if (Application.isPlaying) {
					Object.DontDestroyOnLoad(go);
				}
				_volume = go.AddComponent<Volume>();
				_volume.isGlobal = true;
				_volume.priority = VolumePriority;
				_profile = ScriptableObject.CreateInstance<VolumeProfile>();
				// as the volume's own profile, so that reading Volume.profile never clones it
				_volume.profile = _profile;
			}
			_volume.gameObject.layer = _layer;
			if (_override != null && _override.GetType() == skyType) {
				return;
			}
			if (_override != null) {
				_profile.Remove(_override.GetType());
				Destroy(_override);
			}
			_override = (SkySettings)_profile.Add(skyType, false);
		}

		// What was made here goes with the play session, or when another applier takes over.
		private void Dispose()
		{
			if (_stack != null && _stack.isValid && VolumeManager.instance != null && VolumeManager.instance.isInitialized) {
				VolumeManager.instance.DestroyStack(_stack);
			}
			_stack = null;
			if (_volume != null) {
				Destroy(_volume.gameObject);
			}
			if (_override != null) {
				Destroy(_override);
			}
			if (_profile != null) {
				Destroy(_profile);
			}
			_volume = null;
			_override = null;
			_profile = null;
		}

		private static void Destroy(Object obj)
		{
			if (Application.isPlaying) {
				Object.Destroy(obj);
			} else {
				Object.DestroyImmediate(obj);
			}
		}
	}
}
