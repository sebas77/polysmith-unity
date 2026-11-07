using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// Prefab-asset-only handoff for Unity CLI. Discovery is separate from launch:
    /// callers inspect exact paths before opening them. Never exports or applies meshes.
    /// Uses Polysmith.Editor's existing round-trip ownership and watcher.
    /// </summary>
    public static class PolysmithAutomation
    {
        public static string[] FindPrefabs(string filter = "")
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab " + filter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (IsEditablePrefab(path))
                    paths.Add(path);
            }
            var result = new string[paths.Count];
            paths.CopyTo(result);
            return result;
        }

        /// <summary>Open each exact prefab path in its own visible, MCP-enabled Polysmith.</summary>
        public static LaunchResult[] OpenPrefabs(string[] prefabPaths, string executable = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait until Unity is idle in Edit Mode.");
            if (prefabPaths == null || prefabPaths.Length == 0)
                throw new ArgumentException("Explicit prefab asset paths are required.");
            string exe = string.IsNullOrEmpty(executable) ? PolysmithMenu.ConfiguredExe : executable;
            if (string.IsNullOrEmpty(exe) || Path.IsPathRooted(exe) == false || File.Exists(exe) == false)
                throw new ArgumentException("Configure Polysmith through the menu first, or pass an existing absolute executable path.");

            // Validate the WHOLE selection before changing Selection or starting any process.
            var prefabs = new List<GameObject>();
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in prefabPaths)
            {
                if (IsEditablePrefab(path) == false)
                    throw new ArgumentException($"'{path}' is not an editable prefab asset under Assets/. Scene objects and model assets are unsupported.");
                if (seen.Add(path) == false)
                    continue;
                paths.Add(path);
                prefabs.Add(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            }

            Selection.objects = prefabs.ToArray();
            var results = new List<LaunchResult>();
            for (int index = 0; index < prefabs.Count; index++)
            {
                var result = new LaunchResult { Prefab = paths[index] };
                try
                {
                    result.ProcessId = PolysmithMenu.OpenPrefab(prefabs[index], exe, true);
                    result.Opened = true;
                }
                catch (Exception error)
                {
                    // Partial launch results are explicit; never retry already opened prefabs.
                    result.Error = error.Message;
                }
                results.Add(result);
            }
            return results.ToArray();
        }

        public static bool IsEditablePrefab(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("Assets/", StringComparison.Ordinal) == false ||
                path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) == false ||
                path.Contains("/../") || path.Contains('\\'))
                return false;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return prefab != null && PrefabUtility.IsPartOfPrefabAsset(prefab) &&
                   PrefabUtility.IsPartOfModelPrefab(prefab) == false;
        }

        [Serializable]
        public sealed class LaunchResult
        {
            public string Prefab = "";
            public bool Opened;
            public int ProcessId;
            public string Error = "";
        }
    }
}
