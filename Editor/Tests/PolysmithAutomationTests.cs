using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Polysmith.Tests
{
    public sealed class PolysmithAutomationTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/model.fbx")]
        [TestCase("Packages/example/prefab.prefab")]
        [TestCase("Assets/../outside.prefab")]
        public void RejectsNonEditablePrefabPaths(string path)
        {
            Assert.That(PolysmithAutomation.IsEditablePrefab(path), Is.False);
        }

        [Test]
        public void RejectsEmptyLaunchWithoutChangingSelection()
        {
            var before = Selection.objects;
            Assert.Throws<ArgumentException>(() => PolysmithAutomation.OpenPrefabs(Array.Empty<string>()));
            Assert.That(Selection.objects, Is.EqualTo(before));
        }

        [Test]
        public void FindsOnlyEditablePrefabAssets()
        {
            foreach (string path in PolysmithAutomation.FindPrefabs())
                Assert.That(PolysmithAutomation.IsEditablePrefab(path), Is.True, path);
        }

        [Test]
        public void RejectsSceneObjectPath()
        {
            var sceneObject = new GameObject("Polysmith test (not an asset)");
            try
            {
                Assert.That(PolysmithAutomation.IsEditablePrefab(AssetDatabase.GetAssetPath(sceneObject)), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sceneObject);
            }
        }
    }
}
