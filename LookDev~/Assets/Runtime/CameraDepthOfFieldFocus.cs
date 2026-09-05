// AutoFocusDepthOfField.cs
// Editor‑only: Updates HDRP Depth‑of‑Field focus every time the *camera in the
// Game view* renders, giving frame‑accurate autofocus while you scrub the
// timeline, move objects, etc.—all without entering Play Mode.
//
// Key points
// • Uses RenderPipelineManager.beginCameraRendering so we get a callback for
//   *every* Game‑view render of the attached camera (also works for Scene view).
// • Triangle‑accurate ray cast via internal HandleUtility.IntersectRayMesh,
//   accessed through reflection. Falls back to bounds if unavailable.
// • Zero code included in builds; the class stubs out on non‑Editor targets.
//
// Requirements
// • Scene Volume with a DepthOfField override.
// • Attach this component to the same GameObject as the target Camera.
// • HDRP project (tested 2022.3 LTS, HDRP 14).

// #if UNITY_EDITOR
// using UnityEditor;
// using UnityEngine;
// using UnityEngine.Rendering;
// using UnityEngine.Rendering.HighDefinition;
// using System;
// using System.Reflection;
//
// [ExecuteAlways]
// [RequireComponent(typeof(Camera))]
// public class AutoFocusDepthOfField : MonoBehaviour
// {
// 	[Header("Depth Of Field Settings")] public Volume volume;
//
// 	[Min(0.01f)] public float maxDistance = 100f;
// 	public LayerMask layerMask = ~0;
//
// 	private Camera _cam;
// 	private DepthOfField _dof;
//
// 	//------------------------------------------------------
// 	// Reflection wrapper for HandleUtility.IntersectRayMesh
// 	//------------------------------------------------------
// 	private delegate bool IntersectRayMeshDelegate(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit);
//
// 	private static readonly IntersectRayMeshDelegate _intersectRayMesh;
//
// 	static AutoFocusDepthOfField()
// 	{
// 		const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Static;
// 		var meth = typeof(HandleUtility).GetMethod("IntersectRayMesh", bf,
// 			null,
// 			new[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() },
// 			null);
//
// 		if (meth != null)
// 		{
// 			_intersectRayMesh =
// 				(IntersectRayMeshDelegate)Delegate.CreateDelegate(typeof(IntersectRayMeshDelegate), meth);
// 		}
// 		else
// 		{
// 			Debug.LogWarning(
// 				"[AutoFocusDepthOfField] HandleUtility.IntersectRayMesh not found – falling back to bounds intersection (less accurate).");
// 			_intersectRayMesh = null;
// 		}
// 	}
//
// 	//------------------------------------------------------
// 	// Editor‑lifecycle hooks
// 	//------------------------------------------------------
// 	void OnEnable()
// 	{
// 		_cam = GetComponent<Camera>();
// 		EnsureDoF();
//
// 		// Listen for every SRP camera render (both Scene & Game view).
// 		RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
// 	}
//
// 	void OnDisable()
// 	{
// 		RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
// 	}
//
// 	void OnValidate()
// 	{
// 		if (!Application.isPlaying)
// 		{
// 			EnsureDoF();
// 			// We can’t guarantee a render right now, so update immediately
// 			UpdateFocusDistance();
// 			SceneView.RepaintAll();
// 		}
// 	}
//
// 	//------------------------------------------------------
// 	// Callback each time any camera renders in the Editor.
// 	//------------------------------------------------------
// 	void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera renderingCamera)
// 	{
// 		if (renderingCamera == _cam)
// 		{
// 			UpdateFocusDistance();
// 		}
// 	}
//
// 	//------------------------------------------------------
// 	// Autofocus core
// 	//------------------------------------------------------
// 	void EnsureDoF()
// 	{
// 		if (volume == null)
// 			volume = FindAnyObjectByType<Volume>();
//
// 		if (volume == null || !volume.profile.TryGet(out _dof))
// 		{
// 			_dof = null;
// 			Debug.LogWarning("[AutoFocusDepthOfField] DepthOfField override not found – autofocus disabled.");
// 			return;
// 		}
//
// 		_dof.active = true;
// 	}
//
// 	enum HitType
// 	{
// 		None,
// 		Triangle,
// 		Bounds
// 	}
//
// 	void UpdateFocusDistance()
// 	{
// 		if (_dof == null) return;
//
// 		Ray ray = new Ray(_cam.transform.position, _cam.transform.forward);
// 		float closest = maxDistance;
// 		var closestHit = HitType.None;
//
// 		var renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
// 		foreach (var rend in renderers)
// 		{
// 			if (!rend.enabled) continue;
// 			if ((layerMask & (1 << rend.gameObject.layer)) == 0) continue;
//
// 			Mesh mesh = rend switch
// 			{
// 				MeshRenderer mr => mr.GetComponent<MeshFilter>()?.sharedMesh,
// 				SkinnedMeshRenderer smr => smr.sharedMesh,
// 				_ => null
// 			};
// 			if (mesh == null) continue;
//
// 			var currentHit = HitType.None;
// 			bool hit = false;
// 			float hitDist = 0f;
//
// 			// 1. Triangle‑accurate if possible
// 			if (_intersectRayMesh != null && _intersectRayMesh(ray, mesh, rend.localToWorldMatrix, out RaycastHit meshHit))
// 			{
// 				hit = true;
// 				hitDist = meshHit.distance;
// 				currentHit = HitType.Triangle;
// 				Debug.Log($"Hit tri at {hitDist}m for {rend.name} (mesh: {mesh.name})");
// 			} else if (rend.bounds.IntersectRay(ray, out float boundsDist)) {
// 				// 2. Fallback: bounds
// 				hit = true;
// 				hitDist = boundsDist;
// 				currentHit = HitType.Bounds;
// 				Debug.Log($"Hit bounds at {hitDist}m for {rend.name} (mesh: {mesh.name})");
// 			}
//
// 			if (hit && hitDist < closest) {
// 				closest = hitDist;
// 				closestHit = currentHit;
// 			}
// 		}
//
// 		var newFocusDist = Mathf.Max(0.01f, closest);
// 		if (closestHit != HitType.None && newFocusDist != _dof.focusDistance.value) {
// 			_dof.focusDistance.value = newFocusDist;
// 			Debug.Log($"[AutoFocusDepthOfField] Focus set to {closest}->{newFocusDist}m ({closestHit})");
// 			// Force a repaint so the DoF effect refreshes immediately in Game view
// 			SceneView.RepaintAll();
// 			UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
// 		}
// 	}
// }
// #else
// public class AutoFocusDepthOfField : UnityEngine.MonoBehaviour {}
// #endif
