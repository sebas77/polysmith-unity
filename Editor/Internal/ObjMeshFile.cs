using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Polysmith
{
    /// <summary>
    /// Unity meshes to OBJ objects and back, in the convention Unity's OBJ importer reads: X is negated, so the winding
    /// is reversed to keep triangles facing outward. Submesh n is the material group "slot{n}". Each mesh is written
    /// placed in the prefab (its placement maps mesh space to prefab space), so Polysmith shows the model assembled.
    /// </summary>
    static class ObjMeshFile
    {
        public static string GroupName(int submesh) => "slot" + submesh.ToString(CultureInfo.InvariantCulture);

        // X is mirrored, which turns triangles inside out; a mirrored placement turns them back.
        public static bool ReversesWinding(Matrix4x4 placement) => placement.determinant >= 0;

        /// <summary>
        /// Reads an OBJ that holds one object, written with <paramref name="placement"/>, back into mesh space, with
        /// <paramref name="submeshCount"/> submeshes each from its own group; a group that is missing gives an empty
        /// submesh. Returns null, with a warning, when a group is not one of ours (Polysmith names atlas and billboard
        /// groups its own way).
        /// </summary>
        public static Mesh Read(string path, int submeshCount, Matrix4x4 placement, List<string> warnings)
        {
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var corners = new Dictionary<(int, int, int), int>();
            var cornerList = new List<(int Position, int Uv, int Normal)>();
            var triangles = new List<int>[submeshCount];
            for (int submesh = 0; submesh < submeshCount; submesh++)
                triangles[submesh] = new List<int>();
            int current = 0;
            bool reverse = ReversesWinding(placement);

            foreach (string line in File.ReadLines(path))
            {
                string[] tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;
                switch (tokens[0])
                {
                    case "v":
                        positions.Add(new Vector3(-Number(tokens[1]), Number(tokens[2]), Number(tokens[3])));
                        break;
                    case "vt":
                        uvs.Add(new Vector2(Number(tokens[1]), Number(tokens[2])));
                        break;
                    case "vn":
                        normals.Add(new Vector3(-Number(tokens[1]), Number(tokens[2]), Number(tokens[3])));
                        break;
                    case "usemtl":
                        string group = tokens.Length > 1 ? tokens[1] : "";
                        current = SubmeshOf(group, submeshCount);
                        if (current < 0)
                        {
                            warnings.Add($"'{path}' has material group '{group}', which is not one of the mesh's " +
                                         "submeshes; the mesh was not replaced.");
                            return null;
                        }
                        break;
                    case "f":
                        var face = new int[tokens.Length - 1];
                        for (int k = 0; k < face.Length; k++)
                            face[k] = CornerIndex(tokens[k + 1], positions.Count, uvs.Count, normals.Count, corners, cornerList);
                        for (int k = 1; k + 1 < face.Length; k++)
                            triangles[current].AddRange(reverse
                                ? new[] { face[0], face[k + 1], face[k] }
                                : new[] { face[0], face[k], face[k + 1] });
                        break;
                }
            }

            bool hasUvs = cornerList.TrueForAll(corner => corner.Uv >= 0);
            bool hasNormals = cornerList.TrueForAll(corner => corner.Normal >= 0);
            var vertices = new Vector3[cornerList.Count];
            var vertexUvs = new Vector2[hasUvs ? cornerList.Count : 0];
            var vertexNormals = new Vector3[hasNormals ? cornerList.Count : 0];
            // Normals were written with the inverse transpose, so its inverse, the transpose, brings them back.
            var toMesh = placement.inverse;
            var normalsToMesh = placement.transpose;
            for (int k = 0; k < cornerList.Count; k++)
            {
                vertices[k] = toMesh.MultiplyPoint3x4(positions[cornerList[k].Position]);
                if (hasUvs)
                    vertexUvs[k] = uvs[cornerList[k].Uv];
                if (hasNormals)
                    vertexNormals[k] = normalsToMesh.MultiplyVector(normals[cornerList[k].Normal]).normalized;
            }

            var mesh = new Mesh { indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            if (hasUvs)
                mesh.SetUVs(0, vertexUvs);
            mesh.subMeshCount = submeshCount;
            for (int submesh = 0; submesh < submeshCount; submesh++)
                mesh.SetTriangles(triangles[submesh], submesh);
            if (hasNormals)
                mesh.SetNormals(vertexNormals);
            else
                mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static int SubmeshOf(string group, int submeshCount)
        {
            for (int submesh = 0; submesh < submeshCount; submesh++)
                if (group == GroupName(submesh))
                    return submesh;
            return -1;
        }

        // One vertex per distinct (position, UV, normal) triple. OBJ indices are 1-based, or negative from the end.
        static int CornerIndex(string token, int positionCount, int uvCount, int normalCount,
            Dictionary<(int, int, int), int> corners, List<(int Position, int Uv, int Normal)> cornerList)
        {
            string[] parts = token.Split('/');
            int position = Index(parts[0], positionCount);
            int uv = parts.Length > 1 && parts[1].Length > 0 ? Index(parts[1], uvCount) : -1;
            int normal = parts.Length > 2 && parts[2].Length > 0 ? Index(parts[2], normalCount) : -1;
            var key = (position, uv, normal);
            if (corners.TryGetValue(key, out int index) == false)
            {
                index = cornerList.Count;
                corners[key] = index;
                cornerList.Add(key);
            }
            return index;
        }

        static int Index(string text, int count)
        {
            int index = int.Parse(text, CultureInfo.InvariantCulture);
            return index < 0 ? count + index : index - 1;
        }

        static float Number(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Writes Unity meshes as the objects of one OBJ file (see <see cref="ObjMeshFile"/>).</summary>
    sealed class ObjWriter
    {
        /// <summary>Appends <paramref name="mesh"/> placed by <paramref name="placement"/> (mesh space to prefab space).</summary>
        public void Append(string objectName, Mesh mesh, Matrix4x4 placement)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            bool hasNormals = normals.Length == vertices.Length;
            bool hasUvs = uvs.Length == vertices.Length;
            bool reverse = ObjMeshFile.ReversesWinding(placement);
            var normalsToPrefab = placement.inverse.transpose;

            _obj.Append("o ").AppendLine(objectName);
            foreach (var vertex in vertices)
            {
                var v = placement.MultiplyPoint3x4(vertex);
                _obj.Append("v ").Append(Format(-v.x)).Append(' ').Append(Format(v.y)).Append(' ').AppendLine(Format(v.z));
            }
            if (hasUvs)
                foreach (var uv in uvs)
                    _obj.Append("vt ").Append(Format(uv.x)).Append(' ').AppendLine(Format(uv.y));
            if (hasNormals)
                foreach (var normal in normals)
                {
                    var n = normalsToPrefab.MultiplyVector(normal).normalized;
                    _obj.Append("vn ").Append(Format(-n.x)).Append(' ').Append(Format(n.y)).Append(' ').AppendLine(Format(n.z));
                }

            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                _obj.Append("usemtl ").AppendLine(ObjMeshFile.GroupName(submesh));
                int[] triangles = mesh.GetTriangles(submesh);
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    _obj.Append('f');
                    var corners = reverse
                        ? new[] { triangles[t], triangles[t + 2], triangles[t + 1] }
                        : new[] { triangles[t], triangles[t + 1], triangles[t + 2] };
                    foreach (int vertex in corners)
                    {
                        _obj.Append(' ').Append(_positions + vertex + 1);
                        if (hasUvs || hasNormals)
                            _obj.Append('/');
                        if (hasUvs)
                            _obj.Append(_uvs + vertex + 1);
                        if (hasNormals)
                            _obj.Append('/').Append(_normals + vertex + 1);
                    }
                    _obj.AppendLine();
                }
            }

            _positions += vertices.Length;
            _uvs += hasUvs ? vertices.Length : 0;
            _normals += hasNormals ? vertices.Length : 0;
        }

        public override string ToString() => _obj.ToString();

        // G9 writes every float exactly.
        static string Format(float value) => value.ToString("G9", CultureInfo.InvariantCulture);

        readonly StringBuilder _obj = new StringBuilder();
        int _positions;
        int _uvs;
        int _normals;
    }
}
