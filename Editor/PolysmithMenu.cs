using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Polysmith
{
    /// <summary>
    /// Assets menu "Optimise in Polysmith": opens the selected prefab's meshes in Polysmith; while it is open, every
    /// export is applied to the prefab (see <see cref="PolysmithWatcher"/>). The path of Polysmith.exe is asked once and
    /// kept per machine.
    /// </summary>
    static class PolysmithMenu
    {
        [MenuItem(MenuPath, true)]
        // Model files (FBX, OBJ) are prefab assets too, but they cannot be saved as prefabs.
        static bool CanOptimise() => Selection.activeObject is GameObject selected &&
                                     PrefabUtility.IsPartOfPrefabAsset(selected) &&
                                     PrefabUtility.IsPartOfModelPrefab(selected) == false;

        [MenuItem(MenuPath)]
        static void Optimise()
        {
            if (PolysmithWatcher.IsRunning)
            {
                EditorUtility.DisplayDialog("Optimise in Polysmith",
                    "Polysmith is already open for a prefab. Close it to finish that round trip first.", "OK");
                return;
            }
            var prefab = ((GameObject)Selection.activeObject).transform.root.gameObject;
            string exe = FindExe();
            if (exe == null)
                return;

            string folder = Path.Combine(Path.GetTempPath(), "polysmith_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var warnings = new List<string>();
            string request;
            try
            {
                request = PolysmithRoundTrip.WriteRequest(prefab, folder, warnings);
            }
            catch (InvalidOperationException nothingToSend)
            {
                Directory.Delete(folder, true);
                EditorUtility.DisplayDialog("Optimise in Polysmith", nothingToSend.Message, "OK");
                return;
            }
            foreach (string warning in warnings)
                Debug.LogWarning("Polysmith: " + warning);
            // --unity-roundtrip shows Polysmith's "Replace prefab mesh" button, which writes every part's result file.
            using (var process = Process.Start(new ProcessStartInfo(exe, $"\"{request}\" --unity-roundtrip")
                   {
                       UseShellExecute = false,
                       WorkingDirectory = WorkingFolder(exe)
                   }))
                PolysmithWatcher.Start(AssetDatabase.GetAssetPath(prefab), folder, process);
            Debug.Log($"Polysmith: opened '{prefab.name}'. Each export updates the prefab; close Polysmith to finish.");
        }

        // Null when the user closes the file panel.
        static string FindExe()
        {
            string exe = EditorPrefs.GetString(ExePathKey, "");
            if (File.Exists(exe))
                return exe;
            exe = EditorUtility.OpenFilePanel("Find Polysmith.exe", "", "exe");
            if (exe.Length == 0)
                return null;
            EditorPrefs.SetString(ExePathKey, exe);
            return exe;
        }

        // Polysmith loads its fonts from "resources/" under its working folder, so it starts in the nearest folder
        // above the exe that has one.
        static string WorkingFolder(string exe)
        {
            for (var folder = new DirectoryInfo(Path.GetDirectoryName(exe)); folder != null; folder = folder.Parent)
                if (Directory.Exists(Path.Combine(folder.FullName, "resources")))
                    return folder.FullName;
            return Path.GetDirectoryName(exe);
        }

        const string MenuPath = "Assets/Optimise in Polysmith";
        const string ExePathKey = "Polysmith.ExePath";
    }
}
