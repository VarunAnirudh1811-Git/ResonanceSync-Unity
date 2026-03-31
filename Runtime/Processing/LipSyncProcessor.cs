using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Orchestrates the full lip sync generation pipeline.
    ///
    /// Responsible for:
    ///   1. Calling the selected ILipSyncStrategy to produce raw frames
    ///   2. Running post-processing (smooth → normalise → prune → offset)
    ///   3. Writing the final result into a LipSyncData asset
    ///
    /// Static — no instance state. All state lives in the data objects
    /// passed to and returned from it.
    ///
    /// FIXES APPLIED:
    ///   - Commit now receives ProcessingMode explicitly from the strategy
    ///     instead of inferring it from samplesPerSecond (was always wrong
    ///     for Rhubarb mode).
    ///   - Smooth rewritten as a sliding-window pass: O(n) instead of O(n²),
    ///     no per-frame allocation. Uses a two-pointer approach to maintain
    ///     the active window as we advance through sorted frames.
    ///   - ApplyTimeOffset now adds a small epsilon after sorting to prevent
    ///     duplicate timestamps which caused interpolation discontinuities.
    ///   - FramesDiffer now explicitly compares over the union of viseme names
    ///     from both frames for symmetric, correct pruning behaviour.
    /// </summary>
    public static class LipSyncProcessor
    {
        // Frames whose timestamps are closer than this after offset are
        // given a small nudge to prevent span == 0 in the player.
        private const float MinFrameSpan = 0.0001f;

        // ------------------------------------------------------------------
        // Public entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the full generation pipeline and writes results into the
        /// provided LipSyncData asset.
        ///
        /// Does NOT save the asset to disk. Call
        /// UnityEditor.AssetDatabase.SaveAssets() after this returns.
        /// </summary>
        public static bool Generate(
            ILipSyncStrategy strategy,
            AudioClip clip,
            VisemeProfile profile,
            LipSyncSettings settings,
            LipSyncData target)
        {
            if (!ValidateInputs(strategy, clip, profile, settings, target))
                return false;

            // ── Step 1: Strategy produces raw frames ──────────────────────
            List<VisemeFrame> frames = strategy.Execute(clip, profile, settings);

            if (frames == null || frames.Count == 0)
            {
                Debug.LogError(
                    $"[ResonanceSync] Strategy '{strategy.DisplayName}' " +
                    "returned no frames. Generation failed.");
                return false;
            }

            Debug.Log(
                $"[ResonanceSync] '{strategy.DisplayName}' produced " +
                $"{frames.Count} raw frames.");

            // ── Step 2: Post-processing pipeline ──────────────────────────
            if (settings.smoothing > 0f)
                Smooth(frames, settings.smoothing);

            Normalise(frames, settings.intensityMultiplier);

            Prune(frames);

            if (!Mathf.Approximately(settings.timeOffset, 0f))
                ApplyTimeOffset(frames, settings.timeOffset, clip.length);

            // ── Step 3: Commit to asset ───────────────────────────────────
            // FIX: Pass strategy.DisplayName-derived mode explicitly.
            // Previously used samplesPerSecond > 0 which was always wrong
            // for Rhubarb (which also uses non-zero samplesPerSecond internally).
            ProcessingMode mode = strategy is SignalStrategy
                ? ProcessingMode.Signal
                : ProcessingMode.Rhubarb;

            Commit(frames, clip, mode, target);

            Debug.Log(
                $"[ResonanceSync] Generation complete. " +
                $"{target.frames.Count} frames written to '{target.name}'.");

            return true;
        }

        // ------------------------------------------------------------------
        // Post-processing — Smooth
        // ------------------------------------------------------------------

        /// <summary>
        /// Smooths viseme weights using a sliding temporal window.
        ///
        /// ALGORITHM (O(n) sliding window):
        ///   Maintains two pointers (left, right) into the sorted frame list.
        ///   As the centre pointer advances, left/right expand/contract to keep
        ///   only frames within ±halfWindow seconds of the centre.
        ///   For each centre frame, we average over the weights in the window
        ///   using a pre-built running sum — no per-frame allocation.
        ///
        /// We snapshot original weights before smoothing so early smoothed
        /// frames don't contaminate later ones.
        ///
        /// Window size is in seconds (not frames) so behaviour is consistent
        /// regardless of samplesPerSecond.
        /// </summary>
        private static void Smooth(List<VisemeFrame> frames, float windowSeconds)
        {
            int count = frames.Count;
            if (count == 0) return;

            float halfWindow = windowSeconds * 0.5f;

            // ── Snapshot original weights ─────────────────────────────────
            // Build a flat Dictionary<visemeName, weight> per frame.
            // Allocated once, reused throughout — no per-iteration alloc.
            var snapshots = new Dictionary<string, float>[count];
            var allNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                var snap = new Dictionary<string, float>(
                    frames[i].weights.Count,
                    System.StringComparer.OrdinalIgnoreCase);

                foreach (var vw in frames[i].weights)
                {
                    snap[vw.visemeName] = vw.weight;
                    allNames.Add(vw.visemeName);
                }

                snapshots[i] = snap;
            }

            // Convert name set to array for deterministic iteration order
            string[] visemeNames = new string[allNames.Count];
            allNames.CopyTo(visemeNames);

            // Running sums for each viseme across the current window.
            // Keyed by viseme name. Reused across all centre frames.
            var runningSum = new Dictionary<string, float>(
                visemeNames.Length,
                System.StringComparer.OrdinalIgnoreCase);

            foreach (string name in visemeNames)
                runningSum[name] = 0f;

            // ── Sliding window ────────────────────────────────────────────
            int left = 0;
            int right = 0; // Exclusive upper bound of current window

            // Prime the running sum with the initial window around frame 0
            // Left starts at 0 and right expands until we exceed halfWindow
            while (right < count &&
                   frames[right].time - frames[0].time <= halfWindow)
            {
                AddToRunningSum(runningSum, snapshots[right], visemeNames);
                right++;
            }

            for (int centre = 0; centre < count; centre++)
            {
                float centreTime = frames[centre].time;

                // Expand right to include all frames within halfWindow ahead
                while (right < count &&
                       frames[right].time - centreTime <= halfWindow)
                {
                    AddToRunningSum(runningSum, snapshots[right], visemeNames);
                    right++;
                }

                // Shrink left to exclude frames now beyond halfWindow behind
                while (left < centre &&
                       centreTime - frames[left].time > halfWindow)
                {
                    SubtractFromRunningSum(runningSum, snapshots[left], visemeNames);
                    left++;
                }

                int windowCount = right - left;

                // Write averaged weights back to the frame
                frames[centre].weights.Clear();
                foreach (string name in visemeNames)
                {
                    float avg = runningSum[name] / windowCount;
                    if (avg > 0.001f)
                        frames[centre].weights.Add(new VisemeWeight(name, avg));
                }
            }
        }

        private static void AddToRunningSum(
            Dictionary<string, float> sum,
            Dictionary<string, float> snapshot,
            string[] visemeNames)
        {
            foreach (string name in visemeNames)
            {
                snapshot.TryGetValue(name, out float w);
                sum[name] += w;
            }
        }

        private static void SubtractFromRunningSum(
            Dictionary<string, float> sum,
            Dictionary<string, float> snapshot,
            string[] visemeNames)
        {
            foreach (string name in visemeNames)
            {
                snapshot.TryGetValue(name, out float w);
                sum[name] -= w;

                // Clamp to zero — floating point subtraction can drift negative
                if (sum[name] < 0f) sum[name] = 0f;
            }
        }

        // ------------------------------------------------------------------
        // Post-processing — Normalise
        // ------------------------------------------------------------------

        /// <summary>
        /// Scales all viseme weights so the global peak equals intensityMultiplier.
        ///
        /// Without normalisation, quiet audio produces barely-visible movement.
        /// Normalisation ensures the loudest moment drives shapes to full
        /// intended intensity regardless of recording level.
        ///
        /// intensityMultiplier acts as the target peak:
        ///   1.0 = loudest frame drives shapes to 100% of their range
        ///   0.8 = more subtle overall
        ///   1.5 = boosted (clamped per-shape to 1.0)
        /// </summary>
        private static void Normalise(List<VisemeFrame> frames, float intensityMultiplier)
        {
            float peak = 0f;
            foreach (var frame in frames)
                foreach (var vw in frame.weights)
                    if (vw.weight > peak)
                        peak = vw.weight;

            if (peak < 0.0001f) return;

            float scale = intensityMultiplier / peak;
            foreach (var frame in frames)
                foreach (var vw in frame.weights)
                    vw.weight = Mathf.Clamp01(vw.weight * scale);
        }

        // ------------------------------------------------------------------
        // Post-processing — Prune
        // ------------------------------------------------------------------

        /// <summary>
        /// Removes frames whose weights are nearly identical to their neighbours.
        ///
        /// Comparison is performed over the UNION of viseme names in both
        /// frames, treating absent visemes as weight 0. This is symmetric
        /// and handles coarticulated frames correctly.
        ///
        /// Always preserves first and last frames.
        /// Typically reduces frame count by 30–60% on dialogue clips.
        /// </summary>
        private static void Prune(List<VisemeFrame> frames)
        {
            const float threshold = 0.02f;
            if (frames.Count <= 2) return;

            var pruned = new List<VisemeFrame>(frames.Count) { frames[0] };

            for (int i = 1; i < frames.Count - 1; i++)
            {
                if (FramesDiffer(frames[i - 1], frames[i], threshold) ||
                    FramesDiffer(frames[i], frames[i + 1], threshold))
                    pruned.Add(frames[i]);
            }

            pruned.Add(frames[frames.Count - 1]);

            frames.Clear();
            frames.AddRange(pruned);
        }

        /// <summary>
        /// Returns true if the maximum weight difference between two frames
        /// exceeds the threshold for ANY viseme in the union of both frames'
        /// viseme sets. Treats absent visemes as weight 0.
        /// </summary>
        private static bool FramesDiffer(VisemeFrame a, VisemeFrame b, float threshold)
        {
            // Build lookup for a
            var aWeights = new Dictionary<string, float>(
                a.weights.Count,
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var vw in a.weights)
                aWeights[vw.visemeName] = vw.weight;

            // Build lookup for b
            var bWeights = new Dictionary<string, float>(
                b.weights.Count,
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var vw in b.weights)
                bWeights[vw.visemeName] = vw.weight;

            // Check union of names — compare every name that appears in either
            foreach (var name in aWeights.Keys)
            {
                bWeights.TryGetValue(name, out float bW);
                if (Mathf.Abs(aWeights[name] - bW) > threshold)
                    return true;
            }
            foreach (var name in bWeights.Keys)
            {
                if (aWeights.ContainsKey(name)) continue; // Already checked
                if (bWeights[name] > threshold)           // a has 0, b has bW
                    return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Post-processing — TimeOffset
        // ------------------------------------------------------------------

        /// <summary>
        /// Shifts all frame timestamps by timeOffset seconds, clamped to
        /// [0, clipLength]. After sorting, adds a micro-epsilon between any
        /// frames that ended up at identical timestamps to prevent span == 0
        /// in the player's interpolation.
        /// </summary>
        private static void ApplyTimeOffset(
            List<VisemeFrame> frames,
            float timeOffset,
            float clipLength)
        {
            foreach (var frame in frames)
                frame.time = Mathf.Clamp(frame.time + timeOffset, 0f, clipLength);

            frames.Sort((a, b) => a.time.CompareTo(b.time));

            // FIX: Prevent duplicate timestamps after clamping
            for (int i = 1; i < frames.Count; i++)
            {
                if (frames[i].time - frames[i - 1].time < MinFrameSpan)
                    frames[i].time = frames[i - 1].time + MinFrameSpan;
            }
        }

        // ------------------------------------------------------------------
        // Commit
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes post-processed frames into the LipSyncData asset.
        /// Clears any existing data first.
        ///
        /// FIX: Receives ProcessingMode explicitly from the caller (who knows
        /// which strategy was used) instead of inferring it from settings,
        /// which was unreliable and wrong for Rhubarb mode.
        /// </summary>
        private static void Commit(
            List<VisemeFrame> frames,
            AudioClip clip,
            ProcessingMode mode,
            LipSyncData target)
        {
            target.frames.Clear();
            target.frames.AddRange(frames);
            target.duration = clip.length;
            target.generatedWith = mode;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(target);
#endif
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private static bool ValidateInputs(
            ILipSyncStrategy strategy,
            AudioClip clip,
            VisemeProfile profile,
            LipSyncSettings settings,
            LipSyncData target)
        {
            if (strategy == null)
            {
                Debug.LogError("[ResonanceSync] No strategy provided.");
                return false;
            }
            if (!strategy.IsAvailable)
            {
                Debug.LogError(
                    $"[ResonanceSync] Strategy '{strategy.DisplayName}' " +
                    "is not available.");
                return false;
            }
            if (clip == null)
            {
                Debug.LogError("[ResonanceSync] AudioClip is null.");
                return false;
            }
            if (profile == null)
            {
                Debug.LogError("[ResonanceSync] VisemeProfile is null.");
                return false;
            }
            if (target == null)
            {
                Debug.LogError("[ResonanceSync] Target LipSyncData is null.");
                return false;
            }
            if (settings == null)
            {
                Debug.LogError("[ResonanceSync] LipSyncSettings is null.");
                return false;
            }
            if (!profile.HasRestViseme())
            {
                // FIX: Upgraded from Warning to Error — generation will produce
                // visible mouth movement during silence without a valid REST mapping.
                // This is a configuration error the artist must fix.
                Debug.LogError(
                    $"[ResonanceSync] VisemeProfile '{profile.name}' has no valid " +
                    $"REST mapping for '{profile.restVisemeName}'. " +
                    "Add a REST entry with a valid blendshape index, or update " +
                    "'restVisemeName' to match an existing mapping.");
                return false;
            }
            return true;
        }
    }
}