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
		const string AssetLibraryPath = "Packages/org.visualpinball.unity.assetlibrary/Assets/Library";

		public GameObject CreateBumper()
		{
			return AssetDatabase.LoadAssetAtPath<GameObject>($"{AssetLibraryPath}/Bumpers/VPX/Bumper.prefab");
		}

		public GameObject CreateGate(int type)
		{
			var gatesPath = $"{AssetLibraryPath}/Gates/VPX";
			switch (type) {
				case GateType.GateLongPlate:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate - Long Plate.prefab");
				case GateType.GatePlate:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate - Plate.prefab");
				case GateType.GateWireRectangle:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate - Wire Rectangle.prefab");
				case GateType.GateWireW:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{gatesPath}/Gate - Wire W.prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown gate type {type}.");
			}
		}

		public GameObject CreateKicker(int type)
		{
			var kickerPath = $"{AssetLibraryPath}/Kickers/VPX";
			switch (type) {
				case KickerType.KickerCup:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Cup 1.prefab");
				case KickerType.KickerCup2:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Cup 2.prefab");
				case KickerType.KickerGottlieb:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Gottlieb.prefab");
				case KickerType.KickerHole:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Hole Only.prefab");
				case KickerType.KickerHoleSimple:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Hole Only.prefab"); // todo make it "simple"
				case KickerType.KickerWilliams:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{kickerPath}/Kicker, Williams.prefab");
				case KickerType.KickerInvisible:
					return UnityEngine.Resources.Load<GameObject>("Prefabs/Kicker - Invisible");
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
			return AssetDatabase.LoadAssetAtPath<GameObject>($"{AssetLibraryPath}/Spinners/VPX/Spinner.prefab");
		}

		public GameObject CreateHitTarget(int type)
		{
			var targetsPath = $"{AssetLibraryPath}/Targets/VPX";
			switch (type) {
				case TargetType.HitFatTargetRectangle:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target - Rectangle Fat.prefab");
				case TargetType.HitFatTargetSlim:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target - Rectangle Fat Narrow.prefab");
				case TargetType.HitFatTargetSquare:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target - Square Fat.prefab");
				case TargetType.HitTargetRectangle:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target - Rectangle.prefab");
				case TargetType.HitTargetRound:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target - Round.prefab");
				case TargetType.HitTargetSlim:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Hit Target - Narrow.prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown hit target type {type}.");
			}
		}

		public GameObject CreateDropTarget(int type)
		{
			var targetsPath = $"{AssetLibraryPath}/Targets/VPX";
			switch (type) {
				case TargetType.DropTargetBeveled:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Drop Target - Beveled.prefab");
				case TargetType.DropTargetFlatSimple:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Drop Target - Simple Flat.prefab");
				case TargetType.DropTargetSimple:
					return AssetDatabase.LoadAssetAtPath<GameObject>($"{targetsPath}/Drop Target - Simple.prefab");
				default:
					throw new ArgumentException(nameof(type), $"Unknown drop target type {type}.");
			}
		}
	}
}
