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


using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace VisualPinball.Unity.AssetLibrary
{
	[ExecuteAlways]
	public class CameraShadowAdjustment : MonoBehaviour
	{
		[NonSerialized]
		private Camera _camera;
		[NonSerialized]
		private bool _isCameraSet;

		//Stores the table bounds.
		private Bounds _tableBounds;
		private Vector3 _previousCameraPosition = Vector3.zero;

		private void OnEnable()
		{
			SetShadowDistance(6f);
		}

		private void Update()
		{
			if (!_isCameraSet)
			{
				RetrieveCameraComponent();
			}

			if (_isCameraSet)
			{
				if(_previousCameraPosition != _camera.transform.position)
				{
					UpdateShadowDistance();
					_previousCameraPosition = _camera.transform.position;
				}
			}
		}

		/// <summary>
		/// Gets the currently active or attached camera component.
		/// </summary>
		private void RetrieveCameraComponent()
		{
			_camera = Camera.main;  //Get the current active camera if not on the camera component.
			_isCameraSet = _camera;
		}

		//This is the same setup as the Clip distance.  Might want to combine these into a common function at some point.
		//TODO: Make common function and have Clip distance and Shadow distance use the result.
		private void UpdateShadowDistance()
		{
			//Early out if no table is selected.
			if(!TableSelector.Instance.HasSelectedTable)
			{
				return;
			}

			//Early out if we don't have a camera to work on.
			if(!_isCameraSet)
			{
				return;
			}

			//Get selected table reference.
			var table = TableSelector.Instance.SelectedTable;

			if(Application.isPlaying)
			{
				_tableBounds = table._tableBounds;  //When playing at runtime, get the stored table bounds value instead of calculating.
			}
			else
			{
				_tableBounds = table.GetTableBounds();  //When in editor, calculate the bounds in case things have changed.
			}

			var trans = _camera.transform;
			var cameraPos = trans.position; // camera position.
			var sphereExtent = _tableBounds.extents.magnitude; //sphere radius of the bounds.
			float cameraToCenter = Vector3.Distance(cameraPos, _tableBounds.center);
			var nearPlane = math.max(cameraPos.magnitude - sphereExtent, 0.001f); //Assign initial near plane used when camera is not in the sphere.
			var farPlane = math.max(1f, nearPlane + sphereExtent * 2f);           //initial far bounds

			if(cameraToCenter < sphereExtent)
			{
				farPlane = math.max(0.01f, sphereExtent + Vector3.Distance(cameraPos, _tableBounds.center)); //set far plane to delta between camera and furthest bound.
			}

			SetShadowDistance(farPlane);
		}

		/// <summary>
		/// Sets the shadow distance on the shadow volume override to the specified distance.
		/// </summary>
		/// <param name="distance">Distance in meters.</param>
		private void SetShadowDistance(float distance)
		{
			Volume volume = GetComponent<Volume>();

			VolumeProfile profile = volume.sharedProfile;
			if (profile.TryGet<HDShadowSettings>(out var shadows))
			{
				shadows.maxShadowDistance.value = distance;
			}

		}
	}
}
