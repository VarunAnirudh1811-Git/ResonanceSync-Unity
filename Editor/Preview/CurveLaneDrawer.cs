using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Draws the timeline section of the right panel.
    ///
    /// Layout (top to bottom inside the given rect):
    ///   1. Thin waveform strip — audio amplitude overview
    ///   2. Curve lanes — one filled area graph per viseme, colour-coded
    ///   3. Time ruler — seconds markers
    ///   4. Playback controls — rewind, play/pause, stop, time readout
    ///   5. Scrub slider
    ///
    /// The playhead is drawn as a vertical line across the waveform and
    /// all curve lanes simultaneously.
    ///
    /// INTERACTION:
    ///   Click or drag anywhere on the waveform or curve area to scrub.
    ///   The caller receives the new time via the return value of Draw().
    ///
    /// CURVE RENDERING:
    ///   For each viseme, we sample the interpolated weight at regular
    ///   pixel intervals across the timeline and draw a filled polygon.
    ///   This mirrors the player's binary search + lerp exactly.
    ///   Read-only for MVP — no drag handles yet.
    /// </summary>
    public class CurveLaneDrawer
    {
        // ------------------------------------------------------------------
        // Layout constants
        // ------------------------------------------------------------------

        private const float WaveStripHeight = 32f;
        private const float RulerHeight = 16f;
        private const float ControlsHeight = 28f;
        private const float ScrubHeight = 18f;
        private const float LaneLabelWidth = 48f;
        private const float MinLaneHeight = 14f;
        private const float MaxLaneHeight = 28f;

        // ------------------------------------------------------------------
        // Colours
        // ------------------------------------------------------------------

        private static readonly Color BgColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color WaveBgColor = new Color(0.12f, 0.12f, 0.12f, 1f);
        private static readonly Color WaveColor = new Color(0.35f, 0.75f, 0.55f, 0.85f);
        private static readonly Color PlayheadColor = new Color(1f, 0.85f, 0.2f, 1f);
        private static readonly Color RulerColor = new Color(0.22f, 0.22f, 0.22f, 1f);
        private static readonly Color RulerTextColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color DividerColor = new Color(0.25f, 0.25f, 0.25f, 1f);
        private static readonly Color ScrubBgColor = new Color(0.12f, 0.12f, 0.12f, 1f);

        // Per-viseme curve colours — up to 9 visemes, wraps if more
        private static readonly Color[] CurveColors =
        {
            new Color(0.35f, 0.75f, 0.55f, 0.75f), // AA    — green
            new Color(0.4f,  0.65f, 1.0f,  0.75f), // OH    — blue
            new Color(1.0f,  0.6f,  0.3f,  0.75f), // EE    — orange
            new Color(0.85f, 0.4f,  0.7f,  0.75f), // OOH   — pink
            new Color(0.6f,  0.85f, 0.35f, 0.75f), // FF    — lime
            new Color(0.7f,  0.5f,  1.0f,  0.75f), // TH    — purple
            new Color(1.0f,  0.85f, 0.3f,  0.75f), // DD    — yellow
            new Color(0.4f,  0.9f,  0.9f,  0.75f), // MBP   — cyan
            new Color(0.75f, 0.75f, 0.75f, 0.60f), // REST  — grey
        };

        // ------------------------------------------------------------------
        // Waveform cache
        // ------------------------------------------------------------------

        private float[] _waveformPeaks;  // Pre-computed peak per pixel
        private AudioClip _cachedClip;
        private int _cachedPeakWidth;

        // ------------------------------------------------------------------
        // Curve cache
        // ------------------------------------------------------------------

        // Per-viseme sampled weight arrays [visemeIndex][pixelX]
        private float[][] _curveCache;
        private LipSyncData _cachedData;
        private int _cachedCurveWidth;
        private List<string> _visemeNames = new List<string>();

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Draws the full timeline section.
        /// Returns the new scrub time if the user interacted, otherwise
        /// returns currentTime unchanged.
        /// </summary>
        /// <param name="rect">Total rect allocated for this drawer.</param>
        /// <param name="data">Generated LipSyncData. May be null before generation.</param>
        /// <param name="clip">AudioClip. May be null.</param>
        /// <param name="currentTime">Current playback/scrub time in seconds.</param>
        /// <param name="isPlaying">True if currently playing.</param>
        /// <param name="onPlay">Callback: play button pressed.</param>
        /// <param name="onPause">Callback: pause button pressed.</param>
        /// <param name="onStop">Callback: stop button pressed.</param>
        /// <returns>New scrub time (may equal currentTime if no interaction).</returns>
        public float Draw(
            Rect rect,
            LipSyncData data,
            AudioClip clip,
            float currentTime,
            bool isPlaying,
            System.Action onPlay,
            System.Action onPause,
            System.Action onStop)
        {
            float duration = GetDuration(data, clip);
            float newTime = currentTime;

            EditorGUI.DrawRect(rect, BgColor);

            // ── Layout slices ─────────────────────────────────────────────
            float y = rect.y;

            Rect waveRect = new Rect(rect.x, y, rect.width, WaveStripHeight);
            y += WaveStripHeight;

            // Curve area — remaining space minus ruler, controls, scrub
            float curveHeight = rect.height
                - WaveStripHeight
                - RulerHeight
                - ControlsHeight
                - ScrubHeight
                - 4f; // padding

            curveHeight = Mathf.Max(curveHeight, MinLaneHeight * 3f);
            Rect curveRect = new Rect(rect.x, y, rect.width, curveHeight);
            y += curveHeight;

            Rect rulerRect = new Rect(rect.x, y, rect.width, RulerHeight);
            y += RulerHeight;

            Rect controlsRect = new Rect(rect.x, y, rect.width, ControlsHeight);
            y += ControlsHeight;

            Rect scrubRect = new Rect(rect.x, y, rect.width, ScrubHeight);

            // ── Draw sections ─────────────────────────────────────────────
            DrawWaveStrip(waveRect, clip, currentTime, duration);
            DrawCurveLanes(curveRect, data, currentTime, duration);
            DrawRuler(rulerRect, duration, currentTime);

            // Playhead across waveform + curves
            float normalised = duration > 0f ? currentTime / duration : 0f;
            DrawPlayhead(waveRect, normalised);
            DrawPlayhead(curveRect, normalised);

            // Scrub interaction — waveform and curve area are both hot
            Rect scrubZone = new Rect(rect.x, waveRect.y, rect.width, WaveStripHeight + curveHeight);
            newTime = HandleScrubInteraction(scrubZone, currentTime, duration);

            DrawControls(controlsRect, currentTime, duration, isPlaying,
                onPlay, onPause, onStop);
            DrawScrubSlider(scrubRect, currentTime, duration, ref newTime);

            return newTime;
        }

        /// <summary>
        /// Invalidates waveform and curve caches.
        /// Call when clip or generated data changes.
        /// </summary>
        public void Invalidate()
        {
            _cachedClip = null;
            _cachedData = null;
            _waveformPeaks = null;
            _curveCache = null;
            _cachedPeakWidth = 0;
            _cachedCurveWidth = 0;
            _visemeNames.Clear();
        }

        // ------------------------------------------------------------------
        // Waveform strip
        // ------------------------------------------------------------------

        private void DrawWaveStrip(Rect rect, AudioClip clip, float currentTime, float duration)
        {
            EditorGUI.DrawRect(rect, WaveBgColor);

            int width = Mathf.Max(1, (int)(rect.width - LaneLabelWidth));
            Rect drawRect = new Rect(rect.x + LaneLabelWidth, rect.y, width, rect.height);

            // Rebuild peak cache if needed
            if (clip != _cachedClip || width != _cachedPeakWidth)
                BuildWaveformCache(clip, width);

            if (_waveformPeaks != null)
            {
                int halfH = (int)(rect.height * 0.5f);
                float centreY = rect.y + halfH;

                for (int x = 0; x < _waveformPeaks.Length && x < width; x++)
                {
                    float peak = _waveformPeaks[x];
                    float barH = peak * halfH;
                    EditorGUI.DrawRect(
                        new Rect(drawRect.x + x, centreY - barH, 1f, barH * 2f),
                        WaveColor);
                }
            }

            // Label
            GUI.Label(
                new Rect(rect.x + 2f, rect.y, LaneLabelWidth - 4f, rect.height),
                "WAVE",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 8,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = RulerTextColor }
                });
        }

        private void BuildWaveformCache(AudioClip clip, int width)
        {
            _cachedClip = clip;
            _cachedPeakWidth = width;
            _waveformPeaks = null;

            if (clip == null) return;

            try
            {
                float[] samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0)) return;

                int totalSamples = clip.samples;
                int channels = clip.channels;
                _waveformPeaks = new float[width];

                for (int x = 0; x < width; x++)
                {
                    int start = (int)((float)x / width * totalSamples);
                    int end = Mathf.Min((int)((float)(x + 1) / width * totalSamples), totalSamples);
                    float peak = 0f;
                    for (int s = start; s < end; s++)
                        for (int c = 0; c < channels; c++)
                        {
                            int idx = s * channels + c;
                            if (idx < samples.Length)
                                peak = Mathf.Max(peak, Mathf.Abs(samples[idx]));
                        }
                    _waveformPeaks[x] = peak;
                }
            }
            catch { _waveformPeaks = null; }
        }

        // ------------------------------------------------------------------
        // Curve lanes
        // ------------------------------------------------------------------

        private void DrawCurveLanes(Rect rect, LipSyncData data, float currentTime, float duration)
        {
            EditorGUI.DrawRect(rect, BgColor);

            if (data == null || !data.IsValid)
            {
                DrawNoDataLabel(rect);
                return;
            }

            int width = Mathf.Max(1, (int)(rect.width - LaneLabelWidth));

            // Rebuild curve cache if needed
            if (data != _cachedData || width != _cachedCurveWidth)
                BuildCurveCache(data, width);

            if (_curveCache == null || _visemeNames.Count == 0) return;

            int laneCount = _visemeNames.Count;
            float laneHeight = Mathf.Clamp(rect.height / laneCount, MinLaneHeight, MaxLaneHeight);
            float totalH = laneHeight * laneCount;
            float startY = rect.y + (rect.height - totalH) * 0.5f;

            for (int v = 0; v < laneCount && v < _curveCache.Length; v++)
            {
                float laneY = startY + v * laneHeight;
                Rect laneRect = new Rect(rect.x + LaneLabelWidth, laneY, width, laneHeight);

                // Lane background — subtle alternating
                Color laneBg = v % 2 == 0
                    ? new Color(0.16f, 0.16f, 0.16f, 1f)
                    : new Color(0.18f, 0.18f, 0.18f, 1f);
                EditorGUI.DrawRect(laneRect, laneBg);

                // Filled area curve
                Color curveColor = CurveColors[v % CurveColors.Length];
                DrawFilledCurve(laneRect, _curveCache[v], curveColor);

                // Lane label
                Rect labelRect = new Rect(rect.x + 2f, laneY, LaneLabelWidth - 6f, laneHeight);
                GUI.Label(labelRect, _visemeNames[v],
                    new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        fontSize = 8,
                        normal = { textColor = curveColor + new Color(0.2f, 0.2f, 0.2f, 0f) }
                    });

                // Lane divider
                EditorGUI.DrawRect(
                    new Rect(rect.x, laneY + laneHeight - 1f, rect.width, 1f),
                    DividerColor);
            }
        }

        /// <summary>
        /// Draws a filled area graph from bottom of lane to curve height.
        /// </summary>
        private void DrawFilledCurve(Rect laneRect, float[] weights, Color color)
        {
            if (weights == null) return;

            float usableH = laneRect.height - 2f;
            float baseY = laneRect.y + laneRect.height - 1f;

            for (int x = 0; x < weights.Length && x < (int)laneRect.width; x++)
            {
                float fillH = Mathf.Clamp01(weights[x]) * usableH;
                if (fillH < 0.5f) continue;

                EditorGUI.DrawRect(
                    new Rect(laneRect.x + x, baseY - fillH, 1f, fillH),
                    color);
            }
        }

        private void BuildCurveCache(LipSyncData data, int width)
        {
            _cachedData = data;
            _cachedCurveWidth = width;
            _curveCache = null;
            _visemeNames.Clear();

            if (data == null || !data.IsValid || data.frames.Count == 0) return;

            // Collect ordered viseme names from first frame
            // (all frames should have the same set after full distribution)
            var nameSet = new List<string>();
            foreach (var vw in data.frames[0].weights)
                if (!nameSet.Contains(vw.visemeName))
                    nameSet.Add(vw.visemeName);

            _visemeNames = nameSet;
            int vCount = nameSet.Count;
            _curveCache = new float[vCount][];
            for (int v = 0; v < vCount; v++)
                _curveCache[v] = new float[width];

            // Sample interpolated weight at each pixel position
            float duration = data.duration;
            if (duration <= 0f) return;

            for (int x = 0; x < width; x++)
            {
                float t = (float)x / (width - 1) * duration;
                var weights = SampleAt(data, t);

                for (int v = 0; v < vCount; v++)
                {
                    weights.TryGetValue(nameSet[v], out float w);
                    _curveCache[v][x] = w;
                }
            }
        }

        // ------------------------------------------------------------------
        // Ruler
        // ------------------------------------------------------------------

        private void DrawRuler(Rect rect, float duration, float currentTime)
        {
            EditorGUI.DrawRect(rect, RulerColor);

            if (duration <= 0f) return;

            float usableW = rect.width - LaneLabelWidth;
            float tickIntervalSec = ChooseTickInterval(duration, usableW);
            float t = 0f;

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 8,
                normal = { textColor = RulerTextColor },
                alignment = TextAnchor.UpperLeft,
            };

            while (t <= duration + 0.001f)
            {
                float x = rect.x + LaneLabelWidth + (t / duration) * usableW;
                EditorGUI.DrawRect(new Rect(x, rect.y, 1f, rect.height * 0.5f), DividerColor);

                string label = t < 60f
                    ? $"{t:F1}s"
                    : $"{Mathf.FloorToInt(t / 60f)}:{(t % 60f):00}";

                GUI.Label(new Rect(x + 2f, rect.y, 36f, rect.height), label, labelStyle);
                t += tickIntervalSec;
            }
        }

        private float ChooseTickInterval(float duration, float pixelWidth)
        {
            // Aim for one tick every ~60 pixels
            float[] candidates = { 0.5f, 1f, 2f, 5f, 10f, 30f, 60f };
            foreach (float c in candidates)
            {
                float ticksCount = duration / c;
                if (pixelWidth / ticksCount >= 50f)
                    return c;
            }
            return 60f;
        }

        // ------------------------------------------------------------------
        // Playback controls
        // ------------------------------------------------------------------

        private void DrawControls(
            Rect rect,
            float currentTime,
            float duration,
            bool isPlaying,
            System.Action onPlay,
            System.Action onPause,
            System.Action onStop)
        {
            EditorGUI.DrawRect(rect, ScrubBgColor);

            float btnW = 30f;
            float btnH = 20f;
            float btnY = rect.y + (rect.height - btnH) * 0.5f;
            float x = rect.x + LaneLabelWidth + 4f;

            var btnStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            // Rewind
            if (GUI.Button(new Rect(x, btnY, btnW, btnH), "◀◀", btnStyle))
                onStop?.Invoke();
            x += btnW + 2f;

            // Play / Pause
            string playLabel = isPlaying ? "⏸" : "▶";
            if (GUI.Button(new Rect(x, btnY, btnW, btnH), playLabel, btnStyle))
            {
                if (isPlaying) onPause?.Invoke();
                else onPlay?.Invoke();
            }
            x += btnW + 2f;

            // Stop
            if (GUI.Button(new Rect(x, btnY, btnW, btnH), "■", btnStyle))
                onStop?.Invoke();
            x += btnW + 8f;

            // Time readout
            string timeStr = LipSyncGenerationSession.FormatTime(currentTime) +
                             " / " +
                             LipSyncGenerationSession.FormatTime(duration);
            GUI.Label(
                new Rect(x, btnY, 100f, btnH),
                timeStr,
                new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = RulerTextColor },
                    alignment = TextAnchor.MiddleLeft,
                });
        }

        private void DrawScrubSlider(Rect rect, float currentTime, float duration, ref float newTime)
        {
            EditorGUI.DrawRect(rect, ScrubBgColor);

            float sliderX = rect.x + LaneLabelWidth + 4f;
            float sliderW = rect.width - LaneLabelWidth - 8f;

            if (sliderW < 10f) return;

            EditorGUI.BeginChangeCheck();
            float result = GUI.HorizontalSlider(
                new Rect(sliderX, rect.y + 4f, sliderW, rect.height - 8f),
                currentTime, 0f, Mathf.Max(duration, 0.01f));

            if (EditorGUI.EndChangeCheck())
                newTime = result;
        }

        // ------------------------------------------------------------------
        // Scrub interaction
        // ------------------------------------------------------------------

        private float HandleScrubInteraction(Rect rect, float currentTime, float duration)
        {
            Event e = Event.current;
            bool inRect = rect.Contains(e.mousePosition);

            if (inRect && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                float usableX = rect.x + LaneLabelWidth;
                float usableW = rect.width - LaneLabelWidth;
                float normalised = Mathf.Clamp01(
                    (e.mousePosition.x - usableX) / usableW);
                e.Use();
                return normalised * duration;
            }

            return currentTime;
        }

        // ------------------------------------------------------------------
        // Playhead
        // ------------------------------------------------------------------

        private void DrawPlayhead(Rect rect, float normalised)
        {
            float usableX = rect.x + LaneLabelWidth;
            float usableW = rect.width - LaneLabelWidth;
            float x = usableX + normalised * usableW;
            EditorGUI.DrawRect(
                new Rect(x - 1f, rect.y, 2f, rect.height),
                PlayheadColor);
        }

        // ------------------------------------------------------------------
        // Interpolation — mirrors player logic
        // ------------------------------------------------------------------

        private Dictionary<string, float> SampleAt(LipSyncData data, float time)
        {
            var result = new Dictionary<string, float>(
                System.StringComparer.OrdinalIgnoreCase);

            var frames = data.frames;
            if (frames == null || frames.Count == 0) return result;

            int lo = 0, hi = frames.Count - 1;

            if (time <= frames[lo].time)
                return FrameToDict(frames[lo]);

            if (time >= frames[hi].time)
                return FrameToDict(frames[hi]);

            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].time <= time) lo = mid;
                else hi = mid;
            }

            float span = frames[hi].time - frames[lo].time;
            float t = span > Mathf.Epsilon
                ? (time - frames[lo].time) / span
                : 1f;

            foreach (var vw in frames[lo].weights)
            {
                result.TryGetValue(vw.visemeName, out float e);
                result[vw.visemeName] = e + vw.weight * (1f - t);
            }
            foreach (var vw in frames[hi].weights)
            {
                result.TryGetValue(vw.visemeName, out float e);
                result[vw.visemeName] = e + vw.weight * t;
            }

            return result;
        }

        private Dictionary<string, float> FrameToDict(VisemeFrame frame)
        {
            var d = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var vw in frame.weights)
                d[vw.visemeName] = vw.weight;
            return d;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private float GetDuration(LipSyncData data, AudioClip clip)
        {
            if (data != null && data.IsValid) return data.duration;
            if (clip != null) return clip.length;
            return 1f;
        }

        private void DrawNoDataLabel(Rect rect)
        {
            GUI.Label(rect,
                "Generate Lip Sync to see curve lanes.",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                });
        }
    }
}