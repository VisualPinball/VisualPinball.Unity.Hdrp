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
using UnityEditor;
using UnityEngine;
using VisualPinball.Unity;
using Logger = NLog.Logger;

namespace VisualPinball.Engine.Unity.Hdrp.Editor
{
	// Utility for round-tripping arbitrary Unity textures (compressed, GPU-only, etc.) into export
	// payloads. Goes via a temporary RenderTexture + ReadPixels so it works on assets without the
	// Read/Write flag.
	internal static class HdrpMaterialV1TextureEncoder
	{
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		// Result of cooking a texture into a GPU-ready payload: raw bytes (all mips, in
		// GetRawTextureData layout) plus the format/mip metadata the runtime needs to upload them.
		internal readonly struct CookedTexture
		{
			public readonly byte[] Data;
			public readonly string PixelFormat;
			public readonly int MipCount;
			public readonly int Width;
			public readonly int Height;

			public CookedTexture(byte[] data, string pixelFormat, int mipCount, int width, int height)
			{
				Data = data;
				PixelFormat = pixelFormat;
				MipCount = mipCount;
				Width = width;
				Height = height;
			}

			public bool IsValid => Data is { Length: > 0 } && !string.IsNullOrEmpty(PixelFormat);
		}

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

		// Cooks a color/mask texture into a BC7 payload (RGBA32 when dimensions don't allow block
		// compression) with mips baked at export time. Mip generation runs on the same RGBA32 data
		// the legacy PNG path produced, so cooked output matches what runtime used to compute.
		public static bool TryEncodeCooked(Texture2D source, bool linear, bool generateMipMaps, out CookedTexture cooked)
		{
			return TryCook(source, linear, generateMipMaps, repackNormal: false, out cooked);
		}

		// Cooks a tangent-space normal map: decodes the platform packing (plain RGB, DXT5nm or BC5
		// all read back correctly via x = r * a), re-packs into HDRP's expected AG layout
		// (1, y, 1, x) — the same transform the runtime CPU repack used to apply — and compresses
		// to DXT5, which is exactly what runtime Texture2D.Compress produced before.
		public static bool TryEncodeCookedNormal(Texture2D source, bool generateMipMaps, out CookedTexture cooked)
		{
			return TryCook(source, linear: true, generateMipMaps, repackNormal: true, out cooked);
		}

		private static bool TryCook(Texture2D source, bool linear, bool generateMipMaps, bool repackNormal, out CookedTexture cooked)
		{
			cooked = default;
			if (!TryReadTexturePixels(source, linear, out var readableTexture)) {
				return false;
			}

			Texture2D cookTexture = null;
			try {
				var width = readableTexture.width;
				var height = readableTexture.height;
				var pixels = readableTexture.GetPixels32();
				if (repackNormal) {
					for (var i = 0; i < pixels.Length; i++) {
						var p = pixels[i];
						// x = r * a covers every source packing: plain RGB (a=1 → x=r), DXT5nm
						// (r=1 → x=a) and BC5 (a=1 → x=r). Z is reconstructed in the shader.
						var x = (byte)((p.r * p.a + 127) / 255);
						pixels[i] = new Color32(255, p.g, 255, x);
					}
				}

				cookTexture = new Texture2D(width, height, TextureFormat.RGBA32, generateMipMaps, linear) {
					name = $"{source.name} (Cooked)",
				};
				cookTexture.SetPixels32(pixels);
				cookTexture.Apply(updateMipmaps: generateMipMaps, makeNoLongerReadable: false);

				// Block compression needs multiple-of-4 dimensions; everything else ships raw RGBA32,
				// which still uploads without a decode step.
				var pixelFormat = VpePixelFormats.Rgba32;
				if (width % 4 == 0 && height % 4 == 0 && width >= 4 && height >= 4) {
					if (repackNormal) {
						EditorUtility.CompressTexture(cookTexture, TextureFormat.DXT5, TextureCompressionQuality.Best);
						pixelFormat = VpePixelFormats.Dxt5;
					} else {
						EditorUtility.CompressTexture(cookTexture, TextureFormat.BC7, TextureCompressionQuality.Normal);
						pixelFormat = VpePixelFormats.Bc7;
					}
				}

				var rawData = cookTexture.GetRawTextureData();
				if (rawData == null || rawData.Length == 0) {
					Logger.Warn($"Cooking texture '{source.name}' produced no data; falling back to PNG side-channel.");
					return false;
				}

				cooked = new CookedTexture(rawData, pixelFormat, cookTexture.mipmapCount, width, height);
				return true;

			} catch (Exception e) {
				Logger.Warn(e, $"Unable to cook texture '{source.name}' for v1 material export.");
				return false;

			} finally {
				DestroyTexture(cookTexture);
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
