using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// Turns Polysmith's atlas PNGs into texture assets that keep Polysmith's own mip levels: level 0 is
    /// "&lt;A&gt;&lt;suffix&gt;.png", level l is "&lt;A&gt;&lt;suffix&gt;_mip&lt;l&gt;.png" (as many as exist), and the texture
    /// has exactly that many levels, so Unity never builds its own (they would blend neighbouring charts). Channels are
    /// moved to Unity's packing, and each texture is compressed level by level. A texture whose files have not changed
    /// since it was built is reused.
    /// </summary>
    sealed class AtlasTextures
    {
        public AtlasTextures(string folder)
        {
            _folder = folder;
        }

        /// <summary>The texture for a level-0 file, built once per instance; null, with a warning, if it is unreadable.</summary>
        public Texture2D Get(string level0, MapKind kind, List<string> warnings)
        {
            if (_built.TryGetValue(level0, out var texture))
                return texture;
            texture = Build(level0, kind, warnings);
            _built[level0] = texture;
            return texture;
        }

        Texture2D Build(string level0, MapKind kind, List<string> warnings)
        {
            var levels = Levels(level0);
            string path = $"{_folder}/{Path.GetFileNameWithoutExtension(level0)}.asset";
            string stamp = Stamp(levels, kind);
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null && AssetImporter.GetAtPath(path).userData == stamp)
                return existing;

            var pixels = new List<Color32[]>();
            int width = 0;
            int height = 0;
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                for (int level = 0; level < levels.Count; level++)
                {
                    if (decoded.LoadImage(File.ReadAllBytes(levels[level])) == false)
                    {
                        warnings.Add($"'{levels[level]}' is not a readable PNG; " +
                                     (level == 0 ? "its map was left out." : "the texture stops at the level before it."));
                        break;
                    }
                    if (level == 0)
                    {
                        width = decoded.width;
                        height = decoded.height;
                    }
                    else if (decoded.width != Mathf.Max(1, width >> level) || decoded.height != Mathf.Max(1, height >> level))
                    {
                        warnings.Add($"'{levels[level]}' is {decoded.width}x{decoded.height}, not half the level above; " +
                                     "the texture stops at the level before it.");
                        break;
                    }
                    var levelPixels = decoded.GetPixels32();
                    ToUnityPacking(kind, levelPixels);
                    pixels.Add(levelPixels);
                }
            }
            finally
            {
                Object.DestroyImmediate(decoded);
            }
            if (pixels.Count == 0)
                return null;

            // Colour and emissive are sRGB; the other maps hold data.
            bool linear = kind != MapKind.Colour && kind != MapKind.Emissive;
            var built = new Texture2D(width, height, TextureFormat.RGBA32, pixels.Count, linear)
            {
                name = Path.GetFileNameWithoutExtension(level0),
                // Charts are padded inside the atlas; repeating would bleed the opposite edge in.
                wrapMode = TextureWrapMode.Clamp
            };
            for (int level = 0; level < pixels.Count; level++)
                built.SetPixels32(pixels[level], level);
            built.Apply(false, false);
            EditorUtility.CompressTexture(built, FormatOf(kind), TextureCompressionQuality.Normal);
            return Save(built, existing, path, stamp);
        }

        // Unity's Lit shaders read occlusion from G and metallic (R) with smoothness (A); Polysmith writes occlusion in R
        // and glTF metal-rough (roughness in G, metallic in B).
        static void ToUnityPacking(MapKind kind, Color32[] pixels)
        {
            switch (kind)
            {
                case MapKind.Occlusion:
                    for (int k = 0; k < pixels.Length; k++)
                        pixels[k] = new Color32(pixels[k].r, pixels[k].r, pixels[k].r, 255);
                    break;
                case MapKind.MetalRough:
                    for (int k = 0; k < pixels.Length; k++)
                        pixels[k] = new Color32(pixels[k].b, 0, 0, (byte)(255 - pixels[k].g));
                    break;
            }
        }

        static TextureFormat FormatOf(MapKind kind)
        {
            switch (kind)
            {
                case MapKind.Normal:
                    return TextureFormat.BC5;
                case MapKind.Occlusion:
                case MapKind.Emissive:
                    return TextureFormat.DXT1;
                default:
                    return TextureFormat.BC7;
            }
        }

        static Texture2D Save(Texture2D built, Texture2D existing, string path, string stamp)
        {
            var texture = built;
            if (existing == null)
                AssetDatabase.CreateAsset(built, path);
            else
            {
                EditorUtility.CopySerialized(built, existing);
                Object.DestroyImmediate(built);
                texture = existing;
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);
            }
            var importer = AssetImporter.GetAtPath(path);
            importer.userData = stamp;
            AssetDatabase.WriteImportSettingsIfDirty(path);
            AssetDatabase.SetLabels(texture, new[] { PolysmithRoundTrip.ReducedLabel });
            return texture;
        }

        // Level 0 and its consecutive "_mip<l>" files.
        static List<string> Levels(string level0)
        {
            var levels = new List<string> { level0 };
            string stem = Path.Combine(Path.GetDirectoryName(level0), Path.GetFileNameWithoutExtension(level0));
            for (int level = 1; File.Exists($"{stem}_mip{level}.png"); level++)
                levels.Add($"{stem}_mip{level}.png");
            return levels;
        }

        static string Stamp(List<string> levels, MapKind kind)
        {
            var stamp = new StringBuilder(kind.ToString());
            foreach (string level in levels)
                stamp.Append('|').Append(Path.GetFileName(level)).Append('@')
                    .Append(File.GetLastWriteTimeUtc(level).Ticks.ToString(CultureInfo.InvariantCulture));
            return stamp.ToString();
        }

        readonly string _folder;
        readonly Dictionary<string, Texture2D> _built = new Dictionary<string, Texture2D>();
    }
}
