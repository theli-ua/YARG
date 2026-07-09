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
    /// Material-category batch arrays for a single (theme, noteType, isStarPower) combination.
    /// </summary>
    internal struct ThemeRenderData
    {
        public RenderGroup[] Colored;
        public RenderGroup[] NoStarPower;
        public RenderGroup[] Metal;
        /// <summary>Non-colored material slots on same MeshRenderers (shells/tops).</summary>
        public RenderGroup[] Static;
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
            var staticGroups = ExtractStaticGroups(themeNote);

            if (DebugLogging)
            {
                Debug.Log(
                    $"[ThemeMeshCache] Extracted: theme='{themeName}', noteType={noteType}, " +
                    $"sp={themeNote.StarPowerVariant}, colored={coloredGroups.Length}, " +
                    $"noSP={noStarPowerGroups.Length}, metal={metalGroups.Length}, " +
                    $"static={staticGroups.Length}");
            }

            s_cache[(themeName, noteType, themeNote.StarPowerVariant)] = new ThemeRenderData
            {
                Colored = coloredGroups,
                NoStarPower = noStarPowerGroups,
                Metal = metalGroups,
                Static = staticGroups
            };
        }

        private static RenderGroup[] ExtractGroupsFromEntries(
            IEnumerable<MeshEmissionMaterialIndex> entries,
            Transform rootTransform)
        {
            var groups = new List<RenderGroup>();

            if (entries == null)
                return groups.ToArray();

            foreach (var entry in entries)
            {
                if (TryBuildGroup(entry.Mesh, entry.MaterialIndex, rootTransform,
                        entry.EmissionAddition, entry.EmissionMultiplier, out var group))
                {
                    groups.Add(group);
                }
            }

            return groups.ToArray();
        }

        /// <summary>
        /// Other material slots on MeshRenderers referenced by colored lists.
        /// GO path draws full MeshRenderer; BRG previously only drew listed MaterialIndex.
        /// Circular notes put shell/top on slots 0-2 and colored body on slot 3.
        /// </summary>
        private static RenderGroup[] ExtractStaticGroups(ThemeNote themeNote)
        {
            var claimed = new HashSet<(int rendererId, int materialIndex)>();
            var renderers = new HashSet<MeshRenderer>();

            void Collect(IEnumerable<MeshEmissionMaterialIndex> entries)
            {
                if (entries == null) return;
                foreach (var e in entries)
                {
                    if (e.Mesh == null) continue;
                    renderers.Add(e.Mesh);
                    claimed.Add((e.Mesh.GetInstanceID(), e.MaterialIndex));
                }
            }

            Collect(themeNote.ColoredMaterials);
            Collect(themeNote.ColoredMaterialsNoStarPower);
            Collect(themeNote.ColoredMetalMaterials);

            var root = themeNote.transform;
            var staticGroups = new List<RenderGroup>();

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;

                int rid = renderer.GetInstanceID();
                for (int mi = 0; mi < materials.Length; mi++)
                {
                    if (claimed.Contains((rid, mi))) continue;
                    if (materials[mi] == null) continue;

                    if (TryBuildGroup(renderer, mi, root, 0f, 1f, out var group))
                        staticGroups.Add(group);
                }
            }

            return staticGroups.ToArray();
        }

        private static bool TryBuildGroup(
            MeshRenderer renderer,
            int materialIndex,
            Transform rootTransform,
            float emissionAddition,
            float emissionMultiplier,
            out RenderGroup group)
        {
            group = default;
            if (renderer == null) return false;

            var meshFilter = renderer.GetComponent<MeshFilter>();
            var sharedMesh = meshFilter?.sharedMesh;
            if (sharedMesh == null) return false;

            var materials = renderer.sharedMaterials;
            if (materialIndex < 0 || materialIndex >= materials.Length) return false;

            var material = materials[materialIndex];
            if (material == null) return false;

            // MaterialIndex is a material slot, not always a submesh index.
            int submeshIndex = 0;
            if (sharedMesh.subMeshCount > 1)
            {
                submeshIndex = materialIndex < sharedMesh.subMeshCount
                    ? materialIndex
                    : 0;
            }

            group = new RenderGroup
            {
                Mesh = sharedMesh,
                SubmeshIndex = submeshIndex,
                Material = material,
                MeshLocalOffset = rootTransform.worldToLocalMatrix * renderer.localToWorldMatrix,
                SourceRendererID = renderer.GetInstanceID(),
                EmissionAddition = emissionAddition,
                EmissionMultiplier = emissionMultiplier,
            };
            return true;
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
