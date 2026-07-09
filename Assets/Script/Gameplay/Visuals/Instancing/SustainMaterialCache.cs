using System.Collections.Generic;
using UnityEngine;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Theme sustain materials + widths extracted from note prefab SustainLine components.
    /// </summary>
    internal static class SustainMaterialCache
    {
        private struct Entry
        {
            public Material Material;
            public float Width;
        }

        // themeName → kind → entry
        private static readonly Dictionary<(string, SustainKind), Entry> s_cache = new();

        internal static void ExtractFromPrefab(string themeName, GameObject themePrefab)
        {
            if (themePrefab == null || string.IsNullOrEmpty(themeName))
                return;

            var lines = themePrefab.GetComponentsInChildren<SustainLine>(true);
            foreach (var line in lines)
            {
                var mat = line.SharedMaterial;
                if (mat == null)
                {
                    var mr = line.GetComponent<MeshRenderer>();
                    mat = mr != null ? mr.sharedMaterial : null;
                }

                if (mat == null)
                    continue;

                var kind = Classify(line.gameObject.name, mat.name);
                s_cache[(themeName, kind)] = new Entry
                {
                    Material = mat,
                    Width = line.Width > 0f ? line.Width : 0.1f
                };
            }

            // Ensure Normal always exists if any sustain was found
            if (!s_cache.ContainsKey((themeName, SustainKind.Normal)))
            {
                foreach (var kv in s_cache)
                {
                    if (kv.Key.Item1 == themeName)
                    {
                        s_cache[(themeName, SustainKind.Normal)] = kv.Value;
                        break;
                    }
                }
            }
        }

        private static SustainKind Classify(string goName, string matName)
        {
            string n = (goName + " " + matName).ToLowerInvariant();
            if (n.Contains("open"))
                return SustainKind.Open;
            if (n.Contains("wild"))
                return SustainKind.Wildcard;
            return SustainKind.Normal;
        }

        internal static bool TryGet(string themeName, SustainKind kind, out Material material, out float width)
        {
            if (s_cache.TryGetValue((themeName, kind), out var e) ||
                s_cache.TryGetValue((themeName, SustainKind.Normal), out e))
            {
                material = e.Material;
                width = e.Width;
                return material != null;
            }

            material = null;
            width = 0.1f;
            return false;
        }

        internal static void ClearTheme(string themeName)
        {
            var keys = new List<(string, SustainKind)>();
            foreach (var k in s_cache.Keys)
            {
                if (k.Item1 == themeName)
                    keys.Add(k);
            }

            foreach (var k in keys)
                s_cache.Remove(k);
        }

        internal static void ClearAll() => s_cache.Clear();
    }
}
