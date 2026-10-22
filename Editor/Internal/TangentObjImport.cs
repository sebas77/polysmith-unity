using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Polysmith
{
    /// <summary>
    /// Reads a result with a normal map through Unity's own OBJ importer, because the normal map is only right with
    /// MikkTSpace tangents built from exactly the file's normals and UVs, and the importer is Unity's MikkTSpace
    /// implementation. The file is imported from a temporary folder with fixed settings and copied out, then the folder
    /// is deleted; the mesh is moved back into mesh space as <see cref="ObjMeshFile.Read"/> does.
    /// </summary>
    static class TangentObjImport
    {
        /// <summary>Same contract as <see cref="ObjMeshFile.Read"/>, plus tangents.</summary>
        public static Mesh Read(string path, IReadOnlyList<string> groups, Matrix4x4 placement, List<string> warnings)
        {
            if (AssetDatabase.IsValidFolder(TempFolder) == false)
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            string objAsset = $"{TempFolder}/result.obj";
            try
            {
                WriteCopy(path, objAsset, groups);
                AssetDatabase.ImportAsset($"{TempFolder}/result.mtl", ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(objAsset, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(objAsset);
                importer.importNormals = ModelImporterNormals.Import;
                importer.importTangents = ModelImporterTangents.CalculateMikk;
                importer.importBlendShapes = false;
                importer.importCameras = false;
                importer.importLights = false;
                importer.importAnimation = false;
                importer.animationType = ModelImporterAnimationType.None;
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.indexFormat = ModelImporterIndexFormat.Auto;
                importer.isReadable = true;
                // Its materials name the submeshes; they stay in the temporary folder.
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.SaveAndReimport();

                var model = AssetDatabase.LoadAssetAtPath<GameObject>(objAsset);
                var imported = model.GetComponentInChildren<MeshFilter>().sharedMesh;
                var materials = model.GetComponentInChildren<MeshRenderer>().sharedMaterials;
                return ToMeshSpace(imported, materials, groups, placement, path, warnings);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        // The importer splits submeshes by the groups the MTL defines, so it gets one material per expected group.
        static void WriteCopy(string path, string objAsset, IReadOnlyList<string> groups)
        {
            var mtl = new StringBuilder();
            foreach (string group in groups)
                mtl.Append("newmtl ").AppendLine(group);
            File.WriteAllText(Path.ChangeExtension(objAsset, ".mtl"), mtl.ToString());

            var obj = new StringBuilder("mtllib result.mtl\n");
            foreach (string line in File.ReadLines(path))
                if (line.StartsWith("mtllib ", StringComparison.Ordinal) == false)
                    obj.AppendLine(line);
            File.WriteAllText(objAsset, obj.ToString());
        }

        static Mesh ToMeshSpace(Mesh imported, Material[] materials, IReadOnlyList<string> groups, Matrix4x4 placement,
            string path, List<string> warnings)
        {
            var toMesh = placement.inverse;
            var normalsToMesh = placement.transpose;
            bool mirrored = placement.determinant < 0;

            var vertices = imported.vertices;
            var normals = imported.normals;
            var tangents = imported.tangents;
            for (int k = 0; k < vertices.Length; k++)
            {
                vertices[k] = toMesh.MultiplyPoint3x4(vertices[k]);
                normals[k] = normalsToMesh.MultiplyVector(normals[k]).normalized;
                var tangent = toMesh.MultiplyVector(new Vector3(tangents[k].x, tangents[k].y, tangents[k].z)).normalized;
                // A mirrored placement flips the frame's handedness.
                tangents[k] = new Vector4(tangent.x, tangent.y, tangent.z, mirrored ? -tangents[k].w : tangents[k].w);
            }

            var triangles = new List<int>[groups.Count];
            for (int group = 0; group < groups.Count; group++)
                triangles[group] = new List<int>();
            for (int submesh = 0; submesh < imported.subMeshCount; submesh++)
            {
                int group = IndexOf(groups, materials[submesh].name);
                if (group < 0)
                {
                    warnings.Add($"'{path}' has material group '{materials[submesh].name}', which is not one of the mesh's " +
                                 "submeshes; the mesh was not replaced.");
                    return null;
                }
                int[] submeshTriangles = imported.GetTriangles(submesh);
                for (int t = 0; t < submeshTriangles.Length; t += 3)
                    triangles[group].AddRange(mirrored
                        ? new[] { submeshTriangles[t], submeshTriangles[t + 2], submeshTriangles[t + 1] }
                        : new[] { submeshTriangles[t], submeshTriangles[t + 1], submeshTriangles[t + 2] });
            }

            var mesh = new Mesh { indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, imported.uv);
            mesh.subMeshCount = groups.Count;
            for (int group = 0; group < groups.Count; group++)
                mesh.SetTriangles(triangles[group], group);
            mesh.RecalculateBounds();
            return mesh;
        }

        static int IndexOf(IReadOnlyList<string> groups, string group)
        {
            for (int index = 0; index < groups.Count; index++)
                if (groups[index] == group)
                    return index;
            return -1;
        }

        const string TempFolder = "Assets/__PolysmithImport";
    }
}
