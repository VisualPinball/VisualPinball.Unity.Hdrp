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

// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using UnityEngine;
using VisualPinball.Unity;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VisualPinball.Unity.LookDev
{
	/// <summary>A reversible, Y-axis through cut for the lookdev playfield.</summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	[RequireComponent(typeof(MeshFilter))]
	[RequireComponent(typeof(MeshRenderer))]
	[AddComponentMenu("Pinball/LookDev/Playfield Insert Cutout")]
	public sealed class PlayfieldInsertCutout : MonoBehaviour
	{
		[Tooltip("The original, uncut playfield. Its mesh, transform and material slots are read without modifying it.")]
		public MeshFilter SourcePlayfield;

		[Tooltip("The insert whose outer outline is projected through the playfield along world Y.")]
		public InsertComponent Insert;

		[Min(0f)]
		[Tooltip("Outward offset of the cut in millimeters, along the outline normals. Prevents the insert and plywood walls from sharing a surface; keep it tiny, since the lit plywood wall shows down any wider gap as a bright rim.")]
		public float ClearanceMm = 0.02f;

		[Min(0)]
		[Tooltip("Existing material slot used for the walls inside the hole (1 is plywood on Playfield - Solid).")]
		public int WallMaterialIndex = 1;

		[Min(0.001f)]
		[Tooltip("Multiplier for the UV density sampled from the source's plywood faces. One matches the outside edges.")]
		public float WallUvScale = 1f;

		public string Error { get; private set; }

		[NonSerialized] private Mesh _preview;
		[NonSerialized] private MeshFilter _filter;
		[NonSerialized] private Mesh _originalOutput;
		private int _lastHash;
		private bool _dirty = true;
		private readonly List<Material> _sourceMaterials = new();

		private void OnEnable()
		{
			_filter = GetComponent<MeshFilter>();
			_originalOutput = _filter.sharedMesh;
			_dirty = true;
#if UNITY_EDITOR
			EditorApplication.update += EditorUpdate;
#endif
		}

		private void OnValidate() => _dirty = true;

		private void Update() => RefreshIfChanged();

#if UNITY_EDITOR
		private void EditorUpdate()
		{
			if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && !Application.isPlaying) {
				RefreshIfChanged();
			}
		}
#endif

		private void RefreshIfChanged()
		{
			var hash = InputHash();
			if (_dirty || hash != _lastHash) {
				Rebuild();
			}
		}

		private int InputHash()
		{
			unchecked {
				var hash = SourcePlayfield != null ? SourcePlayfield.GetHashCode() : 0;
				if (SourcePlayfield != null) {
					hash = hash * 31 + SourcePlayfield.transform.localToWorldMatrix.GetHashCode();
					hash = hash * 31 + (SourcePlayfield.sharedMesh != null ? SourcePlayfield.sharedMesh.GetHashCode() : 0);
					var renderer = SourcePlayfield.GetComponent<MeshRenderer>();
					_sourceMaterials.Clear();
					if (renderer != null) {
						renderer.GetSharedMaterials(_sourceMaterials);
						foreach (var material in _sourceMaterials) {
							hash = hash * 31 + (material != null ? material.GetHashCode() : 0);
						}
					}
				}
				hash = hash * 31 + transform.localToWorldMatrix.GetHashCode();
				hash = hash * 31 + WallMaterialIndex;
				hash = hash * 31 + ClearanceMm.GetHashCode();
				hash = hash * 31 + WallUvScale.GetHashCode();
				if (Insert != null) {
					hash = hash * 31 + Insert.GetHashCode();
					hash = hash * 31 + Insert.transform.localToWorldMatrix.GetHashCode();
					if (Insert.Outline != null) {
						foreach (var point in Insert.Outline.Points) {
							hash = hash * 31 + point.Position.GetHashCode();
						}
					}
				}
				return hash;
			}
		}

		[ContextMenu("Rebuild Cutout")]
		public void Rebuild()
		{
			_dirty = false;
			_filter = GetComponent<MeshFilter>();
			try {
				if (SourcePlayfield == null || SourcePlayfield.sharedMesh == null) {
					throw new InvalidOperationException("Assign Playfield - Solid as the source playfield.");
				}
				if (SourcePlayfield == _filter) {
					throw new InvalidOperationException("Use a separate preview object, with Playfield - Solid assigned as Source Playfield.");
				}
				var source = SourcePlayfield.sharedMesh;
				Mesh mesh;
				if (Insert == null) {
					mesh = PlayfieldCutoutMesh.Copy(source, SourcePlayfield.transform.localToWorldMatrix, transform.localToWorldMatrix);
				} else {
					if (Insert.Outline == null) {
						Insert.RebuildMesh();
					}
					if (Insert.Outline == null) {
						throw new InvalidOperationException("The insert has no generated outline.");
					}
					var outline = new Vector3[Insert.Outline.Points.Count];
					for (var i = 0; i < outline.Length; i++) {
						outline[i] = Insert.transform.TransformPoint(InsertMeshGenerator.ToLocal(Insert.Outline.Points[i].Position));
					}
					mesh = PlayfieldCutoutMesh.Cut(source, SourcePlayfield.transform.localToWorldMatrix, outline,
						WallMaterialIndex, WallUvScale, transform.localToWorldMatrix, ClearanceMm * 0.001f);
				}
				mesh.name = source.name + (Insert != null ? " (Insert Cutout Preview)" : " (Playfield Preview)");
				mesh.hideFlags = HideFlags.HideAndDontSave;
				Restore();
				_preview = mesh;
				_filter.sharedMesh = _preview;
				var sourceRenderer = SourcePlayfield.GetComponent<MeshRenderer>();
				if (sourceRenderer != null) {
					GetComponent<MeshRenderer>().sharedMaterials = sourceRenderer.sharedMaterials;
				}
				Error = null;
			} catch (Exception exception) {
				Restore();
				Error = exception.Message;
				Debug.LogWarning($"Playfield insert cutout: {Error}", this);
			} finally {
				_lastHash = InputHash();
			}
		}

		private void OnDisable()
		{
#if UNITY_EDITOR
			EditorApplication.update -= EditorUpdate;
#endif
			Restore();
		}

		private void Restore()
		{
			if (_filter != null && (_filter.sharedMesh == _preview || _preview == null)) {
				_filter.sharedMesh = _originalOutput;
			}
			if (_preview != null) {
				if (Application.isPlaying) {
					Destroy(_preview);
				} else {
					DestroyImmediate(_preview);
				}
				_preview = null;
			}
		}
	}
}
