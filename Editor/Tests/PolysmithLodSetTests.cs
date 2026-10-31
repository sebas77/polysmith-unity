using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Polysmith.Tests
{
    public sealed class PolysmithLodSetTests
    {
        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/PolysmithLodSetTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_folder);
        }

        [TestCase("../outside.json")]
        [TestCase("Packages/test/manifest.json")]
        [TestCase("Assets/../outside.json")]
        public void RejectsNonAssetManifest(string path)
        {
            Assert.Throws<ArgumentException>(() => PolysmithLodSet.Build(path, _folder + "/result.prefab"));
        }

        [TestCase("{\"version\":2,\"format\":\"polysmith-lod-set\",\"levels\":[]}")]
        [TestCase("{\"version\":1,\"format\":\"polysmith-lod-set\",\"levels\":[{}]}")]
        public void RejectsInvalidManifestBeforeCreatingPrefab(string json)
        {
            string path = _folder + "/manifest.json";
            File.WriteAllText(path, json);
            Assert.Throws<InvalidDataException>(() => PolysmithLodSet.Build(path, _folder + "/result.prefab"));
            Assert.That(File.Exists(_folder + "/result.prefab"), Is.False);
        }

        [TestCase("../LOD0.fbx", "LOD1.fbx", .5f, .2f, .25, .8)]
        [TestCase("LOD0.fbx", "LOD0.fbx", .5f, .2f, .25, .8)]
        [TestCase("LOD0.fbx", "LOD1.fbx", .2f, .5f, .25, .8)]
        [TestCase("LOD0.fbx", "LOD1.fbx", .5f, .2f, .8, .25)]
        public void RejectsUnsafeOrUnorderedLevels(string first, string second, float firstHeight, float secondHeight,
            double firstFit, double secondFit)
        {
            string path = _folder + "/manifest.json";
            string Level(string file, float height, double fit) =>
                "{\"file\":\"" + file + "\",\"screenHeight\":" + height.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"fitPercent\":" + fit.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"vertices\":4,\"triangles\":2}";
            File.WriteAllText(path, "{\"version\":1,\"format\":\"polysmith-lod-set\",\"levels\":[" +
                Level(first, firstHeight, firstFit) + "," + Level(second, secondHeight, secondFit) + "]}");
            Assert.Throws<InvalidDataException>(() => PolysmithLodSet.Build(path, _folder + "/result.prefab"));
        }

        // Inputs come from PolysmithLodSetSmoke.py, never a user's asset. Set
        // POLYSMITH_LOD_SET_TEST_DIR to that smoke's --output folder.
        [TestCase("static-atlas", false)]
        [TestCase("static-original", false)]
        [TestCase("skinned-atlas", true)]
        public void ImportsIndependentMeshesAndPreservesRig(string fixture, bool animated)
        {
            string external = Environment.GetEnvironmentVariable("POLYSMITH_LOD_SET_TEST_DIR");
            if (string.IsNullOrEmpty(external))
                Assert.Ignore("Generate fixtures with PolysmithLodSetSmoke.py and set POLYSMITH_LOD_SET_TEST_DIR.");
            string input = Path.Combine(external, fixture, "export");
            foreach (string file in Directory.GetFiles(input))
                if (file.EndsWith("~", StringComparison.Ordinal) == false)
                    File.Copy(file, _folder + "/" + Path.GetFileName(file));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            for (int level = 0; level < 3; level++)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(_folder + "/LOD" + level + ".fbx");
                importer.isReadable = true;
                importer.importNormals = ModelImporterNormals.Import;
                if (animated)
                {
                    importer.animationType = ModelImporterAnimationType.Generic;
                    importer.importAnimation = true;
                }
                importer.SaveAndReimport();
            }
            GameObject sourceMaterials = null;
            if (fixture == "static-original")
                sourceMaterials = CreateSourceMaterials();
            string output = _folder + "/result.prefab";
            var prefab = PolysmithLodSet.Build(_folder + "/manifest.json", output, sourceMaterials);
            var lods = prefab.GetComponent<LODGroup>().GetLODs();
            Assert.That(lods.Length, Is.EqualTo(3));
            var seen = new HashSet<Mesh>();
            for (int level = 0; level < lods.Length; level++)
            {
                Assert.That(lods[level].screenRelativeTransitionHeight, Is.EqualTo(new[] { .5f, .2f, .05f }[level]));
                Assert.That(lods[level].renderers.Length, Is.GreaterThan(0));
                foreach (var renderer in lods[level].renderers)
                {
                    var mesh = MeshOf(renderer);
                    Assert.That(seen.Add(mesh), Is.True, "Every renderer uses its own independent imported mesh.");
                    var used = new HashSet<int>();
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                        foreach (int index in mesh.GetIndices(submesh))
                            used.Add(index);
                    Assert.That(used.Count, Is.EqualTo(mesh.vertexCount), "No unused source vertex table in an imported LOD.");
                    Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(mesh.subMeshCount));
                    foreach (var material in renderer.sharedMaterials)
                        Assert.That(material, Is.Not.Null);
                    if (sourceMaterials != null)
                        foreach (var material in renderer.sharedMaterials)
                            Assert.That(AssetDatabase.GetAssetPath(material), Does.StartWith(_folder + "/source_materials/"));
                }
            }
            Assert.Throws<IOException>(() => PolysmithLodSet.Build(_folder + "/manifest.json", output, sourceMaterials));
            if (animated)
                CheckAnimation(prefab, lods);
        }

        GameObject CreateSourceMaterials()
        {
            AssetDatabase.CreateFolder(_folder, "source_materials");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(_folder + "/LOD0.fbx");
            var source = Object.Instantiate(model);
            try
            {
                var copies = new Dictionary<Material, Material>();
                foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (copies.TryGetValue(materials[i], out var copy) == false)
                        {
                            copy = new Material(materials[i]) { name = materials[i].name };
                            AssetDatabase.CreateAsset(copy, _folder + "/source_materials/" + copy.name + ".mat");
                            copies.Add(materials[i], copy);
                        }
                        materials[i] = copy;
                    }
                    renderer.sharedMaterials = materials;
                }
                return PrefabUtility.SaveAsPrefabAsset(source, _folder + "/source.prefab");
            }
            finally { Object.DestroyImmediate(source); }
        }

        void CheckAnimation(GameObject prefab, LOD[] assetLods)
        {
            var instance = Object.Instantiate(prefab);
            var baked = new Mesh();
            try
            {
                var root = instance.transform.Find("LOD0");
                AnimationClip clip = null;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(_folder + "/LOD0.fbx"))
                    if (asset is AnimationClip animation && animation.name.StartsWith("__preview__", StringComparison.Ordinal) == false)
                        clip = animation;
                Assert.That(clip, Is.Not.Null, "Source animation clips must survive export/import.");
                var lods = instance.GetComponent<LODGroup>().GetLODs();
                clip.SampleAnimation(root.gameObject, 0);
                var rest = new Dictionary<SkinnedMeshRenderer, Vector3[]>();
                foreach (var lod in lods)
                    foreach (var renderer in lod.renderers)
                        if (renderer is SkinnedMeshRenderer skinned)
                        {
                            foreach (var bone in skinned.bones)
                                Assert.That(bone.IsChildOf(root), Is.True, "All levels must bind to the one LOD0 rig.");
                            skinned.BakeMesh(baked);
                            rest.Add(skinned, baked.vertices);
                        }
                Assert.That(rest.Count, Is.GreaterThanOrEqualTo(assetLods.Length));
                clip.SampleAnimation(root.gameObject, clip.length * .9f);
                foreach (var pair in rest)
                {
                    pair.Key.BakeMesh(baked);
                    var vertices = baked.vertices;
                    float moved = 0;
                    for (int v = 0; v < vertices.Length; v++)
                        moved = Mathf.Max(moved, Vector3.Distance(vertices[v], pair.Value[v]));
                    Assert.That(moved, Is.GreaterThan(.0001f), "Every LOD must follow the same animated pose.");
                }
            }
            finally
            {
                Object.DestroyImmediate(baked);
                Object.DestroyImmediate(instance);
            }
        }

        static Mesh MeshOf(Renderer renderer) => renderer is SkinnedMeshRenderer skinned
            ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>().sharedMesh;

        string _folder;
    }
}
