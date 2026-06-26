using System.Collections.Generic;
using UnityEngine;
using YARG.Themes;

namespace YARG.Gameplay.Visuals.Instancing
{
    /// <summary>
    /// Describes a single render group (mesh + material + local offset).
    /// One RenderGroup per mesh/material entry within a material category.
    /// </summary>
    internal struct RenderGroup
    {
        public Mesh Mesh;
        public int SubmeshIndex;
        public Material Material;
        public Matrix4x4 MeshLocalOffset;
        public int SourceRendererID;
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
        // Cache: (themeName, noteType, isStarPower) → ThemeRenderData
        private static readonly Dictionary<(string, ThemeNoteType, bool), ThemeRenderData> s_cache = new();

        // Track which themes have been extracted
        private static readonly HashSet<string> s_extractedThemes = new();

        /// <summary>
        /// Extracts mesh/material data from a theme model prefab.
        /// Call once per theme at load time. Destroys the instantiated prefab after extraction.
        /// </summary>
        internal static void ExtractFromTheme(string themeName, GameObject themeModel, ThemeNoteType noteType)
        {
            if (themeModel == null) return;

            // Instantiate to read components
            var instance = GameObject.Instantiate(themeModel);
            try
            {
                var themeNote = instance.GetComponent<ThemeNote>();
                if (themeNote == null) return;

                bool isStarPower = themeNote.StarPowerVariant;
                var rootTransform = instance.transform;

                // Extract from each material array independently
                var coloredGroups = ExtractGroupsFromEntries(themeNote.ColoredMaterials, rootTransform);
                var noStarPowerGroups = ExtractGroupsFromEntries(themeNote.ColoredMaterialsNoStarPower, rootTransform);
                var metalGroups = ExtractGroupsFromEntries(themeNote.ColoredMetalMaterials, rootTransform);

                // Store in cache
                s_cache[(themeName, noteType, isStarPower)] = new ThemeRenderData
                {
                    Colored = coloredGroups,
                    NoStarPower = noStarPowerGroups,
                    Metal = metalGroups
                };
            }
            finally
            {
                GameObject.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Extracts render groups from a collection of MeshEmissionMaterialIndex entries.
        /// </summary>
        private static RenderGroup[] ExtractGroupsFromEntries(
            IEnumerable<MeshEmissionMaterialIndex> entries,
            Transform rootTransform)
        {
            var groups = new List<RenderGroup>();

            foreach (var entry in entries)
            {
                var renderer = entry.Mesh;
                if (renderer == null) continue;

                // MeshRenderer doesn't have sharedMesh — get it from the MeshFilter on the same GameObject
                var meshFilter = renderer.GetComponent<MeshFilter>();
                var sharedMesh = meshFilter?.sharedMesh;
                if (sharedMesh == null) continue;

                var materials = renderer.sharedMaterials;
                if (entry.MaterialIndex >= materials.Length) continue;

                var material = materials[entry.MaterialIndex];
                if (material == null) continue;

                // Compute mesh-local offset: world → root → mesh
                var meshLocalOffset = rootTransform.worldToLocalMatrix * entry.Mesh.localToWorldMatrix;

                groups.Add(new RenderGroup
                {
                    Mesh = sharedMesh,
                    SubmeshIndex = 0,
                    Material = material,
                    MeshLocalOffset = meshLocalOffset,
                    SourceRendererID = renderer.GetInstanceID()
                });
            }

            return groups.ToArray();
        }

        /// <summary>
        /// Gets render groups for a theme/type/SP state combination.
        /// Falls back to non-SP groups if SP variant is absent.
        /// </summary>
        internal static ThemeRenderData GetRenderGroups(string themeName, ThemeNoteType noteType, bool isStarPowerVisible)
        {
            // Try exact match first
            if (s_cache.TryGetValue((themeName, noteType, isStarPowerVisible), out var data))
                return data;

            // Fall back to non-SP if we're looking for SP
            if (!isStarPowerVisible)
            {
                // Try wildcard as last resort
                if (s_cache.TryGetValue((themeName, ThemeNoteType.Wildcard, false), out var wildcardData))
                    return wildcardData;
                return default;
            }

            // Looking for SP: try non-SP fallback
            if (s_cache.TryGetValue((themeName, noteType, false), out data))
                return data;

            // Try wildcard SP
            if (s_cache.TryGetValue((themeName, ThemeNoteType.Wildcard, true), out data))
                return data;

            // Try wildcard non-SP
            if (s_cache.TryGetValue((themeName, ThemeNoteType.Wildcard, false), out data))
                return data;

            return default;
        }

        /// <summary>
        /// Extracts all note types from a theme's note prefabs.
        /// Call after theme prefabs are resolved, before first NoteTracker.Add().
        /// </summary>
        internal static void ExtractTheme(string themeName, Dictionary<ThemeNoteType, GameObject> models,
            Dictionary<ThemeNoteType, GameObject> starPowerModels)
        {
            // Extract normal models
            foreach (var kvp in models)
            {
                ExtractFromTheme(themeName, kvp.Value, kvp.Key);
            }

            // Extract SP models
            foreach (var kvp in starPowerModels)
            {
                ExtractFromTheme(themeName, kvp.Value, kvp.Key);
            }

            s_extractedThemes.Add(themeName);
        }

        /// <summary>
        /// Clears cache entries for a specific theme.
        /// Call when theme changes to reclaim GPU memory.
        /// </summary>
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

        /// <summary>
        /// Clears the entire cache.
        /// </summary>
        internal static void ClearAll()
        {
            s_cache.Clear();
            s_extractedThemes.Clear();
        }
    }
}
