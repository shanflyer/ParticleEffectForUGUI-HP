using System.Collections.Generic;
using Coffee.UIExtensions;
using UnityEngine;
using UnityEngine.UI;

namespace FxUIParticleTest
{
    // Explicit test shutdown, not an inference from a temporarily empty system.
    internal sealed class ParticleUpdateSetting
    {
        readonly UIParticle _particle;
        readonly List<ParticleSystem> _allSystems = new List<ParticleSystem>();
        readonly List<ParticleSystem> _selectedSystems = new List<ParticleSystem>();
        bool _suspended, _wasEnabled;

        public ParticleUpdateSetting(UIParticle particle)
        {
            _particle = particle;
            if (particle != null) _allSystems.AddRange(particle.particles);
        }

        public int Apply(HashSet<ParticleSystem> enabledSystems, bool paused)
        {
            if (_particle == null) return 0;
            _selectedSystems.Clear();
            for (var i = 0; i < _allSystems.Count; i++)
                if (_allSystems[i] != null && enabledSystems.Contains(_allSystems[i]))
                    _selectedSystems.Add(_allSystems[i]);

            if (_selectedSystems.Count == 0)
            {
                if (_suspended) return 0;
                _wasEnabled = _particle.enabled;
                _suspended = true;
                // OnDisable unregisters sharing/update membership and resets all renderers.
                _particle.enabled = false;
                return 0;
            }
            if (_suspended)
            {
                _suspended = false;
                _particle.enabled = _wasEnabled;
            }
            if (!_particle.enabled) return 0;
            _particle.RefreshParticles(_selectedSystems);
            _particle.Play();
            if (paused) _particle.Pause();
            return _selectedSystems.Count;
        }
    }

    // Captured once so selected systems always run with their original settings.
    internal sealed class ParticleQuantitySetting
    {
        readonly ParticleSystem _system;
        readonly int _maxParticles;
        readonly bool _emissionEnabled;
        readonly float _timeRate, _distanceRate;
        readonly ParticleSystem.Burst[] _bursts;

        public ParticleQuantitySetting(ParticleSystem system)
        {
            _system = system;
            _maxParticles = system.main.maxParticles;
            var emission = system.emission;
            _emissionEnabled = emission.enabled;
            _timeRate = emission.rateOverTimeMultiplier;
            _distanceRate = emission.rateOverDistanceMultiplier;
            _bursts = new ParticleSystem.Burst[emission.burstCount];
            emission.GetBursts(_bursts);
        }

        public void Apply(bool enabled)
        {
            if (_system == null) return;
            var main = _system.main;
            main.maxParticles = _maxParticles;
            var emission = _system.emission;
            emission.enabled = _emissionEnabled && enabled;
            emission.rateOverTimeMultiplier = _timeRate;
            emission.rateOverDistanceMultiplier = _distanceRate;
            emission.SetBursts(_bursts);
        }
    }

    /// <summary>
    /// 基线测试控制器。
    /// 按键:1~7 切换 Case,Space 暂停/恢复粒子,R 手动开始/停止采集,M 切换 MeshSharing,B 切换烘焙率,
    /// N 切换 Renderer 合并,C 循环 Early Culling(0关/1RenderCull/2+FullCull),V 切换静态网格缓存,X 切换面板 Alpha0 演示,Q 退出。
    /// </summary>
    public class FxBaselineController : MonoBehaviour
    {
        public GameObject[] effectPrefabs;
        public RectTransform[] gridSlots;
        public RectTransform[] specialSlots;
        public GameObject[] specialPanels;
        public Text caseLabel;
        [Tooltip("自动开始/切换配置后重采。默认关闭，使用 Start Rec / Stop / Save 控制采集。")]
        public bool autoRecord;
        [Tooltip("关 = 原版全局扫描,开 = 字典缓存(P0 优化)")]
        public bool groupCache;
        [Tooltip("关 = 原版 CPU 顶点色 LinearToGamma,开 = Canvas.vertexColorAlwaysGammaSpace(跳过每帧顶点色循环)。已验证 -42% CPU")]
        public bool vertexColorGammaSpace;
        [Tooltip("关 = 每帧烘焙网格,开 = 高帧率上限约 30Hz、低帧率隔帧烘焙(UIParticle.bakeFPS)")]
        public bool bakeRate30;
        [Tooltip("关 = 原版每 PS 一个 Renderer,开 = 同一 UIParticle 全部非 Trail PS 合并为一个 Renderer")]
        public bool mergeRenderers;
        [Tooltip("0=关(原版,不可见也烘焙) 1=RenderCull(被裁剪时停Bake模拟不停) 2=RenderCull+FullCull(alpha≈0全停)")]
        [Range(0, 2)] public int earlyCull;
        [Tooltip("关 = 原版(暂停也每帧烘焙),开 = 暂停且无变化时整帧跳过 Simulate/Bake/Combine/SetMesh")]
        public bool staticMeshCache;
        [Tooltip("启动场景:默认 G 多特效压测(全部槽位 × 每槽副本数)。0~6 对应 A~G。")]
        [Range(0, 6)] public int initialCase = 6;
        [Tooltip("Unity 已跟踪的保留内存达到此值时停止测试并释放特效。不是系统全部提交内存。")]
        [Min(512)] public int memoryLimitMB = 4096;

        static readonly string[] CaseNames =
        {
            "A: 1 Effect (5 PS)",
            "B: Same Effect x10 (MeshSharing)",
            "C: 10 Different Effects",
            "D: 30 Different Effects",
            "E: 30 Effects + Mask/RectMask/CanvasGroup",
            "F: 60 Effects (Stress)",
            "G: Stress (Slots x Copies)",
        };

        [Range(1, 10)] public int stressCopiesPerSlot = 4;
        [Tooltip("启用的粒子系统数量比例，每档 5%；100% 启用全部系统，5% 启用 floor(总数×5%)。")]
        [SerializeField, Range(0, 20)] int particleQuantityLevel = 20;
        const int ParticleQuantityStepPercent = 5;
        const int ResetParticleQuantityLevel = 20;
        static readonly float[] ParticleQuantityFactors = new float[21];
        readonly List<ParticleQuantitySetting> _quantitySettings = new List<ParticleQuantitySetting>();
        readonly List<ParticleUpdateSetting> _particleUpdateSettings = new List<ParticleUpdateSetting>();
        readonly HashSet<ParticleSystem> _enabledParticleSystems = new HashSet<ParticleSystem>();
        int _enabledParticleSystemCount;
        int _appliedQuantityLevel = -1;
        Button _particleLessBtn, _particleMoreBtn;
        Text _particleQuantityText;

        readonly List<GameObject> _spawned = new List<GameObject>();
        readonly List<ParticleSystem> _particles = new List<ParticleSystem>();
        readonly List<UIParticle> _uiParticles = new List<UIParticle>();
        readonly Dictionary<(int, int), int> _sharingGroupIds = new Dictionary<(int, int), int>();
        int _lastCaseChangeFrame = -1;
        float _nextMemoryCheck;
        bool _memoryLimitReached;

        FxProfilerRecorder _recorder;
        int _caseIndex;
        bool _paused;
        bool _meshSharing;
        bool _lastGroupCache;
        bool _lastGamma;
        bool _lastBake30;
        bool _lastMerge;
        int _lastCull;
        bool _lastStatic;
        bool _alpha0;
        Button _cacheBtn, _sharingBtn, _gammaBtn, _bakeBtn, _mergeBtn, _cullBtn, _staticBtn, _alpha0Btn;
        Text _cacheBtnText, _sharingBtnText, _gammaBtnText, _bakeBtnText, _mergeBtnText, _cullBtnText, _staticBtnText, _alpha0BtnText;
        int _lastLabelFrame;
        int _controlState = -1;
        Button _startRecordBtn, _stopRecordBtn;
        Text _startRecordText;
        int _recordControlState = -1;

        void OnEnable()
        {
            _memoryLimitReached = false;
            _lastCaseChangeFrame = -1;
            _nextMemoryCheck = 0;
        }

        void Start()
        {
            particleQuantityLevel = Mathf.Clamp(particleQuantityLevel, 0, ParticleQuantityFactors.Length - 1);
            for (var i = 0; i < ParticleQuantityFactors.Length; i++)
                ParticleQuantityFactors[i] = i * ParticleQuantityStepPercent / 100f;
            _appliedQuantityLevel = particleQuantityLevel;
            Application.targetFrameRate = 60; // 帧率上限不是内存或单帧工作量上限。
            _recorder = GetComponent<FxProfilerRecorder>();
            if (_recorder == null) _recorder = gameObject.AddComponent<FxProfilerRecorder>();
            _lastGroupCache = groupCache;
            _lastGamma = vertexColorGammaSpace;
            _lastBake30 = bakeRate30;
            _lastMerge = mergeRenderers;
            _lastCull = earlyCull;
            _lastStatic = staticMeshCache;
            // 状态行较长,允许溢出显示第二行(否则纵向 Truncate 会吞掉 Cull/Stat/A0/BkV/BkN)
            if (caseLabel != null) caseLabel.verticalOverflow = VerticalWrapMode.Overflow;
            // Apply switches before instantiation, including when domain reload is disabled.
            UIParticle.useGroupCache = groupCache;
            UIParticle.bakeFPS = bakeRate30 ? 30 : 0;
            UIParticle.mergeRenderers = mergeRenderers;
            UIParticle.earlyCull = earlyCull;
            UIParticle.staticMeshCache = staticMeshCache;
            ApplyCase(Mathf.Clamp(initialCase, 0, CaseNames.Length - 1));
            CreateControlBar();
        }

        void Update()
        {
            UpdateRecordingControls();
            if (Time.unscaledTime >= _nextMemoryCheck)
            {
                _nextMemoryCheck = Time.unscaledTime + 0.5f;
                if (!CheckMemoryBudget()) return;
            }
            if (_memoryLimitReached) return;
            if (particleQuantityLevel != _appliedQuantityLevel) SetParticleQuantityLevel(particleQuantityLevel);
            UIParticle.useGroupCache = groupCache;
            if (groupCache != _lastGroupCache)
            {
                // 开关切动即自动重开采集,两种状态各得一份独立 CSV
                _lastGroupCache = groupCache;
                RestartRecordingForStateChange();
            }

            if (caseLabel != null && caseLabel.canvas != null)
            {
                caseLabel.canvas.rootCanvas.vertexColorAlwaysGammaSpace = vertexColorGammaSpace;
            }
            if (vertexColorGammaSpace != _lastGamma)
            {
                _lastGamma = vertexColorGammaSpace;
                RestartRecordingForStateChange();
            }

            UIParticle.bakeFPS = bakeRate30 ? 30 : 0;
            if (bakeRate30 != _lastBake30)
            {
                _lastBake30 = bakeRate30;
                RestartRecordingForStateChange();
            }

            UIParticle.mergeRenderers = mergeRenderers;
            if (mergeRenderers != _lastMerge)
            {
                _lastMerge = mergeRenderers;
                RestartRecordingForStateChange();
            }

            UIParticle.earlyCull = earlyCull;
            if (earlyCull != _lastCull)
            {
                _lastCull = earlyCull;
                RestartRecordingForStateChange();
            }

            UIParticle.staticMeshCache = staticMeshCache;
            if (staticMeshCache != _lastStatic)
            {
                _lastStatic = staticMeshCache;
                RestartRecordingForStateChange();
            }

            for (var i = 0; i < CaseNames.Length; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.F1 + i))
                {
                    ApplyCase(i);
                    break;
                }
            }

            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
            {
                stressCopiesPerSlot = Mathf.Clamp(stressCopiesPerSlot + 1, 1, 10);
                if (_caseIndex == 6) ApplyCase(6);
            }

            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
            {
                stressCopiesPerSlot = Mathf.Clamp(stressCopiesPerSlot - 1, 1, 10);
                if (_caseIndex == 6) ApplyCase(6);
            }

            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (_recorder != null && _recorder.IsRecording) StopRecording();
                else StartRecording();
            }
            if (Input.GetKeyDown(KeyCode.M)) SetMeshSharing(!_meshSharing);
            if (Input.GetKeyDown(KeyCode.B)) bakeRate30 = !bakeRate30;
            if (Input.GetKeyDown(KeyCode.N)) mergeRenderers = !mergeRenderers;
            if (Input.GetKeyDown(KeyCode.C)) earlyCull = (earlyCull + 1) % 3;
            if (Input.GetKeyDown(KeyCode.V)) staticMeshCache = !staticMeshCache;
            if (Input.GetKeyDown(KeyCode.X)) ToggleAlphaZero();
            if (Input.GetKeyDown(KeyCode.Q)) Application.Quit();

            if (Time.frameCount % 30 == 0) UpdateLabel();
        }

        public void ApplyCase(int index)
        {
            if (!isActiveAndEnabled || index < 0 || CaseNames.Length <= index) return;
            if (_lastCaseChangeFrame == Time.frameCount || !ValidateCase(index) || !CheckMemoryBudget()) return;
            _lastCaseChangeFrame = Time.frameCount;
            ClearSpawned();
            _caseIndex = index;
            _meshSharing = index == 1 || index == 6;
            _paused = false;

            var panelsActive = index == 4 || index == 5 || index == 6;
            foreach (var panel in specialPanels) if (panel != null) panel.SetActive(panelsActive);

            switch (index)
            {
                case 0:
                    Spawn(effectPrefabs[0], gridSlots[0]);
                    break;
                case 1:
                    for (var i = 0; i < 10; i++) Spawn(effectPrefabs[0], gridSlots[i]);
                    break;
                case 2:
                    for (var i = 0; i < 10; i++) Spawn(effectPrefabs[i], gridSlots[i]);
                    break;
                case 3:
                    for (var i = 0; i < 30; i++) Spawn(effectPrefabs[i], gridSlots[i]);
                    break;
                case 4:
                    for (var i = 0; i < 30; i++) Spawn(effectPrefabs[i], gridSlots[i]);
                    for (var i = 0; i < specialSlots.Length; i++)
                        Spawn(effectPrefabs[i % effectPrefabs.Length], specialSlots[i]);
                    break;
                case 5:
                    for (var i = 0; i < 30; i++) Spawn(effectPrefabs[i], gridSlots[i]);
                    for (var i = 0; i < specialSlots.Length; i++)
                        Spawn(effectPrefabs[i % effectPrefabs.Length], specialSlots[i]);
                    for (var i = 0; i < 21; i++)
                        Spawn(effectPrefabs[i % effectPrefabs.Length], gridSlots[i]);
                    break;
                case 6:
                {
                    var allSlots = new List<RectTransform>(gridSlots.Length + specialSlots.Length);
                    allSlots.AddRange(gridSlots);
                    allSlots.AddRange(specialSlots);
                    for (var s = 0; s < allSlots.Count; s++)
                    {
                        for (var c = 0; c < stressCopiesPerSlot; c++)
                        {
                            var prefabIndex = (s * 3 + c * 7) % effectPrefabs.Length;
                            Spawn(effectPrefabs[prefabIndex], allSlots[s], prefabIndex + 1);
                        }
                    }
                    break;
                }
            }

            ApplyParticleSystemSelection();
            if (!CheckMemoryBudget()) return;
            foreach (var uip in _uiParticles)
            {
                if (uip == null) continue;
                uip.meshSharing = _meshSharing ? UIParticle.MeshSharing.Auto : UIParticle.MeshSharing.None;
            }

            UpdateLabel();

            RestartRecordingForStateChange();
        }

        bool ValidateCase(int index)
        {
            var neededPrefabs = index <= 1 ? 1 : index == 2 ? 10 : index == 6 ? 1 : 30;
            var neededSlots = index == 0 ? 1 : index <= 2 ? 10 : index == 6 ? 0 : 30;
            if (effectPrefabs == null || effectPrefabs.Length < neededPrefabs
                || gridSlots == null || gridSlots.Length < neededSlots
                || specialSlots == null || specialPanels == null
                || (index == 6 && gridSlots.Length + specialSlots.Length == 0))
            {
                Debug.LogError("[FxBaselineController] 特效或槽位配置不完整，保留当前场景。", this);
                return false;
            }
            for (var i = 0; i < (index == 6 ? effectPrefabs.Length : neededPrefabs); i++)
                if (effectPrefabs[i] == null) return false;
            stressCopiesPerSlot = Mathf.Clamp(stressCopiesPerSlot, 1, 10);
            return true;
        }

        bool CheckMemoryBudget()
        {
            var reserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
            var limit = (long)Mathf.Max(512, memoryLimitMB) * 1024 * 1024;
            if (reserved < limit) return !_memoryLimitReached;
            if (_memoryLimitReached) return false;
            _memoryLimitReached = true;
            ClearSpawned();
            if (_recorder != null) _recorder.StopAndWrite();
            var message = $"测试已停止: Unity 保留内存 {reserved / (1024 * 1024)} MB 达到 {memoryLimitMB} MB 限额。";
            if (caseLabel != null) caseLabel.text = message;
            Debug.LogWarning("[FxBaselineController] " + message, this);
            return false;
        }

        void OnDisable()
        {
            if (_recorder != null) _recorder.StopAndWrite();
            ClearSpawned();
        }

        public void TogglePause()
        {
            // 走 UIParticle API:Pause/Resume 置 _parent.isPaused(Simulate 据此传 dt=0),
            // 静态网格缓存(刀6)依赖该标志才能识别"暂停且无变化"。
            _paused = !_paused;
            foreach (var uip in _uiParticles)
            {
                if (uip == null) continue;
                if (particleQuantityLevel == 0) continue;
                if (_paused) uip.Pause();
                else uip.Resume();
            }
            UpdateLabel();
            RestartRecordingForStateChange();
        }

        public void SetMeshSharing(bool sharing)
        {
            _meshSharing = sharing;
            foreach (var uip in _uiParticles)
            {
                if (uip == null) continue;
                uip.meshSharing = sharing ? UIParticle.MeshSharing.Auto : UIParticle.MeshSharing.None;
            }
            UpdateLabel();
            RestartRecordingForStateChange();
        }

        // Alpha0 演示:把特殊面板整体压到 alpha 0(页面隐藏的常见做法)。Cull FC 档下这些面板内的特效停止模拟与烘焙;
        // Cull OFF(原版)照常每帧烘焙,可作 A/B 对照。
        public void ToggleAlphaZero()
        {
            _alpha0 = !_alpha0;
            foreach (var panel in specialPanels)
            {
                if (panel == null || !panel.activeSelf) continue;
                if (!panel.TryGetComponent<CanvasGroup>(out var cg)) cg = panel.AddComponent<CanvasGroup>();
                cg.alpha = _alpha0 ? 0f : 1f;
            }
            UpdateLabel();
            RestartRecordingForStateChange();
        }

        void Spawn(GameObject prefab, RectTransform slot, int groupId = 0)
        {
            if (prefab == null || slot == null || !CheckMemoryBudget()) return;
            var go = Instantiate(prefab, slot);
            _spawned.Add(go); // Track immediately, including a malformed prefab.
            var rt = go.transform as RectTransform;
            if (rt == null)
            {
                go.SetActive(false);
                Destroy(go);
                return;
            }
            rt.localScale = Vector3.one;
            rt.anchoredPosition = Vector2.zero;
            rt.localRotation = Quaternion.identity;

            // Both endpoints must match: groupId alone selects a RANDOM group when
            // groupMaxId differs. Different prefabs must never share renderer indices.
            var fixedGroupId = groupId != 0 ? groupId : System.Array.IndexOf(effectPrefabs, prefab) + 1;
            var uiParticles = go.GetComponentsInChildren<UIParticle>(true);
            for (var i = 0; i < uiParticles.Length; i++)
            {
                var uip = uiParticles[i];
                // Nested UIParticles in one prefab may have different renderer layouts.
                var key = (fixedGroupId, i);
                if (!_sharingGroupIds.TryGetValue(key, out var actualGroupId))
                {
                    actualGroupId = _sharingGroupIds.Count + 1;
                    _sharingGroupIds.Add(key, actualGroupId);
                }
                uip.groupMaxId = actualGroupId;
                uip.groupId = actualGroupId;
                uip.meshSharing = _meshSharing ? UIParticle.MeshSharing.Auto : UIParticle.MeshSharing.None;
            }

            _uiParticles.AddRange(uiParticles);
            var systems = go.GetComponentsInChildren<ParticleSystem>(true);
            _particles.AddRange(systems);
            foreach (var ps in systems)
            {
                var setting = new ParticleQuantitySetting(ps);
                _quantitySettings.Add(setting);
            }
            foreach (var uip in uiParticles)
            {
                var updateSetting = new ParticleUpdateSetting(uip);
                _particleUpdateSettings.Add(updateSetting);
            }
        }

        void ClearSpawned()
        {
            foreach (var go in _spawned)
            {
                if (go == null) continue;
                // Destroy is deferred. Unregister renderers before spawning the next case.
                go.SetActive(false);
                Destroy(go);
            }
            _spawned.Clear();
            _particles.Clear();
            _quantitySettings.Clear();
            _particleUpdateSettings.Clear();
            _enabledParticleSystems.Clear();
            _enabledParticleSystemCount = 0;
            _uiParticles.Clear();
        }

        void UpdateLabel()
        {
            if (caseLabel == null || _memoryLimitReached) return;
            var crCount = UIParticleProfiler.completed.activeRenderers;
            long alive = 0;
            if (particleQuantityLevel != 0)
                foreach (var ps in _particles)
                {
                    if (ps == null) continue;
                    alive += ps.particleCount;
                }
            if (_particleQuantityText != null)
                _particleQuantityText.text = particleQuantityLevel == 0
                    ? $"Systems OFF (0/{_particles.Count})\nUpdates stopped"
                    : $"Systems {_enabledParticleSystemCount}/{_particles.Count} ({particleQuantityLevel * ParticleQuantityStepPercent}%)\nAlive: {alive}";
            if (_particleLessBtn != null) _particleLessBtn.interactable = particleQuantityLevel > 0;
            if (_particleMoreBtn != null) _particleMoreBtn.interactable = particleQuantityLevel < ParticleQuantityFactors.Length - 1;
            // 烘焙诊断:统计自上次刷新以来的烘焙顶点总数与烘焙次数(按帧平均)
            var bakedVerts = UIParticle.s_FrameBakedVerts;
            var bakeOps = UIParticle.s_FrameBakeOps;
            UIParticle.s_FrameBakedVerts = 0;
            UIParticle.s_FrameBakeOps = 0;
            var frames = Mathf.Max(1, Time.frameCount - _lastLabelFrame);
            _lastLabelFrame = Time.frameCount;
            caseLabel.text = $"Case {CaseName(_caseIndex)}  |  UIP:{_uiParticles.Count}  PS:{_particles.Count}  PCR:{crCount}\n" +
                             $"MeshSharing:{(_meshSharing ? "ON" : "OFF")}  Cache:{(groupCache ? "ON" : "OFF")}  " +
                             $"Gamma:{(vertexColorGammaSpace ? "SKIP" : "CPU")}  Bake:{(bakeRate30 ? "30" : "60")}  " +
                             $"Merge:{(mergeRenderers ? "ON" : "OFF")}  {(_paused ? "PAUSED" : "")}  " +
                             $"Cull:{(earlyCull == 0 ? "OFF" : earlyCull == 1 ? "RC" : "FC")}  " +
                             $"Stat:{(staticMeshCache ? "ON" : "OFF")}  A0:{(_alpha0 ? "ON" : "OFF")}  " +
                             $"BkV:{bakedVerts / frames} BkN:{bakeOps / frames}";

            var controls = (_meshSharing ? 1 : 0) | (groupCache ? 2 : 0) | (vertexColorGammaSpace ? 4 : 0)
                           | (bakeRate30 ? 8 : 0) | (mergeRenderers ? 16 : 0) | (earlyCull << 5)
                           | (staticMeshCache ? 128 : 0) | (_alpha0 ? 256 : 0);
            if (_sharingBtn == null || controls == _controlState) return;
            _controlState = controls;

            if (_cacheBtnText != null)
            {
                _cacheBtnText.text = groupCache ? "Cache ON" : "Cache OFF";
                _cacheBtn.image.color = groupCache
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_sharingBtnText != null)
            {
                _sharingBtnText.text = _meshSharing ? "Sharing ON" : "Sharing OFF";
                _sharingBtn.image.color = _meshSharing
                    ? new Color(0.10f, 0.30f, 0.55f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_gammaBtnText != null)
            {
                _gammaBtnText.text = vertexColorGammaSpace ? "Gamma SKIP" : "Gamma CPU";
                _gammaBtn.image.color = vertexColorGammaSpace
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_bakeBtnText != null)
            {
                _bakeBtnText.text = bakeRate30 ? "Bake 30Hz" : "Bake 60Hz";
                _bakeBtn.image.color = bakeRate30
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_mergeBtnText != null)
            {
                _mergeBtnText.text = mergeRenderers ? "Merge ON" : "Merge OFF";
                _mergeBtn.image.color = mergeRenderers
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_cullBtnText != null)
            {
                _cullBtnText.text = earlyCull == 0 ? "Cull OFF" : earlyCull == 1 ? "Cull RC" : "Cull FC";
                _cullBtn.image.color = earlyCull != 0
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_staticBtnText != null)
            {
                _staticBtnText.text = staticMeshCache ? "Static ON" : "Static OFF";
                _staticBtn.image.color = staticMeshCache
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
            if (_alpha0BtnText != null)
            {
                _alpha0BtnText.text = _alpha0 ? "A0 ON" : "A0 OFF";
                _alpha0Btn.image.color = _alpha0
                    ? new Color(0.10f, 0.45f, 0.20f, 0.92f)
                    : new Color(0.22f, 0.22f, 0.26f, 0.92f);
            }
        }

        // 顶部控制条:第一行 Case 翻页/重采,第二行三个状态开关(真机无键盘无 Inspector)
        void CreateControlBar()
        {
            if (caseLabel == null) return;
            var parent = caseLabel.transform.parent;
            var font = caseLabel.font;

            CreateButton(parent, "Btn_PrevCase", -325f, 100f, -140f, font, "Prev",
                () => ApplyCase((_caseIndex + CaseNames.Length - 1) % CaseNames.Length));
            CreateButton(parent, "Btn_NextCase", -205f, 100f, -140f, font, "Next",
                () => ApplyCase((_caseIndex + 1) % CaseNames.Length));
            _startRecordBtn = CreateButton(parent, "Btn_StartRecord", -25f, 230f, -140f, font, "Start Rec", StartRecording);
            _startRecordText = _startRecordBtn.GetComponentInChildren<Text>();
            _stopRecordBtn = CreateButton(parent, "Btn_StopRecord", 235f, 230f, -140f, font, "Stop / Save", StopRecording);
            UpdateRecordingControls();

            _sharingBtn = CreateButton(parent, "Btn_Sharing", -340f, 200f, -230f, font, "Sharing OFF", () => SetMeshSharing(!_meshSharing));
            _sharingBtnText = _sharingBtn.GetComponentInChildren<Text>();
            _cacheBtn = CreateButton(parent, "Btn_Cache", -125f, 200f, -230f, font, "Cache OFF", () => groupCache = !groupCache);
            _cacheBtnText = _cacheBtn.GetComponentInChildren<Text>();
            _gammaBtn = CreateButton(parent, "Btn_Gamma", 100f, 220f, -230f, font, "Gamma CPU", () => vertexColorGammaSpace = !vertexColorGammaSpace);
            _gammaBtnText = _gammaBtn.GetComponentInChildren<Text>();
            _bakeBtn = CreateButton(parent, "Btn_Bake", 325f, 200f, -230f, font, "Bake 60Hz", () => bakeRate30 = !bakeRate30);
            _bakeBtnText = _bakeBtn.GetComponentInChildren<Text>();

            _mergeBtn = CreateButton(parent, "Btn_Merge", -100f, 200f, -320f, font, "Merge OFF", () => mergeRenderers = !mergeRenderers);
            _mergeBtnText = _mergeBtn.GetComponentInChildren<Text>();
            _cullBtn = CreateButton(parent, "Btn_Cull", -325f, 200f, -320f, font, "Cull OFF",
                () => earlyCull = (earlyCull + 1) % 3);
            _cullBtnText = _cullBtn.GetComponentInChildren<Text>();
            _staticBtn = CreateButton(parent, "Btn_Static", 120f, 200f, -320f, font, "Static OFF",
                () => staticMeshCache = !staticMeshCache);
            _staticBtnText = _staticBtn.GetComponentInChildren<Text>();
            _alpha0Btn = CreateButton(parent, "Btn_Alpha0", 325f, 200f, -320f, font, "A0 OFF", () => ToggleAlphaZero());
            _alpha0BtnText = _alpha0Btn.GetComponentInChildren<Text>();

            _particleLessBtn = CreateButton(parent, "Btn_ParticlesLess", -325f, 200f, -410f, font, "Systems -5%",
                () => SetParticleQuantityLevel(particleQuantityLevel - 1));
            var resetQuantity = CreateButton(parent, "Btn_ParticlesReset", 0f, 420f, -410f, font, "Systems 100% (Reset)",
                () => SetParticleQuantityLevel(ResetParticleQuantityLevel));
            _particleQuantityText = resetQuantity.GetComponentInChildren<Text>();
            _particleMoreBtn = CreateButton(parent, "Btn_ParticlesMore", 325f, 200f, -410f, font, "Systems +5%",
                () => SetParticleQuantityLevel(particleQuantityLevel + 1));

            UpdateLabel();
        }

        public void SetParticleQuantityLevel(int level)
        {
            if (!isActiveAndEnabled || _memoryLimitReached || !CheckMemoryBudget()) return;
            level = Mathf.Clamp(level, 0, ParticleQuantityFactors.Length - 1);
            if (level == _appliedQuantityLevel) { particleQuantityLevel = level; return; }
            particleQuantityLevel = _appliedQuantityLevel = level;
            ApplyParticleSystemSelection();
            UpdateLabel();
            RestartRecordingForStateChange();
        }

        void ApplyParticleSystemSelection()
        {
            _enabledParticleSystems.Clear();
            var total = 0;
            for (var i = 0; i < _particles.Count; i++)
                if (_particles[i] != null) total++;
            var target = total * particleQuantityLevel / 20; // floor: 158 × 5% = 7.
            var ordinal = 0;
            for (var i = 0; i < _particles.Count; i++)
            {
                var ps = _particles[i];
                if (ps == null) continue;
                // Select exactly target systems, spread across the complete scene list.
                var enabled = (ordinal + 1) * target / total != ordinal * target / total;
                ordinal++;
                if (enabled) _enabledParticleSystems.Add(ps);
                else ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                _quantitySettings[i].Apply(enabled);
            }
            _enabledParticleSystemCount = _enabledParticleSystems.Count;
            foreach (var setting in _particleUpdateSettings)
                setting.Apply(_enabledParticleSystems, _paused);
        }

        public void StartRecording()
        {
            if (!isActiveAndEnabled || _memoryLimitReached || _recorder == null || _recorder.IsRecording) return;
            _recorder.BeginRecording(RecordingName());
            UpdateRecordingControls();
        }

        public void StopRecording()
        {
            // A manual stop must persist across later case/optimization switches.
            autoRecord = false;
            if (_recorder != null) _recorder.StopAndWrite();
            UpdateRecordingControls();
        }

        void RestartRecordingForStateChange()
        {
            // Split an active manual recording too, so different configurations
            // never share one CSV. An idle manual recorder remains idle.
            if (_recorder != null && (autoRecord || _recorder.IsRecording))
                _recorder.BeginRecording(RecordingName());
        }

        void UpdateRecordingControls()
        {
            if (_startRecordBtn == null || _stopRecordBtn == null) return;
            var state = _recorder == null || !isActiveAndEnabled || _memoryLimitReached ? 3
                : !_recorder.IsRecording ? 0 : _recorder.IsSampling ? 2 : 1;
            if (state == _recordControlState) return;
            _recordControlState = state;
            _startRecordBtn.interactable = state == 0;
            _stopRecordBtn.interactable = _recorder != null && _recorder.IsRecording;
            _startRecordText.text = state == 1 ? "Warmup..." : state == 2 ? "Recording" : "Start Rec";
        }

        Button CreateButton(Transform parent, string name, float x, float w, float y, Font font, string label, System.Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); // 顶栏对齐,与 caseLabel 同一参考系
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(w, 80f);
            rt.anchoredPosition = new Vector2(x, y);

            var img = go.GetComponent<Image>();
            img.color = new Color(0.22f, 0.22f, 0.26f, 0.92f);

            var txtGo = new GameObject("Text", typeof(RectTransform));
            txtGo.transform.SetParent(go.transform, false);
            var trt = (RectTransform)txtGo.transform;
            trt.sizeDelta = new Vector2(w, 80f);
            var txt = txtGo.AddComponent<Text>();
            txt.text = label;
            txt.font = font;
            txt.fontSize = 30;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;

            var btn = go.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick());
            return btn;
        }

        string CaseName(int index)
        {
            return index == 6
                ? $"G: {allSlotsCount()} slots x {stressCopiesPerSlot} = {allSlotsCount() * stressCopiesPerSlot} Effects (Stress, MeshSharing)"
                : CaseNames[index];
        }

        string RecordingName()
        {
            return CaseName(_caseIndex)
                   + (groupCache ? " CacheON" : " CacheOFF")
                   + (vertexColorGammaSpace ? " GammaSKIP" : " GammaCPU")
                   + (bakeRate30 ? " Bake30" : " Bake60")
                   + (mergeRenderers ? " MergeON" : " MergeOFF")
                   + (earlyCull == 0 ? " CullOFF" : earlyCull == 1 ? " CullR" : " CullF")
                   + (staticMeshCache ? " StatON" : " StatOFF")
                   + (_alpha0 ? " A0ON" : " A0OFF")
                   + (_meshSharing ? " SharingON" : " SharingOFF")
                   + (_paused ? " PausedON" : " PausedOFF")
                   + " ParticlePercent=" + (ParticleQuantityFactors[particleQuantityLevel] * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                   + " ParticleSystems=" + _enabledParticleSystemCount + "/" + _particles.Count
                   + " ParticleUpdates=" + (particleQuantityLevel == 0 ? "OFF" : "ON");
        }

        int allSlotsCount()
        {
            return gridSlots.Length + specialSlots.Length;
        }
    }
}


