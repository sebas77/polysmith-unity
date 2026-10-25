using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// without a result stay as they are; original mesh assets are never changed. Polysmith's billboards come back as
    /// extra submeshes ("billboard_&lt;n&gt;" groups) with cut-out materials of their own, one more material slot each.</item>
    /// </list>
    /// A round trip always starts from the ORIGINAL meshes and materials (Seb, 2026-10-25): every reduced mesh
    /// remembers them (<see cref="Origin"/>), and a prefab that already shows reduced meshes sends their originals.
    /// Precondition: the prefab's meshes and transforms do not change between the two calls, since objects are
    /// matched by name and moved back into mesh space by the same placement.
    /// </summary>
    public static class PolysmithRoundTrip
    {
        /// <summary>
        /// Asset label of every mesh this tool wrote. A tool that regenerates meshes can check it to keep a reduction.
        /// </summary>
        public const string ReducedLabel = "PolysmithReduced";

        // Image files a material's texture can be handed to Polysmith as: what raylib or Windows WIC decodes.
        static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".tga", ".bmp", ".psd", ".gif", ".hdr", ".pic", ".pnm", ".ppm", ".pgm", ".qoi", ".tif",
            ".tiff"
        };

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
            var maps = new Dictionary<string, (string Colour, string Normal)>();
            foreach (var part in objects)
                for (int submesh = 0; submesh < part.Groups.Length; submesh++)
                {
                    var material = submesh < part.Materials.Length ? part.Materials[submesh] : null;
                    if (material != null && maps.ContainsKey(part.Groups[submesh]) == false)
                        maps[part.Groups[submesh]] = Maps(part.Groups[submesh], material, folder, warnings);
                }
            File.WriteAllText(Path.Combine(folder, stem + ".mtl"), ObjMeshFile.Mtl(colours, maps));
            string path = Path.Combine(folder, stem + ".obj");
            File.WriteAllText(path, writer.ToString());
            return path;
        }

        // The maps Polysmith shows and bakes: the material's colour map (map_Kd) and normal map (norm). Its loader
        // reads no other kinds. The files are copied beside the OBJ so the MTL never depends on project paths.
        static (string Colour, string Normal) Maps(string group, Material material, string folder, List<string> warnings)
        {
            return (CopyMap(material, "_BaseMap", "_MainTex", group + "_base", folder, warnings),
                    CopyMap(material, "_BumpMap", null, group + "_normal", folder, warnings));
        }

        // The image file of a texture property, copied as "<name><ext>"; null when the material has none. An asset
        // that is not a separate image file (embedded in a model, or a package asset with no path on disk) cannot be
        // read by Polysmith, so it is left out with a warning.
        static string CopyMap(Material material, string property, string fallback, string name, string folder,
            List<string> warnings)
        {
            Texture texture = material.HasProperty(property) ? material.GetTexture(property) : null;
            if (texture == null && fallback != null && material.HasProperty(fallback))
                texture = material.GetTexture(fallback);
            if (texture == null)
                return null;
            string assetPath = AssetDatabase.GetAssetPath(texture);
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            // Polysmith's image loader picks the decoder by extension and knows ".jpg", not ".jpeg".
            if (extension == ".jpeg")
                extension = ".jpg";
            string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath));
            if (Array.IndexOf(ImageExtensions, extension) < 0 || File.Exists(source) == false)
            {
                warnings.Add($"Material '{material.name}' has a {property} that is not an image file on disk " +
                             $"('{assetPath}'); it was not sent.");
                return null;
            }
            string file = name + extension;
            File.Copy(source, Path.Combine(folder, file), true);
            return file;
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
                var groups = ObjMeshFile.GroupsOf(result, part.Groups);
                var built = HasNormalMap(exported)
                    ? TangentObjImport.Read(result, groups, part.Placement, warnings)
                    : ObjMeshFile.Read(result, groups, part.Placement, warnings);
                if (built == null)
                    continue;
                built.name = part.Name;
                // As Unity's model importer does: buffers reordered for the GPU's vertex cache, and the UV metric texture
                // streaming picks mip levels from (meshes built in code have none).
                built.Optimize();
                built.RecalculateUVDistributionMetrics();
                var asset = Save(built, output);
                RememberOrigin(asset, part);
                replacements[part.Mesh] = (asset, SlotMaterials(part, groups, exported, materials, warnings));
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

        // The material per submesh of the result (`groups`: the part's submeshes, then its billboard groups): the reduced
        // one where the export has maps, or where a reduced material whose maps must go now is the source (a prefab
        // reduced before origins were kept); else the part's own material, which is the original (Objects). A billboard
        // group gets its cut-out material, made from the part's first material; null keeps the slot as it is.
        static Material[] SlotMaterials(Part part, IReadOnlyList<string> groups, Dictionary<string, MtlMaterial> exported,
            ReducedMaterials materials, List<string> warnings)
        {
            var slots = new Material[groups.Count];
            for (int submesh = 0; submesh < slots.Length; submesh++)
            {
                exported.TryGetValue(groups[submesh], out var maps);
                if (submesh >= part.Groups.Length)
                {
                    var first = part.Materials.Length > 0 ? part.Materials[0] : null;
                    if (first == null)
                        warnings.Add($"'{part.Name}' has billboards but no material to make theirs from; their slot is empty.");
                    else
                        slots[submesh] = materials.GetBillboard($"{part.Name}_{groups[submesh]}", first,
                            maps ?? new MtlMaterial(), warnings);
                    continue;
                }

                var source = submesh < part.Materials.Length ? part.Materials[submesh] : null;
                if (source == null)
                    continue;
                bool hasMaps = maps != null && maps.Maps.Count > 0;
                bool wasReduced = Array.IndexOf(AssetDatabase.GetLabels(source), ReducedLabel) >= 0;
                slots[submesh] = hasMaps || wasReduced
                    ? materials.Get(groups[submesh], source, maps ?? new MtlMaterial(), warnings)
                    : source;
            }
            return slots;
        }

        // Distinct meshes in hierarchy order, each named after its mesh and made unique, and placed where its first
        // MeshFilter shows it in the prefab (the root's own rotation and scale included, so it stands as the prefab
        // does). Only triangle meshes go: Polysmith reduces triangles. A reduced mesh stands for its original, with the
        // original's materials (OriginOf): a round trip always starts from the originals.
        // Each submesh's group is named after the material its first renderer shows there, so Polysmith gives every
        // material one atlas shared by the parts that use it; `colours` collects each group's colour for the MTL.
        static List<Part> Objects(GameObject prefab, List<string> warnings, Dictionary<string, Color> colours)
        {
            var objects = new List<Part>();
            var seen = new HashSet<Mesh>();
            // Names become file names, which differ only by more than case on Windows and macOS.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groupOfMaterial = new Dictionary<Material, string>();
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                    continue;
                var showing = filter.TryGetComponent(out MeshRenderer renderer) ? renderer.sharedMaterials : new Material[0];
                var (mesh, materials) = OriginOf(filter.sharedMesh, showing, warnings);
                if (seen.Add(mesh) == false)
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
                objects.Add(new Part(unique, mesh, filter.transform.localToWorldMatrix, groups, materials));
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

        // `replacements` is keyed by the original mesh; a MeshFilter may show it or an earlier reduction of it.
        static void Repoint(string prefabPath, Dictionary<Mesh, (Mesh Mesh, Material[] Materials)> replacements)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (var filter in contents.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null)
                        continue;
                    var original = OriginOf(filter.sharedMesh, new Material[0], new List<string>()).Mesh;
                    if (replacements.TryGetValue(original, out var reduced) == false)
                        continue;
                    filter.sharedMesh = reduced.Mesh;
                    if (filter.TryGetComponent(out MeshRenderer renderer) == false)
                        continue;
                    // One slot per submesh of the result: billboard slots are added after the mesh's own. Surplus slots
                    // holding a material this tool made are an earlier export's billboards: Unity would draw the last
                    // submesh again with each, so they go.
                    var slots = new List<Material>(renderer.sharedMaterials);
                    int count = reduced.Materials.Length;
                    while (slots.Count > count && slots[slots.Count - 1] != null &&
                           Array.IndexOf(AssetDatabase.GetLabels(slots[slots.Count - 1]), ReducedLabel) >= 0)
                        slots.RemoveAt(slots.Count - 1);
                    while (slots.Count < count)
                        slots.Add(null);
                    for (int slot = 0; slot < count; slot++)
                        if (reduced.Materials[slot] != null)
                            slots[slot] = reduced.Materials[slot];
                    renderer.sharedMaterials = slots.ToArray();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// What a reduced mesh was made from, kept in its asset's import settings (userData, JSON): the original mesh
        /// and the original material of each slot, as "guid:localId" references (empty: none).
        /// </summary>
        [Serializable]
        sealed class Origin
        {
            public string Mesh = "";
            public string[] Materials = new string[0];
        }

        static void RememberOrigin(Mesh reduced, Part part)
        {
            var origin = new Origin { Mesh = Reference(part.Mesh), Materials = new string[part.Materials.Length] };
            for (int slot = 0; slot < part.Materials.Length; slot++)
                origin.Materials[slot] = Reference(part.Materials[slot]);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(reduced));
            string json = JsonUtility.ToJson(origin);
            if (importer.userData == json)
                return;
            importer.userData = json;
            importer.SaveAndReimport();
        }

        // The original mesh and materials `shown` stands for: those it remembers when it is a reduced mesh, else itself
        // with `showing` (its renderer's materials). A reduced mesh whose original is gone stands for itself, with a
        // warning.
        static (Mesh Mesh, Material[] Materials) OriginOf(Mesh shown, Material[] showing, List<string> warnings)
        {
            if (Array.IndexOf(AssetDatabase.GetLabels(shown), ReducedLabel) < 0)
                return (shown, showing);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(shown));
            if (importer == null || string.IsNullOrEmpty(importer.userData))
                return (shown, showing);
            var origin = JsonUtility.FromJson<Origin>(importer.userData);
            var mesh = Load<Mesh>(origin.Mesh);
            if (mesh == null)
            {
                warnings.Add($"The original of reduced mesh '{shown.name}' is gone: the reduced mesh was sent instead.");
                return (shown, showing);
            }
            var materials = new Material[origin.Materials.Length];
            for (int slot = 0; slot < materials.Length; slot++)
                materials[slot] = Load<Material>(origin.Materials[slot]);
            return (mesh, materials);
        }

        static string Reference(UnityEngine.Object asset) =>
            asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId)
                ? $"{guid}:{localId.ToString(CultureInfo.InvariantCulture)}"
                : "";

        // The asset a Reference names (a mesh inside a model file is one of its sub-assets); null when it is gone.
        static T Load<T>(string reference) where T : UnityEngine.Object
        {
            int colon = reference.IndexOf(':');
            if (colon < 0)
                return null;
            long localId = long.Parse(reference.Substring(colon + 1), CultureInfo.InvariantCulture);
            string path = AssetDatabase.GUIDToAssetPath(reference.Substring(0, colon));
            if (string.IsNullOrEmpty(path))
                return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is T typed && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id) &&
                    id == localId)
                    return typed;
            return null;
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
