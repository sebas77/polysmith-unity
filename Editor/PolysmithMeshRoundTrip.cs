#if POLYSMITH_UNITY_GLTF
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Polysmith
{
    /// <summary>
    /// One mesh asset's round trip ("Optimise in Polysmith" on a single mesh in the Project window). The mesh is sent
    /// alone, at the origin, with the materials of the first object in the open scenes that draws it. Each result is
    /// saved as "&lt;mesh folder&gt;/Reduced/&lt;mesh&gt;/&lt;mesh&gt;.asset" (the original is never changed), and
    /// every MeshFilter in the open scenes that draws the mesh, or an earlier result of it, is pointed at it (undoable;
    /// the scenes are left to save). Prefab assets are not touched.
    /// </summary>
    public static class PolysmithMeshRoundTrip
    {
        /// <summary>What the watcher keeps of a request to apply its results.</summary>
        [Serializable]
        public sealed class Request
        {
            public string Glb = "";
            public string Name = "";
            // Asset references (guid:fileId) of the original mesh and the materials sent with it.
            public string Mesh = "";
            public string[] Materials = Array.Empty<string>();
            // The folder the result is saved in, created when the first result arrives.
            public string Output = "";
        }

        /// <summary>The round-trip key of a mesh: one Polysmith at a time per original mesh.</summary>
        public static string Key(Mesh selected)
        {
            var (mesh, _) = PolysmithRoundTrip.OriginOf(selected, Array.Empty<Material>(), new List<string>());
            return "mesh:" + PolysmithRoundTrip.Reference(mesh);
        }

        public static Request WriteRequest(Mesh selected, string folder, List<string> warnings)
        {
            // A result selected again sends its original, as a prefab showing results does.
            var (mesh, _) = PolysmithRoundTrip.OriginOf(selected, Array.Empty<Material>(), warnings);
            if (AssetDatabase.Contains(mesh) == false)
                throw new InvalidOperationException($"'{mesh.name}' is not a mesh asset.");
            if (mesh.bindposes.Length > 0)
                throw new NotSupportedException($"'{mesh.name}' is skinned: optimise the prefab that uses it instead.");
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    throw new NotSupportedException("Polysmith requires triangle meshes: " + mesh.name);

            var users = Users(mesh, warnings);
            var materials = Array.Empty<Material>();
            if (users.Count > 0)
                materials = PolysmithRoundTrip.OriginOf(users[0].sharedMesh,
                    users[0].GetComponent<MeshRenderer>().sharedMaterials, warnings).Materials;
            else
                warnings.Add($"'{mesh.name}' is not drawn in the open scenes: its result is saved but no object uses it.");

            string name = PolysmithRoundTrip.Sanitize(mesh.name);
            var root = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            var temporaryMaterials = new List<Material>();
            try
            {
                root.AddComponent<MeshFilter>();
                var renderer = root.AddComponent<MeshRenderer>();
                // UnityGLTF needs at least one material slot; this one is not remembered as an original.
                var sent = materials;
                if (sent.Length == 0)
                {
                    var placeholder = new Material(DefaultShader()) { name = "default" };
                    temporaryMaterials.Add(placeholder);
                    sent = new[] { placeholder };
                }
                var parts = new List<PolysmithRoundTrip.Part>
                    { new PolysmithRoundTrip.Part { Renderer = renderer, Mesh = mesh, Materials = sent, Path = "", Name = name } };
                PolysmithRoundTrip.ShowOriginals(parts, temporaryMaterials, warnings);
                var request = new Request
                {
                    Glb = PolysmithRoundTrip.Export(root, folder, name),
                    Name = name,
                    Mesh = PolysmithRoundTrip.Reference(mesh),
                    Materials = new string[materials.Length],
                    Output = OutputFolder(mesh, name)
                };
                for (int i = 0; i < materials.Length; i++)
                    request.Materials[i] = PolysmithRoundTrip.Reference(materials[i]);
                return request;
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var material in temporaryMaterials)
                    Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// Saves "&lt;Name&gt;_lod.glb" in folder as the reduced mesh and points the open scenes' users of the original at
        /// it. Null when there is no result yet.
        /// </summary>
        public static Mesh ApplyResults(Request request, string folder, List<string> warnings)
        {
            string result = Path.Combine(folder, request.Name + "_lod.glb");
            if (File.Exists(result) == false)
                return null;
            var original = PolysmithRoundTrip.Load<Mesh>(request.Mesh);
            if (original == null)
                throw new InvalidOperationException("The original mesh of '" + request.Name + "' is gone.");
            var materials = new Material[request.Materials.Length];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = PolysmithRoundTrip.Load<Material>(request.Materials[i]);

            using (var imported = new GlbResult(result))
            {
                var renderer = imported.PrefabRoot.GetComponent<Renderer>();
                var shown = PolysmithRoundTrip.MeshOf(renderer);
                if (renderer != null && shown == null)
                    throw new InvalidDataException("The result has no mesh: " + request.Name);
                // A fully hidden mesh comes back without geometry: its result is empty, not the original.
                var copy = shown != null ? Object.Instantiate(shown) : new Mesh { subMeshCount = 0 };
                try
                {
                    string output = EnsureFolders(request.Output);
                    copy.name = Path.GetFileName(output);
                    var originals = new Dictionary<string, Material>(StringComparer.Ordinal);
                    var part = new PolysmithRoundTrip.Part { Mesh = original, Materials = materials };
                    foreach (var entry in PolysmithRoundTrip.MaterialNames(new List<PolysmithRoundTrip.Part> { part }))
                        originals.Add(entry.Value, entry.Key);
                    var saved = imported.SaveMaterials(output, originals, warnings);
                    var mesh = PolysmithRoundTrip.SaveResult(copy, output + "/" + copy.name + ".asset", original, materials);
                    var slots = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
                    for (int i = 0; i < slots.Length; i++)
                        slots[i] = slots[i] != null ? saved[slots[i]] : null;
                    Repoint(original, mesh, slots, materials, warnings);
                    return mesh;
                }
                finally
                {
                    if (copy != null && AssetDatabase.Contains(copy) == false)
                        Object.DestroyImmediate(copy);
                }
            }
        }

        // Every user of the original in the open scenes draws the result. An atlas result brings its own materials;
        // a result on the original UVs keeps each user's own original materials when their slots still line up.
        static void Repoint(Mesh original, Mesh result, Material[] slots, Material[] sent, List<string> warnings)
        {
            bool atlas = false;
            foreach (var slot in slots)
                if (slot != null && Array.IndexOf(sent, slot) < 0)
                    atlas = true;
            foreach (var filter in Users(original, warnings))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                var own = PolysmithRoundTrip.OriginOf(filter.sharedMesh, renderer.sharedMaterials, warnings).Materials;
                Undo.RecordObjects(new Object[] { filter, renderer }, "Polysmith result");
                filter.sharedMesh = result;
                renderer.sharedMaterials = atlas || own.Length != slots.Length ? slots : own;
                if (PrefabUtility.IsPartOfPrefabInstance(filter))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }
                EditorSceneManager.MarkSceneDirty(filter.gameObject.scene);
            }
        }

        // The MeshFilters (with a MeshRenderer) in the open scenes drawing the mesh or one of its results.
        internal static List<MeshFilter> Users(Mesh original, List<string> warnings)
        {
            var users = new List<MeshFilter>();
            var origins = new Dictionary<Mesh, Mesh>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (scene.isLoaded == false)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var shown = filter.sharedMesh;
                        if (shown == null || filter.TryGetComponent<MeshRenderer>(out _) == false)
                            continue;
                        if (origins.TryGetValue(shown, out var origin) == false)
                            origins.Add(shown, origin = PolysmithRoundTrip.OriginOf(shown, Array.Empty<Material>(), warnings).Mesh);
                        if (origin == original)
                            users.Add(filter);
                    }
            }
            return users;
        }

        // "<mesh folder>/Reduced/<name>" (under Assets/ when the mesh is in a package or built in). A folder that already
        // holds another mesh's result gets a numbered name. A new folder is created now, so another round trip open at
        // the same time does not pick it too; the watcher deletes it if it is still empty when Polysmith closes.
        static string OutputFolder(Mesh mesh, string name)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            string parent = path.StartsWith("Assets/", StringComparison.Ordinal)
                ? Path.GetDirectoryName(path).Replace('\\', '/') : "Assets";
            var scratch = new List<string>();
            for (int suffix = 1;; suffix++)
            {
                string leaf = suffix == 1 ? name : name + "_" + suffix;
                string folder = parent + "/Reduced/" + leaf;
                if (AssetDatabase.IsValidFolder(folder) == false)
                    return EnsureFolders(folder);
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/" + leaf + ".asset");
                if (existing != null && PolysmithRoundTrip.OriginOf(existing, Array.Empty<Material>(), scratch).Mesh == mesh)
                    return folder;
            }
        }

        /// <summary>Deletes the request's output folder when no result was saved in it.</summary>
        public static void DeleteUnusedOutput(Request request)
        {
            if (AssetDatabase.IsValidFolder(request.Output) && AssetDatabase.FindAssets("", new[] { request.Output }).Length == 0)
                AssetDatabase.DeleteAsset(request.Output);
        }

        static string EnsureFolders(string folder)
        {
            string[] names = folder.Split('/');
            string path = names[0];
            for (int i = 1; i < names.Length; i++)
                path = PolysmithRoundTrip.EnsureFolder(path, names[i]);
            return path;
        }

        static Shader DefaultShader()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null)
                return pipeline.defaultMaterial.shader;
            return Shader.Find("Standard");
        }
    }
}
#endif
