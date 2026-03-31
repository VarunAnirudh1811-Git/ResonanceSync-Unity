using System.IO;
using UnityEngine;
using UnityEditor;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Main editor window for ResonanceSync.
    /// Open via: Window > GlyphLabs > ResonanceSync
    ///
    /// RESPONSIBILITIES:
    ///   - Artist input fields (clip, mesh, profile, mode)
    ///   - Settings sliders wired to LipSyncSettings
    ///   - Generate button → LipSyncProcessor.Generate()
    ///   - Preview playback via a scene ResonanceSyncPlayer
    ///   - Waveform, FFT band, and viseme weight visualisations
    ///   - Save generated asset to disk
    ///
    /// PREVIEW PLAYER:
    ///   Preview requires a GameObject in the open scene with both a
    ///   ResonanceSyncPlayer and an AudioSource component. The window finds
    ///   the first such object automatically. If none exists it prompts
    ///   the artist to create one. The player is driven via
    ///   EditorApplication.update — the same AudioSource.time → blendshape
    ///   path as runtime, so preview is faithful to production behaviour.
    ///
    /// DOMAIN RELOAD SAFETY:
    ///   All artist-facing state lives in _session which is [SerializeField],
    ///   so it survives recompile. Non-serialisable state (texture cache,
    ///   player reference, FFT snapshot) is rebuilt on demand.
    /// </summary>
    public class ResonanceSyncEditorWindow : EditorWindow
    {
        // ------------------------------------------------------------------
        // Window registration
        // ------------------------------------------------------------------

        [MenuItem("GlyphLabs/ResonanceSync")]
        public static void Open()
        {
            var window = GetWindow<ResonanceSyncEditorWindow>();
            window.titleContent = new GUIContent("ResonanceSync", EditorGUIUtility.IconContent("AudioSource Icon").image);
            window.minSize = new Vector2(420f, 600f);
            window.Show();
        }

        // ------------------------------------------------------------------
        // Serialised state (survives domain reload)
        // ------------------------------------------------------------------

        [SerializeField] private LipSyncGenerationSession _session = new LipSyncGenerationSession();

        // ------------------------------------------------------------------
        // Non-serialised (rebuilt on demand)
        // ------------------------------------------------------------------

        private ResonanceSyncPlayer _previewPlayer;
        private WaveformDrawer _waveformDrawer = new WaveformDrawer();
        private FFTBandDrawer _fftDrawer = new FFTBandDrawer();
        private VisemeWeightDrawer _visemeDrawer = new VisemeWeightDrawer();

        // Cached FFT band values computed at the last scrub position
        // to avoid running DFT every OnGUI repaint
        private float _fftLow, _fftMid, _fftHigh;
        private float _fftCachedTime = -1f;

        // Scroll position for the main content area
        private Vector2 _scrollPos;

        // Colours and styles — built once on first OnGUI
        private GUIStyle _headerStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _subtleStyle;
        private bool _stylesBuilt;

        // ------------------------------------------------------------------
        // Layout constants
        // ------------------------------------------------------------------

        private const float SectionPadding = 8f;
        private const float WaveformHeight = 72f;
        private const float FFTHeight = 80f;
        private const float ScrubHeight = 20f;

        // ------------------------------------------------------------------
        // Unity editor callbacks
        // ------------------------------------------------------------------

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            _waveformDrawer.Invalidate();

            // Try to find a preview player in the scene
            FindOrClearPreviewPlayer();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            StopPreview();
        }

        private void OnDestroy()
        {
            StopPreview();
            _waveformDrawer.Invalidate();
        }

        // ------------------------------------------------------------------
        // EditorApplication.update — drives preview playback
        // ------------------------------------------------------------------

        private void OnEditorUpdate()
        {
            if (!_session.isPlaying) return;
            if (_previewPlayer == null || !_previewPlayer.IsPlaying)
            {
                _session.isPlaying = false;
                _session.currentTime = 0f;
                Repaint();
                return;
            }

            float newTime = _previewPlayer.CurrentTime;
            if (!Mathf.Approximately(newTime, _session.currentTime))
            {
                _session.currentTime = newTime;
                Repaint();
            }
        }

        // ------------------------------------------------------------------
        // Main GUI
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            BuildStyles();
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawHeader();
            DrawDivider();
            DrawInputSection();
            DrawDivider();
            DrawSettingsSection();
            DrawDivider();
            DrawGenerateButton();

            if (_session.CanPreview)
            {
                DrawDivider();
                DrawPreviewSection();
            }

            DrawDivider();
            DrawDebugSection();
            DrawDivider();
            DrawSaveSection();

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------
        // Header
        // ------------------------------------------------------------------

        private void DrawHeader()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("ResonanceSync", _headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("by Glyph Labs", _subtleStyle);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4f);
        }

        // ------------------------------------------------------------------
        // Inputs
        // ------------------------------------------------------------------

        private void DrawInputSection()
        {
            GUILayout.Label("INPUTS", _sectionStyle);
            EditorGUILayout.Space(4f);

            EditorGUI.BeginChangeCheck();

            var newClip = (AudioClip)EditorGUILayout.ObjectField(
                "Audio Clip", _session.clip, typeof(AudioClip), false);

            if (EditorGUI.EndChangeCheck())
            {
                _session.clip = newClip;
                _waveformDrawer.Invalidate();
                _fftCachedTime = -1f;

                // Auto-derive save path from clip location
                _session.DeriveDefaultSavePath();
                StopPreview();
            }

            _session.skinnedMesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Character", _session.skinnedMesh, typeof(SkinnedMeshRenderer), true);

            EditorGUI.BeginChangeCheck();
            _session.profile = (VisemeProfile)EditorGUILayout.ObjectField(
                "Viseme Profile", _session.profile, typeof(VisemeProfile), false);
            if (EditorGUI.EndChangeCheck())
            {
                // Profile change invalidates any existing generated data's
                // viseme name assumptions — warn if data exists
                if (_session.generatedData != null)
                {
                    Debug.LogWarning(
                        "[ResonanceSync] Viseme Profile changed. " +
                        "Regenerate to ensure data matches the new profile.");
                }
            }

            _session.mode = (ProcessingMode)EditorGUILayout.EnumPopup(
                "Mode", _session.mode);

            // Warn if Rhubarb selected — not yet implemented
            if (_session.mode == ProcessingMode.Rhubarb)
            {
                EditorGUILayout.HelpBox(
                    "Rhubarb mode is not yet implemented. Switch to Signal.",
                    MessageType.Warning);
            }

            // Warn if clip is not Read/Write enabled
            if (_session.clip != null)
            {
                string clipPath = AssetDatabase.GetAssetPath(_session.clip);
                if (!string.IsNullOrEmpty(clipPath))
                {
                    var importer = AssetImporter.GetAtPath(clipPath) as AudioImporter;
                    if (importer != null && !importer.loadInBackground)
                    {
                        // Check for decompress on load
                        var sampleSettings = importer.defaultSampleSettings;
                        if (sampleSettings.loadType != AudioClipLoadType.DecompressOnLoad)
                        {
                            EditorGUILayout.HelpBox(
                                "Set AudioClip Load Type to 'Decompress On Load' " +
                                "and enable 'Read/Write' for waveform preview and generation.",
                                MessageType.Info);
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------

        private void DrawSettingsSection()
        {
            _session.settingsFoldout = EditorGUILayout.Foldout(
                _session.settingsFoldout, "SETTINGS", true, _sectionStyle);

            if (!_session.settingsFoldout) return;

            EditorGUILayout.Space(4f);
            EditorGUI.indentLevel++;

            var s = _session.settings;

            s.samplesPerSecond = EditorGUILayout.IntSlider(
                new GUIContent("Samples / Sec",
                    "Analysis frames per second. 24 = standard animation rate."),
                s.samplesPerSecond, 12, 60);

            s.intensityMultiplier = EditorGUILayout.Slider(
                new GUIContent("Intensity",
                    "Global scale on all output weights. 1.0 = no change."),
                s.intensityMultiplier, 0f, 2f);

            s.smoothing = EditorGUILayout.Slider(
                new GUIContent("Smoothing",
                    "Temporal smoothing window in seconds. 0 = no smoothing."),
                s.smoothing, 0f, 0.15f);

            s.timeOffset = EditorGUILayout.Slider(
                new GUIContent("Time Offset",
                    "Shift all frames forward/backward in seconds."),
                s.timeOffset, -0.15f, 0.15f);

            s.minAmplitudeThreshold = EditorGUILayout.Slider(
                new GUIContent("Silence Threshold",
                    "Audio below this RMS level is treated as silence."),
                s.minAmplitudeThreshold, 0f, 0.1f);

            EditorGUI.indentLevel--;
        }

        // ------------------------------------------------------------------
        // Generate
        // ------------------------------------------------------------------

        private void DrawGenerateButton()
        {
            EditorGUILayout.Space(4f);

            // Show what's missing
            if (!_session.CanGenerate)
            {
                string missing = _session.clip == null ? "Audio Clip" : "Viseme Profile";
                EditorGUILayout.HelpBox($"Assign {missing} to enable generation.", MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(!_session.CanGenerate ||
                                         _session.mode == ProcessingMode.Rhubarb);

            if (GUILayout.Button("Generate Lip Sync", GUILayout.Height(32f)))
                RunGeneration();

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.Space(4f);
        }

        // ------------------------------------------------------------------
        // Preview
        // ------------------------------------------------------------------

        private void DrawPreviewSection()
        {
            GUILayout.Label("PREVIEW", _sectionStyle);
            EditorGUILayout.Space(4f);

            // Find preview player
            if (_previewPlayer == null)
                FindOrClearPreviewPlayer();

            if (_previewPlayer == null)
            {
                EditorGUILayout.HelpBox(
                    "Add a GameObject with ResonanceSyncPlayer to the open scene " +
                    "to enable preview playback.",
                    MessageType.Info);

                if (GUILayout.Button("Create Preview Object in Scene"))
                    CreatePreviewObject();

                // Still draw waveform even without player — allows visual inspection
            }

            // Waveform
            float duration = _session.generatedData != null
                ? _session.generatedData.duration
                : (_session.clip != null ? _session.clip.length : 1f);

            float normalised = duration > 0f ? _session.currentTime / duration : 0f;

            Rect waveRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.Height(WaveformHeight),
                GUILayout.ExpandWidth(true));

            // Reserve a small margin
            waveRect = new Rect(
                waveRect.x + SectionPadding,
                waveRect.y,
                waveRect.width - SectionPadding * 2f,
                waveRect.height);

            _waveformDrawer.Draw(waveRect, _session.clip, normalised);

            // Scrub interaction — click/drag on waveform
            HandleWaveformScrub(waveRect, duration);

            EditorGUILayout.Space(4f);

            // Scrub slider
            EditorGUI.BeginChangeCheck();
            float newTime = EditorGUILayout.Slider(
                _session.currentTime, 0f, Mathf.Max(duration, 0.01f));
            if (EditorGUI.EndChangeCheck())
                ScrubTo(newTime);

            // Playback controls
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            // Rewind
            if (GUILayout.Button("◀◀", GUILayout.Width(36f)))
                ScrubTo(0f);

            // Play / Pause
            string playLabel = _session.isPlaying ? "⏸" : "▶";
            if (GUILayout.Button(playLabel, GUILayout.Width(36f)))
            {
                if (_session.isPlaying) PausePreview();
                else PlayPreview();
            }

            // Stop
            if (GUILayout.Button("■", GUILayout.Width(36f)))
                StopPreview();

            GUILayout.FlexibleSpace();

            // Time readout
            string timeStr = LipSyncGenerationSession.FormatTime(_session.currentTime) +
                             " / " +
                             LipSyncGenerationSession.FormatTime(duration);
            GUILayout.Label(timeStr, _subtleStyle);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4f);
        }

        // ------------------------------------------------------------------
        // Debug
        // ------------------------------------------------------------------

        private void DrawDebugSection()
        {
            _session.debugFoldout = EditorGUILayout.Foldout(
                _session.debugFoldout, "DEBUG", true, _sectionStyle);

            if (!_session.debugFoldout) return;
            EditorGUILayout.Space(4f);

            // FFT bands
            GUILayout.Label("Frequency Bands at Scrub Position",
                EditorStyles.centeredGreyMiniLabel);

            // Recompute FFT if time changed
            if (!Mathf.Approximately(_fftCachedTime, _session.currentTime))
                RecomputeFFT();

            Rect fftRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.Height(FFTHeight),
                GUILayout.ExpandWidth(true));

            fftRect = new Rect(
                fftRect.x + SectionPadding,
                fftRect.y,
                fftRect.width - SectionPadding * 2f,
                fftRect.height);

            _fftDrawer.Draw(fftRect, _fftLow, _fftMid, _fftHigh);

            EditorGUILayout.Space(8f);

            // Viseme weights
            GUILayout.Label("Viseme Weights at Scrub Position",
                EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(2f);

            float visemeHeight = _visemeDrawer.GetHeight(_session.generatedData);
            Rect visemeRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.Height(visemeHeight),
                GUILayout.ExpandWidth(true));

            visemeRect = new Rect(
                visemeRect.x + SectionPadding,
                visemeRect.y,
                visemeRect.width - SectionPadding * 2f,
                visemeRect.height);

            _visemeDrawer.Draw(
                visemeRect,
                _session.generatedData,
                _session.profile,
                _session.currentTime);

            EditorGUILayout.Space(4f);
        }

        // ------------------------------------------------------------------
        // Save
        // ------------------------------------------------------------------

        private void DrawSaveSection()
        {
            GUILayout.Label("OUTPUT", _sectionStyle);
            EditorGUILayout.Space(4f);

            EditorGUILayout.BeginHorizontal();
            _session.savePath = EditorGUILayout.TextField("Save Path", _session.savePath);
            if (GUILayout.Button("…", GUILayout.Width(24f)))
                PickSavePath();
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginDisabledGroup(!_session.CanSave);

            if (GUILayout.Button("Save LipSyncData Asset", GUILayout.Height(28f)))
                SaveAsset();

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.Space(8f);
        }

        // ------------------------------------------------------------------
        // Generation
        // ------------------------------------------------------------------

        private void RunGeneration()
        {
            StopPreview();

            // Create or reuse the in-memory LipSyncData asset
            if (_session.generatedData == null)
                _session.generatedData = ScriptableObject.CreateInstance<LipSyncData>();

            var strategy = new SignalStrategy();

            bool success = LipSyncProcessor.Generate(
                strategy,
                _session.clip,
                _session.profile,
                _session.settings,
                _session.generatedData);

            if (success)
            {
                _session.currentTime = 0f;
                _fftCachedTime = -1f;
                RecomputeFFT();
                Repaint();
                Debug.Log("[ResonanceSync] Generation complete.");
            }
            else
            {
                // Generation failed — clear data so UI doesn't show stale result
                _session.generatedData = null;
                Debug.LogError("[ResonanceSync] Generation failed. Check Console for details.");
            }
        }

        // ------------------------------------------------------------------
        // Preview playback
        // ------------------------------------------------------------------

        private void PlayPreview()
        {
            if (_session.generatedData == null || _session.clip == null) return;
            if (_previewPlayer == null) FindOrClearPreviewPlayer();
            if (_previewPlayer == null) return;

            _previewPlayer.EditorSetup(_session.skinnedMesh, _session.profile);
            _previewPlayer.Play(_session.generatedData, _session.clip);

            // If we were scrubbed mid-clip, seek to that position
            if (_session.currentTime > 0f)
                _previewPlayer.SeekTo(_session.currentTime);

            _session.isPlaying = true;
        }

        private void PausePreview()
        {
            _session.isPlaying = false;
            _previewPlayer?.Pause();
        }

        private void StopPreview()
        {
            _session.isPlaying = false;
            _session.currentTime = 0f;
            _previewPlayer?.Stop();
        }

        private void ScrubTo(float time)
        {
            _session.currentTime = time;

            if (_previewPlayer != null)
                _previewPlayer.SeekTo(time);

            _fftCachedTime = -1f; // Force FFT recompute
            Repaint();
        }

        // ------------------------------------------------------------------
        // Preview player management
        // ------------------------------------------------------------------

        private void FindOrClearPreviewPlayer()
        {
            _previewPlayer = FindFirstObjectByType<ResonanceSyncPlayer>();
        }

        private void CreatePreviewObject()
        {
            var go = new GameObject("ResonanceSync Preview Player");
            go.AddComponent<AudioSource>();
            _previewPlayer = go.AddComponent<ResonanceSyncPlayer>();
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create ResonanceSync Preview Player");
        }

        // ------------------------------------------------------------------
        // Waveform scrubbing
        // ------------------------------------------------------------------

        private void HandleWaveformScrub(Rect waveRect, float duration)
        {
            Event e = Event.current;
            bool inRect = waveRect.Contains(e.mousePosition);

            if (inRect && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                float normalised = Mathf.Clamp01(
                    (e.mousePosition.x - waveRect.x) / waveRect.width);
                ScrubTo(normalised * duration);
                e.Use();
            }
        }

        // ------------------------------------------------------------------
        // FFT recompute
        // ------------------------------------------------------------------

        /// <summary>
        /// Recomputes the FFT band snapshot at the current scrub time.
        /// Runs the same sample extraction and DFT path as SignalStrategy
        /// but only for the one window at the current time — not the full clip.
        /// </summary>
        private void RecomputeFFT()
        {
            _fftLow = _fftMid = _fftHigh = 0f;
            _fftCachedTime = _session.currentTime;

            if (_session.clip == null) return;
            if (_session.settings.samplesPerSecond <= 0) return;

            try
            {
                float[] allSamples = new float[_session.clip.samples * _session.clip.channels];
                if (!_session.clip.GetData(allSamples, 0)) return;

                int sampleRate = _session.clip.frequency;
                int channels = _session.clip.channels;
                int windowSize = sampleRate / _session.settings.samplesPerSecond;
                int startSample = Mathf.Clamp(
                    (int)(_session.currentTime * sampleRate),
                    0,
                    _session.clip.samples - 1);
                int endSample = Mathf.Min(startSample + windowSize, _session.clip.samples);

                int length = endSample - startSample;
                if (length <= 0) return;

                // Mix to mono
                float[] mono = new float[length];
                for (int i = 0; i < length; i++)
                {
                    int baseIndex = (startSample + i) * channels;
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += allSamples[baseIndex + c];
                    mono[i] = sum / channels;
                }

                // DFT — same as SignalStrategy
                int n = 1024;
                float[] windowed = new float[n];
                int copyLen = Mathf.Min(mono.Length, n);

                if (copyLen > 1)
                {
                    for (int i = 0; i < copyLen; i++)
                    {
                        float hann = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (copyLen - 1)));
                        windowed[i] = mono[i] * hann;
                    }
                }
                else if (copyLen == 1)
                {
                    windowed[0] = mono[0];
                }

                int halfN = n / 2;
                float binWidthHz = (float)sampleRate / n;
                float totalE = 0f;
                float lowE = 0f, midE = 0f, highE = 0f;

                for (int k = 0; k < halfN; k++)
                {
                    float real = 0f, imag = 0f;
                    for (int t = 0; t < n; t++)
                    {
                        float angle = 2f * Mathf.PI * k * t / n;
                        real += windowed[t] * Mathf.Cos(angle);
                        imag -= windowed[t] * Mathf.Sin(angle);
                    }
                    float mag = Mathf.Sqrt(real * real + imag * imag) / n;
                    float freq = k * binWidthHz;

                    if (freq < 500f) lowE += mag;
                    else if (freq < 2000f) midE += mag;
                    else highE += mag;

                    totalE += mag;
                }

                if (totalE > 0.0001f)
                {
                    _fftLow = lowE / totalE;
                    _fftMid = midE / totalE;
                    _fftHigh = highE / totalE;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[ResonanceSync] FFT preview failed at {_session.currentTime:F2}s: " +
                    ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Save
        // ------------------------------------------------------------------

        private void SaveAsset()
        {
            if (_session.generatedData == null) return;

            string path = _session.savePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                Debug.LogError("[ResonanceSync] Save path is empty.");
                return;
            }

            // Ensure the directory exists
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Check if an asset already exists at this path
            var existing = AssetDatabase.LoadAssetAtPath<LipSyncData>(path);

            if (existing != null && existing != _session.generatedData)
            {
                // Overwrite: copy data into the existing asset so any existing
                // references in scenes/prefabs remain valid
                EditorUtility.CopySerialized(_session.generatedData, existing);
                _session.generatedData = existing;
                AssetDatabase.SaveAssets();
                Debug.Log($"[ResonanceSync] Updated existing asset at '{path}'.");
            }
            else if (existing == null)
            {
                // New asset
                AssetDatabase.CreateAsset(_session.generatedData, path);
                AssetDatabase.SaveAssets();
                Debug.Log($"[ResonanceSync] Saved new asset at '{path}'.");
            }
            else
            {
                // Same object — just mark dirty and save
                EditorUtility.SetDirty(_session.generatedData);
                AssetDatabase.SaveAssets();
                Debug.Log($"[ResonanceSync] Saved asset at '{path}'.");
            }

            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(_session.generatedData);
        }

        private void PickSavePath()
        {
            string dir = Path.GetDirectoryName(_session.savePath) ?? "Assets";
            string filename = Path.GetFileNameWithoutExtension(_session.savePath);

            string chosen = EditorUtility.SaveFilePanelInProject(
                "Save LipSyncData",
                filename,
                "asset",
                "Choose where to save the LipSyncData asset.",
                dir);

            if (!string.IsNullOrEmpty(chosen))
                _session.savePath = chosen;
        }

        // ------------------------------------------------------------------
        // Style helpers
        // ------------------------------------------------------------------

        private void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleLeft,
            };

            _sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            };

            _subtleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
                alignment = TextAnchor.MiddleRight,
            };
        }

        private void DrawDivider()
        {
            EditorGUILayout.Space(4f);
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(1f));
            EditorGUI.DrawRect(
                new Rect(r.x + SectionPadding, r.y, r.width - SectionPadding * 2f, 1f),
                new Color(0.3f, 0.3f, 0.3f, 1f));
            EditorGUILayout.Space(4f);
        }
    }
}