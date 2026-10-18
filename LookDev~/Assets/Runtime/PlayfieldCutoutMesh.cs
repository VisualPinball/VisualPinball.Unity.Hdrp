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
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using VisualPinball.Unity;

namespace VisualPinball.Unity.LookDev
{
	/// <summary>
	/// Subtracts a closed outline extruded along world Y from a closed playfield solid.
	/// BSP clipping keeps the original surface attributes and clips the new walls to
	/// the actual solid, including bevels and existing holes. No imported asset is edited.
	/// </summary>
	public static class PlayfieldCutoutMesh
	{
		private const float Epsilon = 0.0000001f;

		/// <summary>Copies the uncut solid into the preview's local space, retaining its surface attributes and material slots.</summary>
		public static Mesh Copy(Mesh source, Matrix4x4 localToWorld, Matrix4x4 outputLocalToWorld)
			=> Write(Read(source, localToWorld), source, outputLocalToWorld);

		public static Mesh Cut(Mesh source, Matrix4x4 localToWorld, IReadOnlyList<Vector3> worldOutline,
			int wallMaterialIndex = 1, float wallUvScale = 1f, Matrix4x4? outputLocalToWorld = null, float clearanceMeters = 0f)
		{
			if (source == null || worldOutline == null || worldOutline.Count < 3) {
				throw new ArgumentException("A playfield mesh and a closed insert outline with at least three points are required.");
			}
			if (wallMaterialIndex < 0 || wallMaterialIndex >= source.subMeshCount) {
				throw new ArgumentOutOfRangeException(nameof(wallMaterialIndex), "Choose an existing playfield material slot for the hole walls.");
			}
			if (math.abs(localToWorld.determinant) < 1e-12f) {
				throw new ArgumentException("The playfield transform must have nonzero scale.");
			}

			var original = Read(source, localToWorld);
			var points = worldOutline.Select(p => new double2(p.x, p.z)).ToList();
			var area = 0.0;
			for (var i = 0; i < points.Count; i++) {
				var a = points[i];
				var b = points[(i + 1) % points.Count];
				area += a.x * b.y - a.y * b.x;
			}
			if (math.abs(area) < Epsilon * Epsilon) {
				throw new ArgumentException("The insert outline has no area when projected along world Y.");
			}
			if (area < 0) {
				points.Reverse();
			}
			if (clearanceMeters > 0) {
				points = OffsetOutline(points, clearanceMeters);
			}
			var minY = original.SelectMany(p => p.Vertices).Min(v => v.Position.y);
			var maxY = original.SelectMany(p => p.Vertices).Max(v => v.Position.y);
			var margin = math.max(0.01f, maxY - minY);
			var wallUv = SampleWallUv(original, wallMaterialIndex, wallUvScale);
			var cutter = Prism(points, minY - margin, maxY + margin, wallMaterialIndex, wallUv);

			// A - B: invert A, keep the portions outside B, merge B's inside faces,
			// then invert the result. The cutter caps lie beyond the playfield bounds.
			var aTree = new Node(original);
			var bTree = new Node(cutter);
			aTree.Invert();
			aTree.ClipTo(bTree);
			bTree.ClipTo(aTree);
			bTree.Invert();
			bTree.ClipTo(aTree);
			bTree.Invert();
			aTree.Append(bTree.All());
			aTree.Invert();
			return Write(aTree.All(), source, outputLocalToWorld ?? localToWorld);
		}

		private static List<double2> OffsetOutline(List<double2> points, double clearance)
		{
			var expanded = new List<double2>(points.Count);
			for (var i = 0; i < points.Count; i++) {
				var previous = points[(i + points.Count - 1) % points.Count];
				var current = points[i];
				var next = points[(i + 1) % points.Count];
				var incoming = math.normalizesafe(current - previous);
				var outgoing = math.normalizesafe(next - current);
				var a = new double2(incoming.y, -incoming.x);
				var b = new double2(outgoing.y, -outgoing.x);
				var denominator = 1.0 + math.dot(a, b);
				if (denominator < 1e-10) {
					throw new ArgumentException("The insert outline doubles back at a corner and cannot be offset.");
				}
				// Intersect the two outward-offset edge lines. This maintains a
				// constant edge clearance at both convex and concave corners.
				expanded.Add(current + (a + b) * (clearance / denominator));
			}
			return expanded;
		}

		private sealed class Vertex
		{
			public Vector3 Position;
			public Vector3 Normal;
			public Vector4 Tangent;
			public Color Color;
			public Vector4[] Uv;

			public Vertex Lerp(Vertex other, float t) => new() {
				Position = Vector3.LerpUnclamped(Position, other.Position, t),
				Normal = Vector3.LerpUnclamped(Normal, other.Normal, t),
				Tangent = Vector4.LerpUnclamped(Tangent, other.Tangent, t),
				Color = UnityEngine.Color.LerpUnclamped(Color, other.Color, t),
				Uv = Uv.Select((uv, i) => Vector4.LerpUnclamped(uv, other.Uv[i], t)).ToArray(),
			};

			public Vertex Flipped() => new() {
				Position = Position, Normal = -Normal,
				Tangent = new Vector4(Tangent.x, Tangent.y, Tangent.z, -Tangent.w), Color = Color, Uv = Uv,
			};
		}

		private sealed class Polygon
		{
			public List<Vertex> Vertices;
			public int Material;
			public Plane Plane;

			public static Polygon Create(List<Vertex> vertices, int material)
			{
				// Clipping through an existing corner can leave consecutive duplicates.
				var clean = new List<Vertex>();
				foreach (var vertex in vertices) {
					if (clean.Count == 0 || (clean[clean.Count - 1].Position - vertex.Position).sqrMagnitude > Epsilon * Epsilon) {
						clean.Add(vertex);
					}
				}
				if (clean.Count > 1 && (clean[0].Position - clean[clean.Count - 1].Position).sqrMagnitude <= Epsilon * Epsilon) {
					clean.RemoveAt(clean.Count - 1);
				}
				for (var i = 2; i < clean.Count; i++) {
					var origin = (double3)(float3)clean[0].Position;
					var cross = math.cross((double3)(float3)clean[i - 1].Position - origin, (double3)(float3)clean[i].Position - origin);
					if (math.lengthsq(cross) > 1e-20) {
						var normal = math.normalize(cross);
						return new Polygon { Vertices = clean, Material = material, Plane = new Plane(normal, math.dot(normal, origin)) };
					}
				}
				return null;
			}

			public void Flip()
			{
				Vertices = Vertices.Select(v => v.Flipped()).Reverse().ToList();
				Plane = Plane.Flipped();
			}
		}

		private sealed class Plane
		{
			private readonly double3 _normal;
			private readonly double _distance;
			public Vector3 Normal => (float3)_normal;
			public Plane(double3 normal, double distance) { _normal = normal; _distance = distance; }
			public Plane Flipped() => new(-_normal, -_distance);
			private double DistanceTo(Vector3 point) => math.dot(_normal, (double3)(float3)point) - _distance;

			public void Split(Polygon polygon, List<Polygon> coplanarFront, List<Polygon> coplanarBack,
				List<Polygon> front, List<Polygon> back)
			{
				var types = new int[polygon.Vertices.Count];
				var type = 0;
				for (var i = 0; i < types.Length; i++) {
					var distance = DistanceTo(polygon.Vertices[i].Position);
					types[i] = distance < -Epsilon ? 2 : distance > Epsilon ? 1 : 0;
					type |= types[i];
				}
				switch (type) {
					case 0:
						(Vector3.Dot(Normal, polygon.Plane.Normal) > 0 ? coplanarFront : coplanarBack).Add(polygon);
						return;
					case 1: front.Add(polygon); return;
					case 2: back.Add(polygon); return;
				}
				var f = new List<Vertex>();
				var b = new List<Vertex>();
				for (var i = 0; i < types.Length; i++) {
					var j = (i + 1) % types.Length;
					var vi = polygon.Vertices[i];
					var vj = polygon.Vertices[j];
					if (types[i] != 2) { f.Add(vi); }
					if (types[i] != 1) { b.Add(vi); }
					if ((types[i] | types[j]) == 3) {
						var from = DistanceTo(vi.Position);
						var t = from / (from - DistanceTo(vj.Position));
						var vertex = vi.Lerp(vj, (float)math.clamp(t, 0.0, 1.0));
						f.Add(vertex);
						b.Add(vertex);
					}
				}
				var fp = Polygon.Create(f, polygon.Material);
				var bp = Polygon.Create(b, polygon.Material);
				if (fp != null) { front.Add(fp); }
				if (bp != null) { back.Add(bp); }
			}
		}

		private sealed class Node
		{
			private sealed class Budget
			{
				private int _steps;
				public int DepthLimit { get; private set; }
				public Budget(int polygonCount) => DepthLimit = polygonCount;
				public void AddPolygons(int count) => DepthLimit += count;
				public void Step()
				{
					if (++_steps > 250000) {
						throw new InvalidOperationException("The cut exceeded its geometry budget. Check that the source is a closed solid and the insert outline is valid.");
					}
				}
			}
			private readonly Budget _budget;
			private readonly int _depth;
			private Plane _plane;
			private Node _front;
			private Node _back;
			private readonly List<Polygon> _polygons = new();
			public Node(List<Polygon> polygons) : this(polygons, new Budget(polygons.Count), 0) { }
			private Node(List<Polygon> polygons, Budget budget, int depth)
			{
				_budget = budget;
				_depth = depth;
				// Every level owns a polygon. Bound depth by the actual input rather
				// than rejecting valid outlines with hundreds of rounded edge segments.
				if (depth > _budget.DepthLimit) { throw new InvalidOperationException("The cut failed to make progress through the source polygons."); }
				Build(polygons);
			}

			public void Append(List<Polygon> polygons)
			{
				_budget.AddPolygons(polygons.Count);
				Build(polygons);
			}

			private void Build(List<Polygon> polygons)
			{
				if (polygons.Count == 0) { return; }
				_plane ??= polygons[0].Plane;
				var front = new List<Polygon>();
				var back = new List<Polygon>();
				foreach (var polygon in polygons) {
					_budget.Step();
					if (ReferenceEquals(_plane, polygon.Plane)) {
						_polygons.Add(polygon);
						continue;
					}
					_plane.Split(polygon, _polygons, _polygons, front, back);
				}
				if (front.Count > 0) {
					if (_front == null) { _front = new Node(front, _budget, _depth + 1); } else { _front.Build(front); }
				}
				if (back.Count > 0) {
					if (_back == null) { _back = new Node(back, _budget, _depth + 1); } else { _back.Build(back); }
				}
			}

			private List<Polygon> Clip(List<Polygon> polygons)
			{
				if (_plane == null) { return polygons; }
				var front = new List<Polygon>();
				var back = new List<Polygon>();
				foreach (var polygon in polygons) {
					_budget.Step();
					_plane.Split(polygon, front, back, front, back);
				}
				if (_front != null) { front = _front.Clip(front); }
				if (_back != null) { front.AddRange(_back.Clip(back)); }
				return front;
			}

			public void ClipTo(Node other)
			{
				var clipped = other.Clip(_polygons);
				_polygons.Clear();
				_polygons.AddRange(clipped);
				_front?.ClipTo(other);
				_back?.ClipTo(other);
			}

			public void Invert()
			{
				foreach (var polygon in _polygons) { polygon.Flip(); }
				if (_plane != null) { _plane = _plane.Flipped(); }
				_front?.Invert();
				_back?.Invert();
				(_front, _back) = (_back, _front);
			}

			public List<Polygon> All()
			{
				var result = new List<Polygon>(_polygons);
				if (_front != null) { result.AddRange(_front.All()); }
				if (_back != null) { result.AddRange(_back.All()); }
				return result;
			}
		}

		private static List<Polygon> Read(Mesh source, Matrix4x4 matrix)
		{
			var positions = source.vertices;
			var normals = source.normals;
			var tangents = source.tangents;
			var colors = source.colors;
			var uv = new List<Vector4>[8];
			for (var c = 0; c < uv.Length; c++) {
				uv[c] = new List<Vector4>();
				source.GetUVs(c, uv[c]);
			}
			var normalMatrix = matrix.inverse.transpose;
			var mirrored = matrix.determinant < 0;
			var vertices = new Vertex[positions.Length];
			for (var i = 0; i < vertices.Length; i++) {
				var tangent = tangents.Length == vertices.Length ? tangents[i] : new Vector4(1, 0, 0, 1);
				var direction = matrix.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
				vertices[i] = new Vertex {
					Position = matrix.MultiplyPoint3x4(positions[i]),
					Normal = normals.Length == vertices.Length ? normalMatrix.MultiplyVector(normals[i]).normalized : Vector3.zero,
					Tangent = new Vector4(direction.x, direction.y, direction.z, mirrored ? -tangent.w : tangent.w),
					Color = colors.Length == vertices.Length ? colors[i] : Color.white,
					Uv = uv.Select(channel => channel.Count == vertices.Length ? channel[i] : Vector4.zero).ToArray(),
				};
			}
			var polygons = new List<Polygon>();
			for (var s = 0; s < source.subMeshCount; s++) {
				if (source.GetTopology(s) != MeshTopology.Triangles) {
					throw new ArgumentException("The playfield must contain triangle submeshes.");
				}
				var indices = source.GetTriangles(s);
				for (var i = 0; i < indices.Length; i += 3) {
					var polygon = Polygon.Create(new List<Vertex> {
						vertices[indices[i]], vertices[indices[i + (mirrored ? 2 : 1)]], vertices[indices[i + (mirrored ? 1 : 2)]],
					}, s);
					if (polygon != null) { polygons.Add(polygon); }
				}
			}
			if (polygons.Count == 0) { throw new ArgumentException("The playfield mesh contains no usable triangles."); }
			return polygons;
		}

		private struct WallUv
		{
			public Vector2 Origin;
			public float OriginY;
			public Vector2 Along;
			public Vector2 Up;
			public Vector2 At(float distance, float y) => Origin + Along * distance + Up * (y - OriginY);
		}

		private static WallUv SampleWallUv(List<Polygon> original, int material, float scale)
		{
			var mapping = new WallUv { Along = Vector2.right * scale, Up = Vector2.up * scale };
			var largest = 0f;
			foreach (var polygon in original) {
				if (polygon.Material != material || math.abs(polygon.Plane.Normal.y) > 0.001f) { continue; }
				var a = polygon.Vertices[0];
				var b = polygon.Vertices[1];
				var c = polygon.Vertices[2];
				var ab = b.Position - a.Position;
				var ac = c.Position - a.Position;
				var aa = Vector3.Dot(ab, ab);
				var bb = Vector3.Dot(ac, ac);
				var cc = Vector3.Dot(ab, ac);
				var determinant = aa * bb - cc * cc;
				if (determinant <= largest || determinant < 1e-20f) { continue; }
				Vector3 Gradient(float u, float v) => (ab * (u * bb - v * cc) + ac * (v * aa - u * cc)) / determinant;
				var gu = Gradient(b.Uv[0].x - a.Uv[0].x, c.Uv[0].x - a.Uv[0].x);
				var gv = Gradient(b.Uv[0].y - a.Uv[0].y, c.Uv[0].y - a.Uv[0].y);
				var along = Vector3.Cross(Vector3.up, polygon.Plane.Normal).normalized;
				var horizontal = new Vector2(Vector3.Dot(gu, along), Vector3.Dot(gv, along));
				var vertical = new Vector2(gu.y, gv.y);
				if (math.abs(horizontal.x * vertical.y - horizontal.y * vertical.x) < 1e-10f) { continue; }
				largest = determinant;
				mapping = new WallUv {
					Origin = new Vector2(a.Uv[0].x, a.Uv[0].y), OriginY = a.Position.y,
					Along = horizontal * scale, Up = vertical * scale,
				};
			}
			return mapping;
		}

		private static List<Polygon> Prism(List<double2> points, float bottom, float top, int material, WallUv wallUv)
		{
			var polygons = new List<Polygon>();
			Vertex Make(double2 point, float y, Vector3 normal, Vector3 tangent, Vector2 uv) => new() {
				Position = new Vector3((float)point.x, y, (float)point.y), Normal = normal,
				Tangent = new Vector4(tangent.x, tangent.y, tangent.z, -1), Color = Color.white,
				Uv = Enumerable.Range(0, 8).Select(_ => new Vector4(uv.x, uv.y, 0, 0)).ToArray(),
			};
			void Add(params Vertex[] vertices)
			{
				var polygon = Polygon.Create(vertices.ToList(), material);
				if (polygon != null) { polygons.Add(polygon); }
			}
			foreach (var t in InsertTriangulation.EarClip(points)) {
				Add(Make(points[t.x], bottom, Vector3.down, Vector3.right, Vector2.zero),
					Make(points[t.y], bottom, Vector3.down, Vector3.right, Vector2.zero),
					Make(points[t.z], bottom, Vector3.down, Vector3.right, Vector2.zero));
				Add(Make(points[t.z], top, Vector3.up, Vector3.right, Vector2.zero),
					Make(points[t.y], top, Vector3.up, Vector3.right, Vector2.zero),
					Make(points[t.x], top, Vector3.up, Vector3.right, Vector2.zero));
			}
			var distance = 0f;
			for (var i = 0; i < points.Count; i++) {
				var a = points[i];
				var b = points[(i + 1) % points.Count];
				var tangent = new Vector3((float)(b.x - a.x), 0, (float)(b.y - a.y)).normalized;
				var normal = Vector3.Cross(Vector3.up, tangent);
				var end = distance + (float)math.distance(a, b);
				var determinant = wallUv.Along.x * wallUv.Up.y - wallUv.Up.x * wallUv.Along.y;
				var uDirection = ((tangent * wallUv.Up.y - Vector3.up * wallUv.Along.y) / determinant).normalized;
				var vDirection = (Vector3.up * wallUv.Along.x - tangent * wallUv.Up.x) / determinant;
				var handedness = Vector3.Dot(Vector3.Cross(normal, uDirection), vDirection) < 0 ? -1f : 1f;
				var wall = new[] {
					Make(a, bottom, normal, uDirection, wallUv.At(distance, bottom)),
					Make(a, top, normal, uDirection, wallUv.At(distance, top)),
					Make(b, top, normal, uDirection, wallUv.At(end, top)),
					Make(b, bottom, normal, uDirection, wallUv.At(end, bottom)),
				};
				foreach (var vertex in wall) { vertex.Tangent.w = handedness; }
				Add(wall);
				distance = end;
			}
			return polygons;
		}

		private static Mesh Write(List<Polygon> polygons, Mesh source, Matrix4x4 matrix)
		{
			var vertices = new List<Vector3>();
			var normals = new List<Vector3>();
			var tangents = new List<Vector4>();
			var colors = new List<Color>();
			var uv = Enumerable.Range(0, 8).Select(_ => new List<Vector4>()).ToArray();
			var indices = Enumerable.Range(0, source.subMeshCount).Select(_ => new List<int>()).ToArray();
			var inverse = matrix.inverse;
			var normalMatrix = matrix.transpose;
			var mirrored = matrix.determinant < 0;
			foreach (var polygon in polygons) {
				var start = vertices.Count;
				foreach (var vertex in polygon.Vertices) {
					vertices.Add(inverse.MultiplyPoint3x4(vertex.Position));
					normals.Add(normalMatrix.MultiplyVector(vertex.Normal).normalized);
					var t = inverse.MultiplyVector(new Vector3(vertex.Tangent.x, vertex.Tangent.y, vertex.Tangent.z));
					var n = normals[normals.Count - 1];
					t = (t - n * Vector3.Dot(n, t)).normalized;
					tangents.Add(new Vector4(t.x, t.y, t.z, mirrored ? -vertex.Tangent.w : vertex.Tangent.w));
					colors.Add(vertex.Color);
					for (var c = 0; c < 8; c++) { uv[c].Add(vertex.Uv[c]); }
				}
				for (var i = 2; i < polygon.Vertices.Count; i++) {
					indices[polygon.Material].Add(start);
					indices[polygon.Material].Add(start + (mirrored ? i : i - 1));
					indices[polygon.Material].Add(start + (mirrored ? i - 1 : i));
				}
			}
			var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
			mesh.SetVertices(vertices);
			mesh.SetNormals(normals);
			mesh.SetTangents(tangents);
			if (source.HasVertexAttribute(VertexAttribute.Color)) { mesh.SetColors(colors); }
			for (var c = 0; c < 8; c++) {
				var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + c);
				if (!source.HasVertexAttribute(attribute)) { continue; }
				var dimension = source.GetVertexAttributeDimension(attribute);
				if (dimension == 2) { mesh.SetUVs(c, uv[c].Select(v => new Vector2(v.x, v.y)).ToList()); }
				else if (dimension == 3) { mesh.SetUVs(c, uv[c].Select(v => new Vector3(v.x, v.y, v.z)).ToList()); }
				else { mesh.SetUVs(c, uv[c]); }
			}
			mesh.subMeshCount = source.subMeshCount;
			for (var s = 0; s < indices.Length; s++) { mesh.SetTriangles(indices[s], s); }
			if (!source.HasVertexAttribute(VertexAttribute.Normal)) { mesh.RecalculateNormals(); }
			mesh.RecalculateBounds();
			return mesh;
		}
	}
}
