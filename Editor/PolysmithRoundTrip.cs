using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// Sends a prefab's meshes to Polysmith and takes back the reduced ones as new mesh assets. Generic: any prefab.
    /// <list type="number">
    /// <item><see cref="WriteRequest"/> writes every distinct MeshFilter mesh into one OBJ, one object per mesh and
    /// one material group per submesh, placed as the prefab shows it.</item>
    /// <item>Polysmith exports each object it reduced as "&lt;object&gt;_lod.obj" next to that OBJ.</item>
    /// <item><see cref="ApplyResults"/> saves each as "&lt;prefab folder&gt;/Reduced/&lt;prefab&gt;/&lt;object&gt;.asset",
    /// labelled <see cref="ReducedLabel"/>, and points every MeshFilter of the prefab that used the mesh at it. Meshes
    /// without a result stay as they are; original mesh assets are never changed.</item>
    /// </list>
    /// Precondition: the prefab's meshes and transforms do not change between the two calls, since objects are
    /// matched by name and moved back into mesh space by the same placement.
    /// </summary>
    public static class PolysmithRoundTrip
    {
        /// <summary>
        /// Asset label of every mesh this tool wrote. A tool that regenerates meshes can check it to keep a reduction.
        /// </summary>
        public const string ReducedLabel = "PolysmithReduced";

        /// <summary>
        /// Writes the request OBJ into <paramref name="folder"/> and returns its path. Throws when the prefab has no
        /// mesh to send (Polysmith cannot open an empty file), e.g. when its meshes are built at run time.
        /// </summary>
        public static string WriteRequest(GameObject prefab, string folder, List<string> warnings)
        {
            var colours = new Dictionary<string, Color>();
            var objects = Objects(prefab, warnings, colours);
            if (objects.Count == 0)
                throw new InvalidOperationException($"'{prefab.name}' has no triangle mesh assets on its MeshFilters, " +
                                                    "so there is nothing to send to Polysmith.");
            string stem = Sanitize(prefab.name);
            // Polysmith knows material groups only from an MTL; without one every face loads as one material.
            var writer = new ObjWriter(stem + ".mtl");
            foreach (var part in objects)
                writer.Append(part.Name, part.Mesh, part.Placement, part.Groups);
            File.WriteAllText(Path.Combine(folder, stem + ".mtl"), ObjMeshFile.Mtl(colours));
            string path = Path.Combine(folder, stem + ".obj");
            File.WriteAllText(path, writer.ToString());
            return path;
        }

        /// <summary>Applies the "_lod.obj" files in <paramref name="folder"/> and returns the reduced mesh assets.</summary>
        public static List<Mesh> ApplyResults(GameObject prefab, string folder, List<string> warnings)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            string prefabFolder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string output = null;
            AtlasTextures textures = null;
            ReducedMaterials materials = null;
            var replacements = new Dictionary<Mesh, (Mesh Mesh, Material[] Materials)>();
            // WriteRequest already reported the meshes it left out.
            foreach (var part in Objects(prefab, new List<string>(), new Dictionary<string, Color>()))
            {
                string result = Path.Combine(folder, part.Name + "_lod.obj");
                if (File.Exists(result) == false)
                    continue;
                if (output == null)
                {
                    output = EnsureFolder(EnsureFolder(prefabFolder, "Reduced"), Sanitize(prefab.name));
                    textures = new AtlasTextures(output);
                    materials = new ReducedMaterials(output, textures);
                }

                var exported = MtlFile.Read(result);
                var built = HasNormalMap(exported)
                    ? TangentObjImport.Read(result, part.Groups, part.Placement, warnings)
                    : ObjMeshFile.Read(result, part.Groups, part.Placement, warnings);
                if (built == null)
                    continue;
                built.name = part.Name;
                // As Unity's model importer does: buffers reordered for the GPU's vertex cache, and the UV metric texture
                // streaming picks mip levels from (meshes built in code have none).
                built.Optimize();
                built.RecalculateUVDistributionMetrics();
                replacements[part.Mesh] = (Save(built, output), SlotMaterials(part, exported, materials, warnings));
            }

            if (replacements.Count > 0)
                Repoint(prefabPath, replacements);
            var reduced = new List<Mesh>();
            foreach (var replacement in replacements.Values)
                reduced.Add(replacement.Mesh);
            return reduced;
        }

        static bool HasNormalMap(Dictionary<string, MtlMaterial> exported)
        {
            foreach (var material in exported.Values)
                if (material.Maps.ContainsKey(MapKind.Normal))
                    return true;
            return false;
        }

        // The material per submesh: the reduced one where the export has maps, or where an earlier export left a reduced
        // material whose maps must go now; null keeps the slot as it is.
        static Material[] SlotMaterials(Part part, Dictionary<string, MtlMaterial> exported, ReducedMaterials materials,
            List<string> warnings)
        {
            var slots = new Material[part.Groups.Length];
            for (int submesh = 0; submesh < slots.Length; submesh++)
            {
                var source = submesh < part.Materials.Length ? part.Materials[submesh] : null;
                if (source == null)
                    continue;
                exported.TryGetValue(part.Groups[submesh], out var maps);
                bool hasMaps = maps != null && maps.Maps.Count > 0;
                bool wasReduced = Array.IndexOf(AssetDatabase.GetLabels(source), ReducedLabel) >= 0;
                if (hasMaps || wasReduced)
                    slots[submesh] = materials.Get(part.Groups[submesh], source, maps ?? new MtlMaterial(), warnings);
            }
            return slots;
        }

        // Distinct meshes in hierarchy order, each named after its mesh and made unique, and placed where its first
        // MeshFilter shows it relative to the prefab root. Only triangle meshes go: Polysmith reduces triangles.
        // Each submesh's group is named after the material its first renderer shows there, so Polysmith gives every
        // material one atlas shared by the parts that use it; `colours` collects each group's colour for the MTL.
        static List<Part> Objects(GameObject prefab, List<string> warnings, Dictionary<string, Color> colours)
        {
            var objects = new List<Part>();
            var rootFromWorld = prefab.transform.worldToLocalMatrix;
            var seen = new HashSet<Mesh>();
            // Names become file names, which differ only by more than case on Windows and macOS.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groupOfMaterial = new Dictionary<Material, string>();
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || seen.Add(mesh) == false)
                    continue;
                if (TrianglesOnly(mesh) == false)
                {
                    warnings.Add($"Mesh '{mesh.name}' on '{filter.name}' has a submesh that is not triangles; it was not sent.");
                    continue;
                }

                string name = Sanitize(mesh.name);
                if (name.Length == 0)
                    name = "mesh";
                string unique = name;
                for (int suffix = 2; names.Add(unique) == false; suffix++)
                    unique = $"{name}_{suffix}";
                var materials = filter.TryGetComponent(out MeshRenderer renderer) ? renderer.sharedMaterials : new Material[0];
                var groups = new string[mesh.subMeshCount];
                for (int submesh = 0; submesh < groups.Length; submesh++)
                {
                    var material = submesh < materials.Length ? materials[submesh] : null;
                    string group = material != null ? GroupOf(material, groupOfMaterial, colours) : "none";
                    // One material on two submeshes of a mesh would merge them: the later one gets its own group.
                    if (Array.IndexOf(groups, group, 0, submesh) >= 0)
                        group = $"{group}_{unique}_{submesh}";
                    colours[group] = material != null ? ColourOf(material) : Color.white;
                    groups[submesh] = group;
                }
                objects.Add(new Part(unique, mesh, rootFromWorld * filter.transform.localToWorldMatrix, groups, materials));
            }
            return objects;
        }

        // A group name per material, unique ignoring case: different materials can share a name.
        static string GroupOf(Material material, Dictionary<Material, string> groupOfMaterial, Dictionary<string, Color> colours)
        {
            if (groupOfMaterial.TryGetValue(material, out string group))
                return group;
            string name = Sanitize(material.name);
            group = name;
            for (int suffix = 2; ContainsIgnoringCase(colours.Keys, group); suffix++)
                group = $"{name}_{suffix}";
            groupOfMaterial[material] = group;
            return group;
        }

        static bool ContainsIgnoringCase(IEnumerable<string> names, string name)
        {
            foreach (string existing in names)
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // The colour Polysmith shows: the base colour of URP/HDRP, or of the Built-in pipeline.
        static Color ColourOf(Material material)
        {
            if (material.HasProperty("_BaseColor"))
                return material.GetColor("_BaseColor");
            return material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
        }

        readonly struct Part
        {
            public Part(string name, Mesh mesh, Matrix4x4 placement, string[] groups, Material[] materials)
            {
                Name = name;
                Mesh = mesh;
                Placement = placement;
                Groups = groups;
                Materials = materials;
            }

            public string Name { get; }
            public Mesh Mesh { get; }
            public Matrix4x4 Placement { get; }
            public string[] Groups { get; }
            // The first renderer's materials, one per submesh where it has them.
            public Material[] Materials { get; }
        }

        static bool TrianglesOnly(Mesh mesh)
        {
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                    return false;
            return true;
        }

        // An existing asset is overwritten in place, so its GUID and every reference to it survive. The file is named
        // after the object because Unity names a mesh asset after its file: a later run then finds the same object.
        static Mesh Save(Mesh built, string folder)
        {
            string path = $"{folder}/{built.name}.asset";

            var asset = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (asset == null)
            {
                AssetDatabase.CreateAsset(built, path);
                asset = built;
            }
            else
            {
                EditorUtility.CopySerialized(built, asset);
                asset.name = built.name;
                UnityEngine.Object.DestroyImmediate(built);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
            }

            var labels = new List<string>(AssetDatabase.GetLabels(asset));
            if (labels.Contains(ReducedLabel) == false)
            {
                labels.Add(ReducedLabel);
                AssetDatabase.SetLabels(asset, labels.ToArray());
            }
            return asset;
        }

        static string EnsureFolder(string parent, string name)
        {
            string folder = $"{parent}/{name}";
            if (AssetDatabase.IsValidFolder(folder) == false)
                AssetDatabase.CreateFolder(parent, name);
            return folder;
        }

        static void Repoint(string prefabPath, Dictionary<Mesh, (Mesh Mesh, Material[] Materials)> replacements)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (var filter in contents.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null || replacements.TryGetValue(filter.sharedMesh, out var reduced) == false)
                        continue;
                    filter.sharedMesh = reduced.Mesh;
                    if (filter.TryGetComponent(out MeshRenderer renderer) == false)
                        continue;
                    var slots = renderer.sharedMaterials;
                    for (int slot = 0; slot < slots.Length && slot < reduced.Materials.Length; slot++)
                        if (reduced.Materials[slot] != null)
                            slots[slot] = reduced.Materials[slot];
                    renderer.sharedMaterials = slots;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // Object and file names: letters, digits, '_' and '-' only.
        static string Sanitize(string name)
        {
            var sanitized = new StringBuilder(name.Length);
            foreach (char c in name)
                sanitized.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sanitized.ToString();
        }
    }
}
