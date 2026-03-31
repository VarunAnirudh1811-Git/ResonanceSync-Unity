using System;
using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    // =========================================================================
    // VisemeMapping
    // =========================================================================

    /// <summary>
    /// Maps one logical viseme name to a blendshape on a SkinnedMeshRenderer.
    /// </summary>
    [Serializable]
    public class VisemeMapping
    {
        [Tooltip("Logical viseme name. Must match names used in LipSyncData.")]
        public string visemeName;

        [Tooltip("Zero-based blendshape index on the SkinnedMeshRenderer. -1 = disabled.")]
        public int blendshapeIndex = -1;

        [Tooltip("Per-viseme weight scale. 1.0 = no change.")]
        [Range(0f, 2f)]
        public float weightScale = 1f;
    }

    // =========================================================================
    // VisemeProfile
    // =========================================================================

    /// <summary>
    /// Character-specific asset that maps logical viseme names to blendshape
    /// indices on a SkinnedMeshRenderer.
    ///
    /// This is the ONLY character-specific piece of the system.
    /// LipSyncData stores viseme names. VisemeProfile resolves them to indices.
    ///
    /// DEVELOPER USAGE:
    ///   1. Attach to a character GameObject
    ///   2. Assign SkinnedMeshRenderer and VisemeProfile in the Inspector
    ///   3. Call Play(lipSyncData, audioClip) to begin
    ///
    /// The component manages the AudioSource internally.
    /// Do not drive the AudioSource directly while this player is active.
    /// </summary>
    [CreateAssetMenu(
        menuName = "GlyphLabs/ResonanceSync/Viseme Profile",
        fileName = "NewVisemeProfile",
        order = 1)]
    public class VisemeProfile : ScriptableObject
    {
        // ------------------------------------------------------------------
        // Profile data
        // ------------------------------------------------------------------

        [Tooltip("Name of the closed/neutral mouth viseme. Used for silence frames.")]
        public string restVisemeName = "REST";

        [Tooltip("One entry per mouth shape. Order does not matter.")]
        public List<VisemeMapping> mappings = new ();

        // ------------------------------------------------------------------
        // Cache
        // ------------------------------------------------------------------

        private Dictionary<string, VisemeMapping> _cache;

        // ------------------------------------------------------------------
        // Cache management
        // ------------------------------------------------------------------

        public void BuildCache()
        {
            _cache = new Dictionary<string, VisemeMapping>(
                mappings.Count,
                StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.visemeName))
                {
                    Debug.LogWarning(
                        $"[ResonanceSync] VisemeProfile '{name}': " +
                        "a mapping has an empty viseme name and will be skipped.", this);
                    continue;
                }

                if (_cache.ContainsKey(mapping.visemeName))
                {
                    // FIX: Promote duplicate warning to include actionable guidance.
                    // Previously warned but gave no hint about what to do.
                    Debug.LogWarning(
                        $"[ResonanceSync] VisemeProfile '{name}': " +
                        $"duplicate viseme name '{mapping.visemeName}'. " +
                        "Only the first mapping will be used. " +
                        "Remove or rename the duplicate in the profile.", this);
                    continue;
                }

                _cache[mapping.visemeName] = mapping;
            }
        }

        public void InvalidateCache() => _cache = null;

        private void OnEnable() => _cache = null;

        // ------------------------------------------------------------------
        // Lookup API
        // ------------------------------------------------------------------

        public bool TryGetMapping(string visemeName, out VisemeMapping mapping)
        {
            if (_cache == null) BuildCache();
            return _cache.TryGetValue(visemeName, out mapping);
        }

        public int GetBlendshapeIndex(string visemeName)
        {
            return TryGetMapping(visemeName, out var m) ? m.blendshapeIndex : -1;
        }

        /// <summary>
        /// Returns true if the profile has a non-null, non-empty restVisemeName
        /// that resolves to a valid (index >= 0) mapping.
        ///
        /// FIX: Previously only checked TryGetMapping, which would succeed
        /// even if restVisemeName was null — passing null downstream to
        /// GetSimilarity in SignalStrategy causing ArgumentNullException.
        /// Now validates restVisemeName is non-null/empty first.
        /// </summary>
        public bool HasRestViseme()
        {
            if (string.IsNullOrWhiteSpace(restVisemeName))
                return false;

            return TryGetMapping(restVisemeName, out var m) && m.blendshapeIndex >= 0;
        }

        /// <summary>
        /// Returns all blendshape indices with valid (>= 0) mappings.
        /// FIX: Deduplicated — matches the deduplication in the player's
        /// BuildActiveIndexList to ensure consistent behaviour.
        /// </summary>
        public List<int> GetAllActiveIndices()
        {
            if (_cache == null) BuildCache();

            var indices = new List<int>(mappings.Count);
            var seen = new HashSet<int>();

            foreach (var mapping in mappings)
            {
                if (mapping.blendshapeIndex >= 0 && seen.Add(mapping.blendshapeIndex))
                    indices.Add(mapping.blendshapeIndex);
            }

            return indices;
        }
    }
}