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

// ReSharper disable CheckNamespace

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using VisualPinball.Unity;
using Object = UnityEngine.Object;

namespace VisualPinball.Engine.Unity.Hdrp
{
	/// <summary>
	/// Draws a light's shadows of what stands still once, and only what moves every frame.
	/// </summary>
	///
	/// <remarks>
	/// A light redrawing its shadows every frame casts the whole table into them, which on a slow CPU is the
	/// most expensive thing that's switched on with the shadows (a directional light and two spots: 2100
	/// casters, 1.1 M triangles, 2.7 ms a frame on an i5-2500K, at any resolution; 0.8 ms with this). HDRP's
	/// mixed cached shadows keep the static casters in a cached map and draw only the dynamic ones on top each
	/// frame. Dynamic from the start are the balls, the flippers, the parts an animation component moves (gate
	/// wires, spinner plates, bumper rings and skirts, targets, the plunger...), what an animator drives, and
	/// meshes that bend in place (slingshot and deformable rubbers, skinned meshes); everything else starts
	/// static. A static caster that turns out to move (a kicker on a rotator, a toy) is
	/// caught within a few frames by its transform's <c>hasChanged</c> and is dynamic from then on; one that
	/// is hidden or shown has the cached map drawn again, and goes dynamic if it keeps doing that. Main
	/// components aren't taken as movers by their interfaces: a gate's or a bumper's main component holds
	/// static parts too, and a motion controller can sit high up the hierarchy. The cached map is also drawn
	/// again when the camera changes, since a directional light's cascade follows it, and when a static caster
	/// that cast a shadow is destroyed.
	///
	/// For the lights that draw every frame (one whose author chose to cache it is left as it is): directional
	/// ones where the pipeline asset keeps a cached directional atlas (<c>allowDirectionalMixedCachedShadows</c>;
	/// without it, HDRP would cache everything, balls included), and spots while their maps fit the cached
	/// punctual atlas. Points (six maps each) and area lights keep drawing every frame. The lights are cached
	/// on demand: the cached map is drawn when placed and when asked to (HDRP ignores the asking for lights
	/// cached on enable). A spot that the atlas has no place for after all draws every frame again.
	/// </remarks>
	internal sealed class HdrpShadowCache : MonoBehaviour
	{
		private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

		// the static casters checked per frame, how many changing at once is a moved table, and how often one
		// may be hidden or shown before it's drawn every frame
		private const int SweepPerFrame = 256;
		private const int MovedTable = 64;
		private const int Toggles = 3;
		// frames between looks for a new table, and after one before its renderers are sorted (it loads over a few)
		private const int TablePollFrames = 60;
		private static readonly int[] ScanDelays = { 30, 300 };
		// frames after a spot is cached before its place in the atlas is checked (it's placed once in view)
		private const int PlacementCheckFrames = 120;

		private static HdrpShadowCache _instance;

		private readonly Dictionary<HDAdditionalLightData, (ShadowUpdateMode Mode, bool AlwaysDynamic, bool UponMovement)> _lights = new();
		private readonly List<StaticCaster> _static = new();
		private readonly HashSet<Transform> _demoted = new();
		private readonly List<HDAdditionalLightData> _gone = new();
		// a cached spot's map size as it was placed, and when its placement is checked
		private readonly Dictionary<HDAdditionalLightData, int> _sizes = new();
		private readonly Dictionary<HDAdditionalLightData, int> _placementChecks = new();
		private readonly List<StaticCaster> _destroyed = new();
		private static readonly Dictionary<Type, bool> MoverTypes = new();
		private static readonly HashSet<HDAdditionalLightData> Unfitting = new();

		private TableComponent _table;
		private int _framesToTablePoll;
		private int _scanIndex = -1;
		private int _framesToScan;
		private int _sweep;
		private bool _request;
		private Hash128 _view;

		/// <summary>
		/// Lights were set up, or the graphics settings applied: the pipeline asset may have changed, and the
		/// shadows' resolution.
		/// </summary>
		internal static void Refresh()
		{
			if (!Application.isPlaying || GraphicsSettings.currentRenderPipeline is not HDRenderPipelineAsset asset) {
				return;
			}
			var init = asset.currentPlatformRenderPipelineSettings.hdShadowInitParams;
			var lights = Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include);
			// The spots' cached maps share an atlas with the spots and points (six maps each) their authors cache.
			// Their sizes are powers of two, which pack with little loss; a quarter is left for that. A spot that
			// doesn't find a place after all is drawn every frame again (see CheckPlacement).
			var budget = (long)init.cachedPunctualLightShadowAtlas * init.cachedPunctualLightShadowAtlas * 3 / 4;
			foreach (var hd in lights) {
				if (hd.TryGetComponent<Light>(out var light) && light.type is LightType.Spot or LightType.Point && light.shadows != LightShadows.None
					&& hd.shadowUpdateMode != ShadowUpdateMode.EveryFrame && (_instance == null || !_instance._lights.ContainsKey(hd))) {
					budget -= Area(hd, init) * (light.type == LightType.Point ? 6 : 1);
				}
			}
			foreach (var hd in lights) {
				Configure(hd, init, ref budget);
			}
			if (_instance != null) {
				_instance._request = true;
			}
		}

		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HDAdditionalLightData, object> AuthoredLights = new();

		internal static void SetAuthoredSettings(HDAdditionalLightData light, bool hasCustomCacheSettings)
		{
			_instance?.Restore(light);
			if (hasCustomCacheSettings) {
				AuthoredLights.GetOrCreateValue(light);
			} else {
				AuthoredLights.Remove(light);
			}
		}

		private static void Configure(HDAdditionalLightData hd, HDShadowInitParameters init, ref long budget)
		{
			if (AuthoredLights.TryGetValue(hd, out _) || !hd.TryGetComponent<Light>(out var light)) {
				return;
			}
			var cache = _instance;
			var ours = cache != null && cache._lights.ContainsKey(hd);
			if (ours && hd.shadowUpdateMode == ShadowUpdateMode.EveryFrame) {
				// set up anew to draw every frame: that's what it gives back
				cache._lights[hd] = (ShadowUpdateMode.EveryFrame, hd.alwaysDrawDynamicShadows, hd.updateUponLightMovement);
			}
			if (!ours && hd.shadowUpdateMode != ShadowUpdateMode.EveryFrame) {
				return; // its author chose how it caches
			}
			if (light.shadows == LightShadows.None) {
				return; // nothing to draw, and nothing taken from the atlas
			}
			var cached = light.type switch {
				LightType.Directional => init.allowDirectionalMixedCachedShadows,
				LightType.Spot => Fits(Area(hd, init), ref budget),
				_ => false,
			};
			if (!cached) {
				if (light.type == LightType.Spot && (ours || !Unfitting.Contains(hd))) {
					Unfitting.Add(hd);
					Logger.Info("Shadows of {Light:l} drawn every frame: its {Size} map doesn't fit the cached atlas ({Atlas}).",
						hd.name, Size(hd, init), init.cachedPunctualLightShadowAtlas);
				}
				cache?.Restore(hd);
				return;
			}
			Unfitting.Remove(hd);
			cache = Instance();
			if (!cache._lights.ContainsKey(hd)) {
				cache._lights[hd] = (hd.shadowUpdateMode, hd.alwaysDrawDynamicShadows, hd.updateUponLightMovement);
				Logger.Info("Shadows of {Light:l} ({Type}): static casters cached, dynamic ones every frame.", hd.name, light.type);
			}
			// before the mode: switching it registers the light, which is when HDRP looks at whether it follows
			// the light's movement
			hd.alwaysDrawDynamicShadows = true;
			hd.updateUponLightMovement = true;
			hd.SetShadowUpdateMode(ShadowUpdateMode.OnDemand);
			if (light.type == LightType.Spot) {
				// a map placed at another size keeps it: placed anew at the new one, which the request does (and
				// draws it there)
				var size = Size(hd, init);
				if (cache._sizes.TryGetValue(hd, out var placed) && placed != size && HDCachedShadowManager.instance != null) {
					HDCachedShadowManager.instance.ForceEvictLight(hd);
					hd.RequestShadowMapRendering();
				}
				cache._sizes[hd] = size;
				cache._placementChecks[hd] = Time.frameCount + PlacementCheckFrames;
			}
			cache._request = true;
		}

		private static long Area(HDAdditionalLightData hd, HDShadowInitParameters init)
		{
			long size = Size(hd, init);
			return size * size;
		}

		// the map's size as HDRP allocates it: what the light asks for, up to the pipeline asset's maximum
		private static int Size(HDAdditionalLightData hd, HDShadowInitParameters init)
			=> Mathf.Min((int)hd.shadowResolution.Value(init.shadowResolutionPunctual), init.maxPunctualShadowMapResolution);

		private static bool Fits(long area, ref long budget)
		{
			if (area > budget) {
				return false;
			}
			budget -= area;
			return true;
		}

		private static HdrpShadowCache Instance()
		{
			if (_instance == null) {
				var go = new GameObject("VPE Shadow Cache") { hideFlags = HideFlags.HideAndDontSave };
				DontDestroyOnLoad(go);
				_instance = go.AddComponent<HdrpShadowCache>();
			}
			return _instance;
		}

		private void Restore(HDAdditionalLightData hd)
		{
			_sizes.Remove(hd);
			_placementChecks.Remove(hd);
			if (_lights.Remove(hd, out var original)) {
				hd.SetShadowUpdateMode(original.Mode);
				hd.alwaysDrawDynamicShadows = original.AlwaysDynamic;
				hd.updateUponLightMovement = original.UponMovement;
			}
		}

		private void LateUpdate()
		{
			if (_lights.Count == 0) {
				return;
			}
			PollTable();
			if (_view != (_view = ViewHash())) {
				_request = true;
			}
			Sweep();
			CheckPlacement();
			if (_request) {
				_request = false;
				_gone.Clear();
				foreach (var light in _lights.Keys) {
					if (light == null) {
						_gone.Add(light);
					} else {
						light.RequestShadowMapRendering();
					}
				}
				foreach (var light in _gone) {
					_lights.Remove(light);
				}
			}
		}

		// A spot the atlas found no place for casts no shadow at all: it draws every frame again.
		private void CheckPlacement()
		{
			if (_placementChecks.Count == 0 || HDCachedShadowManager.instance == null) {
				return;
			}
			_gone.Clear();
			foreach (var (light, frame) in _placementChecks) {
				if (light == null || Time.frameCount >= frame) {
					_gone.Add(light);
				}
			}
			foreach (var light in _gone) {
				if (light != null && !light.isActiveAndEnabled) {
					// placed once it's on: checked again later
					_placementChecks[light] = Time.frameCount + PlacementCheckFrames;
					continue;
				}
				_placementChecks.Remove(light);
				if (light == null || HDCachedShadowManager.instance.LightHasBeenPlacedInAtlas(light)) {
					continue;
				}
				Unfitting.Add(light);
				Logger.Info("Shadows of {Light:l} drawn every frame: the cached atlas has no place for its map.", light.name);
				Restore(light);
			}
			_gone.Clear();
		}

		private void PollTable()
		{
			if (_scanIndex >= 0 && --_framesToScan <= 0) {
				Scan();
				_scanIndex++;
				if (_scanIndex < ScanDelays.Length) {
					_framesToScan = ScanDelays[_scanIndex] - ScanDelays[_scanIndex - 1];
				} else {
					_scanIndex = -1;
				}
			}
			if (--_framesToTablePoll > 0) {
				return;
			}
			_framesToTablePoll = TablePollFrames;
			var table = Object.FindAnyObjectByType<TableComponent>();
			if (table != _table) {
				_table = table;
				_demoted.Clear();
				_scanIndex = 0;
				_framesToScan = ScanDelays[0];
			}
		}

		// Sorts the scene's renderers into static and dynamic shadow casters.
		private void Scan()
		{
			var dynamic = new HashSet<Renderer>();
			var byType = new Dictionary<string, int>();
			foreach (var behaviour in Object.FindObjectsByType<Behaviour>(FindObjectsInactive.Include)) {
				if (IsMover(behaviour.GetType())) {
					var before = dynamic.Count;
					foreach (var r in behaviour.GetComponentsInChildren<Renderer>(true)) {
						dynamic.Add(r);
					}
					var name = behaviour.GetType().Name;
					byType[name] = (byType.TryGetValue(name, out var n) ? n : 0) + dynamic.Count - before;
				}
			}
			_static.Clear();
			var byTransform = new Dictionary<Transform, StaticCaster>();
			var statics = 0;
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include)) {
				if (!VpeMaterialReader.HasAuthoredStaticShadowCaster(r)) {
					// A skinned mesh bends without its transform moving.
					var moves = dynamic.Contains(r) || r is SkinnedMeshRenderer || _demoted.Contains(r.transform);
					r.staticShadowCaster = !moves;
				}
				// Authored static casters still invalidate a cached studio shadow when they change.
				if (!r.staticShadowCaster) {
					continue;
				}
				statics++;
				var t = r.transform;
				if (!byTransform.TryGetValue(t, out var caster)) {
					byTransform[t] = caster = new StaticCaster(t);
					_static.Add(caster);
				}
				caster.Renderers.Add(r);
				t.hasChanged = false;
			}
			foreach (var caster in _static) {
				caster.Shown = caster.IsShown();
			}
			_sweep = 0;
			_request = true;
			var movers = new List<string>();
			foreach (var kv in byType) {
				movers.Add($"{kv.Key} {kv.Value}");
			}
			Logger.Info("Shadow casters: {Static} static, {Dynamic} dynamic ({Movers:l}).", statics, dynamic.Count + _demoted.Count, string.Join(", ", movers));
		}

		// The balls, the flippers, what an animation component moves, what an animator drives, and the meshes
		// that bend in place.
		private static bool IsMover(Type type)
		{
			if (MoverTypes.TryGetValue(type, out var mover)) {
				return mover;
			}
			mover = typeof(BallComponent).IsAssignableFrom(type)
				|| typeof(FlipperComponent).IsAssignableFrom(type)
				|| typeof(Animator).IsAssignableFrom(type)
				|| typeof(Animation).IsAssignableFrom(type)
				|| typeof(SlingshotComponent).IsAssignableFrom(type)
				|| typeof(RubberDeformationRenderer).IsAssignableFrom(type)
				|| DerivesFrom(type, typeof(AnimationComponent<>));
			MoverTypes[type] = mover;
			return mover;
		}

		private static bool DerivesFrom(Type type, Type generic)
		{
			for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType) {
				if (t.IsGenericType && t.GetGenericTypeDefinition() == generic) {
					return true;
				}
			}
			return false;
		}

		// A static caster that moved goes dynamic for good; many at once means the table itself moved. One
		// hidden or shown leaves its shadow in the cached map, which is drawn again.
		private void Sweep()
		{
			if (_static.Count == 0) {
				return;
			}
			var count = Mathf.Min(SweepPerFrame, _static.Count);
			List<StaticCaster> moved = null;
			for (var i = 0; i < count; i++) {
				_sweep = (_sweep + 1) % _static.Count;
				var caster = _static[_sweep];
				if (caster.Transform == null) {
					// its shadow stays in the cached map until that's drawn again
					_destroyed.Add(caster);
					continue;
				}
				var shown = caster.IsShown();
				if (shown != caster.Shown) {
					caster.Shown = shown;
					caster.Toggles++;
					_request = true;
					if (caster.Toggles >= Toggles) {
						(moved ??= new List<StaticCaster>()).Add(caster);
						continue;
					}
				}
				if (caster.Transform.hasChanged) {
					(moved ??= new List<StaticCaster>()).Add(caster);
				}
			}
			if (_destroyed.Count > 0) {
				foreach (var caster in _destroyed) {
					_request |= caster.Shown;
					_static.Remove(caster);
				}
				_destroyed.Clear();
			}
			if (moved == null) {
				return;
			}
			_request = true;
			if (moved.Count >= MovedTable) {
				foreach (var caster in _static) {
					if (caster.Transform != null) {
						caster.Transform.hasChanged = false;
					}
				}
				return;
			}
			foreach (var caster in moved) {
				_demoted.Add(caster.Transform);
				foreach (var r in caster.Renderers) {
					if (r != null) {
						r.staticShadowCaster = false;
					}
				}
				_static.Remove(caster);
				Logger.Info("Shadow caster {Name:l} {Why}: drawn every frame from now on.", caster.Transform.name, caster.Toggles >= Toggles ? "comes and goes" : "moves");
			}
		}

		private sealed class StaticCaster
		{
			public readonly Transform Transform;
			public readonly List<Renderer> Renderers = new(1);
			public bool Shown;
			public int Toggles;

			public StaticCaster(Transform transform) => Transform = transform;

			public bool IsShown()
			{
				foreach (var r in Renderers) {
					if (r != null && r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode != ShadowCastingMode.Off) {
						return true;
					}
				}
				return false;
			}
		}

		// The cascade follows the camera: where it stands and what it sees.
		private static Hash128 ViewHash()
		{
			var cam = Camera.main;
			if (cam == null) {
				return default;
			}
			var hash = new Hash128();
			var t = cam.transform;
			var position = t.position;
			var rotation = t.rotation;
			hash.Append(position.x); hash.Append(position.y); hash.Append(position.z);
			hash.Append(rotation.x); hash.Append(rotation.y); hash.Append(rotation.z); hash.Append(rotation.w);
			hash.Append(cam.fieldOfView); hash.Append(cam.aspect); hash.Append(cam.nearClipPlane); hash.Append(cam.farClipPlane);
			hash.Append(cam.orthographic ? cam.orthographicSize : 0f);
			return hash;
		}

		private void OnDestroy()
		{
			if (_instance == this) {
				_instance = null;
			}
		}
	}
}
