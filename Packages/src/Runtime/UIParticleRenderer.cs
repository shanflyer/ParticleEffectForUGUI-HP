#if UNITY_2022_3_0 || UNITY_2022_3_1 || UNITY_2022_3_2 || UNITY_2022_3_3 || UNITY_2022_3_4 || UNITY_2022_3_5 || UNITY_2022_3_6 || UNITY_2022_3_7 || UNITY_2022_3_8 || UNITY_2022_3_9 || UNITY_2022_3_10
#elif UNITY_2023_1_0 || UNITY_2023_1_1 || UNITY_2023_1_2 || UNITY_2023_1_3 || UNITY_2023_1_4 || UNITY_2023_1_5 || UNITY_2023_1_6 || UNITY_2023_1_7 || UNITY_2023_1_8 || UNITY_2023_1_9
#elif UNITY_2023_1_10 || UNITY_2023_1_11 || UNITY_2023_1_12 || UNITY_2023_1_13 || UNITY_2023_1_14 || UNITY_2023_1_15 || UNITY_2023_1_16
#elif UNITY_2022_3_OR_NEWER
#define PS_BAKE_API_V2
#endif
using System;
using System.Collections.Generic;
using Coffee.UIParticleInternal;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Coffee.UIExtensions
{
    [Icon("Packages/com.coffee.ui-particle/Editor/UIParticleIcon.png")]
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("")]
    internal class UIParticleRenderer : MaskableGraphic
    {
        private static readonly CombineInstance[] s_CombineInstances = { new CombineInstance() };
        private static readonly List<Material> s_Materials = new List<Material>(2);
        private static MaterialPropertyBlock s_Mpb;
        private static readonly Vector3[] s_Corners = new Vector3[4];
        private static readonly VertexHelper s_VertexHelper = new VertexHelper();
        // CanvasRenderer only accepts 16-bit indices. Do not change Graphic.workerMesh:
        // that mesh is shared with ordinary UI components throughout the application.
        internal const int MaxUiVertices = 64999;
        private static Mesh s_ParticleMesh;
        private static Mesh particleMesh
        {
            get
            {
                if (s_ParticleMesh == null)
                {
                    s_ParticleMesh = new Mesh
                    {
                        name = "[UIParticleRenderer] UI Output Mesh",
                        hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = IndexFormat.UInt16
                    };
                }
                return s_ParticleMesh;
            }
        }
        private bool _vertexLimitReported;
        private bool _delay;
        private int _index;
        private bool _isPrevStored;
        private bool _isTrail;
        private bool _meshCleared;
        private Bounds _lastBounds;
        private Material _materialForRendering;
        private Material _modifiedMaterial;
        private UIParticle _parent;
        private ParticleSystem _particleSystem;
        private float _prevCanvasScale;
        private Vector3 _prevPsPos;
        private Vector3 _prevScale;
        private Vector2Int _prevScreenSize;
        private bool _preWarm;
        private ParticleSystemRenderer _renderer;
        private bool _originalRendererEnabled = true;
        private Material _boundMaterial;
        private Texture _boundTexture;
        private bool _boundTrails;
        private ParticleSystem _mainEmitter;
        private ParticleBakeClock _bakeClock;
        private bool _forceBake = true;
        private bool _materialsDirty = true;
        private Material _submittedMaterial;
        private int _lastBakeFrame = -1;
        // Merged mode: one renderer binds all non-trail ParticleSystems of the same UIParticle.
        private ParticleSystem[] _mergedSystems;
        private ParticleSystemRenderer[] _mergedPsRenderers;
        private bool[] _mergedOriginalRendererEnabled;
        private ParticleSystem[] _mergedMainEmitters;
        private Vector3[] _mergedPrevPos;
        private Vector3[] _mergedPrevScale;
        private bool[] _mergedDelay;
        private bool[] _mergedPreWarm;
        private bool[] _mergedPrevStored;
        private Vector2Int[] _mergedPrevScreenSize;
        private float[] _mergedPrevCanvasScale;
        private Material[] _mergedMaterials;
        private Texture[] _mergedTextures;
        // [FxUIParticle 刀7] Trail 并入合并渲染器:每系统 [quad, trail] 交错子网格,不再为 trail 单建 renderer。
        private bool[] _mergedIsTrail;
        private int[] _mergedQuadIdx;
        private int[] _mergedTrailIdx;
        private Material[] _mergedSubmeshMaterials;
        private CombineInstance[] _mergedCombines;
        private bool _mergedUniform;
        internal bool isMerged => _mergedSystems != null;
        // [FxUIParticle 刀5] Early Culling:UGUI Cull 回调缓存的几何裁剪结论(不含 bounds-empty,避免首帧被永久跳过烘焙)。
        private bool _uguiClipCulled;
        private float _nextCullProbeTime;
        // [FxUIParticle 刀6] 静态网格缓存:暂停且全部相关 Transform/Canvas 未变时整帧跳过烘焙链。
        private bool _staticValid;
        private Vector3 _staticPsPos;
        private Quaternion _staticPsRot;
        private Vector3 _staticPsScale;
        private Vector3 _staticRenPos;
        private Quaternion _staticRenRot;
        private Vector3 _staticRenScale;
        private Vector3 _staticParentScale;
        private Vector3 _staticRootScale;
        private Vector2Int _staticScreen;
        private float _staticCanvasScale;
        private Vector3 _staticParticleScale;
        private UIParticle.PositionMode _staticPositionMode;
        private float _staticViewSize;
        private bool _staticGamma;
        // [刀6] IsIdleForFastPath and the matching UpdateMesh* evaluate the same static-frame
        // predicate against identical inputs within one frame; cache the result (keyed by mode).
        private int _staticFrameCacheFrame = -1;
        private bool _staticFrameCacheMerged;
        private bool _staticFrameCacheValue;
        private Vector3[] _mergedStaticPos;
        private Quaternion[] _mergedStaticRot;
        private Vector3[] _mergedStaticScale;
        private bool[] _mergedStaticActive;

        public override Texture mainTexture => _isTrail ? null : _particleSystem.GetTextureForSprite();

        public override bool raycastTarget => false;

        private Rect rootCanvasRect
        {
            get
            {
                s_Corners[0] = transform.TransformPoint(_lastBounds.min.x, _lastBounds.min.y, 0);
                s_Corners[1] = transform.TransformPoint(_lastBounds.min.x, _lastBounds.max.y, 0);
                s_Corners[2] = transform.TransformPoint(_lastBounds.max.x, _lastBounds.max.y, 0);
                s_Corners[3] = transform.TransformPoint(_lastBounds.max.x, _lastBounds.min.y, 0);
                if (canvas != null)
                {
                    var worldToLocalMatrix = canvas.rootCanvas.transform.worldToLocalMatrix;
                    for (var i = 0; i < 4; ++i)
                    {
                        s_Corners[i] = worldToLocalMatrix.MultiplyPoint(s_Corners[i]);
                    }
                }

                var corner1 = (Vector2)s_Corners[0];
                var corner2 = (Vector2)s_Corners[0];
                for (var i = 1; i < 4; ++i)
                {
                    if (s_Corners[i].x < corner1.x)
                    {
                        corner1.x = s_Corners[i].x;
                    }
                    else if (s_Corners[i].x > corner2.x)
                    {
                        corner2.x = s_Corners[i].x;
                    }

                    if (s_Corners[i].y < corner1.y)
                    {
                        corner1.y = s_Corners[i].y;
                    }
                    else if (s_Corners[i].y > corner2.y)
                    {
                        corner2.y = s_Corners[i].y;
                    }
                }

                return new Rect(corner1, corner2 - corner1);
            }
        }

        public override Material materialForRendering
        {
            get
            {
                if (_materialForRendering == null)
                {
                    _materialForRendering = base.materialForRendering;
                }

                return _materialForRendering;
            }
        }

        public void Reset(int index = -1)
        {
            if (_mergedPsRenderers != null)
            {
                // Restore each source renderer to the exact state it had before binding.
                for (var i = 0; i < _mergedPsRenderers.Length; i++)
                {
                    if (_mergedPsRenderers[i] == null) continue;
                    var original = _mergedOriginalRendererEnabled != null
                                   && i < _mergedOriginalRendererEnabled.Length
                        ? _mergedOriginalRendererEnabled[i]
                        : true;
                    _mergedPsRenderers[i].enabled = original;
                }
            }
            else if (_renderer != null)
            {
                _renderer.enabled = _originalRendererEnabled;
            }

            _parent = null;
            _particleSystem = null;
            _renderer = null;
            _mainEmitter = null;
            ReleaseMergedMeshes();

            _mergedSystems = null;
            _mergedPsRenderers = null;
            _mergedOriginalRendererEnabled = null;
            _mergedMainEmitters = null;
            _mergedPrevPos = null;
            _mergedPrevScale = null;
            _mergedDelay = null;
            _mergedPreWarm = null;
            _mergedPrevStored = null;
            _mergedPrevScreenSize = null;
            _mergedPrevCanvasScale = null;
            _mergedMaterials = null;
            _mergedTextures = null;
            _mergedIsTrail = null;
            _mergedQuadIdx = null;
            _mergedTrailIdx = null;
            _mergedSubmeshMaterials = null;
            _uguiClipCulled = false;
            _nextCullProbeTime = 0;
            _staticValid = false;
            _meshCleared = false;
            _vertexLimitReported = false;
            _bakeClock.Reset();
            _forceBake = true;
            _lastBakeFrame = -1;
            _mergedStaticPos = null;
            _mergedStaticRot = null;
            _mergedStaticScale = null;
            _mergedStaticActive = null;
            if (0 <= index)
            {
                _index = index;
            }

            //_emitter = null;
            if (isActiveAndEnabled)
            {
                material = null;
                ClearCanvas();
                _lastBounds = new Bounds();
                enabled = false;
            }
            else
            {
                MaterialRepository.Release(ref _modifiedMaterial);
                _materialForRendering = null;
            }
        }

        private void ReleaseMergedMeshes()
        {
            if (_mergedCombines == null) return;
            for (var i = 0; i < _mergedCombines.Length; i++)
                Misc.Destroy(_mergedCombines[i].mesh);
            _mergedCombines = null;
        }

        internal void ReleaseBakeResources() { ReleaseMergedMeshes(); }

        private void EnsureMergedMeshes()
        {
            if (_mergedCombines == null) _mergedCombines = new CombineInstance[_mergedSubmeshMaterials.Length];
            for (var i = 0; i < _mergedCombines.Length; i++)
                if (_mergedCombines[i].mesh == null)
                {
                    UIParticleProfiler.current.meshesCreated++;
                    _mergedCombines[i].mesh = new Mesh
                    {
                        name = "[UIParticleRenderer] Merged Combine Instance Mesh",
                        hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = IndexFormat.UInt16
                    };
                }
        }

        protected override void OnDestroy()
        {
            // Also release if the generated renderer itself is removed/replaced.
            ReleaseMergedMeshes();
            base.OnDestroy();
        }

        internal void InvalidateMeshCache()
        {
            _staticValid = false;
            _meshCleared = false;
            // Make the next active update refill the CanvasRenderer, even with Bake30.
            _forceBake = true;
            _materialsDirty = true;
        }

        private bool AdvanceBakeClock(bool renderOnly, out float scaledStep, out float unscaledStep)
        {
            if (_parent.isPaused)
            {
                _bakeClock.Reset();
                scaledStep = unscaledStep = 0;
                _forceBake = false;
                return true;
            }
            // RenderCull must not raise the simulation cadence: keep the same bakeFPS throttle and just skip Bake/Combine/SetMesh (fps == 0 means every game frame).
            var fps = _isTrail || !Application.isPlaying ? 0 : UIParticle.bakeFPS;
            var ready = _bakeClock.Advance(Time.deltaTime, Time.unscaledDeltaTime,
                fps, _forceBake, out scaledStep, out unscaledStep, _parent.bakePhase);
            if (ready) _forceBake = false;
            return ready;
        }

        private bool ShouldSkipCulledBake()
        {
            if (UIParticle.earlyCull <= 0 || !(_parent.useMeshSharing ? _parent.groupAllClipped : _uguiClipCulled)
                || !Application.isPlaying) return false;
            // Bounds describe the last baked frame. Moving particles can re-enter
            // the clip rect without moving their emitter; periodically refresh them.
            if (_lastBounds.extents == Vector3.zero) return false;
            if (Time.unscaledTime >= _nextCullProbeTime)
            {
                _nextCullProbeTime = Time.unscaledTime + 0.1f;
                // Force this probe frame to bake even if the bake clock is not due,
                // otherwise the throttled frame would drop the probe refresh.
                _forceBake = true;
                return false;
            }
            return true;
        }

        internal bool alphaHidden => canvasRenderer.GetInheritedAlpha() <= 0.001f;
        internal bool clipHidden => _uguiClipCulled && _lastBounds.extents != Vector3.zero;
        private bool FullCullHidden => _parent.useMeshSharing ? _parent.groupAllAlphaHidden : alphaHidden;

        private Matrix4x4 GetCombineMatrix(Vector3 psPos, Vector3 scale, Matrix4x4 worldToLocal, Vector3 parentPosition)
        {
            var matrix = worldToLocal;
            if (_parent.positionMode != UIParticle.PositionMode.Absolute)
            {
                var diff = _particleSystem.transform.position - parentPosition;
                matrix *= Matrix4x4.Translate(diff.GetScaled(scale - Vector3.one));
            }
            return matrix * GetWorldMatrix(psPos, scale);
        }

        private void ClearMeshAndReplicas()
        {
            var renderers = InternalListPool<UIParticleRenderer>.Rent();
            try
            {
                if (_parent != null && _parent.useMeshSharing && _parent.canSimulate)
                    UIParticleUpdater.GetGroupedRenderers(_parent.groupId, _index, renderers);
                if (!renderers.Contains(this)) renderers.Add(this);
                for (var i = 0; i < renderers.Count; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    r.ClearCanvas();
                    r._lastBounds = new Bounds();
                    r._meshCleared = true;
                    r._staticValid = false;
                }
            }
            finally { InternalListPool<UIParticleRenderer>.Return(ref renderers); }
        }

        internal void ClearMesh()
        {
            if (!_meshCleared) ClearMeshAndReplicas();
        }

        internal static bool CanMerge(List<ParticleSystem> systems)
        {
            Material firstMaterial = null;
            Texture firstTexture = null;
            var first = true;
            for (var i = 0; i < systems.Count; i++)
            {
                var ps = systems[i];
                if (ps == null || !ps.TryGetComponent<ParticleSystemRenderer>(out var renderer)) continue;
                var mat = renderer.sharedMaterial;
                var texture = ps.GetTextureForSprite();
                var effectiveTexture = texture != null ? texture : mat != null ? mat.mainTexture : null;
                if (!first && (mat != firstMaterial || effectiveTexture != firstTexture)) return false;
                first = false;
                firstMaterial = mat;
                firstTexture = effectiveTexture;
                // CanvasRenderer has a single texture override. Mixed draw materials
                // and sprite/trail textures require separate MaskableGraphics.
                if (ps.trails.enabled && (renderer.trailMaterial != mat
                    || (mat != null && mat.mainTexture != effectiveTexture))) return false;
            }
            return !first;
        }

        internal bool bindingIsInvalid
        {
            get
            {
                if (_parent == null) return false; // Spare renderer retained for reuse.
                if (_particleSystem == null || _renderer == null) return true;
                if (_mergedSystems == null)
                    return _boundTrails != _particleSystem.trails.enabled
                           || _boundMaterial != (_isTrail ? _renderer.trailMaterial : _renderer.sharedMaterial)
                           || _boundTexture != mainTexture;
                if (_parent.m_AnimatableProperties.Length != 0) return true;
                Texture boundSprite = null;
                var foundBoundSystem = false;
                Material lastMaterial = null;
                Texture lastMaterialTexture = null;
                var hasMaterialTexture = false;
                for (var i = 0; i < _mergedSystems.Length; i++)
                {
                    var ps = _mergedSystems[i];
                    var renderer = _mergedPsRenderers[i];
                    var hasTrails = _mergedIsTrail[i];
                    if (ps == null || renderer == null
                        || ps.trails.enabled != hasTrails
                        || renderer.sharedMaterial != _mergedMaterials[i]) return true;
                    var sprite = ps.GetTextureForSprite();
                    if (ReferenceEquals(ps, _particleSystem))
                    {
                        boundSprite = sprite;
                        foundBoundSystem = true;
                    }
                    var mat = _mergedMaterials[i];
                    // Reuse only within this validation pass, so runtime texture edits
                    // are still observed next time, even within the same Unity frame.
                    if ((sprite == null || hasTrails)
                        && (!hasMaterialTexture || !ReferenceEquals(mat, lastMaterial)))
                    {
                        lastMaterialTexture = mat != null ? mat.mainTexture : null;
                        lastMaterial = mat;
                        hasMaterialTexture = true;
                    }
                    if ((sprite != null ? sprite : lastMaterialTexture) != _mergedTextures[i]) return true;
                    if (hasTrails && (renderer.trailMaterial != _mergedSubmeshMaterials[_mergedTrailIdx[i]]
                        || (mat != null && lastMaterialTexture != _mergedTextures[i]))) return true;
                }
                return _boundTexture != (_isTrail ? null : foundBoundSystem ? boundSprite : mainTexture);
            }
        }

        private void ReportVertexLimit(int vertexCount)
        {
            if (_vertexLimitReported) return;
            _vertexLimitReported = true;
            Debug.LogWarningFormat(this,
                "[UIParticle] UI mesh vertex limit exceeded ({0} > {1}). Reduce particles/trails; oversized meshes are not submitted.",
                vertexCount, MaxUiVertices);
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            hideFlags = UIParticleProjectSettings.globalHideFlags;
            if (s_CombineInstances[0].mesh == null)
            {
                s_CombineInstances[0].mesh = new Mesh
                {
                    name = "[UIParticleRenderer] Combine Instance Mesh",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void RegisterSharedMeshCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseSharedMeshes;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseSharedMeshes;
        }

        private static void ReleaseSharedMeshes()
        {
            Misc.Destroy(s_ParticleMesh);
            s_ParticleMesh = null;
            Misc.Destroy(s_CombineInstances[0].mesh);
            s_CombineInstances[0].mesh = null;
            s_VertexHelper.Clear();
        }
#endif

        protected override void OnDisable()
        {
            base.OnDisable();

            MaterialRepository.Release(ref _modifiedMaterial);
            _materialForRendering = null;
            _isPrevStored = false;
        }

        public static UIParticleRenderer AddRenderer(UIParticle parent, int index)
        {
            // Create renderer object.
            var go = new GameObject("[generated] UIParticleRenderer", typeof(UIParticleRenderer))
            {
                hideFlags = UIParticleProjectSettings.globalHideFlags,
                layer = parent.gameObject.layer
            };

            // Set parent.
            var transform = go.transform;
            transform.SetParent(parent.transform, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            // Add renderer component.
            var renderer = go.GetComponent<UIParticleRenderer>();
            renderer._parent = parent;
            renderer._index = index;

            return renderer;
        }

        /// <summary>
        /// Perform material modification in this function.
        /// </summary>
        public override Material GetModifiedMaterial(Material baseMaterial)
        {
            if (!IsActive() || !_parent)
            {
                MaterialRepository.Release(ref _modifiedMaterial);
                return baseMaterial;
            }

            var modifiedMaterial = base.GetModifiedMaterial(baseMaterial);

            //
            var texture = mainTexture;
            if (texture == null && _parent.m_AnimatableProperties.Length == 0)
            {
                MaterialRepository.Release(ref _modifiedMaterial);
                return modifiedMaterial;
            }

            var hash = new Hash128(
                modifiedMaterial ? (uint)modifiedMaterial.GetHashCode() : 0,
                texture ? (uint)texture.GetHashCode() : 0,
                0 < _parent.m_AnimatableProperties.Length ? (uint)GetHashCode() : 0,
#if UNITY_EDITOR
                (uint)EditorJsonUtility.ToJson(modifiedMaterial).GetHashCode()
#else
                0
#endif
            );
            if (!MaterialRepository.Valid(hash, _modifiedMaterial))
            {
                MaterialRepository.Get(hash, ref _modifiedMaterial, x => new Material(x.mat)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    mainTexture = x.texture ? x.texture : x.mat.mainTexture
                }, (mat: modifiedMaterial, texture));
            }

            return _modifiedMaterial;
        }

        public void Set(UIParticle parent, ParticleSystem ps, bool isTrail, ParticleSystem mainEmitter)
        {
            _parent = parent;
            maskable = parent.maskable;

            gameObject.layer = parent.gameObject.layer;

            _particleSystem = ps;
            _preWarm = _particleSystem.main.prewarm;

#if UNITY_EDITOR
            if (Application.isPlaying)
#endif
            {
                if (_particleSystem.isPlaying || _preWarm)
                {
                    _particleSystem.Clear();
                    _particleSystem.Pause();
                }
            }

            ps.TryGetComponent(out _renderer);
            if (_renderer == null) { Reset(); return; }
            _originalRendererEnabled = _renderer.enabled;
            _renderer.enabled = false;
            _isTrail = isTrail;
            _renderer.GetSharedMaterials(s_Materials);
            var materialIndex = isTrail ? 1 : 0;
            material = materialIndex < s_Materials.Count ? s_Materials[materialIndex] : null;
            _boundMaterial = materialIndex < s_Materials.Count ? s_Materials[materialIndex] : null;
            _boundTexture = mainTexture;
            _boundTrails = ps.trails.enabled;
            s_Materials.Clear();

            // Support sprite.
            var tsa = ps.textureSheetAnimation;
            if (tsa.mode == ParticleSystemAnimationMode.Sprites && tsa.uvChannelMask == 0)
            {
                tsa.uvChannelMask = UVChannelFlags.UV0;
            }

            _prevScale = GetWorldScale();
            _prevPsPos = _particleSystem.transform.position;
            _prevScreenSize = new Vector2Int(Screen.width, Screen.height);
            _prevCanvasScale = canvas ? canvas.scaleFactor : 1f;
            _delay = true;
            _mainEmitter = mainEmitter;

            canvasRenderer.SetTexture(null);

            enabled = true;
            // Binding must establish a valid clipping state even when the parent maskable value did not change.
            RecalculateClipping();
        }

        /// <summary>
        /// Bind compatible ParticleSystems and their trails to a single renderer.
        /// The caller checks material/texture compatibility and animatable properties.
        /// </summary>
        public void SetMerged(UIParticle parent, List<ParticleSystem> particleSystems)
        {
            _parent = parent;
            maskable = parent.maskable;

            gameObject.layer = parent.gameObject.layer;

            var count = 0;
            var trailCount = 0;
            for (var i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;
                count++;
                if (ps.trails.enabled) trailCount++;
            }

            _mergedSystems = new ParticleSystem[count];
            _mergedPsRenderers = new ParticleSystemRenderer[count];
            _mergedOriginalRendererEnabled = new bool[count];
            _mergedMainEmitters = new ParticleSystem[count];
            _mergedPrevPos = new Vector3[count];
            _mergedPrevScale = new Vector3[count];
            _mergedDelay = new bool[count];
            for (var i = 0; i < count; i++)
                _mergedDelay[i] = true; // Match the single-system binding delay on the first simulated frame.
            _mergedPreWarm = new bool[count];
            _mergedPrevStored = new bool[count];
            _mergedPrevScreenSize = new Vector2Int[count];
            _mergedPrevCanvasScale = new float[count];
            _mergedMaterials = new Material[count];
            _mergedTextures = new Texture[count];
            _mergedIsTrail = new bool[count];
            _mergedQuadIdx = new int[count];
            _mergedTrailIdx = new int[count];
            _mergedStaticPos = new Vector3[count];
            _mergedStaticRot = new Quaternion[count];
            _mergedStaticScale = new Vector3[count];
            _mergedStaticActive = new bool[count];
            // Sub-meshes are interleaved per system ([quad, trail]) so the combine order
            // matches the original per-system draw order (quads first, then its trail).
            _mergedSubmeshMaterials = new Material[count + trailCount];

            var uniform = true;
            Material firstMat = null;
            var k = 0;
            var submesh = 0;
            for (var i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;

                _mergedSystems[k] = ps;
                _mergedPreWarm[k] = ps.main.prewarm;
                _mergedMainEmitters[k] = ps.GetMainEmitter(particleSystems);

#if UNITY_EDITOR
                if (Application.isPlaying)
#endif
                {
                    if (ps.isPlaying || _mergedPreWarm[k])
                    {
                        ps.Clear();
                        ps.Pause();
                    }
                }

                ps.TryGetComponent(out _mergedPsRenderers[k]);
                _mergedOriginalRendererEnabled[k] = _mergedPsRenderers[k].enabled;
                _mergedPsRenderers[k].enabled = false;

                _mergedPsRenderers[k].GetSharedMaterials(s_Materials);
                _mergedMaterials[k] = 0 < s_Materials.Count ? s_Materials[0] : null;
                var spriteTexture = ps.GetTextureForSprite();
                _mergedTextures[k] = spriteTexture != null ? spriteTexture
                    : _mergedMaterials[k] != null ? _mergedMaterials[k].mainTexture : null;
                if (firstMat == null)
                {
                    firstMat = _mergedMaterials[k];
                }
                else if (_mergedMaterials[k] != firstMat)
                {
                    uniform = false;
                }

                _mergedQuadIdx[k] = submesh;
                _mergedSubmeshMaterials[submesh++] = _mergedMaterials[k];

                // [FxUIParticle 刀7] Trail-enabled systems get an extra trail sub-mesh here
                // instead of a separate UIParticleRenderer (original merged mode).
                _mergedTrailIdx[k] = -1;
                if (ps.trails.enabled)
                {
                    _mergedIsTrail[k] = true;
                    _mergedTrailIdx[k] = submesh;
                    var trailMat = 1 < s_Materials.Count ? s_Materials[1] : _mergedMaterials[k];
                    _mergedSubmeshMaterials[submesh++] = trailMat;
                    if (trailMat != firstMat) uniform = false;
                }
                s_Materials.Clear();

                // Support sprite.
                var tsa = ps.textureSheetAnimation;
                if (tsa.mode == ParticleSystemAnimationMode.Sprites && tsa.uvChannelMask == 0)
                {
                    tsa.uvChannelMask = UVChannelFlags.UV0;
                }

                _particleSystem = ps;
                _mergedPrevScale[k] = GetWorldScale();
                _mergedPrevPos[k] = ps.transform.position;
                _mergedPrevStored[k] = false;
                k++;
            }

            _mergedUniform = uniform;
            _isTrail = false;
            _mainEmitter = null;
            _particleSystem = 0 < count ? _mergedSystems[0] : null;
            _renderer = 0 < count ? _mergedPsRenderers[0] : null;
            material = firstMat;
            _boundTexture = mainTexture;

            _prevScreenSize = new Vector2Int(Screen.width, Screen.height);
            _prevCanvasScale = canvas ? canvas.scaleFactor : 1f;
            // Keep a per-source resolution baseline: each system must observe a screen/scale
            // change exactly once, even if other sources are inactive or simulation owners.
            for (var i = 0; i < count; i++)
            {
                _mergedPrevScreenSize[i] = _prevScreenSize;
                _mergedPrevCanvasScale[i] = _prevCanvasScale;
            }
            _delay = false;

            canvasRenderer.SetTexture(null);

            enabled = true;
            // Binding must establish a valid clipping state even when the parent maskable value did not change.
            RecalculateClipping();
        }

        private int _openSamples;
        private byte[] _sampleStages;
        private long[] _sampleStarts;

        private void BeginSample(string name)
        {
            Profiler.BeginSample(name);
            var stage = UIParticleProfiler.timingEnabled ? UIParticleProfiler.Stage(name) : (byte)0;
            if (stage != 0 && (_sampleStages == null || _openSamples >= _sampleStages.Length))
            {
                Array.Resize(ref _sampleStages, Math.Max(8, _openSamples + 1));
                Array.Resize(ref _sampleStarts, _sampleStages.Length);
            }
            if (_sampleStages != null && _openSamples < _sampleStages.Length)
            {
                _sampleStages[_openSamples] = stage;
                if (stage != 0) _sampleStarts[_openSamples] = UIParticleProfiler.Timestamp();
            }
            _openSamples++;
        }

        private void EndSample()
        {
            if (_openSamples <= 0) return;
            _openSamples--;
            if (_sampleStages != null && _openSamples < _sampleStages.Length)
                UIParticleProfiler.EndStage(_sampleStages[_openSamples], _sampleStarts[_openSamples]);
            Profiler.EndSample();
        }

        public void UpdateMesh(Camera bakeCamera)
        {
            var previousDepth = _openSamples;
            try { UpdateMeshInternal(bakeCamera); }
            finally
            {
                while (_openSamples > previousDepth) EndSample();
            }
        }

        private void UpdateMeshInternal(Camera bakeCamera)
        {
            // Merged mode: all non-trail systems bake into this single renderer.
            if (_mergedSystems != null)
            {
                try { UpdateMeshMerged(bakeCamera); }
                finally
                {
                    _particleSystem = _mergedSystems != null && _mergedSystems.Length > 0 ? _mergedSystems[0] : null;
                    _mainEmitter = null;
                    _isTrail = false;
                }
                return;
            }

            // No particle to render: Clear mesh.
            if (
                !isActiveAndEnabled || !_particleSystem || !_parent
                || !_particleSystem.gameObject.activeInHierarchy
                || !canvasRenderer || !canvas || !bakeCamera
                || _parent.meshSharing == UIParticle.MeshSharing.Replica
                || !transform.lossyScale.GetScaled(_parent.scale3DForCalc).IsVisible() // Scale is not visible.
                || (!_particleSystem.IsAlive() && !_particleSystem.isPlaying) // No particle.
                || (_isTrail && !_particleSystem.trails.enabled) // Trail, but it is not enabled.
            )
            {
                // Skip clearing the mesh if it's already cleared.
                if (_meshCleared) return;

                ClearMeshAndReplicas();

                return;
            }

            // [FxUIParticle 刀6] 静态网格缓存:暂停且相关 Transform/Canvas 全部未变 → 整帧跳过 Simulate/Bake/Combine/SetMesh,网格零成本保持。
            if (UIParticle.staticMeshCache && _staticValid && EvaluateStaticFrameCached(false))
            {
                return;
            }

            // [FxUIParticle 刀5] FullCull:CanvasGroup 累积 alpha≈0(整页隐藏)→ 模拟与烘焙全停,恢复可见后从停点继续。
            if (UIParticle.earlyCull > 1 && FullCullHidden)
            {
                return;
            }

            // [FxUIParticle 刀5] RenderCull:UGUI 已判裁剪(出屏/RectMask2D 全裁)→ 模拟继续(时间连续),仅跳过 Bake/Combine/SetMesh。
            // 共享组 Primary 承担推送职责,豁免。
            bool renderOnly = ShouldSkipCulledBake();
            if (renderOnly) _staticValid = false;

            // Trails bake from the same simulation tick as their particle mesh.
            if (_isTrail && !_forceBake && !renderOnly && Application.isPlaying)
            {
                var body = _parent.GetRendererIfExists(_index - 1);
                if (body != null && body._particleSystem == _particleSystem
                    && body._lastBakeFrame != Time.frameCount) return;
            }
            float scaledStep, unscaledStep;
            if (!AdvanceBakeClock(renderOnly, out scaledStep, out unscaledStep)) return;
            var bakeSimDt = _particleSystem.main.useUnscaledTime ? unscaledStep : scaledStep;

            // Reset custom data.
            // var customData = _particleSystem.customData;
            // if (!customData.enabled || customData.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Disabled)
            // {
            //     customData.SetVector(ParticleSystemCustomData.Custom1, 0, 0);
            //     customData.SetVector(ParticleSystemCustomData.Custom1, 1, 0);
            //     customData.SetVector(ParticleSystemCustomData.Custom1, 2, 0);
            //     customData.SetVector(ParticleSystemCustomData.Custom1, 3, 0);
            // }
            //
            // if (!customData.enabled || customData.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Disabled)
            // {
            //     customData.SetVector(ParticleSystemCustomData.Custom2, 0, 0);
            //     customData.SetVector(ParticleSystemCustomData.Custom2, 1, 0);
            //     customData.SetVector(ParticleSystemCustomData.Custom2, 2, 0);
            //     customData.SetVector(ParticleSystemCustomData.Custom2, 3, 0);
            // }

            _meshCleared = false;
            var main = _particleSystem.main;
            var scale = GetWorldScale();
            var psPos = _particleSystem.transform.position;

            // Simulate particles.
            BeginSample("[UIParticle] Bake Mesh > Simulate Particles");
            if (!_isTrail && _parent.canSimulate && !_mainEmitter)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    SimulateForEditor(psPos - _prevPsPos, scale);
                }
                else
#endif
                {
                    ResolveResolutionChange(psPos, scale);

                    // fix: second and subsequent bursts not displayed when world simulation and non-looping. (#326)
                    if (!_particleSystem.IsLocalSpace() && !main.loop && _particleSystem.time == 0)
                    {
                        _delay = true;
                    }

                    Simulate(scale, _parent.isPaused || _delay, bakeSimDt);

                    if (_delay && !_parent.isPaused)
                    {
                        Simulate(scale, _parent.isPaused, bakeSimDt);
                    }

                    // When the ParticleSystem simulation is complete, stop it.
                    if (!main.loop
                        && main.duration <= _particleSystem.time
                        && (_particleSystem.IsAlive() || _particleSystem.particleCount == 0)
                       )
                    {
                        _particleSystem.Stop(false);
                    }
                }

                _prevScale = scale;
                _prevPsPos = psPos;
                _delay = false;
                _isPrevStored = true;
            }

            EndSample();

            // [FxUIParticle 刀5] RenderCull:模拟已推进(时间连续),本帧不烘焙不提交,恢复可见后下一帧恢复烘焙。
            if (renderOnly) return;

            _lastBakeFrame = Time.frameCount;
            // Bake mesh.
            BeginSample("[UIParticleRenderer] Bake Mesh");
            s_CombineInstances[0].mesh.Clear(false);

            // Assertion failed on expression: 'ps->array_size()' #278
            var extends = s_CombineInstances[0].mesh.bounds.extents.x;
            if (!float.IsNaN(extends) && !float.IsInfinity(extends) && 0 < extends)
            {
                s_CombineInstances[0].mesh.RecalculateBounds();
            }

            if (_isTrail && _parent.canSimulate && 0 < _particleSystem.particleCount)
            {
#if PS_BAKE_API_V2
                _renderer.BakeTrailsMesh(s_CombineInstances[0].mesh, bakeCamera,
                    ParticleSystemBakeMeshOptions.BakeRotationAndScale);
#else
                _renderer.BakeTrailsMesh(s_CombineInstances[0].mesh, bakeCamera, true);
#endif
            }
            else if (!_isTrail && _renderer.CanBakeMesh())
            {
                _particleSystem.ValidateShape();
#if PS_BAKE_API_V2
                _renderer.BakeMesh(s_CombineInstances[0].mesh, bakeCamera,
                    ParticleSystemBakeMeshOptions.BakeRotationAndScale);
#else
                _renderer.BakeMesh(s_CombineInstances[0].mesh, bakeCamera, true);
#endif
            }

            UIParticle.s_FrameBakedVerts += s_CombineInstances[0].mesh.vertexCount;
                UIParticleProfiler.current.bakedVertices += s_CombineInstances[0].mesh.vertexCount;
                UIParticleProfiler.current.bakeOps++;
            UIParticle.s_FrameBakeOps++;

            // Too many vertices to render.
            if (MaxUiVertices < s_CombineInstances[0].mesh.vertexCount)
            {
                ReportVertexLimit(s_CombineInstances[0].mesh.vertexCount);
                s_CombineInstances[0].mesh.Clear(false);
            }

            EndSample();

            // Combine mesh to transform. ([ParticleSystem local ->] world -> renderer local)
            BeginSample("[UIParticleRenderer] Combine Mesh");
            if (_parent.canSimulate)
            {
                if (_parent.positionMode == UIParticle.PositionMode.Absolute)
                {
                    s_CombineInstances[0].transform =
                        canvasRenderer.transform.worldToLocalMatrix
                        * GetWorldMatrix(psPos, scale);
                }
                else
                {
                    var diff = _particleSystem.transform.position - _parent.transform.position;
                    s_CombineInstances[0].transform =
                        canvasRenderer.transform.worldToLocalMatrix
                        * Matrix4x4.Translate(diff.GetScaled(scale - Vector3.one))
                        * GetWorldMatrix(psPos, scale);
                }

                particleMesh.CombineMeshes(s_CombineInstances, true, true);

                // Convert linear color to gamma color.
                if (UIParticleProjectSettings.autoColorCorrection && canvas.ShouldGammaToLinearInMesh())
                {
                    particleMesh.LinearToGamma();
                }

                var meshModified = false;
                var components = InternalListPool<Component>.Rent();
                try
                {
                    GetComponents(typeof(IMeshModifier), components);
                    if (0 < components.Count)
                    {
                        particleMesh.CopyTo(s_VertexHelper);
                        for (var i = 0; i < components.Count; i++)
                            ((IMeshModifier)components[i]).ModifyMesh(s_VertexHelper);
                        if (MaxUiVertices < s_VertexHelper.currentVertCount)
                        {
                            ReportVertexLimit(s_VertexHelper.currentVertCount);
                            particleMesh.Clear(false);
                        }
                        else s_VertexHelper.FillMesh(particleMesh);
                        // FillMesh rewrites the vertices without recomputing bounds, and an
                        // IMeshModifier may move them. Recompute only here: Mesh.CombineMeshes
                        // already produces correct bounds when no modifier ran.
                        meshModified = true;
                    }
                }
                finally { InternalListPool<Component>.Return(ref components); }

                if (meshModified) particleMesh.RecalculateBounds();
                var bounds = particleMesh.bounds;
                var center = bounds.center;
                center.z = 0;
                bounds.center = center;
                var extents = bounds.extents;
                extents.z = 0;
                bounds.extents = extents;
                particleMesh.bounds = bounds;
                _lastBounds = bounds;
            }

            EndSample();

            if (_parent == null || !isActiveAndEnabled) return;
            // Update animatable material properties.
            BeginSample("[UIParticleRenderer] Update Animatable Material Properties");
            UpdateMaterialProperties();
            EndSample();

            if (_parent == null || !isActiveAndEnabled) return;
            // Get grouped renderers.
            BeginSample("[UIParticleRenderer] Set Mesh");
            var renderers = InternalListPool<UIParticleRenderer>.Rent();
            try
            {
                if (_parent.useMeshSharing)
                {
                    UIParticleUpdater.GetGroupedRenderers(_parent.groupId, _index, renderers);
                }

                for (var i = 0; i < renderers.Count; i++)
                {
                    var r = renderers[i];
                    if (r == null || r == this || !r.isActiveAndEnabled || !r._parent.canRender) continue;

                    UIParticleProfiler.current.setMeshOps++;
                    r.canvasRenderer.SetMesh(particleMesh);
                    r._lastBounds = _lastBounds;
                    r.SetCanvasRendererMaterials(r.canvasRenderer);
                }

            }
            finally { InternalListPool<UIParticleRenderer>.Return(ref renderers); }

            if (_parent != null && _parent.canRender)
            {
                UIParticleProfiler.current.setMeshOps++;
                canvasRenderer.SetMesh(particleMesh);
                SetCanvasRendererMaterials(canvasRenderer);
            }
            else
            {
                ClearCanvas();
            }

            EndSample();

            // [FxUIParticle 刀6] 烘焙完成,刷新静态缓存快照。
            if (UIParticle.staticMeshCache) TakeStaticSnapshotSingle();
        }

        private void UpdateMeshMerged(Camera bakeCamera)
        {
            // Binding-level clear check only; per-system alive checks happen in the loop below.
            if (
                !isActiveAndEnabled || _mergedSystems == null || _mergedSystems.Length == 0 || !_parent
                || !canvasRenderer || !canvas || !bakeCamera
                || _parent.meshSharing == UIParticle.MeshSharing.Replica
                || !transform.lossyScale.GetScaled(_parent.scale3DForCalc).IsVisible() // Scale is not visible.
            )
            {
                if (_meshCleared) return;

                ClearMeshAndReplicas();

                return;
            }

            // [FxUIParticle 刀6] 静态网格缓存:全部系统暂停且相关 Transform/Canvas 全部未变 → 整帧跳过烘焙链(合并网格保持)。
            if (UIParticle.staticMeshCache && _staticValid && EvaluateStaticFrameCached(true))
            {
                return;
            }

            // [FxUIParticle 刀5] FullCull:CanvasGroup 累积 alpha≈0(整页隐藏)→ 模拟与烘焙全停,恢复可见后从停点继续。
            if (UIParticle.earlyCull > 1 && FullCullHidden)
            {
                return;
            }

            // [FxUIParticle 刀5] RenderCull:UGUI 已判裁剪 → 模拟继续(时间连续),仅跳过 Bake/Combine/SetMesh。共享组 Primary 豁免。
            bool renderOnly = ShouldSkipCulledBake();
            if (renderOnly) _staticValid = false;

            float scaledStep, unscaledStep;
            if (!AdvanceBakeClock(renderOnly, out scaledStep, out unscaledStep)) return;

            _meshCleared = false;

            EnsureMergedMeshes();

            // Per-frame values shared by every source in this merged renderer: the canvas
            // transform and parent position do not change across the loop. GetWorldScale
            // stays per-source because it reads that system's scalingMode.
            var combineWorldToLocal = canvasRenderer.transform.worldToLocalMatrix;
            var parentPosition = _parent.transform.position;

            for (var k = 0; k < _mergedSystems.Length; k++)
            {
                var ps = _mergedSystems[k];

                // Swap per-system state in: Simulate / ResolveResolutionChange / GetWorldMatrix read instance fields.
                _particleSystem = ps;
                _mainEmitter = _mergedMainEmitters[k];
                _isTrail = false; // The particle mesh and trail use separate transforms.
                _preWarm = _mergedPreWarm[k];
                _delay = _mergedDelay[k];
                _prevScale = _mergedPrevScale[k];
                _prevPsPos = _mergedPrevPos[k];
                _isPrevStored = _mergedPrevStored[k];
                _prevScreenSize = _mergedPrevScreenSize[k];
                _prevCanvasScale = _mergedPrevCanvasScale[k];

                if (ps == null || !ps.gameObject.activeInHierarchy || (!ps.IsAlive() && !ps.isPlaying))
                {
                    // No particle: keep this system's sub-meshes cleared.
                    _mergedCombines[_mergedQuadIdx[k]].mesh.Clear(false);
                    if (0 <= _mergedTrailIdx[k])
                    {
                        _mergedCombines[_mergedTrailIdx[k]].mesh.Clear(false);
                    }

                    continue;
                }

                var main = ps.main;
                var bakeSimDt = main.useUnscaledTime ? unscaledStep : scaledStep;
                var scale = GetWorldScale();
                var psPos = ps.transform.position;

                // Simulate particles. (same as the single-system path)
                BeginSample("[UIParticle] Bake Mesh > Simulate Particles");
                if (_parent.canSimulate && !_mainEmitter)
                {
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                    {
                        SimulateForEditor(psPos - _prevPsPos, scale);
                    }
                    else
#endif
                    {
                        ResolveResolutionChange(psPos, scale);

                        // fix: second and subsequent bursts not displayed when world simulation and non-looping. (#326)
                        if (!ps.IsLocalSpace() && !main.loop && ps.time == 0)
                        {
                            _delay = true;
                        }

                        Simulate(scale, _parent.isPaused || _delay, bakeSimDt);

                        if (_delay && !_parent.isPaused)
                        {
                            Simulate(scale, _parent.isPaused, bakeSimDt);
                        }

                        // When the ParticleSystem simulation is complete, stop it.
                        if (!main.loop
                            && main.duration <= ps.time
                            && (ps.IsAlive() || ps.particleCount == 0)
                           )
                        {
                            ps.Stop(false);
                        }
                    }
                    _prevScale = scale;
                    _prevPsPos = psPos;
                    _delay = false;
                    _isPrevStored = true;
                }

                EndSample();

                // [FxUIParticle 刀5] RenderCull:模拟已推进,跳过本系统烘焙,状态照常持久化。
                if (renderOnly)
                {
                    _mergedPrevScale[k] = _prevScale;
                    _mergedPrevPos[k] = _prevPsPos;
                    _mergedDelay[k] = _delay;
                    _mergedPreWarm[k] = _preWarm;
                    _mergedPrevStored[k] = _isPrevStored;
                    _mergedPrevScreenSize[k] = _prevScreenSize;
                    _mergedPrevCanvasScale[k] = _prevCanvasScale;
                    continue;
                }

                _lastBakeFrame = Time.frameCount;
                // Bake mesh into this system's sub-mesh.
                BeginSample("[UIParticleRenderer] Bake Mesh");
                var combineMesh = _mergedCombines[_mergedQuadIdx[k]].mesh;
                combineMesh.Clear(false);

                // Assertion failed on expression: 'ps->array_size()' #278
                var extends = combineMesh.bounds.extents.x;
                if (!float.IsNaN(extends) && !float.IsInfinity(extends) && 0 < extends)
                {
                    combineMesh.RecalculateBounds();
                }

                if (_mergedPsRenderers[k].CanBakeMesh())
                {
                    ps.ValidateShape();
#if PS_BAKE_API_V2
                    _mergedPsRenderers[k].BakeMesh(combineMesh, bakeCamera,
                        ParticleSystemBakeMeshOptions.BakeRotationAndScale);
#else
                    _mergedPsRenderers[k].BakeMesh(combineMesh, bakeCamera, true);
#endif
                }

                UIParticle.s_FrameBakedVerts += combineMesh.vertexCount;
                UIParticleProfiler.current.bakedVertices += combineMesh.vertexCount;
                UIParticleProfiler.current.bakeOps++;
                UIParticle.s_FrameBakeOps++;

                // Too many vertices to render.
                if (MaxUiVertices < combineMesh.vertexCount)
                {
                    ReportVertexLimit(combineMesh.vertexCount);
                    combineMesh.Clear(false);
                }

                // [FxUIParticle 刀7] Trail ribbon bake into the system's trail sub-mesh.
                // The simulation has already advanced in this loop; the ribbon bake itself
                // mirrors the single-path trail renderer (canSimulate + particleCount gate).
                if (0 <= _mergedTrailIdx[k])
                {
                    var trailMesh = _mergedCombines[_mergedTrailIdx[k]].mesh;
                    trailMesh.Clear(false);

                    var trailExtends = trailMesh.bounds.extents.x;
                    if (!float.IsNaN(trailExtends) && !float.IsInfinity(trailExtends) && 0 < trailExtends)
                    {
                        trailMesh.RecalculateBounds();
                    }

                    if (_parent.canSimulate && 0 < ps.particleCount)
                    {
#if PS_BAKE_API_V2
                        _mergedPsRenderers[k].BakeTrailsMesh(trailMesh, bakeCamera,
                            ParticleSystemBakeMeshOptions.BakeRotationAndScale);
#else
                        _mergedPsRenderers[k].BakeTrailsMesh(trailMesh, bakeCamera, true);
#endif
                    }

                    UIParticle.s_FrameBakedVerts += trailMesh.vertexCount;
                UIParticleProfiler.current.bakedVertices += trailMesh.vertexCount;
                UIParticleProfiler.current.bakeOps++;
                    UIParticle.s_FrameBakeOps++;

                    if (MaxUiVertices < trailMesh.vertexCount)
                    {
                        ReportVertexLimit(trailMesh.vertexCount);
                        trailMesh.Clear(false);
                    }
                }

                EndSample();

                // World-space trails can differ from their emitter's simulation space.
                _isTrail = false;
                _mergedCombines[_mergedQuadIdx[k]].transform = GetCombineMatrix(psPos, scale, combineWorldToLocal, parentPosition);
                if (0 <= _mergedTrailIdx[k])
                {
                    _isTrail = true;
                    _mergedCombines[_mergedTrailIdx[k]].transform = GetCombineMatrix(psPos, scale, combineWorldToLocal, parentPosition);
                    _isTrail = false;
                }

                // Persist per-system state (ResolveResolutionChange/Simulate mutated the swapped fields).
                _mergedPrevScale[k] = _prevScale;
                _mergedPrevPos[k] = _prevPsPos;
                _mergedDelay[k] = _delay;
                _mergedPreWarm[k] = _preWarm;
                _mergedPrevStored[k] = _isPrevStored;
                _mergedPrevScreenSize[k] = _prevScreenSize;
                _mergedPrevCanvasScale[k] = _prevCanvasScale;
            }

            // Leave a deterministic binding for mainTexture/materialForRendering getters.
            _particleSystem = _mergedSystems[0];
            _mainEmitter = null;
            _isTrail = false;

            // [FxUIParticle 刀5] RenderCull:不合并不上传,保持上一帧网格(该渲染器当前被 UGUI 裁剪)。
            if (renderOnly)
            {
                return;
            }

            // Combine all system sub-meshes.
            BeginSample("[UIParticleRenderer] Combine Mesh");
            if (_parent.canSimulate)
            {
                long totalVerts = 0;
                for (var i = 0; i < _mergedCombines.Length; i++)
                {
                    totalVerts += _mergedCombines[i].mesh.vertexCount;
                }

                if (MaxUiVertices < totalVerts)
                {
                    // Rebind the whole sharing group to the original per-system layout
                    // on the next frame; never submit a UInt32 mesh to CanvasRenderer.
                    UIParticleUpdater.RequestUnmergedFallback(_parent);
                    if (!_vertexLimitReported)
                    {
                        _vertexLimitReported = true;
                        Debug.LogWarningFormat(this,
                            "[UIParticle] Merged mesh has {0} vertices. Falling back to separate renderers for this sharing group.", totalVerts);
                    }
                    EndSample();
                    return;
                }

                particleMesh.CombineMeshes(_mergedCombines, _mergedUniform, true);

                // Convert linear color to gamma color.
                if (UIParticleProjectSettings.autoColorCorrection && canvas.ShouldGammaToLinearInMesh())
                {
                    particleMesh.LinearToGamma();
                }

                var meshModified = false;
                var components = InternalListPool<Component>.Rent();
                try
                {
                    GetComponents(typeof(IMeshModifier), components);
                    if (0 < components.Count)
                    {
                        particleMesh.CopyTo(s_VertexHelper);
                        for (var i = 0; i < components.Count; i++)
                            ((IMeshModifier)components[i]).ModifyMesh(s_VertexHelper);
                        if (MaxUiVertices < s_VertexHelper.currentVertCount)
                        {
                            ReportVertexLimit(s_VertexHelper.currentVertCount);
                            particleMesh.Clear(false);
                        }
                        else s_VertexHelper.FillMesh(particleMesh);
                        // FillMesh rewrites the vertices without recomputing bounds, and an
                        // IMeshModifier may move them. Recompute only here: Mesh.CombineMeshes
                        // already produces correct bounds when no modifier ran.
                        meshModified = true;
                    }
                }
                finally { InternalListPool<Component>.Return(ref components); }

                if (meshModified) particleMesh.RecalculateBounds();
                var bounds = particleMesh.bounds;
                var center = bounds.center;
                center.z = 0;
                bounds.center = center;
                var extents = bounds.extents;
                extents.z = 0;
                bounds.extents = extents;
                particleMesh.bounds = bounds;
                _lastBounds = bounds;
            }

            EndSample();

            if (_parent == null || !isActiveAndEnabled) return;
            // Get grouped renderers.
            BeginSample("[UIParticleRenderer] Set Mesh");
            var renderers = InternalListPool<UIParticleRenderer>.Rent();
            try
            {
                if (_parent.useMeshSharing)
                {
                    UIParticleUpdater.GetGroupedRenderers(_parent.groupId, _index, renderers);
                }

                for (var i = 0; i < renderers.Count; i++)
                {
                    var r = renderers[i];
                    if (r == null || r == this || !r.isActiveAndEnabled || !r._parent.canRender) continue;

                    UIParticleProfiler.current.setMeshOps++;
                    r.canvasRenderer.SetMesh(particleMesh);
                    r._lastBounds = _lastBounds;
                    r.SetCanvasRendererMaterials(r.canvasRenderer);
                }

            }
            finally { InternalListPool<UIParticleRenderer>.Return(ref renderers); }

            if (_parent != null && _parent.canRender)
            {
                UIParticleProfiler.current.setMeshOps++;
                canvasRenderer.SetMesh(particleMesh);
                SetCanvasRendererMaterials(canvasRenderer);
            }
            else
            {
                ClearCanvas();
            }

            EndSample();

            // [FxUIParticle 刀6] 烘焙完成,刷新静态缓存快照。
            if (UIParticle.staticMeshCache) TakeStaticSnapshotMerged();
        }

        // [刀6] Evaluate the static-frame predicate once per frame. IsIdleForFastPath runs first
        // for every renderer and the UpdateMesh* pass can reuse the verdict for renderers that are
        // idle but still processed because a sibling forced the update. merged guards rebinding.
        private bool EvaluateStaticFrameCached(bool merged)
        {
            if (_staticFrameCacheFrame == Time.frameCount && _staticFrameCacheMerged == merged)
                return _staticFrameCacheValue;
            _staticFrameCacheFrame = Time.frameCount;
            _staticFrameCacheMerged = merged;
            _staticFrameCacheValue = merged ? IsStaticFrameMerged() : IsStaticFrameSingle();
            return _staticFrameCacheValue;
        }

        // [FxUIParticle 刀6] 静态网格缓存:比较自上次烘焙以来一切影响网格输出的状态。
        // 暂停信号必须是 _parent.isPaused(UIParticle 标志,Simulate 据此传 dt=0);
        // ps.isPaused 在 Set/SetMerged 绑定时就被 Pause() 恒置真(手动模拟驱动),不可用。
        private bool IsStaticFrameSingle()
        {
            if (!_parent.isPaused || _particleSystem == null) return false;

            var t = _particleSystem.transform;
            if ((t.position - _staticPsPos).sqrMagnitude > 1e-8f) return false;
            if (Mathf.Abs(Quaternion.Dot(t.rotation, _staticPsRot)) < 1f - 1e-6f) return false;
            if ((t.lossyScale - _staticPsScale).sqrMagnitude > 1e-8f) return false;

            return IsStaticFrameCommon();
        }

        private bool IsStaticFrameMerged()
        {
            if (!_parent.isPaused) return false;

            for (var k = 0; k < _mergedSystems.Length; k++)
            {
                if (_mergedSystems[k] == null) return false;
                if (_mergedSystems[k].gameObject.activeInHierarchy != _mergedStaticActive[k]) return false;
                var t = _mergedSystems[k].transform;
                if ((t.position - _mergedStaticPos[k]).sqrMagnitude > 1e-8f) return false;
                if (Mathf.Abs(Quaternion.Dot(t.rotation, _mergedStaticRot[k])) < 1f - 1e-6f) return false;
                if ((t.lossyScale - _mergedStaticScale[k]).sqrMagnitude > 1e-8f) return false;
            }

            return IsStaticFrameCommon();
        }

        private bool IsStaticFrameCommon()
        {
            if (canvas == null || _parent.m_AnimatableProperties.Length != 0) return false;
            if (_parent.scale3DForCalc != _staticParticleScale
                || _parent.positionMode != _staticPositionMode
                || !Mathf.Approximately(_parent.viewSizeForBaking, _staticViewSize)
                || (UIParticleProjectSettings.autoColorCorrection && canvas.ShouldGammaToLinearInMesh()) != _staticGamma)
                return false;
            var rt = canvasRenderer.transform;
            if ((rt.position - _staticRenPos).sqrMagnitude > 1e-8f) return false;
            if (Mathf.Abs(Quaternion.Dot(rt.rotation, _staticRenRot)) < 1f - 1e-6f) return false;
            if ((rt.lossyScale - _staticRenScale).sqrMagnitude > 1e-8f) return false;
            if ((_parent.parentScale - _staticParentScale).sqrMagnitude > 1e-8f) return false;

            var rootT = canvas.rootCanvas.transform;
            if ((rootT.localScale - _staticRootScale).sqrMagnitude > 1e-8f) return false;
            if (new Vector2Int(Screen.width, Screen.height) != _staticScreen) return false;
            if (!Mathf.Approximately(canvas.scaleFactor, _staticCanvasScale)) return false;

            return true;
        }

        private void TakeStaticSnapshotSingle()
        {
            var t = _particleSystem.transform;
            _staticPsPos = t.position;
            _staticPsRot = t.rotation;
            _staticPsScale = t.lossyScale;
            TakeStaticSnapshotCommon();
        }

        private void TakeStaticSnapshotMerged()
        {
            for (var k = 0; k < _mergedSystems.Length; k++)
            {
                var t = _mergedSystems[k].transform;
                _mergedStaticPos[k] = t.position;
                _mergedStaticRot[k] = t.rotation;
                _mergedStaticScale[k] = t.lossyScale;
                _mergedStaticActive[k] = _mergedSystems[k].gameObject.activeInHierarchy;
            }

            TakeStaticSnapshotCommon();
        }

        private void TakeStaticSnapshotCommon()
        {
            var rt = canvasRenderer.transform;
            _staticRenPos = rt.position;
            _staticRenRot = rt.rotation;
            _staticRenScale = rt.lossyScale;
            _staticParentScale = _parent.parentScale;
            _staticRootScale = canvas.rootCanvas.transform.localScale;
            _staticScreen = new Vector2Int(Screen.width, Screen.height);
            _staticCanvasScale = canvas.scaleFactor;
            _staticParticleScale = _parent.scale3DForCalc;
            _staticPositionMode = _parent.positionMode;
            _staticViewSize = _parent.viewSizeForBaking;
            _staticGamma = UIParticleProjectSettings.autoColorCorrection && canvas.ShouldGammaToLinearInMesh();
            _staticValid = true;
        }

        // [FxUIParticle 刀8] FastPath 空闲预判定:UIParticle.UpdateRenderers 据此跳过
        // GetBakeCamera 与本渲染器的 UpdateMesh 调用链。条件集是 UpdateMesh 内部短路
        // 条件的严格子集且每帧重新评估(无跨帧缓存),命中时与内部立即 return 等价;
        // 未命中时照常进入 UpdateMesh(清空检查/裁剪/烘焙门控全部照旧)。
        // 不以 _meshCleared 作为空闲条件:恢复显示后仍需进入 UpdateMesh 重烘。
        internal bool IsIdleForFastPath()
        {
            if (_parent == null || !isActiveAndEnabled) return true;
            if (_mergedSystems == null && (_particleSystem == null || !_particleSystem.gameObject.activeInHierarchy))
                return false;

            // [刀6] 静态网格缓存命中:暂停且相关 Transform/Canvas 全部未变。
            if (UIParticle.staticMeshCache && _staticValid
                && EvaluateStaticFrameCached(_mergedSystems != null))
            {
                return true;
            }

            // [刀5] FullCull:CanvasGroup 累积 alpha≈0(整页隐藏)。恢复可见后预判定
            // 立即失效,清空/重烘由 UpdateMesh 内部逻辑接管。
            if (UIParticle.earlyCull > 1 && canvasRenderer && FullCullHidden)
            {
                return true;
            }

            return false;
        }

        private void SetCanvasRendererMaterials(CanvasRenderer cr)
        {
            var firstMaterial = materialForRendering;
            var count = _mergedSystems == null || _mergedUniform ? 1 : _mergedSubmeshMaterials.Length;
            if (!_materialsDirty && _submittedMaterial == firstMaterial && cr.materialCount == count) return;
            if (count == 1)
            {
                cr.materialCount = 1;
                cr.SetMaterial(firstMaterial, 0);
            }
            else
            {
                cr.materialCount = count;
                for (var i = 0; i < count; i++)
                    cr.SetMaterial(i == 0 ? firstMaterial : _mergedSubmeshMaterials[i], i);
            }
            UIParticleProfiler.current.materialUpdates++;
            _submittedMaterial = firstMaterial;
            _materialsDirty = false;
        }

        private void ClearCanvas()
        {
            canvasRenderer.Clear();
            _materialsDirty = true;
            _submittedMaterial = null;
        }

        protected override void UpdateMaterial()
        {
            if (!IsActive()) return;
            SetCanvasRendererMaterials(canvasRenderer);
            canvasRenderer.SetTexture(mainTexture);
        }

        public override void SetMaterialDirty()
        {
            _materialsDirty = true;
            _materialForRendering = null;
            InvalidateMeshCache();
            base.SetMaterialDirty();
        }

        /// <summary>
        /// Call to update the geometry of the Graphic onto the CanvasRenderer.
        /// </summary>
        protected override void UpdateGeometry()
        {
        }

        public override void Cull(Rect clipRect, bool validRect)
        {
            // [FxUIParticle 刀5] 缓存 UGUI 的几何裁剪结论(不含 bounds-empty 情形,避免首帧被永久跳过烘焙)。
            // earlyCull 关闭时保持与原版完全一致的计算顺序与开销(短路链不变,不额外计算 rootCanvasRect)。
            if (UIParticle.earlyCull > 0)
            {
                var clipped = !validRect || !clipRect.Overlaps(rootCanvasRect, true);
                if (_uguiClipCulled && !clipped)
                {
                    InvalidateMeshCache();
                    _nextCullProbeTime = 0;
                }
                _uguiClipCulled = clipped;

                var cull5 = _lastBounds.extents == Vector3.zero || clipped;
                if (canvasRenderer.cull != cull5)
                {
                    canvasRenderer.cull = cull5;
                    UISystemProfilerApi.AddMarker("MaskableGraphic.cullingChanged", this);
                    onCullStateChanged.Invoke(cull5);
                    OnCullingChanged();
                }
                return;
            }

            var cull = _lastBounds.extents == Vector3.zero
                       || !validRect
                       || !clipRect.Overlaps(rootCanvasRect, true);
            if ((_uguiClipCulled || canvasRenderer.cull) && !cull)
                InvalidateMeshCache();
            _uguiClipCulled = false;
            if (canvasRenderer.cull == cull) return;

            canvasRenderer.cull = cull;
            UISystemProfilerApi.AddMarker("MaskableGraphic.cullingChanged", this);
            onCullStateChanged.Invoke(cull);
            OnCullingChanged();
        }

        private Vector3 GetWorldScale()
        {
            BeginSample("[UIParticleRenderer] GetWorldScale");
            var scale = _parent.scale3DForCalc.GetScaled(_parent.parentScale);

            if (_parent.autoScalingMode == UIParticle.AutoScalingMode.UIParticle
                && _particleSystem.main.scalingMode == ParticleSystemScalingMode.Local
                && _parent.canvas)
            {
                scale = scale.GetScaled(_parent.canvas.rootCanvas.transform.localScale);
            }

            EndSample();
            return scale;
        }

        private Matrix4x4 GetWorldMatrix(Vector3 psPos, Vector3 scale)
        {
            var space = _particleSystem.GetActualSimulationSpace();
            if (_isTrail && _particleSystem.trails.worldSpace)
            {
                space = ParticleSystemSimulationSpace.World;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                switch (space)
                {
                    case ParticleSystemSimulationSpace.World:
                        return Matrix4x4.Translate(psPos)
                               * Matrix4x4.Scale(scale)
                               * Matrix4x4.Translate(-psPos);
                }
            }
#endif

            switch (space)
            {
                case ParticleSystemSimulationSpace.Local:
                    return Matrix4x4.Translate(psPos)
                           * Matrix4x4.Scale(scale);
                case ParticleSystemSimulationSpace.World:
                    if (_isTrail)
                    {
                        return Matrix4x4.Translate(psPos)
                               * Matrix4x4.Scale(scale)
                               * Matrix4x4.Translate(-psPos);
                    }

                    if (_mainEmitter)
                    {
                        if (_mainEmitter.IsLocalSpace())
                        {
                            return Matrix4x4.Translate(psPos)
                                   * Matrix4x4.Scale(scale)
                                   * Matrix4x4.Translate(-psPos);
                        }
                        else
                        {
                            psPos = _particleSystem.transform.position - _mainEmitter.transform.position;
                            return Matrix4x4.Translate(psPos)
                                   * Matrix4x4.Scale(scale)
                                   * Matrix4x4.Translate(-psPos);
                        }
                    }

                    return Matrix4x4.Scale(scale);
                case ParticleSystemSimulationSpace.Custom:
                    return Matrix4x4.Translate(_particleSystem.main.customSimulationSpace.position.GetScaled(scale))
                           * Matrix4x4.Scale(scale);
                default:
                    throw new NotSupportedException();
            }
        }

        /// <summary>
        /// For world simulation, interpolate particle positions when the screen size is changed.
        /// </summary>
        /// <param name="psPos"></param>
        /// <param name="scale"></param>
        private void ResolveResolutionChange(Vector3 psPos, Vector3 scale)
        {
            var screenSize = new Vector2Int(Screen.width, Screen.height);
            var isWorldSpace = _particleSystem.IsWorldSpace();
            var canvasScale = canvas ? canvas.scaleFactor : 1f;
            var resolutionChanged = _prevScreenSize != screenSize
                                    || !Mathf.Approximately(_prevCanvasScale, canvasScale);
            if (resolutionChanged && isWorldSpace && _isPrevStored)
            {
                // Update particle array size and get particles.
                var size = _particleSystem.particleCount;
                var particles = ParticleSystemExtensions.GetParticleArray(size);
                _particleSystem.GetParticles(particles, size);

                // Resolution resolver:
                // (psPos / scale) / (prevPsPos / prevScale) -> psPos * scale.inv * prevPsPos.inv * prevScale
                var modifier = psPos.GetScaled(
                    scale.Inverse(),
                    _prevPsPos.Inverse(),
                    _prevScale);
                for (var i = 0; i < size; i++)
                {
                    var particle = particles[i];
                    particle.position = particle.position.GetScaled(modifier);
                    particles[i] = particle;
                }

                _particleSystem.SetParticles(particles, size);

                // Delay: Do not progress in the frame where the resolution has been changed.
                _delay = true;
                _prevScale = scale;
                _prevPsPos = psPos;
                _isPrevStored = true;
            }

            _prevCanvasScale = canvas ? canvas.scaleFactor : 1f;
            _prevScreenSize = screenSize;
        }

        private void Simulate(Vector3 scale, bool paused, float dtOverride = -1f)
        {
            var main = _particleSystem.main;
            var deltaTime = paused
                ? 0
                : 0f <= dtOverride
                    ? dtOverride
                    : main.useUnscaledTime
                        ? Time.unscaledDeltaTime
                        : Time.deltaTime;
            deltaTime *= _parent.timeScaleMultiplier;
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0) return;

            // Pre-warm:
            if (0 < deltaTime && _preWarm)
            {
                deltaTime += main.duration;
                _preWarm = false;
            }

            // get world position.
            var isLocalSpace = _particleSystem.IsLocalSpace();
            var psTransform = _particleSystem.transform;
            var originLocalPosition = psTransform.localPosition;
            var originLocalRotation = psTransform.localRotation;
            var originWorldPosition = psTransform.position;
            var originWorldRotation = psTransform.rotation;
            var emission = _particleSystem.emission;
            var rateOverDistance = emission.enabled
                                   && 0 < emission.rateOverDistance.constant
                                   && 0 < emission.rateOverDistanceMultiplier;
            try
            {
                if (rateOverDistance && !paused && _isPrevStored)
                {
                    // (For rate-over-distance emission,) Move to previous scaled position, simulate (delta = 0).
                    var prevScaledPos = isLocalSpace
                        ? _prevPsPos
                        : _prevPsPos.GetScaled(_prevScale.Inverse());
                    psTransform.SetPositionAndRotation(prevScaledPos, originWorldRotation);
                    _particleSystem.Simulate(0, false, false, false);
                }

                // Move to scaled position, simulate, revert to origin position.
                var scaledPos = isLocalSpace
                    ? originWorldPosition
                    : originWorldPosition.GetScaled(scale.Inverse());
                psTransform.SetPositionAndRotation(scaledPos, originWorldRotation);
                _particleSystem.Simulate(deltaTime, false, false, false);

            }
            finally
            {
                if (psTransform != null)
                {
                    psTransform.localPosition = originLocalPosition;
                    psTransform.localRotation = originLocalRotation;
                }
            }
        }

#if UNITY_EDITOR
        private void SimulateForEditor(Vector3 diffPos, Vector3 scale)
        {
            // Extra world simulation.
            var isWorldSpace = _particleSystem.IsWorldSpace();
            if (isWorldSpace && 0 < Vector3.SqrMagnitude(diffPos))
            {
                BeginSample("[UIParticle] Bake Mesh > Extra world simulation");
                diffPos.x *= 1f - 1f / Mathf.Max(0.001f, scale.x);
                diffPos.y *= 1f - 1f / Mathf.Max(0.001f, scale.y);
                diffPos.z *= 1f - 1f / Mathf.Max(0.001f, scale.z);

                var size = _particleSystem.particleCount;
                var particles = ParticleSystemExtensions.GetParticleArray(size);
                _particleSystem.GetParticles(particles, size);
                for (var i = 0; i < size; i++)
                {
                    var p = particles[i];
                    p.position += diffPos;
                    particles[i] = p;
                }

                _particleSystem.SetParticles(particles, size);
                EndSample();
            }
        }
#endif

        private void UpdateMaterialProperties()
        {
            if (_parent.m_AnimatableProperties.Length == 0) return;

            if (s_Mpb == null)
            {
                s_Mpb = new MaterialPropertyBlock();
            }

            _renderer.GetPropertyBlock(s_Mpb);
            if (s_Mpb.isEmpty) return;

            // #41: Copy the value from MaterialPropertyBlock to CanvasRenderer
            if (materialForRendering == null) return;

            for (var i = 0; i < _parent.m_AnimatableProperties.Length; i++)
            {
                var ap = _parent.m_AnimatableProperties[i];
                ap.UpdateMaterialProperties(materialForRendering, s_Mpb);
            }

            s_Mpb.Clear();
        }
    }
}
