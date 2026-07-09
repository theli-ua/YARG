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
            // Fallback: some player/inactive paths return empty; also scan by type on root only.
            if (lines == null || lines.Length == 0)
                lines = themePrefab.GetComponentsInChildren<SustainLine>(includeInactive: true);

            foreach (var line in lines)
            {
                if (line == null) continue;

                var mat = line.SharedMaterial;
                if (mat == null)
                {
                    var mr = line.GetComponent<MeshRenderer>();
                    mat = mr != null ? mr.sharedMaterial : null;
                }

                if (mat == null)
                    continue;

                // Prefer GameObject name — material assets are often named WildcardSustain even on Normal lines.
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

            if (!s_cache.ContainsKey((themeName, SustainKind.Normal)))
            {
                // Last resort: project default sustain mats (note prefab search returned nothing).
                TryRegisterFallback(themeName, SustainKind.Normal, "Assets/Art/Materials/Gameplay/Notes/Sustain.mat", 0.8f);
                TryRegisterFallback(themeName, SustainKind.Open, "Assets/Art/Materials/Gameplay/Notes/OpenSustain.mat", 2f);
                TryRegisterFallback(themeName, SustainKind.Wildcard, "Assets/Art/Materials/Gameplay/Notes/WildcardSustain.mat", 2f);
            }

            if (!s_cache.ContainsKey((themeName, SustainKind.Normal)))
            {
                Debug.LogWarning(
                    $"[SustainMaterialCache] No sustain materials found for theme '{themeName}' " +
                    $"(SustainLine count={lines?.Length ?? 0})");
            }
            else if (lines == null || lines.Length == 0)
            {
                Debug.LogWarning(
                    $"[SustainMaterialCache] Theme '{themeName}': used default sustain materials " +
                    "(no SustainLine on extract host)");
            }
        }

        private static void TryRegisterFallback(string themeName, SustainKind kind, string resourceHint, float width)
        {
            // Runtime builds cannot load by Assets/ path — use Resources or already-loaded mats.
            // Prefer Resources.Load by leaf name under Resources (may be null).
            string leaf = System.IO.Path.GetFileNameWithoutExtension(resourceHint);
            var mat = Resources.Load<Material>(leaf);
            if (mat == null)
            {
                // Scan loaded materials by name (editor + player if mat already referenced).
                var all = Resources.FindObjectsOfTypeAll<Material>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].name == leaf)
                    {
                        mat = all[i];
                        break;
                    }
                }
            }

            if (mat == null)
                return;

            s_cache[(themeName, kind)] = new Entry { Material = mat, Width = width };
        }

        private static SustainKind Classify(string goName, string matName)
        {
            // GO name first so "Normal Sustain Line" + WildcardSustain.mat → Normal, not Wildcard.
            string go = (goName ?? string.Empty).ToLowerInvariant();
            if (go.Contains("open"))
                return SustainKind.Open;
            if (go.Contains("wild"))
                return SustainKind.Wildcard;
            if (go.Contains("normal"))
                return SustainKind.Normal;

            string mat = (matName ?? string.Empty).ToLowerInvariant();
            if (mat.Contains("open"))
                return SustainKind.Open;
            if (mat.Contains("wild"))
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
