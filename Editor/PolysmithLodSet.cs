using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Polysmith
{
    /// <summary>
    /// Imports Polysmith's independent levels into a prefab. One level is a
    /// plain animated model (a Polysmith GLB or FBX export); two to four become
    /// a conventional LODGroup. LOD0 retains the source hierarchy/animation,
    /// other levels place only their renderers below the corresponding LOD0
    /// nodes and reuse its bones. No runtime script or source-prefab mutation.
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
                ValidateRenderers(baseRenderers, manifest.levels[0].triangles, manifest.levels[0].file);
                foreach (var renderer in baseRenderers)
                    AssignMaterials(renderer, materials);
                lods[0] = new LOD(manifest.levels[0].screenHeight, baseRenderers);

                for (int level = 1; level < models.Length; level++)
                {
                    var imported = models[level].GetComponentsInChildren<Renderer>(true);
                    ValidateRenderers(imported, manifest.levels[level].triangles, manifest.levels[level].file);
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

                if (models.Length > 1)
                {
                    var group = root.AddComponent<LODGroup>();
                    group.SetLODs(lods);
                    group.RecalculateBounds();
                }
                else
                {
                    // One level: a plain model. Its own clips drive it through an
                    // Animator, which a glTF import leaves without a controller.
                    EnsureAnimation(first, folder + "/" + manifest.levels[0].file, prefabPath);
                }
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
                manifest.levels == null || manifest.levels.Length < 1 || manifest.levels.Length > 4)
                throw new InvalidDataException("Expected a Polysmith independent LOD-set manifest with one to four levels.");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            float previousHeight = 1;
            double previousFit = 0;
            foreach (var level in manifest.levels)
            {
                string extension = level == null || string.IsNullOrEmpty(level.file) ? null : Path.GetExtension(level.file);
                if (level == null || string.IsNullOrEmpty(level.file) || Path.GetFileName(level.file) != level.file ||
                    level.file.Contains("/") || level.file.Contains("\\") ||
                    (string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase) == false &&
                     string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase) == false) ||
                    files.Add(level.file) == false || level.screenHeight <= 0 || level.screenHeight >= previousHeight ||
                    float.IsNaN(level.screenHeight) || double.IsNaN(level.fitPercent) || double.IsInfinity(level.fitPercent) ||
                    level.fitPercent < 0 || level.fitPercent > 100 || level.vertices < 0 || level.triangles < 0)
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

        static void ValidateRenderers(Renderer[] renderers, int expectedTriangles, string file)
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
                    throw new InvalidDataException("Missing mesh or mismatched material slots in " + file);
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        throw new InvalidDataException("LOD submeshes must use triangle topology in " + file);
                    triangles += mesh.GetIndexCount(submesh) / 3;
                }
            }
            // A manifest written without counts (0) states no expectation.
            if (renderers.Length == 0 || (expectedTriangles > 0 && triangles != expectedTriangles))
                throw new InvalidDataException("Imported triangle count does not match the LOD manifest for " + file);
        }

        /// <summary>
        /// Gives a single exported level its own animation: a saved controller
        /// with one state per clip of the model and an Animator that plays it.
        /// glTFast imports clips but no controller, so a GLB would otherwise
        /// rest in its bind pose.
        /// </summary>
        static void EnsureAnimation(GameObject lod0, string modelPath, string prefabPath)
        {
            var animator = lod0.GetComponent<Animator>();
            if (animator == null)
                animator = lod0.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = lod0.AddComponent<Animator>();
            if (animator.runtimeAnimatorController != null)
                return;

            var clips = new List<AnimationClip>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
                if (asset is AnimationClip clip && clip.legacy == false)
                    clips.Add(clip);
            if (clips.Count == 0)
                return;

            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string path = folder + "/" + Path.GetFileNameWithoutExtension(prefabPath) + ".controller";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
                controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            var machine = controller.layers[0].stateMachine;
            foreach (var clip in clips)
            {
                bool present = false;
                foreach (var state in machine.states)
                    if (state.state.motion == clip)
                        present = true;
                if (present)
                    continue;
                var added = machine.AddState(clip.name);
                added.motion = clip;
                if (machine.defaultState == null)
                    machine.defaultState = added;
            }
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
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
