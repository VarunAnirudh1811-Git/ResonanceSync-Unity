using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Contract that all lip sync generation strategies must implement.
    ///
    /// A strategy is responsible for ONE thing only:
    /// analysing an AudioClip and producing a raw list of VisemeFrames.
    ///
    /// It does NOT smooth, normalise, prune, or write to LipSyncData.
    /// All post-processing is handled by LipSyncProcessor after Execute()
    /// returns, regardless of which strategy was used.
    ///
    /// IMPLEMENTED BY:
    ///   SignalStrategy  — amplitude + FFT analysis (no dependencies)
    ///   RhubarbStrategy — phoneme extraction via Rhubarb CLI (editor only)
    ///
    /// CONSUMED BY:
    ///   LipSyncProcessor — calls Execute() and post-processes the result
    /// </summary>
    public interface ILipSyncStrategy
    {
        /// <summary>
        /// Analyses the provided AudioClip and returns a raw list of
        /// VisemeFrames ordered ascending by time.
        ///
        /// Frames returned here are RAW — weights are unsmoothed and
        /// unnormalised. LipSyncProcessor applies post-processing after
        /// this method returns.
        ///
        /// This method runs in the editor only, never at runtime.
        /// It may be slow — it is called once during generation, not per frame.
        /// </summary>
        /// <param name="clip">
        /// The AudioClip to analyse. Must be readable
        /// (Read/Write enabled in import settings for signal analysis).
        /// </param>
        /// <param name="profile">
        /// The VisemeProfile defining which viseme names exist for this
        /// character. Strategies use this to know what names to write into
        /// VisemeWeight entries.
        /// </param>
        /// <param name="settings">
        /// Generation settings including samplesPerSecond and
        /// minAmplitudeThreshold. Post-processing settings like smoothing
        /// and timeOffset are handled by LipSyncProcessor, not the strategy.
        /// </param>
        /// <returns>
        /// Raw VisemeFrames sorted ascending by time.
        /// Must not be null — return an empty list on failure.
        /// </returns>
        List<VisemeFrame> Execute(
            AudioClip clip,
            VisemeProfile profile,
            LipSyncSettings settings
        );

        /// <summary>
        /// Human-readable name for this strategy.
        /// Displayed in the editor window's mode dropdown and stored in
        /// LipSyncData.generatedWith for debugging reference.
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Returns true if this strategy can currently run in the environment.
        /// Signal strategy is always available.
        /// Rhubarb strategy returns false if the binary is missing.
        /// Used by the editor window to disable unavailable options.
        /// </summary>
        bool IsAvailable { get; }
    }
}