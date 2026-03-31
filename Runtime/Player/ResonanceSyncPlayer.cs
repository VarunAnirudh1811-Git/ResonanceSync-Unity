using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Runtime playback component for ResonanceSync.
    ///
    /// Reads LipSyncData and drives blendshapes on a SkinnedMeshRenderer
    /// in sync with an AudioSource. AudioSource.time is the single source
    /// of truth — no separate playback clock is maintained.
    ///
    /// DEVELOPER USAGE:
    ///   1. Attach to a character GameObject
    ///   2. Assign SkinnedMeshRenderer and VisemeProfile in the Inspector
    ///   3. Call Play(lipSyncData, audioClip) to begin
    ///
    /// The component manages the AudioSource internally.
    /// Do not drive the AudioSource directly while this player is active.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("GlyphLabs/ResonanceSync Player")]
    public class ResonanceSyncPlayer : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Inspector fields
        // ------------------------------------------------------------------

        [Tooltip("The SkinnedMeshRenderer that owns the face blendshapes.")]
        [SerializeField] private SkinnedMeshRenderer _skinnedMesh;

        [Tooltip("Character-specific mapping from viseme names to blendshape indices.")]
        [SerializeField] private VisemeProfile _visemeProfile;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private AudioSource _audioSource;

        // The active LipSyncData being played.
        // Null when stopped.
        private LipSyncData _data;

        // Flat list of blendshape indices this profile controls.
        // Built once on Play() for fast zeroing in Update().
        // Avoids iterating the profile's mappings list every frame.
        private List<int> _activeBlendshapeIndices = new ();

        // Playback state flags
        private bool _isPlaying;
        private bool _loop;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();

            // We own the AudioSource entirely.
            // Prevent it from doing anything on its own.
            _audioSource.playOnAwake = false;

            // We handle looping manually so we control
            // blendshape state at the loop boundary.
            _audioSource.loop = false;
        }

        private void Update()
        {
            if (!_isPlaying) return;

            // ── Detect clip end ───────────────────────────────────────────
            // AudioSource.isPlaying becomes false when the clip finishes.
            // We check this before reading time so we don't evaluate
            // a stale time value on the final frame.
            if (!_audioSource.isPlaying)
            {
                if (_loop)
                {
                    // Restart manually so we control the seam.
                    // If we used AudioSource.loop = true, we wouldn't get
                    // a callback at the boundary and couldn't zero blendshapes.
                    ZeroAllBlendshapes();
                    _audioSource.Play();
                    return;
                }
                else
                {
                    Stop();
                    return;
                }
            }

            // ── Evaluate and apply ────────────────────────────────────────
            EvaluateAndApply(_audioSource.time);
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Begin lip sync playback.
        /// Validates inputs, builds the blendshape index cache,
        /// then starts the AudioSource.
        /// </summary>
        /// <param name="data">Generated LipSyncData ScriptableObject.</param>
        /// <param name="clip">The AudioClip this data was generated from.</param>
        /// <param name="loop">If true, audio and animation loop seamlessly.</param>
        public void Play(LipSyncData data, AudioClip clip, bool loop = false)
        {
            // Clean up any existing playback before starting new one
            if (_isPlaying) Stop();

            if (!ValidatePlayRequest(data, clip)) return;

            _data = data;
            _loop = loop;

            // Build the flat blendshape index list for this profile.
            // Done here once so Update() never touches the profile dictionary.
            BuildActiveIndexList();

            _audioSource.clip = clip;
            _audioSource.Play();
            _isPlaying = true;
        }

        /// <summary>
        /// Stop playback, zero all blendshapes, and clear state.
        /// Safe to call when already stopped.
        /// </summary>
        public void Stop()
        {
            _isPlaying = false;

            if (_audioSource != null && _audioSource.isPlaying)
                _audioSource.Stop();

            ZeroAllBlendshapes();

            _data = null;
        }

        /// <summary>
        /// Pause playback. Blendshapes hold their current state.
        /// Resume with Resume().
        /// </summary>
        public void Pause()
        {
            if (!_isPlaying) return;

            _audioSource.Pause();
            _isPlaying = false;
        }

        /// <summary>
        /// Resume after Pause(). Continues from where it was paused.
        /// </summary>
        public void Resume()
        {
            if (_isPlaying) return;
            if (_data == null || _audioSource.clip == null) return;

            _audioSource.UnPause();
            _isPlaying = true;
        }

        /// <summary>
        /// Seek to a specific time in seconds.
        /// Works while playing or paused.
        /// Also used by the editor preview scrubber.
        /// </summary>
        /// <param name="time">Target time in seconds.</param>
        public void SeekTo(float time)
        {
            if (_data == null) return;

            float clamped = Mathf.Clamp(time, 0f, _data.duration);
            _audioSource.time = clamped;

            // Evaluate immediately so the mesh updates at the new position
            // even if the player is currently paused (e.g. scrubbing in editor).
            EvaluateAndApply(clamped);
        }

        // ------------------------------------------------------------------
        // Public read-only state
        // ------------------------------------------------------------------

        /// <summary>
        /// True if the player is actively playing (not paused, not stopped).
        /// </summary>
        public bool IsPlaying => _isPlaying && _audioSource != null && _audioSource.isPlaying;

        /// <summary>
        /// Current playback position in seconds.
        /// </summary>
        public float CurrentTime => _audioSource != null ? _audioSource.time : 0f;

        /// <summary>
        /// Duration of the currently loaded clip in seconds.
        /// 0 if nothing is loaded.
        /// </summary>
        public float Duration => _data != null ? _data.duration : 0f;

        // ------------------------------------------------------------------
        // Core evaluation
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds the two frames bracketing the given time, interpolates
        /// their weights, and writes the result to the SkinnedMeshRenderer.
        ///
        /// Called every Update() during playback and directly by SeekTo()
        /// during scrubbing. This is the hot path — kept allocation-free.
        /// </summary>
        private void EvaluateAndApply(float time)
        {
            if (_data == null || _data.frames == null || _data.frames.Count == 0)
                return;

            var frames = _data.frames;

            // ── Step 1: Zero all controlled blendshapes ───────────────────
            // Clean slate every frame. Prevents stale values from previous
            // frame bleeding into the current one.
            ZeroAllBlendshapes();

            // ── Step 2: Binary search for bracket frames ──────────────────
            int lo = 0;
            int hi = frames.Count - 1;

            // Before first frame: apply first frame at full weight
            if (time <= frames[lo].time)
            {
                ApplyFrame(frames[lo], 1f);
                return;
            }

            // After last frame: apply last frame at full weight
            if (time >= frames[hi].time)
            {
                ApplyFrame(frames[hi], 1f);
                return;
            }

            // Binary search: find lo such that frames[lo].time <= time < frames[hi].time
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].time <= time)
                    lo = mid;
                else
                    hi = mid;
            }

            // lo and hi now bracket the current time
            VisemeFrame frameA = frames[lo];
            VisemeFrame frameB = frames[hi];

            // ── Step 3: Calculate interpolation factor ────────────────────
            // t = how far we are between frameA and frameB, as 0–1
            float span = frameB.time - frameA.time;
            float t = span > Mathf.Epsilon
                ? (time - frameA.time) / span
                : 1f; // Frames at identical time: snap to B

            // ── Step 4: Apply both frames with complementary blends ───────
            // ApplyFrame accumulates into current blendshape values.
            // Because we zeroed first, we can safely add contributions
            // from both frames and the result is their weighted blend.
            ApplyFrame(frameA, 1f - t);
            ApplyFrame(frameB, t);
        }

        /// <summary>
        /// Accumulates one frame's viseme weights onto the SkinnedMeshRenderer,
        /// scaled by the blend factor.
        ///
        /// Uses GetBlendShapeWeight + SetBlendShapeWeight to accumulate rather
        /// than overwrite, so two ApplyFrame calls blend additively.
        /// This works correctly because ZeroAllBlendshapes() runs first.
        ///
        /// Unity blendshapes use a 0–100 range. Our weights are 0–1.
        /// The *100 conversion happens here — one place, never duplicated.
        /// </summary>
        private void ApplyFrame(VisemeFrame frame, float blend)
        {
            if (frame.weights == null || frame.weights.Count == 0) return;

            foreach (var vw in frame.weights)
            {
                // Resolve viseme name → blendshape index via profile
                if (!_visemeProfile.TryGetMapping(vw.visemeName, out var mapping))
                    continue;

                int index = mapping.blendshapeIndex;
                if (index < 0) continue; // Disabled mapping

                // Contribution from this frame for this viseme:
                //   weight (0-1) × blend factor (0-1) × per-viseme scale × 100
                float contribution = vw.weight * blend * mapping.weightScale * 100f;

                // Accumulate onto whatever is already there from the other frame
                float current = _skinnedMesh.GetBlendShapeWeight(index);
                _skinnedMesh.SetBlendShapeWeight(index, current + contribution);
            }
        }

        // ------------------------------------------------------------------
        // Blendshape zeroing
        // ------------------------------------------------------------------

        /// <summary>
        /// Sets all blendshape indices controlled by this profile to zero.
        /// Called at the start of every EvaluateAndApply pass and on Stop().
        /// </summary>
        private void ZeroAllBlendshapes()
        {
            if (_skinnedMesh == null) return;

            foreach (int index in _activeBlendshapeIndices)
                _skinnedMesh.SetBlendShapeWeight(index, 0f);
        }

        /// <summary>
        /// Builds the flat list of blendshape indices from the profile.
        /// Called once on Play() so Update() does not touch the profile.
        /// </summary>
        private void BuildActiveIndexList()
        {
            _activeBlendshapeIndices.Clear();

            if (_visemeProfile == null) return;

            // Ensure the profile cache is warm before we start
            _visemeProfile.BuildCache();

            foreach (var mapping in _visemeProfile.mappings)
            {
                if (mapping.blendshapeIndex >= 0)
                    _activeBlendshapeIndices.Add(mapping.blendshapeIndex);
            }
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private bool ValidatePlayRequest(LipSyncData data, AudioClip clip)
        {
            if (data == null)
            {
                Debug.LogError("[ResonanceSync] Play() called with null LipSyncData.", this);
                return false;
            }

            if (!data.IsValid)
            {
                Debug.LogError(
                    "[ResonanceSync] LipSyncData has no frames. " +
                    "Was it generated successfully?", this);
                return false;
            }

            if (clip == null)
            {
                Debug.LogError("[ResonanceSync] Play() called with null AudioClip.", this);
                return false;
            }

            if (_skinnedMesh == null)
            {
                Debug.LogError(
                    "[ResonanceSync] No SkinnedMeshRenderer assigned.", this);
                return false;
            }

            if (_visemeProfile == null)
            {
                Debug.LogError("[ResonanceSync] No VisemeProfile assigned.", this);
                return false;
            }

            // Warn if clip length doesn't match stored duration.
            // Not a hard failure — the artist may have intentionally
            // re-trimmed the clip. Sync will be off but not crash.
            if (!data.ValidateAgainstClip(clip))
            {
                Debug.LogWarning(
                    $"[ResonanceSync] AudioClip '{clip.name}' length ({clip.length:F2}s) " +
                    $"does not match LipSyncData duration ({data.duration:F2}s). " +
                    "Sync may be incorrect.", this);
            }

            return true;
        }

        // ------------------------------------------------------------------
        // Editor support
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only setup called by ResonanceSyncEditorWindow.
        /// Allows the preview player in the editor window to inject its own
        /// mesh and profile without requiring a full scene GameObject.
        ///
        /// Not part of the public runtime API — stripped from builds.
        /// </summary>
        internal void EditorSetup(SkinnedMeshRenderer mesh, VisemeProfile profile)
        {
            _skinnedMesh = mesh;
            _visemeProfile = profile;

            if (_audioSource == null)
                _audioSource = GetComponent<AudioSource>();
        }
#endif
    }
}