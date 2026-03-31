using UnityEngine;
using UnityEditor;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Holds all transient state for one editor generation session.
    ///
    /// Separated from the window class for two reasons:
    ///   1. Testability — session state can be verified without an open window.
    ///   2. Domain reload safety — the window serializes this object, so all
    ///      state survives recompile and entering/exiting Play Mode cleanly.
    ///
    /// This class owns:
    ///   - Artist inputs (clip, mesh, profile, mode)
    ///   - LipSyncSettings (tuning sliders)
    ///   - Generated asset reference
    ///   - Playback state (isPlaying, currentTime)
    ///   - Save path
    ///
    /// It does NOT own:
    ///   - The waveform texture (rebuilt from clip when needed — not serializable)
    ///   - The preview player reference (scene object — not serializable)
    ///   - FFT band data (computed on demand during scrub)
    /// </summary>
    [System.Serializable]
    public class LipSyncGenerationSession
    {
        // ------------------------------------------------------------------
        // Artist inputs
        // ------------------------------------------------------------------

        public AudioClip clip;
        public SkinnedMeshRenderer skinnedMesh;
        public VisemeProfile profile;
        public ProcessingMode mode = ProcessingMode.Signal;

        // ------------------------------------------------------------------
        // Generation settings
        // ------------------------------------------------------------------

        public LipSyncSettings settings = new LipSyncSettings();

        // ------------------------------------------------------------------
        // Generated asset
        // ------------------------------------------------------------------

        /// <summary>
        /// The last generated LipSyncData asset.
        /// Null until Generate has been run at least once this session.
        /// </summary>
        public LipSyncData generatedData;

        // ------------------------------------------------------------------
        // Playback state
        // ------------------------------------------------------------------

        /// <summary>
        /// True when the editor preview is actively playing.
        /// Distinct from the player's IsPlaying because we track this
        /// independently for UI state (button labels, scrubber interaction).
        /// </summary>
        public bool isPlaying;
        public float currentTime;

        // ------------------------------------------------------------------
        // Save path
        // ------------------------------------------------------------------

        /// <summary>
        /// Project-relative path where the asset will be saved.
        /// Default points to Assets/Audio/LipSync/ — updated via the
        /// save path field in the window.
        /// </summary>
        public string savePath = "Assets/Audio/LipSync/NewLipSyncData.asset";

        // ------------------------------------------------------------------
        // UI fold state
        // ------------------------------------------------------------------

        public bool settingsFoldout = true;
        public bool debugFoldout = false;

        // ------------------------------------------------------------------
        // Validation helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// True when all required inputs are assigned.
        /// The Generate button is only enabled when this returns true.
        /// </summary>
        public bool CanGenerate =>
            clip != null &&
            profile != null;

        /// <summary>
        /// True when generated data exists and a preview player is available.
        /// The playback controls are only shown when this returns true.
        /// </summary>
        public bool CanPreview =>
            generatedData != null &&
            generatedData.IsValid &&
            clip != null;

        /// <summary>
        /// True when generated data exists and is valid.
        /// The Save button is only enabled when this returns true.
        /// </summary>
        public bool CanSave =>
            generatedData != null &&
            generatedData.IsValid;

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats a time value as M:SS.f for display in the playback controls.
        /// e.g. 65.4f → "1:05.4"
        /// </summary>
        public static string FormatTime(float seconds)
        {
            int mins = Mathf.FloorToInt(seconds / 60f);
            float secs = seconds - mins * 60f;
            return $"{mins}:{secs:00.0}";
        }

        /// <summary>
        /// Derives a default save path from the AudioClip's asset path.
        /// e.g. "Assets/Audio/VO_Line01.wav" →
        ///      "Assets/Audio/LipSync/VO_Line01_LipSync.asset"
        /// Falls back to the current savePath if the clip has no asset path.
        /// </summary>
        public void DeriveDefaultSavePath()
        {
            if (clip == null) return;

            string clipPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(clipPath)) return;

            string dir = System.IO.Path.GetDirectoryName(clipPath);
            string baseName = System.IO.Path.GetFileNameWithoutExtension(clipPath);
            string lipSyncDir = System.IO.Path.Combine(dir, "LipSync");

            savePath = System.IO.Path.Combine(
                lipSyncDir, $"{baseName}_LipSync.asset")
                .Replace("\\", "/");
        }
    }
}