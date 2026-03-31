using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Draws a horizontal bar for each viseme showing its interpolated weight
    /// at the current playback time.
    ///
    /// Mirrors the player's EvaluateAndApply logic exactly — finds the two
    /// bracketing frames, interpolates, and displays the result. This is the
    /// direct visual representation of what the player is doing right now.
    ///
    /// If the generated data or profile is null, draws a placeholder message.
    /// </summary>
    public class VisemeWeightDrawer
    {
        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        private const float RowHeight = 18f;
        private const float LabelWidth = 60f;
        private const float ValueWidth = 38f;
        private const float BarPadding = 4f;

        private static readonly Color BarBgColor = new Color(0.18f, 0.18f, 0.18f, 1f);
        private static readonly Color BarFillColor = new Color(0.35f, 0.75f, 0.55f, 1f);
        private static readonly Color DominantColor = new Color(0.9f, 0.75f, 0.25f, 1f);
        private static readonly Color TextColor = new Color(0.85f, 0.85f, 0.85f, 1f);

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Draws interpolated viseme weight bars at the given time.
        /// Returns the total height drawn so the window can advance its layout.
        /// </summary>
        public float Draw(
            Rect startRect,
            LipSyncData data,
            VisemeProfile profile,
            float currentTime)
        {
            if (data == null || !data.IsValid || profile == null)
            {
                EditorGUI.LabelField(startRect,
                    "No data — generate first.",
                    EditorStyles.centeredGreyMiniLabel);
                return RowHeight;
            }

            // Get interpolated weights at current time
            Dictionary<string, float> weights = InterpolateAt(data, currentTime);
            if (weights == null || weights.Count == 0)
            {
                EditorGUI.LabelField(startRect,
                    "No frames in data.",
                    EditorStyles.centeredGreyMiniLabel);
                return RowHeight;
            }

            // Find dominant (highest weight) for highlight
            string dominant = "";
            float maxW = -1f;
            foreach (var kvp in weights)
                if (kvp.Value > maxW) { maxW = kvp.Value; dominant = kvp.Key; }

            float yOffset = 0f;
            float barWidth = startRect.width - LabelWidth - ValueWidth - BarPadding * 2f;

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = TextColor },
                alignment = TextAnchor.MiddleLeft,
            };
            var valueStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = TextColor },
                alignment = TextAnchor.MiddleRight,
            };

            foreach (var kvp in weights)
            {
                float rowY = startRect.y + yOffset;
                bool isDominant = kvp.Key == dominant;

                // Label
                GUI.Label(
                    new Rect(startRect.x, rowY, LabelWidth, RowHeight),
                    kvp.Key,
                    labelStyle);

                // Bar background
                var bgRect = new Rect(
                    startRect.x + LabelWidth + BarPadding,
                    rowY + 3f,
                    barWidth,
                    RowHeight - 6f);
                EditorGUI.DrawRect(bgRect, BarBgColor);

                // Bar fill
                float fillW = Mathf.Clamp01(kvp.Value) * barWidth;
                if (fillW > 0.5f)
                {
                    EditorGUI.DrawRect(
                        new Rect(bgRect.x, bgRect.y, fillW, bgRect.height),
                        isDominant ? DominantColor : BarFillColor);
                }

                // Value label
                GUI.Label(
                    new Rect(
                        startRect.x + LabelWidth + BarPadding + barWidth + BarPadding,
                        rowY,
                        ValueWidth,
                        RowHeight),
                    $"{kvp.Value:F2}",
                    valueStyle);

                yOffset += RowHeight;
            }

            return yOffset;
        }

        /// <summary>
        /// Returns the total height this drawer will occupy for the given data.
        /// Used by the window to reserve layout space before drawing.
        /// </summary>
        public float GetHeight(LipSyncData data)
        {
            if (data == null || !data.IsValid) return RowHeight;

            // Count unique viseme names across all frames
            var names = new HashSet<string>();
            foreach (var frame in data.frames)
                foreach (var vw in frame.weights)
                    names.Add(vw.visemeName);

            return Mathf.Max(RowHeight, names.Count * RowHeight);
        }

        // ------------------------------------------------------------------
        // Interpolation — mirrors ResonanceSyncPlayer.EvaluateAndApply
        // ------------------------------------------------------------------

        private Dictionary<string, float> InterpolateAt(LipSyncData data, float time)
        {
            var frames = data.frames;
            var result = new Dictionary<string, float>(
                System.StringComparer.OrdinalIgnoreCase);

            if (frames == null || frames.Count == 0) return result;

            int lo = 0;
            int hi = frames.Count - 1;

            if (time <= frames[lo].time)
            {
                return FrameToDict(frames[lo], 1f);
            }

            if (time >= frames[hi].time)
            {
                return FrameToDict(frames[hi], 1f);
            }

            // Binary search
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].time <= time) lo = mid;
                else hi = mid;
            }

            VisemeFrame a = frames[lo];
            VisemeFrame b = frames[hi];

            float span = b.time - a.time;
            float t = span > Mathf.Epsilon ? (time - a.time) / span : 1f;

            // Blend A at (1-t)
            foreach (var vw in a.weights)
            {
                result.TryGetValue(vw.visemeName, out float existing);
                result[vw.visemeName] = existing + vw.weight * (1f - t);
            }

            // Blend B at t
            foreach (var vw in b.weights)
            {
                result.TryGetValue(vw.visemeName, out float existing);
                result[vw.visemeName] = existing + vw.weight * t;
            }

            return result;
        }

        private Dictionary<string, float> FrameToDict(VisemeFrame frame, float blend)
        {
            var d = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var vw in frame.weights)
                d[vw.visemeName] = vw.weight * blend;
            return d;
        }
    }
}