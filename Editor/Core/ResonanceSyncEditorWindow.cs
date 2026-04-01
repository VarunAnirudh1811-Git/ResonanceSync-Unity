using System.IO;
using UnityEditor;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Main editor window for ResonanceSync — two-panel resizable layout.
    ///
    /// LEFT PANEL  (resizable, min 220px):
    ///   Inputs → Settings → Generate → Debug → Output/Save
    ///
    /// RIGHT PANEL (remaining width):
    ///   3D character viewport (top ~55%) + Timeline (bottom ~45%)
    ///
    /// FIXES IN THIS VERSION:
    ///   - Draggable splitter between panels (_leftPanelWidth, _isDraggingSplitter)
    ///   - Character preview: _lastSMR initialised to a sentinel so domain-reload
    ///     forces a Setup() call even when session already has an SMR assigned
    ///   - EditorSetup() now only overwrites player fields when values are non-null,
    ///     preventing the session's skinnedMesh/profile from clearing a correctly
    ///     configured scene player
    ///   - PlayPreview() logs explicit diagnostics so animation failures are visible
    /// </summary>
    public class ResonanceSyncEditorWindow : EditorWindow
    {
        // ------------------------------------------------------------------
        // Window registration
        // ------------------------------------------------------------------

        [MenuItem("Window/GlyphLabs/ResonanceSync")]
        public static void Open()
        {
            var w = GetWindow<ResonanceSyncEditorWindow>();
            w.titleContent = new GUIContent(
                "ResonanceSync",
                EditorGUIUtility.IconContent("AudioSource Icon").image);
            w.minSize = new Vector2(700f, 580f);
            w.Show();
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------

        // Serialised so it survives domain reload / window reopen
        [SerializeField] private float _leftPanelWidth = 280f;

        private const float LeftPanelMin = 200f;
        private const float RightPanelMin = 400f;
        private const float SplitterWidth = 5f;
        private const float Padding = 8f;
        private const float ViewportRatio = 0.55f;

        // Splitter drag state — not serialised (rebuilt each OnGUI)
        private bool _isDraggingSplitter;
        private Rect _splitterRect;

        // ------------------------------------------------------------------
        // Serialised session state
        // ------------------------------------------------------------------

        [SerializeField]
        private LipSyncGenerationSession _session = new LipSyncGenerationSession();

        // ------------------------------------------------------------------
        // Non-serialised drawers
        // ------------------------------------------------------------------

        private CharacterPreviewDrawer _characterPreview;
        private CurveLaneDrawer _curveLanes;
        private FFTBandDrawer _fftDrawer;
        private VisemeWeightDrawer _visemeDrawer;

        // Scene preview player
        private ResonanceSyncPlayer _previewPlayer;

        // FFT snapshot
        private float _fftLow, _fftMid, _fftHigh;
        private float _fftCachedTime = -1f;

        // Left panel scroll
        private Vector2 _leftScroll;

        // Sentinel: forces Setup() on first OnGUI even when session already
        // has an SMR. Using a unique "impossible" sentinel rather than null
        // so we can distinguish "never set" from "deliberately cleared".
        private SkinnedMeshRenderer _lastSMR;
        private bool _previewInitialised;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _sectionLabelStyle;
        private GUIStyle _subtleStyle;
        private bool _stylesBuilt;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void OnEnable()
        {
            EnsureDrawers();
            EditorApplication.update += OnEditorUpdate;
            FindOrClearPreviewPlayer();
            _previewInitialised = false; // Force setup on next OnGUI
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            StopPreview();
            _characterPreview?.Cleanup();
        }

        private void OnDestroy()
        {
            StopPreview();
            _characterPreview?.Cleanup();
            _curveLanes?.Invalidate();
        }

        private void EnsureDrawers()
        {
            if (_characterPreview == null) _characterPreview = new CharacterPreviewDrawer();
            if (_curveLanes == null) _curveLanes = new CurveLaneDrawer();
            if (_fftDrawer == null) _fftDrawer = new FFTBandDrawer();
            if (_visemeDrawer == null) _visemeDrawer = new VisemeWeightDrawer();
        }

        // ------------------------------------------------------------------
        // Editor update
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

            float t = _previewPlayer.CurrentTime;
            if (!Mathf.Approximately(t, _session.currentTime))
            {
                _session.currentTime = t;
                Repaint();
            }
        }

        // ------------------------------------------------------------------
        // Main GUI
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            EnsureDrawers();
            BuildStyles();

            // ── Character preview setup ───────────────────────────────────
            // Trigger on first run or when SMR changes
            if (!_previewInitialised || _session.skinnedMesh != _lastSMR)
            {
                _characterPreview.Setup(_session.skinnedMesh);
                _lastSMR = _session.skinnedMesh;
                _previewInitialised = true;
            }

            // ── Handle splitter drag ──────────────────────────────────────
            HandleSplitterDrag();

            // ── Header ───────────────────────────────────────────────────
            Rect headerRect = new Rect(0, 0, position.width, 32f);
            DrawHeader(headerRect);

            float contentY = headerRect.height;
            float contentH = position.height - contentY;

            // ── Panel rects ───────────────────────────────────────────────
            // Clamp left width so neither panel goes below its minimum
            _leftPanelWidth = Mathf.Clamp(
                _leftPanelWidth,
                LeftPanelMin,
                position.width - RightPanelMin - SplitterWidth);

            Rect leftRect = new Rect(0, contentY, _leftPanelWidth, contentH);
            _splitterRect = new Rect(_leftPanelWidth, contentY, SplitterWidth, contentH);
            Rect rightRect = new Rect(_leftPanelWidth + SplitterWidth, contentY,
                position.width - _leftPanelWidth - SplitterWidth, contentH);

            // Draw splitter visual
            EditorGUI.DrawRect(_splitterRect, new Color(0.2f, 0.2f, 0.2f, 1f));

            // Change cursor when hovering splitter
            EditorGUIUtility.AddCursorRect(_splitterRect, MouseCursor.ResizeHorizontal);

            DrawLeftPanel(leftRect);
            DrawRightPanel(rightRect);
        }

        // ------------------------------------------------------------------
        // Splitter drag
        // ------------------------------------------------------------------

        private void HandleSplitterDrag()
        {
            Event e = Event.current;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && _splitterRect.Contains(e.mousePosition))
                    {
                        _isDraggingSplitter = true;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isDraggingSplitter)
                    {
                        _leftPanelWidth += e.delta.x;
                        _leftPanelWidth = Mathf.Clamp(
                            _leftPanelWidth,
                            LeftPanelMin,
                            position.width - RightPanelMin - SplitterWidth);
                        Repaint();
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isDraggingSplitter)
                    {
                        _isDraggingSplitter = false;
                        e.Use();
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Header
        // ------------------------------------------------------------------

        private void DrawHeader(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.13f, 1f));
            GUI.Label(
                new Rect(rect.x + Padding, rect.y, 200f, rect.height),
                "ResonanceSync", _headerStyle);
            GUI.Label(
                new Rect(rect.xMax - 120f, rect.y, 114f, rect.height),
                "by Glyph Labs", _subtleStyle);
        }

        // ------------------------------------------------------------------
        // LEFT PANEL
        // ------------------------------------------------------------------

        private void DrawLeftPanel(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.175f, 0.175f, 0.175f, 1f));

            GUILayout.BeginArea(rect);
            _leftScroll = GUILayout.BeginScrollView(
                _leftScroll, GUIStyle.none, GUI.skin.verticalScrollbar);

            GUILayout.Space(Padding);
            DrawInputSection();
            DrawLeftDivider();
            DrawSettingsSection();
            DrawLeftDivider();
            DrawGenerateButton();
            DrawLeftDivider();
            DrawDebugSection();
            DrawLeftDivider();
            DrawOutputSection();
            GUILayout.Space(Padding);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ── Inputs ────────────────────────────────────────────────────────

        private void DrawInputSection()
        {
            GUILayout.Label("INPUTS", _sectionLabelStyle);
            GUILayout.Space(4f);

            EditorGUI.BeginChangeCheck();
            var newClip = (AudioClip)EditorGUILayout.ObjectField(
                "Audio Clip", _session.clip, typeof(AudioClip), false);
            if (EditorGUI.EndChangeCheck())
            {
                _session.clip = newClip;
                _curveLanes.Invalidate();
                _fftCachedTime = -1f;
                _session.DeriveDefaultSavePath();
                StopPreview();
            }

            EditorGUI.BeginChangeCheck();
            var newSMR = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Character", _session.skinnedMesh, typeof(SkinnedMeshRenderer), true);
            if (EditorGUI.EndChangeCheck())
                _session.skinnedMesh = newSMR;

            EditorGUI.BeginChangeCheck();
            _session.profile = (VisemeProfile)EditorGUILayout.ObjectField(
                "Viseme Profile", _session.profile, typeof(VisemeProfile), false);
            if (EditorGUI.EndChangeCheck() && _session.generatedData != null)
                Debug.LogWarning("[ResonanceSync] Profile changed — regenerate to match.");

            _session.mode = (ProcessingMode)EditorGUILayout.EnumPopup("Mode", _session.mode);

            if (_session.mode == ProcessingMode.Rhubarb)
                EditorGUILayout.HelpBox("Rhubarb not yet implemented.", MessageType.Warning);

            if (_session.clip != null)
            {
                string path = AssetDatabase.GetAssetPath(_session.clip);
                if (!string.IsNullOrEmpty(path))
                {
                    var imp = AssetImporter.GetAtPath(path) as AudioImporter;
                    if (imp != null &&
                        imp.defaultSampleSettings.loadType != AudioClipLoadType.DecompressOnLoad)
                        EditorGUILayout.HelpBox(
                            "Set Load Type to 'Decompress On Load' and enable Read/Write.",
                            MessageType.Info);
                }
            }

            GUILayout.Space(4f);
        }

        // ── Settings ─────────────────────────────────────────────────────

        private void DrawSettingsSection()
        {
            _session.settingsFoldout = EditorGUILayout.Foldout(
                _session.settingsFoldout, "SETTINGS", true, _sectionLabelStyle);

            if (!_session.settingsFoldout) return;

            GUILayout.Space(4f);
            EditorGUI.indentLevel++;
            var s = _session.settings;

            s.samplesPerSecond = EditorGUILayout.IntSlider(
                new GUIContent("Samples/Sec",
                    "Analysis frames per second. 24 recommended."),
                s.samplesPerSecond, 12, 60);

            s.intensityMultiplier = EditorGUILayout.Slider(
                new GUIContent("Intensity", "Global output weight scale. 1.0 = no change."),
                s.intensityMultiplier, 0f, 2f);

            s.smoothing = EditorGUILayout.Slider(
                new GUIContent("Smoothing", "Temporal smoothing window in seconds."),
                s.smoothing, 0f, 0.15f);

            s.timeOffset = EditorGUILayout.Slider(
                new GUIContent("Time Offset", "Shift all frames in seconds."),
                s.timeOffset, -0.15f, 0.15f);

            s.minAmplitudeThreshold = EditorGUILayout.Slider(
                new GUIContent("Silence Threshold", "RMS below this = silence."),
                s.minAmplitudeThreshold, 0f, 0.1f);

            EditorGUI.indentLevel--;
            GUILayout.Space(4f);
        }

        // ── Generate ──────────────────────────────────────────────────────

        private void DrawGenerateButton()
        {
            GUILayout.Space(4f);

            if (!_session.CanGenerate)
            {
                string missing = _session.clip == null ? "Audio Clip" : "Viseme Profile";
                EditorGUILayout.HelpBox($"Assign {missing} to enable generation.",
                    MessageType.Info);
            }

            EditorGUI.BeginDisabledGroup(
                !_session.CanGenerate || _session.mode == ProcessingMode.Rhubarb);

            if (GUILayout.Button("Generate Lip Sync", GUILayout.Height(30f)))
                RunGeneration();

            EditorGUI.EndDisabledGroup();
            GUILayout.Space(4f);
        }

        // ── Debug ─────────────────────────────────────────────────────────

        private void DrawDebugSection()
        {
            _session.debugFoldout = EditorGUILayout.Foldout(
                _session.debugFoldout, "DEBUG", true, _sectionLabelStyle);

            if (!_session.debugFoldout) return;

            GUILayout.Space(4f);
            GUILayout.Label("FFT at Cursor", EditorStyles.centeredGreyMiniLabel);

            if (!Mathf.Approximately(_fftCachedTime, _session.currentTime))
                RecomputeFFT();

            Rect fftRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.Height(70f), GUILayout.ExpandWidth(true));
            fftRect = new Rect(
                fftRect.x + Padding, fftRect.y,
                fftRect.width - Padding * 2f, fftRect.height);
            _fftDrawer.Draw(fftRect, _fftLow, _fftMid, _fftHigh);

            GUILayout.Space(6f);
            GUILayout.Label("Viseme Weights at Cursor", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(2f);

            float visH = _visemeDrawer.GetHeight(_session.generatedData);
            Rect visRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.Height(visH), GUILayout.ExpandWidth(true));
            visRect = new Rect(
                visRect.x + Padding, visRect.y,
                visRect.width - Padding * 2f, visRect.height);
            _visemeDrawer.Draw(visRect, _session.generatedData,
                _session.profile, _session.currentTime);

            GUILayout.Space(4f);
        }

        // ── Output ────────────────────────────────────────────────────────

        private void DrawOutputSection()
        {
            GUILayout.Label("OUTPUT", _sectionLabelStyle);
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            _session.savePath = EditorGUILayout.TextField("Save Path", _session.savePath);
            if (GUILayout.Button("…", GUILayout.Width(22f)))
                PickSavePath();
            GUILayout.EndHorizontal();

            EditorGUI.BeginDisabledGroup(!_session.CanSave);
            if (GUILayout.Button("Save LipSyncData Asset", GUILayout.Height(28f)))
                SaveAsset();
            EditorGUI.EndDisabledGroup();
            GUILayout.Space(4f);
        }

        // ------------------------------------------------------------------
        // RIGHT PANEL
        // ------------------------------------------------------------------

        private void DrawRightPanel(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f, 1f));

            // No player banner (shrinks rect if shown)
            if (_previewPlayer == null)
            {
                FindOrClearPreviewPlayer();
                if (_previewPlayer == null)
                    DrawNoPlayerBanner(ref rect);
            }

            if (rect.height < 20f) return;

            float viewportH = Mathf.Floor(rect.height * ViewportRatio);
            float timelineH = rect.height - viewportH;

            Rect viewportArea = new Rect(rect.x, rect.y, rect.width, viewportH);
            Rect timelineArea = new Rect(rect.x, rect.y + viewportH, rect.width, timelineH);

            // 3D viewport
            _characterPreview.Draw(viewportArea, _session.GetActiveBlendshapeIndices());

            // Divider
            EditorGUI.DrawRect(
                new Rect(rect.x, rect.y + viewportH - 1f, rect.width, 1f),
                new Color(0.1f, 0.1f, 0.1f, 1f));

            // Timeline + curve lanes
            float newTime = _curveLanes.Draw(
                timelineArea,
                _session.generatedData,
                _session.clip,
                _session.currentTime,
                _session.isPlaying,
                onPlay: PlayPreview,
                onPause: PausePreview,
                onStop: StopPreview);

            if (!Mathf.Approximately(newTime, _session.currentTime))
                ScrubTo(newTime);
        }

        // ------------------------------------------------------------------
        // No player banner
        // ------------------------------------------------------------------

        private void DrawNoPlayerBanner(ref Rect rect)
        {
            float h = 28f;
            Rect banner = new Rect(rect.x, rect.y, rect.width, h);
            EditorGUI.DrawRect(banner, new Color(0.25f, 0.20f, 0.10f, 1f));

            GUI.Label(
                new Rect(banner.x + Padding, banner.y, banner.width - 120f, banner.height),
                "No ResonanceSyncPlayer found in scene.",
                new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(1f, 0.85f, 0.4f) },
                    alignment = TextAnchor.MiddleLeft,
                });

            if (GUI.Button(
                new Rect(banner.xMax - 116f, banner.y + 4f, 110f, 20f),
                "Create Preview Object", EditorStyles.miniButton))
                CreatePreviewObject();

            rect = new Rect(rect.x, rect.y + h, rect.width, rect.height - h);
        }

        // ------------------------------------------------------------------
        // Generation
        // ------------------------------------------------------------------

        private void RunGeneration()
        {
            StopPreview();

            if (_session.generatedData == null)
                _session.generatedData = ScriptableObject.CreateInstance<LipSyncData>();

            bool success = LipSyncProcessor.Generate(
                new SignalStrategy(),
                _session.clip,
                _session.profile,
                _session.settings,
                _session.generatedData);

            if (success)
            {
                _session.currentTime = 0f;
                _fftCachedTime = -1f;
                _curveLanes.Invalidate();
                Repaint();
            }
            else
            {
                _session.generatedData = null;
            }
        }

        // ------------------------------------------------------------------
        // Preview playback
        // ------------------------------------------------------------------

        private void PlayPreview()
        {
            if (!_session.CanPreview)
            {
                Debug.LogWarning("[ResonanceSync] Cannot play — generate data first.");
                return;
            }

            if (_previewPlayer == null) FindOrClearPreviewPlayer();

            if (_previewPlayer == null)
            {
                Debug.LogWarning(
                    "[ResonanceSync] No ResonanceSyncPlayer in scene. " +
                    "Click 'Create Preview Object' in the window.");
                return;
            }

            // FIX: EditorSetup only overwrites fields when values are non-null.
            // Previously passing null skinnedMesh would clear a correctly-
            // configured scene player, causing ValidatePlayRequest to fail.
            if (_session.skinnedMesh != null || _session.profile != null)
                _previewPlayer.EditorSetup(
                    _session.skinnedMesh,
                    _session.profile);

            // Verify the player is ready before calling Play
            if (_session.skinnedMesh == null)
            {
                Debug.LogError(
                    "[ResonanceSync] PlayPreview: No SkinnedMeshRenderer assigned. " +
                    "Assign a Character in the window inputs.");
                return;
            }

            if (_session.profile == null)
            {
                Debug.LogError(
                    "[ResonanceSync] PlayPreview: No VisemeProfile assigned. " +
                    "Assign a Viseme Profile in the window inputs.");
                return;
            }

            _previewPlayer.Play(_session.generatedData, _session.clip);

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
            _previewPlayer?.SeekTo(time);
            _fftCachedTime = -1f;
            Repaint();
        }

        // ------------------------------------------------------------------
        // Player management
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
        // FFT
        // ------------------------------------------------------------------

        private void RecomputeFFT()
        {
            _fftLow = _fftMid = _fftHigh = 0f;
            _fftCachedTime = _session.currentTime;

            if (_session.clip == null || _session.settings.samplesPerSecond <= 0) return;

            try
            {
                float[] all = new float[_session.clip.samples * _session.clip.channels];
                if (!_session.clip.GetData(all, 0)) return;

                int sr = _session.clip.frequency;
                int ch = _session.clip.channels;
                int winSize = sr / _session.settings.samplesPerSecond;
                int start = Mathf.Clamp((int)(_session.currentTime * sr), 0, _session.clip.samples - 1);
                int end = Mathf.Min(start + winSize, _session.clip.samples);
                int len = end - start;
                if (len <= 0) return;

                float[] mono = new float[len];
                for (int i = 0; i < len; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < ch; c++) sum += all[(start + i) * ch + c];
                    mono[i] = sum / ch;
                }

                int n = 1024;
                float[] win = new float[n];
                int copyLen = Mathf.Min(mono.Length, n);

                if (copyLen > 1)
                    for (int i = 0; i < copyLen; i++)
                    {
                        float hann = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (copyLen - 1)));
                        win[i] = mono[i] * hann;
                    }
                else if (copyLen == 1)
                    win[0] = mono[0];

                int halfN = n / 2;
                float binW = (float)sr / n;
                float total = 0f, lo = 0f, mid = 0f, hi = 0f;

                for (int k = 0; k < halfN; k++)
                {
                    float re = 0f, im = 0f;
                    for (int t = 0; t < n; t++)
                    {
                        float a = 2f * Mathf.PI * k * t / n;
                        re += win[t] * Mathf.Cos(a);
                        im -= win[t] * Mathf.Sin(a);
                    }
                    float mag = Mathf.Sqrt(re * re + im * im) / n;
                    float freq = k * binW;

                    if (freq < 500f) lo += mag;
                    else if (freq < 2000f) mid += mag;
                    else hi += mag;
                    total += mag;
                }

                if (total > 0.0001f)
                {
                    _fftLow = lo / total;
                    _fftMid = mid / total;
                    _fftHigh = hi / total;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ResonanceSync] FFT preview error: {ex.Message}");
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

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existing = AssetDatabase.LoadAssetAtPath<LipSyncData>(path);

            if (existing != null && existing != _session.generatedData)
            {
                EditorUtility.CopySerialized(_session.generatedData, existing);
                _session.generatedData = existing;
                AssetDatabase.SaveAssets();
                Debug.Log($"[ResonanceSync] Updated existing asset at '{path}'.");
            }
            else if (existing == null)
            {
                AssetDatabase.CreateAsset(_session.generatedData, path);
                AssetDatabase.SaveAssets();
                Debug.Log($"[ResonanceSync] Saved new asset at '{path}'.");
            }
            else
            {
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
            string name = Path.GetFileNameWithoutExtension(_session.savePath);
            string chosen = EditorUtility.SaveFilePanelInProject(
                "Save LipSyncData", name, "asset",
                "Choose where to save the LipSyncData asset.", dir);
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
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            _sectionLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 9,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) }
            };

            _subtleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f) }
            };
        }

        private void DrawLeftDivider()
        {
            GUILayout.Space(4f);
            Rect r = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none, GUILayout.Height(1f));
            EditorGUI.DrawRect(
                new Rect(r.x + Padding * 0.5f, r.y, r.width - Padding, 1f),
                new Color(0.28f, 0.28f, 0.28f, 1f));
            GUILayout.Space(4f);
        }
    }
}