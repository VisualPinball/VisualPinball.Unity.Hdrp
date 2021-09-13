﻿// Visual Pinball Engine
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
using UnityEngine;
using VisualPinball.Engine.VPT;
using VisualPinball.Unity;

namespace VisualPinball.Engine.Unity.Hdrp
{
	public class PrefabProvider : IPrefabProvider
	{
		public GameObject CreateBumper()
		{
			return UnityEngine.Resources.Load<GameObject>("Prefabs/Bumper");
		}
		public GameObject CreateGate(int type)
		{
			switch (type) {
				case GateType.GateLongPlate:
					return UnityEngine.Resources.Load<GameObject>("Prefabs/Gate - Long Plate");
				case GateType.GatePlate:
					return UnityEngine.Resources.Load<GameObject>("Prefabs/Gate - Plate");
				case GateType.GateWireRectangle:
					return UnityEngine.Resources.Load<GameObject>("Prefabs/Gate - Wire Rectangle");
				case GateType.GateWireW:
					return UnityEngine.Resources.Load<GameObject>("Prefabs/Gate - Wire W");
				default:
					throw new ArgumentException(nameof(type), $"Unknown gate type {type}.");
			}
		}
	}
}
