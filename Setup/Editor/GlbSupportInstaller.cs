using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Polysmith.Setup
{
    /// <summary>Compiles without UnityGLTF. Project registry changes require explicit confirmation.</summary>
    [InitializeOnLoad]
    public static class GlbSupportInstaller
    {
        public static bool Available
        {
            get
            {
#if POLYSMITH_UNITY_GLTF
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>Call after user approval; the setup dialog supplies that approval for interactive use.</summary>
        public static void Install()
        {
            if (_request != null)
                return;
            string manifest = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages/manifest.json");
            string before = File.ReadAllText(manifest);
            string after = RegistryManifest.Configure(before);
            if (before != after)
            {
                string temp = manifest + ".polysmith.tmp";
                File.WriteAllText(temp, after);
                File.Replace(temp, manifest, null);
            }
            _request = Client.Add(RegistryManifest.Package + "@" + RegistryManifest.Version);
            EditorApplication.update += Finish;
        }

        static GlbSupportInstaller()
        {
            if (Available == false && SessionState.GetBool(PromptKey, false) == false)
                EditorApplication.delayCall += Offer;
        }

        [MenuItem("Tools/Polysmith/Install GLB support")]
        static void Offer()
        {
            if (Available)
                return;
            SessionState.SetBool(PromptKey, true);
            if (EditorUtility.DisplayDialog("Polysmith GLB support",
                "Install UnityGLTF " + RegistryManifest.Version + "? This adds the OpenUPM registry for " +
                RegistryManifest.Package + " to this project's manifest and installs the package. " +
                "Polysmith's optimisation tools become available after compilation.", "Install", "Later") == false)
                return;
            try
            {
                Install();
            }
            catch (Exception error)
            {
                Debug.LogError("Polysmith setup: " + error.Message);
            }
        }

        static void Finish()
        {
            if (_request.IsCompleted == false)
                return;
            EditorApplication.update -= Finish;
            if (_request.Status == StatusCode.Success)
                Debug.Log("Polysmith: UnityGLTF installed. GLB support is enabled after compilation.");
            else
                Debug.LogError("Polysmith: UnityGLTF installation failed: " + _request.Error?.message +
                    ". Retry with Tools > Polysmith > Install GLB support.");
            _request = null;
        }

        const string PromptKey = "Polysmith.GlbSetupOffered";
        static AddRequest _request;
    }
}
