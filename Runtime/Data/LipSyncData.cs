using System;
using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    // =========================================================================
    // VisemeWeight
    // =========================================================================

    /// <summary>
    /// A single viseme contribution at a point in time.
    ///
    /// Stores a viseme NAME not a blendshape index.
    /// This keeps LipSyncData character-independent — the same asset can
    /// drive any character that has a compatible VisemeProfile.
    ///
    /// VisemeProfile resolves names → blendshape indices at runtime.
    /// The conversion to Unity's 0–100 blendshape range also happens there,
    /// not here. This class works in normalised 0–1 space throughout.
    /// </summary>
    [Serializable]
    public class VisemeWeight
    {
        /// <summary>
        /// Logical viseme name as defined in the VisemeProfile.
        /// Examples: "REST", "MBP", "AA", "EE", "OH", "OOH", "FF", "TH", "DD"
        /// Must exactly match a name in the paired VisemeProfile.
        /// </summary>
        [Tooltip("Logical viseme name. Must match an entry in the VisemeProfile.")]
        public string visemeName;

        /// <summary>
        /// Normalised weight in the range [0, 1].
        /// 0 = shape fully inactive. 1 = shape fully active.
        /// Conversion to Unity's 0–100 blendshape range happens in the player.
        /// </summary>
        [Tooltip("Normalised weight [0, 1]. Converted to 0–100 when applied.")]
        [Range(0f, 1f)]
        public float weight;

        public VisemeWeight(string visemeName, float weight)
        {
            this.visemeName = visemeName;

            // Clamp on construction — processor should never produce values
            // outside this range, but we enforce it defensively here.
            this.weight = Mathf.Clamp01(weight);
        }
    }

    // =========================================================================
    // VisemeFrame
    // =========================================================================

    /// <summary>
    /// A snapshot of all active viseme weights at a specific point in time.
    ///
    /// Frames are stored in ascending time order inside LipSyncData.
    /// The player binary-searches this list every frame to find the two
    /// frames bracketing AudioSource.time, then interpolates between them.
    ///
    /// Frames do NOT need to be evenly spaced. The post-processing pipeline
    /// prunes redundant frames, so spacing will be irregular. The binary
    /// search handles this correctly regardless of spacing.
    ///
    /// Most signal-based frames will have 1–2 active weights.
    /// Rhubarb-based frames may have more to represent coarticulation.
    /// The structure supports both without change.
    /// </summary>
    [Serializable]
    public class VisemeFrame
    {
        /// <summary>
        /// Playback time in seconds, relative to the start of the audio clip.
        /// This is compared directly against AudioSource.time at runtime.
        /// </summary>
        [Tooltip("Timestamp in seconds from the start of the audio clip.")]
        public float time;

        /// <summary>
        /// All viseme contributions active at this timestamp.
        /// In the signal-based MVP this will typically contain one or two
        /// entries — the dominant mouth shape and optionally REST at low weight.
        /// </summary>
        [Tooltip("Active viseme weights at this timestamp.")]
        public List<VisemeWeight> weights = new ();

        public VisemeFrame(float time)
        {
            this.time = time;
        }
    }

    // =========================================================================
    // LipSyncData
    // =========================================================================

    /// <summary>
    /// The core data asset produced by the ResonanceSync editor tool.
    ///
    /// Contains all viseme animation data for one audio clip, stored as an
    /// ordered sequence of VisemeFrames. This asset is intentionally
    /// character-independent — it contains no blendshape indices, no mesh
    /// references, and no rig-specific data.
    ///
    /// WORKFLOW:
    ///   Artist generates this asset in the editor using ResonanceSyncEditorWindow.
    ///   Developer assigns it to a ResonanceSyncPlayer along with the AudioClip.
    ///   The player resolves viseme names → blendshape indices via VisemeProfile.
    ///
    /// This asset can be reused across multiple characters as long as each
    /// character has a VisemeProfile with matching viseme names.
    /// </summary>
    [CreateAssetMenu(
        menuName = "GlyphLabs/ResonanceSync/Lip Sync Data",
        fileName = "NewLipSyncData",
        order = 0)]
    public class LipSyncData : ScriptableObject
    {
        // ------------------------------------------------------------------
        // Metadata
        // ------------------------------------------------------------------

        /// <summary>
        /// Total duration of the source audio clip in seconds.
        /// Stored so the player can validate the paired AudioClip at runtime
        /// and warn if a mismatched clip is provided.
        /// </summary>
        [Tooltip("Duration of the source audio clip in seconds.")]
        public float duration;

        /// <summary>
        /// Which processing mode was used to generate this data.
        /// Stored for reference and debugging — does not affect playback.
        /// </summary>
        [Tooltip("Processing mode used during generation. Informational only.")]
        public ProcessingMode generatedWith;

        // ------------------------------------------------------------------
        // Frame data
        // ------------------------------------------------------------------

        /// <summary>
        /// Ordered list of viseme frames, sorted ascending by time.
        /// This is the primary data consumed by ResonanceSyncPlayer at runtime.
        ///
        /// Do not modify this list at runtime. It is read-only from the
        /// player's perspective. All modifications happen at generation time
        /// in the editor.
        /// </summary>
        [Tooltip("Generated viseme frames, sorted ascending by time.")]
        public List<VisemeFrame> frames = new ();

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        /// <summary>
        /// True if this asset contains at least one frame.
        /// A LipSyncData with no frames has not been generated yet or
        /// generation failed.
        /// </summary>
        public bool IsValid => frames != null && frames.Count > 0;

        /// <summary>
        /// Checks whether the provided AudioClip's length matches the stored
        /// duration within an acceptable tolerance.
        ///
        /// Call this in ResonanceSyncPlayer.Play() before starting playback.
        /// A mismatch means either the wrong clip was assigned, or the clip
        /// was re-imported at a different sample rate after generation.
        /// </summary>
        /// <param name = "clip" > The AudioClip about to be played.</param>
        /// <param name = "toleranceSeconds" >
        /// Acceptable difference in seconds.Default 0.1s covers minor
        /// floating point differences from re-import or compression.
        /// </param>
        /// <returns>True if lengths match within tolerance.</returns>
        public bool ValidateAgainstClip(AudioClip clip, float toleranceSeconds = 0.1f)
        {
            if (clip == null) return false;
            return Mathf.Abs(clip.length - duration) <= toleranceSeconds;
        }
    }
}