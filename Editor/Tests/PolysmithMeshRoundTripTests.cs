#if POLYSMITH_UNITY_GLTF
using System;
using System.Collections.Generic;
using System.IO;
using GLTF;
using GLTF.Schema;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Polysmith.Tests
{
    public sealed class PolysmithMeshRoundTripTests
    {
        [SetUp]
        public void SetUp()
        {
            _assets = "Assets/PolysmithMeshTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_assets));
            _folder = Path.Combine(Path.GetTempPath(), "polysmith_mesh_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _objects = new List<Object>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var created in _objects)
                if (created != null)
                    Object.DestroyImmediate(created);
            AssetDatabase.DeleteAsset(_assets);
            Directory.Delete(_folder, true);
        }

        [Test]
        public void MeshIsSentAloneAtTheOriginWithItsUsersMaterials()
        {
            var mesh = Mesh("tile 0-1");
            var material = Material("ground");
            User(mesh, material, new Vector3(10, 0, 20));
            var request = PolysmithMeshRoundTrip.WriteRequest(mesh, _folder, new List<string>());
            Assert.That(Path.GetFileName(request.Glb), Is.EqualTo("tile_0-1.glb"));
            Assert.That(request.Output, Is.EqualTo(_assets + "/Reduced/tile_0-1"));
            Assert.That(AssetDatabase.IsValidFolder(request.Output), Is.True);
            var root = Read(request.Glb);
            Assert.That(root.GetDefaultScene().Nodes.Count, Is.EqualTo(1));
            var node = root.GetDefaultScene().Nodes[0].Value;
            Assert.That(node.Translation.X == 0 && node.Translation.Y == 0 && node.Translation.Z == 0, Is.True);
            Assert.That(root.Meshes.Count, Is.EqualTo(1));
            Assert.That(root.Materials[0].Name, Is.EqualTo("ground"));
        }

        [Test]
        public void ResultIsSavedAndEveryUserInTheOpenScenesDrawsIt()
        {
            var mesh = Mesh("tile");
            var material = Material("ground");
            var first = User(mesh, material, Vector3.zero);
            var second = User(mesh, material, Vector3.one);
            var request = PolysmithMeshRoundTrip.WriteRequest(mesh, _folder, new List<string>());
            File.Copy(request.Glb, Path.Combine(_folder, request.Name + "_lod.glb"));
            var reduced = PolysmithMeshRoundTrip.ApplyResults(request, _folder, new List<string>());
            Assert.That(AssetDatabase.GetAssetPath(reduced), Is.EqualTo(_assets + "/Reduced/tile/tile.asset"));
            Assert.That(reduced.vertexCount, Is.EqualTo(3));
            Assert.That(Vector3.Distance(reduced.bounds.max, mesh.bounds.max), Is.LessThan(1e-5f), "same mesh space");
            Assert.That(first.sharedMesh, Is.EqualTo(reduced));
            Assert.That(second.sharedMesh, Is.EqualTo(reduced));
            Assert.That(mesh.vertexCount, Is.EqualTo(3), "the original is never changed");
            // The request carried no embedded maps, so the result reuses the original material.
            Assert.That(first.GetComponent<MeshRenderer>().sharedMaterial, Is.EqualTo(material));

            // The result selected again sends the original and is overwritten in place.
            Assert.That(PolysmithMeshRoundTrip.Key(reduced), Is.EqualTo(PolysmithMeshRoundTrip.Key(mesh)));
            var again = PolysmithMeshRoundTrip.WriteRequest(reduced, _folder, new List<string>());
            Assert.That(again.Mesh, Is.EqualTo(request.Mesh));
            Assert.That(again.Output, Is.EqualTo(request.Output));
            File.Copy(again.Glb, Path.Combine(_folder, again.Name + "_lod.glb"), true);
            Assert.That(PolysmithMeshRoundTrip.ApplyResults(again, _folder, new List<string>()), Is.EqualTo(reduced));
        }

        [Test]
        public void UnusedMeshIsSavedWithAWarningAndNothingDrawsIt()
        {
            var mesh = Mesh("loose");
            var warnings = new List<string>();
            var request = PolysmithMeshRoundTrip.WriteRequest(mesh, _folder, warnings);
            Assert.That(warnings.Exists(w => w.Contains("not drawn in the open scenes")), Is.True);
            Assert.That(request.Materials, Is.Empty);
            File.Copy(request.Glb, Path.Combine(_folder, request.Name + "_lod.glb"));
            var reduced = PolysmithMeshRoundTrip.ApplyResults(request, _folder, new List<string>());
            Assert.That(AssetDatabase.Contains(reduced), Is.True);
        }

        [Test]
        public void AnotherMeshWithTheSameNameGetsItsOwnFolder()
        {
            // "tile 1" and "tile_1" sanitize to the same folder name.
            var first = Mesh("tile 1");
            var request = PolysmithMeshRoundTrip.WriteRequest(first, _folder, new List<string>());
            File.Copy(request.Glb, Path.Combine(_folder, request.Name + "_lod.glb"));
            PolysmithMeshRoundTrip.ApplyResults(request, _folder, new List<string>());
            Assert.That(PolysmithMeshRoundTrip.WriteRequest(Mesh("tile_1"), _folder, new List<string>()).Output,
                Is.EqualTo(_assets + "/Reduced/tile_1_2"));
            Assert.That(PolysmithMeshRoundTrip.WriteRequest(first, _folder, new List<string>()).Output,
                Is.EqualTo(_assets + "/Reduced/tile_1"));
        }

        [Test]
        public void UnusedOutputFolderIsDeleted()
        {
            var request = PolysmithMeshRoundTrip.WriteRequest(Mesh("tile"), _folder, new List<string>());
            PolysmithMeshRoundTrip.DeleteUnusedOutput(request);
            Assert.That(AssetDatabase.IsValidFolder(request.Output), Is.False);
        }

        [Test]
        public void SkinnedMeshIsRefused()
        {
            var mesh = Mesh("skin");
            mesh.bindposes = new[] { Matrix4x4.identity };
            Assert.Throws<NotSupportedException>(() => PolysmithMeshRoundTrip.WriteRequest(mesh, _folder, new List<string>()));
        }

        Mesh Mesh(string name)
        {
            var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } };
            mesh.RecalculateNormals();
            AssetDatabase.CreateAsset(mesh, _assets + "/" + name + ".asset");
            return mesh;
        }

        Material Material(string name)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(material, _assets + "/" + name + ".mat");
            return material;
        }

        MeshFilter User(Mesh mesh, Material material, Vector3 position)
        {
            var user = new GameObject("user");
            _objects.Add(user);
            user.transform.position = position;
            user.AddComponent<MeshRenderer>().sharedMaterial = material;
            var filter = user.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            return filter;
        }

        static GLTFRoot Read(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                GLTFParser.ParseJson(stream, out var root);
                return root;
            }
        }

        string _assets;
        string _folder;
        List<Object> _objects;
    }
}
#endif
