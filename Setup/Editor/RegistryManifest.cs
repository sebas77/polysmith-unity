using System;
using Newtonsoft.Json.Linq;

namespace Polysmith.Setup
{
    /// <summary>Adds only UnityGLTF's OpenUPM scope, preserving unrelated manifest content.</summary>
    public static class RegistryManifest
    {
        public const string Package = "org.khronos.unitygltf";
        public const string Version = "2.24.0";
        public const string Url = "https://package.openupm.com";

        public static string Configure(string json)
        {
            var manifest = JObject.Parse(json);
            var registries = manifest["scopedRegistries"] as JArray;
            if (registries == null)
            {
                if (manifest["scopedRegistries"] != null)
                    throw new InvalidOperationException("The project's scopedRegistries is not an array.");
                registries = new JArray();
                manifest["scopedRegistries"] = registries;
            }
            JObject openUpm = null;
            foreach (JObject registry in registries)
            {
                bool matches = string.Equals(((string)registry["url"])?.TrimEnd('/'), Url,
                    StringComparison.OrdinalIgnoreCase);
                if (matches)
                    openUpm = registry;
                if (registry["scopes"] is JArray scopes)
                    foreach (string scope in scopes)
                        if (Package == scope || Package.StartsWith(scope + ".", StringComparison.Ordinal))
                        {
                            if (matches)
                                return json;
                            throw new InvalidOperationException("UnityGLTF is already routed to another registry. " +
                                "Change that scope in Project Settings > Package Manager before installing.");
                        }
            }
            if (openUpm == null)
            {
                openUpm = new JObject { ["name"] = "OpenUPM", ["url"] = Url, ["scopes"] = new JArray() };
                registries.Add(openUpm);
            }
            if (openUpm["scopes"] is not JArray openScopes)
                throw new InvalidOperationException("OpenUPM's scopes is not an array.");
            openScopes.Add(Package);
            return manifest.ToString() + Environment.NewLine;
        }
    }
}
