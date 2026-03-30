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
    ///
    /// The viseme name is the shared identity between LipSyncData and the rig.
    /// LipSyncData references visemes by name. This mapping resolves that name
    /// to the actual blendshape index on a specific character's mesh.
    ///
    /// Because different characters have different meshes with different
    /// blendshape orderings, each character needs its own VisemeProfile.
    /// The LipSyncData asset itself never changes — only the profile does.
    /// </summary>
    [Serializable]
    public class VisemeMapping
    {
        /// <summary>
        /// The logical viseme name. Must match names used in LipSyncData exactly.
        /// Comparison is case-insensitive at runtime (handled by the cache).
        /// Examples: "REST", "MBP", "AA", "EE", "OH", "OOH", "FF", "TH", "DD"
        /// </summary>
        [Tooltip("Logical viseme name. Must match names used in LipSyncData.")]
        public string visemeName;

        /// <summary>
        /// Zero-based blendshape index on the target SkinnedMeshRenderer.
        /// Find this by inspecting the mesh's BlendShapes list in the
        /// SkinnedMeshRenderer component, or via mesh.GetBlendShapeIndex(name).
        /// Set to -1 to disable this mapping without removing the entry.
        /// </summary>
        [Tooltip("Blendshape index on the SkinnedMeshRenderer. -1 = disabled.")]
        public int blendshapeIndex = -1;

        /// <summary>
        /// Per-viseme weight scale applied on top of LipSyncSettings.intensityMultiplier.
        /// Use to calibrate individual shapes when a rig's blendshapes are
        /// over- or under-scaled relative to each other.
        /// 1.0 = no change. 0.5 = half intensity. 2.0 = double intensity.
        /// </summary>
        [Tooltip("Per-shape weight scale. 1.0 = no change.")]
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
    /// LipSyncData is character-independent. VisemeProfile is what makes
    /// a LipSyncData asset work on a specific character.
    ///
    /// One profile can be shared across multiple clips for the same character.
    /// Different characters each need their own profile, even if they share
    /// the same viseme names — because blendshape indices differ per mesh.
    ///
    /// SETUP:
    ///   1. Create via Assets > GlyphLabs > ResonanceSync > Viseme Profile
    ///   2. Add one VisemeMapping entry per mouth shape on the character
    ///   3. Set the blendshape index for each (check the SMR in the Inspector)
    ///   4. Set restVisemeName to match your "closed mouth" entry
    ///   5. Assign this profile in the editor window and on ResonanceSyncPlayer
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

        /// <summary>
        /// The name of the viseme that represents a closed, neutral mouth.
        /// The processor writes this viseme during silent portions of audio.
        /// Must exactly match one of the visemeNames in the mappings list.
        /// Default is "REST" but can be any name your rig uses.
        /// </summary>
        [Tooltip("Name of the closed/neutral mouth viseme. Used for silence frames.")]
        public string restVisemeName = "REST";

        /// <summary>
        /// All viseme mappings for this character.
        /// Order does not matter — lookups are done by name via the cache.
        /// </summary>
        [Tooltip("One entry per mouth shape. Order does not matter.")]
        public List<VisemeMapping> mappings = new ();

        // ------------------------------------------------------------------
        // Runtime lookup cache
        // Built lazily from the mappings list for O(1) name lookups.
        // Not serialized — rebuilt automatically after any reload.
        // ------------------------------------------------------------------
        private Dictionary<string, VisemeMapping> _cache;

        // ------------------------------------------------------------------
        // Cache management
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the internal name → mapping dictionary from the mappings list.
        /// Called automatically on first lookup. Can be called manually after
        /// modifying mappings at editor time without triggering a reload.
        /// </summary>
        public void BuildCache()
        {
            _cache = new Dictionary<string, VisemeMapping>(
                mappings.Count,
                StringComparer.OrdinalIgnoreCase // case-insensitive matching
            );

            foreach (var mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.visemeName))
                {
                    Debug.LogWarning(
                        $"[ResonanceSync] VisemeProfile '{name}' has a mapping with " +
                        "an empty viseme name. It will be ignored.", this);
                    continue;
                }

                if (_cache.ContainsKey(mapping.visemeName))
                {
                    Debug.LogWarning(
                        $"[ResonanceSync] VisemeProfile '{name}' has duplicate viseme " +
                        $"name '{mapping.visemeName}'. Only the first entry will be used.", this);
                    continue;
                }

                _cache[mapping.visemeName] = mapping;
            }
        }

        /// <summary>
        /// Clears the cache so it is rebuilt on the next lookup.
        /// Call this in the editor after modifying mappings programmatically.
        /// </summary>
        public void InvalidateCache() => _cache = null;

        // Unity calls this after deserialization and after domain reloads.
        // Invalidating here ensures we never serve stale cached data.
        private void OnEnable() => _cache = null;

        // ------------------------------------------------------------------
        // Lookup API — used by the player and processor
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the VisemeMapping for the given viseme name.
        /// Builds the cache on first call. Case-insensitive.
        /// </summary>
        /// <param name="visemeName">Logical viseme name to look up.</param>
        /// <param name="mapping">The mapping if found, otherwise null.</param>
        /// <returns>True if the name was found in this profile.</returns>
        public bool TryGetMapping(string visemeName, out VisemeMapping mapping)
        {
            if (_cache == null) BuildCache();
            return _cache.TryGetValue(visemeName, out mapping);
        }

        /// <summary>
        /// Returns the blendshape index for the given viseme name.
        /// Returns -1 if the viseme is not found or is disabled.
        /// </summary>
        public int GetBlendshapeIndex(string visemeName)
        {
            return TryGetMapping(visemeName, out var m) ? m.blendshapeIndex : -1;
        }

        /// <summary>
        /// Returns true if this profile contains a valid REST mapping.
        /// Called by the processor before generation to ensure silence
        /// frames can be written correctly.
        /// </summary>
        public bool HasRestViseme()
        {
            if (string.IsNullOrWhiteSpace(restVisemeName)) return false;
            return TryGetMapping(restVisemeName, out var m) && m.blendshapeIndex >= 0;
        }

        /// <summary>
        /// Returns all blendshape indices with valid (non -1) mappings.
        /// Used by the player to build its zeroing list on Play().
        /// </summary>
        public List<int> GetAllActiveIndices()
        {
            if (_cache == null) BuildCache();

            var indices = new List<int>(mappings.Count);
            foreach (var mapping in mappings)
            {
                if (mapping.blendshapeIndex >= 0)
                    indices.Add(mapping.blendshapeIndex);
            }
            return indices;
        }
    }
}