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
    sealed class GlbResult : IDisposable
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

        public Dictionary<Material, Material> SaveMaterials(string folder, Dictionary<string, Material> originals)
        {
            var textures = new Dictionary<Texture, Texture>();
            var root = _loader.Root;
            for (int i = 0; i < _loader.TextureCache.Length; i++)
            {
                var cached = _loader.TextureCache[i];
                if (cached?.Texture == null)
                    continue;
                var image = root.Textures[i].Source.Value;
                if (image.BufferView == null || (image.MimeType != "image/png" && image.MimeType != "image/jpeg"))
                    throw new InvalidDataException("The result image must be an embedded PNG or JPEG.");
                string path = folder + "/atlas_" + i + (image.MimeType == "image/jpeg" ? ".jpg" : ".png");
                byte[] bytes = _images[root.Textures[i].Source.Id];
                if (File.Exists(path) == false || File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) == false)
                {
                    File.WriteAllBytes(path, bytes);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                // UnityGLTF materials read RGB normals directly; never use Unity's swizzled NormalMap importer.
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = cached.IsLinear == false;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(Mathf.Max(cached.Texture.width, cached.Texture.height)));
                importer.SaveAndReimport();
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                AssetDatabase.SetLabels(asset, new[] { PolysmithRoundTrip.ReducedLabel });
                textures[cached.Texture] = asset;
            }
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
                        if (texture != null && textures.ContainsKey(texture))
                            hasAtlas = true;
                    }
                    // Original-UV exports retain slot names but do not embed source textures.
                    // Reuse the remembered authored material, never a texture-less imported substitute.
                    if (hasAtlas == false && originals.TryGetValue(material.name, out var original))
                    {
                        result.Add(material, original);
                        continue;
                    }
                    var copy = new Material(material)
                    {
                        name = PolysmithRoundTrip.Unique(PolysmithRoundTrip.Sanitize(material.name), names)
                    };
                    foreach (string property in copy.GetTexturePropertyNames())
                    {
                        var texture = copy.GetTexture(property);
                        if (texture != null && textures.TryGetValue(texture, out var asset))
                            copy.SetTexture(property, asset);
                        else if (texture != null)
                            throw new InvalidDataException("The result material references an unpersisted texture: " + property);
                    }
                    string path = folder + "/" + copy.name + ".mat";
                    var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (existing == null)
                    {
                        AssetDatabase.CreateAsset(copy, path);
                        existing = copy;
                    }
                    else
                    {
                        EditorUtility.CopySerialized(copy, existing);
                        Object.DestroyImmediate(copy);
                        EditorUtility.SetDirty(existing);
                        AssetDatabase.SaveAssetIfDirty(existing);
                    }
                    AssetDatabase.SetLabels(existing, new[] { PolysmithRoundTrip.ReducedLabel });
                    result.Add(material, existing);
                }
            return result;
        }

        public void Dispose()
        {
            if (_loader?.LastLoadedScene != null)
                Object.DestroyImmediate(_loader.LastLoadedScene);
            _loader?.Dispose();
            _stream?.Dispose();
        }

        readonly FileStream _stream;
        readonly GLTFSceneImporter _loader;
        readonly byte[][] _images;
    }
}
#endif
