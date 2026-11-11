#if POLYSMITH_UNITY_GLTF
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Polysmith
{
    /// <summary>How the maps of one material are packed for the shader that samples them.</summary>
    enum MapPacking
    {
        /// <summary>Unity's metallic map: metallic (R), smoothness (A). URP Lit, Standard.</summary>
        MetallicSmoothness,
        /// <summary>Unity's occlusion map: occlusion in green. URP Lit, Standard.</summary>
        Occlusion,
        /// <summary>HDRP's mask map: metallic (R), occlusion (G), detail mask (B, unused), smoothness (A).</summary>
        HdrpMask
    }

    /// <summary>The atlas maps of a result material, persisted as texture assets in the form a shader asks for.</summary>
    interface IResultMaps
    {
        /// <summary>A GLB image as it is; a normal map is imported as one.</summary>
        Texture2D Map(Texture imported, bool normal);

        /// <summary>
        /// glTF metallic-roughness (metallic B, roughness G, factors applied) and occlusion (R) packed into one map.
        /// Either source can be null; null when both are.
        /// </summary>
        Texture2D Packed(MapPacking packing, Texture metallicRoughness, float metallic, float roughness, Texture occlusion);
    }

    /// <summary>
    /// A reduced material on the render pipeline's own Lit shader (URP Lit, HDRP Lit or Standard), from UnityGLTF's
    /// import of the result, whose properties have the glTF names. Only properties are set here: the shader's own
    /// editor (<see cref="ShaderGUI.ValidateMaterial"/>) then sets the keywords and render states they imply.
    /// </summary>
    static class PipelineMaterial
    {
        /// <summary>The pipeline's default material; null when its shader is not one of the three.</summary>
        public static Material Template(List<string> warnings)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            var template = pipeline != null ? pipeline.defaultMaterial : null;
            if (pipeline == null)
            {
                var standard = Shader.Find("Standard");
                return standard != null ? new Material(standard) : null;
            }
            if (template != null && FamilyOf(template.shader) != Family.Unknown)
                return new Material(template);
            warnings.Add($"The render pipeline's default material ({(template != null ? template.shader.name : "none")}) " +
                         "is not URP Lit, HDRP Lit or Standard: the reduced materials use UnityGLTF's shader.");
            return null;
        }

        public static Material Build(Material template, Material imported, IResultMaps maps)
        {
            var result = new Material(template);
            var read = new Imported(imported);
            switch (FamilyOf(template.shader))
            {
                case Family.Hdrp:
                    Hdrp(result, read, maps);
                    break;
                default:
                    Urp(result, read, maps);
                    break;
            }
            using (var editor = new Validator(result))
                editor.Validate();
            return result;
        }

        // URP Lit, and the built-in Standard shader (same map layout, older property names).
        static void Urp(Material m, Imported glb, IResultMaps maps)
        {
            var baseMap = glb.Texture("baseColorTexture");
            foreach (string property in new[] { "_BaseMap", "_MainTex" })
                SetTexture(m, property, baseMap != null ? maps.Map(baseMap, false) : null);
            foreach (string property in new[] { "_BaseColor", "_Color" })
                SetColor(m, property, glb.Colour("baseColorFactor", Color.white));

            var normal = glb.Texture("normalTexture");
            SetTexture(m, "_BumpMap", normal != null ? maps.Map(normal, true) : null);
            SetFloat(m, "_BumpScale", glb.Float("normalScale", 1));

            float metallic = glb.Float("metallicFactor", 1), roughness = glb.Float("roughnessFactor", 1);
            var packed = maps.Packed(MapPacking.MetallicSmoothness, glb.Texture("metallicRoughnessTexture"), metallic,
                roughness, null);
            SetFloat(m, "_WorkflowMode", 1);
            SetTexture(m, "_MetallicGlossMap", packed);
            // With the map the factors are baked into it, and these scale it.
            SetFloat(m, "_Metallic", packed != null ? 1 : metallic);
            SetFloat(m, "_Smoothness", packed != null ? 1 : 1 - roughness);
            SetFloat(m, "_Glossiness", packed != null ? 1 : 1 - roughness);
            SetFloat(m, "_GlossMapScale", 1);
            SetFloat(m, "_SmoothnessTextureChannel", 0);

            SetTexture(m, "_OcclusionMap", maps.Packed(MapPacking.Occlusion, null, 1, 1, glb.Texture("occlusionTexture")));
            SetFloat(m, "_OcclusionStrength", glb.Float("occlusionStrength", 1));

            var emission = Emission(glb, out var emissive);
            SetTexture(m, "_EmissionMap", emissive != null ? maps.Map(emissive, false) : null);
            SetColor(m, "_EmissionColor", emission);
            m.globalIlluminationFlags = emission.maxColorComponent > 0
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            // Built-in Standard has one blend mode property (_Mode) instead of these.
            SetFloat(m, "_Surface", glb.Transparent ? 1 : 0);
            SetFloat(m, "_AlphaClip", glb.Cutout ? 1 : 0);
            SetFloat(m, "_Cutoff", glb.Float("alphaCutoff", 0.5f));
            SetFloat(m, "_Mode", glb.Transparent ? 3 : glb.Cutout ? 1 : 0);
            SetFloat(m, "_Cull", glb.DoubleSided ? 0 : 2);
        }

        static void Hdrp(Material m, Imported glb, IResultMaps maps)
        {
            var baseMap = glb.Texture("baseColorTexture");
            SetTexture(m, "_BaseColorMap", baseMap != null ? maps.Map(baseMap, false) : null);
            SetColor(m, "_BaseColor", glb.Colour("baseColorFactor", Color.white));

            var normal = glb.Texture("normalTexture");
            SetTexture(m, "_NormalMap", normal != null ? maps.Map(normal, true) : null);
            SetFloat(m, "_NormalMapSpace", 0);
            SetFloat(m, "_NormalScale", glb.Float("normalScale", 1));

            float metallic = glb.Float("metallicFactor", 1), roughness = glb.Float("roughnessFactor", 1);
            var occlusion = glb.Texture("occlusionTexture");
            var mask = maps.Packed(MapPacking.HdrpMask, glb.Texture("metallicRoughnessTexture"), metallic, roughness,
                occlusion);
            SetTexture(m, "_MaskMap", mask);
            SetFloat(m, "_Metallic", mask != null ? 1 : metallic);
            SetFloat(m, "_Smoothness", 1 - roughness);
            SetFloat(m, "_MetallicRemapMin", 0);
            SetFloat(m, "_MetallicRemapMax", 1);
            SetFloat(m, "_SmoothnessRemapMin", 0);
            SetFloat(m, "_SmoothnessRemapMax", 1);
            // glTF occlusion strength s draws 1 - s + s * ao: the remap's lower end.
            SetFloat(m, "_AORemapMin", occlusion != null ? 1 - glb.Float("occlusionStrength", 1) : 1);
            SetFloat(m, "_AORemapMax", 1);

            var emission = Emission(glb, out var emissive);
            SetTexture(m, "_EmissiveColorMap", emissive != null ? maps.Map(emissive, false) : null);
            SetFloat(m, "_UseEmissiveIntensity", 0);
            SetColor(m, "_EmissiveColor", emission);
            SetColor(m, "_EmissiveColorLDR", emission);

            SetFloat(m, "_SurfaceType", glb.Transparent ? 1 : 0);
            SetFloat(m, "_AlphaCutoffEnable", glb.Cutout ? 1 : 0);
            SetFloat(m, "_AlphaCutoff", glb.Float("alphaCutoff", 0.5f));
            SetFloat(m, "_DoubleSidedEnable", glb.DoubleSided ? 1 : 0);
        }

        // UnityGLTF keeps the factor's alpha at 0; a colour property wants 1.
        static Color Emission(Imported glb, out Texture map)
        {
            map = glb.Texture("emissiveTexture");
            var factor = glb.Colour("emissiveFactor", Color.black);
            factor.a = 1;
            if (factor.maxColorComponent <= 0)
                map = null;
            return map != null || factor.maxColorComponent > 0 ? factor : Color.black;
        }

        enum Family
        {
            Unknown,
            Urp,
            Hdrp
        }

        // By the properties, not the shader name: Standard has URP's layout, and variants of the Lit shaders theirs.
        static Family FamilyOf(Shader shader)
        {
            bool Has(string name) => shader.FindPropertyIndex(name) >= 0;
            if (Has("_BaseColorMap") && Has("_MaskMap") && Has("_NormalMap"))
                return Family.Hdrp;
            if ((Has("_BaseMap") || Has("_MainTex")) && Has("_MetallicGlossMap") && Has("_BumpMap"))
                return Family.Urp;
            return Family.Unknown;
        }

        static void SetTexture(Material m, string property, Texture texture)
        {
            if (m.HasProperty(property))
                m.SetTexture(property, texture);
        }

        static void SetFloat(Material m, string property, float value)
        {
            if (m.HasProperty(property))
                m.SetFloat(property, value);
        }

        static void SetColor(Material m, string property, Color value)
        {
            if (m.HasProperty(property))
                m.SetColor(property, value);
        }

        /// <summary>UnityGLTF's material for a result primitive, read by glTF property names.</summary>
        readonly struct Imported
        {
            public Imported(Material material) => _material = material;

            public bool Transparent => Float("_Surface", 0) > 0;
            public bool Cutout => Transparent == false && Float("_AlphaClip", 0) > 0;
            public bool DoubleSided => Float("_Cull", 2) == 0;

            public Texture Texture(string property) => _material.HasProperty(property) ? _material.GetTexture(property) : null;
            public float Float(string property, float fallback) =>
                _material.HasProperty(property) ? _material.GetFloat(property) : fallback;
            public Color Colour(string property, Color fallback) =>
                _material.HasProperty(property) ? _material.GetColor(property) : fallback;

            readonly Material _material;
        }

        // The shader's own editor sets what the material inspector would: keywords, render queue, blend states.
        sealed class Validator : System.IDisposable
        {
            public Validator(Material material)
            {
                _material = material;
                _editor = (MaterialEditor)Editor.CreateEditor(material);
            }

            public void Validate() => _editor.customShaderGUI?.ValidateMaterial(_material);

            public void Dispose() => Object.DestroyImmediate(_editor);

            readonly Material _material;
            readonly MaterialEditor _editor;
        }
    }
}
#endif
