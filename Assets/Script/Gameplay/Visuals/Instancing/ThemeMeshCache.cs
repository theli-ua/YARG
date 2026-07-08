using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Themes;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Describes a single render group (mesh + material + local offset + emission).
    /// One RenderGroup per mesh/material entry within a material category.
    /// </summary>
    internal struct RenderGroup
    {
        public Mesh Mesh;
        public int SubmeshIndex;
        public Material Material;
        public Matrix4x4 MeshLocalOffset;
        public int SourceRendererID;
        /// <summary>From <see cref="MeshEmissionMaterialIndex.EmissionAddition"/>.</summary>
        public float EmissionAddition;
        /// <summary>From <see cref="MeshEmissionMaterialIndex.EmissionMultiplier"/>.</summary>
        public float EmissionMultiplier;
    }

    /// <summary>
    /// Holds the three material-category batch arrays for a single (theme, noteType, isStarPower) combination.
    /// </summary>
    internal struct ThemeRenderData
    {
        public RenderGroup[] Colored;
        public RenderGroup[] NoStarPower;
        public RenderGroup[] Metal;
    }

    /// <summary>
    /// Caches mesh/material data extracted from theme prefabs.
    /// Keyed by (themeName, noteType, isStarPower).
    /// Batches are created lazily on first use via HighwayElementGraphicsSystem.
    /// </summary>
    internal static class ThemeMeshCache
    {
        private static readonly Dictionary<(string, ThemeNoteType, bool), ThemeRenderData> s_cache = new();
        private static readonly HashSet<string> s_extractedThemes = new();

        /// <summary>When true, extract/miss diagnostics are logged.</summary>
        internal static bool DebugLogging { get; set; }

        /// <summary>
        /// Extracts mesh/material data from a ThemeNote component.
        /// The caller is responsible for instantiating/destroying the GameObject.
        /// </summary>
        internal static void ExtractFromTheme(string themeName, ThemeNote themeNote)
        {
            if (themeNote == null)
            {
                if (DebugLogging)
                    Debug.Log("[ThemeMeshCache] ExtractFromTheme: themeNote is null");
                return;
            }

            var noteType = themeNote.NoteType;

            var coloredGroups = ExtractGroupsFromEntries(themeNote.ColoredMaterials, themeNote.transform);
            var noStarPowerGroups = ExtractGroupsFromEntries(themeNote.ColoredMaterialsNoStarPower, themeNote.transform);
            var metalGroups = ExtractGroupsFromEntries(themeNote.ColoredMetalMaterials, themeNote.transform);

            if (DebugLogging)
            {
                Debug.Log(
                    $"[ThemeMeshCache] Extracted: theme='{themeName}', noteType={noteType}, " +
                    $"sp={themeNote.StarPowerVariant}, colored={coloredGroups.Length}, " +
                    $"noSP={noStarPowerGroups.Length}, metal={metalGroups.Length}");
            }

            s_cache[(themeName, noteType, themeNote.StarPowerVariant)] = new ThemeRenderData
            {
                Colored = coloredGroups,
                NoStarPower = noStarPowerGroups,
                Metal = metalGroups
            };
        }

        private static RenderGroup[] ExtractGroupsFromEntries(
            IEnumerable<MeshEmissionMaterialIndex> entries,
            Transform rootTransform)
        {
            var groups = new List<RenderGroup>();

            foreach (var entry in entries)
            {
                var renderer = entry.Mesh;
                if (renderer == null) continue;

                var meshFilter = renderer.GetComponent<MeshFilter>();
                var sharedMesh = meshFilter?.sharedMesh;
                if (sharedMesh == null) continue;

                var materials = renderer.sharedMaterials;
                if (entry.MaterialIndex < 0 || entry.MaterialIndex >= materials.Length) continue;

                var material = materials[entry.MaterialIndex];
                if (material == null) continue;

                // MaterialIndex is a material slot, not always a submesh index.
                // Prefer matching submesh when present; single-submesh meshes use 0.
                int submeshIndex = 0;
                if (sharedMesh.subMeshCount > 1)
                {
                    submeshIndex = entry.MaterialIndex < sharedMesh.subMeshCount
                        ? entry.MaterialIndex
                        : 0;
                }

                var meshLocalOffset = rootTransform.worldToLocalMatrix * entry.Mesh.localToWorldMatrix;

                groups.Add(new RenderGroup
                {
                    Mesh = sharedMesh,
                    SubmeshIndex = submeshIndex,
                    Material = material,
                    MeshLocalOffset = meshLocalOffset,
                    SourceRendererID = renderer.GetInstanceID(),
                    EmissionAddition = entry.EmissionAddition,
                    EmissionMultiplier = entry.EmissionMultiplier,
                });
            }

            return groups.ToArray();
        }

        /// <summary>
        /// Gets render groups for a theme/type/SP state combination.
        /// Falls back to non-SP groups if SP variant is absent, and to Wildcard type if specific type absent.
        /// </summary>
        internal static ThemeRenderData GetRenderGroups(string themeName, ThemeNoteType noteType, bool isStarPowerVisible)
        {
            if (s_cache.TryGetValue((themeName, noteType, isStarPowerVisible), out var data))
                return data;

            bool oppositeSp = !isStarPowerVisible;
            if (s_cache.TryGetValue((themeName, noteType, oppositeSp), out data))
                return data;

            if (s_cache.TryGetValue((themeName, ThemeNoteType.Wildcard, isStarPowerVisible), out data))
                return data;

            if (s_cache.TryGetValue((themeName, ThemeNoteType.Wildcard, oppositeSp), out data))
                return data;

            if (DebugLogging)
            {
                Debug.LogWarning(
                    $"[ThemeMeshCache] MISS: theme='{themeName}', noteType={noteType}, " +
                    $"sp={isStarPowerVisible}. Cache has {s_cache.Count} entries for theme '{themeName}'.");
            }

            return default;
        }

        internal static void ExtractTheme(string themeName, Dictionary<ThemeNoteType, ThemeNote> models,
            Dictionary<ThemeNoteType, ThemeNote> starPowerModels)
        {
            foreach (var kvp in models)
                ExtractFromTheme(themeName, kvp.Value);

            foreach (var kvp in starPowerModels)
                ExtractFromTheme(themeName, kvp.Value);

            s_extractedThemes.Add(themeName);

            if (DebugLogging)
            {
                var themeKeys = s_cache.Keys.Where(k => k.Item1 == themeName).ToArray();
                Debug.Log($"[ThemeMeshCache] ExtractTheme: theme='{themeName}', entries={themeKeys.Length}");
            }
        }

        internal static void ClearTheme(string themeName)
        {
            var keysToRemove = new List<(string, ThemeNoteType, bool)>();
            foreach (var key in s_cache.Keys)
            {
                if (key.Item1 == themeName)
                    keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove)
                s_cache.Remove(key);

            s_extractedThemes.Remove(themeName);
        }

        internal static void ClearAll()
        {
            s_cache.Clear();
            s_extractedThemes.Clear();
        }
    }
}
