// Visual Pinball Engine
// Copyright (C) 2022 freezy and VPE Team
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
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VisualPinball.Unity.Editor;

namespace VisualPinball.Unity.Library
{
	public class ThumbGenerator : MonoBehaviour
	{
		[SerializeReference]
		public Editor.AssetLibrary AssetLibrary;

		[SerializeReference]
		public GameObject DefaultEnvironment;

		public bool IsProcessing { get; private set; }
		public int TotalProcessing { get; private set; }
		public int NumProcessed { get; private set; }


		private List<AssetMaterialCombination> _combinations;
		private GameObject _currentGo;
		private ThumbGeneratorComponent _currentTbc;
		private AssetMaterialCombination _currentAmc;
		private Camera _camera;
		private readonly Dictionary<string, GameObject> _environmentObjects = new();

		public void StartProcessing(bool newOnly = false, bool selectedOnly = false)
		{
			_camera = Camera.main;
			
			var bgParent = DefaultEnvironment.transform.parent.gameObject;
			_environmentObjects.Clear();
			if (bgParent != null) {
				foreach (var mr in bgParent.GetComponentsInChildren<MeshRenderer>(true)) {
					_environmentObjects[mr.name] = mr.gameObject;
				}
			}

			// var category = AssetLibrary.GetCategories().FirstOrDefault(c => c.Name.Contains("Flipper"));
			// //var category = AssetLibrary.GetCategories().FirstOrDefault(c => c.Name.Contains("Flipper"));
			// if (category != null) {
				//Debug.Log($"Category: {category}");
				var query = new LibraryQuery {
					//Keywords = "post -hex - 1.2"
					//Categories = new List<AssetCategory> { category }
				};
				var assets = AssetLibrary.GetAssets(query).ToArray();

				if (assets.Length == 0) {
					Debug.LogWarning("No assets found.");
					return;
				}

				_combinations = new List<AssetMaterialCombination>(assets
					.SelectMany(a => a.Asset.GetCombinations(true, true, false))
				);

				if (newOnly) {
					_combinations = _combinations.Where(a => !a.HasThumbnail).ToList();
				}
				if (selectedOnly) {
					var selectedAssets = new HashSet<Asset>(EditorWindow.GetWindow<AssetBrowser>().SelectedAssets);
					_combinations = _combinations.Where(a => selectedAssets.Contains(a.Asset)).ToList();
				}

				NumProcessed = 0;
				TotalProcessing = _combinations.Count;
				if (_combinations.Count > 0) {
					IsProcessing = true;
					Process(NextAsset());
				} else {
					Debug.Log("No assets found to process.");
				}

			// } else {
			// 	Debug.Log($"No category found.");
			// }
		}

		public void StopProcessing()
		{
			_combinations?.Clear();
			IsProcessing = false;
		}

		/// <summary>
		/// Renders the current view of the main camera and writes it as a PNG to
		/// <paramref name="path"/>. Independent of the thumbnail batch; captures whatever the
		/// camera currently frames.
		/// </summary>
		public void SaveCurrentFrame(string path)
		{
			if (string.IsNullOrEmpty(path)) {
				return;
			}
			var camera = Camera.main;
			if (camera == null) {
				Debug.LogError("Cannot save frame: no main camera found in the scene.");
				return;
			}

			var width = camera.pixelWidth > 0 ? camera.pixelWidth : 1920;
			var height = camera.pixelHeight > 0 ? camera.pixelHeight : 1080;
			var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
			var previousTarget = camera.targetTexture;
			var previousActive = RenderTexture.active;
			Texture2D texture = null;
			try {
				camera.targetTexture = renderTexture;
				camera.Render();

				RenderTexture.active = renderTexture;
				texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
				texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				texture.Apply();

				File.WriteAllBytes(path, texture.EncodeToPNG());
				Debug.Log($"Saved current camera frame to {path}");
			} catch (Exception e) {
				Debug.LogError($"Failed to save camera frame: {e}");
			} finally {
				camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				if (texture != null) {
					DestroyImmediate(texture);
				}
				renderTexture.Release();
				DestroyImmediate(renderTexture);
			}
		}

		private void Process(AssetMaterialCombination a)
		{
			// camera preset
			_currentAmc = a;
			if (a.Asset.ThumbCameraPreset != null) {
				a.Asset.ThumbCameraPreset.ApplyTo(_camera.transform);
			} else {
				AssetLibrary.DefaultThumbCameraPreset.ApplyTo(_camera.transform);
			}
			
			// background object
			if (a.Asset.EnvironmentGameObjectName != null && _environmentObjects.ContainsKey(a.Asset.EnvironmentGameObjectName)) {
				ToggleEnvironment(_environmentObjects[a.Asset.EnvironmentGameObjectName]);
			} else {
				ToggleEnvironment(DefaultEnvironment);
			}
			
			// instantiate prefab
			_currentGo = PrefabUtility.InstantiatePrefab(a.Asset.Object) as GameObject;

			// apply position and material
			a.ApplyObjectPos(_currentGo);
			a.ApplyMaterial(_currentGo);

			// launch generation
			Debug.Log($"Processing {_currentGo!.name}");
			_currentTbc = _currentGo!.AddComponent<ThumbGeneratorComponent>();
			_currentTbc!.ThumbnailRoot = a.Asset.Library.ThumbnailRoot;
			_currentTbc!.ThumbnailGuid = a.ThumbId;
			_currentTbc!.Prefab = a.Asset.Object;
			_currentTbc!.OnScreenshot += DoneProcessing;
		}

		private void ToggleEnvironment(GameObject go)
		{
			if (go.activeInHierarchy) {
				return;
			}
			foreach (var bgo in _environmentObjects.Values) {
				bgo.SetActive(false);
			}
			go.SetActive(true);
		}

		private void DoneProcessing(object sender, EventArgs e)
		{
			_currentTbc!.OnScreenshot -= DoneProcessing;
			DestroyImmediate(_currentGo);
			EditorWindow.GetWindow<AssetBrowser>().RefreshThumb(_currentAmc.Asset);

			var next = NextAsset();
			if (next != null) {
				Process(next);
			} else {
				AssetLibrary.DefaultThumbCameraPreset.ApplyTo(_camera.transform);
				Debug.Log("All done!");
				IsProcessing = false;
			}
		}

		private AssetMaterialCombination NextAsset()
		{
			if (_combinations.Count == 0) {
				return null;
			}
			var next = _combinations.First();
			if (!next.IsValidCombination) {
				_combinations.RemoveAt(0);
				return NextAsset();
			}
			_combinations.RemoveAt(0);
			NumProcessed++;
			return next;
		}
	}
}
