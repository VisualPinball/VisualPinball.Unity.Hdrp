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

using System.Collections.Generic;
using NLog;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;
using Logger = NLog.Logger;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Reflects the playfield in the balls. Each ball gets a reflection probe the size of the ball, whose
	/// cubemap is filled on the GPU from the frame the camera just rendered.
	/// </summary>
	///
	/// <remarks>
	/// A realtime probe would render the scene six more times per ball and frame. Here, a global custom pass
	/// marches every cubemap texel's direction from the ball through the camera's depth buffer, which finds
	/// what stands on the playfield, falls back to where the direction meets the playfield plane, and takes
	/// the lit color of the hit from the camera's color buffer, so it costs one small dispatch per ball. What
	/// the camera doesn't see, it can't reflect, so close-up cameras get less of it. HDRP then applies the
	/// cubemap through each ball's own material, so any ball look works. Where SSR finds a hit on the ball,
	/// it still wins. The cubemap lags the frame by one.
	/// </remarks>
	[AddComponentMenu("Pinball/Camera/Ball Reflections")]
	[RequireComponent(typeof(Camera))]
	[DisallowMultipleComponent]
	public class BallReflectionsComponent : MonoBehaviour
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		[Tooltip("Size of a face of each ball's reflection cubemap, in pixels.")]
		[Min(16)]
		public int Resolution = 128;

		private const string ShaderResource = "VpeBallReflection";
		private const float BallScanInterval = 0.25f;

		private static readonly int ColorId = Shader.PropertyToID("_BallReflectionColor");
		private static readonly int FacesId = Shader.PropertyToID("_BallReflectionFaces");
		private static readonly int BallId = Shader.PropertyToID("_BallReflectionBall");
		private static readonly int PlaneId = Shader.PropertyToID("_BallReflectionPlane");
		private static readonly int WorldToPlayfieldId = Shader.PropertyToID("_BallReflectionWorldToPlayfield");
		private static readonly int ParamsId = Shader.PropertyToID("_BallReflectionParams");

		private Camera _camera;
		private ComputeShader _shader;
		private int _kernel;
		private FillPass _pass;
		private RenderTexture _faces;
		private PlayfieldComponent _playfield;
		private float _nextBallScan;
		private readonly Dictionary<BallComponent, BallProbe> _probes = new();
		private readonly List<BallComponent> _gone = new();

		// the camera's frame settings this component overrides, and what they were before
		private static readonly FrameSettingsField[] FrameSettingsFields = { FrameSettingsField.CustomPass, FrameSettingsField.ReflectionProbe };
		private HDAdditionalCameraData _hdCamera;
		private bool _hadCustomRenderingSettings;
		private readonly bool[] _hadOverride = new bool[FrameSettingsFields.Length];
		private readonly bool[] _hadEnabled = new bool[FrameSettingsFields.Length];

		private sealed class BallProbe
		{
			public GameObject GameObject;
			public RenderTexture Cube;
			public float Radius;
		}

		private void OnEnable()
		{
			_camera = GetComponent<Camera>();
			_shader = UnityEngine.Resources.Load<ComputeShader>(ShaderResource);
			if (_shader == null) {
				Logger.Warn("[BallReflections] Compute shader {Shader} not found, balls keep reflecting the sky only.", ShaderResource);
				enabled = false;
				return;
			}
			_kernel = _shader.FindKernel("KFill");
			// shared by all balls, since each ball's fill is copied out before the next one starts
			_faces = new RenderTexture(Resolution, Resolution, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None) {
				dimension = TextureDimension.Tex2DArray,
				volumeDepth = 6,
				enableRandomWrite = true,
				name = "VPE Ball Reflection Faces",
			};
			_faces.Create();
			EnableFrameSettings();
			_nextBallScan = 0f;
			_pass = new FillPass { Owner = this, name = "VPE Ball Reflections" };
			// after the transparent pass, so the inserts are in the color buffer
			CustomPassVolume.RegisterGlobalCustomPass(CustomPassInjectionPoint.BeforePostProcess, _pass);
			RenderPipelineManager.beginContextRendering += FollowBalls;
		}

		private void OnDisable()
		{
			RenderPipelineManager.beginContextRendering -= FollowBalls;
			if (_pass != null) {
				CustomPassVolume.UnregisterGlobalCustomPass(_pass);
				_pass = null;
			}
			foreach (var probe in _probes.Values) {
				Release(probe);
			}
			_probes.Clear();
			if (_faces != null) {
				_faces.Release();
				Destroy(_faces);
				_faces = null;
			}
			RestoreFrameSettings();
		}

		/// <summary>
		/// The cubemaps reach the balls as reflection probes, and the fill runs in a custom pass, so the camera
		/// needs both. Only these two fields are overridden, and they're put back when disabled, so a table's
		/// own probes don't light up without the ball reflections.
		/// </summary>
		private void EnableFrameSettings()
		{
			if (!TryGetComponent(out _hdCamera)) {
				return;
			}
			_hadCustomRenderingSettings = _hdCamera.customRenderingSettings;
			_hdCamera.customRenderingSettings = true;
			for (var i = 0; i < FrameSettingsFields.Length; i++) {
				var field = FrameSettingsFields[i];
				_hadOverride[i] = _hdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field];
				_hadEnabled[i] = _hdCamera.renderingPathCustomFrameSettings.IsEnabled(field);
				_hdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = true;
				_hdCamera.renderingPathCustomFrameSettings.SetEnabled(field, true);
			}
		}

		private void RestoreFrameSettings()
		{
			if (_hdCamera == null) {
				return;
			}
			for (var i = 0; i < FrameSettingsFields.Length; i++) {
				var field = FrameSettingsFields[i];
				_hdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = _hadOverride[i];
				_hdCamera.renderingPathCustomFrameSettings.SetEnabled(field, _hadEnabled[i]);
			}
			_hdCamera.customRenderingSettings = _hadCustomRenderingSettings;
			_hdCamera = null;
		}

		// Runs once per frame before HDRP culls the probes, after the balls have moved.
		private void FollowBalls(ScriptableRenderContext context, List<Camera> cameras)
		{
			if (Time.unscaledTime >= _nextBallScan) {
				ScanBalls();
				_nextBallScan = Time.unscaledTime + BallScanInterval;
			}
			foreach (var (ball, probe) in _probes) {
				if (ball == null || !ball.isActiveAndEnabled) {
					_gone.Add(ball);
					continue;
				}
				// not parented to the ball, because the probe must not roll with it
				probe.GameObject.transform.SetPositionAndRotation(ball.transform.position, Quaternion.identity);
			}
			foreach (var ball in _gone) {
				Release(_probes[ball]);
				_probes.Remove(ball);
			}
			_gone.Clear();
		}

		private void ScanBalls()
		{
			if (_playfield == null) {
				_playfield = FindAnyObjectByType<PlayfieldComponent>();
			}
			foreach (var ball in FindObjectsByType<BallComponent>(FindObjectsSortMode.None)) {
				if (!_probes.ContainsKey(ball)) {
					_probes[ball] = CreateProbe(ball);
				}
			}
		}

		private BallProbe CreateProbe(BallComponent ball)
		{
			var filter = ball.GetComponentInChildren<MeshFilter>();
			var radius = filter != null && filter.sharedMesh != null
				? filter.sharedMesh.bounds.extents.x * filter.transform.lossyScale.x
				: VisualPinball.Unity.Physics.ScaleToWorld(ball.Radius);

			var cube = new RenderTexture(Resolution, Resolution, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None) {
				dimension = TextureDimension.Cube,
				useMipMap = true,
				autoGenerateMips = false,
				name = "VPE Ball Reflection",
			};
			cube.Create();

			// set up after the components woke up, since waking up copies the legacy probe's defaults over
			var go = new GameObject("Ball Reflection Probe") { hideFlags = HideFlags.DontSave };
			go.transform.SetPositionAndRotation(ball.transform.position, Quaternion.identity);
			go.AddComponent<ReflectionProbe>();
			var probe = go.AddComponent<HDAdditionalReflectionData>();
			probe.mode = ProbeSettings.Mode.Custom;
			probe.customTexture = cube;
			probe.importance = 32767;
			probe.influenceVolume.shape = InfluenceShape.Sphere;
			probe.influenceVolume.sphereRadius = radius * 1.05f;
			probe.influenceVolume.sphereBlendDistance = 0f;
			// projected at infinity: the cubemap is what the ball sees from its center
			probe.settingsRaw.proxySettings.useInfluenceVolumeAsProxyVolume = false;

			return new BallProbe { GameObject = go, Cube = cube, Radius = radius };
		}

		private static void Release(BallProbe probe)
		{
			if (probe.GameObject != null) {
				Destroy(probe.GameObject);
			}
			if (probe.Cube != null) {
				probe.Cube.Release();
				Destroy(probe.Cube);
			}
		}

		private void Fill(CustomPassContext ctx)
		{
			if (ctx.hdCamera.camera != _camera || _playfield == null || _probes.Count == 0) {
				return;
			}
			var pf = _playfield;
			var toWorld = pf.transform.localToWorldMatrix * (Matrix4x4)VisualPinball.Unity.Physics.VpxToWorld;
			var normal = toWorld.MultiplyVector(Vector3.forward).normalized;
			if (Vector3.Dot(normal, Vector3.up) < 0f) {
				normal = -normal;
			}
			var origin = toWorld.MultiplyPoint3x4(Vector3.zero);
			var worldToPlayfield = Matrix4x4.Scale(new Vector3(1f / (pf.Right - pf.Left), 1f / (pf.Bottom - pf.Top), 1f))
				* Matrix4x4.Translate(new Vector3(-pf.Left, -pf.Top, 0f))
				* toWorld.inverse;

			var cmd = ctx.cmd;
			cmd.SetComputeTextureParam(_shader, _kernel, ColorId, ctx.cameraColorBuffer);
			cmd.SetComputeVectorParam(_shader, PlaneId, new Vector4(normal.x, normal.y, normal.z, Vector3.Dot(normal, origin)));
			cmd.SetComputeMatrixParam(_shader, WorldToPlayfieldId, worldToPlayfield);
			// rays are marched across half the playfield's diagonal, which covers what a ball can see of it
			var traceDistance = toWorld.MultiplyVector(new Vector3(pf.Right - pf.Left, pf.Bottom - pf.Top, 0f)).magnitude * 0.5f;
			cmd.SetComputeVectorParam(_shader, ParamsId, new Vector4(Resolution, traceDistance, 0f, 0f));
			cmd.SetComputeTextureParam(_shader, _kernel, FacesId, _faces);
			var groups = (Resolution + 7) / 8;
			foreach (var probe in _probes.Values) {
				var center = probe.GameObject.transform.position;
				cmd.SetComputeVectorParam(_shader, BallId, new Vector4(center.x, center.y, center.z, probe.Radius));
				cmd.DispatchCompute(_shader, _kernel, groups, groups, 6);
				for (var face = 0; face < 6; face++) {
					cmd.CopyTexture(_faces, face, 0, probe.Cube, face, 0);
				}
				// HDRP filters a custom probe texture again only when its update count changes
				probe.Cube.IncrementUpdateCount();
			}
		}

		private sealed class FillPass : CustomPass
		{
			public BallReflectionsComponent Owner;

			protected override void Execute(CustomPassContext ctx) => Owner.Fill(ctx);
		}
	}
}
