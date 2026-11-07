#if POLYSMITH_UNITY_GLTF
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityGLTF;
using Object = UnityEngine.Object;

namespace Polysmith
{
    /// <summary>
    /// GLB-only prefab round trip. Export original meshes/materials and sampled animation from a temporary copy.
    /// Return only mesh/material assets: never replace the prefab's rig, Animator, Avatar, controller or clips.
    /// </summary>
    public static class PolysmithRoundTrip
    {
        public const string ReducedLabel = "PolysmithReduced";

        public static string WriteRequest(GameObject prefab, string folder, List<string> warnings)
        {
            var instance = Object.Instantiate(prefab);
            var temporaryMaterials = new List<Material>();
            GLTFSettings settings = null;
            try
            {
                instance.name = Sanitize(prefab.name);
                instance.hideFlags = HideFlags.HideAndDontSave;
                var parts = Parts(instance, warnings);
                if (parts.Count == 0)
                    throw new InvalidOperationException("The prefab has no triangle meshes to send to Polysmith.");
                var materials = new Dictionary<Material, Material>();
                var names = MaterialNames(parts);
                foreach (var part in parts)
                {
                    SetMesh(part.Renderer, part.Mesh);
                    var slots = (Material[])part.Materials.Clone();
                    for (int i = 0; i < slots.Length; i++)
                    {
                        var source = slots[i];
                        if (source == null)
                            continue;
                        if (materials.TryGetValue(source, out var copy) == false)
                        {
                            copy = new Material(source) { name = names[source] };
                            temporaryMaterials.Add(copy);
                            materials.Add(source, copy);
                        }
                        slots[i] = copy;
                    }
                    part.Renderer.sharedMaterials = slots;
                    if (part.Mesh.blendShapeCount > 0)
                        warnings.Add($"'{part.Mesh.name}': Polysmith does not currently return blend shapes.");
                }
                settings = GLTFSettings.GetDefaultSettings();
                settings.ExportAnimations = true;
                settings.BakeSkinnedMeshes = false;
                settings.ExportDisabledGameObjects = true;
                settings.UseMainCameraVisibility = false;
                settings.ExportNames = true;
                settings.ExportFullPath = false;
                settings.BakeAnimationSpeed = false;
                settings.UseCaching = false;
                // Only core glTF animation/geometry is needed, not project-specific extension plugins.
                foreach (var plugin in settings.ExportPlugins)
                    plugin.Enabled = false;
                var exporter = new GLTFSceneExporter(instance.transform, new ExportContext(settings));
                exporter.SaveGLB(folder, Sanitize(prefab.name));
                return Path.Combine(folder, Sanitize(prefab.name) + ".glb");
            }
            finally
            {
                if (settings != null)
                    Object.DestroyImmediate(settings);
                Object.DestroyImmediate(instance);
                foreach (var material in temporaryMaterials)
                    Object.DestroyImmediate(material);
            }
        }

        public static List<Mesh> ApplyResults(GameObject prefab, string folder, List<string> warnings)
        {
            string result = Path.Combine(folder, Sanitize(prefab.name) + "_lod.glb");
            var reduced = new List<Mesh>();
            if (File.Exists(result) == false)
                return reduced;
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var parts = Parts(contents, warnings);
                using (var imported = new GlbResult(result))
                {
                    var incoming = imported.PrefabRoot;
                    var replacements = new List<(Part Part, Mesh Mesh, Renderer Imported)>();
                    try
                    {
                        // Validate all renderer/bone matches before writing assets or repointing the prefab.
                        foreach (var part in parts)
                        {
                            var node = part.Path.Length == 0 ? incoming : incoming.Find(part.Path);
                            if (node == null)
                                throw new InvalidDataException("The result has no node at prefab path: " + part.Path);
                            var renderer = node.GetComponent<Renderer>();
                            var mesh = MeshOf(renderer);
                            if (renderer != null && mesh == null)
                                throw new InvalidDataException("The result has no mesh at: " + part.Path);
                            // A fully hidden/reduced object keeps its hierarchy node but has no GLB mesh.
                            // Replace it with an empty mesh rather than resurrecting its original geometry.
                            var copy = mesh != null ? Object.Instantiate(mesh) : new Mesh { subMeshCount = 0 };
                            try
                            {
                                if (part.Renderer is SkinnedMeshRenderer target && copy.vertexCount > 0)
                                    BindToExistingRig(copy, renderer as SkinnedMeshRenderer, incoming, target, contents.transform,
                                        part.Mesh);
                                copy.name = part.Name;
                                replacements.Add((part, copy, renderer));
                            }
                            catch
                            {
                                Object.DestroyImmediate(copy);
                                throw;
                            }
                        }
                        string output = EnsureFolder(EnsureFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/'),
                            "Reduced"), Sanitize(prefab.name));
                        var originals = new Dictionary<string, Material>(StringComparer.Ordinal);
                        foreach (var entry in MaterialNames(parts))
                            originals.Add(entry.Value, entry.Key);
                        var materials = imported.SaveMaterials(output, originals);
                        foreach (var item in replacements)
                        {
                            item.Mesh.Optimize();
                            item.Mesh.RecalculateUVDistributionMetrics();
                            var mesh = Save(item.Mesh, output + "/" + item.Mesh.name + ".asset");
                            RememberOrigin(mesh, item.Part);
                            var slots = item.Imported != null ? item.Imported.sharedMaterials : Array.Empty<Material>();
                            for (int i = 0; i < slots.Length; i++)
                                slots[i] = slots[i] != null ? materials[slots[i]] : null;
                            SetMesh(item.Part.Renderer, mesh);
                            item.Part.Renderer.sharedMaterials = slots;
                            reduced.Add(mesh);
                        }
                        if (PrefabUtility.SaveAsPrefabAsset(contents, prefabPath) == null)
                            throw new IOException("Could not save the prefab: " + prefabPath);
                    }
                    finally
                    {
                        foreach (var item in replacements)
                            if (item.Mesh != null && AssetDatabase.Contains(item.Mesh) == false)
                                Object.DestroyImmediate(item.Mesh);
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            return reduced;
        }

        static List<Part> Parts(GameObject root, List<string> warnings)
        {
            var parts = new List<Part>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                string path = AnimationUtility.CalculateTransformPath(node, root.transform);
                if (node.name.Contains("/") || paths.Add(path) == false)
                    throw new InvalidDataException("Ambiguous prefab transform path: " + path);
                var renderer = node.GetComponent<Renderer>();
                var shown = MeshOf(renderer);
                if (shown == null)
                    continue;
                var (mesh, materials) = OriginOf(shown, renderer.sharedMaterials, warnings);
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        throw new NotSupportedException("Polysmith requires triangle meshes: " + path);
                parts.Add(new Part { Renderer = renderer, Mesh = mesh, Materials = materials, Path = path,
                    Name = Unique(Sanitize(mesh.name), names) });
            }
            return parts;
        }

        static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skin)
                return skin.sharedMesh;
            return renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
        }

        static Dictionary<Material, string> MaterialNames(List<Part> parts)
        {
            var result = new Dictionary<Material, string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
                foreach (var material in part.Materials)
                    if (material != null && result.ContainsKey(material) == false)
                        result.Add(material, Unique(Sanitize(material.name), used));
            return result;
        }

        static void SetMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skin)
                skin.sharedMesh = mesh;
            else
                renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        static void BindToExistingRig(Mesh mesh, SkinnedMeshRenderer source, Transform sourceRoot,
            SkinnedMeshRenderer target, Transform targetRoot, Mesh original)
        {
            if (target.bones.Length == 0)
                return;
            if (source == null || mesh.boneWeights.Length != mesh.vertexCount)
                throw new InvalidDataException("The result lost its skin at " + target.name);
            var targetBones = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int b = 0; b < target.bones.Length; b++)
                if (target.bones[b] != null)
                    targetBones.Add(AnimationUtility.CalculateTransformPath(target.bones[b], targetRoot), b);
            var map = new int[source.bones.Length];
            for (int b = 0; b < map.Length; b++)
                map[b] = source.bones[b] != null && targetBones.TryGetValue(
                    AnimationUtility.CalculateTransformPath(source.bones[b], sourceRoot), out int index) ? index : -1;
            int Bone(int index, float weight)
            {
                if (weight == 0)
                    return 0;
                if (index < 0 || index >= map.Length || map[index] < 0)
                    throw new InvalidDataException("The result references a bone outside the original renderer: " + target.name);
                return map[index];
            }
            var weights = mesh.boneWeights;
            for (int v = 0; v < weights.Length; v++)
            {
                var w = weights[v];
                w.boneIndex0 = Bone(w.boneIndex0, w.weight0);
                w.boneIndex1 = Bone(w.boneIndex1, w.weight1);
                w.boneIndex2 = Bone(w.boneIndex2, w.weight2);
                w.boneIndex3 = Bone(w.boneIndex3, w.weight3);
                weights[v] = w;
            }
            mesh.boneWeights = weights;
            mesh.bindposes = original.bindposes;
        }

        static Mesh Save(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                existing = mesh;
            }
            else
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);
            }
            AssetDatabase.SetLabels(existing, new[] { ReducedLabel });
            return existing;
        }

        static string EnsureFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (AssetDatabase.IsValidFolder(path) == false)
                AssetDatabase.CreateFolder(parent, name);
            return path;
        }

        static void RememberOrigin(Mesh mesh, Part part)
        {
            var origin = new Origin { Mesh = Reference(part.Mesh), Materials = new string[part.Materials.Length] };
            for (int i = 0; i < origin.Materials.Length; i++)
                origin.Materials[i] = Reference(part.Materials[i]);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh));
            importer.userData = JsonUtility.ToJson(origin);
            importer.SaveAndReimport();
        }

        static (Mesh Mesh, Material[] Materials) OriginOf(Mesh shown, Material[] materials, List<string> warnings)
        {
            if (Array.IndexOf(AssetDatabase.GetLabels(shown), ReducedLabel) < 0)
                return (shown, materials);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(shown));
            if (importer == null || string.IsNullOrEmpty(importer.userData))
                return (shown, materials);
            var origin = JsonUtility.FromJson<Origin>(importer.userData);
            var mesh = Load<Mesh>(origin.Mesh);
            if (mesh == null)
            {
                warnings.Add("The original mesh is missing: " + shown.name);
                return (shown, materials);
            }
            var originals = new Material[origin.Materials.Length];
            for (int i = 0; i < originals.Length; i++)
                originals[i] = Load<Material>(origin.Materials[i]);
            return (mesh, originals);
        }

        static string Reference(Object asset) => asset != null &&
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id)
                ? guid + ":" + id.ToString(CultureInfo.InvariantCulture) : "";

        static T Load<T>(string reference) where T : Object
        {
            int colon = reference.IndexOf(':');
            if (colon < 0)
                return null;
            long id = long.Parse(reference.Substring(colon + 1), CultureInfo.InvariantCulture);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(reference.Substring(0, colon))))
                if (asset is T typed && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long local) && id == local)
                    return typed;
            return null;
        }

        internal static string Sanitize(string name)
        {
            var text = new StringBuilder();
            foreach (char c in name)
                text.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return text.Length == 0 ? "mesh" : text.ToString();
        }

        internal static string Unique(string name, HashSet<string> names)
        {
            string candidate = name;
            for (int suffix = 2; names.Add(candidate) == false; suffix++)
                candidate = name + "_" + suffix;
            return candidate;
        }

        sealed class Part
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public Material[] Materials;
            public string Path;
            public string Name;
        }

        [Serializable]
        sealed class Origin
        {
            public string Mesh = "";
            public string[] Materials = Array.Empty<string>();
        }
    }
}
#endif
