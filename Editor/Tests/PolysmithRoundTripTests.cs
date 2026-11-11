#if POLYSMITH_UNITY_GLTF
using System;
using System.Collections.Generic;
using System.IO;
using GLTF;
using GLTF.Schema;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Polysmith.Tests
{
    public sealed class PolysmithRoundTripTests
    {
        [SetUp]
        public void SetUp()
        {
            _assets = "Assets/PolysmithGlbTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_assets));
            _folder = Path.Combine(Path.GetTempPath(), "polysmith_glb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_assets);
            Directory.Delete(_folder, true);
        }

        [Test]
        public void StaticRequestIsOneGlbWithEmbeddedMapsAndNoClips()
        {
            var prefab = Prefab(false);
            string path = PolysmithRoundTrip.WriteRequest(prefab, _folder, new List<string>());
            Assert.That(Path.GetExtension(path), Is.EqualTo(".glb"));
            Assert.That(Directory.GetFiles(_folder).Length, Is.EqualTo(1));
            var root = Read(path);
            Assert.That(root.Animations == null || root.Animations.Count == 0, Is.True);
            Assert.That(root.Images.Count, Is.GreaterThan(0));
            Assert.That(root.Images[0].BufferView, Is.Not.Null);
        }

        [Test]
        public void AnimatedRequestCarriesSkinAndBoneTracks()
        {
            var prefab = Prefab(true);
            string path = PolysmithRoundTrip.WriteRequest(prefab, _folder, new List<string>());
            var root = Read(path);
            Assert.That(root.Skins.Count, Is.EqualTo(1));
            Assert.That(root.Animations.Count, Is.GreaterThan(0));
            Assert.That(root.Animations[0].Channels.Count, Is.GreaterThan(0));
            Assert.That(root.Meshes[0].Primitives[0].Attributes.ContainsKey("JOINTS_0"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReturnReplacesOnlyMeshesAndMaterialsAndNextRequestUsesOriginals(bool animated)
        {
            var prefab = Prefab(animated);
            var originalRenderer = prefab.GetComponentInChildren<Renderer>();
            var original = animated ? ((SkinnedMeshRenderer)originalRenderer).sharedMesh :
                originalRenderer.GetComponent<MeshFilter>().sharedMesh;
            var animator = prefab.GetComponent<Animator>();
            var controller = animator != null ? animator.runtimeAnimatorController : null;
            var avatar = animator != null ? animator.avatar : null;
            var rootRotation = prefab.transform.localRotation;
            var rootScale = prefab.transform.localScale;
            string request = PolysmithRoundTrip.WriteRequest(prefab, _folder, new List<string>());
            File.Copy(request, Path.Combine(_folder, prefab.name + "_lod.glb"));
            var reduced = PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            Assert.That(reduced.Count, Is.EqualTo(1));
            Assert.That(reduced[0], Is.Not.EqualTo(original));
            Assert.That(reduced[0].vertices, Is.EqualTo(original.vertices).Using(Vector3Comparer.Instance));
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(_assets + "/subject.prefab");
            Assert.That(prefab.transform.localRotation, Is.EqualTo(rootRotation));
            Assert.That(prefab.transform.localScale, Is.EqualTo(rootScale));
            if (animated)
            {
                var current = prefab.GetComponent<Animator>();
                Assert.That(current.runtimeAnimatorController, Is.EqualTo(controller));
                Assert.That(current.avatar, Is.EqualTo(avatar));
                Assert.That(current.runtimeAnimatorController.animationClips[0].name, Is.EqualTo("motion"));
                var skin = prefab.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(skin.bones[0].name, Is.EqualTo("joint"));
                Assert.That(skin.sharedMesh.bindposes, Is.EqualTo(original.bindposes));
                Assert.That(skin.sharedMesh.boneWeights[0].boneIndex0, Is.EqualTo(0));
            }
            var material = prefab.GetComponentInChildren<Renderer>().sharedMaterial;
            Assert.That(AssetDatabase.GetAssetPath(material), Does.StartWith(_assets + "/Reduced/"));
            foreach (string property in material.GetTexturePropertyNames())
            {
                var texture = material.GetTexture(property);
                if (texture != null)
                    Assert.That(AssetDatabase.Contains(texture), Is.True, property);
            }
            // A second request must use the original mesh, not the edited reduction.
            reduced[0].vertices = new[] { Vector3.one * 100, Vector3.one * 100, Vector3.one * 100 };
            EditorUtility.SetDirty(reduced[0]);
            AssetDatabase.SaveAssetIfDirty(reduced[0]);
            string second = PolysmithRoundTrip.WriteRequest(prefab, _folder, new List<string>());
            Assert.That(Read(second).Accessors.Find(a => a.Type == GLTFAccessorAttributeType.VEC3 && a.Max != null).Max[0],
                Is.LessThan(10));
        }

        [Test]
        public void RejectsDuplicateTransformPathsBeforeExport()
        {
            var root = new GameObject("ambiguous");
            try
            {
                new GameObject("same").transform.SetParent(root.transform);
                new GameObject("same").transform.SetParent(root.transform);
                Assert.Throws<InvalidDataException>(() => PolysmithRoundTrip.WriteRequest(root, _folder, new List<string>()));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FullyHiddenRendererReturnsAnEmptyMeshInsteadOfKeepingTheOriginal()
        {
            var prefab = Prefab(false);
            string path = AssetDatabase.GetAssetPath(prefab);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var second = Object.Instantiate(contents.transform.Find("part").gameObject, contents.transform);
                second.name = "second";
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var requestCopy = Object.Instantiate(prefab);
            try
            {
                requestCopy.name = prefab.name;
                var hidden = requestCopy.transform.Find("part");
                Object.DestroyImmediate(hidden.GetComponent<MeshRenderer>());
                Object.DestroyImmediate(hidden.GetComponent<MeshFilter>());
                string request = PolysmithRoundTrip.WriteRequest(requestCopy, _folder, new List<string>());
                File.Copy(request, Path.Combine(_folder, prefab.name + "_lod.glb"));
            }
            finally
            {
                Object.DestroyImmediate(requestCopy);
            }
            Assert.That(PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>()).Count, Is.EqualTo(2));
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab.transform.Find("part").GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.Zero);
            Assert.That(prefab.transform.Find("second").GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(3));
        }

        // A re-export overwrites the reduced mesh assets in place. A part merged away or hidden must not keep
        // any of its old geometry; a part still returned keeps its new one.
        [TestCase(false)]
        [TestCase(true)]
        public void ReExportHidingAPartEmptiesItsExistingReducedMesh(bool animated)
        {
            var prefab = TwoPartPrefab(animated);
            WriteResult(prefab, null);
            PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            WriteResult(prefab, "part");
            PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(prefab));
            var hidden = MeshOf(prefab.transform.Find("part"));
            Assert.That(hidden.vertexCount, Is.Zero);
            Assert.That(hidden.subMeshCount, Is.Zero);
            var kept = MeshOf(prefab.transform.Find("second"));
            Assert.That(kept.vertexCount, Is.EqualTo(3));
            Assert.That(kept.triangles.Length, Is.EqualTo(3));
            Assert.That(kept.HasVertexAttribute(VertexAttribute.BlendIndices), Is.EqualTo(animated));
        }

        static Mesh MeshOf(Transform node) => node.TryGetComponent<SkinnedMeshRenderer>(out var skin)
            ? skin.sharedMesh : node.GetComponent<MeshFilter>().sharedMesh;

        // The overwritten asset may be rendered already and laid out differently (here: a UV2 channel and
        // 32-bit indices); the skinned renderer must draw the new layout without a mesh data size error.
        [Test]
        public void ReExportOverAReducedMeshWithAnotherVertexLayoutStillRenders()
        {
            var prefab = Prefab(true);
            WriteResult(prefab, null);
            var reduced = PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>())[0];
            reduced.indexFormat = IndexFormat.UInt32;
            reduced.triangles = reduced.triangles;
            reduced.SetUVs(1, new[] { Vector2.zero, Vector2.one, Vector2.up });
            EditorUtility.SetDirty(reduced);
            AssetDatabase.SaveAssetIfDirty(reduced);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var camera = new GameObject("camera").AddComponent<Camera>();
            var target = new RenderTexture(64, 64, 24);
            try
            {
                var skin = instance.GetComponentInChildren<SkinnedMeshRenderer>();
                skin.updateWhenOffscreen = true;
                camera.targetTexture = target;
                camera.transform.position = skin.bounds.center - Vector3.forward * 5;
                camera.transform.LookAt(skin.bounds.center);
                camera.Render();

                WriteResult(prefab, null);
                PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
                // A rendering error is logged, which fails the test.
                camera.Render();
                Assert.That(reduced.HasVertexAttribute(VertexAttribute.TexCoord1), Is.False);
                Assert.That(reduced.vertexCount, Is.EqualTo(3));
                Assert.That(reduced.boneWeights[0].weight0, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(camera.gameObject);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        // Polysmith writes a billboard's cards as a primitive of their own, with their own vertices; UnityGLTF
        // imports it as a submesh with a base vertex. The applied mesh must keep those vertices.
        [Test]
        public void ResultSubmeshWithItsOwnVerticesKeepsThem()
        {
            var prefab = Prefab(false);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(_assets + "/triangle.asset");
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up,
                new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            mesh.RecalculateNormals();
            EditorUtility.SetDirty(mesh);
            var surface = AssetDatabase.LoadAssetAtPath<Material>(_assets + "/surface.mat");
            var cards = new Material(surface) { name = "cards" };
            AssetDatabase.CreateAsset(cards, _assets + "/cards.mat");
            AssetDatabase.SaveAssets();
            string path = AssetDatabase.GetAssetPath(prefab);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                contents.transform.Find("part").GetComponent<MeshRenderer>().sharedMaterials = new[] { surface, cards };
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            WriteResult(prefab, null);
            PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var applied = MeshOf(prefab.transform.Find("part"));
            Assert.That(applied.subMeshCount, Is.EqualTo(2));
            var used = new HashSet<int>(applied.GetIndices(0, true));
            used.UnionWith(applied.GetIndices(1, true));
            Assert.That(applied.vertexCount, Is.EqualTo(6));
            Assert.That(used.Count, Is.EqualTo(6), "each submesh draws its own vertices");
        }

        // A scene instance overriding the renderer's mesh and materials would hide the result: the apply reverts
        // those overrides, and only those.
        [TestCase(false)]
        [TestCase(true)]
        public void SceneInstanceMeshAndMaterialOverridesAreRevertedSoTheResultShows(bool animated)
        {
            var prefab = Prefab(animated);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var other = new Mesh { name = "other", vertices = new[] { Vector3.zero, Vector3.forward, Vector3.up },
                triangles = new[] { 0, 1, 2 } };
            var otherMaterial = new Material(prefab.GetComponentInChildren<Renderer>().sharedMaterial) { name = "other" };
            try
            {
                var part = instance.transform.Find("part");
                if (animated)
                    part.GetComponent<SkinnedMeshRenderer>().sharedMesh = other;
                else
                    part.GetComponent<MeshFilter>().sharedMesh = other;
                part.GetComponent<Renderer>().sharedMaterial = otherMaterial;
                instance.transform.position = Vector3.one;
                PrefabUtility.RecordPrefabInstancePropertyModifications(part.GetComponent<Renderer>());
                if (animated == false)
                    PrefabUtility.RecordPrefabInstancePropertyModifications(part.GetComponent<MeshFilter>());

                WriteResult(prefab, null);
                var warnings = new List<string>();
                var reduced = PolysmithRoundTrip.ApplyResults(prefab, _folder, warnings)[0];
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(_assets + "/subject.prefab");
                Assert.That(MeshOf(part), Is.EqualTo(reduced));
                Assert.That(part.GetComponent<Renderer>().sharedMaterial,
                    Is.EqualTo(prefab.GetComponentInChildren<Renderer>().sharedMaterial));
                Assert.That(instance.transform.position, Is.EqualTo(Vector3.one), "other overrides are kept");
                Assert.That(warnings.Exists(w => w.StartsWith("Reverted mesh/material overrides")), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(other);
                Object.DestroyImmediate(otherMaterial);
            }
        }

        // An atlas material is built on the render pipeline's Lit shader; once the user picks another shader for it,
        // re-exports keep that material and only rewrite its maps.
        [Test]
        public void AtlasMaterialUsesThePipelineLitShaderAndReExportsKeepAShaderTheUserChose()
        {
            var prefab = Prefab(false);
            WriteResult(prefab, null);
            PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            var reduced = AssetDatabase.LoadAssetAtPath<GameObject>(_assets + "/subject.prefab")
                .GetComponentInChildren<Renderer>().sharedMaterial;
            Assert.That(reduced.shader, Is.EqualTo(GraphicsSettings.currentRenderPipeline.defaultMaterial.shader));
            var baseMap = reduced.GetTexture("_BaseMap");
            Assert.That(baseMap, Is.Not.Null);

            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            reduced.shader = unlit;
            reduced.SetTexture("_BaseMap", baseMap);
            EditorUtility.SetDirty(reduced);
            AssetDatabase.SaveAssetIfDirty(reduced);
            WriteResult(prefab, null);
            PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            reduced = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(reduced));
            Assert.That(reduced.shader, Is.EqualTo(unlit));
            Assert.That(reduced.GetTexture("_BaseMap"), Is.EqualTo(baseMap));
        }

        [Test]
        public void OriginalUvResultWithoutEmbeddedMapsReusesTheOriginalMaterial()
        {
            var prefab = Prefab(false);
            var original = prefab.GetComponentInChildren<Renderer>().sharedMaterial;
            var instance = Object.Instantiate(prefab);
            var material = new Material(original);
            try
            {
                instance.name = prefab.name;
                material.name = original.name;
                material.SetTexture("_BaseMap", null);
                instance.GetComponentInChildren<Renderer>().sharedMaterial = material;
                string request = PolysmithRoundTrip.WriteRequest(instance, _folder, new List<string>());
                File.Copy(request, Path.Combine(_folder, prefab.name + "_lod.glb"));
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(material);
            }
            PolysmithRoundTrip.ApplyResults(prefab, _folder, new List<string>());
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(_assets + "/subject.prefab");
            Assert.That(prefab.GetComponentInChildren<Renderer>().sharedMaterial, Is.EqualTo(original));
            Assert.That(original.GetTexture("_BaseMap"), Is.Not.Null);
        }

        GameObject Prefab(bool animated)
        {
            var mesh = new Mesh { name = "triangle", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } };
            mesh.RecalculateNormals();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "surface" };
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            texture.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red, Color.red, Color.red, Color.red, Color.red,
                Color.red, Color.red, Color.red, Color.red, Color.red, Color.red, Color.red, Color.red });
            texture.Apply();
            File.WriteAllBytes(_assets + "/colour.png", texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(_assets + "/colour.png", ImportAssetOptions.ForceSynchronousImport);
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(_assets + "/colour.png"));
            AssetDatabase.CreateAsset(material, _assets + "/surface.mat");
            var root = new GameObject("subject");
            try
            {
                root.transform.localRotation = Quaternion.Euler(23, 37, 11);
                root.transform.localScale = new Vector3(.5f, 2, .75f);
                var part = new GameObject("part");
                part.transform.SetParent(root.transform, false);
                if (animated)
                {
                    var joint = new GameObject("joint");
                    joint.transform.SetParent(root.transform, false);
                    mesh.bindposes = new[] { Matrix4x4.identity };
                    mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                        new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 } };
                    var renderer = part.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.sharedMaterial = material;
                    renderer.bones = new[] { joint.transform };
                    renderer.rootBone = joint.transform;
                    var clip = new AnimationClip { name = "motion" };
                    clip.SetCurve("joint", typeof(Transform), "localPosition.y", AnimationCurve.Linear(0, 0, 1, 1));
                    AssetDatabase.CreateAsset(clip, _assets + "/motion.anim");
                    var controller = AnimatorController.CreateAnimatorControllerAtPath(_assets + "/motion.controller");
                    controller.AddMotion(clip);
                    root.AddComponent<Animator>().runtimeAnimatorController = controller;
                }
                else
                {
                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    part.AddComponent<MeshRenderer>().sharedMaterial = material;
                }
                AssetDatabase.CreateAsset(mesh, _assets + "/triangle.asset");
                return PrefabUtility.SaveAsPrefabAsset(root, _assets + "/subject.prefab");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        GameObject TwoPartPrefab(bool animated)
        {
            var prefab = Prefab(animated);
            string path = AssetDatabase.GetAssetPath(prefab);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var second = Object.Instantiate(contents.transform.Find("part").gameObject, contents.transform);
                second.name = "second";
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        // Writes the prefab's request as its Polysmith result; a hidden part is sent without its mesh, as an
        // export of a fully hidden or merged-away object.
        void WriteResult(GameObject prefab, string hiddenPart)
        {
            var requestCopy = Object.Instantiate(prefab);
            try
            {
                requestCopy.name = prefab.name;
                if (hiddenPart != null)
                {
                    var hidden = requestCopy.transform.Find(hiddenPart);
                    Object.DestroyImmediate(hidden.GetComponent<Renderer>());
                    if (hidden.TryGetComponent<MeshFilter>(out var filter))
                        Object.DestroyImmediate(filter);
                }
                string request = PolysmithRoundTrip.WriteRequest(requestCopy, _folder, new List<string>());
                File.Copy(request, Path.Combine(_folder, prefab.name + "_lod.glb"), true);
            }
            finally
            {
                Object.DestroyImmediate(requestCopy);
            }
        }

        static GLTFRoot Read(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                GLTFParser.ParseJson(stream, out var root);
                return root;
            }
        }

        sealed class Vector3Comparer : IEqualityComparer<Vector3>
        {
            public static readonly Vector3Comparer Instance = new Vector3Comparer();
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;
            public int GetHashCode(Vector3 value) => 0;
        }

        string _folder;
        string _assets;
    }
}
#endif
