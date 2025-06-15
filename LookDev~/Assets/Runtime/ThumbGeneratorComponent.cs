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
using System.IO;
using NetVips;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Cache = NetVips.Cache;
using Object = UnityEngine.Object;

namespace VisualPinball.Unity.Library
{
	[ExecuteInEditMode]
	public class ThumbGeneratorComponent : MonoBehaviour
	{
		[NonSerialized]
		public Object Prefab;

		public string ThumbnailGuid;
		public string ThumbnailRoot;

		private const int NumPreFrames = 40;
		private const int NumPostFrames = 20;
		private int _frame;

		public event EventHandler OnScreenshot;

		private void Start()
		{
			_frame = 0;
			ModuleInitializer.Initialize();
			Cache.MaxFiles = 0;
			Cache.Max = 0;
			EditorApplication.update += UpdateFrame;
			// TriggerRender();
		}

		private void UpdateFrame()
		{
			if (_frame == NumPreFrames) {
				Screenshot();
			}
			if (_frame == NumPreFrames + NumPostFrames - 1) {
				Resize(ThumbnailGuid);
			}
			if (_frame++ < NumPreFrames + NumPostFrames) {
				TriggerRender();
				return;
			}
			OnScreenshot?.Invoke(this, EventArgs.Empty);
		}

		private static void TriggerRender()
		{
			EditorApplication.QueuePlayerLoopUpdate();
			SceneView.RepaintAll();
			// InternalEditorUtility.RepaintAllViews();
		}

		private void Screenshot()
		{
			if (!string.IsNullOrEmpty(ThumbnailGuid)) {
				Screenshot(ThumbnailGuid);

			} else if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Prefab, out var guid, out long _)) {
				Screenshot(guid);

			} else {
				Debug.LogWarning($"Cannot find GUID for {Prefab.name}.");
			}
		}

		private void Screenshot(string guid)
		{
			try {
				var path = @$"{ThumbnailRoot}/{guid}_large.png";
				ScreenCapture.CaptureScreenshot(path, 2);
				Debug.Log($"Screenshot for \"{Prefab.name}\" saved at {path}");

			} catch (Exception e) {
				Debug.LogError(e);
			}
		}

		private void Resize(string guid)
		{
			Debug.Log($"{ThumbnailRoot}/{guid}_large.png -> {Path.GetFullPath($"{ThumbnailRoot}/{guid}_large.png")}");
			var src = Path.GetFullPath($"{ThumbnailRoot}/{guid}_large.png");
			var dest = Path.GetFullPath($"{ThumbnailRoot}/{guid}.webp");
			var large = Image.NewFromFile(src);
			if (large.Bands == 4) {
				large = large.ExtractBand(0, 3); // Keep only RGB
			}
			using var resized = large.Resize(0.5);
			large.Dispose();

			// Remove alpha channel if present
			resized.Webpsave(dest, q:90, smartSubsample:true);
			File.Delete(src);
		}
	}
}
