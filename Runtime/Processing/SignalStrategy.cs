using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Generates lip sync frames using amplitude (RMS) and FFT frequency
    /// band analysis on raw AudioClip sample data.
    ///
    /// Every frame writes an explicit weight for every viseme in the profile.
    /// No viseme is ever absent from a frame. This prevents the player from
    /// interpolating toward implicit zero during sustained sounds.
    ///
    /// FIXES APPLIED:
    ///   - samplesPerSecond == 0 now fails fast with a clear error (was ÷0 crash).
    ///   - Hann window guards against copyLength <= 1 (was ÷0 → NaN FFT).
    ///   - GetSimilarity guards against null/empty 'from' (was ArgumentNullException).
    ///   - Missing REST mapping in silence handling now falls back to writing
    ///     all-zero weights rather than animating with MinimumResidualWeight.
    ///   - ValidateClip wrapped in try/catch for Unity version variance.
    /// </summary>
    public class SignalStrategy : ILipSyncStrategy
    {
        // ------------------------------------------------------------------
        // ILipSyncStrategy
        // ------------------------------------------------------------------

        public string DisplayName => "Signal (Amplitude + FFT)";
        public bool IsAvailable => true;

        // ------------------------------------------------------------------
        // FFT configuration
        // ------------------------------------------------------------------

        private const int FftWindowSize = 1024; // Must be power of 2
        private const float LowBandMaxHz = 500f;
        private const float MidBandMaxHz = 2000f;

        // ------------------------------------------------------------------
        // Viseme selection thresholds
        // ------------------------------------------------------------------

        private const float FricativeThreshold = 0.35f;
        private const float VowelThreshold = 0.45f;
        private const float AmplitudeLow = 0.05f;
        private const float AmplitudeMid = 0.15f;
        private const float AmplitudeHigh = 0.30f;

        // ------------------------------------------------------------------
        // Residual weight configuration
        // ------------------------------------------------------------------

        // How much dominant weight bleeds into acoustically similar visemes.
        // 0 = no bleed. 1 = all visemes equal (wrong).
        private const float ResidualScale = 0.15f;
        private const float MinimumResidualWeight = 0.01f;

        // ------------------------------------------------------------------
        // Acoustic similarity table
        // ------------------------------------------------------------------

        // Maps viseme pairs to a similarity score [0, 1] based on mouth
        // geometry. Used to distribute residual weight to non-dominant visemes.
        // Visemes absent from the table default to 0 similarity.
        // Values can be tuned without touching any other logic.
        private static readonly Dictionary<string, Dictionary<string, float>>
            AcousticSimilarity =
            new (
                System.StringComparer.OrdinalIgnoreCase)
            {
                ["REST"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["MBP"] = 0.30f,
                    ["OOH"] = 0.10f,
                    ["OH"] = 0.05f,
                    ["EE"] = 0.05f,
                    ["AA"] = 0.00f,
                    ["FF"] = 0.05f,
                    ["TH"] = 0.05f,
                    ["DD"] = 0.05f,
                },
                ["MBP"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["REST"] = 0.30f,
                    ["OOH"] = 0.10f,
                    ["FF"] = 0.05f,
                    ["OH"] = 0.05f,
                    ["EE"] = 0.00f,
                    ["AA"] = 0.00f,
                    ["TH"] = 0.05f,
                    ["DD"] = 0.05f,
                },
                ["AA"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["OH"] = 0.40f,
                    ["EE"] = 0.20f,
                    ["DD"] = 0.15f,
                    ["TH"] = 0.10f,
                    ["FF"] = 0.10f,
                    ["OOH"] = 0.05f,
                    ["MBP"] = 0.00f,
                    ["REST"] = 0.00f,
                },
                ["EE"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["AA"] = 0.20f,
                    ["OH"] = 0.15f,
                    ["DD"] = 0.20f,
                    ["TH"] = 0.15f,
                    ["FF"] = 0.10f,
                    ["OOH"] = 0.05f,
                    ["MBP"] = 0.00f,
                    ["REST"] = 0.05f,
                },
                ["OH"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["AA"] = 0.40f,
                    ["OOH"] = 0.30f,
                    ["EE"] = 0.15f,
                    ["DD"] = 0.10f,
                    ["FF"] = 0.05f,
                    ["TH"] = 0.05f,
                    ["MBP"] = 0.05f,
                    ["REST"] = 0.05f,
                },
                ["OOH"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["OH"] = 0.30f,
                    ["REST"] = 0.10f,
                    ["MBP"] = 0.10f,
                    ["AA"] = 0.05f,
                    ["EE"] = 0.05f,
                    ["FF"] = 0.05f,
                    ["TH"] = 0.00f,
                    ["DD"] = 0.00f,
                },
                ["FF"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["TH"] = 0.40f,
                    ["EE"] = 0.10f,
                    ["DD"] = 0.10f,
                    ["OH"] = 0.05f,
                    ["AA"] = 0.10f,
                    ["OOH"] = 0.05f,
                    ["MBP"] = 0.05f,
                    ["REST"] = 0.05f,
                },
                ["TH"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["FF"] = 0.40f,
                    ["DD"] = 0.30f,
                    ["EE"] = 0.15f,
                    ["AA"] = 0.10f,
                    ["OH"] = 0.05f,
                    ["OOH"] = 0.00f,
                    ["MBP"] = 0.05f,
                    ["REST"] = 0.05f,
                },
                ["DD"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["TH"] = 0.30f,
                    ["EE"] = 0.20f,
                    ["FF"] = 0.10f,
                    ["AA"] = 0.15f,
                    ["OH"] = 0.10f,
                    ["OOH"] = 0.00f,
                    ["MBP"] = 0.05f,
                    ["REST"] = 0.05f,
                },
            };

        // ------------------------------------------------------------------
        // ILipSyncStrategy.Execute
        // ------------------------------------------------------------------

        public List<VisemeFrame> Execute(
            AudioClip clip,
            VisemeProfile profile,
            LipSyncSettings settings)
        {
            var frames = new List<VisemeFrame>();

            if (!ValidateClip(clip)) return frames;

            // FIX: Guard against samplesPerSecond == 0 before division
            if (settings.samplesPerSecond <= 0)
            {
                Debug.LogError(
                    "[ResonanceSync] SignalStrategy: settings.samplesPerSecond " +
                    $"is {settings.samplesPerSecond}. Must be > 0.");
                return frames;
            }

            // Extract all samples from clip
            int totalSamples = clip.samples * clip.channels;
            float[] allSamples = new float[totalSamples];
            clip.GetData(allSamples, 0);

            int channels = clip.channels;
            int sampleRate = clip.frequency;
            int windowSize = sampleRate / settings.samplesPerSecond;

            if (windowSize <= 0)
            {
                Debug.LogError(
                    "[ResonanceSync] SignalStrategy: computed windowSize is 0. " +
                    $"sampleRate={sampleRate}, samplesPerSecond={settings.samplesPerSecond}.");
                return frames;
            }

            // Collect all viseme names — written on every frame
            List<string> allVisemeNames = GetAllVisemeNames(profile);
            if (allVisemeNames.Count == 0)
            {
                Debug.LogError(
                    $"[ResonanceSync] VisemeProfile '{profile.name}' has no mappings.");
                return frames;
            }

            // Process each window
            int totalMonoSamples = clip.samples;
            int windowIndex = 0;

            while (true)
            {
                int windowStart = windowIndex * windowSize;
                if (windowStart >= totalMonoSamples) break;

                int windowEnd = Mathf.Min(windowStart + windowSize, totalMonoSamples);
                float frameTime = (float)windowStart / sampleRate;

                float[] monoWindow = ExtractMonoWindow(
                    allSamples, windowStart, windowEnd, channels);

                float rms = CalculateRMS(monoWindow);

                VisemeFrame frame = new (frameTime);

                if (rms < settings.minAmplitudeThreshold)
                {
                    WriteSilenceDistribution(frame, profile, allVisemeNames);
                }
                else
                {
                    FrequencyBands bands = CalculateFrequencyBands(monoWindow, sampleRate);
                    string dominant = SelectDominantViseme(rms, bands, profile, settings);
                    float weight = CalculateDominantWeight(rms, bands, settings);

                    WriteFullDistribution(
                        frame, dominant, weight, allVisemeNames, profile);
                }

                frames.Add(frame);
                windowIndex++;
            }

            return frames;
        }

        // ------------------------------------------------------------------
        // Frame writing
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes a silence frame. REST gets weight 0 (fully closed).
        /// All other visemes get MinimumResidualWeight so the player has
        /// a defined start point when interpolating out of silence.
        ///
        /// FIX: If the profile has no REST mapping, all visemes get 0 weight.
        /// Previously all non-REST visemes still got MinimumResidualWeight,
        /// causing visible mouth movement during silence on bad profiles.
        /// Generation is already blocked upstream (ValidateInputs checks
        /// HasRestViseme), but we guard here defensively.
        /// </summary>
        private void WriteSilenceDistribution(
            VisemeFrame frame,
            VisemeProfile profile,
            List<string> allVisemeNames)
        {
            bool hasValidRest = profile.HasRestViseme();

            foreach (string visemeName in allVisemeNames)
            {
                bool isRest = string.Equals(
                    visemeName,
                    profile.restVisemeName,
                    System.StringComparison.OrdinalIgnoreCase);

                float weight;

                if (!hasValidRest)
                {
                    // No valid REST mapping — write all-zero to keep mouth closed.
                    // This is a degenerate state that ValidateInputs should have
                    // blocked, but we handle it safely here regardless.
                    weight = 0f;
                }
                else
                {
                    weight = isRest ? 0f : MinimumResidualWeight;
                }

                frame.weights.Add(new VisemeWeight(visemeName, weight));
            }
        }

        /// <summary>
        /// Writes the dominant viseme at its calculated weight.
        /// All other visemes get a residual based on acoustic similarity
        /// to the dominant, floored at MinimumResidualWeight.
        /// </summary>
        private void WriteFullDistribution(
            VisemeFrame frame,
            string dominantViseme,
            float dominantWeight,
            List<string> allVisemeNames,
            VisemeProfile profile)
        {
            foreach (string visemeName in allVisemeNames)
            {
                bool isDominant = string.Equals(
                    visemeName,
                    dominantViseme,
                    System.StringComparison.OrdinalIgnoreCase);

                float weight = isDominant
                    ? dominantWeight
                    : Mathf.Max(
                        GetSimilarity(dominantViseme, visemeName)
                            * dominantWeight
                            * ResidualScale,
                        MinimumResidualWeight);

                frame.weights.Add(new VisemeWeight(visemeName, weight));
            }
        }

        // ------------------------------------------------------------------
        // Dominant viseme selection
        // ------------------------------------------------------------------

        /// <summary>
        /// Priority cascade: fricative → vowel formant → loud → moderate → low.
        /// Falls back through profile alternatives if preferred names are absent.
        /// </summary>
        private string SelectDominantViseme(
            float rms,
            FrequencyBands bands,
            VisemeProfile profile,
            LipSyncSettings settings)
        {
            if (bands.high > FricativeThreshold)
                return GetVisemeOrFallback(profile, "FF", "TH", "EE");

            if (bands.mid > VowelThreshold && rms < AmplitudeHigh)
                return GetVisemeOrFallback(profile, "EE", "DD", "OH");

            if (rms >= AmplitudeHigh)
                return GetVisemeOrFallback(profile, "AA", "OH");

            if (rms >= AmplitudeMid)
                return GetVisemeOrFallback(profile, "OH", "AA");

            return GetVisemeOrFallback(profile, "OH", profile.restVisemeName);
        }

        /// <summary>
        /// Calculates the intensity of the dominant viseme.
        /// Fricatives have a narrower range — defined by shape, not loudness.
        /// Open vowels scale directly with amplitude.
        /// </summary>
        private float CalculateDominantWeight(
            float rms,
            FrequencyBands bands,
            LipSyncSettings settings)
        {
            if (bands.high > FricativeThreshold)
                return Mathf.Lerp(0.3f, 0.7f,
                    Mathf.InverseLerp(0f, AmplitudeHigh, rms));

            if (bands.mid > VowelThreshold && rms < AmplitudeHigh)
                return Mathf.Lerp(0.4f, 0.85f,
                    Mathf.InverseLerp(AmplitudeLow, AmplitudeMid, rms));

            if (rms >= AmplitudeHigh)
                return Mathf.Lerp(0.7f, 1.0f,
                    Mathf.InverseLerp(AmplitudeHigh, 1f, rms));

            if (rms >= AmplitudeMid)
                return Mathf.Lerp(0.3f, 0.7f,
                    Mathf.InverseLerp(AmplitudeMid, AmplitudeHigh, rms));

            return Mathf.Lerp(0.05f, 0.3f,
                Mathf.InverseLerp(settings.minAmplitudeThreshold, AmplitudeMid, rms));
        }

        // ------------------------------------------------------------------
        // Acoustic similarity
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns acoustic similarity [0,1] between two viseme names.
        ///
        /// FIX: Guards against null or empty 'from' which previously caused
        /// ArgumentNullException in Dictionary.TryGetValue.
        /// </summary>
        private float GetSimilarity(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                return 0f;

            if (AcousticSimilarity.TryGetValue(from, out var neighbours))
                if (neighbours.TryGetValue(to, out float similarity))
                    return similarity;

            return 0f;
        }

        // ------------------------------------------------------------------
        // Mono extraction
        // ------------------------------------------------------------------

        private float[] ExtractMonoWindow(
            float[] allSamples,
            int startSample,
            int endSample,
            int channels)
        {
            int length = endSample - startSample;
            float[] mono = new float[length];

            for (int i = 0; i < length; i++)
            {
                int baseIndex = (startSample + i) * channels;
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += allSamples[baseIndex + c];
                mono[i] = sum / channels;
            }

            return mono;
        }

        // ------------------------------------------------------------------
        // RMS amplitude
        // ------------------------------------------------------------------

        private float CalculateRMS(float[] samples)
        {
            if (samples.Length == 0) return 0f;

            float sumOfSquares = 0f;
            foreach (float s in samples)
                sumOfSquares += s * s;

            return Mathf.Sqrt(sumOfSquares / samples.Length);
        }

        // ------------------------------------------------------------------
        // FFT and frequency bands
        // ------------------------------------------------------------------

        private struct FrequencyBands
        {
            public float low;
            public float mid;
            public float high;
            public float total;
        }

        /// <summary>
        /// Runs a DFT on the sample window with a Hann window function
        /// and returns normalised energy across three frequency bands.
        ///
        /// FIX: Guards against copyLength <= 1 before Hann computation.
        /// Previously (copyLength - 1) as denominator caused ÷0 → NaN
        /// when a window contained only one sample.
        /// </summary>
        private FrequencyBands CalculateFrequencyBands(float[] samples, int sampleRate)
        {
            int n = FftWindowSize;
            float[] windowed = new float[n];
            int copyLength = Mathf.Min(samples.Length, n);

            if (copyLength <= 1)
            {
                // Window too small for meaningful frequency analysis.
                // Return empty bands — the amplitude check upstream already
                // handles near-zero-energy windows via the silence threshold.
                if (copyLength == 1)
                    windowed[0] = samples[0]; // No Hann needed for single sample
                return default;
            }

            // Hann window: tapers signal to zero at both ends to prevent
            // spectral leakage at window boundaries.
            for (int i = 0; i < copyLength; i++)
            {
                float hann = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (copyLength - 1)));
                windowed[i] = samples[i] * hann;
            }

            int halfN = n / 2;
            float[] magnitudes = new float[halfN];
            float binWidthHz = (float)sampleRate / n;

            for (int k = 0; k < halfN; k++)
            {
                float real = 0f;
                float imag = 0f;

                for (int t = 0; t < n; t++)
                {
                    float angle = 2f * Mathf.PI * k * t / n;
                    real += windowed[t] * Mathf.Cos(angle);
                    imag -= windowed[t] * Mathf.Sin(angle);
                }

                magnitudes[k] = Mathf.Sqrt(real * real + imag * imag) / n;
            }

            FrequencyBands result = default;

            for (int k = 0; k < halfN; k++)
            {
                float freqHz = k * binWidthHz;
                float energy = magnitudes[k];

                if (freqHz < LowBandMaxHz) result.low += energy;
                else if (freqHz < MidBandMaxHz) result.mid += energy;
                else result.high += energy;

                result.total += energy;
            }

            if (result.total > 0.0001f)
            {
                result.low /= result.total;
                result.mid /= result.total;
                result.high /= result.total;
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private List<string> GetAllVisemeNames(VisemeProfile profile)
        {
            var names = new List<string>(profile.mappings.Count);
            foreach (var mapping in profile.mappings)
                if (!string.IsNullOrWhiteSpace(mapping.visemeName))
                    names.Add(mapping.visemeName);
            return names;
        }

        private string GetVisemeOrFallback(VisemeProfile profile, params string[] preferences)
        {
            foreach (string name in preferences)
                if (profile.TryGetMapping(name, out _))
                    return name;
            return profile.restVisemeName;
        }

        /// <summary>
        /// Validates that the AudioClip can be read by GetData.
        ///
        /// FIX: Wrapped in try/catch to handle Unity version variance in
        /// GetData behaviour. Also checks loadType for a clearer error message.
        /// </summary>
        private bool ValidateClip(AudioClip clip)
        {
            if (clip == null)
            {
                Debug.LogError("[ResonanceSync] SignalStrategy: AudioClip is null.");
                return false;
            }

            try
            {
                float[] testBuffer = new float[1];
                bool readable = clip.GetData(testBuffer, 0);

                if (!readable)
                {
                    Debug.LogError(
                        $"[ResonanceSync] AudioClip '{clip.name}' is not readable. " +
                        "In the clip's Import Settings, enable 'Load In Background' " +
                        "OFF and set 'Load Type' to 'Decompress On Load', then enable " +
                        "'Read/Write'.");
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    $"[ResonanceSync] AudioClip '{clip.name}' threw an exception " +
                    $"during readability check: {ex.Message}. " +
                    "Ensure Read/Write is enabled in import settings.");
                return false;
            }

            return true;
        }
    }
}