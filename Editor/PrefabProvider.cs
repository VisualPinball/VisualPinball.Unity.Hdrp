// Visual Pinball Engine
// Copyright (C) 2020 freezy and VPE Team
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
using UnityEditor;
using UnityEngine;
using VisualPinball.Engine.VPT;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	public class PrefabProvider : IPrefabProvider
	{
		const string AssetLibraryPath = "Packages/org.visualpinball.unity.assets/Assets/Library";

		public GameObject CreateBumper()
		{
			return AssetDatabase.LoadAssetAtPath<GameObject>($"{AssetLibraryPath}/Bumpers/VPX/Bumper (VPX).prefab");
		}

		public GameObject CreateGate(int type)
		{
			var gatesPath = $"{AssetLibraryPath}/Gates/VPX";
			switch (type) {
				case GateType.GateLongPlate:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate, Plate, Long (VPX).prefab");
				case GateType.GatePlate:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate, Plate (VPX).prefab");
				case GateType.GateWireRectangle:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate, Wire, Rectangle (VPX).prefab");
				case GateType.GateWireW:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate, Wire, W (VPX).prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown gate type {type}.");
			}
		}

		public GameObject CreateKicker(int type)
		{
			var kickerPath = $"{AssetLibraryPath}/Kickers/VPX";
			switch (type) {
				case KickerType.KickerCup:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Cup 1 (VPX).prefab");
				case KickerType.KickerCup2:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Cup 2 (VPX).prefab");
				case KickerType.KickerGottlieb:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Gottlieb (VPX).prefab");
				case KickerType.KickerHole:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Hole (VPX).prefab");
				case KickerType.KickerHoleSimple:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Hole, Simple (VPX).prefab");
				case KickerType.KickerWilliams:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Williams (VPX).prefab");
				case KickerType.KickerInvisible:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Invisible.prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown kicker type {type}.");
			}
		}

		public GameObject CreateLight()
		{
			return UnityEngine.Resources.Load<GameObject>("Prefabs/Light");
		}

		public GameObject CreateInsertLight()
		{
			return UnityEngine.Resources.Load<GameObject>("Prefabs/Light - Insert");
		}

		public GameObject CreateSpinner()
		{
			return AssetDatabase.LoadAssetAtPath<GameObject>($"{AssetLibraryPath}/Spinners/VPX/Spinner (VPX).prefab");
		}

		public GameObject CreateHitTarget(int type)
		{
			var targetsPath = $"{AssetLibraryPath}/Targets/VPX";
			switch (type) {
				case TargetType.HitFatTargetRectangle:
					// -6.65565 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target, Rectangle, Fat (VPX).prefab");
				case TargetType.HitFatTargetSlim:
					// -6.31809 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target, Narrow, Fat (VPX).prefab");
				case TargetType.HitFatTargetSquare:
					// -6.67557 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target, Square, Fat (VPX).prefab");
				case TargetType.HitTargetRectangle:
					// -4.96337 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target, Rectangle (VPX).prefab");
				case TargetType.HitTargetRound:
					// -4.97161 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target, Round (VPX).prefab");
				case TargetType.HitTargetSlim:
					// -7.94514 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target, Narrow (VPX).prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown hit target type {type}.");
			}
		}

		public GameObject CreateDropTarget(int type)
		{
			var targetsPath = $"{AssetLibraryPath}/Targets/VPX";
			switch (type) {
				case TargetType.DropTargetBeveled:
					// -5.22032 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Drop Target, Beveled (VPX).prefab");
				case TargetType.DropTargetFlatSimple:
					// -8.58968 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Drop Target, Simple, Flat (VPX).prefab");
				case TargetType.DropTargetSimple:
					// -5.12326 mm
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Drop Target, Simple (VPX).prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown drop target type {type}.");
			}
		}

		public GameObject CreateFlipper() => UnityEngine.Resources.Load<GameObject>("Prefabs/Flipper");

		public GameObject CreatePlunger() => UnityEngine.Resources.Load<GameObject>("Prefabs/Plunger");

		public GameObject CreateTrough() => UnityEngine.Resources.Load<GameObject>("Prefabs/Trough");
	}
}
