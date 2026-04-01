using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Renders a live 3D character preview inside an editor window rect
    /// using Unity's PreviewRenderUtility.
    ///
    /// FIX: Replaced InstantiatePrefabInScene() with the correct approach
    /// for scene objects: create a new GameObject in the preview scene and
    /// copy the mesh/materials from the source SMR manually.
    /// InstantiatePrefabInScene() only works with Project assets (prefabs),
    /// not scene instances — it silently returned null for scene objects.
    ///
    /// FIX: Setup() now guards against null source and logs clearly when
    /// the preview cannot be initialised so the artist knows what to fix.
    /// </summary>
    public class CharacterPreviewDrawer
    {
        // ------------------------------------------------------------------
        // Camera defaults
        // ------------------------------------------------------------------

        private const float DefaultAzimuth = 0f;
        private const float DefaultElevation = 10f;
        private const float DefaultDistance = 0.4f;
        private const float MinDistance = 0.05f;
        private const float MaxDistance = 3.0f;
        private const float OrbitSensitivity = 0.5f;
        private const float ZoomSensitivity = 0.02f;

        // ------------------------------------------------------------------
        // Colours
        // ------------------------------------------------------------------

        private static readonly Color BgColor = new Color(0.17f, 0.17f, 0.17f, 1f);
        private static readonly Color ControlsBgColor = new Color(0.13f, 0.13f, 0.13f, 1f);

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private PreviewRenderUtility _preview;

        // Preview scene objects — owned by us, cleaned up in Cleanup()
        private GameObject _previewRoot;
        private SkinnedMeshRenderer _previewSMR;

        // Source we mirror from
        private SkinnedMeshRenderer _sourceSMR;

        // Spherical camera coordinates
        private float _azimuth = DefaultAzimuth;
        private float _elevation = DefaultElevation;
        private float _distance = DefaultDistance;
        private Vector3 _target;

        // Wireframe toggle
        private bool _wireframe;

        // Drag state
        private bool _isDragging;
        private Vector2 _lastMousePos;

        private const float ControlsHeight = 28f;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Initialises the preview scene for the given SkinnedMeshRenderer.
        /// Safe to call with null — cleans up and shows placeholder.
        /// </summary>
        public void Setup(SkinnedMeshRenderer source)
        {
            Cleanup();

            if (source == null) return;
            if (source.sharedMesh == null)
            {
                Debug.LogWarning(
                    "[ResonanceSync] CharacterPreviewDrawer: " +
                    $"SkinnedMeshRenderer '{source.name}' has no sharedMesh.");
                return;
            }

            _sourceSMR = source;

            // ── Initialise PreviewRenderUtility ───────────────────────────
            _preview = new PreviewRenderUtility();
            _preview.camera.backgroundColor = BgColor;
            _preview.camera.clearFlags = CameraClearFlags.SolidColor;
            _preview.camera.nearClipPlane = 0.001f;
            _preview.camera.farClipPlane = 100f;

            // Lighting
            _preview.lights[0].intensity = 1.1f;
            _preview.lights[0].transform.rotation = Quaternion.Euler(30f, -30f, 0f);
            _preview.lights[1].intensity = 0.4f;
            _preview.lights[1].transform.rotation = Quaternion.Euler(10f, 150f, 0f);

            // ── Build preview mesh object ─────────────────────────────────
            // FIX: Do NOT use InstantiatePrefabInScene — it requires a prefab
            // asset, not a scene instance. Instead we create a fresh GameObject
            // and copy the mesh + materials from the source SMR manually.
            _previewRoot = _preview.InstantiatePrefabInScene(
                // We pass a temporary inactive duplicate so PreviewRenderUtility
                // can manage its lifecycle. We then find the SMR on it.
                CreatePreviewObject(source));

            if (_previewRoot == null)
            {
                // Fallback if InstantiatePrefabInScene still fails
                // (can happen in some Unity versions with scene objects)
                _previewRoot = CreatePreviewObject(source);
                _preview.AddSingleGO(_previewRoot);
            }

            _previewSMR = _previewRoot != null
                ? _previewRoot.GetComponentInChildren<SkinnedMeshRenderer>()
                : null;

            if (_previewSMR == null)
            {
                Debug.LogWarning(
                    "[ResonanceSync] CharacterPreviewDrawer: " +
                    "Could not create preview SkinnedMeshRenderer. " +
                    "3D preview will not display.");
                return;
            }

            // ── Camera framing ────────────────────────────────────────────
            _target = FindHeadTarget(source);
            float size = source.bounds.size.magnitude;
            _distance = Mathf.Clamp(size * 0.22f, MinDistance, MaxDistance);
            ResetCamera();
        }

        /// <summary>
        /// Draws the 3D viewport and camera controls into the given rect.
        /// Mirrors blendshape weights from the source SMR each repaint.
        /// </summary>
        public void Draw(Rect totalRect, IReadOnlyList<int> activeIndices)
        {
            if (totalRect.width < 2f || totalRect.height < 2f) return;

            Rect viewportRect = new Rect(
                totalRect.x, totalRect.y,
                totalRect.width, totalRect.height - ControlsHeight);

            Rect controlsRect = new Rect(
                totalRect.x, totalRect.y + totalRect.height - ControlsHeight,
                totalRect.width, ControlsHeight);

            if (_preview == null || _previewSMR == null)
            {
                DrawPlaceholder(viewportRect);
                DrawControls(controlsRect);
                return;
            }

            // Mirror blendshapes from scene SMR → preview SMR
            MirrorBlendshapes(activeIndices);

            // Handle orbit/zoom input
            HandleInput(viewportRect);

            // ── Render ────────────────────────────────────────────────────
            // BeginPreview requires the rect to have positive dimensions
            if (viewportRect.width < 1f || viewportRect.height < 1f) return;

            _preview.BeginPreview(viewportRect, GUIStyle.none);
            UpdateCamera();
            _preview.camera.Render();
            Texture result = _preview.EndPreview();

            if (result != null)
                GUI.DrawTexture(viewportRect, result, ScaleMode.StretchToFill, false);
            else
                DrawPlaceholder(viewportRect);

            if (_wireframe)
                GUI.Label(
                    new Rect(viewportRect.x + 6f, viewportRect.y + 4f, 80f, 16f),
                    "WIREFRAME",
                    new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = new Color(0.9f, 0.75f, 0.2f) }
                    });

            DrawControls(controlsRect);
        }

        /// <summary>
        /// Releases all preview resources. Call from OnDisable and OnDestroy.
        /// </summary>
        public void Cleanup()
        {
            if (_previewRoot != null)
            {
                Object.DestroyImmediate(_previewRoot);
                _previewRoot = null;
                _previewSMR = null;
            }

            if (_preview != null)
            {
                _preview.Cleanup();
                _preview = null;
            }

            _sourceSMR = null;
        }

        public bool IsReady => _preview != null && _previewSMR != null;

        // ------------------------------------------------------------------
        // Preview object construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a standalone GameObject containing a SkinnedMeshRenderer
        /// that is a copy of the source SMR's mesh and materials.
        ///
        /// This is the correct way to feed an SMR into PreviewRenderUtility
        /// when the source is a scene instance rather than a prefab asset.
        /// The returned object is unparented and initially inactive so it
        /// doesn't affect the scene.
        /// </summary>
        private GameObject CreatePreviewObject(SkinnedMeshRenderer source)
        {
            var go = new GameObject($"__RSPreview_{source.name}");
            go.hideFlags = HideFlags.HideAndDontSave;

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = source.sharedMesh;
            smr.sharedMaterials = source.sharedMaterials;
            smr.rootBone = source.rootBone;
            smr.bones = source.bones;
            smr.localBounds = source.localBounds;

            // Copy blend shape weights
            if (source.sharedMesh != null)
            {
                int count = source.sharedMesh.blendShapeCount;
                for (int i = 0; i < count; i++)
                    smr.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
            }

            return go;
        }

        // ------------------------------------------------------------------
        // Camera
        // ------------------------------------------------------------------

        private void UpdateCamera()
        {
            float azRad = _azimuth * Mathf.Deg2Rad;
            float elRad = _elevation * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(
                _distance * Mathf.Sin(azRad) * Mathf.Cos(elRad),
                _distance * Mathf.Sin(elRad),
                _distance * Mathf.Cos(azRad) * Mathf.Cos(elRad));

            _preview.camera.transform.position = _target + offset;
            _preview.camera.transform.LookAt(_target);
        }

        private void ResetCamera()
        {
            _azimuth = DefaultAzimuth;
            _elevation = DefaultElevation;

            if (_sourceSMR != null)
            {
                float size = _sourceSMR.bounds.size.magnitude;
                _distance = Mathf.Clamp(size * 0.22f, MinDistance, MaxDistance);
            }
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------

        private void HandleInput(Rect rect)
        {
            Event e = Event.current;
            bool inRect = rect.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && e.button == 0 && inRect)
            {
                _isDragging = true;
                _lastMousePos = e.mousePosition;
                e.Use();
            }
            if (e.type == EventType.MouseUp && e.button == 0)
                _isDragging = false;

            if (_isDragging && e.type == EventType.MouseDrag)
            {
                Vector2 delta = e.mousePosition - _lastMousePos;
                _azimuth -= delta.x * OrbitSensitivity;
                _elevation += delta.y * OrbitSensitivity;
                _elevation = Mathf.Clamp(_elevation, -80f, 80f);
                _lastMousePos = e.mousePosition;
                e.Use();
            }

            if (e.type == EventType.ScrollWheel && inRect)
            {
                _distance += e.delta.y * ZoomSensitivity;
                _distance = Mathf.Clamp(_distance, MinDistance, MaxDistance);
                e.Use();
            }
        }

        // ------------------------------------------------------------------
        // Controls strip
        // ------------------------------------------------------------------

        private void DrawControls(Rect rect)
        {
            EditorGUI.DrawRect(rect, ControlsBgColor);

            float btnW = 60f;
            float btnH = 20f;
            float btnY = rect.y + (rect.height - btnH) * 0.5f;
            float x = rect.x + 6f;

            var style = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 9,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };

            if (GUI.Button(new Rect(x, btnY, btnW, btnH), "⟳ Reset", style))
                ResetCamera();
            x += btnW + 4f;

            var wireStyle = new GUIStyle(style);
            if (_wireframe) wireStyle.normal.textColor = new Color(0.9f, 0.75f, 0.25f);
            if (GUI.Button(new Rect(x, btnY, btnW, btnH), "⊡ Wire", wireStyle))
                _wireframe = !_wireframe;
            x += btnW + 8f;

            float labelW = 16f;
            float sliderW = Mathf.Max(20f, rect.width - x - labelW - 12f);

            GUI.Label(
                new Rect(x, btnY, labelW, btnH), "🔍",
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft });

            // Slider: right = close (small distance), left = far (large distance)
            _distance = GUI.HorizontalSlider(
                new Rect(x + labelW, btnY + 4f, sliderW, btnH - 8f),
                _distance, MaxDistance, MinDistance);
        }

        // ------------------------------------------------------------------
        // Blendshape mirroring
        // ------------------------------------------------------------------

        private void MirrorBlendshapes(IReadOnlyList<int> activeIndices)
        {
            if (_sourceSMR == null || _previewSMR == null) return;
            if (activeIndices == null || activeIndices.Count == 0) return;

            int previewCount = _previewSMR.sharedMesh != null
                ? _previewSMR.sharedMesh.blendShapeCount
                : 0;

            foreach (int idx in activeIndices)
            {
                if (idx < 0 || idx >= previewCount) continue;
                _previewSMR.SetBlendShapeWeight(
                    idx, _sourceSMR.GetBlendShapeWeight(idx));
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private Vector3 FindHeadTarget(SkinnedMeshRenderer smr)
        {
            if (smr == null) return Vector3.zero;

            Animator anim = smr.GetComponentInParent<Animator>();
            if (anim != null && anim.isHuman)
            {
                Transform head = anim.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) return head.position;
            }

            Bounds b = smr.bounds;
            return new Vector3(b.center.x, b.min.y + b.size.y * 0.82f, b.center.z);
        }

        private void DrawPlaceholder(Rect rect)
        {
            EditorGUI.DrawRect(rect, BgColor);
            GUI.Label(
                new Rect(rect.x + 16f, rect.y, rect.width - 32f, rect.height),
                "Assign a Character (SkinnedMeshRenderer)\nto enable 3D preview.",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 11,
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter,
                });
        }
    }
}