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

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Enables HDRP depth of field while active and continuously focuses it on whatever is at the center
	/// of the screen. Disabling the component turns depth of field off.
	/// </summary>
	///
	/// <remarks>
	/// The focus distance is measured on the GPU: a global custom pass reads the camera's depth buffer
	/// around the screen center (opaque geometry only, so the glass doesn't steal focus) and the result
	/// is read back asynchronously. The depth of field override runs in physical camera mode, so the
	/// amount of blur comes from the camera's aperture, focal length and sensor size.
	///
	/// Only the volume's runtime profile instance is modified, never the shared profile asset.
	/// </remarks>
	[AddComponentMenu("Pinball/Camera/Auto Focus")]
	[RequireComponent(typeof(Camera))]
	[DisallowMultipleComponent]
	public class AutoFocusComponent : MonoBehaviour
	{
		[Tooltip("The volume whose depth of field is driven. If empty, the active global volume with the highest priority is used.")]
		public Volume PostProcessVolume;

		[Tooltip("How fast the focus follows the measured distance. Zero snaps instantly.")]
		[Min(0f)]
		public float FocusSpeed = 8f;

		[Tooltip("Size of the measured area at the center of the screen, as a fraction of the screen height.")]
		[Range(0f, 0.25f)]
		public float SampleArea = 0.02f;

		[HideInInspector]
		public ComputeShader DepthProbeShader;

		private static readonly int DepthId = Shader.PropertyToID("_AutoFocusDepth");
		private static readonly int ResultId = Shader.PropertyToID("_AutoFocusResult");
		private static readonly int ParamsId = Shader.PropertyToID("_AutoFocusParams");

		private Camera _camera;
		private DepthOfField _dof;
		private DepthProbePass _pass;
		private GraphicsBuffer _resultBuffer;
		private System.Action<AsyncGPUReadbackRequest> _onReadback;
		private int _kernel;

		private float _measuredDistance;
		private bool _hasMeasurement;
		private float _focusDistance;
		private bool _hasFocus;
		private bool _warnedMsaa;
		private bool _warnedNoMeasurement;
		private float _enabledTime;

#if UNITY_EDITOR
		private const string DepthProbeShaderGuid = "5d08e5fafb6c491e84460863c775e19e";

		private void OnValidate()
		{
			// the script's default reference only kicks in when added through the inspector
			if (DepthProbeShader == null) {
				DepthProbeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(UnityEditor.AssetDatabase.GUIDToAssetPath(DepthProbeShaderGuid));
			}
		}
#endif

		private void OnEnable()
		{
			_camera = GetComponent<Camera>();
			if (DepthProbeShader == null) {
				Debug.LogWarning($"[AutoFocus] No depth probe shader assigned on {name}, cannot focus.", this);
				return;
			}

			var volume = PostProcessVolume != null ? PostProcessVolume : HdrpGraphicsApplier.FindGlobalVolume();
			if (volume == null) {
				Debug.LogWarning($"[AutoFocus] No post process volume found for {name}, cannot focus.", this);
				return;
			}

			// .profile gives us the volume's own instance, so the shared asset stays untouched.
			var profile = volume.profile;
			if (!profile.TryGet(out _dof)) {
				_dof = profile.Add<DepthOfField>();
			}
			// stays off until the first measurement arrives, otherwise we'd blur with a stale focus distance.
			_dof.active = true;
			_dof.focusMode.Override(DepthOfFieldMode.Off);
			_dof.focusDistanceMode.Override(FocusDistanceMode.Volume);

			EnableCustomPasses();

			_kernel = DepthProbeShader.FindKernel("KMain");
			_resultBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
			_onReadback = OnReadback;
			_hasMeasurement = false;
			_hasFocus = false;
			_enabledTime = Time.unscaledTime;
			_warnedNoMeasurement = false;

			_pass = new DepthProbePass { Owner = this, name = "VPE Auto Focus" };
			CustomPassVolume.RegisterGlobalCustomPass(CustomPassInjectionPoint.BeforeTransparent, _pass);
		}

		private void OnDisable()
		{
			if (_pass != null) {
				CustomPassVolume.UnregisterGlobalCustomPass(_pass);
				_pass = null;
			}
			_resultBuffer?.Release();
			_resultBuffer = null;

			if (_dof != null) {
				_dof.focusMode.Override(DepthOfFieldMode.Off);
				_dof = null;
			}
		}

		/// <summary>
		/// The depth probe is a custom pass, so it needs custom pass support in the pipeline asset and
		/// the custom pass frame setting on the camera, which we override if needed.
		/// </summary>
		private void EnableCustomPasses()
		{
			if (GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset && !asset.currentPlatformRenderPipelineSettings.supportCustomPass) {
				Debug.LogWarning($"[AutoFocus] Custom passes are disabled in the HDRP asset \"{asset.name}\", auto focus won't work. Enable Rendering > Custom Pass in the asset.", this);
			}
			var hdCamera = GetComponent<HDAdditionalCameraData>();
			if (hdCamera == null) {
				return;
			}
			// overriding only this field leaves all others at their defaults
			hdCamera.customRenderingSettings = true;
			hdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.CustomPass] = true;
			hdCamera.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.CustomPass, true);
		}

		private void Update()
		{
			if (_dof == null) {
				return;
			}
			if (!_hasMeasurement) {
				if (!_warnedNoMeasurement && Time.unscaledTime - _enabledTime > 3f) {
					Debug.LogWarning($"[AutoFocus] No depth measured on {name} yet, is the custom pass running? Check that Custom Pass is enabled in the HDRP asset.", this);
					_warnedNoMeasurement = true;
				}
				return;
			}
			if (!_hasFocus || FocusSpeed <= 0f) {
				_focusDistance = _measuredDistance;
				_hasFocus = true;
				_dof.focusMode.Override(DepthOfFieldMode.UsePhysicalCamera);

			} else {
				var t = 1f - math.exp(-FocusSpeed * Time.unscaledDeltaTime);
				_focusDistance = math.lerp(_focusDistance, _measuredDistance, t);
			}
			_dof.focusDistance.Override(math.max(_dof.focusDistance.min, _focusDistance));
		}

		private void Probe(CustomPassContext ctx)
		{
			if (ctx.hdCamera.camera != _camera || _resultBuffer == null) {
				return;
			}
			if (ctx.hdCamera.msaaEnabled) {
				// the depth buffer is multisampled and can't be loaded as a plain texture.
				if (!_warnedMsaa) {
					Debug.LogWarning("[AutoFocus] MSAA is enabled on the camera, auto focus is not supported.", this);
					_warnedMsaa = true;
				}
				return;
			}

			var cmd = ctx.cmd;
			cmd.SetComputeTextureParam(DepthProbeShader, _kernel, DepthId, ctx.cameraDepthBuffer, 0, RenderTextureSubElement.Depth);
			cmd.SetComputeBufferParam(DepthProbeShader, _kernel, ResultId, _resultBuffer);
			cmd.SetComputeVectorParam(DepthProbeShader, ParamsId, new Vector4(SampleArea, 0f, 0f, 0f));
			cmd.DispatchCompute(DepthProbeShader, _kernel, 1, 1, 1);
			cmd.RequestAsyncReadback(_resultBuffer, _onReadback);
		}

		private void OnReadback(AsyncGPUReadbackRequest request)
		{
			// may arrive after the component got disabled or destroyed
			if (this == null || _dof == null || request.hasError) {
				return;
			}
			var distance = request.GetData<float>()[0];
			if (distance > 0f) { // negative means nothing but sky in the sampled area: keep the last focus.
				_measuredDistance = distance;
				_hasMeasurement = true;
			}
		}

		private sealed class DepthProbePass : CustomPass
		{
			public AutoFocusComponent Owner;

			protected override void Execute(CustomPassContext ctx) => Owner.Probe(ctx);
		}
	}
}
