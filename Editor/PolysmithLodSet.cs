using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Polysmith
{
    /// <summary>
    /// Imports Polysmith's independent FBXs into a conventional LODGroup.
    /// LOD0 retains the source hierarchy/animation. Other levels place only
    /// their renderers below the corresponding LOD0 nodes and reuse its bones.
    /// No runtime script, shared vertex buffer, or source-prefab mutation.
    /// </summary>
    public static class PolysmithLodSet
    {
        public static GameObject Build(string manifestPath, string prefabPath,
            GameObject sourceMaterials = null, bool overwrite = false)
        {
            ValidateAssetPath(manifestPath, ".json");
            ValidateAssetPath(prefabPath, ".prefab");
            if (AssetDatabase.LoadMainAssetAtPath(prefabPath) != null && overwrite == false)
                throw new IOException("The output prefab already exists; explicitly allow overwrite or choose another path.");
            if (AssetDatabase.IsValidFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/')) == false)
                throw new DirectoryNotFoundException("The output prefab folder must already exist.");
            if (sourceMaterials != null && AssetDatabase.GetAssetPath(sourceMaterials) == prefabPath)
                throw new ArgumentException("The output must not overwrite the source-material prefab.");

            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            ValidateManifest(manifest);
            var materials = manifest.reuseSourceMaterials ? MaterialMap(sourceMaterials) : null;
            string folder = Path.GetDirectoryName(manifestPath).Replace('\\', '/');
            var models = new GameObject[manifest.levels.Length];
            for (int i = 0; i < models.Length; i++)
            {
                string path = folder + "/" + manifest.levels[i].file;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                models[i] = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (models[i] == null)
                    throw new IOException("Cannot import LOD model: " + path);
                foreach (var renderer in models[i].GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0)
                        throw new NotSupportedException("LOD-set rig sharing currently supports skeletal animation, not animated blend shapes.");
            }

            var root = new GameObject(Path.GetFileNameWithoutExtension(prefabPath));
            try
            {
                var first = Object.Instantiate(models[0], root.transform, false);
                first.name = "LOD0";
                var nodes = NodeMap(first.transform);
                var lods = new LOD[models.Length];
                var baseRenderers = first.GetComponentsInChildren<Renderer>(true);
                ValidateRenderers(baseRenderers, manifest.levels[0].triangles);
                foreach (var renderer in baseRenderers)
                    AssignMaterials(renderer, materials);
                lods[0] = new LOD(manifest.levels[0].screenHeight, baseRenderers);

                for (int level = 1; level < models.Length; level++)
                {
                    var imported = models[level].GetComponentsInChildren<Renderer>(true);
                    ValidateRenderers(imported, manifest.levels[level].triangles);
                    var renderers = new Renderer[imported.Length];
                    for (int r = 0; r < imported.Length; r++)
                    {
                        var source = imported[r];
                        string path = AnimationUtility.CalculateTransformPath(source.transform, models[level].transform);
                        if (nodes.TryGetValue(path, out var parent) == false)
                            throw new InvalidDataException("LOD hierarchy differs from LOD0 at " + path);
                        var go = new GameObject("LOD" + level);
                        go.transform.SetParent(parent, false);
                        go.layer = parent.gameObject.layer;
                        Renderer target;
                        if (source is SkinnedMeshRenderer skinned)
                        {
                            var copy = go.AddComponent<SkinnedMeshRenderer>();
                            EditorUtility.CopySerialized(skinned, copy);
                            var bones = skinned.bones;
                            for (int b = 0; b < bones.Length; b++)
                                bones[b] = MapBone(bones[b], models[level].transform, nodes);
                            copy.bones = bones;
                            copy.rootBone = skinned.rootBone == null ? null : MapBone(skinned.rootBone, models[level].transform, nodes);
                            target = copy;
                        }
                        else
                        {
                            go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                            target = go.AddComponent<MeshRenderer>();
                            EditorUtility.CopySerialized(source, target);
                        }
                        AssignMaterials(target, materials);
                        renderers[r] = target;
                    }
                    lods[level] = new LOD(manifest.levels[level].screenHeight, renderers);
                }

                var group = root.AddComponent<LODGroup>();
                group.SetLODs(lods);
                group.RecalculateBounds();
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool saved);
                if (saved == false || prefab == null)
                    throw new IOException("Could not save LOD prefab: " + prefabPath);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void ValidateAssetPath(string path, string extension)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("Assets/", StringComparison.Ordinal) == false ||
                path.Contains("\\") || Array.IndexOf(path.Split('/'), "..") >= 0 ||
                string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) == false)
                throw new ArgumentException("Expected an Assets-relative " + extension + " path.");
        }

        static void ValidateManifest(Manifest manifest)
        {
            if (manifest == null || manifest.version != 1 || manifest.format != "polysmith-lod-set" ||
                manifest.levels == null || manifest.levels.Length < 2 || manifest.levels.Length > 4)
                throw new InvalidDataException("Expected a Polysmith independent LOD-set manifest with two to four levels.");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            float previousHeight = 1;
            double previousFit = 0;
            foreach (var level in manifest.levels)
            {
                if (level == null || string.IsNullOrEmpty(level.file) || Path.GetFileName(level.file) != level.file ||
                    level.file.Contains("/") || level.file.Contains("\\") ||
                    string.Equals(Path.GetExtension(level.file), ".fbx", StringComparison.OrdinalIgnoreCase) == false ||
                    files.Add(level.file) == false || level.screenHeight <= 0 || level.screenHeight >= previousHeight ||
                    float.IsNaN(level.screenHeight) || double.IsNaN(level.fitPercent) || double.IsInfinity(level.fitPercent) ||
                    level.fitPercent <= previousFit || level.fitPercent > 100 || level.vertices <= 0 || level.triangles <= 0)
                    throw new InvalidDataException("Invalid LOD filename, order, screen height or mesh counts.");
                previousHeight = level.screenHeight;
                previousFit = level.fitPercent;
            }
        }

        static Dictionary<string, Material> MaterialMap(GameObject source)
        {
            if (source == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(source)))
                throw new ArgumentException("Original-UV LODs need the source Unity prefab/model to retain its materials.");
            var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                        continue;
                    if (materials.TryGetValue(material.name, out var previous) && previous != material)
                        throw new InvalidDataException("Source materials have ambiguous duplicate names: " + material.name);
                    materials[material.name] = material;
                }
            return materials;
        }

        static void AssignMaterials(Renderer renderer, Dictionary<string, Material> materials)
        {
            if (materials == null)
                return;
            var assigned = renderer.sharedMaterials;
            for (int i = 0; i < assigned.Length; i++)
            {
                if (assigned[i] == null || materials.TryGetValue(assigned[i].name, out var original) == false)
                    throw new InvalidDataException("No matching source material for " + renderer.name + " slot " + i);
                assigned[i] = original;
            }
            renderer.sharedMaterials = assigned;
        }

        static Dictionary<string, Transform> NodeMap(Transform root)
        {
            var nodes = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                string path = AnimationUtility.CalculateTransformPath(node, root);
                if (nodes.ContainsKey(path))
                    throw new InvalidDataException("Ambiguous duplicate hierarchy path: " + path);
                nodes.Add(path, node);
            }
            return nodes;
        }

        static Transform MapBone(Transform bone, Transform sourceRoot, Dictionary<string, Transform> nodes)
        {
            if (bone == null || nodes.TryGetValue(AnimationUtility.CalculateTransformPath(bone, sourceRoot), out var mapped) == false)
                throw new InvalidDataException("A lower LOD's bone cannot be bound to the LOD0 rig.");
            return mapped;
        }

        static void ValidateRenderers(Renderer[] renderers, int expectedTriangles)
        {
            long triangles = 0;
            foreach (var renderer in renderers)
            {
                Mesh mesh;
                if (renderer is SkinnedMeshRenderer skinned)
                    mesh = skinned.sharedMesh;
                else if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter))
                    mesh = filter.sharedMesh;
                else
                    throw new NotSupportedException("LOD models must contain mesh renderers only.");
                if (mesh == null || renderer.sharedMaterials.Length != mesh.subMeshCount)
                    throw new InvalidDataException("Missing mesh or mismatched material slots.");
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        throw new InvalidDataException("LOD submeshes must use triangle topology.");
                    triangles += mesh.GetIndexCount(submesh) / 3;
                }
            }
            if (renderers.Length == 0 || triangles != expectedTriangles)
                throw new InvalidDataException("Imported triangle count does not match the LOD manifest.");
        }

        [MenuItem("Assets/Polysmith/Create LOD prefab")]
        static void ImportSelected()
        {
            TextAsset manifest = null;
            GameObject materials = null;
            foreach (var asset in Selection.objects)
            {
                if (asset is TextAsset text && Path.GetExtension(AssetDatabase.GetAssetPath(text)) == ".json")
                    manifest = text;
                if (asset is GameObject model)
                    materials = model;
            }
            if (manifest == null)
            {
                EditorUtility.DisplayDialog("Create LOD prefab", "Select manifest.json (and the source prefab for original-UV exports).", "OK");
                return;
            }
            string path = AssetDatabase.GetAssetPath(manifest);
            string output = AssetDatabase.GenerateUniqueAssetPath(Path.GetDirectoryName(path).Replace('\\', '/') + "/PolysmithLOD.prefab");
            try { Selection.activeObject = Build(path, output, materials); }
            catch (Exception error) { EditorUtility.DisplayDialog("Create LOD prefab", error.Message, "OK"); }
        }

        [Serializable]
        sealed class Manifest
        {
            public int version;
            public string format;
            public bool reuseSourceMaterials;
            public Level[] levels;
        }

        [Serializable]
        sealed class Level
        {
            public string file;
            public double fitPercent;
            public float screenHeight;
            public int triangles;
            public int vertices;
        }
    }
}
