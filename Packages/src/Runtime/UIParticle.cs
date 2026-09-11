using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Coffee.UIParticleInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using UnityEngine.UI;
using Random = UnityEngine.Random;

[assembly: InternalsVisibleTo("Coffee.UIParticle.Editor")]
[assembly: InternalsVisibleTo("Coffee.UIParticle.Editor.Tests")]
[assembly: InternalsVisibleTo("Coffee.UIParticle.PerformanceDemo")]
[assembly: InternalsVisibleTo("Coffee.UIParticle.Demo")]

namespace Coffee.UIExtensions
{
    /// <summary>
    /// Render maskable and sortable particle effect ,without Camera, RenderTexture or Canvas.
    /// </summary>
    [Icon("Packages/com.coffee.ui-particle/Editor/UIParticleIcon.png")]
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(CanvasRenderer))]
    public class UIParticle : MaskableGraphic, ISerializationCallbackReceiver
    {
        public enum AutoScalingMode
        {
            None,
            UIParticle,
            Transform
        }

        public enum MeshSharing
        {
            None,
            Auto,
            Primary,
            PrimarySimulator,
            Replica
        }

        public enum PositionMode
        {
            Relative,
            Absolute
        }

        [HideInInspector]
        [SerializeField]
        [Obsolete]
        internal bool m_IsTrail;

        [HideInInspector]
        [FormerlySerializedAs("m_IgnoreParent")]
        [SerializeField]
        [Obsolete]
        private bool m_IgnoreCanvasScaler;

        [HideInInspector]
        [SerializeField]
        [Obsolete]
        internal bool m_AbsoluteMode;

        [Tooltip("Scale the rendering particles. When the `3D` toggle is enabled, 3D scale (x, y, z) is supported.")]
        [SerializeField]
        private Vector3 m_Scale3D = new Vector3(10, 10, 10);

        [Tooltip("If you want to update material properties (e.g. _MainTex_ST, _Color) in AnimationClip, " +
                 "use this to mark as animatable.")]
        [SerializeField]
        internal AnimatableProperty[] m_AnimatableProperties = new AnimatableProperty[0];

        [Tooltip("Particles")]
        [SerializeField]
        private List<ParticleSystem> m_Particles = new List<ParticleSystem>();

        [Tooltip("Particle simulation results are shared within the same group. " +
                 "A large number of the same effects can be displayed with a small load.\n" +
                 "None: Disable mesh sharing.\n" +
                 "Auto: Automatically select Primary/Replica.\n" +
                 "Primary: Provides particle simulation results to the same group.\n" +
                 "Primary Simulator: Primary, but do not render the particle (simulation only).\n" +
                 "Replica: Render simulation results provided by the primary.")]
        [SerializeField]
        private MeshSharing m_MeshSharing = MeshSharing.None;

        [Tooltip("Mesh sharing group ID.\n" +
                 "If non-zero is specified, particle simulation results are shared within the group.")]
        [SerializeField]
        private int m_GroupId;

        [SerializeField]
        private int m_GroupMaxId;

        [Tooltip("Emission position mode.\n" +
                 "Relative: The particles will be emitted from the scaled position.\n" +
                 "Absolute: The particles will be emitted from the world position.")]
        [SerializeField]
        private PositionMode m_PositionMode = PositionMode.Relative;

        [SerializeField]
        [Obsolete]
        internal bool m_AutoScaling;

        [SerializeField]
        [Tooltip(
            "How to automatically adjust when the Canvas scale is changed by the screen size or reference resolution.\n" +
            "None: Do nothing.\n" +
            "Transform: Transform.lossyScale (=world scale) will be set to (1, 1, 1).\n" +
            "UIParticle: UIParticle.scale will be adjusted.")]
        private AutoScalingMode m_AutoScalingMode = AutoScalingMode.Transform;

        [SerializeField]
        [Tooltip("Use a custom view.\n" +
                 "Use this if the particles are not displayed correctly due to min/max particle size.")]
        private bool m_UseCustomView;

        [SerializeField]
        [Tooltip("Custom view size.\n" +
                 "Change the bake view size.")]
        private float m_CustomViewSize = 10;

        [SerializeField]
        [Tooltip("Time scale multiplier.")]
        private float m_TimeScaleMultiplier = 1;

        private readonly List<UIParticleRenderer> _renderers = new List<UIParticleRenderer>();
        private int _activeRendererCount;
        private bool _simulationOwner;
        internal float bakePhase;
        internal float estimatedBakeCost = 1;
        internal bool groupAllAlphaHidden;
        internal bool groupAllClipped;
        internal int activeRendererCount => _activeRendererCount;
        internal int mergedRendererCount => _activeRendererCount == 1 && _renderers[0] != null && _renderers[0].isMerged ? 1 : 0;
        private readonly List<ParticleSystem> _mergeSystems = new List<ParticleSystem>();
        private bool _mergeModeStamp;
        private bool _fallbackToUnmerged;
        private bool _rebuildRenderers;
        private bool _rendererBindingsDirty;
        private MeshSharing _sharingModeStamp;
        private int _sharingGroupStamp;
        private int _particleCountStamp;
        private readonly List<ParticleSystem> _particleBindings = new List<ParticleSystem>();
        private Camera _bakeCamera;
        private int _groupId;
        internal bool _isInSharingMap;
        internal int _sharingMapGroupId;
        private bool _isScaleStored;
        private Vector3 _storedScale;
        private DrivenRectTransformTracker _tracker;
        private bool _trackingScale;
        private static int s_RootCanvasFrame = -1;
        private static readonly Dictionary<Canvas, Canvas> s_RootCanvases = new Dictionary<Canvas, Canvas>();

        private static Canvas RootCanvas(Canvas value)
        {
            if (s_RootCanvasFrame != Time.frameCount)
            {
                s_RootCanvasFrame = Time.frameCount;
                s_RootCanvases.Clear();
            }
            if (!s_RootCanvases.TryGetValue(value, out var root))
                s_RootCanvases[value] = root = value.rootCanvas;
            return root;
        }

        /// <summary>
        /// Should this graphic be considered a target for ray-casting?
        /// </summary>
        public override bool raycastTarget
        {
            get => false;
            set { }
        }

        /// <summary>
        /// [FxUIParticle] Mesh Sharing 组成员查找方式:开 = 字典缓存,关 = 原版全局扫描。用于 A/B 基准对比。
        /// </summary>
        public static bool useGroupCache
        {
            get => UIParticleUpdater.s_UseGroupCache;
            set => UIParticleUpdater.s_UseGroupCache = value;
        }

        /// <summary>
        /// 全局粒子网格烘焙率(Hz)。0 = 每帧烘焙;30 = 高帧率上限约 30Hz,低帧率隔帧烘焙,各 Renderer 错峰。
        /// </summary>
        public static int bakeFPS { get; set; }

        /// <summary>
        /// 全局开关:开 = 同一 UIParticle 的全部非 Trail ParticleSystem 合并到一个 UIParticleRenderer 烘焙与提交;
        /// 关 = 原版行为(每个 ParticleSystem 一个 Renderer)。含 CanvasAnimator(AnimatableProperties)的效果自动回退原版路径。
        /// </summary>
        public static bool mergeRenderers { get; set; }

        /// <summary>
        /// [FxUIParticle] Early Culling 档位:0 = 关(原版,不可见也每帧烘焙);
        /// 1 = RenderCull(UGUI 已判裁剪/出屏时仅停止 Bake/Combine/SetMesh,模拟继续保证时间连续);
        /// 2 = RenderCull + FullCull(CanvasGroup 累积 alpha≈0 的整页隐藏时连模拟一起停,恢复可见后从停点继续)。
        /// MeshSharing 按整组消费者可见性裁剪,任一副本可见时保持共享输出。
        /// </summary>
        public static int earlyCull { get; set; }

        /// <summary>
        /// [FxUIParticle] 静态网格缓存:开 = 全部系统暂停且相关 Transform/Canvas 状态未变时,
        /// 整帧跳过 Simulate/Bake/Combine/SetMesh(网格零成本保持);关 = 原版行为(暂停也每帧烘焙)。
        /// </summary>
        public static bool staticMeshCache { get; set; }

        /// <summary>
        /// [FxUIParticle 刀9] Binding Fast Mode(默认关):开 = 把运行时绑定状态的“自动侦测”
        /// 改为“调用方显式通知”——跳过每帧对材质/贴图/trail/renderer 的复核,只在绑定时校验一次;
        /// 关 = 原版行为(每帧 bindingIsInvalid 全量校验,运行时改材质/贴图自动生效)。
        /// 开启后,下列运行时改动不再被自动侦测,必须调用 <see cref="MarkBindingDirty"/> 才会生效:
        /// ParticleSystemRenderer.sharedMaterial / trailMaterial、材质 mainTexture、trails.enabled、
        /// TextureSheetAnimation 的 sprite/来源贴图、绑定槽位内的 renderer 替换。
        /// ParticleSystem 列表本身的增删/替换仍会自动侦测重建。
        /// simulationSpace 切换不依赖此开关(烘焙时实时读取),无需 MarkBindingDirty。
        /// 仅当项目承诺运行期基本不修改 UI 粒子的材质/贴图/trail 时开启。
        /// </summary>
        public static bool fastBindingMode { get; set; }

        // 烘焙诊断:每帧由测试控制器读取并清零。
        public static int s_FrameBakedVerts;
        public static int s_FrameBakeOps;

        /// <summary>
        /// Particle simulation results are shared within the same group.
        /// A large number of the same effects can be displayed with a small load.
        /// None: disable mesh sharing.
        /// Auto: automatically select Primary/Replica.
        /// Primary: provides particle simulation results to the same group.
        /// Primary Simulator: Primary, but do not render the particle (simulation only).
        /// Replica: render simulation results provided by the primary.
        /// </summary>
        public MeshSharing meshSharing
        {
            get => m_MeshSharing;
            set => m_MeshSharing = value;
        }

        /// <summary>
        /// Mesh sharing group ID.
        /// If non-zero is specified, particle simulation results are shared within the group.
        /// </summary>
        public int groupId
        {
            get => _groupId;
            set
            {
                if (m_GroupId == value) return;
                m_GroupId = value;
                ResetGroupId();
            }
        }

        public int groupMaxId
        {
            get => m_GroupMaxId;
            set
            {
                if (m_GroupMaxId == value) return;
                m_GroupMaxId = value;
                ResetGroupId();
            }
        }

        /// <summary>
        /// Emission position mode.
        /// Relative: The particles will be emitted from the scaled position.
        /// Absolute: The particles will be emitted from the world position.
        /// </summary>
        public PositionMode positionMode
        {
            get => m_PositionMode;
            set => m_PositionMode = value;
        }

        /// <summary>
        /// Particle position mode.
        /// Relative: The particles will be emitted from the scaled position of the ParticleSystem.
        /// Absolute: The particles will be emitted from the world position of the ParticleSystem.
        /// </summary>
        [Obsolete("The absoluteMode is now obsolete. Please use the autoScalingMode instead.", false)]
        public bool absoluteMode
        {
            get => m_PositionMode == PositionMode.Absolute;
            set => positionMode = value ? PositionMode.Absolute : PositionMode.Relative;
        }

        /// <summary>
        /// Prevents the root-Canvas scale from affecting the hierarchy-scaled ParticleSystem.
        /// </summary>
        [Obsolete("The autoScaling is now obsolete. Please use the autoScalingMode instead.", false)]
        public bool autoScaling
        {
            get => m_AutoScalingMode != AutoScalingMode.None;
            set => autoScalingMode = value ? AutoScalingMode.Transform : AutoScalingMode.None;
        }

        /// <summary>
        /// How to automatically adjust when the Canvas scale is changed by the screen size or reference resolution.
        /// <para/>
        /// None: Do nothing.
        /// <para/>
        /// Transform: Transform.lossyScale (=world scale) will be set to (1, 1, 1).
        /// <para/>
        /// UIParticle: UIParticle.scale will be adjusted.
        /// </summary>
        public AutoScalingMode autoScalingMode
        {
            get => m_AutoScalingMode;
            set
            {
                if (m_AutoScalingMode == value) return;
                m_AutoScalingMode = value;

                if (autoScalingMode != AutoScalingMode.Transform && _isScaleStored)
                {
                    transform.localScale = _storedScale;
                    _isScaleStored = false;
                }
            }
        }

        /// <summary>
        /// Use a custom view.
        /// Use this if the particles are not displayed correctly due to min/max particle size.
        /// </summary>
        public bool useCustomView
        {
            get => m_UseCustomView;
            set => m_UseCustomView = value;
        }

        /// <summary>
        /// Custom view size.
        /// Change the bake view size.
        /// </summary>
        public float customViewSize
        {
            get => m_CustomViewSize;
            set => m_CustomViewSize = Mathf.Max(0.1f, value);
        }

        /// <summary>
        /// View size for Baking.
        /// </summary>
        public float viewSizeForBaking => useCustomView
            ? m_CustomViewSize
            : UIParticleProjectSettings.defaultViewSizeForBaking;

        /// <summary>
        /// Time scale multiplier.
        /// </summary>
        public float timeScaleMultiplier
        {
            get => m_TimeScaleMultiplier;
            set => m_TimeScaleMultiplier = value;
        }

        internal bool useMeshSharing => m_MeshSharing != MeshSharing.None;

        internal bool isPrimary =>
            m_MeshSharing == MeshSharing.Primary
            || m_MeshSharing == MeshSharing.PrimarySimulator;

        internal bool canSimulate =>
            m_MeshSharing == MeshSharing.None
            || m_MeshSharing == MeshSharing.Auto
            || m_MeshSharing == MeshSharing.Primary
            || m_MeshSharing == MeshSharing.PrimarySimulator;

        internal bool canRender =>
            m_MeshSharing == MeshSharing.None
            || m_MeshSharing == MeshSharing.Auto
            || m_MeshSharing == MeshSharing.Primary
            || m_MeshSharing == MeshSharing.Replica;

        /// <summary>
        /// Particle effect scale.
        /// </summary>
        public float scale
        {
            get => m_Scale3D.x;
            set => m_Scale3D = new Vector3(value, value, value);
        }

        /// <summary>
        /// Particle effect scale.
        /// </summary>
        public Vector3 scale3D
        {
            get => m_Scale3D;
            set => m_Scale3D = value;
        }

        /// <summary>
        /// Particle effect scale.
        /// </summary>
        public Vector3 scale3DForCalc => autoScalingMode == AutoScalingMode.Transform
            ? m_Scale3D
            : m_Scale3D.GetScaled(canvasScale, transform.localScale);

        public List<ParticleSystem> particles => m_Particles;

        /// <summary>
        /// Paused.
        /// </summary>
        public bool isPaused { get; private set; }

        public Vector3 parentScale { get; private set; }

        public Vector3 canvasScale { get; private set; }

        protected override void OnEnable()
        {
            _isScaleStored = false;
            ResetGroupId();
            UIParticleUpdater.Register(this);
            RegisterDirtyMaterialCallback(UpdateRendererMaterial);

            if (0 < particles.Count)
            {
                RefreshParticles(particles);
            }
            else
            {
                RefreshParticles();
            }

            base.OnEnable();
        }

        /// <summary>
        /// This function is called when the behaviour becomes disabled.
        /// </summary>
        protected override void OnDisable()
        {
            _tracker.Clear();
            _trackingScale = false;
            if (autoScalingMode == AutoScalingMode.Transform && _isScaleStored)
            {
                transform.localScale = _storedScale;
            }

            _isScaleStored = false;
            UIParticleUpdater.Unregister(this);
            _renderers.RemoveAll(r => r == null);
            _renderers.ForEach(r => r.Reset());
            _activeRendererCount = 0;
            _simulationOwner = false;
            UnregisterDirtyMaterialCallback(UpdateRendererMaterial);

            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            // Removing just the UIParticle component must also release its generated
            // children. Scene/GameObject destruction already removes them naturally.
            for (var i = 0; i < _renderers.Count; i++)
                if (_renderers[i] != null) Misc.Destroy(_renderers[i].gameObject);
            _renderers.Clear();
            if (_bakeCamera != null) Misc.Destroy(_bakeCamera.gameObject);
            _bakeCamera = null;
            base.OnDestroy();
        }

        /// <summary>
        /// Callback for when properties have been changed by animation.
        /// </summary>
        protected override void OnDidApplyAnimationProperties()
        {
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
#pragma warning disable CS0612 // Type or member is obsolete
            if (m_IgnoreCanvasScaler || m_AutoScaling)
            {
                m_IgnoreCanvasScaler = false;
                m_AutoScaling = false;
                m_AutoScalingMode = AutoScalingMode.Transform;
            }

            if (m_AbsoluteMode)
            {
                m_AbsoluteMode = false;
                m_PositionMode = PositionMode.Absolute;
            }
#pragma warning restore CS0612 // Type or member is obsolete
        }

        /// <summary>
        /// Play the ParticleSystems.
        /// </summary>
        public void Play()
        {
            particles.Exec(p => p.Simulate(0, false, true));
            isPaused = false;
            InvalidateRendererCaches();
        }

        /// <summary>
        /// Pause the ParticleSystems.
        /// </summary>
        public void Pause()
        {
            particles.Exec(p => p.Pause());
            isPaused = true;
            InvalidateRendererCaches();
        }

        /// <summary>
        /// Unpause the ParticleSystems.
        /// </summary>
        public void Resume()
        {
            isPaused = false;
            InvalidateRendererCaches();
        }

        /// <summary>
        /// Stop the ParticleSystems.
        /// </summary>
        public void Stop()
        {
            particles.Exec(p => p.Stop());
            isPaused = true;
            InvalidateRendererCaches();
        }

        /// <summary>
        /// Start emission of the ParticleSystems.
        /// </summary>
        public void StartEmission()
        {
            particles.Exec(p =>
            {
                var emission = p.emission;
                emission.enabled = true;
            });
        }

        /// <summary>
        /// Stop emission of the ParticleSystems.
        /// </summary>
        public void StopEmission()
        {
            particles.Exec(p =>
            {
                var emission = p.emission;
                emission.enabled = false;
            });
        }

        /// <summary>
        /// Clear the particles of the ParticleSystems.
        /// </summary>
        public void Clear()
        {
            particles.Exec(p => p.Clear());
            isPaused = true;
            InvalidateRendererCaches();
        }

        /// <summary>
        /// Get all base materials to render.
        /// </summary>
        public void GetMaterials(List<Material> result)
        {
            if (result == null) return;

            for (var i = 0; i < _renderers.Count; i++)
            {
                var r = _renderers[i];
                if (r == null || r.material == null) continue;
                result.Add(r.material);
            }
        }

        /// <summary>
        /// Refresh UIParticle using the ParticleSystem instance.
        /// </summary>
        public void SetParticleSystemInstance(GameObject instance)
        {
            SetParticleSystemInstance(instance, true);
        }

        /// <summary>
        /// Refresh UIParticle using the ParticleSystem instance.
        /// </summary>
        public void SetParticleSystemInstance(GameObject instance, bool destroyOldParticles)
        {
            if (instance == null) return;

            var childCount = transform.childCount;
            for (var i = childCount - 1; 0 <= i; i--)
            {
                var go = transform.GetChild(i).gameObject;
                if (go == instance || instance.transform.IsChildOf(go.transform)) continue;
                if (go.TryGetComponent<Camera>(out var cam) && cam == _bakeCamera) continue;
                if (go.TryGetComponent<UIParticleRenderer>(out var _)) continue;

                go.SetActive(false);
                if (destroyOldParticles)
                {
                    Misc.Destroy(go);
                }
            }

            var tr = instance.transform;
            tr.SetParent(transform, false);
            tr.localPosition = Vector3.zero;

            RefreshParticles(instance);
        }

        /// <summary>
        /// Refresh UIParticle using the prefab.
        /// The prefab is automatically instantiated.
        /// </summary>
        public void SetParticleSystemPrefab(GameObject prefab)
        {
            if (prefab == null) return;

            SetParticleSystemInstance(Instantiate(prefab.gameObject), true);
        }

        /// <summary>
        /// Refresh UIParticle.
        /// Collect ParticleSystems under the GameObject and refresh the UIParticle.
        /// </summary>
        public void RefreshParticles()
        {
            RefreshParticles(gameObject);
        }

        /// <summary>
        /// Refresh UIParticle.
        /// Collect ParticleSystems under the GameObject and refresh the UIParticle.
        /// </summary>
        private void RefreshParticles(GameObject root)
        {
            if (root == null) return;
            root.GetComponentsInChildren(true, particles);
            for (var i = particles.Count - 1; 0 <= i; i--)
            {
                var ps = particles[i];
                if (!ps
#if UNITY_EDITOR
                    || (ps.hideFlags & HideFlags.DontSave) != 0 // Dummy ParticleSystems for preview.
                    || ps.gameObject.CompareTag("EditorOnly") // Ignore "EditorOnly" tagged ParticleSystems.
#endif
                    || ps.GetComponentInParent<UIParticle>(true) != this) // Ignore ParticleSystems that are not under this UIParticle.
                {
                    particles.RemoveAt(i);
                }
            }

            for (var i = 0; i < particles.Count; i++)
            {
                var ps = particles[i];
                var tsa = ps.textureSheetAnimation;
                if (tsa.mode == ParticleSystemAnimationMode.Sprites && tsa.uvChannelMask == 0)
                {
                    tsa.uvChannelMask = UVChannelFlags.UV0;
                }
            }

            RefreshParticles(particles);
        }

        /// <summary>
        /// Refresh UIParticle using a list of ParticleSystems.
        /// </summary>
        public void RefreshParticles(List<ParticleSystem> particleSystems)
        {
            if (particleSystems == null) throw new System.ArgumentNullException(nameof(particleSystems));
            if (!ReferenceEquals(particleSystems, particles))
            {
                particles.Clear();
                particles.AddRange(particleSystems);
                particleSystems = particles;
            }
            _particleCountStamp = particleSystems.Count;
            _particleBindings.Clear();
            _particleBindings.AddRange(particleSystems);
            estimatedBakeCost = 0;
            for (var i = 0; i < particleSystems.Count; i++)
                if (particleSystems[i] != null)
                    estimatedBakeCost += Mathf.Max(1, particleSystems[i].main.maxParticles)
                                         * (particleSystems[i].trails.enabled ? 2 : 1);
            _activeRendererCount = 0;
            // Collect children UIParticleRenderer components.
            // #246: Nullptr exceptions when using nested UIParticle components in hierarchy
            _renderers.Clear();
            var childCount = transform.childCount;
            for (var i = 0; i < childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child.TryGetComponent(out UIParticleRenderer uiParticleRenderer))
                {
                    _renderers.Add(uiParticleRenderer);
                }
            }

            // Reset the UIParticleRenderer components.
            for (var i = 0; i < _renderers.Count; i++)
            {
                _renderers[i].Reset(i);
            }

            _mergeModeStamp = mergeRenderers;
            _rendererBindingsDirty = true;
            _rebuildRenderers = false;

            // Set the ParticleSystem to the UIParticleRenderer. If the trail is enabled, set it additionally.
            var j = 0;
            if (mergeRenderers && !_fallbackToUnmerged && m_AnimatableProperties.Length == 0
                && UIParticleRenderer.CanMerge(particleSystems))
            {
                // [FxUIParticle 刀7] Merged mode: one renderer binds ALL systems; trail-enabled
                // systems bake an extra trail sub-mesh into the combined mesh instead of
                // getting a separate trail renderer (original merged mode).
                _mergeSystems.Clear();
                for (var i = 0; i < particleSystems.Count; i++)
                {
                    var ps = particleSystems[i];
                    if (ps == null || !ps.TryGetComponent<ParticleSystemRenderer>(out var psRenderer)) continue;

                    _mergeSystems.Add(ps);
                }

                if (0 < _mergeSystems.Count)
                {
                    GetRenderer(j++).SetMerged(this, _mergeSystems);
                }
            }
            else
            {
                for (var i = 0; i < particleSystems.Count; i++)
                {
                    var ps = particleSystems[i];
                    if (ps == null || !ps.TryGetComponent<ParticleSystemRenderer>(out var psRenderer)) continue;

                    var mainEmitter = ps.GetMainEmitter(particleSystems);
                    GetRenderer(j++).Set(this, ps, false, mainEmitter);

                    // If the trail is enabled, set it additionally.
                    if (ps.trails.enabled)
                    {
                        GetRenderer(j++).Set(this, ps, true, mainEmitter);
                    }
                }
            }
        }

        internal bool SetSimulationOwner(bool value)
        {
            if (_simulationOwner == value) return false;
            _simulationOwner = value;
            for (var i = 0; i < _activeRendererCount; i++)
                if (_renderers[i] != null)
                {
                    if (!value) _renderers[i].ReleaseBakeResources();
                    else _renderers[i].InvalidateMeshCache();
                }
            return true;
        }

        internal void GetOutputVisibility(out bool hasOutput, out bool alphaHidden, out bool clipped)
        {
            hasOutput = canRender && _activeRendererCount > 0;
            alphaHidden = clipped = true;
            if (!hasOutput) return;
            for (var i = 0; i < _activeRendererCount; i++)
            {
                var r = _renderers[i];
                if (r == null || !r.isActiveAndEnabled) continue;
                var alpha = r.alphaHidden;
                alphaHidden &= alpha;
                clipped &= alpha || r.clipHidden;
            }
        }

        internal bool PrepareForUpdate()
        {
            if (_mergeModeStamp != mergeRenderers)
            {
                _fallbackToUnmerged = false;
                _rebuildRenderers = true;
            }

            if (_particleCountStamp != particles.Count) _rebuildRenderers = true;
            else
                for (var i = 0; i < particles.Count; i++)
                    if (_particleBindings[i] != particles[i]) { _rebuildRenderers = true; break; }
            // [刀9] Binding Fast Mode: skip the per-frame material/texture/trail/renderer
            // revalidation. The particle list check above still runs and a destroyed child
            // renderer is still detected; runtime binding edits must call MarkBindingDirty().
            if (fastBindingMode)
            {
                for (var i = 0; i < _activeRendererCount; i++)
                    if (_renderers[i] == null) { _rebuildRenderers = true; break; }
            }
            else
            {
                for (var i = 0; i < _activeRendererCount; i++)
                    if (_renderers[i] == null || _renderers[i].bindingIsInvalid) _rebuildRenderers = true;
            }
            if (_rebuildRenderers) RefreshParticles(particles);

            UpdateTransformScale();
            var changed = _rendererBindingsDirty || _sharingModeStamp != meshSharing || _sharingGroupStamp != groupId;
            _rendererBindingsDirty = false;
            _sharingModeStamp = meshSharing;
            _sharingGroupStamp = groupId;
            return changed;
        }

        internal void RequestUnmergedFallback()
        {
            if (_fallbackToUnmerged) return;
            _fallbackToUnmerged = true;
            _rebuildRenderers = true;
        }

        internal bool hasUnmergedFallback => _fallbackToUnmerged && _mergeModeStamp == mergeRenderers;

        /// <summary>
        /// Call on the main thread after externally changing particle data (for example
        /// SetParticles or custom data), especially while paused with static caching.
        /// Forces a fresh mesh on the next eligible update without restarting simulation.
        /// Shared groups rebake their simulation owner's data; editing a Replica does
        /// not make that Replica the source. Hidden output still obeys culling.
        /// </summary>
        public void MarkParticleDirty()
        {
            UIParticleUpdater.MarkParticleDirty(this);
        }

        /// <summary>
        /// 在运行时修改了绑定状态后,且 <see cref="fastBindingMode"/> 为开时必须调用。
        /// fast mode 把“自动侦测”换成“显式通知”,下列改动不会再自动生效:
        /// ParticleSystemRenderer.sharedMaterial / trailMaterial、材质 mainTexture、trails.enabled、
        /// TextureSheetAnimation 的 sprite/来源贴图、绑定槽位内的 renderer 替换。
        /// (ParticleSystem 列表的增删/替换、renderer 被销毁仍会自动侦测。)
        /// 调用后会在下一次 update 重绑并拾取改动,等价于补上 fast mode 跳过的那次校验。
        /// fast mode 关闭时调用是安全的(仅多一次重绑)。
        /// </summary>
        public void MarkBindingDirty()
        {
            _rebuildRenderers = true;
        }

        internal void InvalidateRendererCaches()
        {
            for (var i = 0; i < _activeRendererCount; i++)
                if (_renderers[i] != null) _renderers[i].InvalidateMeshCache();
        }

        internal void ClearRendererMeshes()
        {
            for (var i = 0; i < _activeRendererCount; i++)
                if (_renderers[i] != null && _renderers[i].isActiveAndEnabled) _renderers[i].ClearMesh();
        }

        private void UpdateTransformScale()
        {
            canvasScale = RootCanvas(canvas).transform.localScale.Inverse();
            parentScale = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            if (autoScalingMode != AutoScalingMode.Transform)
            {
                if (_trackingScale) { _tracker.Clear(); _trackingScale = false; }
                if (_isScaleStored)
                {
                    transform.localScale = _storedScale;
                }

                _isScaleStored = false;
                return;
            }

            var currentScale = transform.localScale;
            if (!_isScaleStored)
            {
                _storedScale = currentScale.IsVisible() ? currentScale : Vector3.one;
                _isScaleStored = true;
            }

            if (!_trackingScale)
            {
                _tracker.Add(this, rectTransform, DrivenTransformProperties.Scale);
                _trackingScale = true;
            }
            var newScale = parentScale.Inverse();
            if (currentScale != newScale)
            {
                transform.localScale = newScale;
            }
        }

        internal void UpdateRenderers()
        {
            if (!isActiveAndEnabled) return;

            // [FxUIParticle 刀8] FastPath:全部渲染器空闲(刀6 静态命中/刀5 FullCull 命中)时,
            // 跳过 GetBakeCamera 与逐渲染器 UpdateMesh 调用链。UpdateTransformScale 仍由
            // UIParticleUpdater.Refresh 正常驱动(IsStaticFrameCommon 依赖新鲜的 parentScale)。
            var allIdle = true;
            for (var i = 0; i < _activeRendererCount; i++)
            {
                var r = _renderers[i];
                if (r == null || r.IsIdleForFastPath()) continue;

                allIdle = false;
                break;
            }

            if (allIdle) return;

            var bakeCamera = GetBakeCamera();
            var beforeOps = UIParticleProfiler.current.bakeOps;
            var beforeVertices = UIParticleProfiler.current.bakedVertices;
            for (var i = 0; i < _activeRendererCount; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;

                r.UpdateMesh(bakeCamera);
            }
            if (UIParticleProfiler.current.bakeOps > beforeOps)
                estimatedBakeCost = estimatedBakeCost * 0.75f
                    + Mathf.Max(1, (float)(UIParticleProfiler.current.bakedVertices - beforeVertices)) * 0.25f;
        }

        internal void ResetGroupId()
        {
            _groupId = m_GroupId == m_GroupMaxId
                ? m_GroupId
                : Random.Range(m_GroupId, m_GroupMaxId + 1);
        }

        protected override void UpdateMaterial()
        {
        }

        /// <summary>
        /// Call to update the geometry of the Graphic onto the CanvasRenderer.
        /// </summary>
        protected override void UpdateGeometry()
        {
        }

        private void UpdateRendererMaterial()
        {
            for (var i = 0; i < _activeRendererCount; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                if (r.maskable != maskable)
                {
                    r.maskable = maskable;
                    // maskable alone does not re-parent the clippable; force UGUI to
                    // add/remove the RectMask2D clip parent so clipping actually changes.
                    r.RecalculateClipping();
                }
                r.SetMaterialDirty();
            }
        }

        internal UIParticleRenderer GetRenderer(int index)
        {
            if (_renderers.Count <= index)
            {
                _renderers.Add(UIParticleRenderer.AddRenderer(this, index));
            }

            if (_renderers[index] == null)
            {
                _renderers[index] = UIParticleRenderer.AddRenderer(this, index);
            }

            _activeRendererCount = Mathf.Max(_activeRendererCount, index + 1);
            return _renderers[index];
        }

        internal UIParticleRenderer GetRendererIfExists(int index)
        {
            return 0 <= index && index < _activeRendererCount ? _renderers[index] : null;
        }

        private Camera GetBakeCamera()
        {
            if (canvas == null) return Camera.main;
            if (!useCustomView && canvas.renderMode != RenderMode.ScreenSpaceOverlay && canvas.rootCanvas.worldCamera)
            {
                return canvas.rootCanvas.worldCamera;
            }

            if (_bakeCamera != null)
            {
                _bakeCamera.orthographicSize = viewSizeForBaking;
                return _bakeCamera;
            }

            // Find existing baking camera.
            var childCount = transform.childCount;
            for (var i = 0; i < childCount; i++)
            {
                if (transform.GetChild(i).TryGetComponent<Camera>(out var cam)
                    && cam.name == "[generated] UIParticle BakingCamera")
                {
                    _bakeCamera = cam;
                    break;
                }
            }

            // Create baking camera.
            if (_bakeCamera == null)
            {
                var go = new GameObject("[generated] UIParticle BakingCamera");
                go.SetActive(false);
                go.transform.SetParent(transform, false);
                _bakeCamera = go.AddComponent<Camera>();
            }

            // Setup baking camera.
            _bakeCamera.enabled = false;
            _bakeCamera.orthographicSize = viewSizeForBaking;
            _bakeCamera.transform.SetPositionAndRotation(new Vector3(0, 0, -1000), Quaternion.identity);
            _bakeCamera.orthographic = true;
            _bakeCamera.farClipPlane = 2000f;
            _bakeCamera.clearFlags = CameraClearFlags.Nothing;
            _bakeCamera.cullingMask = 0; // Nothing
            _bakeCamera.allowHDR = false;
            _bakeCamera.allowMSAA = false;
            _bakeCamera.renderingPath = RenderingPath.Forward;
            _bakeCamera.useOcclusionCulling = false;

            _bakeCamera.gameObject.SetActive(false);
            _bakeCamera.gameObject.hideFlags = UIParticleProjectSettings.globalHideFlags;

            return _bakeCamera;
        }
    }
}
