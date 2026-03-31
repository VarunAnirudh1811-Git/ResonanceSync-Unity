using System.Collections.Generic;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Generates lip sync frames using amplitude (RMS) and FFT frequency
    /// band analysis on raw AudioClip sample data.
    ///
    /// APPROACH:
    ///   The clip is divided into equal time windows based on samplesPerSecond.
    ///   For each window we calculate RMS amplitude and FFT frequency band
    ///   distribution, select a dominant viseme, then write a FULL weight
    ///   distribution across every viseme in the profile.
    ///
    /// IMPORTANT — FULL FRAME COVERAGE:
    ///   Every frame writes an explicit weight for every viseme in the profile.
    ///   No viseme is ever absent from a frame. This prevents the player from
    ///   interpolating toward implicit zero during sustained sounds, which
    ///   would cause visible mouth flutter or stutter.
    ///
    /// OUTPUT:
    ///   Raw VisemeFrames, one per analysis window, fully populated.
    ///   Frames are unsmoothed and unnormalised — LipSyncProcessor handles that.
    ///
    /// REQUIREMENTS:
    ///   AudioClip must have Read/Write enabled in import settings.
    ///   Editor-only operation — never called at runtime.
    /// </summary>
    public class SignalStrategy : ILipSyncStrategy
    {
        // ------------------------------------------------------------------
        // ILipSyncStrategy
        // ------------------------------------------------------------------

        public string DisplayName => "Signal (Amplitude + FFT)";
        public bool IsAvailable   => true;

        // ------------------------------------------------------------------
        // FFT configuration
        // ------------------------------------------------------------------

        // Must be a power of 2. 1024 gives good frequency resolution.
        // At 44100 Hz, each bin covers ~43 Hz.
        private const int FftWindowSize = 1024;

        // Frequency band boundaries in Hz
        private const float LowBandMaxHz = 500f;
        private const float MidBandMaxHz = 2000f;

        // ------------------------------------------------------------------
        // Viseme selection thresholds
        // ------------------------------------------------------------------

        private const float FricativeThreshold = 0.35f;
        private const float VowelThreshold     = 0.45f;

        private const float AmplitudeLow  = 0.05f;
        private const float AmplitudeMid  = 0.15f;
        private const float AmplitudeHigh = 0.30f;

        // ------------------------------------------------------------------
        // Residual weight configuration
        // ------------------------------------------------------------------

        // How much of the dominant weight bleeds into acoustically similar
        // visemes. Keeps residuals subtle — they influence interpolation
        // without fighting the dominant shape.
        // Range: 0 (no bleed) to 1 (full bleed — would make all shapes equal)
        private const float ResidualScale = 0.15f;

        // Minimum weight written for any viseme in any non-silence frame.
        // Ensures no viseme is ever fully absent from a frame.
        // Low enough to be invisible on the mesh but present for interpolation.
        private const float MinimumResidualWeight = 0.01f;

        // ------------------------------------------------------------------
        // Acoustic similarity table
        // ------------------------------------------------------------------

        // Defines how similar each viseme is to each other in terms of
        // mouth position. Used to distribute residual weight to non-dominant
        // visemes — similar shapes get more bleed, dissimilar shapes get less.
        //
        // Values are informed approximations based on mouth shape geometry:
        //   1.0 = identical shape
        //   0.0 = completely different shape
        //
        // This table is symmetric: similarity(A,B) == similarity(B,A).
        // Visemes not present in this table default to 0 similarity,
        // meaning they receive only MinimumResidualWeight.
        // These values can be tuned without changing any other logic.
        private static readonly Dictionary<string, Dictionary<string, float>>
            AcousticSimilarity =
            new Dictionary<string, Dictionary<string, float>>(
                System.StringComparer.OrdinalIgnoreCase)
            {
                ["REST"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["MBP"] = 0.30f,
                    ["OOH"] = 0.10f,
                    ["OH"]  = 0.05f,
                    ["EE"]  = 0.05f,
                    ["AA"]  = 0.00f,
                    ["FF"]  = 0.05f,
                    ["TH"]  = 0.05f,
                    ["DD"]  = 0.05f,
                },
                ["MBP"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["REST"] = 0.30f,
                    ["OOH"]  = 0.10f,
                    ["FF"]   = 0.05f,
                    ["OH"]   = 0.05f,
                    ["EE"]   = 0.00f,
                    ["AA"]   = 0.00f,
                    ["TH"]   = 0.05f,
                    ["DD"]   = 0.05f,
                },
                ["AA"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["OH"]   = 0.40f,
                    ["EE"]   = 0.20f,
                    ["DD"]   = 0.15f,
                    ["TH"]   = 0.10f,
                    ["FF"]   = 0.10f,
                    ["OOH"]  = 0.05f,
                    ["MBP"]  = 0.00f,
                    ["REST"] = 0.00f,
                },
                ["EE"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["AA"]   = 0.20f,
                    ["OH"]   = 0.15f,
                    ["DD"]   = 0.20f,
                    ["TH"]   = 0.15f,
                    ["FF"]   = 0.10f,
                    ["OOH"]  = 0.05f,
                    ["MBP"]  = 0.00f,
                    ["REST"] = 0.05f,
                },
                ["OH"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["AA"]   = 0.40f,
                    ["OOH"]  = 0.30f,
                    ["EE"]   = 0.15f,
                    ["DD"]   = 0.10f,
                    ["FF"]   = 0.05f,
                    ["TH"]   = 0.05f,
                    ["MBP"]  = 0.05f,
                    ["REST"] = 0.05f,
                },
                ["OOH"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["OH"]   = 0.30f,
                    ["REST"] = 0.10f,
                    ["MBP"]  = 0.10f,
                    ["AA"]   = 0.05f,
                    ["EE"]   = 0.05f,
                    ["FF"]   = 0.05f,
                    ["TH"]   = 0.00f,
                    ["DD"]   = 0.00f,
                },
                ["FF"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["TH"]   = 0.40f,
                    ["EE"]   = 0.10f,
                    ["DD"]   = 0.10f,
                    ["OH"]   = 0.05f,
                    ["AA"]   = 0.10f,
                    ["OOH"]  = 0.05f,
                    ["MBP"]  = 0.05f,
                    ["REST"] = 0.05f,
                },
                ["TH"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["FF"]   = 0.40f,
                    ["DD"]   = 0.30f,
                    ["EE"]   = 0.15f,
                    ["AA"]   = 0.10f,
                    ["OH"]   = 0.05f,
                    ["OOH"]  = 0.00f,
                    ["MBP"]  = 0.05f,
                    ["REST"] = 0.05f,
                },
                ["DD"] = new Dictionary<string, float>(
                    System.StringComparer.OrdinalIgnoreCase)
                {
                    ["TH"]   = 0.30f,
                    ["EE"]   = 0.20f,
                    ["FF"]   = 0.10f,
                    ["AA"]   = 0.15f,
                    ["OH"]   = 0.10f,
                    ["OOH"]  = 0.00f,
                    ["MBP"]  = 0.05f,
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

            // ── Extract all samples ───────────────────────────────────────
            int totalSamples = clip.samples * clip.channels;
            float[] allSamples = new float[totalSamples];
            clip.GetData(allSamples, 0);

            int channels   = clip.channels;
            int sampleRate = clip.frequency;
            int windowSize = Mathf.Max(1, sampleRate / settings.samplesPerSecond);

            // ── Collect all viseme names from profile ─────────────────────
            // We write weights for ALL of these on every frame.
            List<string> allVisemeNames = GetAllVisemeNames(profile);

            // ── Process each window ───────────────────────────────────────
            int totalMonoSamples = clip.samples;
            int windowIndex      = 0;

            while (true)
            {
                int windowStart = windowIndex * windowSize;
                if (windowStart >= totalMonoSamples) break;

                int windowEnd = Mathf.Min(windowStart + windowSize, totalMonoSamples);
                float frameTime = (float)windowStart / sampleRate;

                float[] monoWindow = ExtractMonoWindow(
                    allSamples, windowStart, windowEnd, channels);

                float rms = CalculateRMS(monoWindow);

                VisemeFrame frame = new VisemeFrame(frameTime);

                if (rms < settings.minAmplitudeThreshold)
                {
                    // Silence — REST dominant, all others get minimum residual
                    WriteFullDistribution(
                        frame,
                        dominantViseme: profile.restVisemeName,
                        dominantWeight: 0f,
                        allVisemeNames,
                        profile,
                        isSilence: true);
                }
                else
                {
                    FrequencyBands bands = CalculateFrequencyBands(monoWindow, sampleRate);

                    string dominant = SelectDominantViseme(rms, bands, profile, settings);
                    float  weight   = CalculateDominantWeight(dominant, rms, bands, settings);

                    WriteFullDistribution(
                        frame,
                        dominant,
                        weight,
                        allVisemeNames,
                        profile,
                        isSilence: false);
                }

                frames.Add(frame);
                windowIndex++;
            }

            return frames;
        }

        // ------------------------------------------------------------------
        // Full weight distribution
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes an explicit weight for every viseme in the profile.
        ///
        /// Dominant viseme gets its calculated weight.
        /// All others get a residual based on acoustic similarity to the
        /// dominant, floored at MinimumResidualWeight.
        ///
        /// Silence frames: REST gets 0 (closed). All others get
        /// MinimumResidualWeight so interpolation from silence has a
        /// known start point rather than implicit zero.
        /// </summary>
        private void WriteFullDistribution(
            VisemeFrame frame,
            string dominantViseme,
            float dominantWeight,
            List<string> allVisemeNames,
            VisemeProfile profile,
            bool isSilence)
        {
            foreach (string visemeName in allVisemeNames)
            {
                float weight;

                bool isDominant = string.Equals(
                    visemeName,
                    dominantViseme,
                    System.StringComparison.OrdinalIgnoreCase);

                if (isSilence)
                {
                    bool isRest = string.Equals(
                        visemeName,
                        profile.restVisemeName,
                        System.StringComparison.OrdinalIgnoreCase);

                    weight = isRest ? 0f : MinimumResidualWeight;
                }
                else if (isDominant)
                {
                    weight = dominantWeight;
                }
                else
                {
                    float similarity = GetSimilarity(dominantViseme, visemeName);
                    weight = Mathf.Max(
                        dominantWeight * similarity * ResidualScale,
                        MinimumResidualWeight);
                }

                frame.weights.Add(new VisemeWeight(visemeName, weight));
            }
        }

        // ------------------------------------------------------------------
        // Dominant viseme selection
        // ------------------------------------------------------------------

        /// <summary>
        /// Selects the viseme name that best represents the audio window.
        /// Falls back through profile alternatives if preferred names are
        /// absent from this profile.
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
        /// Fricatives have a narrower range — they're defined by shape,
        /// not loudness. Open vowels scale directly with amplitude.
        /// </summary>
        private float CalculateDominantWeight(
            string dominantViseme,
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

        private float GetSimilarity(string from, string to)
        {
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
        /// Runs a DFT on the sample window and returns normalised energy
        /// across three frequency bands. Applies a Hann window to prevent
        /// spectral leakage at window boundaries.
        /// </summary>
        private FrequencyBands CalculateFrequencyBands(float[] samples, int sampleRate)
        {
            int n = FftWindowSize;
            float[] windowed = new float[n];
            int copyLength = Mathf.Min(samples.Length, n);

            for (int i = 0; i < copyLength; i++)
            {
                float hann = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (copyLength - 1)));
                windowed[i] = samples[i] * hann;
            }

            int halfN = n / 2;
            float[] magnitudes = new float[halfN];
            float binWidthHz   = (float)sampleRate / n;

            for (int k = 0; k < halfN; k++)
            {
                float real = 0f;
                float imag = 0f;

                for (int t = 0; t < n; t++)
                {
                    float angle = 2f * Mathf.PI * k * t / n;
                    real +=  windowed[t] * Mathf.Cos(angle);
                    imag -= windowed[t] * Mathf.Sin(angle);
                }

                magnitudes[k] = Mathf.Sqrt(real * real + imag * imag) / n;
            }

            FrequencyBands result = default;

            for (int k = 0; k < halfN; k++)
            {
                float freqHz = k * binWidthHz;
                float energy = magnitudes[k];

                if      (freqHz < LowBandMaxHz) result.low  += energy;
                else if (freqHz < MidBandMaxHz) result.mid  += energy;
                else                            result.high += energy;

                result.total += energy;
            }

            if (result.total > 0.0001f)
            {
                result.low  /= result.total;
                result.mid  /= result.total;
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

        private bool ValidateClip(AudioClip clip)
        {
            if (clip == null)
            {
                Debug.LogError("[ResonanceSync] SignalStrategy: AudioClip is null.");
                return false;
            }

            float[] testBuffer = new float[1];
            if (!clip.GetData(testBuffer, 0))
            {
                Debug.LogError(
                    $"[ResonanceSync] AudioClip '{clip.name}' is not readable. " +
                    "Enable 'Read/Write' in the clip's import settings.");
                return false;
            }

            return true;
        }
    }
}