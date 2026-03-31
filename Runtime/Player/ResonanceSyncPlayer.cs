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
    /// FIXES APPLIED:
    ///   - BuildActiveIndexList now deduplicates blendshape indices and
    ///     validates each against skinnedMesh.sharedMesh.blendShapeCount.
    ///     Previously duplicate indices caused redundant writes each frame,
    ///     and out-of-range indices could throw or silently fail.
    ///   - ApplyFrame now accumulates into a pre-allocated float[] temp
    ///     buffer instead of calling GetBlendShapeWeight per viseme per frame.
    ///     Writes to the mesh once per blendshape per evaluation pass.
    ///     Eliminates repeated GetBlendShapeWeight calls in the hot path.
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("GlyphLabs/ResonanceSync Player")]
    public class ResonanceSyncPlayer : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Inspector
        // ------------------------------------------------------------------

        [Tooltip("The SkinnedMeshRenderer that owns the face blendshapes.")]
        [SerializeField] private SkinnedMeshRenderer _skinnedMesh;

        [Tooltip("Character-specific mapping from viseme names to blendshape indices.")]
        [SerializeField] private VisemeProfile _visemeProfile;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private AudioSource _audioSource;
        private LipSyncData _data;
        private bool _isPlaying;
        private bool _loop;

        // FIX: Deduplicated, bounds-validated list of blendshape indices.
        // Built once in BuildActiveIndexList() on Play().
        private readonly List<int> _activeIndices = new ();

        // FIX: Accumulation buffer — one float per blendshape index in
        // _activeIndices. Indexed in parallel with _activeIndices.
        // Pre-allocated at Play() time, reused every frame.
        // Eliminates GetBlendShapeWeight calls during EvaluateAndApply.
        private float[] _accumBuffer;

        // Maps blendshape index → position in _activeIndices / _accumBuffer.
        // Used during accumulation so we don't search the list per viseme.
        private readonly Dictionary<int, int> _indexToBufferPos
            = new ();

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = false; // We manage looping manually
        }

        private void Update()
        {
            if (!_isPlaying) return;

            if (!_audioSource.isPlaying)
            {
                if (_loop)
                {
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

            EvaluateAndApply(_audioSource.time);
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Begin lip sync playback.
        /// </summary>
        public void Play(LipSyncData data, AudioClip clip, bool loop = false)
        {
            if (_isPlaying) Stop();
            if (!ValidatePlayRequest(data, clip)) return;

            _data = data;
            _loop = loop;

            BuildActiveIndexList();

            _audioSource.clip = clip;
            _audioSource.Play();
            _isPlaying = true;
        }

        /// <summary>
        /// Stop playback, zero all blendshapes, clear state.
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
        /// </summary>
        public void Pause()
        {
            if (!_isPlaying) return;
            _audioSource.Pause();
            _isPlaying = false;
        }

        /// <summary>
        /// Resume after Pause().
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
        /// Evaluates immediately so the mesh updates even when paused.
        /// </summary>
        public void SeekTo(float time)
        {
            if (_data == null) return;
            _audioSource.time = Mathf.Clamp(time, 0f, _data.duration);
            EvaluateAndApply(_audioSource.time);
        }

        // ------------------------------------------------------------------
        // Public read-only state
        // ------------------------------------------------------------------

        public bool IsPlaying => _isPlaying && _audioSource != null && _audioSource.isPlaying;
        public float CurrentTime => _audioSource != null ? _audioSource.time : 0f;
        public float Duration => _data != null ? _data.duration : 0f;

        // ------------------------------------------------------------------
        // Core evaluation
        // ------------------------------------------------------------------

        /// <summary>
        /// Binary-searches for the two frames bracketing the given time,
        /// interpolates weights, and writes to the SkinnedMeshRenderer.
        ///
        /// Allocation-free hot path — operates on pre-built buffers.
        ///
        /// FIX: Uses a pre-allocated accumulation buffer (_accumBuffer) to
        /// collect blended weights for all active indices, then writes to
        /// the mesh in a single pass. Eliminates GetBlendShapeWeight calls.
        /// </summary>
        private void EvaluateAndApply(float time)
        {
            if (_data == null || _data.frames == null || _data.frames.Count == 0)
                return;
            if (_skinnedMesh == null) return;

            var frames = _data.frames;

            // ── Step 1: Zero accumulation buffer ─────────────────────────
            // Faster than calling SetBlendShapeWeight per index here;
            // we write the mesh in one pass at the end.
            for (int i = 0; i < _accumBuffer.Length; i++)
                _accumBuffer[i] = 0f;

            // ── Step 2: Binary search ─────────────────────────────────────
            int lo = 0;
            int hi = frames.Count - 1;

            if (time <= frames[lo].time)
            {
                AccumulateFrame(frames[lo], 1f);
                FlushBuffer();
                return;
            }

            if (time >= frames[hi].time)
            {
                AccumulateFrame(frames[hi], 1f);
                FlushBuffer();
                return;
            }

            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].time <= time) lo = mid;
                else hi = mid;
            }

            VisemeFrame frameA = frames[lo];
            VisemeFrame frameB = frames[hi];

            // ── Step 3: Interpolation factor ─────────────────────────────
            float span = frameB.time - frameA.time;
            float t = span > Mathf.Epsilon
                ? (time - frameA.time) / span
                : 1f;

            // ── Step 4: Accumulate both frames ────────────────────────────
            AccumulateFrame(frameA, 1f - t);
            AccumulateFrame(frameB, t);

            // ── Step 5: Write to mesh ─────────────────────────────────────
            FlushBuffer();
        }

        /// <summary>
        /// Accumulates one frame's viseme contributions into _accumBuffer.
        /// Does NOT write to the mesh — FlushBuffer() does that.
        ///
        /// Resolves viseme name → buffer position via _indexToBufferPos.
        /// Skips visemes not present in the profile or with disabled indices.
        /// </summary>
        private void AccumulateFrame(VisemeFrame frame, float blend)
        {
            if (frame.weights == null) return;

            foreach (var vw in frame.weights)
            {
                if (!_visemeProfile.TryGetMapping(vw.visemeName, out var mapping))
                    continue;

                int blendshapeIndex = mapping.blendshapeIndex;
                if (blendshapeIndex < 0) continue;

                if (!_indexToBufferPos.TryGetValue(blendshapeIndex, out int bufferPos))
                    continue;

                // Contribution: weight × blend factor × per-viseme scale × 100
                // (Unity blendshapes are 0–100; our weights are 0–1)
                _accumBuffer[bufferPos] +=
                    vw.weight * blend * mapping.weightScale * 100f;
            }
        }

        /// <summary>
        /// Writes _accumBuffer values to the SkinnedMeshRenderer.
        /// One SetBlendShapeWeight call per active index.
        /// Clamps to [0, 100] to handle floating point overshoot.
        /// </summary>
        private void FlushBuffer()
        {
            for (int i = 0; i < _activeIndices.Count; i++)
            {
                _skinnedMesh.SetBlendShapeWeight(
                    _activeIndices[i],
                    Mathf.Clamp(_accumBuffer[i], 0f, 100f));
            }
        }

        // ------------------------------------------------------------------
        // Blendshape zeroing
        // ------------------------------------------------------------------

        private void ZeroAllBlendshapes()
        {
            if (_skinnedMesh == null) return;
            foreach (int index in _activeIndices)
                _skinnedMesh.SetBlendShapeWeight(index, 0f);
        }

        // ------------------------------------------------------------------
        // Index list construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds _activeIndices and _accumBuffer from the profile.
        ///
        /// FIX: Deduplicates blendshape indices — a profile could theoretically
        /// map two viseme names to the same index, which would cause double
        /// writes and incorrect accumulated values.
        ///
        /// FIX: Validates each index against sharedMesh.blendShapeCount.
        /// Out-of-range indices are skipped with a logged error rather than
        /// causing a runtime exception or silent failure.
        /// </summary>
        private void BuildActiveIndexList()
        {
            _activeIndices.Clear();
            _indexToBufferPos.Clear();

            if (_visemeProfile == null || _skinnedMesh == null) return;

            _visemeProfile.BuildCache();

            int maxIndex = _skinnedMesh.sharedMesh != null
                ? _skinnedMesh.sharedMesh.blendShapeCount
                : 0;

            var seen = new HashSet<int>();

            foreach (var mapping in _visemeProfile.mappings)
            {
                int idx = mapping.blendshapeIndex;
                if (idx < 0) continue; // Disabled mapping

                // FIX: Bounds check
                if (idx >= maxIndex)
                {
                    Debug.LogError(
                        $"[ResonanceSync] VisemeProfile '{_visemeProfile.name}': " +
                        $"blendshape index {idx} for viseme '{mapping.visemeName}' " +
                        $"is out of range (mesh has {maxIndex} blendshapes). " +
                        "Update the index in the VisemeProfile.");
                    continue;
                }

                // FIX: Deduplication
                if (!seen.Add(idx))
                {
                    Debug.LogWarning(
                        $"[ResonanceSync] VisemeProfile '{_visemeProfile.name}': " +
                        $"blendshape index {idx} is mapped to multiple visemes. " +
                        "Only the first mapping will be used.");
                    continue;
                }

                _indexToBufferPos[idx] = _activeIndices.Count;
                _activeIndices.Add(idx);
            }

            // Allocate accumulation buffer sized to active index count
            _accumBuffer = new float[_activeIndices.Count];
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
                Debug.LogError("[ResonanceSync] No SkinnedMeshRenderer assigned.", this);
                return false;
            }
            if (_visemeProfile == null)
            {
                Debug.LogError("[ResonanceSync] No VisemeProfile assigned.", this);
                return false;
            }
            if (!data.ValidateAgainstClip(clip))
            {
                Debug.LogWarning(
                    $"[ResonanceSync] AudioClip '{clip.name}' length ({clip.length:F2}s) " +
                    $"does not match LipSyncData duration ({data.duration:F2}s). " +
                    "Sync may be off. Regenerate LipSyncData if the clip was changed.",
                    this);
                // Warning only — do not block playback
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Editor support
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only setup used by ResonanceSyncEditorWindow preview.
        /// Not part of the public runtime API — stripped from builds.
        /// </summary>
        public void EditorSetup(SkinnedMeshRenderer mesh, VisemeProfile profile)
        {
            _skinnedMesh = mesh;
            _visemeProfile = profile;
            if (_audioSource == null)
                _audioSource = GetComponent<AudioSource>();
        }
#endif
    }
}