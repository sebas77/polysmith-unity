using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Polysmith
{
    /// <summary>
    /// The texture maps an atlas export can give a material (see the MTL keys in <see cref="MtlFile"/>). A billboard
    /// group's <c>map_Kd</c> is read as <see cref="Colour"/> and imported as <see cref="Billboard"/>: colour with the
    /// cut-out alpha.
    /// </summary>
    enum MapKind
    {
        Colour,
        Normal,
        Occlusion,
        MetalRough,
        Emissive,
        Billboard
    }

    /// <summary>One MTL material: its flat colour and the level-0 file of each map it has.</summary>
    sealed class MtlMaterial
    {
        public Color Diffuse = Color.white;
        public readonly Dictionary<MapKind, string> Maps = new Dictionary<MapKind, string>();
    }

    /// <summary>
    /// Reads the MTL a Polysmith result names with <c>mtllib</c>. Keys: <c>map_Kd</c> colour (its alpha is the opacity,
    /// so <c>map_d</c> adds nothing), <c>norm</c> normal, <c>map_ao</c> occlusion, <c>map_Pm</c>/<c>map_Pr</c> metal-rough
    /// (one packed file), <c>map_Ke</c> emissive.
    /// </summary>
    static class MtlFile
    {
        /// <summary>The materials by name; empty when the OBJ names no MTL or the MTL is missing.</summary>
        public static Dictionary<string, MtlMaterial> Read(string objPath)
        {
            var materials = new Dictionary<string, MtlMaterial>();
            string mtl = MtlPath(objPath);
            if (mtl == null || File.Exists(mtl) == false)
                return materials;

            string folder = Path.GetDirectoryName(mtl);
            MtlMaterial current = null;
            foreach (string line in File.ReadLines(mtl))
            {
                string[] tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2)
                    continue;
                switch (tokens[0])
                {
                    case "newmtl":
                        current = new MtlMaterial();
                        materials[tokens[1]] = current;
                        break;
                    case "Kd":
                        current.Diffuse = new Color(Number(tokens[1]), Number(tokens[2]), Number(tokens[3]));
                        break;
                    case "map_Kd":
                        current.Maps[MapKind.Colour] = Path.Combine(folder, tokens[1]);
                        break;
                    case "norm":
                        current.Maps[MapKind.Normal] = Path.Combine(folder, tokens[1]);
                        break;
                    case "map_ao":
                        current.Maps[MapKind.Occlusion] = Path.Combine(folder, tokens[1]);
                        break;
                    case "map_Pm":
                    case "map_Pr":
                        current.Maps[MapKind.MetalRough] = Path.Combine(folder, tokens[1]);
                        break;
                    case "map_Ke":
                        current.Maps[MapKind.Emissive] = Path.Combine(folder, tokens[1]);
                        break;
                }
            }
            return materials;
        }

        // The mtllib line comes before the first vertex.
        static string MtlPath(string objPath)
        {
            foreach (string line in File.ReadLines(objPath))
            {
                if (line.StartsWith("mtllib ", StringComparison.Ordinal))
                    return Path.Combine(Path.GetDirectoryName(objPath), line.Substring(7).Trim());
                if (line.StartsWith("v ", StringComparison.Ordinal))
                    return null;
            }
            return null;
        }

        static float Number(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
