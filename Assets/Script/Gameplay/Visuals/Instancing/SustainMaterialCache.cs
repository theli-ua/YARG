using System.Collections.Generic;
using UnityEngine;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Theme sustain materials + widths from note prefab SustainLine components.
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

        /// <summary>Register one SustainLine's material/width for a theme+kind.</summary>
        internal static void RegisterLine(string themeName, SustainKind kind, SustainLine line)
        {
            if (string.IsNullOrEmpty(themeName) || line == null)
                return;

            var mat = line.SharedMaterial;
            if (mat == null)
            {
                var mr = line.GetComponent<MeshRenderer>();
                mat = mr != null ? mr.sharedMaterial : null;
            }

            if (mat == null)
                return;

            float w = line.Width > 0f ? line.Width : 0.1f;
            s_cache[(themeName, kind)] = new Entry { Material = mat, Width = w };
        }

        internal static void ExtractFromPrefab(string themeName, GameObject themePrefab)
        {
            if (themePrefab == null || string.IsNullOrEmpty(themeName))
                return;

            // ThemeManager caches themed pool prefabs. GetComponentsInChildren of SustainLine
            // has returned 0 on those clones; serialized refs on note elements are reliable.
            int fromElements = 0;
            foreach (var g in themePrefab.GetComponentsInChildren<FiveFretGuitarNoteElement>(true))
            {
                g.RegisterSustainMaterials(themeName);
                fromElements++;
            }

            foreach (var k in themePrefab.GetComponentsInChildren<FiveLaneKeysNoteElement>(true))
            {
                k.RegisterSustainMaterials(themeName);
                fromElements++;
            }

            foreach (var p in themePrefab.GetComponentsInChildren<ProKeysNoteElement>(true))
            {
                p.RegisterSustainMaterials(themeName);
                fromElements++;
            }

            // Secondary: direct SustainLine scan (works if hierarchy search is healthy).
            var lines = themePrefab.GetComponentsInChildren<SustainLine>(true);
            int lineCount = lines != null ? lines.Length : 0;
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    if (line == null) continue;
                    var matName = line.SharedMaterial != null ? line.SharedMaterial.name : string.Empty;
                    RegisterLine(themeName, Classify(line.gameObject.name, matName), line);
                }
            }

            // Ensure Normal always exists if any kind was found
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
                // Drums/etc. have no SustainLine — expected, not an error.
                if (fromElements > 0 || lineCount > 0)
                {
                    Debug.LogWarning(
                        $"[SustainMaterialCache] Theme '{themeName}': note elements found but no usable sustain mats " +
                        $"(noteElements={fromElements}, SustainLine count={lineCount})");
                }
            }
            else
            {
                Debug.Log(
                    $"[SustainMaterialCache] Theme '{themeName}': sustain mats ready " +
                    $"(noteElements={fromElements}, SustainLine count={lineCount}, " +
                    $"normalWidth={s_cache[(themeName, SustainKind.Normal)].Width})");
            }
        }

        private static SustainKind Classify(string goName, string matName)
        {
            // GO name first: "Normal Sustain Line" + WildcardSustain.mat → Normal.
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
