using UnityEngine;
using UnityEditor;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Draws a three-band frequency energy bar graph (Low / Mid / High).
    ///
    /// Band values are supplied externally — this drawer does no FFT itself.
    /// The window computes bands at the current scrub position using
    /// SignalStrategy and passes them here for display.
    ///
    /// Bands are normalised [0, 1] where 1 = the band holds all energy.
    /// This gives the artist a visual sense of why a particular viseme
    /// was selected at a given point in the clip.
    /// </summary>
    public class FFTBandDrawer
    {
        // ------------------------------------------------------------------
        // Colours per band
        // ------------------------------------------------------------------

        private static readonly Color LowColor = new Color(0.3f, 0.6f, 1.0f, 1f); // Blue
        private static readonly Color MidColor = new Color(0.4f, 0.9f, 0.4f, 1f); // Green
        private static readonly Color HighColor = new Color(1.0f, 0.5f, 0.3f, 1f); // Orange
        private static readonly Color BgColor = new Color(0.13f, 0.13f, 0.13f, 1f);
        private static readonly Color LabelColor = new Color(0.75f, 0.75f, 0.75f, 1f);

        private const float BarSpacing = 6f;
        private const float LabelHeight = 16f;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Draws three frequency band bars into the given rect.
        /// </summary>
        /// <param name="rect">The rect to draw into.</param>
        /// <param name="low">Low band energy [0, 1].</param>
        /// <param name="mid">Mid band energy [0, 1].</param>
        /// <param name="high">High band energy [0, 1].</param>
        public void Draw(Rect rect, float low, float mid, float high)
        {
            // Background
            EditorGUI.DrawRect(rect, BgColor);

            float barAreaHeight = rect.height - LabelHeight;
            if (barAreaHeight <= 0) return;

            float totalBars = 3f;
            float barWidth = (rect.width - BarSpacing * (totalBars + 1)) / totalBars;
            if (barWidth <= 0) return;

            DrawBar(rect, 0, barWidth, barAreaHeight, low, "LOW", LowColor);
            DrawBar(rect, 1, barWidth, barAreaHeight, mid, "MID", MidColor);
            DrawBar(rect, 2, barWidth, barAreaHeight, high, "HIGH", HighColor);
        }

        // ------------------------------------------------------------------
        // Internal
        // ------------------------------------------------------------------

        private void DrawBar(
            Rect parentRect,
            int barIndex,
            float barWidth,
            float barAreaHeight,
            float value,
            string label,
            Color color)
        {
            float x = parentRect.x + BarSpacing + barIndex * (barWidth + BarSpacing);

            // Background slot
            var slotRect = new Rect(x, parentRect.y, barWidth, barAreaHeight);
            EditorGUI.DrawRect(slotRect, new Color(0.2f, 0.2f, 0.2f, 1f));

            // Filled portion — grows from bottom
            float fillHeight = Mathf.Clamp01(value) * barAreaHeight;
            var fillRect = new Rect(
                x,
                parentRect.y + barAreaHeight - fillHeight,
                barWidth,
                fillHeight);
            EditorGUI.DrawRect(fillRect, color);

            // Label below bar
            var labelRect = new Rect(
                x,
                parentRect.y + barAreaHeight + 2f,
                barWidth,
                LabelHeight);

            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                normal = { textColor = LabelColor }
            };
            GUI.Label(labelRect, label, style);
        }
    }
}