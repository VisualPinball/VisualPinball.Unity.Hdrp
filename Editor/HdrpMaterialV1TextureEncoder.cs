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
using NLog;
using UnityEngine;
using Logger = NLog.Logger;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	// Utility for round-tripping arbitrary Unity textures (compressed, GPU-only, etc.) into export
	// payloads. Goes via a temporary RenderTexture + ReadPixels so it works on assets without the
	// Read/Write flag.
	internal static class HdrpMaterialV1TextureEncoder
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		public static bool TryEncode(Texture2D source, bool linear, out byte[] pngData)
		{
			pngData = null;
			if (!TryReadTexturePixels(source, linear, out var readableTexture)) {
				return false;
			}

			try {
				pngData = readableTexture.EncodeToPNG();
				return pngData is { Length: > 0 };

			} catch (Exception e) {
				Logger.Warn(e, $"Unable to PNG-encode texture '{source.name}' for v1 material export.");
				return false;

			} finally {
				DestroyTexture(readableTexture);
			}
		}

		private static bool TryReadTexturePixels(Texture2D source, bool linear, out Texture2D readableTexture)
		{
			readableTexture = null;
			if (!source) {
				return false;
			}

			var readWrite = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
			var renderTexture = RenderTexture.GetTemporary(
				source.width, source.height, 0, RenderTextureFormat.ARGB32, readWrite);

			var previousRenderTexture = RenderTexture.active;
			try {
				Graphics.Blit(source, renderTexture);
				RenderTexture.active = renderTexture;
				readableTexture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, linear);
				readableTexture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
				readableTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
				return true;

			} catch (Exception e) {
				Logger.Warn(e, $"Unable to read texture '{source.name}' for v1 material export.");
				DestroyTexture(readableTexture);
				readableTexture = null;
				return false;

			} finally {
				RenderTexture.active = previousRenderTexture;
				RenderTexture.ReleaseTemporary(renderTexture);
			}
		}

		private static void DestroyTexture(Texture2D texture)
		{
			if (!texture) {
				return;
			}

			if (Application.isPlaying) {
				UnityEngine.Object.Destroy(texture);
			} else {
				UnityEngine.Object.DestroyImmediate(texture);
			}
		}
	}
}
