using UnityEngine;
using UnityEditor;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Draws a waveform strip for an AudioClip inside an editor window rect.
    ///
    /// CACHING:
    ///   The waveform is rasterised into a Texture2D once per clip and cached.
    ///   The texture is only rebuilt when the clip reference changes.
    ///   This is necessary because GetData on a long clip is expensive and
    ///   should never run every OnGUI repaint.
    ///
    /// RENDERING:
    ///   Each column of pixels represents the peak amplitude of the audio
    ///   samples that map to that horizontal position in the rect. The
    ///   waveform is centred vertically. A playhead line overlays the texture
    ///   at the current playback time.
    /// </summary>
    public class WaveformDrawer
    {
        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        private const int TextureHeight = 64;
        private const float WaveformAlpha = 0.85f;

        // Waveform fill colour — mid green-grey, readable on dark backgrounds
        private static readonly Color WaveColor = new Color(0.35f, 0.75f, 0.55f, WaveformAlpha);
        private static readonly Color BackgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color PlayheadColor = new Color(1f, 0.85f, 0.2f, 1f);
        private static readonly Color SilenceColor = new Color(0.25f, 0.25f, 0.25f, 1f);

        // ------------------------------------------------------------------
        // Cache
        // ------------------------------------------------------------------

        private Texture2D _waveformTexture;
        private AudioClip _cachedClip;
        private int _cachedWidth;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Draws the waveform into the given rect.
        /// Rebuilds the cached texture if the clip or rect width has changed.
        /// Overlays a playhead at the normalised position [0, 1].
        /// </summary>
        /// <param name="rect">The rect to draw into.</param>
        /// <param name="clip">The AudioClip to visualise.</param>
        /// <param name="normalizedTime">
        /// Playhead position as a fraction of total duration [0, 1].
        /// </param>
        public void Draw(Rect rect, AudioClip clip, float normalizedTime)
        {
            int width = Mathf.Max(1, (int)rect.width);

            // Rebuild texture if clip or width changed
            if (clip != _cachedClip || width != _cachedWidth)
                Rebuild(clip, width);

            // Draw waveform texture
            if (_waveformTexture != null)
                GUI.DrawTexture(rect, _waveformTexture, ScaleMode.StretchToFill);
            else
                EditorGUI.DrawRect(rect, BackgroundColor);

            // Draw playhead
            DrawPlayhead(rect, normalizedTime);

            // Draw border
            DrawBorder(rect);
        }

        /// <summary>
        /// Clears the cached texture, forcing a rebuild on next Draw().
        /// Call when the clip changes externally.
        /// </summary>
        public void Invalidate()
        {
            if (_waveformTexture != null)
            {
                Object.DestroyImmediate(_waveformTexture);
                _waveformTexture = null;
            }
            _cachedClip = null;
            _cachedWidth = 0;
        }

        // ------------------------------------------------------------------
        // Texture construction
        // ------------------------------------------------------------------

        private void Rebuild(AudioClip clip, int width)
        {
            // Clean up previous texture
            if (_waveformTexture != null)
                Object.DestroyImmediate(_waveformTexture);

            _cachedClip = clip;
            _cachedWidth = width;

            if (clip == null)
            {
                _waveformTexture = null;
                return;
            }

            // Attempt to read samples — clip must have Read/Write enabled
            float[] samples = null;
            try
            {
                samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0))
                    samples = null;
            }
            catch
            {
                samples = null;
            }

            _waveformTexture = new Texture2D(width, TextureHeight, TextureFormat.RGBA32, false)
            {
                name = $"Waveform_{clip.name}",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = (TextureWrapMode)WrapMode.Clamp,
            };

            Color[] pixels = new Color[width * TextureHeight];

            // Fill background
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = BackgroundColor;

            if (samples != null)
            {
                int totalSamples = clip.samples; // per channel
                int channels = clip.channels;
                int halfH = TextureHeight / 2;

                for (int x = 0; x < width; x++)
                {
                    // Map this pixel column to a range of samples
                    int sampleStart = (int)((float)x / width * totalSamples);
                    int sampleEnd = (int)((float)(x + 1) / width * totalSamples);
                    sampleEnd = Mathf.Min(sampleEnd, totalSamples);

                    float peak = 0f;
                    for (int s = sampleStart; s < sampleEnd; s++)
                    {
                        // Mix channels: use max abs across all channels per sample
                        float maxCh = 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            int idx = s * channels + c;
                            if (idx < samples.Length)
                                maxCh = Mathf.Max(maxCh, Mathf.Abs(samples[idx]));
                        }
                        peak = Mathf.Max(peak, maxCh);
                    }

                    // Draw vertical bar proportional to peak
                    int barHeight = Mathf.RoundToInt(peak * halfH);
                    Color col = barHeight > 0 ? WaveColor : SilenceColor;

                    for (int y = halfH - barHeight; y <= halfH + barHeight; y++)
                    {
                        if (y >= 0 && y < TextureHeight)
                            pixels[y * width + x] = col;
                    }
                }
            }

            _waveformTexture.SetPixels(pixels);
            _waveformTexture.Apply();
        }

        // ------------------------------------------------------------------
        // Overlay drawing
        // ------------------------------------------------------------------

        private void DrawPlayhead(Rect rect, float normalizedTime)
        {
            float x = rect.x + normalizedTime * rect.width;
            EditorGUI.DrawRect(
                new Rect(x - 1f, rect.y, 2f, rect.height),
                PlayheadColor);
        }

        private void DrawBorder(Rect rect)
        {
            float t = 1f;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, t), Color.black);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - t, rect.width, t), Color.black);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, t, rect.height), Color.black);
            EditorGUI.DrawRect(new Rect(rect.x + rect.width - t, rect.y, t, rect.height), Color.black);
        }
    }
}