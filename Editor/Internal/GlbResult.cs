#if POLYSMITH_UNITY_GLTF
using System;
using System.Collections.Generic;
using System.IO;
using GLTF;
using UnityEditor;
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Loader;
using Object = UnityEngine.Object;

namespace Polysmith
{
    /// <summary>Temporary GLB import with no animation, and persistent copies of its embedded maps/materials.</summary>
    sealed class GlbResult : IDisposable, IResultMaps
    {
        public Transform PrefabRoot { get; }

        public GlbResult(string path)
        {
            _stream = File.OpenRead(path);
            try
            {
                GLTFParser.ParseJson(_stream, out var root);
                if (root.GetDefaultScene().Nodes.Count != 1)
                    throw new InvalidDataException("Expected one prefab root in the GLB result.");
                // The importer releases its native buffer views after loading the scene.
                // Preserve encoded image bytes before handing it the stream.
                _images = new byte[root.Images?.Count ?? 0][];
                if (_images.Length > 0)
                {
                    var chunk = GLTFParser.SeekToBinaryChunk(_stream, 0);
                    for (int i = 0; i < _images.Length; i++)
                    {
                        var view = root.Images[i].BufferView?.Value;
                        if (view == null || view.Buffer.Id != 0 || view.ByteOffset > chunk.Length ||
                            view.ByteLength > chunk.Length - view.ByteOffset)
                            throw new InvalidDataException("The result image is not inside the GLB binary chunk.");
                        _stream.Position = chunk.StartPosition + GLTFParser.CHUNK_HEADER_SIZE + view.ByteOffset;
                        var bytes = new byte[checked((int)view.ByteLength)];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int count = _stream.Read(bytes, read, bytes.Length - read);
                            if (count == 0)
                                throw new EndOfStreamException("The GLB image is truncated.");
                            read += count;
                        }
                        _images[i] = bytes;
                    }
                }
                _stream.Position = 0;
                _loader = new GLTFSceneImporter(root, _stream, new ImportOptions
                {
                    DataLoader = new FileLoader(Path.GetDirectoryName(path)), AnimationMethod = AnimationMethod.None,
                    ImportNormals = GLTFImporterNormals.Import, ImportTangents = GLTFImporterNormals.Calculate,
                    CameraImport = CameraImportOption.None
                });
                AsyncHelpers.RunSync(() => _loader.LoadSceneAsync(showSceneObj: false));
                PrefabRoot = _loader.LastLoadedScene.transform.GetChild(0);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Persistent copies of the result's materials and their maps. Each one is built on the render pipeline's Lit
        /// shader (<see cref="PipelineMaterial"/>), or UnityGLTF's when the pipeline's is not known. A reduced material
        /// whose shader the user changed since is kept as it is: its maps are rewritten in place, so it shows the result.
        /// </summary>
        public Dictionary<Material, Material> SaveMaterials(string folder, Dictionary<string, Material> originals,
            List<string> warnings)
        {
            _folder = folder;
            for (int i = 0; i < _loader.TextureCache.Length; i++)
                if (_loader.TextureCache[i]?.Texture != null)
                    _indexOf[_loader.TextureCache[i].Texture] = i;
            var template = PipelineMaterial.Template(warnings);
            try
            {
                var result = new Dictionary<Material, Material>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var renderer in PrefabRoot.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || result.ContainsKey(material))
                            continue;
                        bool hasAtlas = false;
                        foreach (string property in material.GetTexturePropertyNames())
                        {
                            var texture = material.GetTexture(property);
                            if (texture != null && _indexOf.ContainsKey(texture))
                                hasAtlas = true;
                        }
                        // Original-UV exports retain slot names but do not embed source textures.
                        // Reuse the remembered authored material, never a texture-less imported substitute.
                        if (hasAtlas == false && originals.TryGetValue(material.name, out var original))
                        {
                            result.Add(material, original);
                            continue;
                        }
                        var copy = template != null ? PipelineMaterial.Build(template, material, this) : ImportedCopy(material);
                        copy.name = PolysmithRoundTrip.Unique(PolysmithRoundTrip.Sanitize(material.name), names);
                        result.Add(material, Save(copy, folder + "/" + copy.name + ".mat"));
                    }
                return result;
            }
            finally
            {
                if (template != null)
                    Object.DestroyImmediate(template);
            }
        }

        // UnityGLTF's own material, with its maps persisted as they are (it reads RGB normals directly).
        Material ImportedCopy(Material material)
        {
            var copy = new Material(material);
            foreach (string property in copy.GetTexturePropertyNames())
            {
                var texture = copy.GetTexture(property);
                if (texture != null)
                    copy.SetTexture(property, Map(texture, false));
            }
            return copy;
        }

        // The importer's userData records the shader the last export built the material with.
        static Material Save(Material copy, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, path);
                existing = copy;
            }
            else if (UserChangedShader(existing, path))
            {
                Object.DestroyImmediate(copy);
                return existing;
            }
            else
            {
                EditorUtility.CopySerialized(copy, existing);
                Object.DestroyImmediate(copy);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);
            }
            var importer = AssetImporter.GetAtPath(path);
            if (importer.userData != BuiltWith + existing.shader.name)
            {
                importer.userData = BuiltWith + existing.shader.name;
                importer.SaveAndReimport();
            }
            AssetDatabase.SetLabels(existing, new[] { PolysmithRoundTrip.ReducedLabel });
            return existing;
        }

        // A material from before the record, or on a shader the project no longer has, is Polysmith's to rebuild.
        static bool UserChangedShader(Material existing, string path)
        {
            string record = AssetImporter.GetAtPath(path).userData ?? "";
            return record.StartsWith(BuiltWith, StringComparison.Ordinal) &&
                   existing.shader.name != record.Substring(BuiltWith.Length) &&
                   existing.shader.name != "Hidden/InternalErrorShader";
        }

        public Texture2D Map(Texture imported, bool normal)
        {
            int index = IndexOf(imported);
            var image = Image(index, out bool jpeg);
            return Persist((index, normal ? "normal" : "", 1f, 1f), "atlas_" + index, jpeg ? ".jpg" : ".png", () => image,
                normal ? TextureImporterType.NormalMap : TextureImporterType.Default,
                _loader.TextureCache[index].IsLinear == false, imported.width, imported.height);
        }

        public Texture2D Packed(MapPacking packing, Texture metallicRoughness, float metallic, float roughness,
            Texture occlusion)
        {
            if (metallicRoughness == null && occlusion == null)
                return null;
            int mr = metallicRoughness != null ? IndexOf(metallicRoughness) : -1;
            int ao = occlusion != null ? IndexOf(occlusion) : -1;
            var size = metallicRoughness != null ? metallicRoughness : occlusion;
            return Persist((mr * 1000 + ao, packing.ToString(), metallic, roughness),
                "atlas_" + (mr >= 0 ? mr : ao) + "_" + packing.ToString().ToLowerInvariant(), ".png",
                () => Pack(packing, mr >= 0 ? Image(mr, out _) : null, metallic, roughness, ao >= 0 ? Image(ao, out _) : null),
                TextureImporterType.Default, false, size.width, size.height);
        }

        int IndexOf(Texture texture) => _indexOf.TryGetValue(texture, out int index)
            ? index
            : throw new InvalidDataException("The result material references a texture that is not in the GLB: " + texture.name);

        byte[] Image(int index, out bool jpeg)
        {
            var image = _loader.Root.Textures[index].Source.Value;
            if (image.BufferView == null || (image.MimeType != "image/png" && image.MimeType != "image/jpeg"))
                throw new InvalidDataException("The result image must be an embedded PNG or JPEG.");
            jpeg = image.MimeType == "image/jpeg";
            return _images[_loader.Root.Textures[index].Source.Id];
        }

        // One texture asset per distinct use; rewritten in place, so materials referencing it keep it.
        Texture2D Persist((int, string, float, float) key, string name, string extension, Func<byte[]> encode,
            TextureImporterType type, bool sRGB, int width, int height)
        {
            if (_persisted.TryGetValue(key, out var done))
                return done;
            string path = _folder + "/" + name + extension;
            for (int suffix = 2; _paths.Contains(path); suffix++)
                path = _folder + "/" + name + "_" + suffix + extension;
            _paths.Add(path);
            byte[] bytes = encode();
            if (File.Exists(path) == false || File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) == false)
            {
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = type;
            importer.sRGBTexture = sRGB;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(Mathf.Max(width, height)));
            importer.SaveAndReimport();
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            AssetDatabase.SetLabels(asset, new[] { PolysmithRoundTrip.ReducedLabel });
            _persisted.Add(key, asset);
            return asset;
        }

        // glTF: metallic B, roughness G (times the factors), occlusion R. Unity: metallic R, occlusion G, smoothness A.
        static byte[] Pack(MapPacking packing, byte[] metallicRoughness, float metallic, float roughness, byte[] occlusion)
        {
            var mr = Decode(metallicRoughness);
            var ao = Decode(occlusion);
            try
            {
                var size = mr != null ? mr : ao;
                var mrPixels = mr != null ? mr.GetPixels32() : null;
                bool sameSize = ao != null && (mr == null || (ao.width == mr.width && ao.height == mr.height));
                var aoPixels = sameSize ? ao.GetPixels32() : null;
                var output = new Color32[size.width * size.height];
                for (int i = 0; i < output.Length; i++)
                {
                    byte metal = 0, smooth = 255, occluded = 255;
                    if (mrPixels != null)
                    {
                        metal = (byte)Mathf.RoundToInt(mrPixels[i].b * metallic);
                        smooth = (byte)(255 - Mathf.RoundToInt(mrPixels[i].g * roughness));
                    }
                    if (aoPixels != null)
                        occluded = aoPixels[i].r;
                    else if (ao != null)
                        occluded = (byte)Mathf.RoundToInt(ao.GetPixelBilinear((i % size.width + .5f) / size.width,
                            (i / size.width + .5f) / size.height).r * 255);
                    output[i] = packing == MapPacking.Occlusion ? new Color32(occluded, occluded, occluded, 255)
                        : packing == MapPacking.HdrpMask ? new Color32(metal, occluded, 0, smooth)
                        : new Color32(metal, metal, metal, smooth);
                }
                var packed = new Texture2D(size.width, size.height, TextureFormat.RGBA32, false, true);
                try
                {
                    packed.SetPixels32(output);
                    return packed.EncodeToPNG();
                }
                finally
                {
                    Object.DestroyImmediate(packed);
                }
            }
            finally
            {
                if (mr != null)
                    Object.DestroyImmediate(mr);
                if (ao != null)
                    Object.DestroyImmediate(ao);
            }
        }

        static Texture2D Decode(byte[] encoded)
        {
            if (encoded == null)
                return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (texture.LoadImage(encoded) == false)
            {
                Object.DestroyImmediate(texture);
                throw new InvalidDataException("A result map could not be decoded.");
            }
            return texture;
        }

        public void Dispose()
        {
            if (_loader?.LastLoadedScene != null)
                Object.DestroyImmediate(_loader.LastLoadedScene);
            _loader?.Dispose();
            _stream?.Dispose();
        }

        const string BuiltWith = "Polysmith built with ";

        readonly Dictionary<Texture, int> _indexOf = new Dictionary<Texture, int>();
        readonly Dictionary<(int, string, float, float), Texture2D> _persisted =
            new Dictionary<(int, string, float, float), Texture2D>();
        readonly HashSet<string> _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string _folder;
        readonly FileStream _stream;
        readonly GLTFSceneImporter _loader;
        readonly byte[][] _images;
    }
}
#endif
