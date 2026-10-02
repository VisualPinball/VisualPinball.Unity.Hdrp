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

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity.Editor;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	[InitializeOnLoad]
	internal static class HdrpAssetPreviewEnvironmentRegistration
	{
		static HdrpAssetPreviewEnvironmentRegistration()
		{
			AssetPreviewEnvironmentProvider.Register(new Provider());
		}

		private sealed class Provider : IAssetPreviewEnvironmentProvider
		{
			public IAssetPreviewEnvironment Create(PreviewRenderUtility utility) => new HdrpAssetPreviewEnvironment(utility);
		}
	}

	/// <summary>
	/// An asset preview lit and exposed like a table. HDRP treats a preview
	/// camera specially: a fixed exposure of zero, no refraction, no screen
	/// space effects, its own flat sky. So the utility's camera is made a game
	/// camera reading its volumes from a layer of its own, where a global
	/// volume fixes the exposure at what a lit playfield wants and puts a grey
	/// gradient sky up, and the preview's two lights get an intensity in lux.
	/// The volume is only switched on while the preview renders, so no other
	/// camera whose mask includes that layer ever sees it.
	/// </summary>
	internal sealed class HdrpAssetPreviewEnvironment : IAssetPreviewEnvironment
	{
		/// <summary>The layer the preview volume lives on; the last user layer, which VPE leaves free.</summary>
		private const int VolumeLayer = 31;

		/// <summary>Fixed exposure in EV100: an insert's emission of a thousand nits glows without clipping.</summary>
		private const float Exposure = 10f;

		/// <summary>Key and fill, in lux, for that exposure.</summary>
		private const float KeyLux = 2000f;
		private const float FillLux = 1000f;

		private readonly GameObject _volumeObject;
		private readonly VolumeProfile _profile;

		public HdrpAssetPreviewEnvironment(PreviewRenderUtility utility)
		{
			var camera = utility.camera;
			camera.cameraType = CameraType.Game;
			var cameraData = camera.GetComponent<HDAdditionalCameraData>();
			if (cameraData == null) {
				cameraData = camera.gameObject.AddComponent<HDAdditionalCameraData>();
			}
			cameraData.volumeLayerMask = 1 << VolumeLayer;
			cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
			cameraData.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;

			for (var i = 0; i < utility.lights.Length; i++) {
				var light = utility.lights[i];
				var lightData = light.GetComponent<HDAdditionalLightData>();
				if (lightData == null) {
					lightData = light.gameObject.AddComponent<HDAdditionalLightData>();
					HDAdditionalLightData.InitDefaultHDAdditionalLightData(lightData);
				}
				lightData.SetIntensity(i == 0 ? KeyLux : FillLux, LightUnit.Lux);
				lightData.EnableShadows(false);
			}

			_volumeObject = new GameObject("Asset Preview Volume") {
				hideFlags = HideFlags.HideAndDontSave,
				layer = VolumeLayer,
			};
			_volumeObject.SetActive(false);
			var volume = _volumeObject.AddComponent<Volume>();
			volume.isGlobal = true;
			volume.priority = float.MaxValue;

			_profile = ScriptableObject.CreateInstance<VolumeProfile>();
			_profile.hideFlags = HideFlags.HideAndDontSave;
			var exposure = _profile.Add<Exposure>(true);
			exposure.mode.Override(ExposureMode.Fixed);
			exposure.fixedExposure.Override(Exposure);
			var environment = _profile.Add<VisualEnvironment>(true);
			environment.skyType.Override((int)SkyType.Gradient);
			environment.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
			var sky = _profile.Add<GradientSky>(true);
			sky.top.Override(new Color(0.55f, 0.55f, 0.55f));
			sky.middle.Override(new Color(0.45f, 0.45f, 0.45f));
			sky.bottom.Override(new Color(0.3f, 0.3f, 0.3f));
			sky.exposure.Override(Exposure);
			sky.multiplier.Override(1f);
			volume.sharedProfile = _profile;
			utility.AddSingleGO(_volumeObject);
		}

		public void BeginRender()
		{
			if (_volumeObject != null) {
				_volumeObject.SetActive(true);
			}
		}

		public void EndRender()
		{
			if (_volumeObject != null) {
				_volumeObject.SetActive(false);
			}
		}

		public void Dispose()
		{
			// The utility destroys the volume object with its scene; the profile is ours.
			if (_profile != null) {
				Object.DestroyImmediate(_profile);
			}
		}
	}
}
