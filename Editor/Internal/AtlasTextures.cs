using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// Brings Polysmith's atlas PNGs into the project as PNG textures next to the reduced meshes, imported with the
    /// settings their kind needs. Unity builds the mips from the single level Polysmith writes; Polysmith's mip setting
    /// lays the charts out so those levels stay clean up to that level. Occlusion and metal-rough are repacked to
    /// Unity's channels; the other kinds are copied as they are. A file whose content has not changed is not rewritten.
    /// </summary>
    sealed class AtlasTextures
    {
        public AtlasTextures(string folder)
        {
            _folder = folder;
        }

        /// <summary>The texture for an atlas file, imported once per instance; null, with a warning, if it is unreadable.</summary>
        public Texture2D Get(string source, MapKind kind, List<string> warnings)
        {
            if (_imported.TryGetValue(source, out var texture))
                return texture;
            texture = Import(source, kind, warnings);
            _imported[source] = texture;
            return texture;
        }

        Texture2D Import(string source, MapKind kind, List<string> warnings)
        {
            string path = $"{_folder}/{Path.GetFileName(source)}";
            byte[] bytes = Repack(File.ReadAllBytes(source), kind, source, warnings);
            if (bytes == null)
                return null;
            if (File.Exists(path) == false || File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) == false)
            {
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (Configure(importer, kind, PngSize(bytes)))
                importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            AssetDatabase.SetLabels(texture, new[] { PolysmithRoundTrip.ReducedLabel });
            return texture;
        }

        // Unity's Lit shaders read occlusion from G and metallic (R) with smoothness (A); Polysmith writes occlusion in R
        // and glTF metal-rough (roughness in G, metallic in B).
        static byte[] Repack(byte[] png, MapKind kind, string source, List<string> warnings)
        {
            if (kind != MapKind.Occlusion && kind != MapKind.MetalRough)
                return png;
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (decoded.LoadImage(png) == false)
                {
                    warnings.Add($"'{source}' is not a readable PNG; its map was left out.");
                    return null;
                }
                var pixels = decoded.GetPixels32();
                for (int k = 0; k < pixels.Length; k++)
                    pixels[k] = kind == MapKind.Occlusion
                        ? new Color32(pixels[k].r, pixels[k].r, pixels[k].r, 255)
                        : new Color32(pixels[k].b, 0, 0, (byte)(255 - pixels[k].g));
                decoded.SetPixels32(pixels);
                return decoded.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(decoded);
            }
        }

        // True when a setting had to change. Colour, emissive and billboard are sRGB; the other kinds hold data. A
        // billboard's alpha is its silhouette, cut at 0.5 (ReducedMaterials): its mips keep the coverage at that value,
        // so a distant card does not thin out.
        static bool Configure(TextureImporter importer, MapKind kind, int size)
        {
            var type = kind == MapKind.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool srgb = kind == MapKind.Colour || kind == MapKind.Emissive || kind == MapKind.Billboard;
            bool cutout = kind == MapKind.Billboard;
            // The default maximum (2048) would shrink a larger atlas.
            int maxSize = Mathf.Max(32, Mathf.NextPowerOfTwo(size));
            if (importer.textureType == type && importer.sRGBTexture == srgb && importer.mipmapEnabled &&
                importer.wrapMode == TextureWrapMode.Clamp && importer.maxTextureSize == maxSize &&
                importer.alphaIsTransparency == cutout && importer.mipMapsPreserveCoverage == cutout &&
                (cutout == false || importer.alphaTestReferenceValue == ReducedMaterials.Cutoff))
                return false;
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.mipmapEnabled = true;
            // Charts are padded inside the atlas; repeating would bleed the opposite edge in.
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = maxSize;
            importer.alphaIsTransparency = cutout;
            importer.mipMapsPreserveCoverage = cutout;
            if (cutout)
                importer.alphaTestReferenceValue = ReducedMaterials.Cutoff;
            return true;
        }

        // The larger side, from the PNG header (width and height are big-endian at bytes 16 and 20).
        static int PngSize(byte[] png)
        {
            int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return Mathf.Max(width, height);
        }

        readonly string _folder;
        readonly Dictionary<string, Texture2D> _imported = new Dictionary<string, Texture2D>();
    }
}
