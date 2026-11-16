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
    /// "Optimise in Polysmith", in the Assets menu for a prefab asset or one mesh asset, and in the Hierarchy menu for a
    /// prefab instance (which opens the instance's prefab asset): opens the prefab's meshes, or the mesh, in a Polysmith
    /// of its own (several can be open at once); while it is open, every export is applied (see
    /// <see cref="PolysmithWatcher"/>). The path of Polysmith.exe is asked once and kept per machine.
    /// </summary>
    static class PolysmithMenu
    {
        [MenuItem(MenuPath, true)]
        static bool CanOptimise() => Selection.activeObject is GameObject selected ? IsEditablePrefab(selected) : SelectedMesh() != null;

        [MenuItem(MenuPath)]
        static void Optimise()
        {
            if (Selection.activeObject is GameObject selected)
                Optimise(selected.transform.root.gameObject);
            else
                OptimiseMesh(SelectedMesh());
        }

        // One mesh asset alone: a mesh used by several objects cannot be sent more than once, so no multiple selection.
        static Mesh SelectedMesh() => Selection.objects.Length == 1 && Selection.activeObject is Mesh mesh &&
                                      AssetDatabase.Contains(mesh) ? mesh : null;

        [MenuItem(HierarchyMenuPath, true)]
        static bool CanOptimiseInstance() => InstancePrefab(Selection.activeGameObject) != null;

        // Edits the prefab asset, so every instance of it shows the result, as with the Assets menu.
        [MenuItem(HierarchyMenuPath, false, HierarchyMenuPriority)]
        static void OptimiseInstance() => Optimise(InstancePrefab(Selection.activeGameObject));

        // Model files (FBX, OBJ) are prefab assets too, but they cannot be saved as prefabs.
        static bool IsEditablePrefab(GameObject asset) => PrefabUtility.IsPartOfPrefabAsset(asset) &&
                                                          PrefabUtility.IsPartOfModelPrefab(asset) == false;

        // The prefab asset of the outermost prefab instance the scene object belongs to; null for anything else.
        static GameObject InstancePrefab(GameObject sceneObject)
        {
            if (sceneObject == null || PrefabUtility.IsPartOfPrefabInstance(sceneObject) == false)
                return null;
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(sceneObject);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root));
            return asset != null && IsEditablePrefab(asset) ? asset : null;
        }

        static void Optimise(GameObject prefab)
        {
            // Every prefab gets its own Polysmith; one prefab has one at a time, or both would write its reduced assets.
            if (PolysmithWatcher.IsOpen(AssetDatabase.GetAssetPath(prefab)))
            {
                EditorUtility.DisplayDialog("Optimise in Polysmith",
                    $"Polysmith is already open for '{prefab.name}'. Close it to finish that round trip first.", "OK");
                return;
            }
            string exe = FindExe();
            if (exe == null)
                return;

            try
            {
                OpenPrefab(prefab, exe, false);
            }
            catch (Exception error)
            {
                EditorUtility.DisplayDialog("Optimise in Polysmith", error.Message, "OK");
            }
        }

        static void OptimiseMesh(Mesh mesh)
        {
            string key = PolysmithMeshRoundTrip.Key(mesh);
            if (PolysmithWatcher.IsOpen(key))
            {
                EditorUtility.DisplayDialog("Optimise in Polysmith",
                    $"Polysmith is already open for '{mesh.name}'. Close it to finish that round trip first.", "OK");
                return;
            }
            string exe = FindExe();
            if (exe == null)
                return;

            string folder = NewFolder();
            PolysmithMeshRoundTrip.Request request = null;
            try
            {
                var warnings = new List<string>();
                request = PolysmithMeshRoundTrip.WriteRequest(mesh, folder, warnings);
                foreach (string warning in warnings)
                    Debug.LogWarning("Polysmith: " + warning);
                Launch(exe, request.Glb, folder, false, process => PolysmithWatcher.Start(key, folder, process, request));
                Debug.Log($"Polysmith: opened mesh '{mesh.name}'. Each export updates the objects drawing it; close Polysmith to finish.");
            }
            catch (Exception error)
            {
                if (PolysmithWatcher.IsOpen(key) == false)
                {
                    Directory.Delete(folder, true);
                    if (request != null)
                        PolysmithMeshRoundTrip.DeleteUnusedOutput(request);
                }
                EditorUtility.DisplayDialog("Optimise in Polysmith", error.Message, "OK");
            }
        }

        // Shared launch path for the menu and the prefab-only automation API.
        // Automation never opens a picker or applies a result; the watcher only
        // receives files when an explicit export is requested in Polysmith.
        internal static int OpenPrefab(GameObject prefab, string exe, bool automation)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            if (PolysmithWatcher.IsOpen(prefabPath))
                throw new InvalidOperationException($"Polysmith is already open for '{prefabPath}'.");

            string folder = NewFolder();
            var warnings = new List<string>();
            string request;
            try
            {
                request = PolysmithRoundTrip.WriteRequest(prefab, folder, warnings);
            }
            catch
            {
                Directory.Delete(folder, true);
                throw;
            }
            foreach (string warning in warnings)
                Debug.LogWarning("Polysmith: " + warning);
            try
            {
                int id = Launch(exe, request, folder, automation, process => PolysmithWatcher.Start(prefabPath, folder, process));
                Debug.Log($"Polysmith: opened '{prefab.name}'. Each export updates the prefab; close Polysmith to finish.");
                return id;
            }
            catch
            {
                // Do not delete a folder owned by a successfully registered process.
                if (PolysmithWatcher.IsOpen(prefabPath) == false)
                    Directory.Delete(folder, true);
                throw;
            }
        }

        static string NewFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "polysmith_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        // Starts Polysmith on the request and hands its process to register (the watcher); returns the process id.
        static int Launch(string exe, string request, string folder, bool automation, Action<Process> register)
        {
            // --unity-roundtrip shows Polysmith's "Replace prefab mesh" button, which writes every part's result file.
            // Every interactive Polysmith has its automation mailbox: the removed --automation flag is refused.
            using (var process = Process.Start(new ProcessStartInfo(exe, $"\"{request}\" --unity-roundtrip")
                   {
                       UseShellExecute = false,
                       WorkingDirectory = automation ? folder : WorkingFolder(exe)
                   }))
            {
                if (process == null)
                    throw new InvalidOperationException("Polysmith did not start.");
                register(process);
                return process.Id;
            }
        }

        internal static string ConfiguredExe => EditorPrefs.GetString(ExePathKey, "");

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
        // GameObject/ items with a low priority are what the Hierarchy context menu lists (glTFast's export uses 31).
        const string HierarchyMenuPath = "GameObject/Optimise in Polysmith";
        const int HierarchyMenuPriority = 30;
        const string ExePathKey = "Polysmith.ExePath";
    }
}
