using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Orchestrates the full lip sync generation pipeline.
    ///
    /// Responsible for:
    ///   1. Calling the selected ILipSyncStrategy to produce raw frames
    ///   2. Running post-processing on those frames (smooth, normalise, prune)
    ///   3. Applying timeOffset
    ///   4. Writing the result into a LipSyncData asset
    ///
    /// This class is static — it has no state of its own.
    /// All state lives in the data objects passed to and from it.
    ///
    /// The post-processing pipeline runs identically regardless of which
    /// strategy produced the raw frames. This guarantees consistent output
    /// quality from both Signal and Rhubarb modes.
    /// </summary>
    public static class LipSyncProcessor
    {
        // ------------------------------------------------------------------
        // Public entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the full generation pipeline and writes results into the
        /// provided LipSyncData asset.
        ///
        /// Call this from the editor window when the artist clicks Generate.
        /// This method does not save the asset to disk — call
        /// UnityEditor.AssetDatabase.SaveAssets() after this returns.
        /// </summary>
        /// <param name="strategy">
        /// The analysis strategy to use. Determines how raw frames are produced.
        /// </param>
        /// <param name="clip">AudioClip to analyse.</param>
        /// <param name="profile">VisemeProfile for the target character.</param>
        /// <param name="settings">Generation and post-processing settings.</param>
        /// <param name="target">
        /// LipSyncData asset to write results into.
        /// Must already exist as an asset — this method populates it,
        /// not creates it.
        /// </param>
        /// <returns>
        /// True if generation succeeded and target was populated.
        /// False if validation failed or strategy returned no frames.
        /// </returns>
        public static bool Generate(
            ILipSyncStrategy strategy,
            AudioClip clip,
            VisemeProfile profile,
            LipSyncSettings settings,
            LipSyncData target)
        {
            // ── Validate inputs ───────────────────────────────────────────
            if (!ValidateInputs(strategy, clip, profile, settings, target))
                return false;

            // ── Step 1: Run strategy to get raw frames ────────────────────
            List<VisemeFrame> frames = strategy.Execute(clip, profile, settings);

            if (frames == null || frames.Count == 0)
            {
                Debug.LogError(
                    $"[ResonanceSync] Strategy '{strategy.DisplayName}' returned " +
                    "no frames. Generation failed.");
                return false;
            }

            Debug.Log(
                $"[ResonanceSync] Strategy '{strategy.DisplayName}' produced " +
                $"{frames.Count} raw frames.");

            // ── Step 2: Post-processing pipeline ──────────────────────────
            if (settings.smoothing > 0f)
                Smooth(frames, settings.smoothing);

            Normalise(frames, settings.intensityMultiplier);

            Prune(frames);

            if (!Mathf.Approximately(settings.timeOffset, 0f))
                ApplyTimeOffset(frames, settings.timeOffset, clip.length);

            // ── Step 3: Write into target asset ───────────────────────────
            Commit(frames, clip, settings, target);

            Debug.Log(
                $"[ResonanceSync] Generation complete. " +
                $"{target.frames.Count} frames written to '{target.name}'.");

            return true;
        }

        // ------------------------------------------------------------------
        // Post-processing stages
        // ------------------------------------------------------------------

        /// <summary>
        /// Smooths viseme weights using a temporal box average.
        ///
        /// For each frame, finds all neighbouring frames within the smoothing
        /// window (±windowSeconds/2) and replaces the frame's weights with
        /// the average of all weights in that window.
        ///
        /// This removes jitter caused by rapid amplitude fluctuation,
        /// producing more fluid and natural-looking mouth movement.
        ///
        /// The smoothing window is defined in seconds, not frames, so the
        /// result is consistent regardless of samplesPerSecond.
        /// </summary>
        private static void Smooth(List<VisemeFrame> frames, float windowSeconds)
        {
            // Half window on each side of the current frame
            float halfWindow = windowSeconds * 0.5f;

            // We need to read from the original weights while writing
            // smoothed weights, so we snapshot the originals first.
            // Without this, early frames would be smoothed using already-
            // smoothed neighbours, producing inconsistent results.
            var snapshots = SnapshotWeights(frames);

            // Collect all unique viseme names across the entire clip.
            // We need this so we can average weights for visemes that may
            // be absent (weight 0) in some frames within the window.
            var allVisemeNames = CollectVisemeNames(frames);

            for (int i = 0; i < frames.Count; i++)
            {
                float frameTime = frames[i].time;

                // Find all frame indices within the smoothing window
                var windowIndices = new List<int>();
                for (int j = 0; j < frames.Count; j++)
                {
                    if (Mathf.Abs(frames[j].time - frameTime) <= halfWindow)
                        windowIndices.Add(j);
                }

                // For each viseme, average its weight across all frames in window
                frames[i].weights.Clear();

                foreach (string visemeName in allVisemeNames)
                {
                    float weightSum = 0f;
                    foreach (int wi in windowIndices)
                    {
                        weightSum += GetWeightFromSnapshot(snapshots[wi], visemeName);
                    }

                    float averaged = weightSum / windowIndices.Count;

                    if (averaged > 0.001f) // Skip negligible weights
                    {
                        frames[i].weights.Add(new VisemeWeight(visemeName, averaged));
                    }
                }
            }
        }

        /// <summary>
        /// Scales all viseme weights so the global peak equals intensityMultiplier.
        ///
        /// Without normalisation, quiet audio produces barely visible movement
        /// because raw amplitude values are low. Normalisation ensures the
        /// loudest moment in the clip drives blendshapes to full intended intensity.
        ///
        /// intensityMultiplier acts as the target peak:
        ///   1.0 = loudest frame drives shapes to 100% of their range
        ///   0.8 = loudest frame drives shapes to 80% (more subtle overall)
        ///   1.5 = loudest frame drives shapes to 150% (clamped to 1.0 per shape)
        /// </summary>
        private static void Normalise(List<VisemeFrame> frames, float intensityMultiplier)
        {
            // Find the global peak weight across all frames and all visemes
            float peak = 0f;
            foreach (var frame in frames)
                foreach (var vw in frame.weights)
                    if (vw.weight > peak)
                        peak = vw.weight;

            // If everything is silence, nothing to normalise
            if (peak < 0.0001f) return;

            float scale = intensityMultiplier / peak;

            foreach (var frame in frames)
                foreach (var vw in frame.weights)
                    vw.weight = Mathf.Clamp01(vw.weight * scale);
        }

        /// <summary>
        /// Removes frames whose weights are nearly identical to their neighbours.
        ///
        /// Two adjacent frames are considered redundant if the maximum weight
        /// difference across all shared visemes is below the pruning threshold.
        /// The player's linear interpolation between the surviving frames
        /// produces output visually indistinguishable from the unpruned data.
        ///
        /// Always preserves the first and last frames regardless of similarity,
        /// so the full duration of the clip is represented correctly.
        ///
        /// Pruning can reduce frame count by 30–60% on typical dialogue,
        /// significantly reducing asset size and binary search depth.
        /// </summary>
        private static void Prune(List<VisemeFrame> frames)
        {
            const float pruneThreshold = 0.02f; // Frames within 2% are redundant

            if (frames.Count <= 2) return;

            var pruned = new List<VisemeFrame> { frames[0] }; // Always keep first

            for (int i = 1; i < frames.Count - 1; i++)
            {
                VisemeFrame prev = frames[i - 1];
                VisemeFrame curr = frames[i];
                VisemeFrame next = frames[i + 1];

                // Keep frame if it differs meaningfully from either neighbour
                if (FramesDiffer(prev, curr, pruneThreshold) ||
                    FramesDiffer(curr, next, pruneThreshold))
                {
                    pruned.Add(curr);
                }
            }

            pruned.Add(frames[frames.Count - 1]); // Always keep last

            frames.Clear();
            frames.AddRange(pruned);
        }

        /// <summary>
        /// Shifts all frame timestamps by timeOffset seconds.
        ///
        /// Applied after all other post-processing so offset doesn't affect
        /// how smoothing windows are calculated.
        ///
        /// Frames shifted before 0 are clamped to 0.
        /// Frames shifted beyond clip length are clamped to clip length.
        /// </summary>
        private static void ApplyTimeOffset(
            List<VisemeFrame> frames,
            float timeOffset,
            float clipLength)
        {
            foreach (var frame in frames)
                frame.time = Mathf.Clamp(frame.time + timeOffset, 0f, clipLength);

            // Re-sort in case offset caused ordering to change at boundaries
            frames.Sort((a, b) => a.time.CompareTo(b.time));
        }

        // ------------------------------------------------------------------
        // Commit
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes post-processed frames into the LipSyncData asset.
        /// Clears any existing data in the asset before writing.
        /// </summary>
        private static void Commit(
            List<VisemeFrame> frames,
            AudioClip clip,
            LipSyncSettings settings,
            LipSyncData target)
        {
            target.frames.Clear();
            target.frames.AddRange(frames);
            target.duration = clip.length;
            target.generatedWith = settings.samplesPerSecond > 0
                ? ProcessingMode.Signal
                : ProcessingMode.Rhubarb;

            // Mark the asset dirty so Unity knows to save it
            // This is editor-only but processor lives in Runtime assembly,
            // so we use a conditional compile guard.
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
                    $"[ResonanceSync] Strategy '{strategy.DisplayName}' is not available.");
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
                Debug.LogError("[ResonanceSync] Target LipSyncData asset is null.");
                return false;
            }
            if (settings == null)
            {
                Debug.LogError("[ResonanceSync] LipSyncSettings is null.");
                return false;
            }
            if (!profile.HasRestViseme())
            {
                Debug.LogWarning(
                    $"[ResonanceSync] VisemeProfile '{profile.name}' has no valid " +
                    "REST viseme. Silence frames will be empty.");
                // Warning only — generation can continue
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Takes a snapshot of all frames' weights as plain dictionaries.
        /// Used by Smooth() so we read from original values while writing
        /// smoothed values, preventing earlier smoothed frames from
        /// contaminating later ones.
        /// </summary>
        private static List<Dictionary<string, float>> SnapshotWeights(
            List<VisemeFrame> frames)
        {
            var snapshots = new List<Dictionary<string, float>>(frames.Count);
            foreach (var frame in frames)
            {
                var snap = new Dictionary<string, float>(
                    frame.weights.Count,
                    System.StringComparer.OrdinalIgnoreCase);

                foreach (var vw in frame.weights)
                    snap[vw.visemeName] = vw.weight;

                snapshots.Add(snap);
            }
            return snapshots;
        }

        /// <summary>
        /// Returns the weight for a given viseme name from a snapshot,
        /// or 0 if that viseme is absent (treated as inactive).
        /// </summary>
        private static float GetWeightFromSnapshot(
            Dictionary<string, float> snapshot,
            string visemeName)
        {
            return snapshot.TryGetValue(visemeName, out float w) ? w : 0f;
        }

        /// <summary>
        /// Collects every unique viseme name that appears across all frames.
        /// Used by Smooth() to ensure absent visemes are treated as weight 0
        /// rather than being silently skipped during averaging.
        /// </summary>
        private static HashSet<string> CollectVisemeNames(List<VisemeFrame> frames)
        {
            var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var frame in frames)
                foreach (var vw in frame.weights)
                    names.Add(vw.visemeName);
            return names;
        }

        /// <summary>
        /// Returns true if the maximum weight difference between two frames
        /// exceeds the given threshold for any shared viseme.
        ///
        /// Used by Prune() to determine if a frame carries meaningful
        /// information not already represented by its neighbours.
        /// </summary>
        private static bool FramesDiffer(
            VisemeFrame a,
            VisemeFrame b,
            float threshold)
        {
            // Build a fast lookup for frame a's weights
            var aWeights = new Dictionary<string, float>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var vw in a.weights)
                aWeights[vw.visemeName] = vw.weight;

            // Check every viseme in b against a
            foreach (var vw in b.weights)
            {
                float aWeight = aWeights.TryGetValue(vw.visemeName, out float w) ? w : 0f;
                if (Mathf.Abs(vw.weight - aWeight) > threshold)
                    return true;
            }

            // Also check visemes in a that are absent in b (effectively weight 0 in b)
            foreach (var vw in a.weights)
            {
                bool existsInB = false;
                foreach (var bvw in b.weights)
                {
                    if (string.Equals(bvw.visemeName, vw.visemeName,
                        System.StringComparison.OrdinalIgnoreCase))
                    {
                        existsInB = true;
                        break;
                    }
                }
                if (!existsInB && vw.weight > threshold)
                    return true;
            }

            return false;
        }
    }
}