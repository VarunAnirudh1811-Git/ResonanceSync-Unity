using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Holds all transient state for one editor generation session.
    ///
    /// Separated from the window class for two reasons:
    ///   1. Testability — session state can be verified without an open window.
    ///   2. Domain reload safety — the window serializes this object so all
    ///      state survives recompile and entering/exiting Play Mode cleanly.
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
        // Settings
        // ------------------------------------------------------------------

        public LipSyncSettings settings = new LipSyncSettings();

        // ------------------------------------------------------------------
        // Generated asset
        // ------------------------------------------------------------------

        public LipSyncData generatedData;

        // ------------------------------------------------------------------
        // Playback state
        // ------------------------------------------------------------------

        public bool isPlaying;
        public float currentTime;

        // ------------------------------------------------------------------
        // Save path
        // ------------------------------------------------------------------

        public string savePath = "Assets/Audio/LipSync/NewLipSyncData.asset";

        // ------------------------------------------------------------------
        // UI fold state
        // ------------------------------------------------------------------

        public bool settingsFoldout = true;
        public bool debugFoldout = false;

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        public bool CanGenerate =>
            clip != null &&
            profile != null;

        public bool CanPreview =>
            generatedData != null &&
            generatedData.IsValid &&
            clip != null;

        public bool CanSave =>
            generatedData != null &&
            generatedData.IsValid;

        // ------------------------------------------------------------------
        // Active blendshape index list
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the current active blendshape indices from the profile.
        /// Used by CharacterPreviewDrawer to mirror weights from the scene
        /// SkinnedMeshRenderer to the preview scene SMR.
        ///
        /// Returns an empty list if no profile is assigned or the profile
        /// has no valid mappings.
        /// </summary>
        public IReadOnlyList<int> GetActiveBlendshapeIndices()
        {
            if (profile == null) return _emptyIndices;
            return profile.GetAllActiveIndices();
        }

        private static readonly List<int> _emptyIndices = new List<int>();

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats a time value as M:SS.f for display in playback controls.
        /// e.g. 65.4f → "1:05.4"
        /// </summary>
        public static string FormatTime(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            int mins = Mathf.FloorToInt(seconds / 60f);
            float secs = seconds - mins * 60f;
            return $"{mins}:{secs:00.0}";
        }

        /// <summary>
        /// Derives a default save path from the AudioClip's asset path.
        /// e.g. Assets/Audio/VO_Line01.wav →
        ///      Assets/Audio/LipSync/VO_Line01_LipSync.asset
        /// Falls back to current savePath if the clip has no asset path.
        /// </summary>
        public void DeriveDefaultSavePath()
        {
            if (clip == null) return;

            string clipPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(clipPath)) return;

            string dir = System.IO.Path.GetDirectoryName(clipPath);
            string baseName = System.IO.Path.GetFileNameWithoutExtension(clipPath);
            string lipSyncDir = System.IO.Path.Combine(dir ?? "Assets", "LipSync");

            savePath = System.IO.Path.Combine(lipSyncDir, $"{baseName}_LipSync.asset")
                .Replace("\\", "/");
        }
    }
}