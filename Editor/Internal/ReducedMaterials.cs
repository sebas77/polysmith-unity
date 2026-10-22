using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// The material of a reduced group: a copy of the group's material with the atlas maps Polysmith exported for it,
    /// saved as "&lt;folder&gt;/&lt;group&gt;.mat" and shared by every part that uses the group (Polysmith gives each
    /// group one atlas). Maps the export does not have are cleared, so a copy made from an earlier export never keeps
    /// a map on UVs it no longer matches. Property names are those of the URP and Built-in Lit/Standard shaders.
    /// </summary>
    sealed class ReducedMaterials
    {
        public ReducedMaterials(string folder, AtlasTextures textures)
        {
            _folder = folder;
            _textures = textures;
        }

        /// <summary>The reduced material for a group, made once per instance from <paramref name="source"/>.</summary>
        public Material Get(string group, Material source, MtlMaterial exported, List<string> warnings)
        {
            if (_made.TryGetValue(group, out var made))
                return made;

            var material = new Material(source) { name = group };
            SetColour(material, Map(exported, MapKind.Colour, warnings));
            SetMap(material, "_BumpMap", Map(exported, MapKind.Normal, warnings), "_NORMALMAP");
            SetMap(material, "_OcclusionMap", Map(exported, MapKind.Occlusion, warnings), "_OCCLUSIONMAP");
            var metalRough = Map(exported, MapKind.MetalRough, warnings);
            SetMap(material, "_MetallicGlossMap", metalRough, "_METALLICSPECGLOSSMAP");
            SetMap(material, "_MetallicGlossMap", metalRough, "_METALLICGLOSSMAP");
            // With a metal-rough map the smoothness comes from its alpha, scaled by these.
            if (metalRough != null)
            {
                SetFloat(material, "_Smoothness", 1);
                SetFloat(material, "_GlossMapScale", 1);
            }
            var emissive = Map(exported, MapKind.Emissive, warnings);
            SetMap(material, "_EmissionMap", emissive, "_EMISSION");
            if (emissive != null)
            {
                material.SetColor("_EmissionColor", Color.white);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            made = Save(material, $"{_folder}/{group}.mat");
            _made[group] = made;
            return made;
        }

        Texture2D Map(MtlMaterial exported, MapKind kind, List<string> warnings) =>
            exported.Maps.TryGetValue(kind, out string level0) ? _textures.Get(level0, kind, warnings) : null;

        // A colour atlas already holds the colour, so the tint goes to white.
        static void SetColour(Material material, Texture2D colour)
        {
            foreach (string property in new[] { "_BaseMap", "_MainTex" })
                if (material.HasProperty(property))
                    material.SetTexture(property, colour);
            if (colour == null)
                return;
            foreach (string property in new[] { "_BaseColor", "_Color" })
                if (material.HasProperty(property))
                    material.SetColor(property, Color.white);
        }

        static void SetMap(Material material, string property, Texture2D map, string keyword)
        {
            if (material.HasProperty(property) == false)
                return;
            material.SetTexture(property, map);
            if (map != null)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }

        static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        // An existing asset is overwritten in place, so renderers keep pointing at it.
        static Material Save(Material material, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(material, path);
                existing = material;
            }
            else
            {
                EditorUtility.CopySerialized(material, existing);
                Object.DestroyImmediate(material);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);
            }
            AssetDatabase.SetLabels(existing, new[] { PolysmithRoundTrip.ReducedLabel });
            return existing;
        }

        readonly string _folder;
        readonly AtlasTextures _textures;
        readonly Dictionary<string, Material> _made = new Dictionary<string, Material>();
    }
}
