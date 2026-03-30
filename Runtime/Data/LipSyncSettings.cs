using System;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Tuning parameters for the lip sync generation pipeline.
    ///
    /// This is a plain serializable class, not a ScriptableObject.
    /// It lives inside the Editor window and is passed directly to the
    /// processor at generation time. It is NOT stored inside LipSyncData —
    /// the data asset only contains the final baked result.
    ///
    /// This means artists can re-generate with different settings without
    /// creating or managing a separate settings asset.
    /// </summary>
    [Serializable]
    public class LipSyncSettings
    {
        // ------------------------------------------------------------------
        // Sampling
        // ------------------------------------------------------------------

        /// <summary>
        /// How many analysis windows (frames) are generated per second of audio.
        /// Higher values produce more responsive animation but larger assets.
        /// 30 matches traditional animation frame rate and is a safe default.
        /// </summary>
        [Tooltip("Analysis frames generated per second of audio. 30 recommended.")]
        [Range(12, 60)]
        public int samplesPerSecond = 30;

        // ------------------------------------------------------------------
        // Output shaping
        // ------------------------------------------------------------------

        /// <summary>
        /// Global multiplier applied to all viseme weights after generation.
        /// Use to scale the overall intensity of mouth movement up or down
        /// without regenerating. 1.0 = no change.
        /// </summary>
        [Tooltip("Global scale on all output viseme weights. 1.0 = no change.")]
        [Range(0f, 2f)]
        public float intensityMultiplier = 1f;

        /// <summary>
        /// Temporal smoothing strength applied after raw weight generation.
        /// Smoothing averages each frame's weights with its neighbours,
        /// removing jitter caused by rapid amplitude fluctuation.
        /// 0 = no smoothing. Higher values = softer, more fluid movement.
        /// Stored in seconds (window size), not frames, so it's
        /// sample-rate-independent.
        /// </summary>
        [Tooltip("Smoothing window size in seconds. 0 = no smoothing.")]
        [Range(0f, 0.15f)]
        public float smoothing = 0.04f;

        /// <summary>
        /// Shifts all generated frames forward or backward in time.
        /// Positive values make the mouth lead the audio slightly.
        /// Negative values make it lag.
        /// Use to compensate for rig or renderer latency on specific characters.
        /// </summary>
        [Tooltip("Time shift in seconds applied to all frames. Usually 0.")]
        [Range(-0.15f, 0.15f)]
        public float timeOffset = 0f;

        // ------------------------------------------------------------------
        // Silence detection
        // ------------------------------------------------------------------

        /// <summary>
        /// RMS amplitude below this value is treated as silence.
        /// Frames below threshold will use the REST viseme at weight 0.
        /// Prevents subtle mouth movement during breath, room tone, or pauses.
        /// </summary>
        [Tooltip("Audio windows quieter than this are treated as silence.")]
        [Range(0f, 0.1f)]
        public float minAmplitudeThreshold = 0.01f;
    }
}