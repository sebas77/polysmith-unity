using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// Sends a prefab's meshes to Polysmith and takes back the reduced ones as new mesh assets. Generic: any prefab.
    /// <list type="number">
    /// <item><see cref="WriteRequest"/> writes every distinct MeshFilter mesh into one OBJ, one object per mesh and
    /// one material group per submesh, placed as the prefab shows it.</item>
    /// <item>Polysmith exports each object it reduced as "&lt;object&gt;_lod.obj" next to that OBJ.</item>
    /// <item><see cref="ApplyResults"/> saves each as "&lt;prefab folder&gt;/Reduced/&lt;prefab&gt;/&lt;object&gt;.asset",
    /// labelled <see cref="ReducedLabel"/>, and points every MeshFilter of the prefab that used the mesh at it. Meshes
    /// without a result stay as they are; original mesh assets are never changed.</item>
    /// </list>
    /// Precondition: the prefab's meshes and transforms do not change between the two calls, since objects are
    /// matched by name and moved back into mesh space by the same placement.
    /// </summary>
    public static class PolysmithRoundTrip
    {
        /// <summary>
        /// Asset label of every mesh this tool wrote. A tool that regenerates meshes can check it to keep a reduction.
        /// </summary>
        public const string ReducedLabel = "PolysmithReduced";

        /// <summary>
        /// Writes the request OBJ into <paramref name="folder"/> and returns its path. Throws when the prefab has no
        /// mesh to send (Polysmith cannot open an empty file), e.g. when its meshes are built at run time.
        /// </summary>
        public static string WriteRequest(GameObject prefab, string folder, List<string> warnings)
        {
            var objects = Objects(prefab, warnings);
            if (objects.Count == 0)
                throw new InvalidOperationException($"'{prefab.name}' has no triangle mesh assets on its MeshFilters, " +
                                                    "so there is nothing to send to Polysmith.");
            var writer = new ObjWriter();
            foreach (var (name, mesh, placement) in objects)
                writer.Append(name, mesh, placement);
            string path = Path.Combine(folder, Sanitize(prefab.name) + ".obj");
            File.WriteAllText(path, writer.ToString());
            return path;
        }

        /// <summary>Applies the "_lod.obj" files in <paramref name="folder"/> and returns the reduced mesh assets.</summary>
        public static List<Mesh> ApplyResults(GameObject prefab, string folder, List<string> warnings)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            string prefabFolder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var replacements = new Dictionary<Mesh, Mesh>();
            // WriteRequest already reported the meshes it left out.
            foreach (var (name, mesh, placement) in Objects(prefab, new List<string>()))
            {
                string result = Path.Combine(folder, name + "_lod.obj");
                if (File.Exists(result) == false)
                    continue;
                var built = ObjMeshFile.Read(result, mesh.subMeshCount, placement, warnings);
                if (built == null)
                    continue;
                built.name = name;
                replacements[mesh] = Save(built, prefabFolder, Sanitize(prefab.name));
            }

            if (replacements.Count > 0)
                Repoint(prefabPath, replacements);
            return new List<Mesh>(replacements.Values);
        }

        // Distinct meshes in hierarchy order, each named after its mesh and made unique, and placed where its first
        // MeshFilter shows it relative to the prefab root. Only triangle meshes go: Polysmith reduces triangles.
        static List<(string Name, Mesh Mesh, Matrix4x4 Placement)> Objects(GameObject prefab, List<string> warnings)
        {
            var objects = new List<(string, Mesh, Matrix4x4)>();
            var rootFromWorld = prefab.transform.worldToLocalMatrix;
            var seen = new HashSet<Mesh>();
            // Names become file names, which differ only by more than case on Windows and macOS.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || seen.Add(mesh) == false)
                    continue;
                if (TrianglesOnly(mesh) == false)
                {
                    warnings.Add($"Mesh '{mesh.name}' on '{filter.name}' has a submesh that is not triangles; it was not sent.");
                    continue;
                }

                string name = Sanitize(mesh.name);
                if (name.Length == 0)
                    name = "mesh";
                string unique = name;
                for (int suffix = 2; names.Add(unique) == false; suffix++)
                    unique = $"{name}_{suffix}";
                objects.Add((unique, mesh, rootFromWorld * filter.transform.localToWorldMatrix));
            }
            return objects;
        }

        static bool TrianglesOnly(Mesh mesh)
        {
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    return false;
            return true;
        }

        // An existing asset is overwritten in place, so its GUID and every reference to it survive. The file is named
        // after the object because Unity names a mesh asset after its file: a later run then finds the same object.
        static Mesh Save(Mesh built, string prefabFolder, string prefabName)
        {
            string folder = EnsureFolder(EnsureFolder(prefabFolder, "Reduced"), prefabName);
            string path = $"{folder}/{built.name}.asset";

            var asset = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (asset == null)
            {
                AssetDatabase.CreateAsset(built, path);
                asset = built;
            }
            else
            {
                EditorUtility.CopySerialized(built, asset);
                asset.name = built.name;
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
            }

            var labels = new List<string>(AssetDatabase.GetLabels(asset));
            if (labels.Contains(ReducedLabel) == false)
            {
                labels.Add(ReducedLabel);
                AssetDatabase.SetLabels(asset, labels.ToArray());
            }
            return asset;
        }

        static string EnsureFolder(string parent, string name)
        {
            string folder = $"{parent}/{name}";
            if (AssetDatabase.IsValidFolder(folder) == false)
                AssetDatabase.CreateFolder(parent, name);
            return folder;
        }

        static void Repoint(string prefabPath, Dictionary<Mesh, Mesh> replacements)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (var filter in contents.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && replacements.TryGetValue(filter.sharedMesh, out var reduced))
                        filter.sharedMesh = reduced;
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Object and file names: letters, digits, '_' and '-' only.
        static string Sanitize(string name)
        {
            var sanitized = new StringBuilder(name.Length);
            foreach (char c in name)
                sanitized.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sanitized.ToString();
        }
    }
}
