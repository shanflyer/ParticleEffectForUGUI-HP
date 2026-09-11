using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Frame Debugger 数据导出工具（独立通用版）
/// 将当前帧的渲染数据导出为 Markdown 文件，包括渲染顺序列表、Pass统计和详细事件信息。
/// 通过反射访问 Unity 内部 FrameDebugger API。
/// </summary>
public class FrameDebuggerExport : EditorWindow
{
    private const string DefaultAiExportDirectoryName = "FrameDebuggerAIExports";
    private const string AutoAnalyzeAfterExportPrefKey = "FrameDebuggerExport.AutoAnalyzeAfterExport";
    private const string RenderDocTargetMatchPrefKey = "FrameDebuggerExport.RenderDocTargetMatch";
    private const string RenderDocTargetUrlPrefKey = "FrameDebuggerExport.RenderDocTargetUrl";
    private const string RenderDocAiHomePrefKey = "FrameDebuggerExport.RenderDocAiHome";
    private static readonly Guid RuntimeSnapshotRequestGuid = new Guid("4b9f2a33-9f07-4ec7-b558-c2ef0f7162a1");
    private static readonly Guid RuntimeSnapshotResponseGuid = new Guid("3f85df43-8c6a-4a1a-8c77-6c8bb93a2b7d");
    private const bool ExportFullRendererSnapshotByDefault = true;
    private const double RuntimeSnapshotTimeoutSeconds = 20.0;
    private static ExportBuildCache s_exportCache;

    #region Reflection Cache

    private static bool s_reflectionInited;
    private static string s_reflectionError;

    // FrameDebuggerUtility
    private static Type s_utilType;
    private static PropertyInfo s_countProp;
    private static PropertyInfo s_limitProp;
    private static FieldInfo s_limitField;
    private static MethodInfo s_getFrameEvents;
    private static MethodInfo s_getFrameEventData;
    private static MethodInfo s_getFrameEventObject;
    private static MethodInfo s_getFrameEventGameObject;
    private static MethodInfo s_getFrameEventRenderer;
    private static MethodInfo s_getBatchBreakCauseStrings;
    private static MethodInfo s_isLocalEnabled;
    private static MethodInfo s_isRemoteEnabled;
    private static PropertyInfo s_enabledProp; // FrameDebugger.enabled fallback
    private static string s_enabledSource = ""; // debug: where we found the enabled check
    private static string[] s_batchBreakCauseStrings = new string[0];

    // FrameDebuggerEvent
    private static Type s_eventType;
    private static FieldInfo[] s_eventFields;
    private static PropertyInfo[] s_eventProperties;

    // FrameDebuggerEventData
    private static Type s_eventDataType;
    private static FieldInfo[] s_eventDataFields;
    private static PropertyInfo[] s_eventDataProperties;

    #endregion

    #region Data Structures

    private class FrameEventInfo
    {
        public int index;
        public string eventName = "";
        public string typeName = "";
        public string shaderName = "";
        public string passName = "";
        public string passLightMode = "";
        public int passIndex = -1;
        public string renderTargetName = "";
        public int renderTargetWidth;
        public int renderTargetHeight;
        public int renderTargetFormat;
        public int vertexCount;
        public int indexCount;
        public int instanceCount;
        public int drawCallCount;
        public string shaderKeywords = "";
        public string batchBreakCause = "";
        public string gameObjectName = "";
        public string gameObjectPath = "";
        public string objectType = "";
        public int objectInstanceId;
        public string rendererType = "";
        public int rendererSortingLayerId;
        public int rendererSortingOrder;
        public int rendererPriority;
        public string rendererMaterials = "";
        public string frameDebuggerGameObjectName = "";
        public string frameDebuggerGameObjectPath = "";
        public int frameDebuggerGameObjectInstanceId;
        public string frameDebuggerRendererType = "";
        public string frameDebuggerRendererPath = "";
        public int frameDebuggerRendererInstanceId;
        public string meshName = "";
        public string meshAssetPath = "";
        public string meshGuid = "";
        public string materialName = "";
        public string materialAssetPath = "";
        public string materialGuid = "";
        public int materialRenderQueue = -1;
        public bool materialInstancingEnabled;
        public string materialKeywords = "";
        public string materialTextures = "";
        public string materialProperties = "";
        public string resolvedShaderName = "";
        public string resolvedShaderAssetPath = "";
        public string resolvedShaderGuid = "";
        public int resolvedShaderPassCount = -1;
        public int resolvedShaderRenderQueue = -1;
        public string resolvedShaderPassSummary = "";
        public int meshSubset = -1;
        public List<string> detailMeshNames = new List<string>();
        public List<string> detailMeshAssetPaths = new List<string>();
        public List<string> detailMeshInstanceIds = new List<string>();
        public Dictionary<string, string> extraFields = new Dictionary<string, string>();
        public Dictionary<string, string> rawEventFields = new Dictionary<string, string>();
        public Dictionary<string, string> rawDataFields = new Dictionary<string, string>();
        public bool frameEventDataAttempted;
        public bool frameEventDataSuccess;
        public int frameEventDataLimitBefore = -1;
        public int frameEventDataLimitSetTo = -1;
        public int frameEventDataLimitAfter = -1;
        public int frameEventDataLimitAfterRestore = -1;
        public int requestedEventIndex = -1;
        public int filledFrameEventIndex = -1;
        public string frameEventDataFailureReason = "";
        public string frameEventDataMethod = "";
    }

    private class PassStatEntry
    {
        public string passName = "";
        public int drawCallCount;
        public long totalVertices;
        public long totalIndices;
        public HashSet<string> shaderNames = new HashSet<string>();
    }

    private class MaterialStatEntry
    {
        public string materialName = "";
        public string materialAssetPath = "";
        public string shaderName = "";
        public string shaderAssetPath = "";
        public bool instancingEnabled;
        public string keywords = "";
        public string textures = "";
        public string properties = "";
        public int renderQueue = -1;
        public int drawCallCount;
        public HashSet<string> passNames = new HashSet<string>();
        public HashSet<string> objectNames = new HashSet<string>();
    }

    private class ShaderStatEntry
    {
        public string shaderName = "";
        public string shaderAssetPath = "";
        public string passSummary = "";
        public int drawCallCount;
        public HashSet<string> passNames = new HashSet<string>();
        public HashSet<string> materialNames = new HashSet<string>();
        public HashSet<string> objectNames = new HashSet<string>();
    }

    private class UiGraphicRecord
    {
        public string source = "editor_scene";
        public string path = "";
        public string type = "";
        public string canvasPath = "";
        public string canvasCamera = "";
        public string canvasRenderMode = "";
        public int canvasSortingLayerId;
        public int canvasSortingOrder;
        public int depth;
        public bool active;
        public bool enabled;
        public bool canvasEnabled;
        public bool raycastTarget;
        public string materialName = "";
        public string materialPath = "";
        public string shaderName = "";
        public string shaderPath = "";
        public string textureName = "";
        public string texturePath = "";
        public string textureMeta = "";
        public string materialTextures = "";
        public string spriteName = "";
        public string rect = "";
        public string screenRect = "";
        public string rootCanvasPath = "";
        public float canvasScaleFactor;
        public float canvasReferencePixelsPerUnit;
        public bool isTextComponent;
        public int textLength;
        public string fontName = "";
        public string fontMaterialName = "";
        public string materialFingerprint = "";
        public string resourceFingerprint = "";
        public string sortingGroup = "";
        public string maskState = "";
        public string canvasRenderer = "";
        public string prefabAssetPath = "";
        public string batchKey = "";
    }

    private class UiBatchCandidate
    {
        public int id;
        public int mappedEventIndex;
        public int sequenceIndex;
        public string camera = "";
        public string canvasPath = "";
        public string batchKey = "";
        public string materialName = "";
        public string materialPath = "";
        public string shaderName = "";
        public string textureName = "";
        public string texturePath = "";
        public string maskState = "";
        public string confidence = "";
        public string reason = "";
        public List<UiGraphicRecord> graphics = new List<UiGraphicRecord>();
    }

    private class ParticleCandidate
    {
        public string source = "editor_scene";
        public int id;
        public string path = "";
        public string materialName = "";
        public string materialPath = "";
        public string shaderName = "";
        public int aliveParticles;
        public int maxParticles;
        public bool rendererEnabled;
        public string rendererMode = "";
        public int sortingLayerId;
        public int sortingOrder;
        public string textureName = "";
        public string textureMeta = "";
        public string materialTextures = "";
        public string bounds = "";
        public string materialFingerprint = "";
        public string resourceFingerprint = "";
        public string sortingGroup = "";
        public bool mappedToFrameEvent;
        public string confidence = "";
        public string reason = "";
        public int matchScore;
        public string[] matchedBy = new string[0];
    }

    private class RenderFeatureSnapshot
    {
        public string rendererName = "";
        public string rendererType = "";
        public string rendererAssetPath = "";
        public string featureName = "";
        public string featureType = "";
        public bool active;
        public string featureAssetPath = "";
        public string materialShaderRefs = "";
    }

    private class RendererRecord
    {
        public string source = "editor_scene";
        public string path = "";
        public string name = "";
        public string rendererType = "";
        public int layer;
        public bool active;
        public bool enabled;
        public string meshName = "";
        public string meshPath = "";
        public int subMeshIndex = -1;
        public string materialName = "";
        public string materialPath = "";
        public string shaderName = "";
        public string shaderPath = "";
        public string shaderPassSummary = "";
        public int renderQueue = -1;
        public int sortingLayerId;
        public int sortingOrder;
        public int rendererPriority;
        public int instanceId;
        public int meshInstanceId;
        public int materialInstanceId;
        public string textureName = "";
        public string textureMeta = "";
        public string materialTextures = "";
        public string bounds = "";
        public string materialFingerprint = "";
        public string resourceFingerprint = "";
        public string sortingGroup = "";
        public string prefabRoot = "";
    }

    private class SrpBatchCandidate
    {
        public int eventIndex;
        public string camera = "";
        public string stage = "";
        public string eventType = "";
        public string lightMode = "";
        public string batchCause = "";
        public string confidence = "";
        public string reason = "";
        public int matchScore;
        public string[] matchedBy = new string[0];
        public List<string> eventMeshNames = new List<string>();
        public List<string> frameDebuggerDetailMeshNames = new List<string>();
        public List<string> frameDebuggerMeshInstanceIds = new List<string>();
        public List<RendererRecord> renderers = new List<RendererRecord>();
        public bool hasFrameDebuggerDetailMeshes;
    }

    private class IssueEntry
    {
        public string name = "";
        public int count;
        public string confidence = "";
        public string evidence = "";
        public string limitations = "";
    }

    private class ExportBuildCache
    {
        public List<FrameEventInfo> events;
        public string runtimeSnapshotJson = "";
        public string renderDocAnalysisDirectory = "";
        public bool runtimeSnapshotParseAttempted;
        public RuntimePlayerSnapshot runtimeSnapshot;
        public List<RuntimeTextureRecord> runtimeTextures;
        public List<RuntimeTextureThumbnailRecord> runtimeTextureThumbnails;
        public Dictionary<int, UiBatchCandidate> uiBatchCandidateMap;
        public Dictionary<int, List<ParticleCandidate>> particleCandidateMap;
        public Dictionary<int, SrpBatchCandidate> srpBatchCandidateMap;
        public List<UiGraphicRecord> uiGraphicRecords;
        public List<ParticleSystem> sceneParticleSystems;
        public List<ParticleCandidate> activeParticleCandidates;
        public List<RendererRecord> sceneRendererRecords;
        public List<Camera> sceneCameras;
        public List<RenderFeatureSnapshot> renderFeatureSnapshots;
    }

    private class RuntimePlayerSnapshot
    {
        public bool available;
        public bool success;
        public string status = "";
        public string error = "";
        public int frameCount;
        public string scene = "";
        public List<RuntimeCameraRecord> cameras = new List<RuntimeCameraRecord>();
        public List<RendererRecord> renderers = new List<RendererRecord>();
        public List<UiGraphicRecord> uiGraphics = new List<UiGraphicRecord>();
        public List<ParticleCandidate> particles = new List<ParticleCandidate>();
        public List<RuntimeTextureRecord> textures = new List<RuntimeTextureRecord>();
        public List<RuntimeTextureThumbnailRecord> textureThumbnails = new List<RuntimeTextureThumbnailRecord>();
    }

    private class RuntimeCameraRecord
    {
        public string path = "";
        public string name = "";
        public bool enabled;
        public bool activeInHierarchy;
        public float depth;
        public string clearFlags = "";
        public int cullingMask = -1;
        public string cullingMaskNames = "";
        public bool orthographic;
        public float orthographicSize;
        public float fieldOfView;
        public float nearClipPlane;
        public float farClipPlane;
        public bool allowHDR;
        public bool allowMSAA;
        public int pixelWidth;
        public int pixelHeight;
        public int scaledPixelWidth;
        public int scaledPixelHeight;
        public float aspect;
        public string rect = "";
        public string cameraType = "";
        public string actualRenderingPath = "";
        public string renderingPath = "";
        public bool useOcclusionCulling;
        public string targetTexture = "";
        public string targetTextureMeta = "";
        public string pixelRect = "";
        public string urpAdditionalData = "";
    }

    private class RuntimeTextureRecord
    {
        public string name = "";
        public string type = "";
        public int instanceId;
        public int width;
        public int height;
        public string dimension = "";
        public string format = "";
        public int mipMapCount;
        public int antiAliasing;
        public int depth;
        public int memoryBytes;
    }

    private class RuntimeTextureThumbnailRecord
    {
        public string name = "";
        public string type = "";
        public int instanceId;
        public int width;
        public int height;
        public int thumbnailWidth;
        public int thumbnailHeight;
        public string pngBase64 = "";
        public string relativePath = "";
    }

    private class UnityCorrelationCandidate
    {
        public string type = "";
        public string path = "";
        public string materialName = "";
        public string shaderName = "";
        public string textureName = "";
        public string materialTextures = "";
        public int width;
        public int height;
        public string screenRect = "";
        public string fingerprint = "";
    }

    private class ExportTimingEntry
    {
        public string path = "";
        public long elapsedMs;
        public long bytes;
    }

    private class LinkedCaptureContext
    {
        public string captureId = "";
        public string startedAtUtc = "";
        public string scene = "";
        public string sceneSource = "";
        public string editorSceneAtExport = "";
        public string activeCamera = "";
        public string gameViewSize = "";
        public int unityFrameCount;
        public float originalTimeScale = 1f;
        public bool timeScaleFrozen;
        public string captureMode = "renderdoc_trigger_next_frame_then_framedebugger_export";
        public string sameFrameGuarantee = "same_frozen_unity_state_not_same_present_event";
        public string renderDocDirectory = "";
        public string renderDocPathTemplate = "";
        public string renderDocCapturePath = "";
        public int renderDocCaptureCountBefore;
        public int renderDocCaptureCountAfter;
        public double renderDocTriggerEditorTime;
        public double renderDocCompletedEditorTime;
        public string renderDocApiVersion = "";
        public string renderDocStatus = "";
        public string renderDocTriggerResultPath = "";
        public string renderDocTargetUrl = "";
        public string renderDocTargetName = "";
        public int renderDocTargetPid;
        public int renderDocFrameNumber;
        public long renderDocByteSize;
        public string renderDocAnalysisDirectory = "";
        public string renderDocAnalysisStatus = "";
        public int renderDocAnalysisExitCode;
        public double renderDocAnalysisStartedEditorTime;
        public double renderDocAnalysisCompletedEditorTime;
        public string runtimeSnapshotStatus = "";
        public string runtimeSnapshotPath = "";
        public int runtimeSnapshotByteSize;
        public double runtimeSnapshotRequestedEditorTime;
        public double runtimeSnapshotReceivedEditorTime;
        public bool autoAnalyze;
    }

    [Serializable]
    private class RenderDocTriggerResult
    {
        public bool success;
        public string error = "";
        public string url = "";
        public int ident;
        public string target = "";
        public string api = "";
        public int pid;
        public int captureId;
        public int frameNumber;
        public long byteSize;
        public string remotePath = "";
        public string localPath = "";
        public string copyMode = "";
        public RenderDocTargetCandidate[] targets = new RenderDocTargetCandidate[0];
    }

    [Serializable]
    private class RenderDocTargetCandidate
    {
        public string url = "";
        public int ident;
        public string target = "";
        public string api = "";
        public int pid;
        public int matchScore;
        public string connectError = "";
        public string busyClient = "";
    }

    #endregion

    #region Instance State

    private string _statusMessage = "";
    private string _lastExportPath = "";
    private string _exportDir = "";
    private readonly List<ExportTimingEntry> _exportTimings = new List<ExportTimingEntry>();
    private string _activeExportDir = "";
    private LinkedCaptureContext _linkedCaptureContext;
    private string _runtimePlayerSnapshotJson = "";
    private Action<string> _runtimePlayerSnapshotCallback;

    private List<FrameEventInfo> _capturedEvents;
    private Dictionary<string, PassStatEntry> _passStats;
    private string _captureTimestamp = "";
    private Array _asyncEventsArray;
    private int _asyncEventCount;
    private int _asyncEventIndex;
    private int _asyncOriginalLimit;
    private bool _asyncCaptureRunning;
    private Action<string> _asyncComplete;
    private Action<Exception> _asyncError;

    #endregion

    private void RegisterRuntimePlayerSnapshotCallback(Action<string> callback)
    {
        _runtimePlayerSnapshotCallback = callback;
        EditorConnection.instance.Unregister(RuntimeSnapshotResponseGuid, OnRuntimePlayerSnapshotResponse);
        EditorConnection.instance.Register(RuntimeSnapshotResponseGuid, OnRuntimePlayerSnapshotResponse);
    }

    private void UnregisterRuntimePlayerSnapshotCallback()
    {
        try { EditorConnection.instance.Unregister(RuntimeSnapshotResponseGuid, OnRuntimePlayerSnapshotResponse); }
        catch { }
        _runtimePlayerSnapshotCallback = null;
    }

    private void OnRuntimePlayerSnapshotResponse(UnityEngine.Networking.PlayerConnection.MessageEventArgs args)
    {
        string json = args != null && args.data != null ? Encoding.UTF8.GetString(args.data) : "";
        _runtimePlayerSnapshotJson = json;
        var callback = _runtimePlayerSnapshotCallback;
        if (callback != null)
            callback(json);
    }

    #region Entry Point

    [MenuItem("Tools/AI 分析/Frame Debugger/导出当前帧...")]
    public static void ShowAiExportConfirmWindowMenu()
    {
        ShowAiExportConfirmWindow();
    }

    [MenuItem("Tools/AI 分析/Frame Debugger/导出 RenderDoc 联动帧...")]
    public static void ShowRemoteLinkedCaptureWindowMenu()
    {
        FrozenFrameLinkedCaptureWindow.Open();
    }

    [MenuItem("Tools/AI 分析/Frame Debugger/清理 RenderDoc 联动进度条")]
    public static void ClearLinkedRenderDocProgressBar()
    {
        EditorUtility.ClearProgressBar();
    }

    public static void ShowAiExportConfirmWindow()
    {
        FrameDebuggerAIExportConfirmWindow.Open();
    }

    public static string CaptureAndExportAiBundleToDefaultDirectory()
    {
        var exporter = CreateInstance<FrameDebuggerExport>();
        try
        {
            InitReflection();
            exporter._exportDir = GetDefaultAiExportRoot();
            if (!exporter.IsFrameDebuggerEnabled())
                exporter.TrySetFrameDebuggerEnabled(true);
            exporter.CaptureFrameData();
            if (exporter._capturedEvents == null || exporter._capturedEvents.Count == 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(exporter._statusMessage) ? "没有可导出的 Frame Debugger 事件。" : exporter._statusMessage);

            return exporter.ExportAiBundleToDirectory(GetDefaultAiExportRoot(), true);
        }
        finally
        {
            DestroyImmediate(exporter);
        }
    }

    public static void CaptureAndExportAiBundleToDefaultDirectoryAsync(bool autoAnalyze)
    {
        var exporter = CreateInstance<FrameDebuggerExport>();
        try
        {
            InitReflection();
            exporter._exportDir = GetDefaultAiExportRoot();
            if (!exporter.IsFrameDebuggerEnabled())
                exporter.TrySetFrameDebuggerEnabled(true);

            exporter.BeginCaptureFrameDataAsync(
                exportDir =>
                {
                    if (autoAnalyze)
                        ProfilerAIClaudeRunner.StartFrameDebugAnalysis(exportDir);
                },
                ex =>
                {
                    Debug.LogException(ex);
                    EditorUtility.DisplayDialog("Frame Debugger AI 导出失败", ex.Message, "确定");
                });
        }
        catch (Exception ex)
        {
            DestroyImmediate(exporter);
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Frame Debugger AI 导出失败", ex.Message, "确定");
        }
    }

    public static string GetDefaultAiExportRoot()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), DefaultAiExportDirectoryName);
    }

    public static void CaptureAndExportLinkedFrameDebuggerAsync(bool autoAnalyze)
    {
        CaptureRenderDocAndExportLinkedFrameDebuggerAsync(autoAnalyze, "", "");
    }

    public static void CaptureRenderDocAndExportLinkedFrameDebuggerAsync(bool autoAnalyze, string targetMatch, string targetUrl)
    {
        var job = new LinkedRenderDocCaptureJob(autoAnalyze, targetMatch, targetUrl);
        job.Start();
    }

    private static void ExportLinkedFrameDebuggerAfterRenderDocAsync(FrameDebuggerExport exporter, bool autoAnalyze)
    {
        try
        {
            exporter.BeginCaptureFrameDataAsync(
                exportDir =>
                {
                    if (autoAnalyze)
                        ProfilerAIClaudeRunner.StartFrameDebugAnalysis(exportDir);
                    DestroyImmediate(exporter);
                },
                ex =>
                {
                    DestroyImmediate(exporter);
                    Debug.LogException(ex);
                    EditorUtility.DisplayDialog("Frame Debugger 联动导出失败", ex.Message, "确定");
                });
        }
        catch (Exception ex)
        {
            DestroyImmediate(exporter);
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Frame Debugger 联动导出失败", ex.Message, "确定");
        }
    }

    private static LinkedCaptureContext CreateFrameDebuggerFrozenLinkedCaptureContext()
    {
        string captureId = "FrameDebuggerLinkedCapture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return new LinkedCaptureContext
        {
            captureId = captureId,
            startedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            scene = "remote_player_scene_unknown",
            sceneSource = "unavailable_for_remote_development_player",
            editorSceneAtExport = SceneManager.GetActiveScene().name,
            activeCamera = "",
            gameViewSize = "",
            unityFrameCount = -1,
            originalTimeScale = -1f,
            timeScaleFrozen = false,
            captureMode = "framedebugger_enable_freeze_then_auto_renderdoc_targetcontrol_capture_then_framedebugger_export",
            sameFrameGuarantee = "same_framedebugger_frozen_remote_target_state",
            renderDocDirectory = "",
            renderDocPathTemplate = "",
            renderDocCapturePath = "",
            renderDocStatus = "Pending RenderDoc TargetControl capture.",
            renderDocApiVersion = "renderdoc_target_control",
            renderDocAnalysisDirectory = "",
            renderDocAnalysisStatus = "Pending RenderDoc AI index export."
        };
    }

    private sealed class LinkedRenderDocCaptureJob
    {
        private readonly bool autoAnalyze;
        private readonly string targetMatch;
        private readonly string targetUrl;
        private FrameDebuggerExport exporter;
        private LinkedCaptureContext context;
        private System.Diagnostics.Process process;
        private System.Diagnostics.Process analysisProcess;
        private readonly StringBuilder stdout = new StringBuilder();
        private readonly StringBuilder stderr = new StringBuilder();
        private readonly StringBuilder analysisStdout = new StringBuilder();
        private readonly StringBuilder analysisStderr = new StringBuilder();
        private string resultJsonPath = "";
        private string renderDocAnalysisDirectory = "";
        private double waitFrameDebuggerUntil;
        private double renderDocProcessStartedAt;
        private double renderDocAnalysisStartedAt;
        private double forceDisableUntil;
        private double runtimeSnapshotWaitUntil;
        private bool processStarted;
        private bool renderDocCaptureCompleted;
        private bool analysisStarted;
        private bool oldFrameDebuggerDisabled;
        private bool runtimeSnapshotRequested;
        private bool runtimeSnapshotCompleted;
        private string runtimeSnapshotJson = "";
        private string lastProfilerDiagnostic = "";

        public LinkedRenderDocCaptureJob(bool autoAnalyze, string targetMatch, string targetUrl)
        {
            this.autoAnalyze = autoAnalyze;
            this.targetMatch = targetMatch ?? "";
            this.targetUrl = targetUrl ?? "";
        }

        public void Start()
        {
            try
            {
                InitReflection();
                exporter = CreateInstance<FrameDebuggerExport>();
                exporter._exportDir = GetDefaultAiExportRoot();
                context = CreateFrameDebuggerFrozenLinkedCaptureContext();
                context.renderDocTriggerEditorTime = EditorApplication.timeSinceStartup;
                exporter._linkedCaptureContext = context;

                OpenFrameDebuggerWindowForCapture();

                forceDisableUntil = EditorApplication.timeSinceStartup + 2.0;
                waitFrameDebuggerUntil = EditorApplication.timeSinceStartup + 12.0;
                EditorApplication.update += OnUpdate;
            }
            catch (Exception ex)
            {
                Cleanup();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("RenderDoc 联动采集失败", ex.Message, "确定");
            }
        }

        private void OnUpdate()
        {
            try
            {
                if (!processStarted)
                {
                    if (!oldFrameDebuggerDisabled)
                    {
                        if (exporter.IsFrameDebuggerEnabled())
                        {
                            exporter.TrySetFrameDebuggerEnabled(false);
                            EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在清理旧 Frame Debugger 冻结状态...", 0.01f);
                            if (EditorApplication.timeSinceStartup < forceDisableUntil)
                                return;
                        }

                        oldFrameDebuggerDisabled = true;
                    }

                    if (!exporter.IsFrameDebuggerEnabled())
                    {
                        string diagnostic;
                        if (!exporter.TryEnableFrameDebuggerForLinkedCapture(targetMatch, out diagnostic))
                        {
                            lastProfilerDiagnostic = diagnostic;
                            if (EditorApplication.timeSinceStartup < waitFrameDebuggerUntil)
                            {
                                EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在等待 Unity 发现远端 Development Player...", 0.03f);
                                return;
                            }

                            throw new InvalidOperationException(
                                "没有切换到远端 Development Player，已停止，避免继续抓 Editor。\n\n" +
                                diagnostic);
                        }
                    }

                    if (!exporter.IsFrameDebuggerEnabled() || exporter.GetEventCount() == 0)
                    {
                        if (EditorApplication.timeSinceStartup < waitFrameDebuggerUntil)
                        {
                            EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在等待 Frame Debugger 远端帧冻结...", 0.05f);
                            return;
                        }

                        throw new InvalidOperationException(
                            "Frame Debugger 没有可导出的事件。当前不会继续抓 Editor。\n\n" +
                            "Unity PlayerConnection 诊断：\n" + lastProfilerDiagnostic);
                    }

                    if (!runtimeSnapshotRequested)
                    {
                        RequestRuntimePlayerSnapshot();
                        return;
                    }

                    if (!runtimeSnapshotCompleted && EditorApplication.timeSinceStartup < runtimeSnapshotWaitUntil)
                    {
                        EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在等待远端 Player 返回运行时渲染快照...", 0.08f);
                        return;
                    }

                    if (!runtimeSnapshotCompleted && context != null && string.Equals(context.runtimeSnapshotStatus, "requested", StringComparison.OrdinalIgnoreCase))
                        context.runtimeSnapshotStatus = "timeout_or_agent_not_available";

                    StartRenderDocProcess();
                    return;
                }

                if (process != null && !process.HasExited)
                {
                    if (IsRenderDocResultReady())
                    {
                        CompleteRenderDocProcess();
                        return;
                    }

                    if (renderDocProcessStartedAt > 0.0 && EditorApplication.timeSinceStartup - renderDocProcessStartedAt > 150.0)
                    {
                        throw new TimeoutException(
                            "RenderDoc 抓帧脚本超过 150 秒没有完成，也没有写出可用 result JSON。\n\n" +
                            "Result: " + resultJsonPath + "\n" +
                            "stdout:\n" + stdout + "\n" +
                            "stderr:\n" + stderr);
                    }

                    EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在触发 RenderDoc 抓帧并复制 .rdc...", 0.35f);
                    return;
                }

                if (!renderDocCaptureCompleted)
                {
                    CompleteRenderDocProcess();
                    return;
                }

                if (!analysisStarted)
                {
                    StartRenderDocAnalysisProcess();
                    return;
                }

                if (analysisProcess != null && !analysisProcess.HasExited)
                {
                    if (renderDocAnalysisStartedAt > 0.0 && EditorApplication.timeSinceStartup - renderDocAnalysisStartedAt > 240.0)
                    {
                        throw new TimeoutException(
                            "RenderDoc .rdc 解析超过 240 秒没有完成。\n\n" +
                            "RDC: " + context.renderDocCapturePath + "\n" +
                            "OUT: " + renderDocAnalysisDirectory + "\n\n" +
                            "stdout:\n" + analysisStdout + "\n" +
                            "stderr:\n" + analysisStderr);
                    }

                    EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在解析 RenderDoc .rdc 并导出 AI 索引...", 0.70f);
                    return;
                }

                CompleteRenderDocAnalysisProcess();
            }
            catch (Exception ex)
            {
                Cleanup();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("RenderDoc 联动采集失败", ex.Message, "确定");
            }
        }

        private void RequestRuntimePlayerSnapshot()
        {
            runtimeSnapshotRequested = true;
            runtimeSnapshotCompleted = false;
            runtimeSnapshotJson = "";
            runtimeSnapshotWaitUntil = EditorApplication.timeSinceStartup + RuntimeSnapshotTimeoutSeconds;
            if (context != null)
            {
                context.runtimeSnapshotStatus = "requested";
                context.runtimeSnapshotRequestedEditorTime = EditorApplication.timeSinceStartup;
            }

            try
            {
                exporter.RegisterRuntimePlayerSnapshotCallback(OnRuntimePlayerSnapshotJson);
                string request = "{\"captureId\":\"" + EscapeJson(context != null ? context.captureId : "") + "\"}";
                EditorConnection.instance.Send(RuntimeSnapshotRequestGuid, Encoding.UTF8.GetBytes(request));
                EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "已请求远端 Player 运行时渲染快照...", 0.07f);
            }
            catch (Exception ex)
            {
                runtimeSnapshotCompleted = true;
                if (context != null)
                    context.runtimeSnapshotStatus = "request_failed: " + ex.Message;
            }
        }

        private void OnRuntimePlayerSnapshotJson(string json)
        {
            try
            {
                runtimeSnapshotJson = json ?? "";
                runtimeSnapshotCompleted = true;
                if (context != null)
                {
                    context.runtimeSnapshotStatus = string.IsNullOrEmpty(runtimeSnapshotJson) ? "empty_response" : "received";
                    context.runtimeSnapshotByteSize = Encoding.UTF8.GetByteCount(runtimeSnapshotJson ?? "");
                    context.runtimeSnapshotReceivedEditorTime = EditorApplication.timeSinceStartup;
                }
            }
            finally
            {
                if (exporter != null)
                    exporter.UnregisterRuntimePlayerSnapshotCallback();
            }
        }

        private void StartRenderDocProcess()
        {
            string portableRoot = FindRenderDocAiPortableRoot();
            if (string.IsNullOrEmpty(portableRoot))
                throw new FileNotFoundException("找不到 RenderDocAIAnalyzer_Portable。请把便携包放到项目根目录，或设置环境变量 RENDERDOC_AI_ANALYZER_HOME。");

            PythonLaunch python = ResolveRenderDocAnalyzerPython(portableRoot);
            string helper = Path.Combine(portableRoot, "rdc_ai_analyzer", "renderdoc_trigger_capture.py");
            string renderDocPath = Path.Combine(portableRoot, ".rdc_runtime", "renderdoc");
            if (!File.Exists(helper))
                throw new FileNotFoundException("找不到 RenderDoc 触发脚本: " + helper);

            string captureRoot = Path.Combine(GetDefaultAiExportRoot(), "RenderDocCaptures");
            Directory.CreateDirectory(captureRoot);
            resultJsonPath = Path.Combine(captureRoot, context.captureId + "_renderdoc_result.json");
            context.renderDocDirectory = captureRoot;
            context.renderDocPathTemplate = Path.Combine(captureRoot, context.captureId + ".rdc");
            context.renderDocTriggerResultPath = resultJsonPath;

            var args = new List<string>
            {
                QuoteArgument(helper),
                "--out-dir", QuoteArgument(captureRoot),
                "--capture-id", QuoteArgument(context.captureId),
                "--json-out", QuoteArgument(resultJsonPath),
                "--renderdoc-path", QuoteArgument(renderDocPath),
                "--timeout", "30",
                "--copy-timeout", "90"
            };
            if (!string.IsNullOrWhiteSpace(targetMatch))
            {
                args.Add("--target-match");
                args.Add(QuoteArgument(targetMatch));
            }
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                args.Add("--url");
                args.Add(QuoteArgument(targetUrl));
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = python.fileName,
                Arguments = BuildPythonArguments(python, string.Join(" ", args.ToArray())),
                WorkingDirectory = portableRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["RENDERDOC_AI_ANALYZER_HOME"] = portableRoot;

            process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) stderr.AppendLine(e.Data); };
            if (!process.Start())
                throw new InvalidOperationException("无法启动 RenderDoc 触发脚本。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            processStarted = true;
            renderDocProcessStartedAt = EditorApplication.timeSinceStartup;
        }

        private void CompleteRenderDocProcess()
        {
            EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在读取 RenderDoc 抓帧结果...", 0.75f);

            string json = File.Exists(resultJsonPath) ? File.ReadAllText(resultJsonPath, Encoding.UTF8) : stdout.ToString();
            var result = JsonUtility.FromJson<RenderDocTriggerResult>(json);
            if (result == null)
                throw new InvalidOperationException("RenderDoc 触发脚本没有输出有效 JSON。\n" + stderr);
            if (!result.success)
            {
                string details = BuildRenderDocTriggerFailureDetails(result, stderr.ToString());
                throw new InvalidOperationException("RenderDoc 自动抓帧失败。\n" + details);
            }

            context.renderDocCompletedEditorTime = EditorApplication.timeSinceStartup;
            context.renderDocCaptureCountBefore = 0;
            context.renderDocCaptureCountAfter = 1;
            context.renderDocCapturePath = result.localPath;
            context.renderDocStatus = "RenderDoc capture completed through TargetControl; .rdc copied before Frame Debugger export.";
            context.renderDocApiVersion = result.api;
            context.renderDocTargetUrl = result.url;
            context.renderDocTargetName = result.target;
            context.renderDocTargetPid = result.pid;
            context.renderDocFrameNumber = result.frameNumber;
            context.renderDocByteSize = result.byteSize;

            ReleaseRenderDocTriggerProcess();
            renderDocCaptureCompleted = true;
        }

        private bool IsRenderDocResultReady()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(resultJsonPath) || !File.Exists(resultJsonPath))
                    return false;

                string json = File.ReadAllText(resultJsonPath, Encoding.UTF8);
                var result = JsonUtility.FromJson<RenderDocTriggerResult>(json);
                if (result == null)
                    return false;

                if (!result.success)
                    return !string.IsNullOrEmpty(result.error);

                return !string.IsNullOrWhiteSpace(result.localPath) &&
                       File.Exists(result.localPath) &&
                       new FileInfo(result.localPath).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void ReleaseRenderDocTriggerProcess()
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }

            try { process.Dispose(); }
            catch { }
            process = null;
        }

        private void StartRenderDocAnalysisProcess()
        {
            string portableRoot = FindRenderDocAiPortableRoot();
            if (string.IsNullOrEmpty(portableRoot))
                throw new FileNotFoundException("找不到 RenderDocAIAnalyzer_Portable。请把便携包放到项目根目录，或设置环境变量 RENDERDOC_AI_ANALYZER_HOME。");

            PythonLaunch python = ResolveRenderDocAnalyzerPython(portableRoot);
            string renderDocPath = Path.Combine(portableRoot, ".rdc_runtime", "renderdoc");
            if (string.IsNullOrWhiteSpace(context.renderDocCapturePath) || !File.Exists(context.renderDocCapturePath))
                throw new FileNotFoundException("RenderDoc .rdc 文件不存在，无法解析: " + context.renderDocCapturePath);

            renderDocAnalysisDirectory = Path.Combine(
                Path.GetDirectoryName(context.renderDocCapturePath),
                Path.GetFileNameWithoutExtension(context.renderDocCapturePath) + "_export");

            context.renderDocAnalysisDirectory = renderDocAnalysisDirectory;
            context.renderDocAnalysisStatus = "Running RenderDoc AI index export.";
            context.renderDocAnalysisStartedEditorTime = EditorApplication.timeSinceStartup;

            var args = new List<string>
            {
                "-m", "rdc_ai_analyzer",
                "--renderdoc-path", QuoteArgument(renderDocPath),
                "index",
                "--rdc", QuoteArgument(context.renderDocCapturePath),
                "--out", QuoteArgument(renderDocAnalysisDirectory)
            };

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = python.fileName,
                Arguments = BuildPythonArguments(python, string.Join(" ", args.ToArray())),
                WorkingDirectory = portableRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["RENDERDOC_AI_ANALYZER_HOME"] = portableRoot;

            analysisProcess = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            analysisProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) analysisStdout.AppendLine(e.Data); };
            analysisProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) analysisStderr.AppendLine(e.Data); };
            if (!analysisProcess.Start())
                throw new InvalidOperationException("无法启动 RenderDoc 解析脚本。");
            analysisProcess.BeginOutputReadLine();
            analysisProcess.BeginErrorReadLine();
            analysisStarted = true;
            renderDocAnalysisStartedAt = EditorApplication.timeSinceStartup;
        }

        private void CompleteRenderDocAnalysisProcess()
        {
            EditorApplication.update -= OnUpdate;
            EditorUtility.DisplayProgressBar("RenderDoc 联动采集", "正在完成 Frame Debugger AI Bundle 导出...", 0.90f);

            int exitCode = analysisProcess != null ? analysisProcess.ExitCode : -1;
            context.renderDocAnalysisExitCode = exitCode;
            context.renderDocAnalysisCompletedEditorTime = EditorApplication.timeSinceStartup;
            if (exitCode != 0)
            {
                context.renderDocAnalysisStatus = "RenderDoc AI index export failed.";
                throw new InvalidOperationException(
                    "RenderDoc .rdc 已抓取，但解析失败，已停止导出，避免生成缺少 RenderDoc 索引的联动包。\n\n" +
                    "RDC: " + context.renderDocCapturePath + "\n" +
                    "OUT: " + renderDocAnalysisDirectory + "\n\n" +
                    "stdout:\n" + analysisStdout + "\n" +
                    "stderr:\n" + analysisStderr);
            }

            context.renderDocAnalysisStatus = "RenderDoc AI index export completed.";
            EditorUtility.ClearProgressBar();
            exporter._runtimePlayerSnapshotJson = runtimeSnapshotJson ?? "";
            ExportLinkedFrameDebuggerAfterRenderDocAsync(exporter, autoAnalyze);
        }

        private void Cleanup()
        {
            EditorApplication.update -= OnUpdate;
            EditorUtility.ClearProgressBar();
            if (exporter != null)
                exporter.UnregisterRuntimePlayerSnapshotCallback();
            if (process != null)
            {
                ReleaseRenderDocTriggerProcess();
            }
            if (analysisProcess != null)
            {
                try
                {
                    if (!analysisProcess.HasExited)
                        analysisProcess.Kill();
                }
                catch { }
                analysisProcess.Dispose();
                analysisProcess = null;
            }
            if (exporter != null)
            {
                DestroyImmediate(exporter);
                exporter = null;
            }
        }
    }

    private class PythonLaunch
    {
        public string fileName = "";
        public string argumentPrefix = "";
    }

    private static PythonLaunch ResolveRenderDocAnalyzerPython(string portableRoot)
    {
        string embeddedPython = Path.Combine(portableRoot, "python", "python.exe");
        if (File.Exists(embeddedPython))
            return new PythonLaunch { fileName = embeddedPython };

        string systemPython = FindExecutableOnPath("python.exe");
        if (!string.IsNullOrEmpty(systemPython) && IsCompatibleRenderDocPython(systemPython, ""))
            return new PythonLaunch { fileName = systemPython };

        string pyLauncher = FindExecutableOnPath("py.exe");
        if (!string.IsNullOrEmpty(pyLauncher) && IsCompatibleRenderDocPython(pyLauncher, "-3.14"))
            return new PythonLaunch { fileName = pyLauncher, argumentPrefix = "-3.14" };

        throw new FileNotFoundException(
            "找不到 Python，无法运行 RenderDocAIAnalyzer_Portable。\n\n" +
            "可选方案：\n" +
            "1. 使用完整版便携包，保留 python\\python.exe；\n" +
            "2. 在本机安装 64 位 Python 3.14，并确保 python.exe 或 py.exe 在 PATH 中。\n\n" +
            "当前 RenderDoc runtime 带有 python314.dll/renderdoc.pyd，推荐 Python 3.14 x64。");
    }

    private static string BuildPythonArguments(PythonLaunch python, string arguments)
    {
        if (python == null || string.IsNullOrWhiteSpace(python.argumentPrefix))
            return arguments;
        return python.argumentPrefix + " " + arguments;
    }

    private static bool IsCompatibleRenderDocPython(string pythonExe, string argumentPrefix)
    {
        try
        {
            string check = "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 14) and sys.maxsize > 2**32 else 1)";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = BuildPythonArguments(new PythonLaunch { argumentPrefix = argumentPrefix }, "-c " + QuoteArgument(check)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit(3000);
                return p.HasExited && p.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string FindExecutableOnPath(string exeName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string part in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;
            try
            {
                string candidate = Path.Combine(part.Trim().Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return "";
    }

    private static string FindRenderDocAiPortableRoot()
    {
        var candidates = new List<string>();
        string pref = EditorPrefs.GetString(RenderDocAiHomePrefKey, "");
        string env = Environment.GetEnvironmentVariable("RENDERDOC_AI_ANALYZER_HOME");
        if (!string.IsNullOrEmpty(pref)) candidates.Add(pref);
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);

        string projectRoot = Directory.GetCurrentDirectory();
        candidates.Add(Path.Combine(projectRoot, "RenderDocAIAnalyzer_Portable"));
        candidates.Add(Path.Combine(projectRoot, "Tools", "RenderDocAIAnalyzer_Portable"));
        candidates.Add(@"E:\Unity+AI\dist\RenderDocAIAnalyzer_Portable");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RenderDocAIAnalyzer_Portable"));

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            string root = candidate.Trim().Trim('"');
            if (File.Exists(Path.Combine(root, "rdc_ai_analyzer", "renderdoc_trigger_capture.py")) &&
                File.Exists(Path.Combine(root, ".rdc_runtime", "renderdoc", "renderdoc.pyd")))
                return root;
        }
        return "";
    }

    private static void OpenFrameDebuggerWindowForCapture()
    {
        try
        {
            Type frameDebuggerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow")
                                          ?? typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerWindow");
            if (frameDebuggerWindowType == null)
                return;

            MethodInfo openWindow = frameDebuggerWindowType.GetMethod("OpenWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (openWindow != null)
            {
                openWindow.Invoke(null, null);
                return;
            }

            EditorWindow.GetWindow(frameDebuggerWindowType, false, "Frame Debugger");
        }
        catch
        {
            // Frame Debugger can still be enabled through internal utility APIs on some Unity versions.
        }
    }

    private static string BuildRenderDocTriggerFailureDetails(RenderDocTriggerResult result, string stderrText)
    {
        var sb = new StringBuilder(1024);
        if (result != null && !string.IsNullOrEmpty(result.error))
            sb.AppendLine(result.error);
        else if (!string.IsNullOrEmpty(stderrText))
            sb.AppendLine(stderrText);
        else
            sb.AppendLine("Unknown RenderDoc trigger failure.");

        if (result != null)
        {
            if (!string.IsNullOrEmpty(result.url) || result.ident != 0 || !string.IsNullOrEmpty(result.target))
            {
                sb.AppendLine();
                sb.AppendLine("Selected target:");
                sb.AppendLine("  " + FormatRenderDocTarget(result.url, result.ident, result.target, result.api, result.pid, ""));
            }

            if (result.targets != null && result.targets.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("RenderDoc candidates:");
                foreach (var target in result.targets.Take(8))
                    sb.AppendLine("  " + FormatRenderDocTarget(target.url, target.ident, target.target, target.api, target.pid, target.busyClient));
            }
        }

        sb.AppendLine();
        sb.AppendLine("If it still times out, fill RenderDoc 目标匹配 with the render process shown above, usually MuMuVMMHeadless instead of MuMuNxDevice.");
        return sb.ToString();
    }

    private static string FormatRenderDocTarget(string url, int ident, string target, string api, int pid, string busyClient)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(target)) parts.Add(target);
        if (pid != 0) parts.Add("pid=" + pid.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(api)) parts.Add("api=" + api);
        if (!string.IsNullOrEmpty(url) || ident != 0) parts.Add("url=" + url + "#" + ident.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(busyClient)) parts.Add("busy=" + busyClient);
        return string.Join(", ", parts.ToArray());
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        InitReflection();
        _exportDir = EditorPrefs.GetString("FrameDebuggerExport.ExportDir",
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
    }

    private void OnDisable()
    {
        EditorPrefs.SetString("FrameDebuggerExport.ExportDir", _exportDir);
    }

    #endregion

    #region Reflection Initialization

    private static void InitReflection()
    {
        if (s_reflectionInited) return;
        s_reflectionInited = true;
        s_reflectionError = null;

        try
        {
            var bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            // --- FrameDebuggerUtility ---
            s_utilType = FindType("UnityEditorInternal.FrameDebuggerUtility",
                                  "UnityEditor.FrameDebuggerUtility");
            if (s_utilType == null)
            {
                s_reflectionError = "Cannot find FrameDebuggerUtility type.";
                return;
            }

            s_countProp = s_utilType.GetProperty("count", bf)
                       ?? s_utilType.GetProperty("eventsCount", bf);
            s_limitProp = s_utilType.GetProperty("limit", bf)
                       ?? s_utilType.GetProperty("eventsLimit", bf);
            s_limitField = s_utilType.GetField("limit", bf)
                       ?? s_utilType.GetField("eventsLimit", bf)
                       ?? s_utilType.GetField("s_Limit", bf)
                       ?? s_utilType.GetField("m_Limit", bf);
            s_getFrameEvents = s_utilType.GetMethod("GetFrameEvents", bf);
            s_getFrameEventObject = s_utilType.GetMethod("GetFrameEventObject", bf);
            s_getFrameEventGameObject = FindStaticFrameDebuggerMethod("GetFrameEventGameObject", typeof(int));
            s_getFrameEventRenderer = FindStaticFrameDebuggerMethod("GetFrameEventRenderer", typeof(int))
                                   ?? FindStaticFrameDebuggerMethod("GetFrameEventRendererComponent", typeof(int));
            s_getBatchBreakCauseStrings = s_utilType.GetMethod("GetBatchBreakCauseStrings", bf);
            s_isLocalEnabled = s_utilType.GetMethod("IsLocalEnabled", bf);
            s_isRemoteEnabled = s_utilType.GetMethod("IsRemoteEnabled", bf);
            if (s_getBatchBreakCauseStrings != null)
            {
                try
                {
                    s_batchBreakCauseStrings = (s_getBatchBreakCauseStrings.Invoke(null, null) as string[]) ?? new string[0];
                }
                catch
                {
                    s_batchBreakCauseStrings = new string[0];
                }
            }

            // If not found on FrameDebuggerUtility, search on UnityEditor.FrameDebugger
            if (s_isLocalEnabled == null)
            {
                var fdType = FindType("UnityEditor.FrameDebugger", null);
                if (fdType != null)
                {
                    s_isLocalEnabled = fdType.GetMethod("IsLocalEnabled", bf);
                    s_isRemoteEnabled = fdType.GetMethod("IsRemoteEnabled", bf);
                    // Also try 'enabled' property as fallback
                    if (s_isLocalEnabled == null)
                        s_enabledProp = fdType.GetProperty("enabled", bf);
                    s_enabledSource = fdType.FullName;
                }
            }
            else
            {
                s_enabledSource = s_utilType.FullName;
            }

            // --- GetFrameEventData ---
            foreach (var m in s_utilType.GetMethods(bf).OrderBy(m => m.Name == "GetFrameEventData" ? 0 : 1))
            {
                var mName = m.Name.ToLowerInvariant();
                var ps = m.GetParameters();
                if ((mName.Contains("frameeventdata") || mName.Contains("geteventdata")) &&
                    !mName.Contains("impl") &&
                    ps.Length >= 1 &&
                    ps[0].ParameterType == typeof(int))
                {
                    s_getFrameEventData = m;
                    break;
                }
            }

            // --- FrameDebuggerEvent (from GetFrameEvents return type) ---
            if (s_getFrameEvents != null)
            {
                var retType = s_getFrameEvents.ReturnType;
                if (retType.IsArray)
                    s_eventType = retType.GetElementType();
            }
            if (s_eventType == null)
            {
                s_eventType = FindType("UnityEditorInternal.FrameDebuggerEvent",
                                       "UnityEditor.FrameDebuggerEvent");
            }
            if (s_eventType != null)
            {
                s_eventFields = s_eventType.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                s_eventProperties = s_eventType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            // --- FrameDebuggerEventData ---
            s_eventDataType = FindType("UnityEditorInternal.FrameDebuggerEventData",
                                       "UnityEditor.FrameDebuggerEventData");
            if (s_eventDataType != null)
            {
                s_eventDataFields = s_eventDataType.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                s_eventDataProperties = s_eventDataType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            // If no dedicated GetFrameEventData found, search all methods more broadly
            if (s_getFrameEventData == null && s_eventDataType != null)
            {
                foreach (var m in s_utilType.GetMethods(bf))
                {
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(int))
                    {
                        if (m.ReturnType == s_eventDataType)
                        {
                            s_getFrameEventData = m;
                            break;
                        }
                        foreach (var p in ps)
                        {
                            var pt = p.IsOut ? p.ParameterType.GetElementType() : p.ParameterType;
                            if (pt == s_eventDataType)
                            {
                                s_getFrameEventData = m;
                                break;
                            }
                        }
                        if (s_getFrameEventData != null) break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            s_reflectionError = $"Reflection init failed: {ex.Message}";
        }
    }

    private static Type FindType(string primary, string fallback)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(primary, false);
                if (t != null) return t;
            }
            catch { /* skip */ }
        }
        if (!string.IsNullOrEmpty(fallback))
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fallback, false);
                    if (t != null) return t;
                }
                catch { /* skip */ }
            }
        }
        // Last resort: search by short name
        string shortName = primary.Substring(primary.LastIndexOf('.') + 1);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == shortName) return t;
                }
            }
            catch { /* skip assemblies that fail to enumerate */ }
        }
        return null;
    }

    #endregion

    #region Data Capture

    private bool IsFrameDebuggerEnabled()
    {
        // Try IsLocalEnabled() method
        if (s_isLocalEnabled != null)
        {
            try
            {
                bool local = (bool)s_isLocalEnabled.Invoke(null, null);
                bool remote = s_isRemoteEnabled != null && (bool)s_isRemoteEnabled.Invoke(null, null);
                return local || remote;
            }
            catch { }
        }
        // Fallback: FrameDebugger.enabled property
        if (s_enabledProp != null)
        {
            try { return (bool)s_enabledProp.GetValue(null); }
            catch { }
        }
        // Last resort: check if count > 0 (Frame Debugger populates events only when enabled)
        return GetEventCount() > 0;
    }

    private bool TryEnableFrameDebuggerForLinkedCapture(string preferredTarget, out string diagnostic)
    {
        int remoteProfiler = TrySelectRemoteProfiler(preferredTarget, out diagnostic);
        if (remoteProfiler > 0 && TryEnableOpenFrameDebuggerWindow() && IsFrameDebuggerEnabled())
            return true;
        if (remoteProfiler > 0 && TrySetFrameDebuggerEnabled(true, remoteProfiler))
            return true;
        return false;
    }

    private static int TrySelectRemoteProfiler(string preferredTarget, out string diagnostic)
    {
        var diag = new StringBuilder(512);
        try
        {
            Type profilerDriverType = FindType("UnityEditorInternal.ProfilerDriver", null);
            if (profilerDriverType == null)
            {
                diagnostic = "UnityEditorInternal.ProfilerDriver not found.";
                return 0;
            }

            const BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo getAvailable = profilerDriverType.GetMethod("GetAvailableProfilers", bf);
            MethodInfo getIdentifier = profilerDriverType.GetMethod("GetConnectionIdentifier", bf);
            MethodInfo getProjectName = profilerDriverType.GetMethod("GetProjectName", bf);
            MethodInfo getIp = profilerDriverType.GetMethod("GetConnectionIP", bf);
            MethodInfo getPort = profilerDriverType.GetMethod("GetConnectionPort", bf);
            MethodInfo isConnectable = profilerDriverType.GetMethod("IsIdentifierConnectable", bf);
            PropertyInfo connectedProp = profilerDriverType.GetProperty("connectedProfiler", bf);
            diag.AppendLine("Profiler targets:");

            int[] ids = getAvailable != null ? getAvailable.Invoke(null, null) as int[] : null;

            string needle = (preferredTarget ?? "").Trim().ToLowerInvariant();
            int bestId = 0;
            int bestScore = int.MinValue;
            foreach (int id in ids ?? new int[0])
            {
                bool connectable = true;
                if (isConnectable != null)
                {
                    try
                    {
                        connectable = (bool)isConnectable.Invoke(null, new object[] { id });
                    }
                    catch { }
                }

                string identifier = InvokeProfilerString(getIdentifier, id);
                string project = InvokeProfilerString(getProjectName, id);
                string ip = InvokeProfilerString(getIp, id);
                string port = InvokeProfilerString(getPort, id);
                string text = (identifier + " " + project + " " + ip + " " + port + " " + id.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant();
                bool looksEditor = text.Contains("editor");
                diag.AppendLine("  id=" + id.ToString(CultureInfo.InvariantCulture) + " connectable=" + connectable + " identifier=" + identifier + " project=" + project + " ip=" + ip + " port=" + port);
                if (!connectable)
                    continue;
                int score = 0;
                if (!string.IsNullOrEmpty(needle) && text.Contains(needle)) score += 1000;
                if (!looksEditor) score += 100;
                if (!string.IsNullOrEmpty(project)) score += 20;
                if (!string.IsNullOrEmpty(ip)) score += 10;
                if (id > 0) score += 1;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = id;
                }
            }

            if (bestId <= 0)
            {
                int deviceProfiler = TrySelectRemoteDevice(profilerDriverType, preferredTarget, diag);
                diagnostic = diag.ToString();
                return deviceProfiler;
            }
            if (connectedProp != null && connectedProp.CanWrite)
            {
                try { connectedProp.SetValue(null, bestId); }
                catch { }
            }
            NotifyPlayerConnectionStateChanged(bestId);
            RefreshFrameDebuggerWindowsAfterConnectionChange();
            diagnostic = diag.ToString();
            return bestId;
        }
        catch (Exception ex)
        {
            diag.AppendLine("Profiler selection failed: " + ex.GetType().Name + ": " + ex.Message);
            diagnostic = diag.ToString();
            return 0;
        }
    }

    private static int TrySelectRemoteDevice(Type profilerDriverType, string preferredTarget, StringBuilder diag)
    {
        diag.AppendLine("Device targets:");
        try
        {
            const BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type devDeviceListType = FindType("UnityEditor.Hardware.DevDeviceList", null);
            if (devDeviceListType == null)
            {
                diag.AppendLine("  DevDeviceList not found.");
                return 0;
            }

            MethodInfo getDevices = devDeviceListType.GetMethod("GetDevices", bf);
            MethodInfo directUrlConnect = FindType("UnityEditor.Networking.PlayerConnection.GeneralConnectionState", null)
                ?.GetMethod("DirectURLConnect", bf);
            PropertyInfo connectedProp = profilerDriverType.GetProperty("connectedProfiler", bf);
            if (getDevices == null || directUrlConnect == null)
            {
                diag.AppendLine("  GetDevices or DirectURLConnect not found.");
                return 0;
            }

            Array devices = getDevices.Invoke(null, null) as Array;
            if (devices == null || devices.Length == 0)
            {
                diag.AppendLine("  No devices found.");
                return 0;
            }

            string needle = (preferredTarget ?? "").Trim().ToLowerInvariant();
            object bestDevice = null;
            string bestUrl = "";
            int bestScore = int.MinValue;
            for (int i = 0; i < devices.Length; i++)
            {
                object dev = devices.GetValue(i);
                if (dev == null)
                    continue;
                Type t = dev.GetType();
                string id = ReadMemberString(t, dev, "id");
                string name = ReadMemberString(t, dev, "name");
                string type = ReadMemberString(t, dev, "type");
                bool connected = ReadBoolProperty(t, dev, "isConnected", true);
                int features = ReadMemberInt(t, dev, "features");
                bool supportsPlayerConnection = (features & 1) != 0;
                string url = "device://" + id;
                string text = (id + " " + name + " " + type + " " + url).ToLowerInvariant();
                diag.AppendLine("  " + name + " type=" + type + " id=" + id + " connected=" + connected + " features=" + features.ToString(CultureInfo.InvariantCulture));
                if (!connected || !supportsPlayerConnection || string.IsNullOrEmpty(id))
                    continue;

                int score = 0;
                if (!string.IsNullOrEmpty(needle) && text.Contains(needle)) score += 1000;
                if (string.Equals(type, "Android", StringComparison.OrdinalIgnoreCase)) score += 100;
                if (text.Contains("adb") || text.Contains("emulator")) score += 50;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDevice = dev;
                    bestUrl = url;
                }
            }

            if (bestDevice == null || string.IsNullOrEmpty(bestUrl))
                return 0;

            directUrlConnect.Invoke(null, new object[] { bestUrl });
            RefreshFrameDebuggerWindowsAfterConnectionChange();
            int connectedProfiler = 0;
            if (connectedProp != null)
            {
                try { connectedProfiler = Convert.ToInt32(connectedProp.GetValue(null), CultureInfo.InvariantCulture); }
                catch { }
            }
            diag.AppendLine("Selected device URL: " + bestUrl + " connectedProfiler=" + connectedProfiler.ToString(CultureInfo.InvariantCulture));
            return connectedProfiler != 0 ? connectedProfiler : 65262;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadMemberString(Type type, object instance, string name)
    {
        try
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return Convert.ToString(field.GetValue(instance), CultureInfo.InvariantCulture) ?? "";
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return Convert.ToString(prop.GetValue(instance), CultureInfo.InvariantCulture) ?? "";
        }
        catch { }
        return "";
    }

    private static int ReadMemberInt(Type type, object instance, string name)
    {
        try
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return Convert.ToInt32(field.GetValue(instance), CultureInfo.InvariantCulture);
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return Convert.ToInt32(prop.GetValue(instance), CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
    }

    private static bool ReadBoolProperty(Type type, object instance, string name, bool defaultValue)
    {
        try
        {
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return Convert.ToBoolean(prop.GetValue(instance), CultureInfo.InvariantCulture);
        }
        catch { }
        return defaultValue;
    }

    private static void NotifyPlayerConnectionStateChanged(int profilerId)
    {
        try
        {
            Type stateType = FindType("UnityEditor.Networking.PlayerConnection.GeneralConnectionState", null);
            if (stateType == null)
                return;

            const BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo getConnectionName = stateType.GetMethod("GetConnectionName", bf);
            MethodInfo connected = stateType.GetMethod("SuccessfullyConnectedToPlayer", bf);
            if (getConnectionName == null || connected == null)
                return;

            string connectionName = getConnectionName.Invoke(null, new object[] { profilerId }) as string;
            if (string.IsNullOrEmpty(connectionName))
                connectionName = profilerId.ToString(CultureInfo.InvariantCulture);

            connected.Invoke(null, new object[] { connectionName, null });
        }
        catch { }
    }

    private static void RefreshFrameDebuggerWindowsAfterConnectionChange()
    {
        try
        {
            Type frameDebuggerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow")
                                          ?? typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerWindow");
            if (frameDebuggerWindowType == null)
                return;

            MethodInfo repaint = typeof(EditorWindow).GetMethod("Repaint", BindingFlags.Instance | BindingFlags.Public);
            var windows = Resources.FindObjectsOfTypeAll(frameDebuggerWindowType);
            foreach (var window in windows)
            {
                try { repaint?.Invoke(window, null); }
                catch { }
            }
        }
        catch { }
    }

    private static bool TryEnableOpenFrameDebuggerWindow()
    {
        try
        {
            Type frameDebuggerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow")
                                          ?? typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerWindow");
            if (frameDebuggerWindowType == null)
                return false;

            MethodInfo enable = frameDebuggerWindowType.GetMethod("EnableFrameDebugger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (enable == null)
                return false;

            var windows = Resources.FindObjectsOfTypeAll(frameDebuggerWindowType);
            foreach (var window in windows)
            {
                try
                {
                    enable.Invoke(window, null);
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static string InvokeProfilerString(MethodInfo method, int id)
    {
        if (method == null)
            return "";
        try
        {
            object value = method.Invoke(null, new object[] { id });
            return value != null ? value.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    private bool TrySetFrameDebuggerEnabled(bool enabled, int remotePlayerId = 0)
    {
        var bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (s_utilType != null)
        {
            foreach (var method in s_utilType.GetMethods(bf).Where(m => m.Name == "SetEnabled"))
            {
                var parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(bool) &&
                        parameters[1].ParameterType == typeof(int))
                    {
                        method.Invoke(null, new object[] { enabled, remotePlayerId });
                        return IsFrameDebuggerEnabled() == enabled;
                    }
                }
                catch { }
            }
        }

        if (s_enabledProp != null && s_enabledProp.CanWrite)
        {
            try
            {
                s_enabledProp.SetValue(null, enabled);
                return IsFrameDebuggerEnabled() == enabled;
            }
            catch { }
        }

        Type frameDebuggerType = FindType("UnityEditor.FrameDebugger", null);
        if (frameDebuggerType == null)
            return false;

        foreach (var methodName in new[] { "SetEnabled", "SetFrameDebuggerEnabled", "Enable", "SetEnabledLocal" })
        {
            foreach (var method in frameDebuggerType.GetMethods(bf).Where(m => m.Name == methodName))
            {
                var parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                    {
                        method.Invoke(null, new object[] { enabled });
                        return IsFrameDebuggerEnabled() == enabled;
                    }

                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(bool))
                    {
                        object second = parameters[1].ParameterType == typeof(int) ? (object)remotePlayerId : null;
                        if (second != null || !parameters[1].ParameterType.IsValueType)
                        {
                            method.Invoke(null, new[] { (object)enabled, second });
                            return IsFrameDebuggerEnabled() == enabled;
                        }
                    }
                }
                catch { }
            }
        }

        return false;
    }

    private int GetEventCount()
    {
        if (s_countProp != null)
        {
            try { return (int)s_countProp.GetValue(null); }
            catch { }
        }
        return 0;
    }

    private int GetLimit()
    {
        if (s_limitProp != null)
        {
            try { return (int)s_limitProp.GetValue(null); }
            catch { }
        }
        if (s_limitField != null)
        {
            try { return Convert.ToInt32(s_limitField.GetValue(null), CultureInfo.InvariantCulture); }
            catch { }
        }
        return 0;
    }

    private void SetLimit(int value)
    {
        if (s_limitProp != null)
        {
            try
            {
                var setter = s_limitProp.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(null, new object[] { value });
                    return;
                }
            }
            catch { }
        }
        if (s_limitField != null)
        {
            try { s_limitField.SetValue(null, value); }
            catch { }
        }
    }

    private void ChangeFrameDebuggerLimit(int value)
    {
        if (TryChangeFrameDebuggerWindowLimit(value))
            return;

        SetLimit(value);
    }

    private static bool TryChangeFrameDebuggerWindowLimit(int value)
    {
        Type frameDebuggerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow")
                                      ?? typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerWindow");
        if (frameDebuggerWindowType == null)
            return false;

        var windows = Resources.FindObjectsOfTypeAll(frameDebuggerWindowType);
        if (windows == null || windows.Length == 0)
            return false;

        MethodInfo changeLimit = FindInstanceMethod(frameDebuggerWindowType, "ChangeFrameEventLimit", typeof(int));
        MethodInfo repaintOnLimitChange = FindInstanceMethod(frameDebuggerWindowType, "RepaintOnLimitChange");

        foreach (var obj in windows)
        {
            var window = obj as EditorWindow;
            if (window == null)
                continue;

            try
            {
                if (changeLimit != null)
                    changeLimit.Invoke(window, new object[] { value });
                else
                    SetLimitStatic(value);

                if (repaintOnLimitChange != null)
                    repaintOnLimitChange.Invoke(window, null);
                window.Repaint();
                return true;
            }
            catch { }
        }

        return false;
    }

    private static MethodInfo FindInstanceMethod(Type type, string methodName, params Type[] parameterTypes)
    {
        if (type == null || string.IsNullOrEmpty(methodName))
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in type.GetMethods(flags).Where(m => m.Name == methodName))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length)
                continue;

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return method;
        }

        return null;
    }

    private static MethodInfo FindStaticFrameDebuggerMethod(string methodName, params Type[] parameterTypes)
    {
        if (s_utilType == null || string.IsNullOrEmpty(methodName))
            return null;

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in s_utilType.GetMethods(flags).Where(m => m.Name == methodName))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length)
                continue;

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return method;
        }

        return null;
    }

    private static void SetLimitStatic(int value)
    {
        if (s_limitProp != null)
        {
            try
            {
                var setter = s_limitProp.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(null, new object[] { value });
                    return;
                }
            }
            catch { }
        }

        if (s_limitField != null)
        {
            try { s_limitField.SetValue(null, value); }
            catch { }
        }
    }

    private void CaptureFrameData()
    {
        _capturedEvents = new List<FrameEventInfo>();
        _passStats = new Dictionary<string, PassStatEntry>();
        _captureTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (!IsFrameDebuggerEnabled())
        {
            _statusMessage = "Frame Debugger 未启用。请先打开 Window > Analysis > Frame Debugger 并启用。";
            return;
        }

        int count = GetEventCount();
        if (count == 0)
        {
            _statusMessage = "没有捕获到帧事件。";
            return;
        }

        // Get frame events array
        Array eventsArray = null;
        if (s_getFrameEvents != null)
        {
            try { eventsArray = s_getFrameEvents.Invoke(null, null) as Array; }
            catch { }
        }

        int originalLimit = GetLimit();
        bool showProgress = count > 100;

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (showProgress)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "读取 Frame Debugger 事件", $"正在读取事件 {i + 1}/{count}...", (float)i / count))
                    {
                        _statusMessage = $"已取消。已抓取 {_capturedEvents.Count}/{count} 个事件。";
                        break;
                    }
                }

                // Set limit to i+1 so Unity populates data for event i
                SetLimit(i + 1);

                object rawEvent = (eventsArray != null && i < eventsArray.Length) ? eventsArray.GetValue(i) : null;
                var info = ReadEventData(i, rawEvent);
                _capturedEvents.Add(info);
            }
        }
        finally
        {
            // Restore original limit
            SetLimit(originalLimit);
            if (showProgress) EditorUtility.ClearProgressBar();
        }

        BuildPassStatistics();
        _statusMessage = $"成功抓取 {_capturedEvents.Count} 个事件。";
    }

    private void BeginCaptureFrameDataAsync(Action<string> complete, Action<Exception> error)
    {
        _asyncComplete = complete;
        _asyncError = error;
        _capturedEvents = new List<FrameEventInfo>();
        _passStats = new Dictionary<string, PassStatEntry>();
        _captureTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (!IsFrameDebuggerEnabled())
            throw new InvalidOperationException("Frame Debugger 未启用。请先打开 Window > Analysis > Frame Debugger 并启用。");

        _asyncEventCount = GetEventCount();
        if (_asyncEventCount == 0)
            throw new InvalidOperationException("没有捕获到帧事件。");

        _asyncEventsArray = null;
        if (s_getFrameEvents != null)
        {
            try { _asyncEventsArray = s_getFrameEvents.Invoke(null, null) as Array; }
            catch { }
        }

        _asyncOriginalLimit = GetLimit();
        _asyncEventIndex = 0;
        _asyncCaptureRunning = true;
        EditorApplication.update -= OnAsyncCaptureUpdate;
        EditorApplication.update += OnAsyncCaptureUpdate;
        PrepareAsyncFrameEvent(_asyncEventIndex);
    }

    private void OnAsyncCaptureUpdate()
    {
        if (!_asyncCaptureRunning)
            return;

        try
        {
            if (EditorUtility.DisplayCancelableProgressBar(
                    "读取 Frame Debugger 事件",
                    "正在读取事件 " + (_asyncEventIndex + 1).ToString(CultureInfo.InvariantCulture) + "/" + _asyncEventCount.ToString(CultureInfo.InvariantCulture) + "...",
                    (float)_asyncEventIndex / Math.Max(1, _asyncEventCount)))
            {
                FinishAsyncCapture(new OperationCanceledException("已取消 Frame Debugger AI 导出。"));
                return;
            }

            object rawEvent = (_asyncEventsArray != null && _asyncEventIndex < _asyncEventsArray.Length)
                ? _asyncEventsArray.GetValue(_asyncEventIndex)
                : null;
            var info = ReadEventData(_asyncEventIndex, rawEvent);
            _capturedEvents.Add(info);

            _asyncEventIndex++;
            if (_asyncEventIndex >= _asyncEventCount)
            {
                BuildPassStatistics();
                _statusMessage = "成功抓取 " + _capturedEvents.Count.ToString(CultureInfo.InvariantCulture) + " 个事件。";
                string exportDir = ExportAiBundleToDirectory(GetDefaultAiExportRoot(), true);
                FinishAsyncCapture(null, exportDir);
                return;
            }

            PrepareAsyncFrameEvent(_asyncEventIndex);
        }
        catch (Exception ex)
        {
            FinishAsyncCapture(ex);
        }
    }

    private void PrepareAsyncFrameEvent(int eventIndex)
    {
        ChangeFrameDebuggerLimit(eventIndex + 1);
        EditorApplication.QueuePlayerLoopUpdate();
    }

    private void FinishAsyncCapture(Exception error, string exportDir = "")
    {
        _asyncCaptureRunning = false;
        EditorApplication.update -= OnAsyncCaptureUpdate;
        try { ChangeFrameDebuggerLimit(_asyncOriginalLimit); }
        catch { }
        EditorUtility.ClearProgressBar();

        var complete = _asyncComplete;
        var onError = _asyncError;
        _asyncComplete = null;
        _asyncError = null;

        try
        {
            if (error != null)
            {
                if (onError != null)
                    onError(error);
            }
            else if (complete != null)
            {
                complete(exportDir);
            }
        }
        finally
        {
            DestroyImmediate(this);
        }
    }

    private FrameEventInfo ReadEventData(int eventIndex, object rawEvent)
    {
        var info = new FrameEventInfo { index = eventIndex };

        // Read fields from FrameDebuggerEvent
        if (s_eventFields != null && rawEvent != null)
        {
            foreach (var f in s_eventFields)
            {
                try
                {
                    var val = f.GetValue(rawEvent);
                    if (val == null) continue;
                    string sval = val.ToString();
                    string lname = f.Name.ToLowerInvariant().Replace("m_", "").Replace("_", "");
                    info.rawEventFields[f.Name] = ObjectValueToString(val);
                    CaptureObjectReference(info, lname, val);
                    CaptureDetailedFrameValue(info, "event." + f.Name, lname, val, 0);

                    if (lname.Contains("type"))
                        info.typeName = sval;
                    else if (lname.Contains("gameobject") || lname.Contains("objectinstanceid"))
                    {
                        if (val is int instanceId && instanceId != 0)
                        {
                            var obj = EditorUtility.InstanceIDToObject(instanceId);
                            info.gameObjectName = obj != null ? obj.name : $"(ID:{instanceId})";
                        }
                        else if (val is GameObject go)
                            info.gameObjectName = go != null ? go.name : "";
                    }
                    else
                        info.extraFields[$"event.{f.Name}"] = sval;
                }
                catch { /* skip unreadable fields */ }
            }
        }
        if (s_eventProperties != null && rawEvent != null)
        {
            foreach (var p in s_eventProperties)
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try
                {
                    var val = p.GetValue(rawEvent, null);
                    if (val == null) continue;
                    string lname = p.Name.ToLowerInvariant().Replace("m_", "").Replace("_", "");
                    info.rawEventFields["prop." + p.Name] = ObjectValueToString(val);
                    CaptureObjectReference(info, lname, val);
                    CaptureDetailedFrameValue(info, "event.prop." + p.Name, lname, val, 0);
                    ApplyCommonFrameField(info, lname, val, val.ToString());
                }
                catch { /* skip unreadable properties */ }
            }
        }

        object eventData = ReadFrameEventDataWithLimit(info, eventIndex);
        if (eventData != null)
        {
            PopulateFromEventData(info, eventData);
            info.filledFrameEventIndex = DetectFilledFrameEventIndex(info);
        }

        // Try to get event display name
        if (s_utilType != null)
        {
            var bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (s_getFrameEventObject != null)
            {
                try
                {
                    var eventObject = s_getFrameEventObject.Invoke(null, new object[] { eventIndex });
                    if (eventObject != null)
                    {
                        info.rawEventFields["FrameDebuggerUtility.GetFrameEventObject"] = ObjectValueToString(eventObject);
                        CaptureObjectReference(info, "frameeventobject", eventObject);
                        CaptureDetailedFrameValue(info, "utility.GetFrameEventObject", "frameeventobject", eventObject, 0);
                        ApplyFrameDebuggerDirectObjectReference(info, eventObject as UnityEngine.Object);
                    }
                }
                catch { }
            }

            CaptureFrameDebuggerDirectObject(info, eventIndex);

            var getNameMethods = new[] { "GetFrameEventInfoName", "GetEventInfoName", "GetFrameEventName" };
            foreach (var methodName in getNameMethods)
            {
                var mi = s_utilType.GetMethod(methodName, bf);
                if (mi != null)
                {
                    try
                    {
                        info.eventName = mi.Invoke(null, new object[] { eventIndex })?.ToString() ?? "";
                        break;
                    }
                    catch { }
                }
            }
        }

        if (string.IsNullOrEmpty(info.eventName))
            info.eventName = !string.IsNullOrEmpty(info.typeName) ? info.typeName : $"Event #{eventIndex}";

        ResolveProjectReferences(info);

        return info;
    }

    private object ReadFrameEventDataWithLimit(FrameEventInfo info, int eventIndex)
    {
        if (info != null)
        {
            info.frameEventDataAttempted = true;
            info.requestedEventIndex = eventIndex + 1;
            info.frameEventDataMethod = s_getFrameEventData != null ? s_getFrameEventData.ToString() : "";
        }

        if (s_getFrameEventData == null)
        {
            if (info != null)
                info.frameEventDataFailureReason = "GetFrameEventData method not found.";
            return null;
        }

        int previousLimit = GetLimit();
        int targetLimit = eventIndex + 1;
        if (info != null)
        {
            info.frameEventDataLimitBefore = previousLimit;
            info.frameEventDataLimitSetTo = targetLimit;
        }

        try
        {
            if (previousLimit != targetLimit)
                ChangeFrameDebuggerLimit(targetLimit);
            string failureReason;
            object eventData = InvokeGetFrameEventData(eventIndex, out failureReason);
            if (info != null)
            {
                info.frameEventDataLimitAfter = GetLimit();
                info.frameEventDataSuccess = eventData != null;
                info.frameEventDataFailureReason = eventData != null ? "" : (string.IsNullOrEmpty(failureReason) ? "GetFrameEventData returned null or no event data argument was filled." : failureReason);
            }
            return eventData;
        }
        catch (Exception ex)
        {
            if (info != null)
            {
                info.frameEventDataLimitAfter = GetLimit();
                info.frameEventDataSuccess = false;
                info.frameEventDataFailureReason = ex.GetType().Name + ": " + ex.Message;
            }
            return null;
        }
        finally
        {
            if (previousLimit != targetLimit)
                ChangeFrameDebuggerLimit(previousLimit);
            if (info != null)
                info.frameEventDataLimitAfterRestore = GetLimit();
        }
    }

    private object InvokeGetFrameEventData(int eventIndex, out string failureReason)
    {
        failureReason = "";
        var parameters = s_getFrameEventData.GetParameters();
        if (parameters.Length == 2 && parameters[1].IsOut)
        {
            var args = new object[] { eventIndex, s_eventDataType != null ? Activator.CreateInstance(s_eventDataType, true) : null };
            var result = s_getFrameEventData.Invoke(null, args);
            if (result is bool ok && !ok)
            {
                failureReason = "GetFrameEventData returned false.";
                return null;
            }
            if (args[1] == null)
                failureReason = "GetFrameEventData out argument is null.";
            return args[1];
        }

        if (parameters.Length == 1)
        {
            object result = s_getFrameEventData.Invoke(null, new object[] { eventIndex });
            if (result == null)
                failureReason = "GetFrameEventData returned null.";
            return result;
        }

        if (parameters.Length >= 2)
        {
            var args = new object[parameters.Length];
            args[0] = eventIndex;
            for (int p = 1; p < parameters.Length; p++)
            {
                var pt = parameters[p].ParameterType;
                if (pt.IsByRef) pt = pt.GetElementType();
                args[p] = CreateFrameDebuggerArgument(pt);
            }

            var result = s_getFrameEventData.Invoke(null, args);
            if (result != null && s_eventDataType != null && s_eventDataType.IsAssignableFrom(result.GetType()))
                return result;

            bool returnedFalse = result is bool ok && !ok;
            if (returnedFalse)
                failureReason = "GetFrameEventData returned false.";
            for (int p = 1; p < args.Length; p++)
            {
                if (args[p] != null && s_eventDataType != null && s_eventDataType.IsAssignableFrom(args[p].GetType()))
                    return returnedFalse ? null : args[p];
            }
            if (string.IsNullOrEmpty(failureReason))
                failureReason = "No FrameDebuggerEventData-compatible return value or argument was filled.";
        }
        else
        {
            failureReason = "Unsupported GetFrameEventData signature.";
        }

        return null;
    }

    private static int DetectFilledFrameEventIndex(FrameEventInfo info)
    {
        if (info == null || info.rawDataFields == null || info.rawDataFields.Count == 0)
            return -1;

        foreach (var kv in info.rawDataFields)
        {
            string key = NormalizeFieldName(kv.Key);
            if (key == "eventindex" || key == "frameeventindex" || key == "frameeventid" || key == "index")
            {
                int value;
                if (int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return value;
            }
        }

        return -1;
    }

    private static object CreateFrameDebuggerArgument(Type type)
    {
        if (type == null) return null;
        if (s_eventDataType != null && s_eventDataType.IsAssignableFrom(type))
            return Activator.CreateInstance(type, true);
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        return null;
    }

    private static void CaptureFrameDebuggerDirectObject(FrameEventInfo info, int eventIndex)
    {
        if (info == null) return;

        UnityEngine.Object gameObjectObj = InvokeFrameDebuggerObjectMethod(s_getFrameEventGameObject, eventIndex);
        if (gameObjectObj != null)
        {
            info.rawEventFields["FrameDebuggerUtility.GetFrameEventGameObject"] = ObjectValueToString(gameObjectObj);
            CaptureObjectReference(info, "frameeventgameobject", gameObjectObj);
            ApplyFrameDebuggerDirectObjectReference(info, gameObjectObj);
        }

        UnityEngine.Object rendererObj = InvokeFrameDebuggerObjectMethod(s_getFrameEventRenderer, eventIndex);
        if (rendererObj != null)
        {
            info.rawEventFields["FrameDebuggerUtility.GetFrameEventRenderer"] = ObjectValueToString(rendererObj);
            CaptureObjectReference(info, "frameeventrenderer", rendererObj);
            ApplyFrameDebuggerDirectObjectReference(info, rendererObj);
        }
    }

    private static void ApplyFrameDebuggerDirectObjectReference(FrameEventInfo info, UnityEngine.Object obj)
    {
        if (info == null || obj == null) return;

        var go = obj as GameObject;
        if (go == null && obj is Component component)
            go = component.gameObject;
        if (go != null)
        {
            info.frameDebuggerGameObjectName = go.name;
            info.frameDebuggerGameObjectPath = GetHierarchyPath(go);
            info.frameDebuggerGameObjectInstanceId = go.GetInstanceID();
        }

        var renderer = obj as Renderer;
        if (renderer == null && go != null)
            renderer = go.GetComponent<Renderer>();
        if (renderer == null && obj is Component rendererComponent)
            renderer = rendererComponent.GetComponent<Renderer>();
        if (renderer != null)
        {
            info.frameDebuggerRendererType = renderer.GetType().Name;
            info.frameDebuggerRendererPath = GetHierarchyPath(renderer.gameObject);
            info.frameDebuggerRendererInstanceId = renderer.GetInstanceID();
            ApplyRendererReference(info, renderer);
        }
    }

    private static UnityEngine.Object InvokeFrameDebuggerObjectMethod(MethodInfo method, int eventIndex)
    {
        if (method == null) return null;
        try
        {
            return method.Invoke(null, new object[] { eventIndex }) as UnityEngine.Object;
        }
        catch
        {
            return null;
        }
    }

    private void PopulateFromEventData(FrameEventInfo info, object eventData)
    {
        if (s_eventDataFields != null)
        {
            foreach (var f in s_eventDataFields)
            {
                try
                {
                    var val = f.GetValue(eventData);
                    if (val == null) continue;

                    string sval = val.ToString();
                    string lname = f.Name.ToLowerInvariant().Replace("m_", "").Replace("_", "");
                    info.rawDataFields[f.Name] = ObjectValueToString(val);
                    CaptureObjectReference(info, lname, val);
                    CaptureDetailedFrameValue(info, "data." + f.Name, lname, val, 0);

                    if (!ApplyCommonFrameField(info, lname, val, sval))
                    {
                        if (lname.Contains("gameobject") || lname.Contains("componentinstanceid"))
                        {
                            if (val is int iid && iid != 0 && string.IsNullOrEmpty(info.gameObjectName))
                            {
                                var obj = EditorUtility.InstanceIDToObject(iid);
                                if (obj != null) info.gameObjectName = obj.name;
                            }
                        }
                        else if (sval != "0" && sval != "" && sval != "False" && sval != "-1")
                        {
                            info.extraFields[$"data.{f.Name}"] = sval;
                        }
                    }
                }
                catch { /* skip */ }
            }
        }

        if (s_eventDataProperties != null)
        {
            foreach (var p in s_eventDataProperties)
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try
                {
                    var val = p.GetValue(eventData, null);
                    if (val == null) continue;

                    string sval = val.ToString();
                    string lname = p.Name.ToLowerInvariant().Replace("m_", "").Replace("_", "");
                    info.rawDataFields["prop." + p.Name] = ObjectValueToString(val);
                    CaptureObjectReference(info, lname, val);
                    CaptureDetailedFrameValue(info, "data.prop." + p.Name, lname, val, 0);
                    ApplyCommonFrameField(info, lname, val, sval);
                }
                catch { /* skip */ }
            }
        }
    }

    private static bool ApplyCommonFrameField(FrameEventInfo info, string lname, object val, string sval)
    {
        if (info == null || string.IsNullOrEmpty(lname)) return false;

        if ((lname.Contains("shader") && lname.Contains("name")) || lname == "shadername")
            info.shaderName = sval;
        else if ((lname.Contains("pass") && lname.Contains("name")) || lname == "passname")
            info.passName = sval;
        else if (lname.Contains("lightmode") || lname.Contains("passlightmode"))
            info.passLightMode = sval;
        else if (lname.Contains("passindex") || lname == "shaderpassindex")
            info.passIndex = TryInt(val);
        else if ((lname.Contains("rt") && lname.Contains("name")) ||
                 lname.Contains("rendertargetname") || lname == "rtname")
            info.renderTargetName = sval;
        else if (lname.Contains("rtwidth") || lname.Contains("rendertargetwidth"))
            info.renderTargetWidth = TryInt(val);
        else if (lname.Contains("rtheight") || lname.Contains("rendertargetheight"))
            info.renderTargetHeight = TryInt(val);
        else if (lname.Contains("rtformat") || lname.Contains("rendertargetformat"))
            info.renderTargetFormat = TryInt(val);
        else if (lname.Contains("vertex") && lname.Contains("count"))
            info.vertexCount = TryInt(val);
        else if (lname.Contains("index") && lname.Contains("count"))
            info.indexCount = TryInt(val);
        else if (lname.Contains("instance") && lname.Contains("count"))
            info.instanceCount = TryInt(val);
        else if (lname.Contains("drawcall"))
            info.drawCallCount = TryInt(val);
        else if (lname.Contains("keyword"))
            info.shaderKeywords = sval;
        else if (lname.Contains("batchbreak") || lname.Contains("batchingbreak"))
            info.batchBreakCause = FormatBatchBreakCause(val, sval);
        else if (lname.Contains("meshsubset") || lname.Contains("submesh"))
            info.meshSubset = TryInt(val);
        else if (lname.Contains("type") && string.IsNullOrEmpty(info.typeName))
            info.typeName = sval;
        else
            return false;

        return true;
    }

    private static void CaptureObjectReference(FrameEventInfo info, string normalizedFieldName, object val)
    {
        if (info == null || val == null) return;

        UnityEngine.Object obj = val as UnityEngine.Object;
        if (obj == null && val is int iid && iid != 0 && LooksLikeObjectReferenceField(normalizedFieldName))
            obj = EditorUtility.InstanceIDToObject(iid);

        if (obj == null) return;

        if (info.objectInstanceId == 0)
            info.objectInstanceId = obj.GetInstanceID();
        if (string.IsNullOrEmpty(info.objectType))
            info.objectType = obj.GetType().Name;

        var go = obj as GameObject;
        if (go == null && obj is Component component)
            go = component.gameObject;
        if (go != null && (normalizedFieldName.Contains("gameobject") ||
                           normalizedFieldName.Contains("object") ||
                           normalizedFieldName.Contains("component") ||
                           string.IsNullOrEmpty(info.gameObjectName)))
        {
            info.gameObjectName = go.name;
            info.gameObjectPath = GetHierarchyPath(go);
        }

        var renderer = obj as Renderer;
        if (renderer == null && go != null)
            renderer = go.GetComponent<Renderer>();
        if (renderer != null)
            ApplyRendererReference(info, renderer);

        if (obj is Mesh mesh)
            ApplyMeshReference(info, mesh);

        if (obj is Material material)
            ApplyMaterialReference(info, material);

        if (obj is Shader shader)
            ApplyShaderReference(info, shader);
    }

    private static void CaptureDetailedFrameValue(FrameEventInfo info, string sourceName, string normalizedFieldName, object val, int depth)
    {
        if (info == null || val == null || depth > 2) return;
        bool frameDebuggerDataSource = IsFrameDebuggerEventDataSource(sourceName);

        if (val is string sval)
        {
            if (normalizedFieldName.Contains("batch") && normalizedFieldName.Contains("cause") && string.IsNullOrEmpty(info.batchBreakCause))
                info.batchBreakCause = sval;
            if (normalizedFieldName.Contains("lightmode") && string.IsNullOrEmpty(info.passLightMode))
                info.passLightMode = sval;
            return;
        }

        if (val is UnityEngine.Object unityObject)
        {
            if (frameDebuggerDataSource && unityObject is Mesh mesh)
            {
                AddDetailMeshReference(info, mesh, sourceName);
                return;
            }
            CaptureObjectReference(info, normalizedFieldName, unityObject);
            return;
        }

        if (val is int iid && iid != 0)
        {
            if (normalizedFieldName.Contains("batch") && normalizedFieldName.Contains("cause"))
                info.batchBreakCause = FormatBatchBreakCause(val, iid.ToString(CultureInfo.InvariantCulture));

            UnityEngine.Object obj = null;
            try { obj = EditorUtility.InstanceIDToObject(iid); }
            catch { }
            if (obj != null)
            {
                if (frameDebuggerDataSource && obj is Mesh mesh)
                {
                    AddDetailMeshReference(info, mesh, sourceName);
                }
                else if (frameDebuggerDataSource && normalizedFieldName.Contains("mesh"))
                {
                    AddUnique(info.detailMeshInstanceIds, iid.ToString(CultureInfo.InvariantCulture));
                    CaptureObjectReference(info, normalizedFieldName, obj);
                }
                else
                {
                    CaptureObjectReference(info, normalizedFieldName, obj);
                }
            }
            else if (frameDebuggerDataSource && normalizedFieldName.Contains("mesh"))
            {
                AddUnique(info.detailMeshInstanceIds, iid.ToString(CultureInfo.InvariantCulture));
            }
            return;
        }

        if (val is IEnumerable enumerable && !(val is string))
        {
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= 64) break;
                CaptureDetailedFrameValue(info, sourceName + "[" + (count - 1).ToString(CultureInfo.InvariantCulture) + "]", normalizedFieldName, item, depth + 1);
            }
            return;
        }

        Type type = val.GetType();
        if (depth >= 2 || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
            return;

        string ns = type.Namespace ?? "";
        if (!ns.StartsWith("UnityEditor", StringComparison.Ordinal) && !ns.StartsWith("UnityEngine", StringComparison.Ordinal))
            return;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in type.GetFields(flags))
        {
            if (field.IsStatic) continue;
            object child = null;
            try { child = field.GetValue(val); }
            catch { }
            if (child != null)
                CaptureDetailedFrameValue(info, sourceName + "." + field.Name, NormalizeFieldName(field.Name), child, depth + 1);
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object child = null;
            try { child = prop.GetValue(val, null); }
            catch { }
            if (child != null)
                CaptureDetailedFrameValue(info, sourceName + ".prop." + prop.Name, NormalizeFieldName(prop.Name), child, depth + 1);
        }
    }

    private static bool IsFrameDebuggerEventDataSource(string sourceName)
    {
        return !string.IsNullOrEmpty(sourceName) &&
               (sourceName.StartsWith("data.", StringComparison.Ordinal) ||
                sourceName.StartsWith("data[", StringComparison.Ordinal));
    }

    private static string NormalizeFieldName(string name)
    {
        return (name ?? "").ToLowerInvariant().Replace("m_", "").Replace("_", "");
    }

    private static string FormatBatchBreakCause(object value, string fallback)
    {
        int index = TryInt(value);
        if (index >= 0 && s_batchBreakCauseStrings != null && index < s_batchBreakCauseStrings.Length)
        {
            string cause = s_batchBreakCauseStrings[index];
            if (!string.IsNullOrEmpty(cause))
                return cause;
        }
        return fallback ?? "";
    }

    private static void AddDetailMeshReference(FrameEventInfo info, Mesh mesh, string sourceName)
    {
        if (info == null || mesh == null) return;
        AddUnique(info.detailMeshNames, mesh.name);
        string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(mesh));
        if (!string.IsNullOrEmpty(path))
            AddUnique(info.detailMeshAssetPaths, path);
        AddUnique(info.detailMeshInstanceIds, mesh.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(sourceName))
            info.extraFields["detailMeshSource." + sourceName] = mesh.name;
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (list == null || string.IsNullOrEmpty(value)) return;
        if (!list.Contains(value))
            list.Add(value);
    }

    private static bool LooksLikeObjectReferenceField(string normalizedFieldName)
    {
        if (string.IsNullOrEmpty(normalizedFieldName)) return false;
        return normalizedFieldName.Contains("instanceid") ||
               normalizedFieldName.Contains("object") ||
               normalizedFieldName.Contains("gameobject") ||
               normalizedFieldName.Contains("component") ||
               normalizedFieldName.Contains("renderer") ||
               normalizedFieldName.Contains("material") ||
               normalizedFieldName.Contains("mesh") ||
               normalizedFieldName.Contains("shader");
    }

    private static void ResolveProjectReferences(FrameEventInfo info)
    {
        if (info == null) return;

        UnityEngine.Object obj = null;
        if (info.objectInstanceId != 0)
            obj = EditorUtility.InstanceIDToObject(info.objectInstanceId);

        GameObject go = obj as GameObject;
        if (go == null && obj is Component component)
            go = component.gameObject;

        if (go == null && !string.IsNullOrEmpty(info.gameObjectName))
        {
            var found = GameObject.Find(info.gameObjectName);
            if (found != null)
                go = found;
        }

        if (go == null) return;

        if (string.IsNullOrEmpty(info.gameObjectName))
            info.gameObjectName = go.name;
        if (string.IsNullOrEmpty(info.gameObjectPath))
            info.gameObjectPath = GetHierarchyPath(go);

        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
            ApplyRendererReference(info, renderer);
    }

    private static void ApplyRendererReference(FrameEventInfo info, Renderer renderer)
    {
        if (info == null || renderer == null) return;

        info.rendererType = renderer.GetType().Name;
        info.rendererSortingLayerId = renderer.sortingLayerID;
        info.rendererSortingOrder = renderer.sortingOrder;
        info.rendererPriority = TryGetRendererPriority(renderer);
        if (string.IsNullOrEmpty(info.gameObjectName))
            info.gameObjectName = renderer.gameObject.name;
        if (string.IsNullOrEmpty(info.gameObjectPath))
            info.gameObjectPath = GetHierarchyPath(renderer.gameObject);

        var mesh = GetRendererMesh(renderer);
        if (mesh != null)
            ApplyMeshReference(info, mesh);

        var materials = renderer.sharedMaterials;
        if (materials != null && materials.Length > 0)
        {
            info.rendererMaterials = BuildRendererMaterialsSummary(materials);
            Material material = null;
            if (info.meshSubset >= 0 && info.meshSubset < materials.Length)
                material = materials[info.meshSubset];
            else if (materials.Length == 1)
                material = materials[0];
            else
                material = materials.FirstOrDefault(m => m != null);

            if (material != null)
                ApplyMaterialReference(info, material);
        }
    }

    private static int TryGetRendererPriority(Renderer renderer)
    {
        if (renderer == null) return 0;
        try
        {
            var prop = typeof(Renderer).GetProperty("rendererPriority", BindingFlags.Instance | BindingFlags.Public);
            return prop != null ? Convert.ToInt32(prop.GetValue(renderer), CultureInfo.InvariantCulture) : 0;
        }
        catch { return 0; }
    }

    private static string BuildRendererMaterialsSummary(Material[] materials)
    {
        if (materials == null || materials.Length == 0) return "";

        var items = new List<string>();
        for (int i = 0; i < materials.Length; i++)
        {
            var mat = materials[i];
            if (mat == null)
            {
                items.Add(i + ":<null>");
                continue;
            }
            string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(mat));
            string shaderName = mat.shader != null ? mat.shader.name : "";
            items.Add(i + ":" + mat.name + (string.IsNullOrEmpty(shaderName) ? "" : "[" + shaderName + "]") +
                      (string.IsNullOrEmpty(path) ? "" : "@" + path));
        }
        return string.Join("; ", items);
    }

    private static Mesh GetRendererMesh(Renderer renderer)
    {
        if (renderer == null) return null;

        var skinned = renderer as SkinnedMeshRenderer;
        if (skinned != null)
            return skinned.sharedMesh;

        var meshFilter = renderer.GetComponent<MeshFilter>();
        return meshFilter != null ? meshFilter.sharedMesh : null;
    }

    private static void ApplyMeshReference(FrameEventInfo info, Mesh mesh)
    {
        if (info == null || mesh == null) return;

        info.meshName = mesh.name;
        info.meshAssetPath = ToProjectRelativePath(AssetDatabase.GetAssetPath(mesh));
        info.meshGuid = !string.IsNullOrEmpty(info.meshAssetPath) ? AssetDatabase.AssetPathToGUID(info.meshAssetPath) : "";
        if (info.vertexCount <= 0)
            info.vertexCount = mesh.vertexCount;
        if (info.indexCount <= 0 && info.meshSubset >= 0 && info.meshSubset < mesh.subMeshCount)
            info.indexCount = (int)mesh.GetIndexCount(info.meshSubset);
    }

    private static void ApplyMaterialReference(FrameEventInfo info, Material material)
    {
        if (info == null || material == null) return;

        info.materialName = material.name;
        info.materialAssetPath = ToProjectRelativePath(AssetDatabase.GetAssetPath(material));
        info.materialGuid = !string.IsNullOrEmpty(info.materialAssetPath) ? AssetDatabase.AssetPathToGUID(info.materialAssetPath) : "";
        info.materialRenderQueue = material.renderQueue;
        info.materialInstancingEnabled = material.enableInstancing;
        info.materialKeywords = material.shaderKeywords != null ? string.Join(" ", material.shaderKeywords) : "";
        info.materialTextures = BuildMaterialTextureSummary(material, 12);
        info.materialProperties = BuildMaterialPropertySummary(material, 160);

        if (material.shader != null)
            ApplyShaderReference(info, material.shader);
    }

    private static void ApplyShaderReference(FrameEventInfo info, Shader shader)
    {
        if (info == null || shader == null) return;

        info.resolvedShaderName = shader.name;
        if (string.IsNullOrEmpty(info.shaderName))
            info.shaderName = shader.name;
        info.resolvedShaderAssetPath = ToProjectRelativePath(AssetDatabase.GetAssetPath(shader));
        info.resolvedShaderGuid = !string.IsNullOrEmpty(info.resolvedShaderAssetPath) ? AssetDatabase.AssetPathToGUID(info.resolvedShaderAssetPath) : "";
        info.resolvedShaderRenderQueue = shader.renderQueue;
        try
        {
            var passCountProp = typeof(Shader).GetProperty("passCount", BindingFlags.Instance | BindingFlags.Public);
            info.resolvedShaderPassCount = passCountProp != null ? Convert.ToInt32(passCountProp.GetValue(shader), CultureInfo.InvariantCulture) : -1;
        }
        catch { info.resolvedShaderPassCount = -1; }
        info.resolvedShaderPassSummary = BuildShaderPassSummary(shader);
    }

    private static string BuildMaterialTextureSummary(Material material, int maxItems)
    {
        if (material == null || material.shader == null) return "";

        var items = new List<string>();
        try
        {
            foreach (var propName in material.GetTexturePropertyNames())
            {
                if (items.Count >= maxItems) break;
                var tex = material.GetTexture(propName);
                if (tex == null) continue;

                string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(tex));
                items.Add(propName + "=" + tex.name + (string.IsNullOrEmpty(path) ? "" : "@" + path));
            }
        }
        catch { }

        return string.Join("; ", items);
    }

    private static string BuildMaterialPropertySummary(Material material, int maxItems)
    {
        if (material == null || material.shader == null) return "";

        var items = new List<string>();
        try
        {
            var shaderUtil = typeof(Editor).Assembly.GetType("UnityEditor.ShaderUtil");
            if (shaderUtil == null) return "";

            var getPropertyCount = shaderUtil.GetMethod("GetPropertyCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getPropertyName = shaderUtil.GetMethod("GetPropertyName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getPropertyType = shaderUtil.GetMethod("GetPropertyType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (getPropertyCount == null || getPropertyName == null || getPropertyType == null) return "";

            int count = Convert.ToInt32(getPropertyCount.Invoke(null, new object[] { material.shader }), CultureInfo.InvariantCulture);
            for (int i = 0; i < count && items.Count < maxItems; i++)
            {
                string propName = getPropertyName.Invoke(null, new object[] { material.shader, i }) as string;
                if (string.IsNullOrEmpty(propName) || !material.HasProperty(propName)) continue;

                string propType = Convert.ToString(getPropertyType.Invoke(null, new object[] { material.shader, i }), CultureInfo.InvariantCulture);
                string value = "";
                try
                {
                    if (propType.Contains("TexEnv"))
                    {
                        var tex = material.GetTexture(propName);
                        value = tex == null ? "<null>" : tex.name + "@" + ToProjectRelativePath(AssetDatabase.GetAssetPath(tex));
                    }
                    else if (propType.Contains("Color"))
                        value = ColorToString(material.GetColor(propName));
                    else if (propType.Contains("Vector"))
                        value = VectorToString(material.GetVector(propName));
                    else
                        value = material.GetFloat(propName).ToString("0.###", CultureInfo.InvariantCulture);
                }
                catch
                {
                    value = "<unreadable>";
                }

                items.Add(propName + ":" + propType + "=" + value);
            }
        }
        catch { }

        return string.Join("; ", items);
    }

    private static string BuildShaderPassSummary(Shader shader)
    {
        if (shader == null) return "";

        string path = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return "";

        try
        {
            var summaries = new List<string>();
            var lines = File.ReadAllLines(path);
            bool inPass = false;
            int depth = 0;
            var passLines = new List<string>();

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!inPass && line.StartsWith("Pass", StringComparison.Ordinal))
                {
                    inPass = true;
                    depth = 0;
                    passLines.Clear();
                }

                if (inPass)
                {
                    passLines.Add(line);
                    depth += CountChar(rawLine, '{');
                    depth -= CountChar(rawLine, '}');
                    if (depth <= 0 && passLines.Any(l => l.Contains("{")))
                    {
                        summaries.Add(SummarizeShaderPass(passLines));
                        inPass = false;
                    }
                }
            }

            return string.Join(" | ", summaries.Where(s => !string.IsNullOrEmpty(s)).Take(24));
        }
        catch
        {
            return "";
        }
    }

    private static string SummarizeShaderPass(List<string> lines)
    {
        if (lines == null || lines.Count == 0) return "";

        string name = ExtractShaderDirective(lines, "Name");
        string tags = lines.FirstOrDefault(l => l.StartsWith("Tags", StringComparison.Ordinal)) ?? "";
        string lightMode = ExtractTagValue(tags, "LightMode");
        string cull = ExtractStateLine(lines, "Cull");
        string blend = ExtractStateLine(lines, "Blend");
        string zwrite = ExtractStateLine(lines, "ZWrite");
        string ztest = ExtractStateLine(lines, "ZTest");
        string colorMask = ExtractStateLine(lines, "ColorMask");
        string stencil = lines.Any(l => l.StartsWith("Stencil", StringComparison.Ordinal)) ? "Stencil" : "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(name)) parts.Add("Name=" + name);
        if (!string.IsNullOrEmpty(lightMode)) parts.Add("LightMode=" + lightMode);
        if (!string.IsNullOrEmpty(cull)) parts.Add(cull);
        if (!string.IsNullOrEmpty(blend)) parts.Add(blend);
        if (!string.IsNullOrEmpty(zwrite)) parts.Add(zwrite);
        if (!string.IsNullOrEmpty(ztest)) parts.Add(ztest);
        if (!string.IsNullOrEmpty(colorMask)) parts.Add(colorMask);
        if (!string.IsNullOrEmpty(stencil)) parts.Add(stencil);
        return string.Join(",", parts);
    }

    private static string ExtractShaderDirective(List<string> lines, string directive)
    {
        string line = lines.FirstOrDefault(l => l.StartsWith(directive + " ", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(line)) return "";
        int firstQuote = line.IndexOf('"');
        int lastQuote = line.LastIndexOf('"');
        if (firstQuote >= 0 && lastQuote > firstQuote)
            return line.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
        return line.Substring(directive.Length).Trim();
    }

    private static string ExtractStateLine(List<string> lines, string directive)
    {
        string line = lines.FirstOrDefault(l => l.StartsWith(directive + " ", StringComparison.Ordinal));
        return string.IsNullOrEmpty(line) ? "" : line;
    }

    private static string ExtractTagValue(string tagsLine, string key)
    {
        if (string.IsNullOrEmpty(tagsLine) || string.IsNullOrEmpty(key)) return "";
        string pattern = "\"" + key + "\"";
        int keyIndex = tagsLine.IndexOf(pattern, StringComparison.Ordinal);
        if (keyIndex < 0) return "";
        int equalsIndex = tagsLine.IndexOf('=', keyIndex);
        if (equalsIndex < 0) return "";
        int firstQuote = tagsLine.IndexOf('"', equalsIndex);
        if (firstQuote < 0) return "";
        int secondQuote = tagsLine.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return "";
        return tagsLine.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    private static int CountChar(string value, char target)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        int count = 0;
        for (int i = 0; i < value.Length; i++)
            if (value[i] == target) count++;
        return count;
    }

    private static string ColorToString(Color color)
    {
        return "(" + color.r.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               color.g.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               color.b.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               color.a.ToString("0.###", CultureInfo.InvariantCulture) + ")";
    }

    private static string VectorToString(Vector4 value)
    {
        return "(" + value.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               value.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               value.z.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               value.w.ToString("0.###", CultureInfo.InvariantCulture) + ")";
    }

    private static string GetHierarchyPath(GameObject go)
    {
        if (go == null) return "";

        var names = new List<string>();
        var t = go.transform;
        while (t != null)
        {
            names.Add(t.name);
            t = t.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string ObjectValueToString(object val)
    {
        if (val == null) return "";

        var obj = val as UnityEngine.Object;
        if (obj != null)
        {
            string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(obj));
            string suffix = string.IsNullOrEmpty(path) ? "" : " @ " + path;
            return obj.name + " (" + obj.GetType().Name + ", id=" + obj.GetInstanceID() + ")" + suffix;
        }

        if (val is IEnumerable enumerable && !(val is string))
        {
            var parts = new List<string>();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= 16)
                {
                    parts.Add("...");
                    break;
                }
                parts.Add(ObjectValueToString(item));
            }
            return "[" + string.Join(", ", parts) + "]";
        }

        return val.ToString();
    }

    private static int TryInt(object val)
    {
        if (val is int i) return i;
        if (val is short s) return s;
        if (val is uint u) return (int)u;
        if (val is long l) return (int)l;
        if (int.TryParse(val?.ToString() ?? "", out int parsed)) return parsed;
        return 0;
    }

    private void BuildPassStatistics()
    {
        _passStats = new Dictionary<string, PassStatEntry>();
        if (_capturedEvents == null) return;

        foreach (var evt in _capturedEvents)
        {
            string key = !string.IsNullOrEmpty(evt.passName) ? evt.passName :
                         !string.IsNullOrEmpty(evt.passLightMode) ? evt.passLightMode :
                         !string.IsNullOrEmpty(evt.typeName) ? evt.typeName : "(Unknown)";

            if (!_passStats.TryGetValue(key, out var stat))
            {
                stat = new PassStatEntry { passName = key };
                _passStats[key] = stat;
            }

            stat.drawCallCount++;
            stat.totalVertices += evt.vertexCount;
            stat.totalIndices += evt.indexCount;
            if (!string.IsNullOrEmpty(evt.shaderName))
                stat.shaderNames.Add(evt.shaderName);
        }
    }

    #endregion

    #region Markdown Export

    private void ExportToMarkdown()
    {
        if (_capturedEvents == null || _capturedEvents.Count == 0)
        {
            EditorUtility.DisplayDialog("导出失败", "没有可导出的 Frame Debugger 事件。", "确定");
            return;
        }

        try
        {
            string dir = string.IsNullOrEmpty(_exportDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : _exportDir;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            string fileName = $"FrameDebug_{DateTime.Now:yyyyMMdd_HHmmss}.md";
            string filePath = Path.Combine(dir, fileName);

            string content = BuildMarkdownContent();
            File.WriteAllText(filePath, content, Encoding.UTF8);

            _lastExportPath = filePath;
            _statusMessage = $"已导出到: {filePath}";
            EditorUtility.RevealInFinder(filePath);
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("导出失败", ex.Message, "确定");
        }
    }

    private void ExportAiBundle()
    {
        if (_capturedEvents == null || _capturedEvents.Count == 0)
        {
            EditorUtility.DisplayDialog("导出失败", "没有可导出的 Frame Debugger 事件。", "确定");
            return;
        }

        try
        {
            string root = string.IsNullOrEmpty(_exportDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : _exportDir;
            ExportAiBundleToDirectory(root, true);
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("AI 导出失败", ex.Message, "确定");
        }
    }

    private string ExportAiBundleToDirectory(string root, bool revealInFinder)
    {
        if (_capturedEvents == null || _capturedEvents.Count == 0)
            throw new InvalidOperationException("没有可导出的 Frame Debugger 数据。");

        if (string.IsNullOrEmpty(root))
            root = GetDefaultAiExportRoot();
        if (!Directory.Exists(root))
            Directory.CreateDirectory(root);

        string previousExportDir = FindLatestPreviousAiExport(root);
        string dir = Path.Combine(root, $"FrameDebugAI_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        string snapshotsDir = Path.Combine(dir, "snapshots");
        string dictionariesDir = Path.Combine(dir, "dictionaries");
        string humanDir = Path.Combine(dir, "human");
        string debugDir = Path.Combine(dir, "debug");
        Directory.CreateDirectory(snapshotsDir);
        Directory.CreateDirectory(dictionariesDir);
        Directory.CreateDirectory(humanDir);
        Directory.CreateDirectory(debugDir);
        _activeExportDir = dir;
        _exportTimings.Clear();
        string runtimeSnapshotForCache = !string.IsNullOrWhiteSpace(_runtimePlayerSnapshotJson)
            ? _runtimePlayerSnapshotJson
            : (_linkedCaptureContext != null ? BuildRuntimePlayerSnapshotJson() : "");
        s_exportCache = new ExportBuildCache
        {
            events = _capturedEvents,
            runtimeSnapshotJson = runtimeSnapshotForCache,
            renderDocAnalysisDirectory = _linkedCaptureContext != null ? _linkedCaptureContext.renderDocAnalysisDirectory : ""
        };

        int step = 0;
        int totalSteps = (ExportFullRendererSnapshotByDefault ? 37 : 36) + (_linkedCaptureContext != null ? 4 : 0);
        try
        {
            WriteExportFileWithProgress(dir, "AI_ANALYSIS_GUIDE.md", BuildAiGuideContent, ref step, totalSteps);
            if (_linkedCaptureContext != null)
            {
                _linkedCaptureContext.runtimeSnapshotPath = "snapshots/runtime_player_snapshot.json";
                WriteExportFileWithProgress(dir, "linked_capture.json", BuildLinkedCaptureJson, ref step, totalSteps);
                WriteExportFileWithProgress(dir, "AI_LINKED_ANALYSIS_GUIDE.md", BuildLinkedAnalysisGuideContent, ref step, totalSteps);
            }
            WriteExportFileWithProgress(dir, "ai_summary.json", BuildAiDigestJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_data_quality.json", BuildAiDataQualityJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_analysis_blocking_policy.json", BuildAiAnalysisBlockingPolicyJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_data_quality.md", BuildAiDataQualityMarkdown, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_runtime_resolution_snapshot.json", BuildAiRuntimeResolutionSnapshotJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_transparent_submission_snapshot.json", BuildAiTransparentSubmissionSnapshotJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_events_raw.jsonl", BuildAiEventsRawJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_frameeventdata_diagnostics.json", BuildAiFrameEventDataDiagnosticsJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_direct_object_diagnostics.json", BuildAiDirectObjectDiagnosticsJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_event_evidence.jsonl", BuildAiEventEvidenceJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_event_analysis.jsonl", BuildAiEventAnalysisJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_object_inventory.json", BuildAiObjectInventoryJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_event_attribution_index.jsonl", BuildAiEventAttributionIndexJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_ui_batches.jsonl", BuildAiUiBatchesJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_ui_batch_details.jsonl", BuildAiUiBatchDetailsJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_srp_batch_candidates.jsonl", BuildAiSrpBatchCandidatesJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_srp_batch_diagnostics.json", BuildAiSrpBatchDiagnosticsJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_resource_fingerprints.json", BuildAiResourceFingerprintsJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_unity_renderdoc_correlation_seed.json", BuildAiUnityRenderDocCorrelationSeedJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_deep_event_sampling_plan.json", BuildAiDeepEventSamplingPlanJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_reflection_inventory.json", BuildAiReflectionInventoryJson, ref step, totalSteps);
            WriteExportFileWithProgress(snapshotsDir, "ui_graphics.jsonl", BuildAiUiGraphicsJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(snapshotsDir, "particles.jsonl", BuildAiParticlesJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(snapshotsDir, "renderers_compact.jsonl", BuildAiRenderersCompactJsonl, ref step, totalSteps);
            if (ExportFullRendererSnapshotByDefault)
                WriteExportFileWithProgress(snapshotsDir, "renderers.jsonl", BuildAiRenderersJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(snapshotsDir, "cameras.json", BuildAiCamerasJson, ref step, totalSteps);
            WriteExportFileWithProgress(snapshotsDir, "renderfeatures.json", BuildAiRenderFeaturesJson, ref step, totalSteps);
            if (_linkedCaptureContext != null)
            {
                WriteExportFileWithProgress(snapshotsDir, "runtime_player_snapshot.json", BuildRuntimePlayerSnapshotJson, ref step, totalSteps);
                WriteRuntimeTextureThumbnailFiles(Path.Combine(snapshotsDir, "runtime_texture_thumbnails"));
                WriteExportFileWithProgress(snapshotsDir, "runtime_texture_thumbnails.json", BuildRuntimeTextureThumbnailsIndexJson, ref step, totalSteps);
            }
            WriteExportFileWithProgress(dictionariesDir, "strings.json", BuildAiStringDictionaryJson, ref step, totalSteps);
            WriteExportFileWithProgress(dictionariesDir, "materials.json", BuildAiMaterialDictionaryJson, ref step, totalSteps);
            WriteExportFileWithProgress(dictionariesDir, "shaders.json", BuildAiShaderDictionaryJson, ref step, totalSteps);
            WriteExportFileWithProgress(dictionariesDir, "textures.json", BuildAiTextureDictionaryJson, ref step, totalSteps);
            WriteExportFileWithProgress(humanDir, "FrameDebug.md", BuildMarkdownContent, ref step, totalSteps);
            WriteExportFileWithProgress(debugDir, "ai_events_raw_full.jsonl", BuildAiEventsRawFullJsonl, ref step, totalSteps);
            WriteExportFileWithProgress(debugDir, "ai_direct_object_diagnostics_full.json", BuildAiDirectObjectDiagnosticsFullJson, ref step, totalSteps);
            WriteExportFileWithProgress(dir, "ai_export_timings.json", BuildAiExportTimingsJson, ref step, totalSteps);
            WriteBaselineComparisonWithProgress(dir, previousExportDir, ref step, totalSteps);
        }
        finally
        {
            s_exportCache = null;
            _activeExportDir = "";
            EditorUtility.ClearProgressBar();
        }

        _lastExportPath = dir;
        _statusMessage = $"AI 导出完成: {dir}";
        if (revealInFinder)
            EditorUtility.RevealInFinder(dir);

        return dir;
    }

    private void WriteExportFileWithProgress(string directory, string fileName, Func<string> buildContent, ref int step, int totalSteps)
    {
        step++;
        EditorUtility.DisplayProgressBar(
            "Frame Debugger AI Export",
            "正在生成 " + fileName + " (" + step.ToString(CultureInfo.InvariantCulture) + "/" + totalSteps.ToString(CultureInfo.InvariantCulture) + ")",
            Mathf.Clamp01((float)(step - 1) / Math.Max(1, totalSteps)));
        string path = Path.Combine(directory, fileName);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string content = buildContent != null ? buildContent() : "";
        WriteUtf8NoBom(path, content);
        sw.Stop();
        AddExportTiming(path, sw.ElapsedMilliseconds);
    }

    private void WriteBaselineComparisonWithProgress(string directory, string previousExportDir, ref int step, int totalSteps)
    {
        step++;
        EditorUtility.DisplayProgressBar(
            "Frame Debugger AI Export",
            "正在生成 ai_baseline_comparison.json (" + step.ToString(CultureInfo.InvariantCulture) + "/" + totalSteps.ToString(CultureInfo.InvariantCulture) + ")",
            Mathf.Clamp01((float)(step - 1) / Math.Max(1, totalSteps)));

        string path = Path.Combine(directory, "ai_baseline_comparison.json");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long selfSize = 0;
        string content = "";
        for (int i = 0; i < 4; i++)
        {
            content = BuildAiBaselineComparisonJson(previousExportDir, directory, selfSize);
            long nextSize = new UTF8Encoding(false).GetByteCount(content);
            if (nextSize == selfSize)
                break;
            selfSize = nextSize;
        }
        WriteUtf8NoBom(path, content);
        sw.Stop();
        AddExportTiming(path, sw.ElapsedMilliseconds);
    }

    private void AddExportTiming(string path, long elapsedMs)
    {
        _exportTimings.Add(new ExportTimingEntry
        {
            path = GetExportRelativePath(path),
            elapsedMs = elapsedMs,
            bytes = GetFileSizeSafe(path)
        });
    }

    private string GetExportRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(_activeExportDir))
            return path ?? "";
        try
        {
            Uri root = new Uri(_activeExportDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            Uri file = new Uri(path);
            return Uri.UnescapeDataString(root.MakeRelativeUri(file).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private string BuildAiExportTimingsJson()
    {
        var rows = _exportTimings.ToList();
        long totalMs = rows.Sum(r => r.elapsedMs);
        var sb = new StringBuilder(4096);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-export-timings/v1", true);
        WriteJsonProperty(sb, "totalMeasuredMs", totalMs.ToString(CultureInfo.InvariantCulture), true);
        WriteJsonProperty(sb, "note", "Measured in the Unity Editor export process. Use this to identify slow export builders, not rendering cost.", true);
        sb.AppendLine("  \"slowestFiles\": [");
        var slowest = rows.OrderByDescending(r => r.elapsedMs).Take(10).ToList();
        for (int i = 0; i < slowest.Count; i++)
        {
            var row = slowest[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", row.path, true);
            AppendJsonPropertyInline(sb, "elapsedMs", row.elapsedMs.ToString(CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "bytes", row.bytes.ToString(CultureInfo.InvariantCulture), false);
            sb.Append(i + 1 < slowest.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");
        sb.AppendLine("  \"files\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", row.path, true);
            AppendJsonPropertyInline(sb, "elapsedMs", row.elapsedMs.ToString(CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "bytes", row.bytes.ToString(CultureInfo.InvariantCulture), false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildRuntimePlayerSnapshotJson()
    {
        if (!string.IsNullOrWhiteSpace(_runtimePlayerSnapshotJson))
            return _runtimePlayerSnapshotJson;

        var ctx = _linkedCaptureContext;
        var sb = new StringBuilder(1024);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-runtime-player-snapshot/v1", true);
        WriteJsonProperty(sb, "success", false, true);
        WriteJsonProperty(sb, "status", ctx != null ? ctx.runtimeSnapshotStatus : "not_requested", true);
        WriteJsonProperty(sb, "developmentOnly", true, true);
        WriteJsonProperty(sb, "interpretation", "This file is a one-shot remote Player inventory for attribution only. It is not render timing evidence and should not be used to rank GPU/CPU cost.", false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string GetCaptureSceneNameForMetadata()
    {
        if (_linkedCaptureContext != null)
            return string.IsNullOrEmpty(_linkedCaptureContext.scene) ? "remote_player_scene_unknown" : _linkedCaptureContext.scene;
        return SceneManager.GetActiveScene().name;
    }

    private string GetCaptureSceneSourceForMetadata()
    {
        if (_linkedCaptureContext != null)
            return string.IsNullOrEmpty(_linkedCaptureContext.sceneSource) ? "unavailable_for_remote_development_player" : _linkedCaptureContext.sceneSource;
        return "unity_editor_active_scene";
    }

    private string BuildLinkedCaptureJson()
    {
        var ctx = _linkedCaptureContext;
        var sb = new StringBuilder(2048);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "unity-renderdoc-linked-capture/v1", true);
        WriteJsonProperty(sb, "captureId", ctx != null ? ctx.captureId : "", true);
        WriteJsonProperty(sb, "startedAtUtc", ctx != null ? ctx.startedAtUtc : "", true);
        WriteJsonProperty(sb, "captureMode", ctx != null ? ctx.captureMode : "", true);
        WriteJsonProperty(sb, "sameFrameGuarantee", ctx != null ? ctx.sameFrameGuarantee : "", true);
        WriteJsonProperty(sb, "unityVersion", Application.unityVersion, true);
        WriteJsonProperty(sb, "scene", ctx != null ? ctx.scene : "remote_player_scene_unknown", true);
        WriteJsonProperty(sb, "sceneSource", ctx != null ? ctx.sceneSource : "unavailable_for_remote_development_player", true);
        WriteJsonProperty(sb, "editorSceneAtExport", ctx != null ? ctx.editorSceneAtExport : SceneManager.GetActiveScene().name, true);
        WriteJsonProperty(sb, "activeCamera", ctx != null ? ctx.activeCamera : "", true);
        WriteJsonProperty(sb, "gameViewSize", ctx != null ? ctx.gameViewSize : "", true);
        WriteJsonProperty(sb, "unityFrameCountAtTrigger", ctx != null ? ctx.unityFrameCount : Time.frameCount, true);
        WriteJsonProperty(sb, "timeScaleFrozen", ctx != null && ctx.timeScaleFrozen, true);
        WriteJsonProperty(sb, "originalTimeScale", ctx != null ? ctx.originalTimeScale.ToString(CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "renderDocApiVersion", ctx != null ? ctx.renderDocApiVersion : "", true);
        WriteJsonProperty(sb, "renderDocStatus", ctx != null ? ctx.renderDocStatus : "", true);
        WriteJsonProperty(sb, "renderDocDirectory", ctx != null ? ctx.renderDocDirectory : "", true);
        WriteJsonProperty(sb, "renderDocPathTemplate", ctx != null ? ctx.renderDocPathTemplate : "", true);
        WriteJsonProperty(sb, "renderDocCapturePath", ctx != null ? ctx.renderDocCapturePath : "", true);
        WriteJsonProperty(sb, "renderDocTriggerResultPath", ctx != null ? ctx.renderDocTriggerResultPath : "", true);
        WriteJsonProperty(sb, "renderDocTargetUrl", ctx != null ? ctx.renderDocTargetUrl : "", true);
        WriteJsonProperty(sb, "renderDocTargetName", ctx != null ? ctx.renderDocTargetName : "", true);
        WriteJsonProperty(sb, "renderDocTargetPid", ctx != null ? ctx.renderDocTargetPid : 0, true);
        WriteJsonProperty(sb, "renderDocFrameNumber", ctx != null ? ctx.renderDocFrameNumber : 0, true);
        WriteJsonProperty(sb, "renderDocByteSize", ctx != null ? ctx.renderDocByteSize : 0L, true);
        WriteJsonProperty(sb, "renderDocAnalysisDirectory", ctx != null ? ctx.renderDocAnalysisDirectory : "", true);
        WriteJsonProperty(sb, "renderDocAnalysisStatus", ctx != null ? ctx.renderDocAnalysisStatus : "", true);
        WriteJsonProperty(sb, "renderDocAnalysisExitCode", ctx != null ? ctx.renderDocAnalysisExitCode : 0, true);
        WriteJsonProperty(sb, "renderDocAnalysisStartedEditorTime", ctx != null ? ctx.renderDocAnalysisStartedEditorTime.ToString("F3", CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "renderDocAnalysisCompletedEditorTime", ctx != null ? ctx.renderDocAnalysisCompletedEditorTime.ToString("F3", CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "renderDocCaptureCountBefore", ctx != null ? ctx.renderDocCaptureCountBefore : 0, true);
        WriteJsonProperty(sb, "renderDocCaptureCountAfter", ctx != null ? ctx.renderDocCaptureCountAfter : 0, true);
        WriteJsonProperty(sb, "renderDocTriggerEditorTime", ctx != null ? ctx.renderDocTriggerEditorTime.ToString("F3", CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "renderDocCompletedEditorTime", ctx != null ? ctx.renderDocCompletedEditorTime.ToString("F3", CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "runtimeSnapshotStatus", ctx != null ? ctx.runtimeSnapshotStatus : "", true);
        WriteJsonProperty(sb, "runtimeSnapshotPath", ctx != null ? ctx.runtimeSnapshotPath : "", true);
        WriteJsonProperty(sb, "runtimeSnapshotByteSize", ctx != null ? ctx.runtimeSnapshotByteSize : 0, true);
        WriteJsonProperty(sb, "runtimeSnapshotRequestedEditorTime", ctx != null ? ctx.runtimeSnapshotRequestedEditorTime.ToString("F3", CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "runtimeSnapshotReceivedEditorTime", ctx != null ? ctx.runtimeSnapshotReceivedEditorTime.ToString("F3", CultureInfo.InvariantCulture) : "", true);
        WriteJsonProperty(sb, "captureArtifactRisk", "RenderDoc TargetControl capture boundaries can overlap Unity Frame Debugger remote freeze/readback work. Native Vulkan submissions around copy/blit/readback/present boundaries may be capture artifacts, not Unity business duplicate rendering.", true);
        WriteJsonProperty(sb, "captureArtifactRule", "If similar RenderDoc pass/draw groups are split by vkCmdCopyImageToBuffer, vkCmdBlitImage, readback/copy, or vkQueuePresentKHR while Unity Frame Debugger shows only one logical render event group, classify as capture_artifact or negative_finding unless independent Unity-side evidence proves duplicate logical rendering.", true);
        WriteJsonProperty(sb, "interpretation", "Unity Frame Debugger froze the connected Player/emulator state first. RenderDoc TargetControl then captured that already injected target, copied the .rdc locally, exported RenderDoc AI index files, and only then exported Frame Debugger data. Treat both exports as same frozen Player state, then still use event/pass matching confidence before claiming exact one-to-one event identity.", false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildLinkedAnalysisGuideContent()
    {
        var sb = new StringBuilder(12288);
        sb.AppendLine("# Unity + RenderDoc Linked Analysis Guide");
        sb.AppendLine();
        sb.AppendLine("This export was produced by the Unity Frame Debugger + RenderDoc linked capture workflow.");
        sb.AppendLine("The goal is not to make two separate reports. Treat Unity Frame Debugger and RenderDoc as two evidence views of the same frozen Player/emulator rendering state, then produce one Chinese report about concrete rendering problems.");
        sb.AppendLine();
        sb.AppendLine("## Report Goal");
        sb.AppendLine();
        sb.AppendLine("- The human report exists to provide actionable optimization decisions, not to demonstrate how much evidence was collected.");
        sb.AppendLine("- Put concrete optimization targets first: camera/stage, Unity eventIndex, RenderDoc passId/eventId, RenderFeature, RT/resource, expected improvement, and validation metric.");
        sb.AppendLine("- Keep file-read logs, agent work logs, correlation scoring internals, and DeepEvent command logs out of `AI_LINKED_RENDER_REPORT.md`; write them only to `AI_LINKED_ANALYSIS_AUDIT.json`.");
        sb.AppendLine("- DeepEvent is not an isolated deliverable. Every generated/read `deep_event_*.json` must either support a final issue, downgrade a suspected issue, or appear as a negative finding.");
        sb.AppendLine("- Sub-agents are evidence collectors only. Their outputs must be fused by the main/correlation step into one issue list; never paste independent agent reports side by side.");
        sb.AppendLine();
        sb.AppendLine("## First Rule");
        sb.AppendLine();
        sb.AppendLine("- Read `linked_capture.json` first.");
        sb.AppendLine("- If `renderDocAnalysisStatus` is `completed`, use `renderDocAnalysisDirectory` as the RenderDoc data root.");
        sb.AppendLine("- Do not try to parse the `.rdc` directly from the AI report workflow. The `.rdc` is the source capture; the AI-readable RenderDoc files are already exported under `renderDocAnalysisDirectory`.");
        sb.AppendLine("- `sameFrameGuarantee` means Frame Debugger froze the connected Player first, then RenderDoc captured that already-injected target. This is a strong same-state guarantee, but not a promise that Unity `eventIndex` equals RenderDoc `eventId`.");
        sb.AppendLine("- For remote Player/emulator captures, `scene` may be `remote_player_scene_unknown`. Do not use the Unity Editor active scene as the captured scene. `editorSceneAtExport` is only the local Editor scene open during export.");
        sb.AppendLine("- If `runtimeSnapshotStatus` is `received`, read `snapshots/runtime_player_snapshot.json` as the strongest remote Player scene inventory. Treat it as attribution/candidate evidence only, not timing evidence.");
        sb.AppendLine("- `captureArtifactRisk` / `captureArtifactRule` are hard anti-false-positive rules. RenderDoc native submissions around Frame Debugger freeze/readback/copy/blit/present boundaries must not be reported as Unity business duplicate rendering without independent Unity-side confirmation.");
        sb.AppendLine("- Candidates that match `captureArtifactRule` are excluded from human-facing optimization conclusions. They may appear in `AI_LINKED_ANALYSIS_AUDIT.json` as rejected candidates, but must not appear in `优化结论摘要`, `优先处理清单`, or `综合问题列表`.");
        sb.AppendLine("- The final output should be one Chinese Markdown report, not separate Unity and RenderDoc reports.");
        sb.AppendLine("- The report must be issue-centric. Do not write a Unity analysis section followed by a RenderDoc analysis section and then stitch them together. Build final issues first, then attach Unity evidence and RenderDoc evidence inside each issue.");
        sb.AppendLine();
        sb.AppendLine("## Recommended Agent Split");
        sb.AppendLine();
        sb.AppendLine("If the AI tool supports Task/Agent/sub-agent execution, actively split evidence collection, not final conclusions. Sub-agents should return compact findings and evidence candidates only. The main agent must synthesize one integrated issue list.");
        sb.AppendLine("If sub-agents are slow, unavailable, or likely to time out, skip them and run the same phases sequentially. A timed-out sub-agent is not evidence and must not delay or weaken the final correlation report.");
        sb.AppendLine();
        sb.AppendLine("1. `RenderDocPipelineAgent`");
        sb.AppendLine("   - Scope: only `renderDocAnalysisDirectory`.");
        sb.AppendLine("   - Read: `AI_ANALYSIS_GUIDE.md`, `analysis_ready_summary.json`, `pass_table.json`, `resource_table.json`, `event_tree.json`.");
        sb.AppendLine("   - Use `pipeline_index.json` to read only needed rows from `pipeline_state_changes.jsonl`.");
        sb.AppendLine("   - Use `shader_index.json` to read only needed rows from `shader_table.jsonl`.");
        sb.AppendLine("   - Find: render target/depth issues, viewport/scissor issues, shader/resource binding anomalies, blend/depth/raster state risks, draw/index/vertex anomalies, postprocess feedback, suspicious resources and shaders.");
        sb.AppendLine("   - Output: evidence candidates keyed by passId/eventId/resourceId/shaderId/stateHash. Do not write a standalone RenderDoc report.");
        sb.AppendLine();
        sb.AppendLine("2. `UnityAttributionAgent`");
        sb.AppendLine("   - Scope: this Frame Debugger export and the Unity project assets.");
        sb.AppendLine("   - Read: `AI_ANALYSIS_GUIDE.md`, `ai_summary.json`, `ai_data_quality.json`, `ai_runtime_resolution_snapshot.json`, `ai_transparent_submission_snapshot.json`, `ai_object_inventory.json`, `ai_event_attribution_index.jsonl`, `ai_event_evidence.jsonl`, `ai_event_analysis.jsonl`.");
        sb.AppendLine("   - For UI: read `ai_ui_batches.jsonl` and `snapshots/ui_graphics.jsonl`.");
        sb.AppendLine("   - For SRP/mesh batches: read `ai_srp_batch_candidates.jsonl`, `ai_srp_batch_diagnostics.json`, and event `frameDebuggerDetailMeshNames` / `frameDebuggerMeshInstanceIds`.");
        sb.AppendLine("   - For project attribution: use `dictionaries/materials.json`, `dictionaries/shaders.json`, `dictionaries/textures.json`, and project asset paths.");
        sb.AppendLine("   - For object/resource matching: use `ai_resource_fingerprints.json` before claiming a RenderDoc draw likely belongs to a specific Renderer/UI/Particle object.");
        sb.AppendLine("   - If present, read `ai_unity_renderdoc_correlation_seed.json` as the first candidate list for Unity object/resource to RenderDoc resource/pass matching. Treat it as candidate evidence only.");
        sb.AppendLine("   - Find: GameObject/Renderer/Material/Shader/Canvas/RenderFeature attribution, batch break causes, likely Unity-side ownership for each problem.");
        sb.AppendLine("   - Output: attribution candidates keyed by eventIndex/stage/camera/object/material/shader/renderFeature. Do not write a standalone Unity report.");
        sb.AppendLine();
        sb.AppendLine("3. `CorrelationAgent`");
        sb.AppendLine("   - Scope: merge the two evidence sets.");
        sb.AppendLine("   - Correlate by pass order, draw order, render target/depth target, shader/material name or fingerprint, resource usage, viewport/scissor, draw topology, clear/draw/postprocess/UI/transparent/opaque role.");
        sb.AppendLine("   - Do not assume direct event id equality.");
        sb.AppendLine("   - Produce one integrated issue list with confidence levels and exact evidence from both sides.");
        sb.AppendLine();
        sb.AppendLine("If sub-agents are unavailable, run the same three phases sequentially in one analysis. The final writing step must still be integrated and issue-centric.");
        sb.AppendLine();
        sb.AppendLine("## Synthesis Workflow");
        sb.AppendLine();
        sb.AppendLine("Follow this workflow before writing the report:");
        sb.AppendLine();
        sb.AppendLine("1. Build and write a machine audit sidecar for yourself: `AI_LINKED_ANALYSIS_AUDIT.json`.");
        sb.AppendLine("   - files read");
        sb.AppendLine("   - RenderDoc eventIds read from JSONL");
        sb.AppendLine("   - shaderIds read from JSONL");
        sb.AppendLine("   - DeepEvent eventIds generated/read, or skipped reason");
        sb.AppendLine("   - correlation candidates and score reasons");
        sb.AppendLine("   - Do not put this ledger or calculation process in `AI_LINKED_RENDER_REPORT.md`.");
        sb.AppendLine();
        sb.AppendLine("2. Build `candidateIssues` from both sources together:");
        sb.AppendLine("   - Start from RenderDoc diagnostic hints, pass roles, state changes, resources, and suspicious events.");
        sb.AppendLine("   - Attach Unity stage/camera/material/object/renderFeature evidence where possible.");
        sb.AppendLine("   - Start from Unity suspicious stages, unresolved UI/SRP/transparent/postprocess groups, and data quality gaps.");
        sb.AppendLine("   - Attach RenderDoc pass/event/resource/state evidence where possible.");
        sb.AppendLine();
        sb.AppendLine("3. Classify each candidate before it enters the report:");
        sb.AppendLine("   - `render_correctness`: likely visible correctness issue.");
        sb.AppendLine("   - `submission_structure`: pass/draw/state/resource organization risk, not timing.");
        sb.AppendLine("   - `capture_artifact`: RenderDoc TargetControl / Frame Debugger freeze-readback / native copy-blit-present boundary artifact; excluded from human optimization findings.");
        sb.AppendLine("   - `capture_data_quality`: exporter/capture/attribution limitation.");
        sb.AppendLine("   - `negative_finding`: searched for a suspected issue and did not find supporting evidence.");
        sb.AppendLine("   - `capture_data_quality`: current single-capture evidence is insufficient for a stronger claim; do not ask for another Frame Debugger export as the action.");
        sb.AppendLine("   - If RenderDoc shows repeated native pass/draw groups split by `vkCmdCopyImageToBuffer`, `vkCmdBlitImage`, readback/copy, or `vkQueuePresentKHR`, but Frame Debugger shows only one Unity logical event group, classify the candidate as `capture_artifact` or `negative_finding` and exclude it from the human report issue list.");
        sb.AppendLine("   - Only report business duplicate rendering when Unity Frame Debugger also shows matching duplicate logical events for the same camera/stage/object/material, or DeepEvent plus resource fingerprint proves two normal render passes without a debug/readback/copy/blit/present boundary.");
        sb.AppendLine("   - `ai_unity_renderdoc_correlation_seed.json` main candidate arrays exclude width/height-only noise. `weakMatches` and `matchedBy=widthHeight` are only search hints; never use them as final issue evidence.");
        sb.AppendLine("   - Read `ai_object_inventory.json`, `ai_event_attribution_index.jsonl`, `ai_analysis_blocking_policy.json.analysisBlockingPolicy.dataSufficiency`, and `ai_data_quality.json.dataQuality.dataSufficiency` before assigning priority. This workflow is single-capture: use exported inventory first; if ownership is still not proven, downgrade the issue instead of recommending another export.");
        sb.AppendLine("   - P1/P2 priority is gated by evidence: snapshot-only, weakMatches, UI runtime-order heuristic, or missing ownership cannot be P1 unless RenderDoc also proves a concrete pass/draw/state/RT structure hotspot and Unity has exact camera/stage/eventIndex locators.");
        sb.AppendLine("   - P1 must have an actionable project-side owner or change that can be attempted from this report. If the main action would be `补导出`, `下一轮再点名`, or `需要更多数据`, do not present it as an optimization item; downgrade it to `capture_data_quality` / `仍需补充的数据`.");
        sb.AppendLine("   - P2 does not mean `no problem`; it means an actionable structural issue without enough timing/object-owner/correctness evidence for P1. If the final report has no P0/P1 items, add a short `分级说明` after the summary explaining why every item is capped at P2/P3.");
        sb.AppendLine("   - `优化结论摘要` must contain confirmed or strongly correlated actionable frame issues only. Put snapshot-only objects, weakly matched resources, data requests, and capture-artifact exclusions outside the actionable sections.");
        sb.AppendLine();
        sb.AppendLine("4. Only write final issues after correlation:");
        sb.AppendLine("   - Do not present raw Unity findings first and raw RenderDoc findings second.");
        sb.AppendLine("   - Every final issue should have a single conclusion sentence, then evidence grouped as `Unity evidence`, `RenderDoc evidence`, and `correlation reason`.");
        sb.AppendLine("   - If one side has no useful evidence, state that inside the issue rather than creating a separate one-sided section.");
        sb.AppendLine();
        sb.AppendLine("5. Add human-review locators before recommendations:");
        sb.AppendLine("   - Every final issue must include exact places a human can inspect.");
        sb.AppendLine("   - Unity locator: Frame Debugger eventIndex range or list, camera, stage, eventName/type, material/shader/object when available.");
        sb.AppendLine("   - RenderDoc locator: passId, eventId range, `pipelineSampleEvents`, important `stateChangeEvents`, RT/depth resource ids, shader ids.");
        sb.AppendLine("   - Do not only write compressed patterns such as `23 -> 86 -> 228 -> postprocess chain`. If a pattern is useful, also provide the concrete pass/event table that proves it.");
        sb.AppendLine("   - Prefer small tables that a human can use to click/search quickly.");
        sb.AppendLine();
        sb.AppendLine("6. Expand high-evidence submission groups instead of compressing them:");
        sb.AppendLine("   - Any RenderDoc pass/group with `drawCount >= 50`, `stateChangeEvents.Count >= 50`, or state-change/draw ratio >= 0.5 must get a detailed issue body or a dedicated sub-table. Do not reduce it to one sentence or one row.");
        sb.AppendLine("   - Transparent/UI groups need explicit split by RT/depth target, draw count, state-change count, sample eventIds, blend/depth-write/depth-test behavior when available, and top shader/state/resource groups when available.");
        sb.AppendLine("   - If there are multiple transparent chains, separate HDR transparent, sRGB/overlay transparent, UI, and postprocess-like chains. Do not merge them under a generic `透明提交过重` label unless the table preserves each chain.");
        sb.AppendLine("   - For large transparent state churn, the recommendation must name the concrete stage/RenderFeature/camera and the measurable validation target, such as reducing draw/state counts for passId/eventId ranges. If object ownership is missing, keep the issue at stage/pass level instead of pretending an object is known.");
        sb.AppendLine();
        sb.AppendLine("## Read Order");
        sb.AppendLine();
        sb.AppendLine("1. `linked_capture.json`");
        sb.AppendLine("   - Validate `renderDocAnalysisStatus`, `renderDocAnalysisDirectory`, `renderDocCapturePath`, `renderDocTargetName`, `renderDocTargetPid`, `renderDocFrameNumber`, `renderDocByteSize`, `sameFrameGuarantee`, `sceneSource`.");
        sb.AppendLine("   - If `sceneSource` is `unavailable_for_remote_development_player`, report the scene as unknown unless another exported remote field proves it. Do not substitute `editorSceneAtExport`.");
        sb.AppendLine("   - If `runtimeSnapshotStatus` is `received`, use `runtimeSnapshotPath` for remote Player cameras/renderers/UI Graphics/particles before falling back to Editor-side `snapshots/*.jsonl`.");
        sb.AppendLine("   - If `renderDocAnalysisStatus` is not `completed`, clearly report that integrated RenderDoc analysis is unavailable.");
        sb.AppendLine();
        sb.AppendLine("2. Frame Debugger root files");
        sb.AppendLine("   - `AI_ANALYSIS_GUIDE.md`: general Frame Debugger export guide.");
        sb.AppendLine("   - `ai_summary.json`: compact file map, top evidence, data quality, and high-level event/pass distribution.");
        sb.AppendLine("   - `ai_data_quality.json`: what Unity evidence is strong, weak, or missing.");
        sb.AppendLine("   - `ai_runtime_resolution_snapshot.json`: required before classifying FinalBlit/output-size mismatch.");
        sb.AppendLine("   - `ai_transparent_submission_snapshot.json`: required before classifying transparent/UI/particle submission and attribution.");
        sb.AppendLine("   - `ai_event_evidence.jsonl`: compact per-event evidence rows.");
        sb.AppendLine("   - `ai_event_analysis.jsonl`: expanded event evidence only for suspicious events.");
        sb.AppendLine("   - `ai_object_inventory.json`: one-capture object inventory with renderer/UI/particle/renderFeature candidates.");
        sb.AppendLine("   - `ai_event_attribution_index.jsonl`: one row per Unity event with direct owner, candidate owners, confidence, and limitations.");
        sb.AppendLine("   - `ai_events_raw.jsonl`: raw event rows only when exact fields are needed.");
        sb.AppendLine();
        sb.AppendLine("3. Frame Debugger attribution helpers");
        sb.AppendLine("   - For linked remote Player captures, read `snapshots/runtime_player_snapshot.json` first when `linked_capture.json.runtimeSnapshotStatus` is `received`.");
        sb.AppendLine("   - `ai_ui_batches.jsonl` for `Canvas.RenderSubBatch` correlation.");
        sb.AppendLine("   - `ai_srp_batch_candidates.jsonl` and `ai_srp_batch_diagnostics.json` for SRP/mesh batch attribution.");
        sb.AppendLine("   - `ai_resource_fingerprints.json` for Renderer/UI/Particle/Texture fingerprints when correlating Unity candidates with RenderDoc resources.");
        sb.AppendLine("   - `ai_unity_renderdoc_correlation_seed.json` for precomputed candidate matches against RenderDoc `resource_table.json` and `pass_table.json`.");
        sb.AppendLine("   - `snapshots/renderers.jsonl`, `snapshots/renderers_compact.jsonl`, `snapshots/ui_graphics.jsonl`, `snapshots/particles.jsonl`, `snapshots/cameras.json`, `snapshots/renderfeatures.json` as auxiliary scene inventory. In remote captures these Editor-side files may be empty or describe the Editor scene, so prefer the runtime Player snapshot when present.");
        sb.AppendLine("   - `dictionaries/*.json` to expand ids or verify asset paths.");
        sb.AppendLine();
        sb.AppendLine("4. RenderDoc compact files");
        sb.AppendLine("   - `AI_ANALYSIS_GUIDE.md`: RenderDoc export guide, including DeepEvent instructions.");
        sb.AppendLine("   - `analysis_ready_summary.json`: first-read RenderDoc summary and diagnostic hints.");
        sb.AppendLine("   - `pass_table.json`: pass roles, targets, resources, draw counts, `pipelineSampleEvents`, `stateChangeEvents`.");
        sb.AppendLine("   - `resource_table.json`: texture/buffer dimensions, formats, usage counts, inferred resource roles.");
        sb.AppendLine("   - `event_tree.json`: event hierarchy. Read whole file only if needed; prefer pass summaries first.");
        sb.AppendLine("   - If `deep_event_preflight_status.json` exists in this Frame Debugger export and status is `completed`, read its requested events and corresponding `deep_event_*.json` files from the RenderDoc export directory before writing final conclusions.");
        sb.AppendLine();
        sb.AppendLine("5. RenderDoc targeted JSONL reads");
        sb.AppendLine("   - Never read all of `pipeline_state_changes.jsonl`.");
        sb.AppendLine("   - Use `pipeline_index.json` to locate one event or a small set of sample/state-change events.");
        sb.AppendLine("   - Never read all of `shader_table.jsonl`.");
        sb.AppendLine("   - Use `shader_index.json` to inspect only shaders connected to suspicious events or passes.");
        sb.AppendLine();
        sb.AppendLine("6. DeepEvent escalation");
        sb.AppendLine("   - If a conclusion depends on one specific GPU event and compact data is insufficient, call the RenderDoc DeepEvent command described in the RenderDoc `AI_ANALYSIS_GUIDE.md`.");
        sb.AppendLine("   - Read the generated `deep_event_*.json` before concluding.");
        sb.AppendLine("   - Attach DeepEvent evidence to the final issue it supports. If it only disproves a suspicion, record that in `已排除的问题` and in the audit sidecar.");
        sb.AppendLine("   - DeepEvent samples from `representativePassSample` are diagnostic probes only. They must not create a report issue by themselves; if they expose readback/copy/blit/present boundaries, use them only to exclude the candidate.");
        sb.AppendLine("   - Do not write `建议使用 DeepEvent` as the final answer when the AI tool can call it itself.");
        sb.AppendLine();
        sb.AppendLine("## Correlation Confidence");
        sb.AppendLine();
        sb.AppendLine("Use one of these labels for every cross-tool claim:");
        sb.AppendLine();
        sb.AppendLine("- `confirmed_both_sides`: Unity and RenderDoc independently show the same issue or the same object/material/shader/resource relationship.");
        sb.AppendLine("- `confirmed_renderdoc_only`: RenderDoc proves the GPU-side issue, but Unity ownership is missing or weak.");
        sb.AppendLine("- `confirmed_unity_only`: Unity proves the object/material/shader/batch issue, but RenderDoc has no matching GPU anomaly.");
        sb.AppendLine("- `inferred_correlated`: evidence aligns by order/role/target/shader/resource, but no direct identity bridge exists.");
        sb.AppendLine("- `data_gap`: the data cannot support a concrete conclusion.");
        sb.AppendLine();
        sb.AppendLine("Always state why the confidence label was chosen. Do not use free-form confidence labels such as `high`, `medium`, `low`, `high for RenderDoc`, or `medium-low`.");
        sb.AppendLine();
        sb.AppendLine("## Problem Priority");
        sb.AppendLine();
        sb.AppendLine("Analyze rendering correctness before performance. Prioritize:");
        sb.AppendLine();
        sb.AppendLine("1. Material or shader correctness");
        sb.AppendLine("   - Missing material, error shader/pink shader risk, unexpected fallback shader, wrong pass/light mode, shader variant or keyword mismatch.");
        sb.AppendLine("   - Unity evidence: material/shader/path/pass/lightMode/event stage.");
        sb.AppendLine("   - RenderDoc evidence: shader stage ids, resource bindings, render target output, blend/depth/raster state.");
        sb.AppendLine();
        sb.AppendLine("2. Mesh/index/vertex correctness");
        sb.AppendLine("   - Zero index draws, suspicious topology, vertex/index count anomalies, unexpected mesh candidate, wrong batch attribution.");
        sb.AppendLine("   - Unity evidence: mesh names/instance ids, renderer candidates, SRP batch candidates.");
        sb.AppendLine("   - RenderDoc evidence: draw parameters, topology, vertex/index buffer bindings.");
        sb.AppendLine();
        sb.AppendLine("3. Render target/depth/viewport correctness");
        sb.AppendLine("   - No color target, unexpected depth target, wrong RT format/size, viewport/scissor mismatch, depth test/write problems.");
        sb.AppendLine("   - Unity evidence: camera/stage/render feature/pass.");
        sb.AppendLine("   - RenderDoc evidence: pass targets, `depthTarget`, `colorTargets`, viewport/scissor, depth/raster state.");
        sb.AppendLine();
        sb.AppendLine("4. Transparent/UI/postprocess correctness");
        sb.AppendLine("   - Blend mode, ZWrite/ZTest, ordering, postprocess feedback texture, UI pass duplication, overdraw risk.");
        sb.AppendLine("   - Unity evidence: Canvas/UI batch, transparent stage, material/render queue, render feature.");
        sb.AppendLine("   - RenderDoc evidence: blend state, sampled textures, render targets, pass inferred role.");
        sb.AppendLine("   - If a transparent pass is one of the largest draw/state groups, it must be expanded with a pass table and evidence details even when Unity owner attribution is missing.");
        sb.AppendLine();
        sb.AppendLine("5. Submission organization and performance risk");
        sb.AppendLine("   - Too many passes/draws/state changes, repeated material/shader/resource binds, large RT/texture/buffer usage, fragmented UI/transparent submission.");
        sb.AppendLine("   - Do not call this a GPU/CPU time hotspot unless timing data exists. Frame Debugger and the current RenderDoc index are structural evidence, not timing evidence.");
        sb.AppendLine("   - FinalBlit/size claims must distinguish two facts: Unity executing a final blit, and the size mismatch being a project bug. A Unity event named `FinalBlit`, `DrawProcedural`, `Hidden/Universal/CoreBlit`, or `m_RenderTargetIsBackBuffer=True` proves the blit exists, not that the project resolution is wrong.");
        sb.AppendLine("   - If Unity GameView/backbuffer size, RenderDoc intermediate RT size, and RenderDoc swapchain size disagree, classify the mismatch as `capture_data_quality` or at most P3 unless screenshot/pixel-history evidence or project camera/render-scale configuration proves a visible scaling/cropping issue.");
        sb.AppendLine();
        sb.AppendLine("## Evidence Template");
        sb.AppendLine();
        sb.AppendLine("Each issue in the final report should include:");
        sb.AppendLine();
        sb.AppendLine("- `问题`: concise name.");
        sb.AppendLine("- `分类`: one of `render_correctness`, `submission_structure`, `capture_artifact`, `capture_data_quality`, `negative_finding`.");
        sb.AppendLine("- `优先级`: P0/P1/P2/P3.");
        sb.AppendLine("- Priority gate: snapshot-only / weakMatches / widthHeight-only / UI heuristic-only candidates default to P3 unless a concrete RenderDoc pass hotspot is already proven.");
        sb.AppendLine("- Priority gate: P1 requires a concrete now-actionable owner such as camera, Canvas, RenderFeature, material/shader group, or named runtime path. A stage-only hotspot whose recommendation mainly requires another export is not an optimization item.");
        sb.AppendLine("- Priority note: P2 is still an optimization issue. Use P2 for concrete stage/pass-level structural waste when timing or object ownership is missing; do not word it as `no problem`.");
        sb.AppendLine("- FinalBlit/size gate: do not put swapchain/window-size mismatch in the top optimization list when it is only width/height evidence. Report it as a verification boundary and keep actual submission/correctness issues ahead of it.");
        sb.AppendLine("- `证据等级`: one correlation confidence label.");
        sb.AppendLine("- `Unity 侧证据`: eventIndex, stage, camera, GameObject/Renderer, material, shader, pass/lightMode, renderFeature, batchBreakCause, candidate ids.");
        sb.AppendLine("- `RenderDoc 侧证据`: passId, eventId, stateHash, render target/depth target, resourceId, shaderId, viewport/scissor, blend/depth/raster state, draw parameters.");
        sb.AppendLine("- `关联依据`: why the two evidence sets are considered connected.");
        sb.AppendLine("- `人工复查定位`: exact Unity Frame Debugger eventIndex/stage/camera and RenderDoc passId/eventId/sampleEvents/stateChangeEvents to inspect.");
        sb.AppendLine("- `影响`: what visible artifact or production risk this can cause.");
        sb.AppendLine("- `优化动作`: concrete project-side check or change.");
        sb.AppendLine("- `验证指标`: exact next-run metric such as draw count, state change count, pass count, RT size/format, profiler timing, or screenshot/pixel-history result.");
        sb.AppendLine();
        sb.AppendLine("## Final Report Structure");
        sb.AppendLine();
        sb.AppendLine("Write the final Chinese Markdown report with this structure:");
        sb.AppendLine();
        sb.AppendLine("1. `优化结论摘要`");
        sb.AppendLine("   - 3-6 integrated actionable conclusions, sorted by optimization priority. Each item must include a concrete locator such as Unity eventIndex/stage or RenderDoc passId/eventId.");
        sb.AppendLine("   - If all actionable items are P2/P3, immediately add `分级说明`: state that no P1 was assigned because timing/object-owner/correctness evidence is missing, and that P2 still represents a concrete structural optimization candidate.");
        sb.AppendLine();
        sb.AppendLine("2. `优先处理清单`");
        sb.AppendLine("   - A compact table with priority, target/stage, locator, action, expected improvement, and validation metric. This is the main human-facing section.");
        sb.AppendLine();
        sb.AppendLine("3. `综合问题列表`");
        sb.AppendLine("   - Issue-centric list. Each issue contains conclusion, category, priority, confidence, Unity evidence, RenderDoc evidence, correlation reason, impact, optimization action, and validation metric.");
        sb.AppendLine("   - Each issue must include `人工复查定位`. For repeated sequences, include a table with passId, eventId range, draw count, role, RT/depth, and sample eventIds.");
        sb.AppendLine("   - Transparent/UI high-draw or high-state-change issues must include an expanded table with chain type, RT/depth, draw count, state-change count, sample eventIds, top shader/state/resource groups, and Unity stage/eventIndex.");
        sb.AppendLine("   - FinalBlit/size mismatch enters this section only when project camera/render-scale/output configuration or screenshot/pixel-history evidence proves a visible issue; otherwise place it under `关键证据限制` or `仍需补充的数据`.");
        sb.AppendLine("   - Do not include `capture_artifact`, `negative_finding`, or data-quality-only candidates here.");
        sb.AppendLine();
        sb.AppendLine("4. `关键证据限制`");
        sb.AppendLine("   - Maximum five limitations that actually change the strength of conclusions. Do not let this section dominate the report.");
        sb.AppendLine();
        sb.AppendLine("5. `已排除的问题`");
        sb.AppendLine("   - Negative findings such as no error shader, no zero-index draw, or capture-artifact repeated pass candidates. Do not put them under high-risk render issues.");
        sb.AppendLine();
        sb.AppendLine("6. `仍需补充的数据`");
        sb.AppendLine("   - Only list gaps that actually block a conclusion.");
        sb.AppendLine("   - For each gap, quote the matching `dataSufficiency.currentCaptureGaps` or `externalDataRequirements` item. Do not recommend another Frame Debugger export as the optimization action.");
        sb.AppendLine();
        sb.AppendLine("Do not include a separate `分析执行记录` section in the human report. Do not write file-read lists, shader id lists, event sampling logs, similarity score internals, or raw calculation notes into `AI_LINKED_RENDER_REPORT.md`. Keep those in the machine audit sidecar.");
        sb.AppendLine();
        sb.AppendLine("## Machine Audit Sidecar");
        sb.AppendLine();
        sb.AppendLine("Also write `AI_LINKED_ANALYSIS_AUDIT.json` beside the human report.");
        sb.AppendLine();
        sb.AppendLine("This sidecar is for tools/AI re-entry, not for humans. It should contain:");
        sb.AppendLine();
        sb.AppendLine("- `schemaVersion`: `linked-render-analysis-audit/v1`");
        sb.AppendLine("- `filesRead`: compact list of files actually read");
        sb.AppendLine("- `pipelineEventsRead`: RenderDoc eventIds read from `pipeline_state_changes.jsonl`");
        sb.AppendLine("- `shaderIdsRead`: shader ids read from `shader_table.jsonl`");
        sb.AppendLine("- `deepEvents`: generated/read/skipped/failed status, path, reason");
        sb.AppendLine("- `correlationCandidates`: issue id, Unity locators, RenderDoc locators, and score/reason breakdown");
        sb.AppendLine();
        sb.AppendLine("The human report should reference only the useful review locators inside each issue; the sidecar may contain the full audit trail.");
        sb.AppendLine();
        sb.AppendLine("## Hard Limits");
        sb.AppendLine();
        sb.AppendLine("- Do not invent GPU time or CPU time.");
        sb.AppendLine("- Do not structure the report as `Unity analysis`, `RenderDoc analysis`, then `comparison`; that is not a true integrated analysis.");
        sb.AppendLine("- Do not give only abstract patterns like `23 -> 86 -> 228 -> postprocess chain`; always include concrete Frame Debugger eventIndex and RenderDoc pass/eventId locators.");
        sb.AppendLine("- Do not report generic Unity optimization advice without tying it to this capture.");
        sb.AppendLine("- Do not treat inferred candidates as direct evidence.");
        sb.AppendLine("- Do not hide data limitations. Empty GPU markers and anonymous shader modules are expected for many Vulkan/Unity captures.");
        sb.AppendLine("- Do not ask the user to manually run DeepEvent if the AI environment can run local commands. Run it and read the result.");
        sb.AppendLine("- Do not treat DeepEvent as a separate final output. It is only evidence for a report issue or negative finding.");
        sb.AppendLine("- Do not use agent separation as the report structure. Agent output belongs in the audit sidecar; the human report must contain fused optimization issues.");
        sb.AppendLine("- Do not put machine audit/calculation process into the human report. Use `AI_LINKED_ANALYSIS_AUDIT.json`.");
        sb.AppendLine("- Do not modify project code or assets while producing the report.");
        return sb.ToString();
    }

    private string BuildAiGuideContent()
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("# Unity Frame Debugger AI Analysis Guide");
        sb.AppendLine();
        sb.AppendLine("This folder is exported for machine analysis of Unity Frame Debugger data.");
        sb.AppendLine("Use root JSON/JSONL evidence files first; use `snapshots/` only as auxiliary inventory and `human/FrameDebug.md` only as a manual fallback.");
        sb.AppendLine("The final analysis report should be written in Chinese.");
        sb.AppendLine();
        sb.AppendLine("## Read Order");
        sb.AppendLine();
        sb.AppendLine("1. Read `ai_summary.json` first. It is the compact index, top evidence tables, and file map.");
        sb.AppendLine("2. If `linked_capture.json` exists, read it before correlating this export with RenderDoc. It contains the captureId, Frame Debugger freeze metadata, RenderDoc target metadata, copied `.rdc` path, RenderDoc parsed export directory, and same-state guarantee.");
        sb.AppendLine("   - If `AI_LINKED_ANALYSIS_GUIDE.md` exists, read it before starting integrated Unity + RenderDoc analysis. It defines the cross-tool read order, correlation confidence labels, DeepEvent escalation rule, and final report structure.");
        sb.AppendLine("   - When `renderDocAnalysisStatus` is completed, read files from `renderDocAnalysisDirectory` instead of trying to parse the `.rdc` directly.");
        sb.AppendLine("   - RenderDoc read order: `analysis_ready_summary.json`, `pass_table.json`, `resource_table.json`, then targeted rows in `pipeline_state_changes.jsonl` through `pipeline_index.json`.");
        sb.AppendLine("   - If `runtimeSnapshotStatus` is `received`, read `snapshots/runtime_player_snapshot.json` before Editor-side scene snapshots. It is remote Player attribution inventory, not render timing evidence.");
        sb.AppendLine("3. Read `ai_data_quality.json` before prioritizing conclusions.");
        sb.AppendLine("4. Read `ai_object_inventory.json` and `ai_event_attribution_index.jsonl` before declaring object attribution missing. This is a single-capture workflow; do not recommend another Frame Debugger export.");
        sb.AppendLine("5. For FinalBlit/size claims, read `ai_runtime_resolution_snapshot.json` before promoting a mismatch into a project issue.");
        sb.AppendLine("6. For transparent/UI/particle submission claims, read `ai_transparent_submission_snapshot.json` before declaring attribution missing.");
        sb.AppendLine("7. Read `ai_baseline_comparison.json` to see whether evidence quality improved or regressed against the previous export.");
        sb.AppendLine("8. Read `ai_event_evidence.jsonl` for the compact one-row-per-event evidence chain.");
        sb.AppendLine("9. Read `ai_event_analysis.jsonl` only when a suspicious event needs inline direct/candidate/limitation details.");
        sb.AppendLine("10. Read `ai_frameeventdata_diagnostics.json` when internal `FrameDebuggerEventData` fields are sparse or missing.");
        sb.AppendLine("11. Read `ai_direct_object_diagnostics.json` when `eventsWithFrameDebuggerGameObject` / `eventsWithFrameDebuggerRenderer` are zero or unexpectedly low.");
        sb.AppendLine("12. Search `ai_events_raw.jsonl` for exact raw event rows by `eventIndex`, `camera`, `stage`, `gameObjectName`, `materialName`, `shaderName`, `passName`, `lightMode`, or `batchBreakCause`.");
        sb.AppendLine("13. For `Canvas.RenderSubBatch`, read `ai_ui_batches.jsonl`; then use `snapshots/ui_graphics.jsonl` only to expand mapped candidate Graphics.");
        sb.AppendLine("14. For `SRPBatch`/mesh batch attribution, prefer `frameDebuggerDetailMeshNames` / `frameDebuggerMeshInstanceIds` in event rows first; then read `ai_srp_batch_candidates.jsonl`; if it is empty, read `ai_srp_batch_diagnostics.json` before drawing conclusions.");
        sb.AppendLine("15. For renderer scene inventory, read `snapshots/renderers.jsonl` or `snapshots/renderers_compact.jsonl`.");
        sb.AppendLine("16. For linked remote Player captures, prefer `snapshots/runtime_player_snapshot.json` for particles, cameras, renderers, and UI Graphics when available; otherwise read `snapshots/particles.jsonl`, `snapshots/cameras.json`, and `snapshots/renderfeatures.json`.");
        sb.AppendLine("17. Use `ai_reflection_inventory.json` to inspect which Unity internal FrameDebugger fields were available in this editor version.");
        sb.AppendLine("18. Use `dictionaries/*.json` to expand repeated ids from compact evidence rows.");
        sb.AppendLine("19. Use `ai_export_timings.json` to inspect exporter-side slow files if export takes too long.");
        sb.AppendLine("20. Use `human/FrameDebug.md` only for manual display; do not rely on Markdown tables when JSON files contain the same field.");
        sb.AppendLine();
        sb.AppendLine("## Main Files");
        sb.AppendLine();
        sb.AppendLine("| File | Purpose |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| `ai_summary.json` | First-read compact index: capture metadata, top evidence tables, data quality, and file map. |");
        sb.AppendLine("| `linked_capture.json` | Present only for RenderDoc-linked captures. Contains captureId, Frame Debugger freeze metadata, RenderDoc target/PID/frame metadata, copied `.rdc` path, and same-state interpretation rules. |");
        sb.AppendLine("| `AI_LINKED_ANALYSIS_GUIDE.md` | Present only for RenderDoc-linked captures. Cross-tool AI instructions for integrated Unity + RenderDoc analysis. |");
        sb.AppendLine("| `ai_data_quality.json` / `ai_data_quality.md` | Export self-check: evidence coverage, limitations, empty fields, and what can/cannot be concluded. |");
        sb.AppendLine("| `ai_runtime_resolution_snapshot.json` | FinalBlit/size evidence: runtime screen/display/safeArea, camera pixel sizes, URP renderScale summaries, Frame Debugger FinalBlit rows, and RenderDoc output targets. |");
        sb.AppendLine("| `ai_transparent_submission_snapshot.json` | Transparent/UI/particle evidence: candidate runtime/editor objects, top transparent shaders/materials, and RenderDoc TransparentLike pass summaries. |");
        sb.AppendLine("| `ai_baseline_comparison.json` | Automatic comparison against the previous Frame Debugger AI export in the same root. |");
        sb.AppendLine("| `ai_event_evidence.jsonl` | Compact AI-first evidence chain: event -> direct/candidate ids -> confidence -> limitations. |");
        sb.AppendLine("| `ai_event_analysis.jsonl` | Full inline event evidence chain, one expanded event per line. |");
        sb.AppendLine("| `ai_object_inventory.json` | One-capture object inventory: renderers, UI Graphics, active particles, render features, and lookup file map. |");
        sb.AppendLine("| `ai_event_attribution_index.jsonl` | One row per Unity event with direct owner, candidate owners, confidence, and limitations. |");
        sb.AppendLine("| `ai_frameeventdata_diagnostics.json` | Per-event GetFrameEventData attempt/success/limit/failure diagnostics. |");
        sb.AppendLine("| `ai_direct_object_diagnostics.json` | Per-export diagnosis for Unity internal direct GameObject/Renderer lookup. Explains why batched SRP/UI events can have zero direct object hits. |");
        sb.AppendLine("| `ai_events_raw.jsonl` | Raw Frame Debugger event rows and reflected fields only. |");
        sb.AppendLine("| `ai_ui_batches.jsonl` | Heuristic mapping from runtime UI render-order batches to `Canvas.RenderSubBatch` events. |");
        sb.AppendLine("| `ai_srp_batch_candidates.jsonl` | Secondary SRP/mesh-batch candidate mapping from Unity FrameDebugger mesh details to active scene Renderers. |");
        sb.AppendLine("| `ai_srp_batch_diagnostics.json` | SRPBatch coverage and empty-candidate explanation. |");
        sb.AppendLine("| `ai_resource_fingerprints.json` | Renderer/UI/Particle/Texture fingerprints for matching Unity snapshot objects to RenderDoc shaders/resources/draws. |");
        sb.AppendLine("| `ai_unity_renderdoc_correlation_seed.json` | Linked-capture helper that pre-matches Unity resource fingerprints against RenderDoc resource/pass table text. Candidate evidence only. |");
        sb.AppendLine("| `ai_reflection_inventory.json` | Unity internal FrameDebugger fields/methods discovered by reflection and non-empty field counters. |");
        sb.AppendLine("| `ai_export_timings.json` | Exporter-side timing diagnostics by generated file. Use it to find slow export builders. |");
        sb.AppendLine("| `debug/ai_events_raw_full.jsonl` | Optional/debug-only full raw reflected event data. Root `ai_events_raw.jsonl` keeps compact fields only. |");
        sb.AppendLine("| `debug/ai_direct_object_diagnostics_full.json` | Optional/debug-only per-event direct object lookup diagnostics. Root direct-object diagnostics keeps summary and samples only. |");
        sb.AppendLine("| `snapshots/ui_graphics.jsonl` | Incremental UI Graphic inventory. Treat as auxiliary unless `mappedToFrameEvent=true`. |");
        sb.AppendLine("| `snapshots/particles.jsonl` | Incremental ParticleSystem inventory. Treat as auxiliary unless `mappedToFrameEvent=true` or `aliveParticles>0`. |");
        sb.AppendLine("| `snapshots/renderers_compact.jsonl` | Compact Active Renderer/material/mesh index with dictionary ids for secondary candidate evidence. |");
        sb.AppendLine("| `snapshots/renderers.jsonl` | Full Active Renderer/material/mesh index, exported by default for one-capture attribution. |");
        sb.AppendLine("| `snapshots/cameras.json` | Camera, culling mask, target, URP additional camera data, camera stack, and post-processing snapshot. |");
        sb.AppendLine("| `snapshots/renderfeatures.json` | Current render pipeline asset, renderer data, renderer feature names, active state, and material/shader references. |");
        sb.AppendLine("| `snapshots/runtime_player_snapshot.json` | Linked-capture only. One-shot remote Player cameras/renderers/UI Graphics/particles inventory for attribution. It is not GPU/CPU timing evidence. |");
        sb.AppendLine("| `dictionaries/*.json` | Repeated path/material/shader/texture strings indexed by stable ids used by compact evidence rows. |");
        sb.AppendLine("| `human/FrameDebug.md` | Human-readable fallback. |");
        sb.AppendLine();
        sb.AppendLine("## Important Interpretation Rules");
        sb.AppendLine();
        sb.AppendLine("- Frame Debugger events are draw-order evidence, not CPU/GPU timing evidence.");
        sb.AppendLine("- If `linked_capture.json` exists, this export belongs to a remote Player / RenderDoc linked workflow. Treat it as the same frozen Player state, then still use event/pass matching confidence before claiming exact one-to-one event identity.");
        sb.AppendLine("- `timingAvailable` is false for this export. `costRankingBasis=draw_count_only`; do not conclude GPU time, CPU time, bandwidth cost, or hotspot severity from this data alone.");
        sb.AppendLine("- Evidence confidence values: `direct`, `inferred_high`, `inferred_medium`, `inferred_low`, `snapshot_only`, `expected_missing`, `stage_only`, `render_feature_stage_only`, `data_gap`.");
        sb.AppendLine("- `direct` means the event row itself has object/material/shader evidence. `inferred_high` means runtime order or strong path/material/shader evidence matched. `inferred_medium` means partial but useful scene evidence. `inferred_low` means stage-level or weak material/shader inference. `snapshot_only` means the object exists in scene data but is not mapped to a frame event.");
        sb.AppendLine("- If `frameDebuggerGameObjectPath` or `frameDebuggerRendererPath` is present, it came from Unity internal FrameDebuggerUtility object lookup and should be treated as stronger than renderer snapshot candidate matching.");
        sb.AppendLine("- If direct FrameDebugger GameObject/Renderer fields are empty, do not automatically treat it as exporter failure. Unity only exposes a highlightable object when the selected draw event corresponds to a single GameObject. Batched events such as `SRPBatch` and `Canvas.RenderSubBatch` can represent multiple renderers/graphics, so they often require `FrameDebuggerEventData` mesh details, UI batch candidates, UI Details Profiler data, or scene renderer snapshots.");
        sb.AppendLine("- For UI batch object lists, Unity's UI Details Profiler is the stronger source when available because it has Canvas batch GameObject columns. This Frame Debugger export still marks `Canvas.RenderSubBatch` attribution as heuristic unless direct UI profiler batch data is present.");
        sb.AppendLine("- `unresolvedCategory` is filled only when an event lacks both direct attribution and candidate attribution. For categorized attributed events, read `attributionKind`, `attributionSource`, `confidence`, and `isDirectObjectMissingExpected` instead.");
        sb.AppendLine("- `stage` is parsed from the event path and is useful for routing: opaque, transparent, shadow, postprocess, UI, and custom render features.");
        sb.AppendLine("- `shaderName` may come from Unity's internal event data or from project material resolution. Prefer rows that also include `materialAssetPath` and `shaderAssetPath`.");
        sb.AppendLine("- If a field is empty in Frame Debugger raw data, use resolved project fields only as a best-effort inference from the event object.");
        sb.AppendLine("- When `rendererMaterials` contains multiple entries, `materialName` is selected by `meshSubset` if available; otherwise it is a best-effort first non-null material.");
        sb.AppendLine("- `rawEventFields` and `rawDataFieldsCompact` dump useful reflected fields and properties from Unity internals. Use them when normalized columns miss a Unity-version-specific field.");
        sb.AppendLine("- Root `ai_events_raw.jsonl` intentionally omits full `rawDataFields`. Read `debug/ai_events_raw_full.jsonl` only when compact fields are insufficient.");
        sb.AppendLine("- `directMeshName` is direct event/object mesh evidence. `frameDebuggerDetailMeshNames` / `frameDebuggerMeshInstanceIds` are extracted from Unity's internal `FrameDebuggerEventData` detail fields. Do not mix these evidence levels.");
        sb.AppendLine("- Mesh detail coverage must be judged with `meshExpectedEvents`, `meshExpectedButMissingDetailMeshes`, `meshNotExpectedEvents`, `srpBatchMeshExpectedEvents`, `srpBatchMeshDetailCoveredEvents`, and `meshEventDirectRendererCoveredEvents`. Do not treat clear/postprocess/UI/renderfeature/procedural events as missing mesh-detail failures.");
        sb.AppendLine("- Baseline direct attribution regressions should be interpreted with normalized rates: `directAttributionRateExcludingBatched`, `directAttributionRateForNonBatchedMesh`, `uiCandidateCoverageRate`, and `srpMeshCoverageRate`.");
        sb.AppendLine("- Do not use `eventsWithoutMaterial`, `eventsWithoutShader`, or `eventsWithoutDirectAttribution` as primary production findings. Prefer `uiCandidateCoverageRate`, `srpMeshCoverageRate`, `directAttributionRateExcludingBatched`, particle direct counts, and split mesh gaps such as `ordinaryMeshExpectedButMissingDetailMeshes`.");
        sb.AppendLine("- `scene snapshot` files are scene inventory. They are not current-frame cost evidence unless linked from `ai_event_evidence.jsonl`/`ai_event_analysis.jsonl` or marked `mappedToFrameEvent=true`.");
        sb.AppendLine("- For `Canvas.RenderSubBatch`, use `ai_event_evidence.jsonl`, `ai_event_analysis.jsonl`, and `ai_ui_batches.jsonl` as heuristic reverse attribution. It is stronger than a scene snapshot but still not as strong as a direct event object reference.");
        sb.AppendLine("- `materialProperties` is a project-side material snapshot generated from shader properties. It may be long, but it is usually the best clue for textures, toggles, render variants, and per-material differences.");
        sb.AppendLine("- `shaderPassSummary` is parsed from the shader source when available. Use it to inspect `LightMode`, `Cull`, `Blend`, `ZWrite`, `ZTest`, `ColorMask`, and stencil-related pass state.");
        sb.AppendLine("- For setpass analysis, focus on `materialName`, `shaderName`, `passName`, `lightMode`, `renderQueue`, `batchBreakCause`, `meshSubset`, and repeated adjacent events.");
        sb.AppendLine("- For transparent sorting analysis, inspect `stage=DrawTransparentObjects`, `renderQueue`, `passName`, `lightMode`, culling/blend state in the shader file, and object/material order.");
        sb.AppendLine("- Do not assume all transparent events are hair. Confirm by material/shader/object name and project asset path.");
        sb.AppendLine();
        sb.AppendLine("## Project Investigation");
        sb.AppendLine();
        sb.AppendLine("After identifying suspicious material or shader rows, search the project paths from `materialAssetPath`, `shaderAssetPath`, `meshAssetPath`, and `gameObjectPath`.");
        sb.AppendLine("When reporting a problem, include concrete event indexes, object names, material paths, shader paths, pass/lightmode, and the exact reason the frame evidence points there.");
        sb.AppendLine("Report format must separate `proven issues` from `insufficient-data / candidate attribution` issues. Do not mix candidate UI snapshot evidence into proven event-level claims.");
        sb.AppendLine();
        sb.AppendLine("## Capture Metadata");
        sb.AppendLine();
        sb.AppendLine("- Unity version: `" + EscapeMd(Application.unityVersion) + "`");
        sb.AppendLine("- Scene: `" + EscapeMd(GetCaptureSceneNameForMetadata()) + "`");
        sb.AppendLine("- Scene source: `" + EscapeMd(GetCaptureSceneSourceForMetadata()) + "`");
        if (_linkedCaptureContext != null)
            sb.AppendLine("- Editor scene at export: `" + EscapeMd(_linkedCaptureContext.editorSceneAtExport) + "` (not the remote captured scene)");
        sb.AppendLine("- Capture time: `" + EscapeMd(_captureTimestamp) + "`");
        sb.AppendLine("- Events: `" + (_capturedEvents != null ? _capturedEvents.Count.ToString(CultureInfo.InvariantCulture) : "0") + "`");
        return sb.ToString();
    }

    private string BuildAiDigestJson()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-summary/v2", true);
        WriteJsonProperty(sb, "purpose", "First-read compact index for Unity Frame Debugger AI analysis.", true);
        WriteJsonProperty(sb, "linkedCapture", _linkedCaptureContext != null ? "linked_capture.json" : "", true);
        WriteJsonProperty(sb, "linkedAnalysisGuide", _linkedCaptureContext != null ? "AI_LINKED_ANALYSIS_GUIDE.md" : "", true);
        WriteJsonProperty(sb, "dataQuality", "ai_data_quality.json", true);
        WriteJsonProperty(sb, "analysisBlockingPolicy", "ai_analysis_blocking_policy.json", true);
        WriteJsonProperty(sb, "runtimeResolutionSnapshot", "ai_runtime_resolution_snapshot.json", true);
        WriteJsonProperty(sb, "transparentSubmissionSnapshot", "ai_transparent_submission_snapshot.json", true);
        WriteJsonProperty(sb, "baselineComparison", "ai_baseline_comparison.json", true);
        WriteJsonProperty(sb, "exportTimings", "ai_export_timings.json", true);
        WriteJsonProperty(sb, "eventEvidence", "ai_event_evidence.jsonl", true);
        WriteJsonProperty(sb, "eventAnalysis", "ai_event_analysis.jsonl", true);
        WriteJsonProperty(sb, "objectInventory", "ai_object_inventory.json", true);
        WriteJsonProperty(sb, "eventAttributionIndex", "ai_event_attribution_index.jsonl", true);
        WriteJsonProperty(sb, "rawEvents", "ai_events_raw.jsonl", true);
        WriteJsonProperty(sb, "rawEventsFullDebug", "debug/ai_events_raw_full.jsonl", true);
        WriteJsonProperty(sb, "frameEventDataDiagnostics", "ai_frameeventdata_diagnostics.json", true);
        WriteJsonProperty(sb, "directObjectDiagnostics", "ai_direct_object_diagnostics.json", true);
        WriteJsonProperty(sb, "directObjectDiagnosticsFullDebug", "debug/ai_direct_object_diagnostics_full.json", true);
        WriteJsonProperty(sb, "uiSnapshotJsonl", "snapshots/ui_graphics.jsonl", true);
        WriteJsonProperty(sb, "uiBatchCandidates", "ai_ui_batches.jsonl", true);
        WriteJsonProperty(sb, "uiBatchDetails", "ai_ui_batch_details.jsonl", true);
        WriteJsonProperty(sb, "srpBatchCandidates", "ai_srp_batch_candidates.jsonl", true);
        WriteJsonProperty(sb, "srpBatchDiagnostics", "ai_srp_batch_diagnostics.json", true);
        WriteJsonProperty(sb, "resourceFingerprints", "ai_resource_fingerprints.json", true);
        WriteJsonProperty(sb, "unityRenderDocCorrelationSeed", "ai_unity_renderdoc_correlation_seed.json", true);
        WriteJsonProperty(sb, "deepEventSamplingPlan", "ai_deep_event_sampling_plan.json", true);
        WriteJsonProperty(sb, "reflectionInventory", "ai_reflection_inventory.json", true);
        WriteJsonProperty(sb, "particleSnapshotJsonl", "snapshots/particles.jsonl", true);
        WriteJsonProperty(sb, "rendererCompactSnapshotJsonl", "snapshots/renderers_compact.jsonl", true);
        WriteJsonProperty(sb, "rendererSnapshotJsonl", ExportFullRendererSnapshotByDefault ? "snapshots/renderers.jsonl" : "optional_debug_disabled_by_default", true);
        WriteJsonProperty(sb, "cameraSnapshot", "snapshots/cameras.json", true);
        WriteJsonProperty(sb, "renderFeatureSnapshot", "snapshots/renderfeatures.json", true);
        WriteJsonProperty(sb, "runtimePlayerSnapshot", _linkedCaptureContext != null ? "snapshots/runtime_player_snapshot.json" : "", true);
        WriteJsonProperty(sb, "runtimeTextureThumbnails", _linkedCaptureContext != null ? "snapshots/runtime_texture_thumbnails.json" : "", true);
        WriteJsonProperty(sb, "stringDictionary", "dictionaries/strings.json", true);
        WriteJsonProperty(sb, "materialDictionary", "dictionaries/materials.json", true);
        WriteJsonProperty(sb, "shaderDictionary", "dictionaries/shaders.json", true);
        WriteJsonProperty(sb, "textureDictionary", "dictionaries/textures.json", true);
        WriteJsonProperty(sb, "humanReadable", "human/FrameDebug.md", true);
        WriteJsonProperty(sb, "humanReadableOnly", true, true);
        WriteJsonProperty(sb, "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
        WriteJsonProperty(sb, "unityVersion", Application.unityVersion, true);
        WriteJsonProperty(sb, "scene", GetCaptureSceneNameForMetadata(), true);
        WriteJsonProperty(sb, "sceneSource", GetCaptureSceneSourceForMetadata(), true);
        WriteJsonProperty(sb, "editorSceneAtExport", _linkedCaptureContext != null ? _linkedCaptureContext.editorSceneAtExport : "", true);
        WriteJsonProperty(sb, "eventCount", events.Count, true);
        AppendTimingMetadataObject(sb, true);
        AppendDataQualityObject(sb, events, true);
        AppendRuntimeSnapshotQualityObject(sb, true);
        AppendResourceFingerprintQualityGateObject(sb, events, true);
        AppendAnalysisBlockingPolicyObject(sb, events, true);
        AppendIssueTable(sb, "topDirectDrawObjects", BuildTopDirectDrawObjects(events), true);
        AppendIssueTable(sb, "topInferredUiBatches", BuildTopInferredUiBatches(events), true);
        AppendTopUiBatchCandidateTable(sb, "topUiBatchCandidates", events, true);
        AppendParticleCandidateSummaryObject(sb, "particleCandidateSummary", events, true);
        AppendTopParticleCandidateTable(sb, "topActiveParticlesInFrame", events, true);
        AppendTopParticleRootTable(sb, "topActiveParticleRoots", events, true);
        AppendTopParticleMaterialTable(sb, "topMappedParticleMaterials", events, true);
        AppendIssueTable(sb, "topUnresolvedStages", BuildTopUnresolvedStages(events), true);
        AppendIssueTable(sb, "topUnresolvedEventKinds", BuildTopUnresolvedStages(events), true);
        AppendIssueTable(sb, "topDataGaps", BuildTopDataGaps(events), true);
        AppendAggregateArray(sb, "topStages", BuildStageStats(events), true);
        AppendAggregateArray(sb, "topCameraStages", BuildCameraStageStats(events), true);
        AppendAggregateArray(sb, "topEventTypes", BuildSimpleStats(events, e => e.typeName), true);
        AppendAggregateArray(sb, "topPasses", BuildSimpleStats(events, e => BestPassName(e)), true);
        AppendAggregateArray(sb, "topShaders", BuildSimpleStats(events, e => BestShaderName(e)), true);
        AppendAggregateArray(sb, "topMaterials", BuildSimpleStats(events, e => e.materialName), true);
        AppendAggregateArray(sb, "topObjects", BuildSimpleStats(events, e => e.gameObjectName), true);
        AppendAggregateArray(sb, "topPrefabRoots", BuildSimpleStats(events, e => GetPrefabRootFromPath(e.gameObjectPath)), true);
        AppendAggregateArray(sb, "topBatchBreakCauses", BuildSimpleStats(events, e => e.batchBreakCause), true);
        AppendSuspiciousHighlights(sb, events);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiEventsRawJsonl()
    {
        return BuildAiEventsRawJsonl(false);
    }

    private string BuildAiEventsRawFullJsonl()
    {
        return BuildAiEventsRawJsonl(true);
    }

    private string BuildAiEventsRawJsonl(bool includeFullRawData)
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        var srpBatchMap = BuildSrpBatchCandidateMap(events);
        var particleMap = BuildParticleCandidateMap(events);
        var sb = new StringBuilder(16384);
        foreach (var evt in events)
        {
            uiBatchMap.TryGetValue(evt.index, out var uiBatch);
            srpBatchMap.TryGetValue(evt.index, out var srpBatch);
            particleMap.TryGetValue(evt.index, out var particleCandidates);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
            AppendJsonPropertyInline(sb, "eventName", evt.eventName, true);
            AppendJsonPropertyInline(sb, "camera", ParseCameraName(evt.eventName), true);
            AppendJsonPropertyInline(sb, "stage", ParseStageName(evt.eventName), true);
            AppendJsonPropertyInline(sb, "attributionLevel", GetAttributionLevel(evt), true);
            AppendJsonPropertyInline(sb, "unresolvedCategory", ClassifyUnresolvedEvent(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "attributionKind", GetAttributionKind(evt), true);
            AppendJsonPropertyInline(sb, "attributionSource", GetAttributionSource(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "isDirectObjectMissingExpected", IsDirectObjectMissingExpected(evt), true);
            AppendJsonPropertyInline(sb, "hasTiming", false, true);
            AppendJsonPropertyInline(sb, "costRankSource", "draw_count_only", true);
            AppendJsonPropertyInline(sb, "timingAvailable", false, true);
            AppendJsonPropertyInline(sb, "costRankingBasis", "draw_count_only", true);
            AppendJsonPropertyInline(sb, "frameEventDataAttempted", evt.frameEventDataAttempted, true);
            AppendJsonPropertyInline(sb, "frameEventDataSuccess", evt.frameEventDataSuccess, true);
            AppendJsonPropertyInline(sb, "limitBefore", evt.frameEventDataLimitBefore, true);
            AppendJsonPropertyInline(sb, "limitSetTo", evt.frameEventDataLimitSetTo, true);
            AppendJsonPropertyInline(sb, "limitAfter", evt.frameEventDataLimitAfter, true);
            AppendJsonPropertyInline(sb, "limitAfterRestore", evt.frameEventDataLimitAfterRestore, true);
            AppendJsonPropertyInline(sb, "requestedEventIndex", evt.requestedEventIndex, true);
            AppendJsonPropertyInline(sb, "filledFrameEventIndex", evt.filledFrameEventIndex, true);
            AppendJsonPropertyInline(sb, "frameEventDataFailureReason", evt.frameEventDataFailureReason, true);
            AppendJsonPropertyInline(sb, "frameEventDataMethod", evt.frameEventDataMethod, true);
            AppendJsonPropertyInline(sb, "isUiSubBatch", IsUiSubBatchEvent(evt), true);
            AppendJsonPropertyInline(sb, "prefabRoot", GetPrefabRootFromPath(evt.gameObjectPath), true);
            AppendJsonPropertyInline(sb, "directObject", evt.gameObjectPath, true);
            AppendJsonPropertyInline(sb, "directMaterial", evt.materialName, true);
            AppendJsonPropertyInline(sb, "directShader", BestShaderName(evt), true);
            AppendJsonPropertyInline(sb, "type", evt.typeName, true);
            AppendJsonPropertyInline(sb, "gameObjectName", evt.gameObjectName, true);
            AppendJsonPropertyInline(sb, "gameObjectPath", evt.gameObjectPath, true);
            AppendJsonPropertyInline(sb, "rendererType", evt.rendererType, true);
            AppendJsonPropertyInline(sb, "rendererSortingLayerId", evt.rendererSortingLayerId, true);
            AppendJsonPropertyInline(sb, "rendererSortingOrder", evt.rendererSortingOrder, true);
            AppendJsonPropertyInline(sb, "rendererPriority", evt.rendererPriority, true);
            AppendJsonPropertyInline(sb, "rendererMaterials", evt.rendererMaterials, true);
            AppendJsonPropertyInline(sb, "frameDebuggerGameObjectName", evt.frameDebuggerGameObjectName, true);
            AppendJsonPropertyInline(sb, "frameDebuggerGameObjectPath", evt.frameDebuggerGameObjectPath, true);
            AppendJsonPropertyInline(sb, "frameDebuggerGameObjectInstanceId", evt.frameDebuggerGameObjectInstanceId, true);
            AppendJsonPropertyInline(sb, "frameDebuggerRendererType", evt.frameDebuggerRendererType, true);
            AppendJsonPropertyInline(sb, "frameDebuggerRendererPath", evt.frameDebuggerRendererPath, true);
            AppendJsonPropertyInline(sb, "frameDebuggerRendererInstanceId", evt.frameDebuggerRendererInstanceId, true);
            AppendJsonPropertyInline(sb, "directMeshName", evt.meshName, true);
            AppendJsonPropertyInline(sb, "directMeshPath", evt.meshAssetPath, true);
            AppendJsonPropertyInline(sb, "meshName", evt.meshName, true);
            AppendJsonPropertyInline(sb, "meshAssetPath", evt.meshAssetPath, true);
            AppendJsonPropertyInline(sb, "meshGuid", evt.meshGuid, true);
            AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", evt.detailMeshInstanceIds.ToArray(), true);
            AppendStringArrayInline(sb, "detailMeshNames", evt.detailMeshNames.ToArray(), true);
            AppendStringArrayInline(sb, "detailMeshAssetPaths", evt.detailMeshAssetPaths.ToArray(), true);
            AppendStringArrayInline(sb, "detailMeshInstanceIds", evt.detailMeshInstanceIds.ToArray(), true);
            AppendJsonPropertyInline(sb, "meshSubset", evt.meshSubset, true);
            AppendJsonPropertyInline(sb, "materialName", evt.materialName, true);
            AppendJsonPropertyInline(sb, "materialAssetPath", evt.materialAssetPath, true);
            AppendJsonPropertyInline(sb, "materialGuid", evt.materialGuid, true);
            AppendJsonPropertyInline(sb, "materialRenderQueue", evt.materialRenderQueue, true);
            AppendJsonPropertyInline(sb, "materialInstancingEnabled", evt.materialInstancingEnabled, true);
            AppendJsonPropertyInline(sb, "materialKeywords", evt.materialKeywords, true);
            AppendJsonPropertyInline(sb, "materialTextures", evt.materialTextures, true);
            AppendJsonPropertyInline(sb, "materialProperties", evt.materialProperties, true);
            AppendJsonPropertyInline(sb, "shaderName", BestShaderName(evt), true);
            AppendJsonPropertyInline(sb, "shaderAssetPath", evt.resolvedShaderAssetPath, true);
            AppendJsonPropertyInline(sb, "shaderGuid", evt.resolvedShaderGuid, true);
            AppendJsonPropertyInline(sb, "shaderPassCount", evt.resolvedShaderPassCount, true);
            AppendJsonPropertyInline(sb, "shaderRenderQueue", evt.resolvedShaderRenderQueue, true);
            AppendJsonPropertyInline(sb, "shaderPassSummary", evt.resolvedShaderPassSummary, true);
            AppendJsonPropertyInline(sb, "renderStateSummary", ExtractRenderStateForEvent(evt), true);
            AppendJsonPropertyInline(sb, "passName", evt.passName, true);
            AppendJsonPropertyInline(sb, "lightMode", evt.passLightMode, true);
            AppendJsonPropertyInline(sb, "passIndex", evt.passIndex, true);
            AppendJsonPropertyInline(sb, "renderTargetName", evt.renderTargetName, true);
            AppendJsonPropertyInline(sb, "renderTargetWidth", evt.renderTargetWidth, true);
            AppendJsonPropertyInline(sb, "renderTargetHeight", evt.renderTargetHeight, true);
            AppendJsonPropertyInline(sb, "vertexCount", evt.vertexCount, true);
            AppendJsonPropertyInline(sb, "indexCount", evt.indexCount, true);
            AppendJsonPropertyInline(sb, "instanceCount", evt.instanceCount, true);
            AppendJsonPropertyInline(sb, "drawCallCount", evt.drawCallCount, true);
            AppendJsonPropertyInline(sb, "shaderKeywords", evt.shaderKeywords, true);
            AppendJsonPropertyInline(sb, "batchBreakCause", evt.batchBreakCause, true);
            AppendDictionaryInline(sb, "extraFields", evt.extraFields, true);
            AppendDictionaryInline(sb, "rawEventFields", evt.rawEventFields, true);
            AppendDictionaryInline(sb, "rawDataFieldsCompact", BuildCompactRawDataFields(evt.rawDataFields), includeFullRawData);
            if (includeFullRawData)
                AppendDictionaryInline(sb, "rawDataFields", evt.rawDataFields, false);
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildCompactRawDataFields(Dictionary<string, string> rawDataFields)
    {
        var compact = new Dictionary<string, string>();
        if (rawDataFields == null)
            return compact;

        foreach (var kv in rawDataFields)
        {
            if (IsDefaultRawDataValue(kv.Value))
                continue;
            compact[kv.Key] = kv.Value;
        }
        return compact;
    }

    private static bool IsDefaultRawDataValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        string v = value.Trim();
        return v == "0" ||
               v == "-1" ||
               string.Equals(v, "False", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(v, "None", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(v, "null", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildAiFrameEventDataDiagnosticsJson()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        int attempted = events.Count(e => e.frameEventDataAttempted);
        int success = events.Count(e => e.frameEventDataSuccess);
        int withRawData = events.Count(e => e.rawDataFields != null && e.rawDataFields.Count > 0);
        int withDetailMeshes = events.Count(HasFrameDebuggerDetailMeshes);
        int limitMismatch = events.Count(e => e.frameEventDataAttempted && e.frameEventDataLimitAfter != e.frameEventDataLimitSetTo);
        int restoreMismatch = events.Count(e => e.frameEventDataAttempted && e.frameEventDataLimitAfterRestore != e.frameEventDataLimitBefore);

        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-frameeventdata-diagnostics/v1", true);
        WriteJsonProperty(sb, "purpose", "Per-event diagnostics for Unity internal GetFrameEventData reflection calls.", true);
        WriteJsonProperty(sb, "getFrameEventData", s_getFrameEventData != null ? s_getFrameEventData.ToString() : "", true);
        WriteJsonProperty(sb, "limitProperty", s_limitProp != null ? s_limitProp.ToString() : "", true);
        WriteJsonProperty(sb, "limitField", s_limitField != null ? s_limitField.ToString() : "", true);
        WriteJsonProperty(sb, "indexBaseNote", "requestedEventIndex is 1-based export/UI numbering; filledFrameEventIndex may be Unity internal 0-based numbering.", true);
        WriteJsonProperty(sb, "eventCount", events.Count, true);
        WriteJsonProperty(sb, "attemptedCount", attempted, true);
        WriteJsonProperty(sb, "successCount", success, true);
        WriteJsonProperty(sb, "eventsWithRawDataFields", withRawData, true);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerDetailMeshes", withDetailMeshes, true);
        WriteJsonProperty(sb, "limitAfterMismatchCount", limitMismatch, true);
        WriteJsonProperty(sb, "limitRestoreMismatchCount", restoreMismatch, true);
        sb.AppendLine("  \"failureReasons\": [");
        var failures = events
            .Where(e => !e.frameEventDataSuccess)
            .GroupBy(e => string.IsNullOrEmpty(e.frameEventDataFailureReason) ? "unknown" : e.frameEventDataFailureReason)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();
        for (int i = 0; i < failures.Count; i++)
        {
            var row = failures[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "reason", row.Key, true);
            AppendJsonPropertyInline(sb, "count", row.Count(), true);
            AppendStringArrayInline(sb, "eventIndexes", row.Select(e => e.requestedEventIndex.ToString(CultureInfo.InvariantCulture)).Take(24).ToArray(), false);
            sb.Append(i + 1 < failures.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");
        sb.AppendLine("  \"events\": [");
        for (int i = 0; i < events.Count; i++)
        {
            AppendFrameEventDataDiagnosticObject(sb, events[i], "    ");
            sb.Append(i + 1 < events.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendFrameEventDataDiagnosticObject(StringBuilder sb, FrameEventInfo evt, string pad)
    {
        sb.Append(pad).Append("{");
        AppendJsonPropertyInline(sb, "eventIndex", evt != null ? evt.index + 1 : -1, true);
        AppendJsonPropertyInline(sb, "eventName", evt != null ? evt.eventName : "", true);
        AppendJsonPropertyInline(sb, "eventType", evt != null ? evt.typeName : "", true);
        AppendJsonPropertyInline(sb, "unresolvedCategory", evt != null ? ClassifyUnresolvedEvent(evt) : "Unknown", true);
        AppendJsonPropertyInline(sb, "frameEventDataAttempted", evt != null && evt.frameEventDataAttempted, true);
        AppendJsonPropertyInline(sb, "frameEventDataSuccess", evt != null && evt.frameEventDataSuccess, true);
        AppendJsonPropertyInline(sb, "limitBefore", evt != null ? evt.frameEventDataLimitBefore : -1, true);
        AppendJsonPropertyInline(sb, "limitSetTo", evt != null ? evt.frameEventDataLimitSetTo : -1, true);
        AppendJsonPropertyInline(sb, "limitAfter", evt != null ? evt.frameEventDataLimitAfter : -1, true);
        AppendJsonPropertyInline(sb, "limitAfterRestore", evt != null ? evt.frameEventDataLimitAfterRestore : -1, true);
        AppendJsonPropertyInline(sb, "requestedEventIndex", evt != null ? evt.requestedEventIndex : -1, true);
        AppendJsonPropertyInline(sb, "filledFrameEventIndex", evt != null ? evt.filledFrameEventIndex : -1, true);
        AppendJsonPropertyInline(sb, "rawDataFieldCount", evt != null && evt.rawDataFields != null ? evt.rawDataFields.Count : 0, true);
        AppendJsonPropertyInline(sb, "detailMeshNameCount", evt != null && evt.detailMeshNames != null ? evt.detailMeshNames.Count : 0, true);
        AppendJsonPropertyInline(sb, "detailMeshInstanceIdCount", evt != null && evt.detailMeshInstanceIds != null ? evt.detailMeshInstanceIds.Count : 0, true);
        AppendJsonPropertyInline(sb, "failureReason", evt != null ? evt.frameEventDataFailureReason : "", false);
        sb.Append("}");
    }

    private string BuildAiEventEvidenceJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        var srpBatchMap = BuildSrpBatchCandidateMap(events);
        var particleCandidateMap = BuildParticleCandidateMap(events);
        var renderFeatures = GetRenderFeatureSnapshots();
        var sb = new StringBuilder(16384);

        foreach (var evt in events)
        {
            uiBatchMap.TryGetValue(evt.index, out var uiBatch);
            srpBatchMap.TryGetValue(evt.index, out var srpBatch);
            particleCandidateMap.TryGetValue(evt.index, out var particleCandidates);
            string confidence = GetEvidenceConfidence(evt, uiBatch, srpBatch, particleCandidates);

            sb.Append("{");
            AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
            AppendJsonPropertyInline(sb, "camera", ParseCameraName(evt.eventName), true);
            AppendJsonPropertyInline(sb, "stage", ParseStageName(evt.eventName), true);
            AppendJsonPropertyInline(sb, "eventType", evt.typeName, true);
            AppendJsonPropertyInline(sb, "eventNameId", StableStringId("s", evt.eventName), true);
            AppendJsonPropertyInline(sb, "directMeshName", evt.meshName, true);
            AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", evt.detailMeshInstanceIds.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshNames", GetEventMeshNames(evt).ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshIds", evt.detailMeshInstanceIds.ToArray(), true);
            AppendStringArrayInline(sb, "rendererCandidateMeshNames", srpBatch != null ? srpBatch.renderers.Select(r => r.meshName).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(8).ToArray() : new string[0], true);
            AppendJsonPropertyInline(sb, "lightMode", evt.passLightMode, true);
            AppendJsonPropertyInline(sb, "batchBreakCause", evt.batchBreakCause, true);
            AppendJsonPropertyInline(sb, "confidence", confidence, true);
            AppendJsonPropertyInline(sb, "reason", BuildConfidenceReason(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "unresolvedCategory", ClassifyUnresolvedEvent(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "attributionKind", GetAttributionKind(evt), true);
            AppendJsonPropertyInline(sb, "attributionSource", GetAttributionSource(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "isDirectObjectMissingExpected", IsDirectObjectMissingExpected(evt), true);
            AppendJsonPropertyInline(sb, "timingAvailable", false, true);
            AppendJsonPropertyInline(sb, "costRankingBasis", "draw_count_only", true);
            AppendDirectEvidenceCompact(sb, evt, true);
            AppendCandidateEvidenceCompact(sb, evt, uiBatch, srpBatch, particleCandidates, renderFeatures, true);
            AppendStringArrayInline(sb, "limitations", BuildEventLimitations(evt, uiBatch, srpBatch, particleCandidates), false);
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private string BuildAiEventAnalysisJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        var srpBatchMap = BuildSrpBatchCandidateMap(events);
        var particleCandidateMap = BuildParticleCandidateMap(events);
        var renderFeatures = GetRenderFeatureSnapshots();
        var sb = new StringBuilder(32768);

        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            uiBatchMap.TryGetValue(evt.index, out var uiBatch);
            srpBatchMap.TryGetValue(evt.index, out var srpBatch);
            particleCandidateMap.TryGetValue(evt.index, out var particleCandidates);
            AppendEventAnalysisObject(sb, evt, uiBatch, srpBatch, particleCandidates, renderFeatures, "");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendEventAnalysisObject(StringBuilder sb, FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates, List<RenderFeatureSnapshot> renderFeatures, string pad)
    {
        string confidence = GetEvidenceConfidence(evt, uiBatch, srpBatch, particleCandidates);
        sb.Append(pad).Append("{");
        AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
        AppendJsonPropertyInline(sb, "camera", ParseCameraName(evt.eventName), true);
        AppendJsonPropertyInline(sb, "stage", ParseStageName(evt.eventName), true);
        AppendJsonPropertyInline(sb, "eventType", evt.typeName, true);
        AppendJsonPropertyInline(sb, "eventName", evt.eventName, true);
        AppendJsonPropertyInline(sb, "confidence", confidence, true);
        AppendJsonPropertyInline(sb, "reason", BuildConfidenceReason(evt, uiBatch, srpBatch, particleCandidates), true);
        AppendJsonPropertyInline(sb, "unresolvedCategory", ClassifyUnresolvedEvent(evt, uiBatch, srpBatch, particleCandidates), true);
        AppendJsonPropertyInline(sb, "attributionKind", GetAttributionKind(evt), true);
        AppendJsonPropertyInline(sb, "attributionSource", GetAttributionSource(evt, uiBatch, srpBatch, particleCandidates), true);
        AppendJsonPropertyInline(sb, "isDirectObjectMissingExpected", IsDirectObjectMissingExpected(evt), true);
        AppendJsonPropertyInline(sb, "timingAvailable", false, true);
        AppendJsonPropertyInline(sb, "costRankingBasis", "draw_count_only", true);
        AppendJsonPropertyInline(sb, "directMeshName", evt.meshName, true);
        AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames.ToArray(), true);
        AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", evt.detailMeshInstanceIds.ToArray(), true);
        AppendStringArrayInline(sb, "frameDebuggerMeshNames", GetEventMeshNames(evt).ToArray(), true);
        AppendStringArrayInline(sb, "frameDebuggerMeshIds", evt.detailMeshInstanceIds.ToArray(), true);
        AppendStringArrayInline(sb, "rendererCandidateMeshNames", srpBatch != null ? srpBatch.renderers.Select(r => r.meshName).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(8).ToArray() : new string[0], true);
        AppendDirectAttributionObjectInline(sb, evt, true);
        AppendCandidateAttributionsInline(sb, evt, uiBatch, srpBatch, particleCandidates, renderFeatures, true);
        AppendStringArrayInline(sb, "limitations", BuildEventLimitations(evt, uiBatch, srpBatch, particleCandidates), true);
        AppendStringArrayInline(sb, "cannotConclude", GetFrameDebuggerCannotConclude(), true);
        AppendJsonPropertyInline(sb, "renderStateSummary", ExtractRenderStateForEvent(evt), true);
        AppendJsonPropertyInline(sb, "passName", evt.passName, true);
        AppendJsonPropertyInline(sb, "lightMode", evt.passLightMode, true);
        AppendJsonPropertyInline(sb, "batchBreakCause", evt.batchBreakCause, false);
        sb.Append("}");
    }

    private static void AppendDirectEvidenceCompact(StringBuilder sb, FrameEventInfo evt, bool trailingComma)
    {
        sb.Append("\"direct\": ");
        if (!HasDirectAttribution(evt))
        {
            sb.Append("null");
            if (trailingComma) sb.Append(", ");
            return;
        }

        sb.Append("{");
        AppendJsonPropertyInline(sb, "objectName", evt.gameObjectName, true);
        AppendJsonPropertyInline(sb, "objectPathId", StableStringId("s", !string.IsNullOrEmpty(evt.gameObjectPath) ? evt.gameObjectPath : evt.gameObjectName), true);
        AppendJsonPropertyInline(sb, "frameDebuggerGameObjectPath", evt.frameDebuggerGameObjectPath, true);
        AppendJsonPropertyInline(sb, "frameDebuggerRendererPath", evt.frameDebuggerRendererPath, true);
        AppendJsonPropertyInline(sb, "materialName", evt.materialName, true);
        AppendJsonPropertyInline(sb, "materialId", StableStringId("mat", !string.IsNullOrEmpty(evt.materialAssetPath) ? evt.materialAssetPath : evt.materialName), true);
        AppendJsonPropertyInline(sb, "shaderName", BestShaderName(evt), true);
        AppendJsonPropertyInline(sb, "shaderId", StableStringId("sh", !string.IsNullOrEmpty(evt.resolvedShaderAssetPath) ? evt.resolvedShaderAssetPath : BestShaderName(evt)), true);
        AppendJsonPropertyInline(sb, "meshName", evt.meshName, true);
        AppendJsonPropertyInline(sb, "meshId", StableStringId("s", !string.IsNullOrEmpty(evt.meshAssetPath) ? evt.meshAssetPath : evt.meshName), true);
        AppendJsonPropertyInline(sb, "directMeshName", evt.meshName, true);
        AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames.ToArray(), true);
        AppendStringArrayInline(sb, "frameDebuggerMeshNames", GetEventMeshNames(evt).ToArray(), false);
        sb.Append("}");
        if (trailingComma) sb.Append(", ");
    }

    private static void AppendDirectAttributionObjectInline(StringBuilder sb, FrameEventInfo evt, bool trailingComma)
    {
        sb.Append("\"directAttribution\": ");
        if (!HasDirectAttribution(evt))
        {
            sb.Append("null");
            if (trailingComma) sb.Append(", ");
            return;
        }

        sb.Append("{");
        AppendJsonPropertyInline(sb, "objectName", evt.gameObjectName, true);
        AppendJsonPropertyInline(sb, "objectPath", evt.gameObjectPath, true);
        AppendJsonPropertyInline(sb, "objectType", evt.objectType, true);
        AppendJsonPropertyInline(sb, "rendererType", evt.rendererType, true);
        AppendJsonPropertyInline(sb, "frameDebuggerGameObjectPath", evt.frameDebuggerGameObjectPath, true);
        AppendJsonPropertyInline(sb, "frameDebuggerRendererPath", evt.frameDebuggerRendererPath, true);
        AppendJsonPropertyInline(sb, "frameDebuggerRendererType", evt.frameDebuggerRendererType, true);
        AppendJsonPropertyInline(sb, "materialName", evt.materialName, true);
        AppendJsonPropertyInline(sb, "materialPath", evt.materialAssetPath, true);
        AppendJsonPropertyInline(sb, "shaderName", BestShaderName(evt), true);
        AppendJsonPropertyInline(sb, "shaderPath", evt.resolvedShaderAssetPath, true);
        AppendJsonPropertyInline(sb, "meshName", evt.meshName, true);
        AppendJsonPropertyInline(sb, "meshPath", evt.meshAssetPath, true);
        AppendJsonPropertyInline(sb, "directMeshName", evt.meshName, true);
        AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames.ToArray(), true);
        AppendStringArrayInline(sb, "frameDebuggerMeshNames", GetEventMeshNames(evt).ToArray(), true);
        AppendJsonPropertyInline(sb, "renderQueue", evt.materialRenderQueue, false);
        sb.Append("}");
        if (trailingComma) sb.Append(", ");
    }

    private static void AppendCandidateEvidenceCompact(StringBuilder sb, FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates, List<RenderFeatureSnapshot> renderFeatures, bool trailingComma)
    {
        sb.Append("\"candidates\": [");
        int count = 0;
        if (uiBatch != null)
        {
            AppendCommaIfNeeded(sb, ref count);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "type", "ui_batch", true);
            AppendJsonPropertyInline(sb, "confidence", uiBatch.confidence, true);
            AppendJsonPropertyInline(sb, "candidateId", uiBatch.id, true);
            AppendJsonPropertyInline(sb, "path", GetRepresentativeUiPath(uiBatch), true);
            AppendJsonPropertyInline(sb, "pathId", StableStringId("s", GetRepresentativeUiPath(uiBatch)), true);
            AppendJsonPropertyInline(sb, "materialName", uiBatch.materialName, true);
            AppendJsonPropertyInline(sb, "materialId", StableStringId("mat", !string.IsNullOrEmpty(uiBatch.materialPath) ? uiBatch.materialPath : uiBatch.materialName), true);
            AppendJsonPropertyInline(sb, "shaderName", uiBatch.shaderName, true);
            AppendJsonPropertyInline(sb, "shaderId", StableStringId("sh", uiBatch.shaderName), true);
            AppendJsonPropertyInline(sb, "textureName", uiBatch.textureName, true);
            AppendJsonPropertyInline(sb, "graphicCount", uiBatch.graphics.Count, true);
            AppendJsonPropertyInline(sb, "representativeScreenRect", uiBatch.graphics.Count > 0 ? uiBatch.graphics[0].screenRect : "", true);
            AppendJsonPropertyInline(sb, "representativeFingerprint", uiBatch.graphics.Count > 0 ? uiBatch.graphics[0].resourceFingerprint : "", true);
            AppendJsonPropertyInline(sb, "reason", uiBatch.reason, false);
            sb.Append("}");
        }

        if (particleCandidates != null)
        {
            foreach (var particle in particleCandidates.Take(3))
            {
                AppendCommaIfNeeded(sb, ref count);
                sb.Append("{");
                AppendJsonPropertyInline(sb, "type", "particle", true);
                AppendJsonPropertyInline(sb, "confidence", particle.confidence, true);
                AppendJsonPropertyInline(sb, "candidateId", particle.id, true);
                AppendJsonPropertyInline(sb, "source", particle.source, true);
                AppendJsonPropertyInline(sb, "path", particle.path, true);
                AppendJsonPropertyInline(sb, "pathId", StableStringId("s", particle.path), true);
                AppendJsonPropertyInline(sb, "materialName", particle.materialName, true);
                AppendJsonPropertyInline(sb, "materialId", StableStringId("mat", !string.IsNullOrEmpty(particle.materialPath) ? particle.materialPath : particle.materialName), true);
                AppendJsonPropertyInline(sb, "shaderName", particle.shaderName, true);
                AppendJsonPropertyInline(sb, "shaderId", StableStringId("sh", particle.shaderName), true);
                AppendJsonPropertyInline(sb, "textureName", particle.textureName, true);
                AppendJsonPropertyInline(sb, "textureId", StableStringId("tex", particle.textureName), true);
                AppendJsonPropertyInline(sb, "aliveParticles", particle.aliveParticles, true);
                AppendJsonPropertyInline(sb, "bounds", particle.bounds, true);
                AppendJsonPropertyInline(sb, "resourceFingerprint", particle.resourceFingerprint, true);
                AppendJsonPropertyInline(sb, "matchScore", particle.matchScore, true);
                AppendStringArrayInline(sb, "matchedBy", particle.matchedBy, false);
                sb.Append("}");
            }
        }

        if (srpBatch != null)
        {
            AppendCommaIfNeeded(sb, ref count);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "type", "srp_batch", true);
            AppendJsonPropertyInline(sb, "confidence", srpBatch.confidence, true);
            AppendJsonPropertyInline(sb, "eventIndex", srpBatch.eventIndex, true);
            AppendJsonPropertyInline(sb, "matchScore", srpBatch.matchScore, true);
            AppendStringArrayInline(sb, "matchedBy", srpBatch.matchedBy, true);
            AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", srpBatch.frameDebuggerDetailMeshNames.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", srpBatch.frameDebuggerMeshInstanceIds.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshNames", srpBatch.eventMeshNames.ToArray(), true);
            AppendJsonPropertyInline(sb, "rendererCandidateCount", srpBatch.renderers.Count, true);
            AppendStringArrayInline(sb, "rendererCandidateMeshNames", srpBatch.renderers.Select(r => r.meshName).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(8).ToArray(), true);
            AppendJsonPropertyInline(sb, "firstRendererPath", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].path : "", true);
            AppendJsonPropertyInline(sb, "firstRendererSource", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].source : "", true);
            AppendJsonPropertyInline(sb, "firstRendererFingerprint", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].resourceFingerprint : "", true);
            AppendJsonPropertyInline(sb, "reason", srpBatch.reason, false);
            sb.Append("}");
        }

        var renderFeature = FindRenderFeatureCandidate(evt, renderFeatures);
        if (renderFeature != null)
        {
            AppendCommaIfNeeded(sb, ref count);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "type", "render_feature", true);
            AppendJsonPropertyInline(sb, "confidence", "inferred_low", true);
            AppendJsonPropertyInline(sb, "name", renderFeature.featureName, true);
            AppendJsonPropertyInline(sb, "nameId", StableStringId("s", renderFeature.featureName), true);
            AppendJsonPropertyInline(sb, "assetPathId", StableStringId("s", renderFeature.featureAssetPath), true);
            AppendJsonPropertyInline(sb, "reason", "Stage-level match only, not exact render pass instance.", false);
            sb.Append("}");
        }

        sb.Append("]");
        if (trailingComma) sb.Append(", ");
    }

    private static void AppendCandidateAttributionsInline(StringBuilder sb, FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates, List<RenderFeatureSnapshot> renderFeatures, bool trailingComma)
    {
        sb.Append("\"candidateAttributions\": [");
        int count = 0;
        if (uiBatch != null)
        {
            AppendCommaIfNeeded(sb, ref count);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "type", "ui_batch", true);
            AppendJsonPropertyInline(sb, "confidence", uiBatch.confidence, true);
            AppendJsonPropertyInline(sb, "candidateId", uiBatch.id, true);
            AppendJsonPropertyInline(sb, "path", GetRepresentativeUiPath(uiBatch), true);
            AppendJsonPropertyInline(sb, "canvasPath", uiBatch.canvasPath, true);
            AppendJsonPropertyInline(sb, "material", uiBatch.materialName, true);
            AppendJsonPropertyInline(sb, "materialPath", uiBatch.materialPath, true);
            AppendJsonPropertyInline(sb, "shader", uiBatch.shaderName, true);
            AppendJsonPropertyInline(sb, "texture", uiBatch.textureName, true);
            AppendJsonPropertyInline(sb, "texturePath", uiBatch.texturePath, true);
            AppendJsonPropertyInline(sb, "method", "canvas_order_sequence_match", true);
            AppendJsonPropertyInline(sb, "matchedByCamera", !string.IsNullOrEmpty(uiBatch.camera), true);
            AppendJsonPropertyInline(sb, "matchedByCanvas", !string.IsNullOrEmpty(uiBatch.canvasPath), true);
            AppendJsonPropertyInline(sb, "matchedByMaterial", !string.IsNullOrEmpty(uiBatch.materialName) || !string.IsNullOrEmpty(uiBatch.materialPath), true);
            AppendJsonPropertyInline(sb, "matchedByShader", !string.IsNullOrEmpty(uiBatch.shaderName), true);
            AppendJsonPropertyInline(sb, "matchedByTexture", !string.IsNullOrEmpty(uiBatch.textureName) || !string.IsNullOrEmpty(uiBatch.texturePath), true);
            AppendJsonPropertyInline(sb, "matchedByOrder", true, true);
            AppendJsonPropertyInline(sb, "matchedGraphicDepthRange", GetUiBatchDepthRange(uiBatch), true);
            AppendJsonPropertyInline(sb, "representativeScreenRect", uiBatch.graphics.Count > 0 ? uiBatch.graphics[0].screenRect : "", true);
            AppendJsonPropertyInline(sb, "representativeFingerprint", uiBatch.graphics.Count > 0 ? uiBatch.graphics[0].resourceFingerprint : "", true);
            AppendJsonPropertyInline(sb, "riskNote", GetUiBatchRiskNote(), true);
            AppendJsonPropertyInline(sb, "reason", uiBatch.reason, false);
            sb.Append("}");
        }

        if (particleCandidates != null)
        {
            foreach (var particle in particleCandidates.Take(3))
            {
                AppendCommaIfNeeded(sb, ref count);
                sb.Append("{");
                AppendJsonPropertyInline(sb, "type", "particle", true);
                AppendJsonPropertyInline(sb, "confidence", particle.confidence, true);
                AppendJsonPropertyInline(sb, "candidateId", particle.id, true);
                AppendJsonPropertyInline(sb, "source", particle.source, true);
                AppendJsonPropertyInline(sb, "path", particle.path, true);
                AppendJsonPropertyInline(sb, "material", particle.materialName, true);
                AppendJsonPropertyInline(sb, "materialPath", particle.materialPath, true);
                AppendJsonPropertyInline(sb, "shader", particle.shaderName, true);
                AppendJsonPropertyInline(sb, "texture", particle.textureName, true);
                AppendJsonPropertyInline(sb, "aliveParticles", particle.aliveParticles, true);
                AppendJsonPropertyInline(sb, "bounds", particle.bounds, true);
                AppendJsonPropertyInline(sb, "resourceFingerprint", particle.resourceFingerprint, true);
                AppendJsonPropertyInline(sb, "matchScore", particle.matchScore, true);
                AppendStringArrayInline(sb, "matchedBy", particle.matchedBy, true);
                AppendJsonPropertyInline(sb, "reason", particle.reason, false);
                sb.Append("}");
            }
        }

        if (srpBatch != null)
        {
            AppendCommaIfNeeded(sb, ref count);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "type", "srp_batch", true);
            AppendJsonPropertyInline(sb, "confidence", srpBatch.confidence, true);
            AppendJsonPropertyInline(sb, "method", srpBatch.eventMeshNames.Count > 0 ? "framedebug_mesh_to_renderer_index" : "stage_renderer_index_fallback", true);
            AppendJsonPropertyInline(sb, "matchScore", srpBatch.matchScore, true);
            AppendStringArrayInline(sb, "matchedBy", srpBatch.matchedBy, true);
            AppendStringArrayInline(sb, "frameDebuggerMeshNames", srpBatch.eventMeshNames.ToArray(), true);
            AppendJsonPropertyInline(sb, "candidateFile", "ai_srp_batch_candidates.jsonl", true);
            AppendJsonPropertyInline(sb, "firstRendererPath", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].path : "", true);
            AppendJsonPropertyInline(sb, "firstRendererSource", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].source : "", true);
            AppendJsonPropertyInline(sb, "firstMaterial", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].materialName : "", true);
            AppendJsonPropertyInline(sb, "firstShader", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].shaderName : "", true);
            AppendJsonPropertyInline(sb, "firstTexture", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].textureName : "", true);
            AppendJsonPropertyInline(sb, "firstRendererFingerprint", srpBatch.renderers.Count > 0 ? srpBatch.renderers[0].resourceFingerprint : "", true);
            AppendJsonPropertyInline(sb, "reason", srpBatch.reason, false);
            sb.Append("}");
        }

        var renderFeature = FindRenderFeatureCandidate(evt, renderFeatures);
        if (renderFeature != null)
        {
            AppendCommaIfNeeded(sb, ref count);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "type", "render_feature", true);
            AppendJsonPropertyInline(sb, "confidence", "inferred_low", true);
            AppendJsonPropertyInline(sb, "name", renderFeature.featureName, true);
            AppendJsonPropertyInline(sb, "featureType", renderFeature.featureType, true);
            AppendJsonPropertyInline(sb, "rendererName", renderFeature.rendererName, true);
            AppendJsonPropertyInline(sb, "assetPath", renderFeature.featureAssetPath, true);
            AppendJsonPropertyInline(sb, "active", renderFeature.active, true);
            AppendJsonPropertyInline(sb, "reason", "Stage-level match only, not exact render pass instance; exact pass cost still needs Profiler/GPU capture.", false);
            sb.Append("}");
        }

        sb.Append("]");
        if (trailingComma) sb.Append(", ");
    }

    private string BuildAiDirectObjectDiagnosticsJson()
    {
        return BuildAiDirectObjectDiagnosticsJson(false);
    }

    private string BuildAiDirectObjectDiagnosticsFullJson()
    {
        return BuildAiDirectObjectDiagnosticsJson(true);
    }

    private string BuildAiDirectObjectDiagnosticsJson(bool includeAllEvents)
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        int gameObjectHits = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath));
        int rendererHits = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerRendererPath));
        int batchedEvents = events.Count(IsBatchedFrameDebuggerEvent);
        int batchedObjectHits = events.Count(e => IsBatchedFrameDebuggerEvent(e) &&
                                                 (!string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) ||
                                                  !string.IsNullOrEmpty(e.frameDebuggerRendererPath)));
        int detailMeshEvents = events.Count(HasFrameDebuggerDetailMeshes);
        int uiCandidates = BuildUiBatchCandidateMap(events).Count;
        int srpCandidates = BuildSrpBatchCandidateMap(events).Count;

        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-direct-object-diagnostics/v1", true);
        WriteJsonProperty(sb, "purpose", "Diagnoses Unity internal direct GameObject/Renderer lookup coverage and explains expected misses for batched Frame Debugger events.", true);
        WriteJsonProperty(sb, "unityVersion", Application.unityVersion, true);
        WriteJsonProperty(sb, "eventCount", events.Count, true);
        sb.AppendLine("  \"methods\": {");
        WriteJsonProperty(sb, "getFrameEventObject", s_getFrameEventObject != null ? s_getFrameEventObject.ToString() : "", true, 4);
        WriteJsonProperty(sb, "getFrameEventGameObject", s_getFrameEventGameObject != null ? s_getFrameEventGameObject.ToString() : "", true, 4);
        WriteJsonProperty(sb, "getFrameEventRenderer", s_getFrameEventRenderer != null ? s_getFrameEventRenderer.ToString() : "", false, 4);
        sb.AppendLine("  },");
        sb.AppendLine("  \"coverage\": {");
        WriteJsonProperty(sb, "eventsWithFrameDebuggerGameObject", gameObjectHits, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerRenderer", rendererHits, true, 4);
        WriteJsonProperty(sb, "batchedEventCount", batchedEvents, true, 4);
        WriteJsonProperty(sb, "batchedEventsWithFrameDebuggerDirectObject", batchedObjectHits, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerDetailMeshes", detailMeshEvents, true, 4);
        WriteJsonProperty(sb, "uiBatchCandidateCount", uiCandidates, true, 4);
        WriteJsonProperty(sb, "srpBatchCandidateCount", srpCandidates, false, 4);
        sb.AppendLine("  },");
        WriteJsonProperty(sb, "conclusion", BuildDirectObjectLookupConclusion(events), true);
        AppendStringArray(sb, "interpretationRules", new[]
        {
            "A direct GameObject/Renderer hit is strong evidence and should outrank scene snapshot candidates.",
            "Zero direct GameObject/Renderer hits is expected for many SRPBatch and Canvas.RenderSubBatch events because one Frame Debugger event can represent multiple renderers or UI graphics.",
            "For SRPBatch, prefer FrameDebuggerEventData detail mesh names / mesh instance ids plus renderer index matching.",
            "For Canvas.RenderSubBatch, prefer direct UI profiler batch GameObject data when exported; otherwise use ai_ui_batches.jsonl as heuristic runtime-order attribution.",
            "Do not upgrade renderer snapshot matches to direct attribution unless the FrameDebugger direct object API returned an object."
        }, true);
        AppendStringArray(sb, "recommendedNextDataSources", new[]
        {
            "FrameDebuggerEventData detail mesh names and mesh instance ids.",
            "UI Details Profiler batch GameObject columns for Canvas.RenderSubBatch attribution.",
            "Active scene Renderer snapshot filtered by mesh/material/shader/camera/renderQueue as secondary SRP evidence.",
            "Profiler/GPU capture for timing and cost ranking."
        }, true);
        WriteJsonProperty(sb, "fullEventDiagnostics", "debug/ai_direct_object_diagnostics_full.json", true);
        AppendDirectObjectDiagnosticSampleArray(sb, "directObjectHitExamples", events.Where(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) || !string.IsNullOrEmpty(e.frameDebuggerRendererPath)).Take(5).ToList(), true);
        AppendDirectObjectDiagnosticSampleArray(sb, "batchedExpectedMissExamples", events.Where(e => IsBatchedFrameDebuggerEvent(e) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)).Take(5).ToList(), true);
        AppendDirectObjectDiagnosticSampleArray(sb, "detailMeshFallbackExamples", events.Where(e => HasFrameDebuggerDetailMeshes(e)).Take(5).ToList(), includeAllEvents);
        if (includeAllEvents)
        {
            sb.AppendLine("  \"events\": [");
            for (int i = 0; i < events.Count; i++)
            {
                AppendDirectObjectDiagnosticEventObject(sb, events[i], "    ");
                sb.Append(i + 1 < events.Count ? "," : "");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendDirectObjectDiagnosticSampleArray(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma)
    {
        sb.AppendLine("  \"" + EscapeJson(name) + "\": [");
        for (int i = 0; i < events.Count; i++)
        {
            AppendDirectObjectDiagnosticEventObject(sb, events[i], "    ");
            sb.Append(i + 1 < events.Count ? "," : "");
            sb.AppendLine();
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendDirectObjectDiagnosticEventObject(StringBuilder sb, FrameEventInfo evt, string pad)
    {
        sb.Append(pad).Append("{");
        AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
        AppendJsonPropertyInline(sb, "eventName", evt.eventName, true);
        AppendJsonPropertyInline(sb, "eventType", evt.typeName, true);
        AppendJsonPropertyInline(sb, "unresolvedCategory", ClassifyUnresolvedEvent(evt), true);
        AppendJsonPropertyInline(sb, "isBatchedEvent", IsBatchedFrameDebuggerEvent(evt), true);
        AppendJsonPropertyInline(sb, "frameDebuggerGameObjectPath", evt.frameDebuggerGameObjectPath, true);
        AppendJsonPropertyInline(sb, "frameDebuggerRendererPath", evt.frameDebuggerRendererPath, true);
        AppendJsonPropertyInline(sb, "hasFrameDebuggerDetailMeshes", HasFrameDebuggerDetailMeshes(evt), true);
        AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames != null ? evt.detailMeshNames.Take(8).ToArray() : new string[0], true);
        AppendJsonPropertyInline(sb, "fallbackAttribution", GetDirectObjectFallbackAttribution(evt), false);
        sb.Append("}");
    }

    private static void AppendDirectObjectLookupObject(StringBuilder sb, List<FrameEventInfo> events, bool trailingComma)
    {
        events = events ?? new List<FrameEventInfo>();
        int gameObjectHits = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath));
        int rendererHits = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerRendererPath));
        int batchedEvents = events.Count(IsBatchedFrameDebuggerEvent);
        int batchedObjectHits = events.Count(e => IsBatchedFrameDebuggerEvent(e) &&
                                                 (!string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) ||
                                                  !string.IsNullOrEmpty(e.frameDebuggerRendererPath)));

        sb.AppendLine("  \"directObjectLookup\": {");
        WriteJsonProperty(sb, "diagnosticsFile", "ai_direct_object_diagnostics.json", true, 4);
        WriteJsonProperty(sb, "getFrameEventObjectMethodFound", s_getFrameEventObject != null, true, 4);
        WriteJsonProperty(sb, "getFrameEventGameObjectMethodFound", s_getFrameEventGameObject != null, true, 4);
        WriteJsonProperty(sb, "getFrameEventRendererMethodFound", s_getFrameEventRenderer != null, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerGameObject", gameObjectHits, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerRenderer", rendererHits, true, 4);
        WriteJsonProperty(sb, "batchedEventCount", batchedEvents, true, 4);
        WriteJsonProperty(sb, "batchedEventsWithFrameDebuggerDirectObject", batchedObjectHits, true, 4);
        WriteJsonProperty(sb, "expectedForBatchedEvents", "often_zero", true, 4);
        WriteJsonProperty(sb, "preferredFallbackForSRPBatch", "FrameDebuggerEventData.detail_mesh_fields_plus_renderer_index", true, 4);
        WriteJsonProperty(sb, "preferredFallbackForCanvasRenderSubBatch", "UI Details Profiler batch GameObjects when exported; otherwise ai_ui_batches.jsonl heuristic", false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendRuntimeSnapshotQualityObject(StringBuilder sb, bool trailingComma)
    {
        var runtime = GetRuntimePlayerSnapshot();
        sb.AppendLine("  \"runtimePlayerSnapshot\": {");
        WriteJsonProperty(sb, "status", runtime.status, true, 4);
        WriteJsonProperty(sb, "success", runtime.success, true, 4);
        WriteJsonProperty(sb, "available", runtime.available, true, 4);
        WriteJsonProperty(sb, "sourceFile", "snapshots/runtime_player_snapshot.json", true, 4);
        WriteJsonProperty(sb, "scene", runtime.scene, true, 4);
        WriteJsonProperty(sb, "frameCount", runtime.frameCount, true, 4);
        WriteJsonProperty(sb, "cameraCount", runtime.cameras.Count, true, 4);
        WriteJsonProperty(sb, "rendererCount", runtime.renderers.Count, true, 4);
        WriteJsonProperty(sb, "uiGraphicCount", runtime.uiGraphics.Count, true, 4);
        WriteJsonProperty(sb, "activeParticleCount", runtime.particles.Count(p => p.aliveParticles > 0), true, 4);
        WriteJsonProperty(sb, "textureCount", runtime.textures.Count, true, 4);
        WriteJsonProperty(sb, "renderersWithTexture", runtime.renderers.Count(r => !string.IsNullOrEmpty(r.textureName)), true, 4);
        WriteJsonProperty(sb, "uiGraphicsWithScreenRect", runtime.uiGraphics.Count(g => !string.IsNullOrEmpty(g.screenRect)), true, 4);
        WriteJsonProperty(sb, "uiTextGraphicCount", runtime.uiGraphics.Count(g => g.isTextComponent), true, 4);
        WriteJsonProperty(sb, "camerasWithCullingMask", runtime.cameras.Count(c => c.cullingMask >= 0), true, 4);
        WriteJsonProperty(sb, "usedForUiBatchCandidates", runtime.available && runtime.uiGraphics.Count > 0, true, 4);
        WriteJsonProperty(sb, "usedForSrpRendererCandidates", runtime.available && runtime.renderers.Count > 0, true, 4);
        WriteJsonProperty(sb, "usedForParticleCandidates", runtime.available && runtime.particles.Count > 0, true, 4);
        WriteJsonProperty(sb, "timingEvidence", false, true, 4);
        WriteJsonProperty(sb, "error", runtime.error, false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendResourceFingerprintQualityGateObject(StringBuilder sb, List<FrameEventInfo> events, bool trailingComma)
    {
        var runtime = GetRuntimePlayerSnapshot();
        int eventCount = events != null ? events.Count : 0;
        int uiSubBatch = events != null ? events.Count(IsUiSubBatchEvent) : 0;
        int srpBatch = events != null ? events.Count(IsSrpBatchEvent) : 0;
        int renderersWithFingerprint = BuildSceneRendererRecords().Count(r => !string.IsNullOrEmpty(r.resourceFingerprint));
        int uiWithFingerprint = BuildUiGraphicRecords().Count(g => !string.IsNullOrEmpty(g.resourceFingerprint));
        int particlesWithFingerprint = GetActiveParticleCandidates().Count(p => !string.IsNullOrEmpty(p.resourceFingerprint));
        bool hasRuntime = runtime.available;
        bool hasTextureMeta = runtime.textures.Count > 0 || BuildUiGraphicRecords().Any(g => !string.IsNullOrEmpty(g.textureMeta)) || BuildSceneRendererRecords().Any(r => !string.IsNullOrEmpty(r.textureMeta));
        bool hasCameraMasks = runtime.cameras.Count == 0 || runtime.cameras.Any(c => c.cullingMask >= 0);
        bool hasUiGeometry = uiSubBatch == 0 || BuildUiGraphicRecords().Any(g => !string.IsNullOrEmpty(g.screenRect));
        bool hasRenderDocTables = s_exportCache != null &&
                                  s_exportCache.events != null &&
                                  File.Exists(Path.Combine(GetCurrentLinkedRenderDocAnalysisDirectory(), "resource_table.json"));
        string status = hasRuntime && hasTextureMeta && hasCameraMasks && hasUiGeometry ? "pass" :
            (hasTextureMeta || renderersWithFingerprint > 0 || uiWithFingerprint > 0 || particlesWithFingerprint > 0) ? "warn" : "fail";
        string attributionReadiness = status == "pass" && hasRenderDocTables
            ? "object_resource_candidate_matching_ready"
            : status == "pass"
                ? "unity_object_fingerprints_ready_renderdoc_tables_missing_or_not_linked"
                : status == "warn"
                    ? "partial_fingerprints_only"
                    : "stage_level_only";

        sb.AppendLine("  \"resourceFingerprintQualityGate\": {");
        WriteJsonProperty(sb, "status", status, true, 4);
        WriteJsonProperty(sb, "attributionReadiness", attributionReadiness, true, 4);
        WriteJsonProperty(sb, "eventCount", eventCount, true, 4);
        WriteJsonProperty(sb, "runtimeSnapshotAvailable", hasRuntime, true, 4);
        WriteJsonProperty(sb, "textureMetadataAvailable", hasTextureMeta, true, 4);
        WriteJsonProperty(sb, "cameraCullingMasksAvailable", hasCameraMasks, true, 4);
        WriteJsonProperty(sb, "uiScreenRectsAvailable", hasUiGeometry, true, 4);
        WriteJsonProperty(sb, "rendererFingerprints", renderersWithFingerprint, true, 4);
        WriteJsonProperty(sb, "uiFingerprints", uiWithFingerprint, true, 4);
        WriteJsonProperty(sb, "particleFingerprints", particlesWithFingerprint, true, 4);
        WriteJsonProperty(sb, "fingerprintFile", "ai_resource_fingerprints.json", true, 4);
        WriteJsonProperty(sb, "unityRenderDocCorrelationSeed", "ai_unity_renderdoc_correlation_seed.json", true, 4);
        WriteJsonProperty(sb, "renderDocResourceTableAvailable", hasRenderDocTables, true, 4);
        AppendStringArray(sb, "rules", new[]
        {
            "pass: runtime snapshot, texture metadata, camera masks, and UI geometry are available for candidate matching.",
            "warn: some resource fingerprints exist, but at least one major evidence channel is missing.",
            "fail: object/resource fingerprint matching is too weak; keep conclusions at stage/pass level."
        }, false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendAnalysisBlockingPolicyObject(StringBuilder sb, List<FrameEventInfo> events, bool trailingComma)
    {
        events = events ?? new List<FrameEventInfo>();
        var runtime = GetRuntimePlayerSnapshot();
        string renderDocDir = GetCurrentLinkedRenderDocAnalysisDirectory();
        bool hasRenderDocDir = !string.IsNullOrEmpty(renderDocDir) && Directory.Exists(renderDocDir);
        bool hasResourceTable = hasRenderDocDir && File.Exists(Path.Combine(renderDocDir, "resource_table.json"));
        bool hasPassTable = hasRenderDocDir && File.Exists(Path.Combine(renderDocDir, "pass_table.json"));
        bool hasPipelineIndex = hasRenderDocDir && File.Exists(Path.Combine(renderDocDir, "pipeline_index.json"));
        bool hasPipelineChanges = hasRenderDocDir && File.Exists(Path.Combine(renderDocDir, "pipeline_state_changes.jsonl"));
        bool hasDeepEventFiles = hasRenderDocDir && Directory.EnumerateFiles(renderDocDir, "*deep*event*", SearchOption.TopDirectoryOnly).Any();
        bool deepEventPreflightExpected = hasRenderDocDir && hasPassTable && (hasPipelineIndex || hasPipelineChanges);
        int directRows = events.Count(e => (!string.IsNullOrEmpty(e.gameObjectName) || !string.IsNullOrEmpty(e.gameObjectPath) || !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath)) &&
                                          !string.IsNullOrEmpty(e.materialName) &&
                                          !string.IsNullOrEmpty(BestShaderName(e)));
        int fingerprintRows = BuildSceneRendererRecords().Count(r => !string.IsNullOrEmpty(r.resourceFingerprint)) +
                              BuildUiGraphicRecords().Count(g => !string.IsNullOrEmpty(g.resourceFingerprint)) +
                              GetActiveParticleCandidates().Count(p => !string.IsNullOrEmpty(p.resourceFingerprint));
        string allowedConclusionLevel;
        if (hasResourceTable && hasPassTable && fingerprintRows > 0)
            allowedConclusionLevel = hasDeepEventFiles ? "draw_level_with_deep_event_evidence" : "object_resource_candidate";
        else if (directRows > 0)
            allowedConclusionLevel = "unity_event_object_only";
        else if (events.Count > 0)
            allowedConclusionLevel = "stage_pass_count_only";
        else
            allowedConclusionLevel = "insufficient";

        var blockers = new List<string>();
        if (!runtime.available) blockers.Add("runtime_player_snapshot_missing_or_empty");
        if (fingerprintRows == 0) blockers.Add("resource_fingerprints_empty");
        if (!hasResourceTable) blockers.Add("renderdoc_resource_table_missing");
        if (!hasPassTable) blockers.Add("renderdoc_pass_table_missing");
        if (!hasPipelineIndex && !hasPipelineChanges) blockers.Add("renderdoc_pipeline_draw_index_missing");
        if (!hasDeepEventFiles && !deepEventPreflightExpected) blockers.Add("deep_event_samples_missing");

        sb.AppendLine("  \"analysisBlockingPolicy\": {");
        WriteJsonProperty(sb, "allowedConclusionLevel", allowedConclusionLevel, true, 4);
        WriteJsonProperty(sb, "renderDocAnalysisDirectory", renderDocDir, true, 4);
        WriteJsonProperty(sb, "runtimeSnapshotAvailable", runtime.available, true, 4);
        WriteJsonProperty(sb, "directUnityRows", directRows, true, 4);
        WriteJsonProperty(sb, "resourceFingerprintRows", fingerprintRows, true, 4);
        WriteJsonProperty(sb, "renderDocResourceTableAvailable", hasResourceTable, true, 4);
        WriteJsonProperty(sb, "renderDocPassTableAvailable", hasPassTable, true, 4);
        WriteJsonProperty(sb, "renderDocPipelineIndexAvailable", hasPipelineIndex, true, 4);
        WriteJsonProperty(sb, "renderDocPipelineStateChangesAvailable", hasPipelineChanges, true, 4);
        WriteJsonProperty(sb, "deepEventSamplesAvailableAtExport", hasDeepEventFiles, true, 4);
        WriteJsonProperty(sb, "deepEventPreflightExpectedAfterExport", deepEventPreflightExpected, true, 4);
        WriteJsonProperty(sb, "deepEventStatusSource", "At report time, prefer deep_event_preflight_status.json over this export-time availability flag.", true, 4);
        AppendDataSufficiencyObject(sb, "dataSufficiency", events, true, 4);
        AppendStringArray(sb, "blockers", blockers.ToArray(), true, 4);
        AppendStringArray(sb, "mustNotClaim", new[]
        {
            "Do not claim GPU time or performance cost ranking; timingAvailable=false unless an external timing source is present.",
            "Do not claim Unity eventIndex equals RenderDoc eventId; linked capture only shares a frozen remote target state.",
            "Do not claim exact GameObject ownership from resource/pass/pipeline candidates without DeepEvent or direct FrameDebugger object evidence.",
            "Do not treat runtime scene inventory as current-frame draw participation unless mapped through ai_event_evidence or ai_event_analysis.",
            "Do not treat UI batch candidate order as Unity UI Details Profiler ground truth.",
            "Do not claim Unity business duplicate rendering from repeated RenderDoc native submissions across vkCmdCopyImageToBuffer, vkCmdBlitImage, readback/copy, or vkQueuePresentKHR boundaries unless Unity Frame Debugger and runtime/resource evidence independently confirm repeated logical rendering.",
            "Do not include capture_artifact repeated-pass candidates in AI_LINKED_RENDER_REPORT.md actionable sections; keep them in AI_LINKED_ANALYSIS_AUDIT.json or a short excluded finding only."
        }, true, 4);
        AppendStringArray(sb, "requiredForDrawLevelClaims", new[]
        {
            "direct FrameDebugger object/renderer row, or",
            "DeepEvent sample tied to RenderDoc draw/event plus matching Unity resource fingerprint, or",
            "pipeline_state_changes evidence with texture/shader/material/geometry match and explicit candidate confidence."
        }, false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static string GetCurrentLinkedRenderDocAnalysisDirectory()
    {
        return s_exportCache != null ? s_exportCache.renderDocAnalysisDirectory ?? "" : "";
    }

    private static bool IsBatchedFrameDebuggerEvent(FrameEventInfo evt)
    {
        if (evt == null) return false;
        string name = evt.eventName ?? "";
        string type = evt.typeName ?? "";
        if (name.Contains("Canvas.RenderSubBatch")) return true;
        if (type.Contains("SRPBatch") || name.Contains("SRPBatch")) return true;
        if (name.Contains("BatchRendererGroup") || type.Contains("Batch")) return true;
        return false;
    }

    private static bool IsMeshDetailExpectedEvent(FrameEventInfo evt)
    {
        if (evt == null) return false;
        string name = evt.eventName ?? "";
        string type = evt.typeName ?? "";
        string stage = ParseStageName(name);

        if (type.Contains("SRPBatch") || name.Contains("SRPBatch"))
            return true;

        if (stage == "UI" ||
            name.Contains("Canvas.RenderSubBatch") ||
            name.Contains("Canvas.RenderOverlays") ||
            name.Contains("RenderOverlays") ||
            name.Contains("Clear") ||
            name.Contains("Copy") ||
            name.Contains("Blit") ||
            name.Contains("DepthOnly") ||
            name.Contains("DrawProcedural") ||
            type.Contains("Procedural") ||
            stage == "PostProcessing" ||
            name.Contains("RenderFeature"))
            return false;

        if (type.Contains("Mesh") ||
            name.Contains("Draw Mesh") ||
            name.Contains("Draw Dynamic") ||
            name.Contains("DrawRenderer") ||
            name.Contains("RenderLoop.Draw"))
            return true;
        if (!string.IsNullOrEmpty(evt.meshName))
            return true;

        return false;
    }

    private static bool IsSrpBatchEvent(FrameEventInfo evt)
    {
        if (evt == null) return false;
        string name = evt.eventName ?? "";
        string type = evt.typeName ?? "";
        return type.Contains("SRPBatch") || name.Contains("SRPBatch");
    }

    private static bool IsNonUiMeshEvent(FrameEventInfo evt)
    {
        if (evt == null) return false;
        if (ParseStageName(evt.eventName) == "UI") return false;
        string type = evt.typeName ?? "";
        string name = evt.eventName ?? "";
        return type.Contains("Mesh") || name.Contains("Draw Mesh") || name.Contains("Draw Dynamic") || !string.IsNullOrEmpty(evt.meshName);
    }

    private static bool IsOrdinaryMeshEvent(FrameEventInfo evt)
    {
        return IsMeshDetailExpectedEvent(evt) && IsNonUiMeshEvent(evt) && !IsSrpBatchEvent(evt) && !IsDynamicGeometryEvent(evt);
    }

    private static bool IsDynamicGeometryEvent(FrameEventInfo evt)
    {
        if (evt == null) return false;
        string name = evt.eventName ?? "";
        string type = evt.typeName ?? "";
        return type.Contains("DynamicGeometry") || name.Contains("Draw Dynamic");
    }

    private static bool IsUiMeshAttributionEvent(FrameEventInfo evt)
    {
        if (evt == null) return false;
        string name = evt.eventName ?? "";
        string stage = ParseStageName(name);
        return stage == "UI" || name.Contains("Canvas.RenderSubBatch") || name.Contains("Canvas.RenderOverlays") || name.Contains("UGUI.Rendering.RenderOverlays");
    }

    private static string BuildDirectObjectLookupConclusion(List<FrameEventInfo> events)
    {
        events = events ?? new List<FrameEventInfo>();
        int directObjectHits = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) || !string.IsNullOrEmpty(e.frameDebuggerRendererPath));
        int detailMeshEvents = events.Count(HasFrameDebuggerDetailMeshes);
        if (directObjectHits > 0)
            return "Unity internal direct object lookup returned objects for some events. Treat those rows as strong direct attribution.";
        if (detailMeshEvents > 0)
            return "Unity internal direct object lookup returned no GameObject/Renderer, but FrameDebuggerEventData detail mesh evidence exists. This is expected for batched SRP/UI events; use mesh/detail based candidates instead of direct object claims.";
        if (s_getFrameEventGameObject != null || s_getFrameEventRenderer != null || s_getFrameEventObject != null)
            return "Direct object methods were discovered but returned no objects for this capture. Treat this as API coverage limitation unless frameEventData diagnostics also failed.";
        return "No direct object lookup method was discovered in this Unity editor version. Use FrameDebuggerEventData fields and scene snapshots only.";
    }

    private static string GetDirectObjectFallbackAttribution(FrameEventInfo evt)
    {
        if (evt == null) return "none";
        if (!string.IsNullOrEmpty(evt.frameDebuggerGameObjectPath) || !string.IsNullOrEmpty(evt.frameDebuggerRendererPath))
            return "direct_object_api";
        if (HasFrameDebuggerDetailMeshes(evt))
            return "frame_debugger_detail_mesh";
        if (IsUiSubBatchEvent(evt))
            return "ui_batch_candidate_or_ui_details_profiler";
        if (ClassifyUnresolvedEvent(evt) == "SRPBatch")
            return "srp_batch_renderer_candidate";
        return "event_fields_or_scene_snapshot";
    }

    private string BuildAiDataQualityJson()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-data-quality/v1", true);
        WriteJsonProperty(sb, "purpose", "Self-check for AI analysis reliability and evidence limits.", true);
        WriteJsonProperty(sb, "analysisBlockingPolicy", "ai_analysis_blocking_policy.json", true);
        WriteJsonProperty(sb, "baselineComparison", "ai_baseline_comparison.json", true);
        WriteJsonProperty(sb, "frameEventDataDiagnostics", "ai_frameeventdata_diagnostics.json", true);
        WriteJsonProperty(sb, "directObjectDiagnostics", "ai_direct_object_diagnostics.json", true);
        WriteJsonProperty(sb, "srpBatchDiagnostics", "ai_srp_batch_diagnostics.json", true);
        WriteJsonProperty(sb, "eventCount", events.Count, true);
        AppendTimingMetadataObject(sb, true);
        AppendDataQualityObject(sb, events, true);
        AppendDirectObjectLookupObject(sb, events, true);
        AppendRuntimeSnapshotQualityObject(sb, true);
        AppendResourceFingerprintQualityGateObject(sb, events, true);
        AppendAnalysisBlockingPolicyObject(sb, events, true);
        AppendIssueTable(sb, "topDataGaps", BuildTopDataGaps(events), true);
        AppendIssueTable(sb, "topUnresolvedStages", BuildTopUnresolvedStages(events), true);
        AppendIssueTable(sb, "unresolvedCategories", BuildTopUnresolvedStages(events), true);
        AppendTopUiBatchCandidateTable(sb, "topUiBatchCandidates", events, true);
        AppendParticleCandidateSummaryObject(sb, "particleCandidateSummary", events, true);
        AppendTopParticleRootTable(sb, "topActiveParticleRoots", events, true);
        AppendTopParticleMaterialTable(sb, "topMappedParticleMaterials", events, true);
        AppendStringArray(sb, "canAnalyze", new[]
        {
            "Draw order and event counts by camera/stage.",
            "Direct object/material/shader rows when confidence=direct.",
            "UI Canvas.RenderSubBatch candidate batches when confidence=inferred_high.",
            "Active particle candidates when mappedToFrameEvent=true or particleCandidateIds is non-empty.",
            "RenderFeature and post-processing presence/order, but not cost."
        }, true);
        AppendStringArray(sb, "candidateOnly", new[]
        {
            "UI batch attribution from runtime Graphic order.",
            "Particle attribution from material/shader/path matching.",
            "Remote Player runtime snapshot attribution, when present, because it is a one-shot inventory and not event-level direct evidence.",
            "Scene inventory files not linked to ai_event_evidence or event_analysis."
        }, true);
        AppendStringArray(sb, "cannotConclude", GetFrameDebuggerCannotConclude(), true);
        AppendStringArray(sb, "emptyOrWeakFields", BuildEmptyOrWeakFieldList(events).ToArray(), false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiAnalysisBlockingPolicyJson()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-analysis-blocking-policy/v1", true);
        WriteJsonProperty(sb, "purpose", "Machine-readable evidence gates for preventing over-claims during AI analysis.", true);
        AppendAnalysisBlockingPolicyObject(sb, events, false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiDataQualityMarkdown()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var sb = new StringBuilder(4096);
        int direct = events.Count(e => (!string.IsNullOrEmpty(e.gameObjectName) || !string.IsNullOrEmpty(e.gameObjectPath)) && !string.IsNullOrEmpty(e.materialName) && !string.IsNullOrEmpty(BestShaderName(e)));
        int uiInferred = BuildUiBatchCandidateMap(events).Count;
        int unresolved = BuildAnalysisUnresolvedEvents(events).Count;
        int frameEventDataSuccess = events.Count(e => e.frameEventDataSuccess);
        int frameEventDataRaw = events.Count(e => e.rawDataFields != null && e.rawDataFields.Count > 0);
        int frameEventDataMeshes = events.Count(HasFrameDebuggerDetailMeshes);
        int frameDebuggerGameObject = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath));
        int frameDebuggerRenderer = events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerRendererPath));
        int meshExpectedEvents = events.Count(IsMeshDetailExpectedEvent);
        int meshExpectedButMissing = events.Count(e => IsMeshDetailExpectedEvent(e) && !HasFrameDebuggerDetailMeshes(e));
        int meshNotExpected = events.Count(e => !IsMeshDetailExpectedEvent(e));
        int srpBatchExpected = events.Count(IsSrpBatchEvent);
        int srpBatchCovered = events.Count(e => IsSrpBatchEvent(e) && HasFrameDebuggerDetailMeshes(e));
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        int uiSubBatch = events.Count(IsUiSubBatchEvent);
        int uiCandidateCovered = events.Count(e => IsUiSubBatchEvent(e) && uiBatchMap.ContainsKey(e.index));
        int uiUnresolved = events.Count(e => IsUiSubBatchEvent(e) && IsUnresolvedEvent(e));
        var runtime = GetRuntimePlayerSnapshot();

        sb.AppendLine("# Frame Debugger AI Data Quality");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("- Event count: `" + events.Count.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Direct event attribution: `" + direct.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Inferred UI batch candidates: `" + uiInferred.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Unresolved events: `" + unresolved.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- GetFrameEventData success/raw/detailMesh: `" + frameEventDataSuccess.ToString(CultureInfo.InvariantCulture) + "/" + frameEventDataRaw.ToString(CultureInfo.InvariantCulture) + "/" + frameEventDataMeshes.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Direct FrameDebugger GameObject/Renderer lookup: `" + frameDebuggerGameObject.ToString(CultureInfo.InvariantCulture) + "/" + frameDebuggerRenderer.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Mesh detail expected/missing/notExpected: `" + meshExpectedEvents.ToString(CultureInfo.InvariantCulture) + "/" + meshExpectedButMissing.ToString(CultureInfo.InvariantCulture) + "/" + meshNotExpected.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- SRPBatch mesh detail covered/expected: `" + srpBatchCovered.ToString(CultureInfo.InvariantCulture) + "/" + srpBatchExpected.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- UI subbatch candidateCovered/unresolved: `" + uiCandidateCovered.ToString(CultureInfo.InvariantCulture) + "/" + uiUnresolved.ToString(CultureInfo.InvariantCulture) + "` of `" + uiSubBatch.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Runtime Player snapshot: `" + runtime.status + "`, cameras/renderers/ui/activeParticles/textures: `" + runtime.cameras.Count.ToString(CultureInfo.InvariantCulture) + "/" + runtime.renderers.Count.ToString(CultureInfo.InvariantCulture) + "/" + runtime.uiGraphics.Count.ToString(CultureInfo.InvariantCulture) + "/" + runtime.particles.Count(p => p.aliveParticles > 0).ToString(CultureInfo.InvariantCulture) + "/" + runtime.textures.Count.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Resource fingerprints: `ai_resource_fingerprints.json`");
        sb.AppendLine("- Timing: `timingAvailable=false`, `costRankingBasis=draw_count_only`");
        sb.AppendLine("- Baseline comparison: `ai_baseline_comparison.json`");
        sb.AppendLine("- Direct object diagnostics: `ai_direct_object_diagnostics.json`");
        sb.AppendLine();
        sb.AppendLine("## Can Analyze");
        sb.AppendLine();
        sb.AppendLine("- Draw order, draw counts, camera/stage distribution.");
        sb.AppendLine("- Direct object/material/shader rows where `confidence=direct`.");
        sb.AppendLine("- UI subbatch candidates where `confidence=inferred_high`, with heuristic limitations.");
        sb.AppendLine("- Active particle candidates only when linked through `particleCandidateIds` or `mappedToFrameEvent=true`.");
        sb.AppendLine("- `FrameDebuggerUtility.GetFrameEventGameObject/GetFrameEventRenderer` hits when present; when zero, use `ai_direct_object_diagnostics.json` before treating it as failure.");
        sb.AppendLine();
        sb.AppendLine("## Cannot Conclude From This Export Alone");
        sb.AppendLine();
        foreach (string item in GetFrameDebuggerCannotConclude())
            sb.AppendLine("- " + item);
        sb.AppendLine();
        sb.AppendLine("## Top Data Gaps");
        sb.AppendLine();
        foreach (var row in BuildTopDataGaps(events))
            sb.AppendLine("- `" + row.name + "`: " + row.count.ToString(CultureInfo.InvariantCulture) + " - " + row.limitations);
        return sb.ToString();
    }

    private string BuildAiBaselineComparisonJson(string previousExportDir, string currentExportDir)
    {
        return BuildAiBaselineComparisonJson(previousExportDir, currentExportDir, -1);
    }

    private string BuildAiBaselineComparisonJson(string previousExportDir, string currentExportDir, long currentBaselineSizeOverride)
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        int currentDirect = events.Count(e => (!string.IsNullOrEmpty(e.gameObjectName) || !string.IsNullOrEmpty(e.gameObjectPath)) && !string.IsNullOrEmpty(e.materialName) && !string.IsNullOrEmpty(BestShaderName(e)));
        int currentCandidates = BuildUiBatchCandidateMap(events).Count +
                                BuildSrpBatchCandidateMap(events).Count +
                                BuildParticleCandidateMap(events).SelectMany(kv => kv.Value).Select(c => c.id).Distinct().Count();
        int currentUnresolved = Math.Max(0, events.Count - currentDirect);
        int currentUiCandidates = BuildUiBatchCandidateMap(events).Count;
        int currentSrpCandidates = BuildSrpBatchCandidateMap(events).Count;
        int currentParticleCandidates = BuildParticleCandidateMap(events).SelectMany(kv => kv.Value).Select(c => c.id).Distinct().Count();
        var currentUnresolvedCategories = BuildTopUnresolvedStages(events);
        int currentNonBatchedEvents = events.Count(e => !IsBatchedFrameDebuggerEvent(e));
        int currentDirectNonBatchedEvents = events.Count(e => !IsBatchedFrameDebuggerEvent(e) && HasDirectAttribution(e));
        int currentNonBatchedMeshEvents = events.Count(e => IsNonUiMeshEvent(e) && !IsBatchedFrameDebuggerEvent(e));
        int currentDirectNonBatchedMeshEvents = events.Count(e => IsNonUiMeshEvent(e) && !IsBatchedFrameDebuggerEvent(e) && HasDirectAttribution(e));
        var currentUiBatchMap = BuildUiBatchCandidateMap(events);
        int currentUiSubBatchEvents = events.Count(IsUiSubBatchEvent);
        int currentUiCandidateCovered = events.Count(e => IsUiSubBatchEvent(e) && currentUiBatchMap.ContainsKey(e.index));
        int currentSrpMeshExpected = events.Count(IsSrpBatchEvent);
        int currentSrpMeshCovered = events.Count(e => IsSrpBatchEvent(e) && HasFrameDebuggerDetailMeshes(e));

        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-baseline-comparison/v2", true);
        WriteJsonProperty(sb, "previousExportDir", previousExportDir ?? "", true);
        if (string.IsNullOrEmpty(previousExportDir))
        {
            WriteJsonProperty(sb, "status", "no_previous_export", true);
            WriteJsonProperty(sb, "currentExportDir", currentExportDir ?? "", true);
            AppendCoreFileStatusObject(sb, previousExportDir, currentExportDir, true);
            WriteJsonProperty(sb, "note", "No previous FrameDebugAI_* directory was found in the same export root.", false);
            sb.AppendLine("}");
            return sb.ToString();
        }

        string previousQualityPath = Path.Combine(previousExportDir, "ai_data_quality.json");
        string previousSummaryPath = Path.Combine(previousExportDir, "ai_summary.json");
        string currentSummaryPath = Path.Combine(currentExportDir, "ai_summary.json");
        string previousJson = File.Exists(previousQualityPath) ? File.ReadAllText(previousQualityPath) : "";
        string previousSummaryJson = File.Exists(previousSummaryPath) ? File.ReadAllText(previousSummaryPath) : "";
        string currentSummaryJson = File.Exists(currentSummaryPath) ? File.ReadAllText(currentSummaryPath) : "";
        int previousEventCount = ExtractJsonInt(previousJson, "eventCount");
        int previousDirect = ExtractJsonInt(previousJson, "eventsWithObjectMaterialShader");
        int previousCandidates = ExtractJsonInt(previousJson, "candidateAttributionCount");
        int previousUnresolved = ExtractJsonInt(previousJson, "eventsWithoutObjectMaterialShader");
        int previousUiCandidates = ExtractJsonInt(previousJson, "uiBatchCandidateCount");
        int previousSrpCandidates = ExtractJsonInt(previousJson, "srpBatchCandidateCount");
        int previousParticleCandidates = ExtractJsonInt(previousJson, "particleCandidateCount");
        int previousFrameDebuggerMeshes = ExtractJsonInt(previousJson, "eventsWithFrameDebuggerMeshes");
        string previousDirectAttributionRateExcludingBatched = ExtractJsonString(previousJson, "directAttributionRateExcludingBatched");
        string previousDirectAttributionRateForNonBatchedMesh = ExtractJsonString(previousJson, "directAttributionRateForNonBatchedMesh");
        string previousUiCandidateCoverageRate = ExtractJsonString(previousJson, "uiCandidateCoverageRate");
        string previousSrpMeshCoverageRate = ExtractJsonString(previousJson, "srpMeshCoverageRate");
        string previousSchema = ExtractJsonString(previousSummaryJson, "schemaVersion");
        string currentSchema = ExtractJsonString(currentSummaryJson, "schemaVersion");
        int currentFrameDebuggerMeshes = events.Count(HasFrameDebuggerDetailMeshes);
        bool captureContentChanged = previousEventCount != events.Count;
        bool exporterSchemaChanged = !string.Equals(previousSchema, currentSchema, StringComparison.Ordinal) || PreviousUsesOldFrameDebugSchema(previousExportDir);
        bool attributionRegressed = previousEventCount > 0 && currentDirect * 100.0 / Math.Max(1, events.Count) < previousDirect * 100.0 / Math.Max(1, previousEventCount);
        bool candidateInferenceImproved = currentCandidates > previousCandidates;
        bool uiCandidateChanged = currentUiCandidates != previousUiCandidates;
        bool srpCandidateImproved = currentSrpCandidates > previousSrpCandidates;
        bool particleCandidateImproved = currentParticleCandidates > previousParticleCandidates;
        bool directCandidateQualityImproved = currentDirect > previousDirect;
        bool frameDebugInternalDataImproved = currentFrameDebuggerMeshes > previousFrameDebuggerMeshes;
        bool sceneSnapshotCandidateImproved = currentCandidates > previousCandidates;
        bool candidateQualityImproved = directCandidateQualityImproved || frameDebugInternalDataImproved;
        bool candidateCountChanged = currentCandidates != previousCandidates;

        WriteJsonProperty(sb, "status", File.Exists(previousQualityPath) ? "compared" : "previous_quality_missing", true);
        WriteJsonProperty(sb, "currentExportDir", currentExportDir ?? "", true);
        sb.AppendLine("  \"flags\": {");
        WriteJsonProperty(sb, "capture_content_changed", captureContentChanged, true, 4);
        WriteJsonProperty(sb, "exporter_schema_changed", exporterSchemaChanged, true, 4);
        WriteJsonProperty(sb, "attribution_regressed", attributionRegressed, true, 4);
        WriteJsonProperty(sb, "candidate_inference_improved", candidateInferenceImproved, true, 4);
        WriteJsonProperty(sb, "ui_candidate_changed", uiCandidateChanged, true, 4);
        WriteJsonProperty(sb, "srp_candidate_improved", srpCandidateImproved, true, 4);
        WriteJsonProperty(sb, "particle_candidate_improved", particleCandidateImproved, true, 4);
        WriteJsonProperty(sb, "direct_candidate_quality_improved", directCandidateQualityImproved, true, 4);
        WriteJsonProperty(sb, "framedebug_internal_data_improved", frameDebugInternalDataImproved, true, 4);
        WriteJsonProperty(sb, "scene_snapshot_candidate_improved", sceneSnapshotCandidateImproved, true, 4);
        WriteJsonProperty(sb, "candidate_quality_improved", candidateQualityImproved, true, 4);
        WriteJsonProperty(sb, "candidate_count_changed", candidateCountChanged, false, 4);
        sb.AppendLine("  },");
        AppendStringArray(sb, "interpretationHints", BuildBaselineInterpretationHints(captureContentChanged, exporterSchemaChanged, attributionRegressed, candidateInferenceImproved), true);
        sb.AppendLine("  \"current\": {");
        WriteJsonProperty(sb, "eventCount", events.Count, true, 4);
        WriteJsonProperty(sb, "eventsWithObjectMaterialShader", currentDirect, true, 4);
        WriteJsonProperty(sb, "directAttributionRate", FormatRate(currentDirect, events.Count), true, 4);
        WriteJsonProperty(sb, "directAttributionRateExcludingBatched", FormatRate(currentDirectNonBatchedEvents, currentNonBatchedEvents), true, 4);
        WriteJsonProperty(sb, "directAttributionRateForNonBatchedMesh", FormatRate(currentDirectNonBatchedMeshEvents, currentNonBatchedMeshEvents), true, 4);
        WriteJsonProperty(sb, "uiCandidateCoverageRate", FormatRate(currentUiCandidateCovered, currentUiSubBatchEvents), true, 4);
        WriteJsonProperty(sb, "srpMeshCoverageRate", FormatRate(currentSrpMeshCovered, currentSrpMeshExpected), true, 4);
        WriteJsonProperty(sb, "candidateAttributionCount", currentCandidates, true, 4);
        WriteJsonProperty(sb, "uiBatchCandidateCount", currentUiCandidates, true, 4);
        WriteJsonProperty(sb, "srpBatchCandidateCount", currentSrpCandidates, true, 4);
        WriteJsonProperty(sb, "particleCandidateCount", currentParticleCandidates, true, 4);
        WriteJsonProperty(sb, "eventsWithoutObjectMaterialShader", currentUnresolved, false, 4);
        sb.AppendLine("  },");
        sb.AppendLine("  \"previous\": {");
        WriteJsonProperty(sb, "eventCount", previousEventCount, true, 4);
        WriteJsonProperty(sb, "eventsWithObjectMaterialShader", previousDirect, true, 4);
        WriteJsonProperty(sb, "directAttributionRate", FormatRate(previousDirect, previousEventCount), true, 4);
        WriteJsonProperty(sb, "directAttributionRateExcludingBatched", previousDirectAttributionRateExcludingBatched, true, 4);
        WriteJsonProperty(sb, "directAttributionRateForNonBatchedMesh", previousDirectAttributionRateForNonBatchedMesh, true, 4);
        WriteJsonProperty(sb, "uiCandidateCoverageRate", previousUiCandidateCoverageRate, true, 4);
        WriteJsonProperty(sb, "srpMeshCoverageRate", previousSrpMeshCoverageRate, true, 4);
        WriteJsonProperty(sb, "candidateAttributionCount", previousCandidates, true, 4);
        WriteJsonProperty(sb, "uiBatchCandidateCount", previousUiCandidates, true, 4);
        WriteJsonProperty(sb, "srpBatchCandidateCount", previousSrpCandidates, true, 4);
        WriteJsonProperty(sb, "particleCandidateCount", previousParticleCandidates, true, 4);
        WriteJsonProperty(sb, "eventsWithoutObjectMaterialShader", previousUnresolved, false, 4);
        sb.AppendLine("  },");
        sb.AppendLine("  \"delta\": {");
        WriteJsonProperty(sb, "eventCount", events.Count - previousEventCount, true, 4);
        WriteJsonProperty(sb, "eventsWithObjectMaterialShader", currentDirect - previousDirect, true, 4);
        WriteJsonProperty(sb, "candidateAttributionCount", currentCandidates - previousCandidates, true, 4);
        WriteJsonProperty(sb, "uiBatchCandidateCount", currentUiCandidates - previousUiCandidates, true, 4);
        WriteJsonProperty(sb, "srpBatchCandidateCount", currentSrpCandidates - previousSrpCandidates, true, 4);
        WriteJsonProperty(sb, "particleCandidateCount", currentParticleCandidates - previousParticleCandidates, true, 4);
        WriteJsonProperty(sb, "eventsWithoutObjectMaterialShader", currentUnresolved - previousUnresolved, false, 4);
        sb.AppendLine("  },");
        sb.AppendLine("  \"schema\": {");
        WriteJsonProperty(sb, "current", currentSchema, true, 4);
        WriteJsonProperty(sb, "previous", previousSchema, true, 4);
        WriteJsonProperty(sb, "eventsRawReplacedOldAiEvents", File.Exists(Path.Combine(currentExportDir, "ai_events_raw.jsonl")) && File.Exists(Path.Combine(previousExportDir, "ai_events.jsonl")), false, 4);
        sb.AppendLine("  },");
        AppendCoreFileStatusObject(sb, previousExportDir, currentExportDir, true);
        AppendFileSizeComparisonObject(sb, previousExportDir, currentExportDir, currentBaselineSizeOverride, true);
        AppendUnresolvedCategoryDeltaObject(sb, currentUnresolvedCategories, previousJson, false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiUiBatchesJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var candidates = BuildUiBatchCandidateMap(events).Values
            .OrderBy(c => c.mappedEventIndex)
            .ToList();
        var sb = new StringBuilder(16384);
        foreach (var candidate in candidates)
        {
            AppendUiBatchCandidateObject(sb, candidate, "");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildAiUiBatchDetailsJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var candidates = BuildUiBatchCandidateMap(events).Values
            .OrderBy(c => c.mappedEventIndex)
            .ToList();
        var sb = new StringBuilder(32768);
        foreach (var candidate in candidates)
        {
            var orderedGraphics = candidate.graphics
                .OrderBy(g => g.canvasSortingLayerId)
                .ThenBy(g => g.canvasSortingOrder)
                .ThenBy(g => g.depth)
                .ThenBy(g => g.path)
                .Take(64)
                .ToList();
            sb.Append("{");
            AppendJsonPropertyInline(sb, "schemaVersion", "framedebug-ai-ui-batch-details/v1", true);
            AppendJsonPropertyInline(sb, "mappedEventIndex", candidate.mappedEventIndex, true);
            AppendJsonPropertyInline(sb, "candidateId", candidate.id, true);
            AppendJsonPropertyInline(sb, "source", "runtime_or_editor_graphic_order_approximation", true);
            AppendJsonPropertyInline(sb, "profilerUiDetailsAvailable", false, true);
            AppendJsonPropertyInline(sb, "profilerUiDetailsNote", "Unity UI Details Profiler data is not exposed in this exporter path; this file provides the closest automatic batch evidence from Graphic order, CanvasRenderer, mask, material, texture, and screenRect.", true);
            AppendJsonPropertyInline(sb, "confidence", candidate.confidence, true);
            AppendJsonPropertyInline(sb, "canvasPath", candidate.canvasPath, true);
            AppendJsonPropertyInline(sb, "batchKey", candidate.batchKey, true);
            AppendJsonPropertyInline(sb, "materialName", candidate.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", candidate.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", candidate.textureName, true);
            AppendJsonPropertyInline(sb, "maskState", candidate.maskState, true);
            AppendJsonPropertyInline(sb, "graphicCount", candidate.graphics.Count, true);
            AppendStringArrayInline(sb, "screenRectSamples", orderedGraphics.Select(g => g.screenRect).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(12).ToArray(), true);
            AppendStringArrayInline(sb, "canvasRendererSamples", orderedGraphics.Select(g => g.canvasRenderer).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(12).ToArray(), true);
            AppendStringArrayInline(sb, "materialTextureSamples", orderedGraphics.Select(g => g.materialTextures).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(12).ToArray(), true);
            sb.Append("\"graphics\": [");
            for (int i = 0; i < orderedGraphics.Count; i++)
            {
                var graphic = orderedGraphics[i];
                if (i > 0) sb.Append(", ");
                sb.Append("{");
                AppendJsonPropertyInline(sb, "path", graphic.path, true);
                AppendJsonPropertyInline(sb, "type", graphic.type, true);
                AppendJsonPropertyInline(sb, "depth", graphic.depth, true);
                AppendJsonPropertyInline(sb, "screenRect", graphic.screenRect, true);
                AppendJsonPropertyInline(sb, "canvasRenderer", graphic.canvasRenderer, true);
                AppendJsonPropertyInline(sb, "materialName", graphic.materialName, true);
                AppendJsonPropertyInline(sb, "shaderName", graphic.shaderName, true);
                AppendJsonPropertyInline(sb, "textureName", graphic.textureName, true);
                AppendJsonPropertyInline(sb, "materialTextures", graphic.materialTextures, true);
                AppendJsonPropertyInline(sb, "spriteName", graphic.spriteName, true);
                AppendJsonPropertyInline(sb, "maskState", graphic.maskState, true);
                AppendJsonPropertyInline(sb, "sortingGroup", graphic.sortingGroup, true);
                AppendJsonPropertyInline(sb, "resourceFingerprint", graphic.resourceFingerprint, false);
                sb.Append("}");
            }
            sb.Append("]}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildAiSrpBatchCandidatesJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var candidates = BuildSrpBatchCandidateMap(events)
            .Values
            .OrderBy(c => c.eventIndex)
            .ToList();
        var sb = new StringBuilder(16384);
        foreach (var candidate in candidates)
        {
            sb.Append("{");
            AppendJsonPropertyInline(sb, "eventIndex", candidate.eventIndex, true);
            AppendJsonPropertyInline(sb, "camera", candidate.camera, true);
            AppendJsonPropertyInline(sb, "stage", candidate.stage, true);
            AppendJsonPropertyInline(sb, "eventType", candidate.eventType, true);
            AppendJsonPropertyInline(sb, "confidence", candidate.confidence, true);
            AppendJsonPropertyInline(sb, "matchScore", candidate.matchScore, true);
            AppendStringArrayInline(sb, "matchedBy", candidate.matchedBy, true);
            AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", candidate.frameDebuggerDetailMeshNames.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", candidate.frameDebuggerMeshInstanceIds.ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshNames", candidate.eventMeshNames.ToArray(), true);
            AppendStringArrayInline(sb, "rendererCandidateMeshNames", candidate.renderers.Select(r => r.meshName).Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(8).ToArray(), true);
            AppendJsonPropertyInline(sb, "lightMode", candidate.lightMode, true);
            AppendJsonPropertyInline(sb, "batchBreakCause", candidate.batchCause, true);
            AppendJsonPropertyInline(sb, "source", candidate.hasFrameDebuggerDetailMeshes ? "FrameDebuggerEventData.detail_mesh_fields_plus_renderer_index" : "renderer_index_stage_fallback", true);
            AppendJsonPropertyInline(sb, "reason", candidate.reason, true);
            sb.Append("\"renderers\": [");
            int count = 0;
            foreach (var renderer in candidate.renderers.Take(8))
            {
                AppendCommaIfNeeded(sb, ref count);
                AppendRendererRecordInline(sb, renderer);
            }
            sb.Append("]}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildAiSrpBatchDiagnosticsJson()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var srpEvents = events
            .Where(e => e != null && (string.Equals(e.typeName, "SRPBatch", StringComparison.OrdinalIgnoreCase) || ClassifyUnresolvedEvent(e) == "SRPBatch"))
            .OrderBy(e => e.index)
            .ToList();
        var candidates = BuildSrpBatchCandidateMap(events);
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-srp-batch-diagnostics/v1", true);
        WriteJsonProperty(sb, "purpose", "Explains SRPBatch candidate coverage and why ai_srp_batch_candidates.jsonl may be empty.", true);
        WriteJsonProperty(sb, "srpBatchEventCount", srpEvents.Count, true);
        WriteJsonProperty(sb, "srpBatchCandidateCount", candidates.Count, true);
        WriteJsonProperty(sb, "srpBatchEventsWithFrameDebuggerDetailMeshes", srpEvents.Count(HasFrameDebuggerDetailMeshes), true);
        WriteJsonProperty(sb, "srpBatchEventsWithFrameEventDataSuccess", srpEvents.Count(e => e.frameEventDataSuccess), true);
        WriteJsonProperty(sb, "srpBatchEventsWithRawDataFields", srpEvents.Count(e => e.rawDataFields != null && e.rawDataFields.Count > 0), true);
        WriteJsonProperty(sb, "candidateFile", "ai_srp_batch_candidates.jsonl", true);
        WriteJsonProperty(sb, "diagnosis", candidates.Count == 0 ? "No SRP batch candidates were emitted because no SRPBatch event exposed FrameDebuggerEventData detail mesh names/mesh instance ids or reliable renderer matches." : "SRP batch candidates are available; inspect candidate confidence/source fields.", true);
        sb.AppendLine("  \"events\": [");
        for (int i = 0; i < srpEvents.Count; i++)
        {
            var evt = srpEvents[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
            AppendJsonPropertyInline(sb, "eventName", evt.eventName, true);
            AppendJsonPropertyInline(sb, "eventType", evt.typeName, true);
            AppendJsonPropertyInline(sb, "frameEventDataSuccess", evt.frameEventDataSuccess, true);
            AppendJsonPropertyInline(sb, "rawDataFieldCount", evt.rawDataFields != null ? evt.rawDataFields.Count : 0, true);
            AppendJsonPropertyInline(sb, "hasFrameDebuggerDetailMeshes", HasFrameDebuggerDetailMeshes(evt), true);
            AppendStringArrayInline(sb, "frameDebuggerDetailMeshNames", evt.detailMeshNames != null ? evt.detailMeshNames.ToArray() : new string[0], true);
            AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", evt.detailMeshInstanceIds != null ? evt.detailMeshInstanceIds.ToArray() : new string[0], true);
            AppendJsonPropertyInline(sb, "limitBefore", evt.frameEventDataLimitBefore, true);
            AppendJsonPropertyInline(sb, "limitSetTo", evt.frameEventDataLimitSetTo, true);
            AppendJsonPropertyInline(sb, "limitAfter", evt.frameEventDataLimitAfter, true);
            AppendJsonPropertyInline(sb, "requestedEventIndex", evt.requestedEventIndex, true);
            AppendJsonPropertyInline(sb, "filledFrameEventIndex", evt.filledFrameEventIndex, true);
            AppendJsonPropertyInline(sb, "failureReason", evt.frameEventDataFailureReason, false);
            sb.Append(i + 1 < srpEvents.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiObjectInventoryJson()
    {
        var runtime = GetRuntimePlayerSnapshot();
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var renderers = BuildSceneRendererRecords()
            .OrderBy(r => r.path)
            .ThenBy(r => r.subMeshIndex)
            .ToList();
        var graphics = BuildUiGraphicRecords()
            .OrderBy(g => g.canvasSortingLayerId)
            .ThenBy(g => g.canvasSortingOrder)
            .ThenBy(g => g.depth)
            .ThenBy(g => g.path)
            .ToList();
        var particles = GetActiveParticleCandidates()
            .OrderByDescending(p => p.aliveParticles)
            .ThenBy(p => p.path)
            .ToList();
        var runtimeCameras = runtime.available ? runtime.cameras.OrderBy(c => c.depth).ThenBy(c => c.path).ToList() : new List<RuntimeCameraRecord>();
        var sceneCameras = runtime.available ? new List<Camera>() : GetSceneCameras();
        var renderFeatures = GetRenderFeatureSnapshots()
            .OrderBy(f => f.rendererName)
            .ThenBy(f => f.featureName)
            .ToList();

        var sb = new StringBuilder(262144);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-object-inventory/v1", true);
        WriteJsonProperty(sb, "purpose", "One-capture object inventory for attribution. Read this before asking for another export; it contains renderer, UI, particle, camera, and render feature evidence available in this single capture.", true);
        WriteJsonProperty(sb, "source", runtime.available ? "runtime_player_snapshot" : "editor_scene_snapshot", true);
        WriteJsonProperty(sb, "timingEvidence", false, true);
        WriteJsonProperty(sb, "eventCount", events.Count, true);
        WriteJsonProperty(sb, "rendererCount", renderers.Count, true);
        WriteJsonProperty(sb, "uiGraphicCount", graphics.Count, true);
        WriteJsonProperty(sb, "activeParticleCount", particles.Count, true);
        WriteJsonProperty(sb, "cameraCount", runtime.available ? runtimeCameras.Count : sceneCameras.Count, true);
        WriteJsonProperty(sb, "renderFeatureCount", renderFeatures.Count, true);
        WriteJsonProperty(sb, "objectLevelUseRule", "Use this file and ai_event_attribution_index.jsonl for current-capture ownership candidates. If they do not identify an owner, downgrade the claim instead of requesting another capture.", true);

        sb.AppendLine("  \"renderers\": [");
        for (int i = 0; i < renderers.Count; i++)
        {
            sb.Append("    ");
            AppendRendererRecordInline(sb, renderers[i]);
            sb.Append(i + 1 < renderers.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"uiGraphics\": [");
        for (int i = 0; i < graphics.Count; i++)
        {
            sb.Append("    ");
            AppendUiGraphicRecordInline(sb, graphics[i], true);
            sb.Append(i + 1 < graphics.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"activeParticles\": [");
        for (int i = 0; i < particles.Count; i++)
        {
            sb.Append("    ");
            AppendParticleCandidateInline(sb, particles[i], true);
            sb.Append(i + 1 < particles.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"cameras\": [");
        if (runtime.available)
        {
            for (int i = 0; i < runtimeCameras.Count; i++)
            {
                sb.Append("    ");
                AppendRuntimeCameraRecordInline(sb, runtimeCameras[i]);
                sb.Append(i + 1 < runtimeCameras.Count ? "," : "");
                sb.AppendLine();
            }
        }
        else
        {
            for (int i = 0; i < sceneCameras.Count; i++)
            {
                sb.Append("    ");
                AppendSceneCameraInline(sb, sceneCameras[i]);
                sb.Append(i + 1 < sceneCameras.Count ? "," : "");
                sb.AppendLine();
            }
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"renderFeatures\": [");
        for (int i = 0; i < renderFeatures.Count; i++)
        {
            sb.Append("    ");
            AppendRenderFeatureSnapshotInline(sb, renderFeatures[i]);
            sb.Append(i + 1 < renderFeatures.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"lookupFiles\": {");
        WriteJsonProperty(sb, "eventAttributionIndex", "ai_event_attribution_index.jsonl", true, 4);
        WriteJsonProperty(sb, "rendererFullSnapshot", "snapshots/renderers.jsonl", true, 4);
        WriteJsonProperty(sb, "rendererCompactSnapshot", "snapshots/renderers_compact.jsonl", true, 4);
        WriteJsonProperty(sb, "uiSnapshot", "snapshots/ui_graphics.jsonl", true, 4);
        WriteJsonProperty(sb, "particleSnapshot", "snapshots/particles.jsonl", true, 4);
        WriteJsonProperty(sb, "cameraSnapshot", "snapshots/cameras.json", true, 4);
        WriteJsonProperty(sb, "renderFeatureSnapshot", "snapshots/renderfeatures.json", false, 4);
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiEventAttributionIndexJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        var srpBatchMap = BuildSrpBatchCandidateMap(events);
        var particleCandidateMap = BuildParticleCandidateMap(events);
        var renderFeatures = GetRenderFeatureSnapshots();
        var sb = new StringBuilder(131072);

        foreach (var evt in events)
        {
            uiBatchMap.TryGetValue(evt.index, out var uiBatch);
            srpBatchMap.TryGetValue(evt.index, out var srpBatch);
            particleCandidateMap.TryGetValue(evt.index, out var particleCandidates);
            var feature = FindRenderFeatureCandidate(evt, renderFeatures);
            string confidence = GetEvidenceConfidence(evt, uiBatch, srpBatch, particleCandidates);

            sb.Append("{");
            AppendJsonPropertyInline(sb, "schemaVersion", "framedebug-ai-event-attribution-index/v1", true);
            AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
            AppendJsonPropertyInline(sb, "camera", ParseCameraName(evt.eventName), true);
            AppendJsonPropertyInline(sb, "stage", ParseStageName(evt.eventName), true);
            AppendJsonPropertyInline(sb, "eventType", evt.typeName, true);
            AppendJsonPropertyInline(sb, "eventName", evt.eventName, true);
            AppendJsonPropertyInline(sb, "confidence", confidence, true);
            AppendJsonPropertyInline(sb, "attributionSource", GetAttributionSource(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "unresolvedCategory", ClassifyUnresolvedEvent(evt, uiBatch, srpBatch, particleCandidates), true);
            AppendJsonPropertyInline(sb, "passName", evt.passName, true);
            AppendJsonPropertyInline(sb, "lightMode", evt.passLightMode, true);
            AppendJsonPropertyInline(sb, "batchBreakCause", evt.batchBreakCause, true);
            AppendJsonPropertyInline(sb, "directOwnerAvailable", HasDirectAttribution(evt), true);
            AppendJsonPropertyInline(sb, "candidateCount", CountAttributionCandidates(uiBatch, srpBatch, particleCandidates, feature), true);
            AppendDirectAttributionObjectInline(sb, evt, true);
            AppendCandidateAttributionsInline(sb, evt, uiBatch, srpBatch, particleCandidates, renderFeatures, true);
            AppendJsonPropertyInline(sb, "renderFeatureCandidate", feature != null ? feature.featureName : "", true);
            AppendStringArrayInline(sb, "frameDebuggerMeshNames", GetEventMeshNames(evt).ToArray(), true);
            AppendStringArrayInline(sb, "frameDebuggerMeshInstanceIds", evt.detailMeshInstanceIds != null ? evt.detailMeshInstanceIds.ToArray() : new string[0], true);
            AppendStringArrayInline(sb, "limitations", BuildEventLimitations(evt, uiBatch, srpBatch, particleCandidates), false);
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private string BuildAiRenderersJsonl()
    {
        var renderers = BuildSceneRendererRecords()
            .OrderBy(r => r.path)
            .ThenBy(r => r.subMeshIndex)
            .ToList();
        var sb = new StringBuilder(32768);
        foreach (var renderer in renderers)
        {
            AppendRendererRecordInline(sb, renderer);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string BuildAiRenderersCompactJsonl()
    {
        var renderers = BuildSceneRendererRecords()
            .OrderBy(r => r.path)
            .ThenBy(r => r.subMeshIndex)
            .ToList();
        var sb = new StringBuilder(32768);
        foreach (var renderer in renderers)
        {
            sb.Append("{");
            AppendJsonPropertyInline(sb, "rendererId", StableStringId("r", (renderer.path ?? "") + "#" + renderer.subMeshIndex.ToString(CultureInfo.InvariantCulture)), true);
            AppendJsonPropertyInline(sb, "source", renderer.source, true);
            AppendJsonPropertyInline(sb, "pathId", StableStringId("s", renderer.path), true);
            AppendJsonPropertyInline(sb, "name", renderer.name, true);
            AppendJsonPropertyInline(sb, "rendererType", renderer.rendererType, true);
            AppendJsonPropertyInline(sb, "layer", renderer.layer, true);
            AppendJsonPropertyInline(sb, "active", renderer.active, true);
            AppendJsonPropertyInline(sb, "enabled", renderer.enabled, true);
            AppendJsonPropertyInline(sb, "instanceId", renderer.instanceId, true);
            AppendJsonPropertyInline(sb, "meshName", renderer.meshName, true);
            AppendJsonPropertyInline(sb, "meshInstanceId", renderer.meshInstanceId, true);
            AppendJsonPropertyInline(sb, "meshPathId", StableStringId("s", !string.IsNullOrEmpty(renderer.meshPath) ? renderer.meshPath : renderer.meshName), true);
            AppendJsonPropertyInline(sb, "subMeshIndex", renderer.subMeshIndex, true);
            AppendJsonPropertyInline(sb, "materialName", renderer.materialName, true);
            AppendJsonPropertyInline(sb, "materialInstanceId", renderer.materialInstanceId, true);
            AppendJsonPropertyInline(sb, "materialId", StableStringId("mat", !string.IsNullOrEmpty(renderer.materialPath) ? renderer.materialPath : renderer.materialName), true);
            AppendJsonPropertyInline(sb, "shaderName", renderer.shaderName, true);
            AppendJsonPropertyInline(sb, "shaderId", StableStringId("sh", !string.IsNullOrEmpty(renderer.shaderPath) ? renderer.shaderPath : renderer.shaderName), true);
            AppendJsonPropertyInline(sb, "textureName", renderer.textureName, true);
            AppendJsonPropertyInline(sb, "textureId", StableStringId("tex", renderer.textureName), true);
            AppendJsonPropertyInline(sb, "textureMeta", renderer.textureMeta, true);
            AppendJsonPropertyInline(sb, "materialTextures", renderer.materialTextures, true);
            AppendJsonPropertyInline(sb, "renderQueue", renderer.renderQueue, true);
            AppendJsonPropertyInline(sb, "sortingLayerId", renderer.sortingLayerId, true);
            AppendJsonPropertyInline(sb, "sortingOrder", renderer.sortingOrder, true);
            AppendJsonPropertyInline(sb, "sortingGroup", renderer.sortingGroup, true);
            AppendJsonPropertyInline(sb, "rendererPriority", renderer.rendererPriority, true);
            AppendJsonPropertyInline(sb, "bounds", renderer.bounds, true);
            AppendJsonPropertyInline(sb, "materialFingerprint", renderer.materialFingerprint, true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", renderer.resourceFingerprint, true);
            AppendJsonPropertyInline(sb, "prefabRootId", StableStringId("s", renderer.prefabRoot), false);
            sb.Append("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendRendererRecordInline(StringBuilder sb, RendererRecord renderer)
    {
        sb.Append("{");
        AppendJsonPropertyInline(sb, "path", renderer != null ? renderer.path : "", true);
        AppendJsonPropertyInline(sb, "source", renderer != null ? renderer.source : "", true);
        AppendJsonPropertyInline(sb, "name", renderer != null ? renderer.name : "", true);
        AppendJsonPropertyInline(sb, "rendererType", renderer != null ? renderer.rendererType : "", true);
        AppendJsonPropertyInline(sb, "layer", renderer != null ? renderer.layer : 0, true);
        AppendJsonPropertyInline(sb, "active", renderer != null && renderer.active, true);
        AppendJsonPropertyInline(sb, "enabled", renderer != null && renderer.enabled, true);
        AppendJsonPropertyInline(sb, "instanceId", renderer != null ? renderer.instanceId : 0, true);
        AppendJsonPropertyInline(sb, "meshName", renderer != null ? renderer.meshName : "", true);
        AppendJsonPropertyInline(sb, "meshPath", renderer != null ? renderer.meshPath : "", true);
        AppendJsonPropertyInline(sb, "meshInstanceId", renderer != null ? renderer.meshInstanceId : 0, true);
        AppendJsonPropertyInline(sb, "subMeshIndex", renderer != null ? renderer.subMeshIndex : -1, true);
        AppendJsonPropertyInline(sb, "materialName", renderer != null ? renderer.materialName : "", true);
        AppendJsonPropertyInline(sb, "materialPath", renderer != null ? renderer.materialPath : "", true);
        AppendJsonPropertyInline(sb, "materialInstanceId", renderer != null ? renderer.materialInstanceId : 0, true);
        AppendJsonPropertyInline(sb, "shaderName", renderer != null ? renderer.shaderName : "", true);
        AppendJsonPropertyInline(sb, "shaderPath", renderer != null ? renderer.shaderPath : "", true);
        AppendJsonPropertyInline(sb, "textureName", renderer != null ? renderer.textureName : "", true);
        AppendJsonPropertyInline(sb, "textureMeta", renderer != null ? renderer.textureMeta : "", true);
        AppendJsonPropertyInline(sb, "materialTextures", renderer != null ? renderer.materialTextures : "", true);
        AppendJsonPropertyInline(sb, "renderQueue", renderer != null ? renderer.renderQueue : -1, true);
        AppendJsonPropertyInline(sb, "sortingLayerId", renderer != null ? renderer.sortingLayerId : 0, true);
        AppendJsonPropertyInline(sb, "sortingOrder", renderer != null ? renderer.sortingOrder : 0, true);
        AppendJsonPropertyInline(sb, "sortingGroup", renderer != null ? renderer.sortingGroup : "", true);
        AppendJsonPropertyInline(sb, "rendererPriority", renderer != null ? renderer.rendererPriority : 0, true);
        AppendJsonPropertyInline(sb, "bounds", renderer != null ? renderer.bounds : "", true);
        AppendJsonPropertyInline(sb, "materialFingerprint", renderer != null ? renderer.materialFingerprint : "", true);
        AppendJsonPropertyInline(sb, "resourceFingerprint", renderer != null ? renderer.resourceFingerprint : "", true);
        AppendJsonPropertyInline(sb, "prefabRoot", renderer != null ? renderer.prefabRoot : "", false);
        sb.Append("}");
    }

    private static void AppendUiGraphicRecordInline(StringBuilder sb, UiGraphicRecord graphic, bool includeBatchKey)
    {
        sb.Append("{");
        AppendJsonPropertyInline(sb, "path", graphic != null ? graphic.path : "", true);
        AppendJsonPropertyInline(sb, "source", graphic != null ? graphic.source : "", true);
        AppendJsonPropertyInline(sb, "type", graphic != null ? graphic.type : "", true);
        AppendJsonPropertyInline(sb, "canvasPath", graphic != null ? graphic.canvasPath : "", true);
        AppendJsonPropertyInline(sb, "rootCanvasPath", graphic != null ? graphic.rootCanvasPath : "", true);
        AppendJsonPropertyInline(sb, "canvasCamera", graphic != null ? graphic.canvasCamera : "", true);
        AppendJsonPropertyInline(sb, "canvasRenderMode", graphic != null ? graphic.canvasRenderMode : "", true);
        AppendJsonPropertyInline(sb, "canvasSortingLayerId", graphic != null ? graphic.canvasSortingLayerId : 0, true);
        AppendJsonPropertyInline(sb, "canvasSortingOrder", graphic != null ? graphic.canvasSortingOrder : 0, true);
        AppendJsonPropertyInline(sb, "depth", graphic != null ? graphic.depth : -1, true);
        AppendJsonPropertyInline(sb, "active", graphic != null && graphic.active, true);
        AppendJsonPropertyInline(sb, "enabled", graphic != null && graphic.enabled, true);
        AppendJsonPropertyInline(sb, "canvasEnabled", graphic != null && graphic.canvasEnabled, true);
        AppendJsonPropertyInline(sb, "raycastTarget", graphic != null && graphic.raycastTarget, true);
        AppendJsonPropertyInline(sb, "materialName", graphic != null ? graphic.materialName : "", true);
        AppendJsonPropertyInline(sb, "materialPath", graphic != null ? graphic.materialPath : "", true);
        AppendJsonPropertyInline(sb, "shaderName", graphic != null ? graphic.shaderName : "", true);
        AppendJsonPropertyInline(sb, "shaderPath", graphic != null ? graphic.shaderPath : "", true);
        AppendJsonPropertyInline(sb, "textureName", graphic != null ? graphic.textureName : "", true);
        AppendJsonPropertyInline(sb, "texturePath", graphic != null ? graphic.texturePath : "", true);
        AppendJsonPropertyInline(sb, "textureMeta", graphic != null ? graphic.textureMeta : "", true);
        AppendJsonPropertyInline(sb, "materialTextures", graphic != null ? graphic.materialTextures : "", true);
        AppendJsonPropertyInline(sb, "spriteName", graphic != null ? graphic.spriteName : "", true);
        AppendJsonPropertyInline(sb, "rect", graphic != null ? graphic.rect : "", true);
        AppendJsonPropertyInline(sb, "screenRect", graphic != null ? graphic.screenRect : "", true);
        AppendJsonPropertyInline(sb, "isTextComponent", graphic != null && graphic.isTextComponent, true);
        AppendJsonPropertyInline(sb, "textLength", graphic != null ? graphic.textLength : 0, true);
        AppendJsonPropertyInline(sb, "fontName", graphic != null ? graphic.fontName : "", true);
        AppendJsonPropertyInline(sb, "fontMaterialName", graphic != null ? graphic.fontMaterialName : "", true);
        AppendJsonPropertyInline(sb, "canvasRenderer", graphic != null ? graphic.canvasRenderer : "", true);
        AppendJsonPropertyInline(sb, "maskState", graphic != null ? graphic.maskState : "", true);
        AppendJsonPropertyInline(sb, "sortingGroup", graphic != null ? graphic.sortingGroup : "", true);
        AppendJsonPropertyInline(sb, "materialFingerprint", graphic != null ? graphic.materialFingerprint : "", true);
        AppendJsonPropertyInline(sb, "resourceFingerprint", graphic != null ? graphic.resourceFingerprint : "", true);
        AppendJsonPropertyInline(sb, "prefabAssetPath", graphic != null ? graphic.prefabAssetPath : "", includeBatchKey);
        if (includeBatchKey)
            AppendJsonPropertyInline(sb, "batchKey", graphic != null ? graphic.batchKey : "", false);
        sb.Append("}");
    }

    private static void AppendParticleCandidateInline(StringBuilder sb, ParticleCandidate particle, bool includeMatchFields)
    {
        sb.Append("{");
        AppendJsonPropertyInline(sb, "id", particle != null ? particle.id : 0, true);
        AppendJsonPropertyInline(sb, "source", particle != null ? particle.source : "", true);
        AppendJsonPropertyInline(sb, "path", particle != null ? particle.path : "", true);
        AppendJsonPropertyInline(sb, "materialName", particle != null ? particle.materialName : "", true);
        AppendJsonPropertyInline(sb, "materialPath", particle != null ? particle.materialPath : "", true);
        AppendJsonPropertyInline(sb, "shaderName", particle != null ? particle.shaderName : "", true);
        AppendJsonPropertyInline(sb, "aliveParticles", particle != null ? particle.aliveParticles : 0, true);
        AppendJsonPropertyInline(sb, "maxParticles", particle != null ? particle.maxParticles : 0, true);
        AppendJsonPropertyInline(sb, "rendererEnabled", particle != null && particle.rendererEnabled, true);
        AppendJsonPropertyInline(sb, "rendererMode", particle != null ? particle.rendererMode : "", true);
        AppendJsonPropertyInline(sb, "sortingLayerId", particle != null ? particle.sortingLayerId : 0, true);
        AppendJsonPropertyInline(sb, "sortingOrder", particle != null ? particle.sortingOrder : 0, true);
        AppendJsonPropertyInline(sb, "textureName", particle != null ? particle.textureName : "", true);
        AppendJsonPropertyInline(sb, "textureMeta", particle != null ? particle.textureMeta : "", true);
        AppendJsonPropertyInline(sb, "materialTextures", particle != null ? particle.materialTextures : "", true);
        AppendJsonPropertyInline(sb, "bounds", particle != null ? particle.bounds : "", true);
        AppendJsonPropertyInline(sb, "sortingGroup", particle != null ? particle.sortingGroup : "", true);
        AppendJsonPropertyInline(sb, "materialFingerprint", particle != null ? particle.materialFingerprint : "", true);
        AppendJsonPropertyInline(sb, "resourceFingerprint", particle != null ? particle.resourceFingerprint : "", true);
        AppendJsonPropertyInline(sb, "confidence", particle != null ? particle.confidence : "", true);
        AppendJsonPropertyInline(sb, "reason", particle != null ? particle.reason : "", includeMatchFields);
        if (includeMatchFields)
        {
            AppendJsonPropertyInline(sb, "mappedToFrameEvent", particle != null && particle.mappedToFrameEvent, true);
            AppendJsonPropertyInline(sb, "matchScore", particle != null ? particle.matchScore : 0, true);
            AppendStringArrayInline(sb, "matchedBy", particle != null ? particle.matchedBy : new string[0], false);
        }
        sb.Append("}");
    }

    private static void AppendRenderFeatureSnapshotInline(StringBuilder sb, RenderFeatureSnapshot feature)
    {
        sb.Append("{");
        AppendJsonPropertyInline(sb, "rendererName", feature != null ? feature.rendererName : "", true);
        AppendJsonPropertyInline(sb, "rendererType", feature != null ? feature.rendererType : "", true);
        AppendJsonPropertyInline(sb, "rendererAssetPath", feature != null ? feature.rendererAssetPath : "", true);
        AppendJsonPropertyInline(sb, "featureName", feature != null ? feature.featureName : "", true);
        AppendJsonPropertyInline(sb, "featureType", feature != null ? feature.featureType : "", true);
        AppendJsonPropertyInline(sb, "active", feature != null && feature.active, true);
        AppendJsonPropertyInline(sb, "featureAssetPath", feature != null ? feature.featureAssetPath : "", true);
        AppendJsonPropertyInline(sb, "materialShaderRefs", feature != null ? feature.materialShaderRefs : "", false);
        sb.Append("}");
    }

    private static void AppendRuntimeCameraRecordInline(StringBuilder sb, RuntimeCameraRecord camera)
    {
        sb.Append("{");
        AppendJsonPropertyInline(sb, "source", "runtime_player_snapshot", true);
        AppendJsonPropertyInline(sb, "path", camera != null ? camera.path : "", true);
        AppendJsonPropertyInline(sb, "name", camera != null ? camera.name : "", true);
        AppendJsonPropertyInline(sb, "enabled", camera != null && camera.enabled, true);
        AppendJsonPropertyInline(sb, "activeInHierarchy", camera != null && camera.activeInHierarchy, true);
        AppendJsonPropertyInline(sb, "depth", camera != null ? camera.depth.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "clearFlags", camera != null ? camera.clearFlags : "", true);
        AppendJsonPropertyInline(sb, "cullingMaskRaw", camera != null ? camera.cullingMask : -1, true);
        AppendJsonPropertyInline(sb, "cullingMaskNames", camera != null ? camera.cullingMaskNames : "", true);
        AppendJsonPropertyInline(sb, "orthographic", camera != null && camera.orthographic, true);
        AppendJsonPropertyInline(sb, "orthographicSize", camera != null ? camera.orthographicSize.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "fieldOfView", camera != null ? camera.fieldOfView.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "nearClipPlane", camera != null ? camera.nearClipPlane.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "farClipPlane", camera != null ? camera.farClipPlane.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "allowHDR", camera != null && camera.allowHDR, true);
        AppendJsonPropertyInline(sb, "allowMSAA", camera != null && camera.allowMSAA, true);
        AppendJsonPropertyInline(sb, "pixelWidth", camera != null ? camera.pixelWidth : 0, true);
        AppendJsonPropertyInline(sb, "pixelHeight", camera != null ? camera.pixelHeight : 0, true);
        AppendJsonPropertyInline(sb, "scaledPixelWidth", camera != null ? camera.scaledPixelWidth : 0, true);
        AppendJsonPropertyInline(sb, "scaledPixelHeight", camera != null ? camera.scaledPixelHeight : 0, true);
        AppendJsonPropertyInline(sb, "aspect", camera != null ? camera.aspect.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "rect", camera != null ? camera.rect : "", true);
        AppendJsonPropertyInline(sb, "cameraType", camera != null ? camera.cameraType : "", true);
        AppendJsonPropertyInline(sb, "actualRenderingPath", camera != null ? camera.actualRenderingPath : "", true);
        AppendJsonPropertyInline(sb, "renderingPath", camera != null ? camera.renderingPath : "", true);
        AppendJsonPropertyInline(sb, "useOcclusionCulling", camera != null && camera.useOcclusionCulling, true);
        AppendJsonPropertyInline(sb, "targetTexture", camera != null ? camera.targetTexture : "", true);
        AppendJsonPropertyInline(sb, "targetTextureMeta", camera != null ? camera.targetTextureMeta : "", true);
        AppendJsonPropertyInline(sb, "pixelRect", camera != null ? camera.pixelRect : "", true);
        AppendJsonPropertyInline(sb, "urpAdditionalData", camera != null ? camera.urpAdditionalData : "", false);
        sb.Append("}");
    }

    private static void AppendSceneCameraInline(StringBuilder sb, Camera camera)
    {
        Component additionalData = camera != null ? camera.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name.Contains("UniversalAdditionalCameraData")) : null;
        sb.Append("{");
        AppendJsonPropertyInline(sb, "source", "editor_scene_snapshot", true);
        AppendJsonPropertyInline(sb, "path", camera != null ? GetHierarchyPath(camera.gameObject) : "", true);
        AppendJsonPropertyInline(sb, "name", camera != null ? camera.name : "", true);
        AppendJsonPropertyInline(sb, "enabled", camera != null && camera.enabled, true);
        AppendJsonPropertyInline(sb, "activeInHierarchy", camera != null && camera.gameObject.activeInHierarchy, true);
        AppendJsonPropertyInline(sb, "depth", camera != null ? camera.depth.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "clearFlags", camera != null ? camera.clearFlags.ToString() : "", true);
        AppendJsonPropertyInline(sb, "cullingMaskRaw", camera != null ? camera.cullingMask : -1, true);
        AppendJsonPropertyInline(sb, "cullingMaskNames", camera != null ? LayerMaskToNames(camera.cullingMask) : "", true);
        AppendJsonPropertyInline(sb, "orthographic", camera != null && camera.orthographic, true);
        AppendJsonPropertyInline(sb, "orthographicSize", camera != null ? camera.orthographicSize.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "fieldOfView", camera != null ? camera.fieldOfView.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "nearClipPlane", camera != null ? camera.nearClipPlane.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "farClipPlane", camera != null ? camera.farClipPlane.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "allowHDR", camera != null && camera.allowHDR, true);
        AppendJsonPropertyInline(sb, "allowMSAA", camera != null && camera.allowMSAA, true);
        AppendJsonPropertyInline(sb, "pixelWidth", camera != null ? camera.pixelWidth : 0, true);
        AppendJsonPropertyInline(sb, "pixelHeight", camera != null ? camera.pixelHeight : 0, true);
        AppendJsonPropertyInline(sb, "scaledPixelWidth", camera != null ? camera.scaledPixelWidth : 0, true);
        AppendJsonPropertyInline(sb, "scaledPixelHeight", camera != null ? camera.scaledPixelHeight : 0, true);
        AppendJsonPropertyInline(sb, "aspect", camera != null ? camera.aspect.ToString("0.###", CultureInfo.InvariantCulture) : "0", true);
        AppendJsonPropertyInline(sb, "rect", camera != null ? camera.rect.ToString() : "", true);
        AppendJsonPropertyInline(sb, "cameraType", camera != null ? camera.cameraType.ToString() : "", true);
        AppendJsonPropertyInline(sb, "actualRenderingPath", camera != null ? camera.actualRenderingPath.ToString() : "", true);
        AppendJsonPropertyInline(sb, "renderingPath", camera != null ? camera.renderingPath.ToString() : "", true);
        AppendJsonPropertyInline(sb, "useOcclusionCulling", camera != null && camera.useOcclusionCulling, true);
        AppendJsonPropertyInline(sb, "targetTexture", camera != null && camera.targetTexture != null ? camera.targetTexture.name : "", true);
        AppendJsonPropertyInline(sb, "targetTextureMeta", camera != null && camera.targetTexture != null ? TextureSummary(camera.targetTexture) : "", true);
        AppendJsonPropertyInline(sb, "pixelRect", camera != null ? camera.pixelRect.ToString() : "", true);
        AppendJsonPropertyInline(sb, "urpAdditionalData", additionalData != null ? SummarizeObjectFields(additionalData, 24) : "", true);
        AppendJsonPropertyInline(sb, "prefabAssetPath", camera != null ? GetPrefabAssetPath(camera.gameObject) : "", false);
        sb.Append("}");
    }

    private string BuildAiRuntimeResolutionSnapshotJson()
    {
        var runtime = GetRuntimePlayerSnapshot();
        JObject runtimeRoot = TryParseJObject(s_exportCache != null ? s_exportCache.runtimeSnapshotJson : "");
        var finalBlitEvents = (_capturedEvents ?? new List<FrameEventInfo>())
            .Where(e => e != null && (e.eventName.IndexOf("FinalBlit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      e.passName.IndexOf("Blit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      (e.rawDataFields != null && e.rawDataFields.Any(kv => kv.Key.IndexOf("BackBuffer", StringComparison.OrdinalIgnoreCase) >= 0 && string.Equals(kv.Value, "True", StringComparison.OrdinalIgnoreCase)))))
            .Take(8)
            .ToList();

        var sb = new StringBuilder(4096);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-runtime-resolution-snapshot/v1", true);
        WriteJsonProperty(sb, "purpose", "One-capture evidence for FinalBlit, GameView/backbuffer, runtime screen/display, camera pixel size, URP renderScale, and RenderDoc output target size. Use this before classifying size mismatch as project issue.", true);
        WriteJsonProperty(sb, "runtimeSnapshotStatus", runtime != null ? runtime.status : "empty", true);
        WriteJsonProperty(sb, "runtimeSnapshotAvailable", runtime != null && runtime.available, true);
        WriteJsonProperty(sb, "linkedCaptureGameViewSize", _linkedCaptureContext != null ? _linkedCaptureContext.gameViewSize : "", true);
        WriteJsonProperty(sb, "frameDebuggerEventCount", _capturedEvents != null ? _capturedEvents.Count : 0, true);
        AppendRawJsonProperty(sb, "runtimeResolution", runtimeRoot != null ? runtimeRoot["resolution"] : null, true, 2);
        AppendFinalBlitEventsArray(sb, finalBlitEvents, true);
        AppendResolutionCamerasArray(sb, runtime, true);
        AppendRenderDocOutputTargetsArray(sb, true);
        WriteJsonProperty(sb, "classificationRule", "Unity FinalBlit evidence proves the final blit exists. A project-side resolution issue requires matching project camera/render-scale/output configuration or screenshot/pixel-history evidence; otherwise treat swapchain/window/intermediate RT mismatch as capture_data_quality/P3.", false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiTransparentSubmissionSnapshotJson()
    {
        var runtime = GetRuntimePlayerSnapshot();
        var transparentRenderers = BuildSceneRendererRecords()
            .Where(IsTransparentRendererCandidate)
            .OrderByDescending(r => r.renderQueue)
            .ThenBy(r => r.shaderName)
            .ThenBy(r => r.path)
            .Take(1024)
            .ToList();
        var uiGraphics = BuildUiGraphicRecords()
            .Where(g => g != null && g.active && g.enabled)
            .OrderBy(g => g.canvasSortingLayerId)
            .ThenBy(g => g.canvasSortingOrder)
            .ThenBy(g => g.depth)
            .Take(1024)
            .ToList();
        var particles = GetActiveParticleCandidates()
            .Where(p => p != null && p.rendererEnabled && (p.aliveParticles > 0 || IsTransparentMaterial(p.materialName, p.shaderName, -1)))
            .OrderByDescending(p => p.aliveParticles)
            .ThenBy(p => p.path)
            .Take(1024)
            .ToList();

        var sb = new StringBuilder(1024 * 128);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-transparent-submission-snapshot/v1", true);
        WriteJsonProperty(sb, "purpose", "One-capture evidence for transparent/UI/particle submission attribution. Use this with RenderDoc TransparentLike passes before saying object ownership is missing.", true);
        WriteJsonProperty(sb, "runtimeSnapshotStatus", runtime != null ? runtime.status : "empty", true);
        WriteJsonProperty(sb, "runtimeSnapshotAvailable", runtime != null && runtime.available, true);
        WriteJsonProperty(sb, "source", runtime != null && runtime.available ? "runtime_player_snapshot" : "editor_scene_snapshot", true);
        sb.AppendLine("  \"counts\": {");
        WriteJsonProperty(sb, "transparentRendererCandidates", transparentRenderers.Count, true, 4);
        WriteJsonProperty(sb, "activeUiGraphics", uiGraphics.Count, true, 4);
        WriteJsonProperty(sb, "activeParticleCandidates", particles.Count, false, 4);
        sb.AppendLine("  },");
        AppendStringStatsArray(sb, "topTransparentShaders", transparentRenderers.Select(r => r.shaderName).Concat(uiGraphics.Select(g => g.shaderName)).Concat(particles.Select(p => p.shaderName)), true);
        AppendStringStatsArray(sb, "topTransparentMaterials", transparentRenderers.Select(r => r.materialName).Concat(uiGraphics.Select(g => g.materialName)).Concat(particles.Select(p => p.materialName)), true);
        AppendRenderDocTransparentPassesArray(sb, true);
        AppendTransparentRendererCandidatesArray(sb, transparentRenderers, true);
        AppendUiGraphicsCandidatesArray(sb, uiGraphics, true);
        AppendParticleCandidatesArray(sb, particles, false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static JObject TryParseJObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JObject.Parse(json); }
        catch { return null; }
    }

    private static void AppendRawJsonProperty(StringBuilder sb, string name, JToken token, bool trailingComma, int indent)
    {
        sb.Append(new string(' ', indent)).Append('"').Append(EscapeJson(name)).Append("\": ");
        sb.Append(token != null ? token.ToString(Newtonsoft.Json.Formatting.None) : "null");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendFinalBlitEventsArray(StringBuilder sb, List<FrameEventInfo> events, bool trailingComma)
    {
        sb.AppendLine("  \"frameDebuggerFinalBlitEvents\": [");
        for (int i = 0; events != null && i < events.Count; i++)
        {
            var evt = events[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "eventIndex", evt.index + 1, true);
            AppendJsonPropertyInline(sb, "eventName", evt.eventName, true);
            AppendJsonPropertyInline(sb, "type", evt.typeName, true);
            AppendJsonPropertyInline(sb, "passName", evt.passName, true);
            AppendJsonPropertyInline(sb, "renderTargetName", evt.renderTargetName, true);
            AppendJsonPropertyInline(sb, "renderTargetWidth", evt.renderTargetWidth, true);
            AppendJsonPropertyInline(sb, "renderTargetHeight", evt.renderTargetHeight, true);
            AppendJsonPropertyInline(sb, "rawRealShaderName", GetRawField(evt, "m_RealShaderName"), true);
            AppendJsonPropertyInline(sb, "rawOriginalShaderName", GetRawField(evt, "m_OriginalShaderName"), true);
            AppendJsonPropertyInline(sb, "renderTargetIsBackBuffer", GetRawField(evt, "m_RenderTargetIsBackBuffer"), true);
            AppendJsonPropertyInline(sb, "renderTargetRenderTexture", GetRawField(evt, "m_RenderTargetRenderTexture"), false);
            sb.Append("}");
            sb.AppendLine(i + 1 < events.Count ? "," : "");
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static string GetRawField(FrameEventInfo evt, string name)
    {
        if (evt == null || string.IsNullOrEmpty(name)) return "";
        if (evt.rawDataFields != null && evt.rawDataFields.TryGetValue(name, out string rawValue)) return rawValue;
        if (evt.extraFields != null && evt.extraFields.TryGetValue("data." + name, out string extraValue)) return extraValue;
        return "";
    }

    private static void AppendResolutionCamerasArray(StringBuilder sb, RuntimePlayerSnapshot runtime, bool trailingComma)
    {
        sb.AppendLine("  \"cameras\": [");
        if (runtime != null && runtime.available)
        {
            for (int i = 0; i < runtime.cameras.Count; i++)
            {
                sb.Append("    ");
                AppendRuntimeCameraRecordInline(sb, runtime.cameras[i]);
                sb.AppendLine(i + 1 < runtime.cameras.Count ? "," : "");
            }
        }
        else
        {
            var cameras = GetSceneCameras();
            for (int i = 0; i < cameras.Count; i++)
            {
                sb.Append("    ");
                AppendSceneCameraInline(sb, cameras[i]);
                sb.AppendLine(i + 1 < cameras.Count ? "," : "");
            }
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private void AppendRenderDocOutputTargetsArray(StringBuilder sb, bool trailingComma)
    {
        sb.AppendLine("  \"renderDocOutputTargets\": [");
        var passes = ReadRenderDocPassTable();
        var selected = passes
            .Where(p => IsRenderDocOutputPass(p))
            .Take(24)
            .ToList();
        for (int i = 0; i < selected.Count; i++)
        {
            AppendRenderDocPassSummaryInline(sb, selected[i], 4);
            sb.AppendLine(i + 1 < selected.Count ? "," : "");
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private void AppendRenderDocTransparentPassesArray(StringBuilder sb, bool trailingComma)
    {
        sb.AppendLine("  \"renderDocTransparentPasses\": [");
        var passes = ReadRenderDocPassTable()
            .Where(p => TokenString(p["inferredRole"]).IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(p => TokenInt(p["drawCount"], 0))
            .Take(64)
            .ToList();
        for (int i = 0; i < passes.Count; i++)
        {
            AppendRenderDocPassSummaryInline(sb, passes[i], 4);
            sb.AppendLine(i + 1 < passes.Count ? "," : "");
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private List<JObject> ReadRenderDocPassTable()
    {
        var result = new List<JObject>();
        string dir = s_exportCache != null ? s_exportCache.renderDocAnalysisDirectory : "";
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;
        string path = Path.Combine(dir, "pass_table.json");
        if (!File.Exists(path)) return result;
        try
        {
            JToken root = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
            JArray array = root as JArray ?? root["passes"] as JArray ?? new JArray();
            foreach (var token in array)
            {
                if (token is JObject obj) result.Add(obj);
            }
        }
        catch { }
        return result;
    }

    private static bool IsRenderDocOutputPass(JObject pass)
    {
        if (pass == null) return false;
        string role = TokenString(pass["inferredRole"]);
        if (role.IndexOf("PostProcess", StringComparison.OrdinalIgnoreCase) >= 0 && TokenInt(pass["drawCount"], 0) <= 4)
            return true;
        foreach (var target in (pass["colorTargets"] as JArray) ?? new JArray())
        {
            string name = TokenString(target["name"]);
            if (name.IndexOf("Swapchain", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static void AppendRenderDocPassSummaryInline(StringBuilder sb, JObject pass, int indent)
    {
        sb.Append(new string(' ', indent)).Append("{");
        AppendJsonPropertyInline(sb, "passId", TokenString(pass["passId"]), true);
        AppendJsonPropertyInline(sb, "eventStart", TokenInt(pass["eventStart"], 0), true);
        AppendJsonPropertyInline(sb, "eventEnd", TokenInt(pass["eventEnd"], 0), true);
        AppendJsonPropertyInline(sb, "drawCount", TokenInt(pass["drawCount"], 0), true);
        AppendJsonPropertyInline(sb, "stateChangeCount", ((pass["stateChangeEvents"] as JArray) ?? new JArray()).Count, true);
        AppendJsonPropertyInline(sb, "inferredRole", TokenString(pass["inferredRole"]), true);
        AppendJsonPropertyInline(sb, "colorTargets", CompactTargets(pass["colorTargets"] as JArray), true);
        AppendJsonPropertyInline(sb, "depthTarget", CompactTarget(pass["depthTarget"] as JObject), true);
        AppendJsonPropertyInline(sb, "sampleEvents", CompactIntArray(pass["pipelineSampleEvents"] as JArray, 20), true);
        AppendJsonPropertyInline(sb, "readResources", CompactResources(pass["readResources"] as JArray, 12), true);
        AppendJsonPropertyInline(sb, "writeResources", CompactResources(pass["writeResources"] as JArray, 12), false);
        sb.Append("}");
    }

    private static string CompactTargets(JArray targets)
    {
        if (targets == null) return "";
        return string.Join(" | ", targets.OfType<JObject>().Select(CompactTarget).Where(s => !string.IsNullOrEmpty(s)));
    }

    private static string CompactTarget(JObject target)
    {
        if (target == null) return "";
        return TokenString(target["resourceId"]) + ":" + TokenString(target["name"]) + "[" + TokenString(target["format"]) + ";" + TokenInt(target["width"], 0) + "x" + TokenInt(target["height"], 0) + "]";
    }

    private static string CompactResources(JArray resources, int maxItems)
    {
        if (resources == null) return "";
        return string.Join(" | ", resources.OfType<JObject>().Take(maxItems).Select(r =>
            TokenString(r["resourceId"]) + ":" + TokenString(r["name"]) + "[" + TokenString(r["format"]) + ";" + TokenInt(r["width"], 0) + "x" + TokenInt(r["height"], 0) + ";count=" + TokenInt(r["count"], 0) + "]"));
    }

    private static string CompactIntArray(JArray array, int maxItems)
    {
        if (array == null) return "";
        return string.Join(",", array.Take(maxItems).Select(t => TokenInt(t, 0).ToString(CultureInfo.InvariantCulture)));
    }

    private static bool IsTransparentRendererCandidate(RendererRecord record)
    {
        if (record == null || !record.active || !record.enabled) return false;
        return IsTransparentMaterial(record.materialName, record.shaderName, record.renderQueue) ||
               record.rendererType.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTransparentMaterial(string materialName, string shaderName, int renderQueue)
    {
        if (renderQueue >= 2500) return true;
        string text = (materialName ?? "") + " " + (shaderName ?? "");
        return text.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("UI/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void AppendStringStatsArray(StringBuilder sb, string name, IEnumerable<string> values, bool trailingComma)
    {
        var stats = (values ?? Enumerable.Empty<string>())
            .Where(v => !string.IsNullOrEmpty(v))
            .GroupBy(v => v)
            .Select(g => new { value = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ThenBy(x => x.value)
            .Take(32)
            .ToList();
        sb.Append("  \"").Append(EscapeJson(name)).Append("\": [");
        for (int i = 0; i < stats.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{");
            AppendJsonPropertyInline(sb, "value", stats[i].value, true);
            AppendJsonPropertyInline(sb, "count", stats[i].count, false);
            sb.Append("}");
        }
        sb.Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTransparentRendererCandidatesArray(StringBuilder sb, List<RendererRecord> renderers, bool trailingComma)
    {
        sb.AppendLine("  \"transparentRendererCandidates\": [");
        for (int i = 0; renderers != null && i < renderers.Count; i++)
        {
            var r = renderers[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "source", r.source, true);
            AppendJsonPropertyInline(sb, "path", r.path, true);
            AppendJsonPropertyInline(sb, "rendererType", r.rendererType, true);
            AppendJsonPropertyInline(sb, "layer", r.layer, true);
            AppendJsonPropertyInline(sb, "materialName", r.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", r.shaderName, true);
            AppendJsonPropertyInline(sb, "renderQueue", r.renderQueue, true);
            AppendJsonPropertyInline(sb, "sortingLayerId", r.sortingLayerId, true);
            AppendJsonPropertyInline(sb, "sortingOrder", r.sortingOrder, true);
            AppendJsonPropertyInline(sb, "textureName", r.textureName, true);
            AppendJsonPropertyInline(sb, "bounds", r.bounds, true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", r.resourceFingerprint, false);
            sb.Append("}");
            sb.AppendLine(i + 1 < renderers.Count ? "," : "");
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendUiGraphicsCandidatesArray(StringBuilder sb, List<UiGraphicRecord> graphics, bool trailingComma)
    {
        sb.AppendLine("  \"uiGraphicCandidates\": [");
        for (int i = 0; graphics != null && i < graphics.Count; i++)
        {
            var g = graphics[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "source", g.source, true);
            AppendJsonPropertyInline(sb, "path", g.path, true);
            AppendJsonPropertyInline(sb, "type", g.type, true);
            AppendJsonPropertyInline(sb, "canvasPath", g.canvasPath, true);
            AppendJsonPropertyInline(sb, "canvasCamera", g.canvasCamera, true);
            AppendJsonPropertyInline(sb, "canvasRenderMode", g.canvasRenderMode, true);
            AppendJsonPropertyInline(sb, "canvasSortingLayerId", g.canvasSortingLayerId, true);
            AppendJsonPropertyInline(sb, "canvasSortingOrder", g.canvasSortingOrder, true);
            AppendJsonPropertyInline(sb, "depth", g.depth, true);
            AppendJsonPropertyInline(sb, "materialName", g.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", g.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", g.textureName, true);
            AppendJsonPropertyInline(sb, "screenRect", g.screenRect, true);
            AppendJsonPropertyInline(sb, "maskState", g.maskState, true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", g.resourceFingerprint, false);
            sb.Append("}");
            sb.AppendLine(i + 1 < graphics.Count ? "," : "");
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendParticleCandidatesArray(StringBuilder sb, List<ParticleCandidate> particles, bool trailingComma)
    {
        sb.AppendLine("  \"particleCandidates\": [");
        for (int i = 0; particles != null && i < particles.Count; i++)
        {
            var p = particles[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "source", p.source, true);
            AppendJsonPropertyInline(sb, "path", p.path, true);
            AppendJsonPropertyInline(sb, "aliveParticles", p.aliveParticles, true);
            AppendJsonPropertyInline(sb, "maxParticles", p.maxParticles, true);
            AppendJsonPropertyInline(sb, "rendererMode", p.rendererMode, true);
            AppendJsonPropertyInline(sb, "materialName", p.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", p.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", p.textureName, true);
            AppendJsonPropertyInline(sb, "bounds", p.bounds, true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", p.resourceFingerprint, false);
            sb.Append("}");
            sb.AppendLine(i + 1 < particles.Count ? "," : "");
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static int CountAttributionCandidates(UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates, RenderFeatureSnapshot renderFeature)
    {
        int count = 0;
        if (uiBatch != null) count++;
        if (srpBatch != null) count += Math.Max(1, srpBatch.renderers != null ? srpBatch.renderers.Count : 0);
        if (particleCandidates != null) count += particleCandidates.Count;
        if (renderFeature != null) count++;
        return count;
    }

    private string BuildAiResourceFingerprintsJson()
    {
        var runtime = GetRuntimePlayerSnapshot();
        var renderers = BuildSceneRendererRecords()
            .Where(r => r != null && !string.IsNullOrEmpty(r.resourceFingerprint))
            .OrderBy(r => r.path)
            .ThenBy(r => r.subMeshIndex)
            .Take(2048)
            .ToList();
        var graphics = BuildUiGraphicRecords()
            .Where(g => g != null && !string.IsNullOrEmpty(g.resourceFingerprint))
            .OrderBy(g => g.canvasSortingLayerId)
            .ThenBy(g => g.canvasSortingOrder)
            .ThenBy(g => g.depth)
            .Take(2048)
            .ToList();
        var particles = GetActiveParticleCandidates()
            .Where(p => p != null && !string.IsNullOrEmpty(p.resourceFingerprint))
            .OrderByDescending(p => p.aliveParticles)
            .ThenBy(p => p.path)
            .Take(1024)
            .ToList();

        var sb = new StringBuilder(65536);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-resource-fingerprints/v1", true);
        WriteJsonProperty(sb, "purpose", "Object/resource fingerprints for matching Unity runtime/editor inventory to RenderDoc resources and draws. This is attribution evidence only, not timing evidence.", true);
        WriteJsonProperty(sb, "source", runtime.available ? "runtime_player_snapshot" : "editor_scene_snapshot", true);
        WriteJsonProperty(sb, "rendererFingerprintCount", renderers.Count, true);
        WriteJsonProperty(sb, "uiFingerprintCount", graphics.Count, true);
        WriteJsonProperty(sb, "particleFingerprintCount", particles.Count, true);
        WriteJsonProperty(sb, "textureCount", runtime.textures.Count, true);

        sb.AppendLine("  \"renderers\": [");
        for (int i = 0; i < renderers.Count; i++)
        {
            var r = renderers[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", r.path, true);
            AppendJsonPropertyInline(sb, "source", r.source, true);
            AppendJsonPropertyInline(sb, "meshName", r.meshName, true);
            AppendJsonPropertyInline(sb, "meshInstanceId", r.meshInstanceId, true);
            AppendJsonPropertyInline(sb, "materialName", r.materialName, true);
            AppendJsonPropertyInline(sb, "materialInstanceId", r.materialInstanceId, true);
            AppendJsonPropertyInline(sb, "shaderName", r.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", r.textureName, true);
            AppendJsonPropertyInline(sb, "materialTextures", r.materialTextures, true);
            AppendJsonPropertyInline(sb, "renderQueue", r.renderQueue, true);
            AppendJsonPropertyInline(sb, "bounds", r.bounds, true);
            AppendJsonPropertyInline(sb, "sortingGroup", r.sortingGroup, true);
            AppendJsonPropertyInline(sb, "fingerprint", r.resourceFingerprint, false);
            sb.Append(i + 1 < renderers.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"uiGraphics\": [");
        for (int i = 0; i < graphics.Count; i++)
        {
            var g = graphics[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", g.path, true);
            AppendJsonPropertyInline(sb, "source", g.source, true);
            AppendJsonPropertyInline(sb, "type", g.type, true);
            AppendJsonPropertyInline(sb, "canvasPath", g.canvasPath, true);
            AppendJsonPropertyInline(sb, "depth", g.depth, true);
            AppendJsonPropertyInline(sb, "materialName", g.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", g.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", g.textureName, true);
            AppendJsonPropertyInline(sb, "materialTextures", g.materialTextures, true);
            AppendJsonPropertyInline(sb, "spriteName", g.spriteName, true);
            AppendJsonPropertyInline(sb, "screenRect", g.screenRect, true);
            AppendJsonPropertyInline(sb, "maskState", g.maskState, true);
            AppendJsonPropertyInline(sb, "sortingGroup", g.sortingGroup, true);
            AppendJsonPropertyInline(sb, "isTextComponent", g.isTextComponent, true);
            AppendJsonPropertyInline(sb, "fontName", g.fontName, true);
            AppendJsonPropertyInline(sb, "fingerprint", g.resourceFingerprint, false);
            sb.Append(i + 1 < graphics.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"particles\": [");
        for (int i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", p.path, true);
            AppendJsonPropertyInline(sb, "source", p.source, true);
            AppendJsonPropertyInline(sb, "aliveParticles", p.aliveParticles, true);
            AppendJsonPropertyInline(sb, "materialName", p.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", p.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", p.textureName, true);
            AppendJsonPropertyInline(sb, "materialTextures", p.materialTextures, true);
            AppendJsonPropertyInline(sb, "rendererMode", p.rendererMode, true);
            AppendJsonPropertyInline(sb, "sortingOrder", p.sortingOrder, true);
            AppendJsonPropertyInline(sb, "sortingGroup", p.sortingGroup, true);
            AppendJsonPropertyInline(sb, "bounds", p.bounds, true);
            AppendJsonPropertyInline(sb, "fingerprint", p.resourceFingerprint, false);
            sb.Append(i + 1 < particles.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"textures\": [");
        for (int i = 0; i < runtime.textures.Count; i++)
        {
            var t = runtime.textures[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "name", t.name, true);
            AppendJsonPropertyInline(sb, "type", t.type, true);
            AppendJsonPropertyInline(sb, "instanceId", t.instanceId, true);
            AppendJsonPropertyInline(sb, "width", t.width, true);
            AppendJsonPropertyInline(sb, "height", t.height, true);
            AppendJsonPropertyInline(sb, "dimension", t.dimension, true);
            AppendJsonPropertyInline(sb, "format", t.format, true);
            AppendJsonPropertyInline(sb, "mipMapCount", t.mipMapCount, true);
            AppendJsonPropertyInline(sb, "antiAliasing", t.antiAliasing, true);
            AppendJsonPropertyInline(sb, "depth", t.depth, true);
            AppendJsonPropertyInline(sb, "memoryBytes", t.memoryBytes, false);
            sb.Append(i + 1 < runtime.textures.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiUnityRenderDocCorrelationSeedJson()
    {
        var ctx = _linkedCaptureContext;
        string renderDocDir = ctx != null ? ctx.renderDocAnalysisDirectory : "";
        string resourcePath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "resource_table.json") : "";
        string passPath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "pass_table.json") : "";
        var unityCandidates = BuildUnityCorrelationCandidates();
        var resourceObjects = LoadJsonObjectsSafe(resourcePath, 4096);
        var passObjects = LoadJsonObjectsSafe(passPath, 4096);

        var sb = new StringBuilder(65536);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-unity-renderdoc-correlation-seed/v1", true);
        WriteJsonProperty(sb, "purpose", "Precomputed candidate matches between Unity resource fingerprints and RenderDoc resource/pass tables. Candidate evidence only; do not treat as direct event identity.", true);
        bool renderDocAvailable = IsRenderDocAnalysisAvailable(ctx, resourcePath, passPath);
        WriteJsonProperty(sb, "status", renderDocAvailable ? "available" : "renderdoc_analysis_unavailable", true);
        WriteJsonProperty(sb, "matchPolicy", "Strong matches require texture/material/shader text or UI screenRect-to-viewport evidence. Width/height-only matches are emitted only as weak matches and must not drive report conclusions.", true);
        WriteJsonProperty(sb, "renderDocAnalysisDirectory", renderDocDir, true);
        WriteJsonProperty(sb, "resourceTable", File.Exists(resourcePath) ? resourcePath : "", true);
        WriteJsonProperty(sb, "passTable", File.Exists(passPath) ? passPath : "", true);
        WriteJsonProperty(sb, "unityCandidateCount", unityCandidates.Count, true);
        WriteJsonProperty(sb, "renderDocResourceObjectCount", resourceObjects.Count, true);
        WriteJsonProperty(sb, "renderDocPassObjectCount", passObjects.Count, true);

        var resourceAllMatches = BuildCorrelationMatches(unityCandidates, resourceObjects, 384, 5, false);
        var resourceMatches = resourceAllMatches.Where(IsStrongCorrelationMatch).Take(384).ToList();
        var weakResourceMatches = resourceAllMatches.Where(IsWeakOnlyCorrelationMatch).Take(96).ToList();
        sb.AppendLine("  \"resourceMatches\": [");
        for (int i = 0; i < resourceMatches.Count; i++)
        {
            AppendCorrelationMatchObject(sb, resourceMatches[i], "    ");
            sb.Append(i + 1 < resourceMatches.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        var passAllMatches = BuildCorrelationMatches(unityCandidates, passObjects, 384, 5, false);
        var passMatches = passAllMatches.Where(IsStrongCorrelationMatch).Take(384).ToList();
        var weakPassMatches = passAllMatches.Where(IsWeakOnlyCorrelationMatch).Take(96).ToList();
        sb.AppendLine("  \"passMatches\": [");
        for (int i = 0; i < passMatches.Count; i++)
        {
            AppendCorrelationMatchObject(sb, passMatches[i], "    ");
            sb.Append(i + 1 < passMatches.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        string pipelinePath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "pipeline_index.json") : "";
        string pipelineChangesPath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "pipeline_state_changes.jsonl") : "";
        var drawObjects = new List<JObject>();
        drawObjects.AddRange(LoadJsonObjectsSafe(pipelinePath, 4096));
        drawObjects.AddRange(LoadJsonObjectsFromJsonlSafe(pipelineChangesPath, 4096));
        WriteJsonProperty(sb, "renderDocDrawObjectCount", drawObjects.Count, true);
        var drawAllMatches = BuildCorrelationMatches(unityCandidates, drawObjects, 256, 4, false);
        var drawMatches = drawAllMatches.Where(IsStrongCorrelationMatch).Take(256).ToList();
        var weakDrawMatches = drawAllMatches.Where(IsWeakOnlyCorrelationMatch).Take(96).ToList();
        sb.AppendLine("  \"drawLevelCandidates\": [");
        for (int i = 0; i < drawMatches.Count; i++)
        {
            AppendCorrelationMatchObject(sb, drawMatches[i], "    ");
            sb.Append(i + 1 < drawMatches.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        sb.AppendLine("  \"weakMatches\": [");
        var weakMatches = weakResourceMatches.Concat(weakPassMatches).Concat(weakDrawMatches).Take(128).ToList();
        for (int i = 0; i < weakMatches.Count; i++)
        {
            AppendCorrelationMatchObject(sb, weakMatches[i], "    ");
            sb.Append(i + 1 < weakMatches.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");

        AppendStringArray(sb, "interpretationRules", new[]
        {
            "Matches are generated from names, dimensions, material/shader/texture text, material texture properties, UI screenRect, and compact RenderDoc table JSON.",
            "A match means inspect these RenderDoc resources/passes first; it is not proof of Unity eventIndex to RenderDoc eventId equality.",
            "Prefer matches with textureName plus width/height, UI screenRect versus viewport/scissor geometry, or shader/material text evidence.",
            "Do not use weakMatches or widthHeight-only matches as issue evidence; they are only a fallback search index.",
            "Use deep_event or pipeline_state_changes only for final per-draw claims."
        }, false, 2);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiDeepEventSamplingPlanJson()
    {
        var ctx = _linkedCaptureContext;
        string renderDocDir = ctx != null ? ctx.renderDocAnalysisDirectory : "";
        string passPath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "pass_table.json") : "";
        string pipelinePath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "pipeline_index.json") : "";
        string pipelineChangesPath = !string.IsNullOrEmpty(renderDocDir) ? Path.Combine(renderDocDir, "pipeline_state_changes.jsonl") : "";
        var unityCandidates = BuildUnityCorrelationCandidates();
        var renderDocObjects = new List<JObject>();
        renderDocObjects.AddRange(LoadJsonObjectsSafe(passPath, 4096));
        renderDocObjects.AddRange(LoadJsonObjectsSafe(pipelinePath, 4096));
        renderDocObjects.AddRange(LoadJsonObjectsFromJsonlSafe(pipelineChangesPath, 4096));
        var strongMatches = BuildCorrelationMatches(unityCandidates, renderDocObjects, 256, 4, true)
            .Where(m => m != null && m.score >= 45)
            .Take(96)
            .ToList();
        var passSamples = BuildRepresentativePassDeepEventSamples(LoadJsonObjectsSafe(passPath, 4096))
            .Take(32)
            .ToList();
        var matches = strongMatches
            .Concat(passSamples)
            .GroupBy(m => FirstToken(m.renderDocId, ExtractEventIdFromText(m.renderDocSample)))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.OrderByDescending(m => m.score).First())
            .OrderByDescending(m => m.score)
            .Take(96)
            .ToList();
        var eventIds = matches
            .Select(m => FirstToken(m.renderDocId, ExtractEventIdFromText(m.renderDocSample)))
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct()
            .Take(64)
            .ToList();

        var sb = new StringBuilder(32768);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-deep-event-sampling-plan/v1", true);
        WriteJsonProperty(sb, "purpose", "Automatic DeepEvent sampling queue generated from Unity runtime/resource fingerprints and RenderDoc pass/pipeline indices.", true);
        WriteJsonProperty(sb, "status", !string.IsNullOrEmpty(renderDocDir) && Directory.Exists(renderDocDir) && matches.Count > 0 ? "ready" : "insufficient_renderdoc_candidates", true);
        WriteJsonProperty(sb, "executionMode", "plan_only_no_external_process_spawned_by_unity_exporter", true);
        WriteJsonProperty(sb, "renderDocAnalysisDirectory", renderDocDir, true);
        WriteJsonProperty(sb, "passTable", File.Exists(passPath) ? passPath : "", true);
        WriteJsonProperty(sb, "pipelineIndex", File.Exists(pipelinePath) ? pipelinePath : "", true);
        WriteJsonProperty(sb, "pipelineStateChanges", File.Exists(pipelineChangesPath) ? pipelineChangesPath : "", true);
        WriteJsonProperty(sb, "unityCandidateCount", unityCandidates.Count, true);
        WriteJsonProperty(sb, "renderDocCandidateObjectCount", renderDocObjects.Count, true);
        WriteJsonProperty(sb, "strongCorrelationSampleCount", strongMatches.Count, true);
        WriteJsonProperty(sb, "representativePassSampleCount", passSamples.Count, true);
        WriteJsonProperty(sb, "sampleBudget", eventIds.Count, true);
        AppendStringArray(sb, "eventIdOrResourceIdQueue", eventIds.ToArray(), true);
        AppendStringArray(sb, "reportUsePolicy", new[]
        {
            "Samples with matchedBy=representativePassSample are diagnostic probes, not report issues.",
            "Representative pass samples may support a real issue only when independent Unity-side evidence links the pass to an actionable camera/stage/object/material problem.",
            "If similar representative pass samples are split by vkCmdCopyImageToBuffer, vkCmdBlitImage, readback/copy, or vkQueuePresentKHR boundaries, use them as capture_artifact exclusion evidence and do not place them in the human report issue list."
        }, true);
        AppendStringArray(sb, "runnerRequirements", new[]
        {
            "Run outside Unity Editor export to avoid blocking capture/export and machine-specific RenderDoc paths.",
            "Use DeepEvent.exe or the existing RenderDoc analysis runner against the .rdc and event identifiers below.",
            "DeepEvent output is required before claiming exact per-draw object identity or GPU state causality."
        }, true);
        sb.AppendLine("  \"samples\": [");
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "priority", i + 1, true);
            AppendJsonPropertyInline(sb, "score", match.score, true);
            AppendJsonPropertyInline(sb, "matchedBy", match.matchedBy, true);
            AppendJsonPropertyInline(sb, "eventIdOrResourceId", FirstToken(match.renderDocId, ExtractEventIdFromText(match.renderDocSample)), true);
            AppendJsonPropertyInline(sb, "unityType", match.unity != null ? match.unity.type : "", true);
            AppendJsonPropertyInline(sb, "unityPath", match.unity != null ? match.unity.path : "", true);
            AppendJsonPropertyInline(sb, "unityTexture", match.unity != null ? match.unity.textureName : "", true);
            AppendJsonPropertyInline(sb, "unityShader", match.unity != null ? match.unity.shaderName : "", true);
            AppendJsonPropertyInline(sb, "renderDocId", match.renderDocId, true);
            AppendJsonPropertyInline(sb, "renderDocName", match.renderDocName, true);
            AppendJsonPropertyInline(sb, "renderDocKind", match.renderDocKind, true);
            AppendJsonPropertyInline(sb, "reportEligibility", match.matchedBy == "representativePassSample" ? "diagnostic_only_requires_independent_unity_evidence" : "candidate_evidence", true);
            AppendJsonPropertyInline(sb, "renderDocSample", match.renderDocSample, false);
            sb.Append(i + 1 < matches.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private class CorrelationMatch
    {
        public UnityCorrelationCandidate unity;
        public string renderDocId = "";
        public string renderDocName = "";
        public string renderDocKind = "";
        public int score;
        public string matchedBy = "";
        public string renderDocSample = "";
    }

    private static List<UnityCorrelationCandidate> BuildUnityCorrelationCandidates()
    {
        var rows = new List<UnityCorrelationCandidate>();
        foreach (var r in BuildSceneRendererRecords())
        {
            if (r == null || (string.IsNullOrEmpty(r.textureName) && string.IsNullOrEmpty(r.shaderName) && string.IsNullOrEmpty(r.materialName))) continue;
            ParseTextureMetaSize(r.textureMeta, out int width, out int height);
            rows.Add(new UnityCorrelationCandidate
            {
                type = "renderer",
                path = r.path,
                materialName = r.materialName,
                shaderName = r.shaderName,
                textureName = r.textureName,
                materialTextures = r.materialTextures,
                width = width,
                height = height,
                fingerprint = r.resourceFingerprint
            });
        }
        foreach (var g in BuildUiGraphicRecords())
        {
            if (g == null || (string.IsNullOrEmpty(g.textureName) && string.IsNullOrEmpty(g.shaderName) && string.IsNullOrEmpty(g.materialName))) continue;
            ParseTextureMetaSize(g.textureMeta, out int width, out int height);
            rows.Add(new UnityCorrelationCandidate
            {
                type = "ui_graphic",
                path = g.path,
                materialName = g.materialName,
                shaderName = g.shaderName,
                textureName = g.textureName,
                materialTextures = g.materialTextures,
                width = width,
                height = height,
                screenRect = g.screenRect,
                fingerprint = g.resourceFingerprint
            });
        }
        foreach (var p in GetActiveParticleCandidates())
        {
            if (p == null || (string.IsNullOrEmpty(p.textureName) && string.IsNullOrEmpty(p.shaderName) && string.IsNullOrEmpty(p.materialName))) continue;
            ParseTextureMetaSize(p.textureMeta, out int width, out int height);
            rows.Add(new UnityCorrelationCandidate
            {
                type = "particle",
                path = p.path,
                materialName = p.materialName,
                shaderName = p.shaderName,
                textureName = p.textureName,
                materialTextures = p.materialTextures,
                width = width,
                height = height,
                fingerprint = p.resourceFingerprint
            });
        }
        foreach (var t in GetRuntimePlayerSnapshot().textures)
        {
            if (t == null || string.IsNullOrEmpty(t.name)) continue;
            rows.Add(new UnityCorrelationCandidate
            {
                type = "texture",
                path = t.name,
                textureName = t.name,
                width = t.width,
                height = t.height,
                fingerprint = t.name + "|" + t.width.ToString(CultureInfo.InvariantCulture) + "x" + t.height.ToString(CultureInfo.InvariantCulture) + "|" + t.format
            });
        }
        return rows
            .GroupBy(r => r.type + "|" + r.path + "|" + r.textureName + "|" + r.shaderName + "|" + r.materialName)
            .Select(g => g.First())
            .Take(2048)
            .ToList();
    }

    private static bool IsRenderDocAnalysisAvailable(LinkedCaptureContext ctx, string resourcePath, string passPath)
    {
        if (ctx == null) return false;
        bool statusLooksComplete = !string.IsNullOrEmpty(ctx.renderDocAnalysisStatus) &&
                                   ctx.renderDocAnalysisStatus.IndexOf("completed", StringComparison.OrdinalIgnoreCase) >= 0;
        return statusLooksComplete &&
               !string.IsNullOrEmpty(ctx.renderDocAnalysisDirectory) &&
               Directory.Exists(ctx.renderDocAnalysisDirectory) &&
               File.Exists(resourcePath) &&
               File.Exists(passPath);
    }

    private static List<CorrelationMatch> BuildCorrelationMatches(List<UnityCorrelationCandidate> unityCandidates, List<JObject> renderDocObjects, int unityLimit, int perCandidateLimit, bool strongOnly)
    {
        var matches = new List<CorrelationMatch>();
        foreach (var candidate in unityCandidates.Take(unityLimit))
        {
            var local = new List<CorrelationMatch>();
            foreach (var obj in renderDocObjects)
            {
                int score = ScoreRenderDocObject(candidate, obj, out string matchedBy);
                if (score <= 0) continue;
                if (strongOnly && !IsStrongCorrelationReason(matchedBy)) continue;
                local.Add(new CorrelationMatch
                {
                    unity = candidate,
                    renderDocId = ExtractRenderDocId(obj),
                    renderDocName = ExtractRenderDocName(obj),
                    renderDocKind = ExtractRenderDocKind(obj),
                    score = score,
                    matchedBy = matchedBy,
                    renderDocSample = Truncate(ObjectText(obj), 600)
                });
            }
            matches.AddRange(local.OrderByDescending(m => m.score).ThenBy(m => m.renderDocId).Take(perCandidateLimit));
        }
        return matches.OrderByDescending(m => m.score).ThenBy(m => m.unity.type).ThenBy(m => m.unity.path).Take(512).ToList();
    }

    private static bool IsStrongCorrelationMatch(CorrelationMatch match)
    {
        return match != null && IsStrongCorrelationReason(match.matchedBy);
    }

    private static bool IsWeakOnlyCorrelationMatch(CorrelationMatch match)
    {
        return match != null && !IsStrongCorrelationReason(match.matchedBy) && IsWeakCorrelationReason(match.matchedBy);
    }

    private static bool IsStrongCorrelationReason(string matchedBy)
    {
        if (string.IsNullOrEmpty(matchedBy)) return false;
        return matchedBy.IndexOf("textureName", StringComparison.OrdinalIgnoreCase) >= 0 ||
               matchedBy.IndexOf("materialTexture:", StringComparison.OrdinalIgnoreCase) >= 0 ||
               matchedBy.IndexOf("shaderName", StringComparison.OrdinalIgnoreCase) >= 0 ||
               matchedBy.IndexOf("materialName", StringComparison.OrdinalIgnoreCase) >= 0 ||
               matchedBy.IndexOf("uiScreenRectViewportOrScissor", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWeakCorrelationReason(string matchedBy)
    {
        if (string.IsNullOrEmpty(matchedBy)) return false;
        return matchedBy.IndexOf("widthHeight", StringComparison.OrdinalIgnoreCase) >= 0 ||
               matchedBy.IndexOf("dimensionText", StringComparison.OrdinalIgnoreCase) >= 0 ||
               matchedBy.IndexOf("uiScreenRectDimensionText", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<CorrelationMatch> BuildRepresentativePassDeepEventSamples(List<JObject> passObjects)
    {
        var matches = new List<CorrelationMatch>();
        foreach (var pass in passObjects ?? new List<JObject>())
        {
            string passId = ExtractFirstPropertyText(pass, "passId");
            int drawCount = ExtractFirstIntProperty(pass, "drawCount");
            int eventStart = ExtractFirstIntProperty(pass, "eventStart");
            int eventEnd = ExtractFirstIntProperty(pass, "eventEnd");
            int stateHashCount = CountArrayProperty(pass, "stateHashes");
            int stateChangeCount = CountArrayProperty(pass, "stateChangeEvents");
            var sampleEvents = ExtractIntArrayProperty(pass, "pipelineSampleEvents").Take(3).ToList();
            if (sampleEvents.Count == 0 && eventStart > 0) sampleEvents.Add(eventStart);
            foreach (int eventId in sampleEvents)
            {
                if (eventId <= 0) continue;
                matches.Add(new CorrelationMatch
                {
                    renderDocId = eventId.ToString(CultureInfo.InvariantCulture),
                    renderDocName = passId,
                    renderDocKind = "representative_pass_event",
                    score = 40 + Math.Min(40, drawCount / 4) + Math.Min(20, stateChangeCount) + Math.Min(10, stateHashCount),
                    matchedBy = "representativePassSample",
                    renderDocSample = "{\"eventId\":" + eventId.ToString(CultureInfo.InvariantCulture) +
                                      ",\"passId\":\"" + EscapeJson(passId) +
                                      "\",\"eventStart\":" + eventStart.ToString(CultureInfo.InvariantCulture) +
                                      ",\"eventEnd\":" + eventEnd.ToString(CultureInfo.InvariantCulture) +
                                      ",\"drawCount\":" + drawCount.ToString(CultureInfo.InvariantCulture) +
                                      ",\"stateHashCount\":" + stateHashCount.ToString(CultureInfo.InvariantCulture) +
                                      ",\"stateChangeCount\":" + stateChangeCount.ToString(CultureInfo.InvariantCulture) + "}"
                });
            }
        }

        return matches
            .GroupBy(m => m.renderDocId)
            .Select(g => g.OrderByDescending(m => m.score).First())
            .OrderByDescending(m => m.score)
            .ThenBy(m => m.renderDocId)
            .ToList();
    }

    private static int CountArrayProperty(JObject obj, string propertyName)
    {
        if (obj == null || string.IsNullOrEmpty(propertyName)) return 0;
        var prop = EnumerateSelfAndDescendants(obj)
            .OfType<JProperty>()
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        var array = prop != null ? prop.Value as JArray : null;
        return array != null ? array.Count : 0;
    }

    private static List<int> ExtractIntArrayProperty(JObject obj, string propertyName)
    {
        var values = new List<int>();
        if (obj == null || string.IsNullOrEmpty(propertyName)) return values;
        var prop = EnumerateSelfAndDescendants(obj)
            .OfType<JProperty>()
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        var array = prop != null ? prop.Value as JArray : null;
        if (array == null) return values;
        foreach (var token in array)
        {
            if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                values.Add(value);
        }
        return values;
    }

    private static int ScoreRenderDocObject(UnityCorrelationCandidate candidate, JObject obj, out string matchedBy)
    {
        var reasons = new List<string>();
        string text = ObjectText(obj);
        int score = 0;
        if (ContainsIgnoreCase(text, candidate.textureName))
        {
            score += 70;
            reasons.Add("textureName");
        }
        foreach (string textureToken in ExtractMaterialTextureTokens(candidate.materialTextures).Take(4))
        {
            if (!ContainsIgnoreCase(text, textureToken)) continue;
            score += 25;
            reasons.Add("materialTexture:" + textureToken);
            break;
        }
        if (ContainsIgnoreCase(text, candidate.shaderName))
        {
            score += 30;
            reasons.Add("shaderName");
        }
        if (ContainsIgnoreCase(text, candidate.materialName))
        {
            score += 20;
            reasons.Add("materialName");
        }
        int width = ExtractFirstIntProperty(obj, "width");
        int height = ExtractFirstIntProperty(obj, "height");
        if (candidate.width > 0 && candidate.height > 0 && width == candidate.width && height == candidate.height)
        {
            score += 35;
            reasons.Add("widthHeight");
        }
        else if (candidate.width > 0 && candidate.height > 0 && ContainsIgnoreCase(text, candidate.width.ToString(CultureInfo.InvariantCulture)) && ContainsIgnoreCase(text, candidate.height.ToString(CultureInfo.InvariantCulture)))
        {
            score += 15;
            reasons.Add("dimensionText");
        }
        if (TryParseRectSize(candidate.screenRect, out int screenWidth, out int screenHeight))
        {
            if (TryExtractRenderDocRectSize(obj, out int rectWidth, out int rectHeight) &&
                IsCloseDimension(screenWidth, rectWidth, 4) &&
                IsCloseDimension(screenHeight, rectHeight, 4))
            {
                score += 45;
                reasons.Add("uiScreenRectViewportOrScissor");
            }
            else if (screenWidth > 0 && screenHeight > 0 &&
                     ContainsIgnoreCase(text, screenWidth.ToString(CultureInfo.InvariantCulture)) &&
                     ContainsIgnoreCase(text, screenHeight.ToString(CultureInfo.InvariantCulture)))
            {
                score += 12;
                reasons.Add("uiScreenRectDimensionText");
            }
        }
        matchedBy = string.Join(",", reasons);
        return score;
    }

    private static void AppendCorrelationMatchObject(StringBuilder sb, CorrelationMatch match, string pad)
    {
        sb.Append(pad).Append("{");
        AppendJsonPropertyInline(sb, "score", match != null ? match.score : 0, true);
        AppendJsonPropertyInline(sb, "matchedBy", match != null ? match.matchedBy : "", true);
        AppendJsonPropertyInline(sb, "unityType", match != null && match.unity != null ? match.unity.type : "", true);
        AppendJsonPropertyInline(sb, "unityPath", match != null && match.unity != null ? match.unity.path : "", true);
        AppendJsonPropertyInline(sb, "unityMaterial", match != null && match.unity != null ? match.unity.materialName : "", true);
        AppendJsonPropertyInline(sb, "unityShader", match != null && match.unity != null ? match.unity.shaderName : "", true);
        AppendJsonPropertyInline(sb, "unityTexture", match != null && match.unity != null ? match.unity.textureName : "", true);
        AppendJsonPropertyInline(sb, "unityFingerprint", match != null && match.unity != null ? match.unity.fingerprint : "", true);
        AppendJsonPropertyInline(sb, "renderDocId", match != null ? match.renderDocId : "", true);
        AppendJsonPropertyInline(sb, "renderDocName", match != null ? match.renderDocName : "", true);
        AppendJsonPropertyInline(sb, "renderDocKind", match != null ? match.renderDocKind : "", true);
        AppendJsonPropertyInline(sb, "renderDocSample", match != null ? match.renderDocSample : "", false);
        sb.Append("}");
    }

    private static List<JObject> LoadJsonObjectsSafe(string path, int maxObjects)
    {
        var rows = new List<JObject>();
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rows;
            JToken root = JToken.Parse(File.ReadAllText(path));
            foreach (var obj in EnumerateSelfAndDescendants(root).OfType<JObject>())
            {
                if (rows.Count >= maxObjects) break;
                rows.Add(obj);
            }
        }
        catch { }
        return rows;
    }

    private static List<JObject> LoadJsonObjectsFromJsonlSafe(string path, int maxObjects)
    {
        var rows = new List<JObject>();
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rows;
            foreach (string line in File.ReadLines(path))
            {
                if (rows.Count >= maxObjects) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var obj = JToken.Parse(line) as JObject;
                if (obj != null) rows.Add(obj);
            }
        }
        catch { }
        return rows;
    }

    private static string ExtractRenderDocId(JObject obj)
    {
        return FirstNonEmptyProperty(obj, "resourceId", "id", "eventId", "passId", "shaderId", "name");
    }

    private static string ExtractRenderDocName(JObject obj)
    {
        return FirstNonEmptyProperty(obj, "name", "resourceName", "debugName", "label", "markerPath");
    }

    private static string ExtractRenderDocKind(JObject obj)
    {
        return FirstNonEmptyProperty(obj, "type", "resourceType", "kind", "role", "passRole");
    }

    private static string FirstNonEmptyProperty(JObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            string value = ExtractFirstPropertyText(obj, name);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return "";
    }

    private static string ExtractFirstPropertyText(JObject obj, string propertyName)
    {
        if (obj == null || string.IsNullOrEmpty(propertyName)) return "";
        var prop = EnumerateSelfAndDescendants(obj)
            .OfType<JProperty>()
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        return prop != null ? prop.Value.ToString() : "";
    }

    private static int ExtractFirstIntProperty(JObject obj, string propertyName)
    {
        string text = ExtractFirstPropertyText(obj, propertyName);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    private static string ObjectText(JObject obj)
    {
        return obj != null ? obj.ToString(Newtonsoft.Json.Formatting.None) : "";
    }

    private static IEnumerable<JToken> EnumerateSelfAndDescendants(JToken token)
    {
        if (token == null) yield break;
        yield return token;
        var container = token as JContainer;
        if (container == null) yield break;
        foreach (var child in container.Descendants())
            yield return child;
    }

    private static bool ContainsIgnoreCase(string text, string value)
    {
        return !string.IsNullOrEmpty(text) &&
               !string.IsNullOrEmpty(value) &&
               text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
        return value.Substring(0, maxLength);
    }

    private static void ParseTextureMetaSize(string meta, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(meta)) return;
        var match = System.Text.RegularExpressions.Regex.Match(meta, @"(\d+)x(\d+)");
        if (!match.Success) return;
        int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
        int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
    }

    private static IEnumerable<string> ExtractMaterialTextureTokens(string materialTextures)
    {
        if (string.IsNullOrEmpty(materialTextures)) yield break;
        foreach (var part in materialTextures.Split('|'))
        {
            string value = part.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            int equals = value.IndexOf('=');
            if (equals >= 0 && equals + 1 < value.Length)
                value = value.Substring(equals + 1).Trim();
            int bracket = value.IndexOf('[');
            if (bracket > 0)
                value = value.Substring(0, bracket).Trim();
            if (value.Length >= 3)
                yield return value;
        }
    }

    private static bool TryParseRectSize(string rectText, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(rectText)) return false;
        var numbers = System.Text.RegularExpressions.Regex.Matches(rectText, @"-?\d+(?:\.\d+)?")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Value)
            .ToList();
        if (numbers.Count < 4) return false;
        if (!float.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w)) return false;
        if (!float.TryParse(numbers[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float h)) return false;
        width = Mathf.RoundToInt(Math.Abs(w));
        height = Mathf.RoundToInt(Math.Abs(h));
        return width > 0 && height > 0;
    }

    private static bool TryExtractRenderDocRectSize(JObject obj, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (obj == null) return false;
        string text = ObjectText(obj);
        if (!ContainsIgnoreCase(text, "viewport") && !ContainsIgnoreCase(text, "scissor"))
            return false;
        foreach (var token in EnumerateSelfAndDescendants(obj).OfType<JObject>())
        {
            int w = ExtractFirstIntProperty(token, "width");
            int h = ExtractFirstIntProperty(token, "height");
            if (w <= 0) w = ExtractFirstIntProperty(token, "w");
            if (h <= 0) h = ExtractFirstIntProperty(token, "h");
            if (w > 0 && h > 0)
            {
                width = w;
                height = h;
                return true;
            }
        }
        return false;
    }

    private static bool IsCloseDimension(int a, int b, int tolerance)
    {
        return a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(0, tolerance);
    }

    private static string ExtractEventIdFromText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(text, "\"(?:eventId|event|eid)\"\\s*:\\s*\"?([0-9]+)\"?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }

    private string BuildAiReflectionInventoryJson()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var rawDataCounts = events
            .SelectMany(e => e.rawDataFields.Keys)
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-reflection-inventory/v1", true);
        WriteJsonProperty(sb, "unityVersion", Application.unityVersion, true);
        WriteJsonProperty(sb, "frameDebuggerUtilityType", s_utilType != null ? s_utilType.FullName : "", true);
        WriteJsonProperty(sb, "limitProperty", s_limitProp != null ? s_limitProp.ToString() : "", true);
        WriteJsonProperty(sb, "limitField", s_limitField != null ? s_limitField.ToString() : "", true);
        WriteJsonProperty(sb, "getFrameEvents", s_getFrameEvents != null ? s_getFrameEvents.ToString() : "", true);
        WriteJsonProperty(sb, "getFrameEventData", s_getFrameEventData != null ? s_getFrameEventData.ToString() : "", true);
        WriteJsonProperty(sb, "getFrameEventObject", s_getFrameEventObject != null ? s_getFrameEventObject.ToString() : "", true);
        WriteJsonProperty(sb, "getFrameEventGameObject", s_getFrameEventGameObject != null ? s_getFrameEventGameObject.ToString() : "", true);
        WriteJsonProperty(sb, "getFrameEventRenderer", s_getFrameEventRenderer != null ? s_getFrameEventRenderer.ToString() : "", true);
        WriteJsonProperty(sb, "eventDataType", s_eventDataType != null ? s_eventDataType.FullName : "", true);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerGameObject", events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath)), true);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerRenderer", events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerRendererPath)), true);
        WriteJsonProperty(sb, "eventsWithDirectMeshName", events.Count(e => !string.IsNullOrEmpty(e.meshName)), true);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerMeshes", events.Count(HasFrameDebuggerDetailMeshes), true);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerDetailMeshNames", events.Count(e => e.detailMeshNames != null && e.detailMeshNames.Count > 0), true);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerMeshInstanceIds", events.Count(e => e.detailMeshInstanceIds != null && e.detailMeshInstanceIds.Count > 0), true);
        WriteJsonProperty(sb, "frameEventDataAttemptedCount", events.Count(e => e.frameEventDataAttempted), true);
        WriteJsonProperty(sb, "frameEventDataSuccessCount", events.Count(e => e.frameEventDataSuccess), true);
        WriteJsonProperty(sb, "frameEventDataRawDataFieldEventCount", events.Count(e => e.rawDataFields != null && e.rawDataFields.Count > 0), true);
        WriteJsonProperty(sb, "eventsWithBatchBreakCause", events.Count(e => !string.IsNullOrEmpty(e.batchBreakCause)), true);
        WriteJsonProperty(sb, "eventsWithLightMode", events.Count(e => !string.IsNullOrEmpty(e.passLightMode)), true);
        AppendStringArray(sb, "batchBreakCauseStrings", s_batchBreakCauseStrings ?? new string[0], true);
        AppendStringArray(sb, "eventFields", s_eventFields != null ? s_eventFields.Select(f => f.Name).ToArray() : new string[0], true);
        AppendStringArray(sb, "eventProperties", s_eventProperties != null ? s_eventProperties.Select(p => p.Name).ToArray() : new string[0], true);
        AppendStringArray(sb, "eventDataFields", s_eventDataFields != null ? s_eventDataFields.Select(f => f.Name).ToArray() : new string[0], true);
        AppendStringArray(sb, "eventDataProperties", s_eventDataProperties != null ? s_eventDataProperties.Select(p => p.Name).ToArray() : new string[0], true);
        sb.AppendLine("  \"nonEmptyRawDataFieldCounts\": [");
        for (int i = 0; i < rawDataCounts.Count; i++)
        {
            var row = rawDataCounts[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "field", row.Key, true);
            AppendJsonPropertyInline(sb, "count", row.Count(), false);
            sb.Append(i + 1 < rawDataCounts.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiCamerasJson()
    {
        var runtime = GetRuntimePlayerSnapshot();
        if (runtime.available && runtime.cameras.Count > 0)
            return BuildRuntimeCamerasJson(runtime);

        var cameras = GetSceneCameras();
        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-cameras/v1", true);
        WriteJsonProperty(sb, "cameraCount", cameras.Count, true);
        AppendAggregateArray(sb, "frameEventsByCameraStage", BuildCameraStageStats(_capturedEvents ?? new List<FrameEventInfo>()), true);
        sb.AppendLine("  \"cameras\": [");
        for (int i = 0; i < cameras.Count; i++)
        {
            var camera = cameras[i];
            Component additionalData = camera.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name.Contains("UniversalAdditionalCameraData"));
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", GetHierarchyPath(camera.gameObject), true);
            AppendJsonPropertyInline(sb, "activeInHierarchy", camera.gameObject.activeInHierarchy, true);
            AppendJsonPropertyInline(sb, "enabled", camera.enabled, true);
            AppendJsonPropertyInline(sb, "depth", camera.depth.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "clearFlags", camera.clearFlags.ToString(), true);
            AppendJsonPropertyInline(sb, "cullingMask", LayerMaskToNames(camera.cullingMask), true);
            AppendJsonPropertyInline(sb, "orthographic", camera.orthographic, true);
            AppendJsonPropertyInline(sb, "pixelRect", camera.pixelRect.ToString(), true);
            AppendJsonPropertyInline(sb, "targetTexture", camera.targetTexture != null ? camera.targetTexture.name : "", true);
            AppendJsonPropertyInline(sb, "allowHDR", camera.allowHDR, true);
            AppendJsonPropertyInline(sb, "allowMSAA", camera.allowMSAA, true);
            AppendJsonPropertyInline(sb, "urpAdditionalData", additionalData != null ? SummarizeObjectFields(additionalData, 24) : "", true);
            AppendJsonPropertyInline(sb, "prefabAssetPath", GetPrefabAssetPath(camera.gameObject), false);
            sb.Append(i + 1 < cameras.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildRuntimeCamerasJson(RuntimePlayerSnapshot runtime)
    {
        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-cameras/v1", true);
        WriteJsonProperty(sb, "source", "runtime_player_snapshot", true);
        WriteJsonProperty(sb, "scene", runtime != null ? runtime.scene : "", true);
        WriteJsonProperty(sb, "cameraCount", runtime != null ? runtime.cameras.Count : 0, true);
        WriteJsonProperty(sb, "note", "Remote Player camera inventory captured once through PlayerConnection. Use as attribution inventory, not timing evidence.", true);
        AppendAggregateArray(sb, "frameEventsByCameraStage", BuildCameraStageStats(s_exportCache != null ? s_exportCache.events ?? new List<FrameEventInfo>() : new List<FrameEventInfo>()), true);
        sb.AppendLine("  \"cameras\": [");
        var cameras = runtime != null ? runtime.cameras : new List<RuntimeCameraRecord>();
        for (int i = 0; i < cameras.Count; i++)
        {
            var camera = cameras[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", camera.path, true);
            AppendJsonPropertyInline(sb, "name", camera.name, true);
            AppendJsonPropertyInline(sb, "activeInHierarchy", camera.activeInHierarchy, true);
            AppendJsonPropertyInline(sb, "enabled", camera.enabled, true);
            AppendJsonPropertyInline(sb, "depth", camera.depth.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "clearFlags", camera.clearFlags, true);
            AppendJsonPropertyInline(sb, "cullingMaskRaw", camera.cullingMask, true);
            AppendJsonPropertyInline(sb, "cullingMaskNames", camera.cullingMaskNames, true);
            AppendJsonPropertyInline(sb, "orthographic", camera.orthographic, true);
            AppendJsonPropertyInline(sb, "orthographicSize", camera.orthographicSize.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "fieldOfView", camera.fieldOfView.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "nearClipPlane", camera.nearClipPlane.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "farClipPlane", camera.farClipPlane.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "allowHDR", camera.allowHDR, true);
            AppendJsonPropertyInline(sb, "allowMSAA", camera.allowMSAA, true);
            AppendJsonPropertyInline(sb, "targetTexture", camera.targetTexture, true);
            AppendJsonPropertyInline(sb, "targetTextureMeta", camera.targetTextureMeta, true);
            AppendJsonPropertyInline(sb, "pixelRect", camera.pixelRect, false);
            sb.Append(i + 1 < cameras.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildRuntimeTextureThumbnailsIndexJson()
    {
        var runtime = GetRuntimePlayerSnapshot();
        var thumbnails = runtime != null ? runtime.textureThumbnails : new List<RuntimeTextureThumbnailRecord>();
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-runtime-texture-thumbnails/v1", true);
        WriteJsonProperty(sb, "source", "runtime_player_snapshot", true);
        WriteJsonProperty(sb, "status", thumbnails.Count > 0 ? "available" : "empty", true);
        WriteJsonProperty(sb, "note", "One-shot development-only RenderTexture thumbnails. They are small diagnostic PNGs and not timing evidence.", true);
        WriteJsonProperty(sb, "thumbnailCount", thumbnails.Count, true);
        sb.AppendLine("  \"thumbnails\": [");
        for (int i = 0; i < thumbnails.Count; i++)
        {
            var thumbnail = thumbnails[i];
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "name", thumbnail.name, true);
            AppendJsonPropertyInline(sb, "type", thumbnail.type, true);
            AppendJsonPropertyInline(sb, "instanceId", thumbnail.instanceId, true);
            AppendJsonPropertyInline(sb, "width", thumbnail.width, true);
            AppendJsonPropertyInline(sb, "height", thumbnail.height, true);
            AppendJsonPropertyInline(sb, "thumbnailWidth", thumbnail.thumbnailWidth, true);
            AppendJsonPropertyInline(sb, "thumbnailHeight", thumbnail.thumbnailHeight, true);
            AppendJsonPropertyInline(sb, "relativePath", RuntimeTextureThumbnailRelativePath(thumbnail, i), true);
            AppendJsonPropertyInline(sb, "hasPngBase64", !string.IsNullOrEmpty(thumbnail.pngBase64), false);
            sb.Append(i + 1 < thumbnails.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void WriteRuntimeTextureThumbnailFiles(string directory)
    {
        var runtime = GetRuntimePlayerSnapshot();
        var thumbnails = runtime != null ? runtime.textureThumbnails : new List<RuntimeTextureThumbnailRecord>();
        if (thumbnails.Count == 0 || string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        for (int i = 0; i < thumbnails.Count; i++)
        {
            var thumbnail = thumbnails[i];
            if (thumbnail == null || string.IsNullOrEmpty(thumbnail.pngBase64)) continue;
            try
            {
                byte[] bytes = Convert.FromBase64String(thumbnail.pngBase64);
                string path = Path.Combine(directory, Path.GetFileName(RuntimeTextureThumbnailRelativePath(thumbnail, i)));
                File.WriteAllBytes(path, bytes);
            }
            catch { }
        }
    }

    private static string RuntimeTextureThumbnailRelativePath(RuntimeTextureThumbnailRecord thumbnail, int index)
    {
        string name = thumbnail != null && !string.IsNullOrEmpty(thumbnail.name) ? thumbnail.name : "render_texture";
        return "runtime_texture_thumbnails/" + (index + 1).ToString("000", CultureInfo.InvariantCulture) + "_" + SafeFileName(name) + ".png";
    }

    private static string SafeFileName(string value)
    {
        if (string.IsNullOrEmpty(value)) return "unnamed";
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        string safe = new string(chars).Trim();
        if (safe.Length == 0) safe = "unnamed";
        return safe.Length > 80 ? safe.Substring(0, 80) : safe;
    }

    private string BuildAiRenderFeaturesJson()
    {
        var sb = new StringBuilder(16384);
        var asset = UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset as ScriptableObject;
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-renderfeatures/v1", true);
        WriteJsonProperty(sb, "pipelineAssetName", asset != null ? asset.name : "", true);
        WriteJsonProperty(sb, "pipelineAssetPath", asset != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(asset)) : "", true);
        sb.AppendLine("  \"rendererData\": [");
        var rendererDataObjects = GetRendererDataObjects(asset).ToList();
        for (int i = 0; i < rendererDataObjects.Count; i++)
        {
            var rendererData = rendererDataObjects[i];
            var features = GetRendererFeatures(rendererData).ToList();
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "name", rendererData != null ? rendererData.name : "", true);
            AppendJsonPropertyInline(sb, "type", rendererData != null ? rendererData.GetType().Name : "", true);
            AppendJsonPropertyInline(sb, "assetPath", rendererData != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(rendererData)) : "", true);
            sb.Append("\"features\": [");
            for (int j = 0; j < features.Count; j++)
            {
                var feature = features[j];
                sb.Append("{");
                AppendJsonPropertyInline(sb, "name", feature != null ? feature.name : "", true);
                AppendJsonPropertyInline(sb, "type", feature != null ? feature.GetType().FullName : "", true);
                AppendJsonPropertyInline(sb, "active", feature != null && GetFeatureActive(feature), true);
                AppendJsonPropertyInline(sb, "assetPath", feature != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(feature)) : "", true);
                AppendJsonPropertyInline(sb, "materialShaderRefs", feature != null ? SummarizeObjectFields(feature, 32) : "", false);
                sb.Append(j + 1 < features.Count ? "}," : "}");
            }
            sb.Append("]");
            sb.Append(i + 1 < rendererDataObjects.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiUiGraphicsJsonl()
    {
        var uiBatchMap = BuildUiBatchCandidateMap(_capturedEvents ?? new List<FrameEventInfo>());
        var mappedGraphicPaths = new HashSet<string>(uiBatchMap.Values.SelectMany(c => c.graphics).Select(g => g.path));
        var graphics = BuildUiGraphicRecords();
        var sb = new StringBuilder(32768);

        foreach (var graphic in graphics)
        {
            bool mapped = mappedGraphicPaths.Contains(graphic.path);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "path", graphic.path, true);
            AppendJsonPropertyInline(sb, "source", graphic.source, true);
            AppendJsonPropertyInline(sb, "pathId", StableStringId("s", graphic.path), true);
            AppendJsonPropertyInline(sb, "type", graphic.type, true);
            AppendJsonPropertyInline(sb, "activeInHierarchy", graphic.active, true);
            AppendJsonPropertyInline(sb, "canvasEnabled", graphic.canvasEnabled, true);
            AppendJsonPropertyInline(sb, "graphicEnabled", graphic.enabled, true);
            AppendJsonPropertyInline(sb, "raycastTarget", graphic.raycastTarget, true);
            AppendJsonPropertyInline(sb, "mappedToFrameEvent", mapped, true);
            AppendJsonPropertyInline(sb, "confidence", mapped ? "inferred_high" : "snapshot_only", true);
            AppendJsonPropertyInline(sb, "canvas", graphic.canvasPath, true);
            AppendJsonPropertyInline(sb, "rootCanvas", graphic.rootCanvasPath, true);
            AppendJsonPropertyInline(sb, "canvasRenderMode", graphic.canvasRenderMode, true);
            AppendJsonPropertyInline(sb, "canvasSortingLayerId", graphic.canvasSortingLayerId, true);
            AppendJsonPropertyInline(sb, "canvasSortingOrder", graphic.canvasSortingOrder, true);
            AppendJsonPropertyInline(sb, "canvasScaleFactor", graphic.canvasScaleFactor.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "canvasReferencePixelsPerUnit", graphic.canvasReferencePixelsPerUnit.ToString("0.###", CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "depth", graphic.depth, true);
            AppendJsonPropertyInline(sb, "materialName", graphic.materialName, true);
            AppendJsonPropertyInline(sb, "materialId", StableStringId("mat", !string.IsNullOrEmpty(graphic.materialPath) ? graphic.materialPath : graphic.materialName), true);
            AppendJsonPropertyInline(sb, "shaderName", graphic.shaderName, true);
            AppendJsonPropertyInline(sb, "shaderId", StableStringId("sh", graphic.shaderName), true);
            AppendJsonPropertyInline(sb, "textureName", graphic.textureName, true);
            AppendJsonPropertyInline(sb, "textureId", StableStringId("tex", !string.IsNullOrEmpty(graphic.texturePath) ? graphic.texturePath : graphic.textureName), true);
            AppendJsonPropertyInline(sb, "textureMeta", graphic.textureMeta, true);
            AppendJsonPropertyInline(sb, "materialTextures", graphic.materialTextures, true);
            AppendJsonPropertyInline(sb, "spriteName", graphic.spriteName, true);
            AppendJsonPropertyInline(sb, "rect", graphic.rect, true);
            AppendJsonPropertyInline(sb, "screenRect", graphic.screenRect, true);
            AppendJsonPropertyInline(sb, "isTextComponent", graphic.isTextComponent, true);
            AppendJsonPropertyInline(sb, "textLength", graphic.textLength, true);
            AppendJsonPropertyInline(sb, "fontName", graphic.fontName, true);
            AppendJsonPropertyInline(sb, "fontMaterialName", graphic.fontMaterialName, true);
            AppendJsonPropertyInline(sb, "sortingGroup", graphic.sortingGroup, true);
            AppendJsonPropertyInline(sb, "canvasRenderer", graphic.canvasRenderer, true);
            AppendJsonPropertyInline(sb, "materialFingerprint", graphic.materialFingerprint, true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", graphic.resourceFingerprint, true);
            AppendJsonPropertyInline(sb, "maskState", graphic.maskState, true);
            AppendJsonPropertyInline(sb, "prefabAssetPath", graphic.prefabAssetPath, false);
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private string BuildAiParticlesJsonl()
    {
        var events = _capturedEvents ?? new List<FrameEventInfo>();
        var mappedParticleIds = new HashSet<int>(BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value)
            .Select(c => c.id));
        var runtime = GetRuntimePlayerSnapshot();
        if (runtime.available && runtime.particles.Count > 0)
        {
            var runtimeSb = new StringBuilder(32768);
            foreach (var particle in runtime.particles.OrderByDescending(p => p.aliveParticles).ThenBy(p => p.path))
            {
                bool mapped = mappedParticleIds.Contains(particle.id);
                runtimeSb.Append("{");
                AppendJsonPropertyInline(runtimeSb, "id", particle.id, true);
                AppendJsonPropertyInline(runtimeSb, "source", particle.source, true);
                AppendJsonPropertyInline(runtimeSb, "path", particle.path, true);
                AppendJsonPropertyInline(runtimeSb, "pathId", StableStringId("s", particle.path), true);
                AppendJsonPropertyInline(runtimeSb, "activeInHierarchy", true, true);
                AppendJsonPropertyInline(runtimeSb, "rendererEnabled", true, true);
                AppendJsonPropertyInline(runtimeSb, "isPlaying", particle.aliveParticles > 0, true);
                AppendJsonPropertyInline(runtimeSb, "aliveParticles", particle.aliveParticles, true);
                AppendJsonPropertyInline(runtimeSb, "maxParticles", particle.maxParticles, true);
                AppendJsonPropertyInline(runtimeSb, "visibleForFrameCandidate", particle.aliveParticles > 0, true);
                AppendJsonPropertyInline(runtimeSb, "mappedToFrameEvent", mapped, true);
                AppendJsonPropertyInline(runtimeSb, "confidence", mapped ? "inferred_medium" : "snapshot_only", true);
                AppendJsonPropertyInline(runtimeSb, "materialName", particle.materialName, true);
                AppendJsonPropertyInline(runtimeSb, "materialId", StableStringId("mat", particle.materialName), true);
                AppendJsonPropertyInline(runtimeSb, "shaderName", particle.shaderName, true);
                AppendJsonPropertyInline(runtimeSb, "shaderId", StableStringId("sh", particle.shaderName), true);
                AppendJsonPropertyInline(runtimeSb, "textureName", particle.textureName, true);
                AppendJsonPropertyInline(runtimeSb, "textureId", StableStringId("tex", particle.textureName), true);
                AppendJsonPropertyInline(runtimeSb, "textureMeta", particle.textureMeta, true);
                AppendJsonPropertyInline(runtimeSb, "materialTextures", particle.materialTextures, true);
                AppendJsonPropertyInline(runtimeSb, "rendererMode", particle.rendererMode, true);
                AppendJsonPropertyInline(runtimeSb, "sortingLayerId", particle.sortingLayerId, true);
                AppendJsonPropertyInline(runtimeSb, "sortingOrder", particle.sortingOrder, true);
                AppendJsonPropertyInline(runtimeSb, "sortingGroup", particle.sortingGroup, true);
                AppendJsonPropertyInline(runtimeSb, "bounds", particle.bounds, true);
                AppendJsonPropertyInline(runtimeSb, "materialFingerprint", particle.materialFingerprint, true);
                AppendJsonPropertyInline(runtimeSb, "resourceFingerprint", particle.resourceFingerprint, true);
                AppendJsonPropertyInline(runtimeSb, "prefabAssetPath", "", false);
                runtimeSb.AppendLine("}");
            }
            return runtimeSb.ToString();
        }

        var particles = GetSceneParticleSystems();
        var sb = new StringBuilder(32768);

        foreach (var ps in particles)
        {
            int particleId = GetParticleCandidateId(ps);
            var main = ps.main;
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var material = renderer != null ? renderer.sharedMaterial : null;
            Texture mainTexture = GetMaterialMainTexture(material);
            bool mapped = mappedParticleIds.Contains(particleId);
            string path = GetHierarchyPath(ps.gameObject);
            sb.Append("{");
            AppendJsonPropertyInline(sb, "id", particleId, true);
            AppendJsonPropertyInline(sb, "source", "editor_scene", true);
            AppendJsonPropertyInline(sb, "path", path, true);
            AppendJsonPropertyInline(sb, "pathId", StableStringId("s", path), true);
            AppendJsonPropertyInline(sb, "activeInHierarchy", ps.gameObject.activeInHierarchy, true);
            AppendJsonPropertyInline(sb, "rendererEnabled", renderer != null && renderer.enabled, true);
            AppendJsonPropertyInline(sb, "isPlaying", ps.isPlaying, true);
            AppendJsonPropertyInline(sb, "aliveParticles", ps.particleCount, true);
            AppendJsonPropertyInline(sb, "maxParticles", main.maxParticles, true);
            AppendJsonPropertyInline(sb, "visibleForFrameCandidate", IsActiveParticleSystemForFrame(ps), true);
            AppendJsonPropertyInline(sb, "mappedToFrameEvent", mapped, true);
            AppendJsonPropertyInline(sb, "confidence", mapped ? "inferred_medium" : "snapshot_only", true);
            AppendJsonPropertyInline(sb, "materialName", material != null ? material.name : "", true);
            AppendJsonPropertyInline(sb, "materialId", StableStringId("mat", material != null ? (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)) ? ToProjectRelativePath(AssetDatabase.GetAssetPath(material)) : material.name) : ""), true);
            AppendJsonPropertyInline(sb, "shaderName", material != null && material.shader != null ? material.shader.name : "", true);
            AppendJsonPropertyInline(sb, "shaderId", StableStringId("sh", material != null && material.shader != null ? material.shader.name : ""), true);
            AppendJsonPropertyInline(sb, "textureName", mainTexture != null ? mainTexture.name : "", true);
            AppendJsonPropertyInline(sb, "textureId", StableStringId("tex", mainTexture != null ? mainTexture.name : ""), true);
            AppendJsonPropertyInline(sb, "textureMeta", TextureSummary(mainTexture), true);
            AppendJsonPropertyInline(sb, "materialTextures", BuildMaterialTextureSummary(material, 12), true);
            AppendJsonPropertyInline(sb, "rendererMode", renderer != null ? renderer.renderMode.ToString() : "", true);
            AppendJsonPropertyInline(sb, "sortingLayerId", renderer != null ? renderer.sortingLayerID : 0, true);
            AppendJsonPropertyInline(sb, "sortingOrder", renderer != null ? renderer.sortingOrder : 0, true);
            AppendJsonPropertyInline(sb, "sortingGroup", SortingGroupSummary(ps.gameObject), true);
            AppendJsonPropertyInline(sb, "bounds", renderer != null ? renderer.bounds.ToString() : "", true);
            AppendJsonPropertyInline(sb, "materialFingerprint", MaterialFingerprint(material), true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", BuildRuntimeRendererFingerprint(path, "", material != null ? material.name : "", material != null && material.shader != null ? material.shader.name : "", mainTexture != null ? mainTexture.name : "", material != null ? material.renderQueue : -1), true);
            AppendJsonPropertyInline(sb, "prefabAssetPath", GetPrefabAssetPath(ps.gameObject), false);
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private string BuildAiStringDictionaryJson()
    {
        var strings = new SortedDictionary<string, string>();
        Action<string> add = value =>
        {
            if (string.IsNullOrEmpty(value)) return;
            strings[StableStringId("s", value)] = value;
        };

        foreach (var evt in _capturedEvents ?? new List<FrameEventInfo>())
        {
            add(evt.eventName);
            add(evt.gameObjectPath);
            add(evt.materialAssetPath);
            add(evt.materialName);
            add(BestShaderName(evt));
            add(evt.resolvedShaderAssetPath);
            add(evt.meshAssetPath);
            add(evt.meshName);
        }
        foreach (var batch in BuildUiBatchCandidateMap(_capturedEvents ?? new List<FrameEventInfo>()).Values)
        {
            add(batch.canvasPath);
            add(GetRepresentativeUiPath(batch));
            add(batch.materialPath);
            add(batch.materialName);
            add(batch.shaderName);
            add(batch.texturePath);
            add(batch.textureName);
            foreach (var graphic in batch.graphics.Take(32))
                add(graphic.path);
        }
        foreach (var particle in GetSceneParticleSystems())
            add(GetHierarchyPath(particle.gameObject));
        foreach (var particle in GetActiveParticleCandidates())
            add(particle.path);
        foreach (var renderer in BuildSceneRendererRecords())
        {
            add(renderer.path);
            add(renderer.materialName);
            add(renderer.shaderName);
            add(renderer.meshName);
        }

        return BuildDictionaryObjectJson("framedebug-ai-string-dictionary/v1", strings);
    }

    private string BuildAiMaterialDictionaryJson()
    {
        var rows = new SortedDictionary<string, string[]>();
        Action<string, string, string> addRow = (name, path, shader) =>
        {
            string key = !string.IsNullOrEmpty(path) ? path : name;
            if (string.IsNullOrEmpty(key)) return;
            string[] row = new[] { name ?? "", path ?? "", shader ?? "" };
            rows[StableStringId("mat", key)] = row;
            if (!string.IsNullOrEmpty(name) && !string.Equals(name, key, StringComparison.Ordinal))
                rows[StableStringId("mat", name)] = row;
        };
        foreach (var evt in _capturedEvents ?? new List<FrameEventInfo>())
        {
            if (!string.IsNullOrEmpty(evt.materialName))
                addRow(evt.materialName, evt.materialAssetPath, BestShaderName(evt));
        }
        foreach (var graphic in BuildUiGraphicRecords())
            addRow(graphic.materialName, graphic.materialPath, graphic.shaderName);
        foreach (var renderer in BuildSceneRendererRecords())
            addRow(renderer.materialName, renderer.materialPath, renderer.shaderName);
        foreach (var particle in GetActiveParticleCandidates())
            addRow(particle.materialName, particle.materialPath, particle.shaderName);
        foreach (var ps in GetSceneParticleSystems())
        {
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var material = renderer != null ? renderer.sharedMaterial : null;
            if (material == null) continue;
            string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(material));
            addRow(material.name, path, material.shader != null ? material.shader.name : "");
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-material-dictionary/v2", true);
        sb.AppendLine("  \"materials\": [");
        int i = 0;
        foreach (var kv in rows)
        {
            string[] row = kv.Value;
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "id", kv.Key, true);
            AppendJsonPropertyInline(sb, "canonicalId", BuildCanonicalId("material", row.Length > 1 ? row[1] : "", row.Length > 0 ? row[0] : ""), true);
            AppendJsonPropertyInline(sb, "name", row.Length > 0 ? row[0] : "", true);
            AppendJsonPropertyInline(sb, "path", row.Length > 1 ? row[1] : "", true);
            AppendJsonPropertyInline(sb, "shader", row.Length > 2 ? row[2] : "", false);
            sb.Append(++i < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiShaderDictionaryJson()
    {
        var rows = new SortedDictionary<string, string[]>();
        Action<string, string, string> addRow = (name, path, guid) =>
        {
            string key = !string.IsNullOrEmpty(path) ? path : name;
            if (string.IsNullOrEmpty(key)) return;
            string[] row = new[] { name ?? "", path ?? "", guid ?? "" };
            rows[StableStringId("sh", key)] = row;
            if (!string.IsNullOrEmpty(name) && !string.Equals(name, key, StringComparison.Ordinal))
                rows[StableStringId("sh", name)] = row;
        };
        foreach (var evt in _capturedEvents ?? new List<FrameEventInfo>())
        {
            addRow(BestShaderName(evt), evt.resolvedShaderAssetPath, evt.resolvedShaderGuid);
        }
        foreach (var graphic in BuildUiGraphicRecords())
        {
            if (!string.IsNullOrEmpty(graphic.shaderName))
                addRow(graphic.shaderName, graphic.shaderPath, !string.IsNullOrEmpty(graphic.shaderPath) ? AssetDatabase.AssetPathToGUID(graphic.shaderPath) : "");
        }
        foreach (var renderer in BuildSceneRendererRecords())
        {
            if (!string.IsNullOrEmpty(renderer.shaderName))
                addRow(renderer.shaderName, renderer.shaderPath, !string.IsNullOrEmpty(renderer.shaderPath) ? AssetDatabase.AssetPathToGUID(renderer.shaderPath) : "");
        }
        foreach (var particle in GetActiveParticleCandidates())
        {
            if (!string.IsNullOrEmpty(particle.shaderName))
                addRow(particle.shaderName, "", "");
        }
        foreach (var ps in GetSceneParticleSystems())
        {
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            var material = renderer != null ? renderer.sharedMaterial : null;
            if (material != null && material.shader != null)
            {
                string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(material.shader));
                addRow(material.shader.name, path, !string.IsNullOrEmpty(path) ? AssetDatabase.AssetPathToGUID(path) : "");
            }
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "framedebug-ai-shader-dictionary/v2", true);
        sb.AppendLine("  \"shaders\": [");
        int i = 0;
        foreach (var kv in rows)
        {
            string[] row = kv.Value;
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "id", kv.Key, true);
            AppendJsonPropertyInline(sb, "canonicalId", BuildCanonicalId("shader", row.Length > 1 ? row[1] : "", row.Length > 0 ? row[0] : ""), true);
            AppendJsonPropertyInline(sb, "name", row.Length > 0 ? row[0] : "", true);
            AppendJsonPropertyInline(sb, "path", row.Length > 1 ? row[1] : "", true);
            AppendJsonPropertyInline(sb, "guid", row.Length > 2 ? row[2] : "", false);
            sb.Append(++i < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildAiTextureDictionaryJson()
    {
        var strings = new SortedDictionary<string, string>();
        foreach (var graphic in BuildUiGraphicRecords())
        {
            string value = !string.IsNullOrEmpty(graphic.texturePath) ? graphic.texturePath : graphic.textureName;
            if (!string.IsNullOrEmpty(value))
                strings[StableStringId("tex", value)] = value;
        }
        foreach (var renderer in BuildSceneRendererRecords())
        {
            string value = renderer.textureName;
            if (!string.IsNullOrEmpty(value))
                strings[StableStringId("tex", value)] = value;
        }
        foreach (var particle in GetActiveParticleCandidates())
        {
            string value = particle.textureName;
            if (!string.IsNullOrEmpty(value))
                strings[StableStringId("tex", value)] = value;
        }
        foreach (var texture in GetRuntimePlayerSnapshot().textures)
        {
            string value = texture.name;
            if (!string.IsNullOrEmpty(value))
                strings[StableStringId("tex", value)] = value + " [" + texture.type + ";" + texture.width.ToString(CultureInfo.InvariantCulture) + "x" + texture.height.ToString(CultureInfo.InvariantCulture) + ";" + texture.format + "]";
        }

        return BuildDictionaryObjectJson("framedebug-ai-texture-dictionary/v1", strings, "textures");
    }

    private static string BuildDictionaryObjectJson(string schemaVersion, SortedDictionary<string, string> strings, string propertyName = "strings")
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", schemaVersion, true);
        sb.AppendLine("  \"" + EscapeJson(propertyName) + "\": {");
        int i = 0;
        foreach (var kv in strings)
        {
            sb.Append("    ").Append(Json(kv.Key)).Append(": ").Append(Json(kv.Value));
            sb.Append(++i < strings.Count ? "," : "");
            sb.AppendLine();
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendCommaIfNeeded(StringBuilder sb, ref int count)
    {
        if (count++ > 0) sb.Append(", ");
    }

    private static string[] BuildEventLimitations(FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates)
    {
        var rows = new List<string>();
        rows.Add("Frame Debugger has no CPU/GPU timing.");
        if (!HasDirectAttribution(evt))
            rows.Add("No direct object/material/shader attribution in the Frame Debugger event row.");
        if (IsUiSubBatchEvent(evt))
            rows.Add("Canvas.RenderSubBatch has no direct Graphic reference; UI attribution is runtime-order inference.");
        if (uiBatch != null)
            rows.Add(GetUiBatchRiskNote());
        if (particleCandidates != null && particleCandidates.Count > 0)
            rows.Add("Particle candidates are inferred by active scene state plus material/shader/path matching.");
        string category = ClassifyUnresolvedEvent(evt, uiBatch, srpBatch, particleCandidates);
        if (!string.IsNullOrEmpty(category))
            rows.Add("Unresolved category: " + category + ".");
        return rows.Distinct().ToArray();
    }

    private static string GetRepresentativeUiPath(UiBatchCandidate candidate)
    {
        if (candidate == null || candidate.graphics == null || candidate.graphics.Count == 0) return "";
        return candidate.graphics
            .OrderBy(g => g.depth)
            .ThenBy(g => g.path)
            .Select(g => g.path)
            .FirstOrDefault() ?? "";
    }

    private static string GetUiBatchDepthRange(UiBatchCandidate candidate)
    {
        if (candidate == null || candidate.graphics == null || candidate.graphics.Count == 0) return "";
        int min = candidate.graphics.Min(g => g.depth);
        int max = candidate.graphics.Max(g => g.depth);
        return min.ToString(CultureInfo.InvariantCulture) + "-" + max.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetUiBatchRiskNote()
    {
        return "Dynamic layout, nested canvases, disabled cameras, or engine-side material mutation may shift Canvas.RenderSubBatch order.";
    }

    private static string StableStringId(string prefix, string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 16777619;
            }
            return prefix + "_" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatRate(int numerator, int denominator)
    {
        if (denominator <= 0) return "0%";
        double rate = Math.Max(0, numerator) * 100.0 / denominator;
        return rate.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }

    private static string BuildCanonicalId(string type, string path, string name)
    {
        string value = !string.IsNullOrEmpty(path) ? path : name;
        if (string.IsNullOrEmpty(value)) return "";
        return type + ":" + value.Replace("\\", "/");
    }

    private static string FindLatestPreviousAiExport(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "";
        return Directory.GetDirectories(root, "FrameDebugAI_*")
            .Where(dir => File.Exists(Path.Combine(dir, "ai_data_quality.json")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? "";
    }

    private static string[] BuildBaselineInterpretationHints(bool captureContentChanged, bool exporterSchemaChanged, bool attributionRegressed, bool candidateInferenceImproved)
    {
        var hints = new List<string>();
        if (captureContentChanged)
            hints.Add("capture_content_changed: event count changed, attribution-rate delta may come from a different sampled frame.");
        if (exporterSchemaChanged)
            hints.Add("exporter_schema_changed: file layout or schema changed, compare evidence quality with caution.");
        if (attributionRegressed)
            hints.Add("attribution_regressed: direct attribution rate decreased in this export.");
        if (candidateInferenceImproved)
            hints.Add("candidate_inference_improved: candidate attribution count increased.");
        if (hints.Count == 0)
            hints.Add("no_major_quality_flag: headline evidence-quality counters are stable.");
        return hints.ToArray();
    }

    private static bool PreviousUsesOldFrameDebugSchema(string previousExportDir)
    {
        if (string.IsNullOrEmpty(previousExportDir) || !Directory.Exists(previousExportDir)) return false;
        return File.Exists(Path.Combine(previousExportDir, "ai_framedebug_digest.json")) ||
               File.Exists(Path.Combine(previousExportDir, "ai_framedebug_events.jsonl")) ||
               File.Exists(Path.Combine(previousExportDir, "ai_events.jsonl"));
    }

    private static string[] GetCoreAiFiles()
    {
        return new[]
        {
            "AI_ANALYSIS_GUIDE.md",
            "ai_summary.json",
            "ai_data_quality.json",
            "ai_analysis_blocking_policy.json",
            "ai_data_quality.md",
            "ai_runtime_resolution_snapshot.json",
            "ai_transparent_submission_snapshot.json",
            "ai_baseline_comparison.json",
            "ai_export_timings.json",
            "ai_events_raw.jsonl",
            "ai_frameeventdata_diagnostics.json",
            "ai_direct_object_diagnostics.json",
            "ai_event_evidence.jsonl",
            "ai_event_analysis.jsonl",
            "ai_object_inventory.json",
            "ai_event_attribution_index.jsonl",
            "ai_ui_batches.jsonl",
            "ai_ui_batch_details.jsonl",
            "ai_srp_batch_candidates.jsonl",
            "ai_srp_batch_diagnostics.json",
            "ai_resource_fingerprints.json",
            "ai_unity_renderdoc_correlation_seed.json",
            "ai_deep_event_sampling_plan.json",
            "ai_reflection_inventory.json",
            "snapshots/ui_graphics.jsonl",
            "snapshots/particles.jsonl",
            "snapshots/renderers_compact.jsonl",
            "snapshots/renderers.jsonl",
            "snapshots/cameras.json",
            "snapshots/renderfeatures.json",
            "snapshots/runtime_texture_thumbnails.json",
            "dictionaries/strings.json",
            "dictionaries/materials.json",
            "dictionaries/shaders.json",
            "dictionaries/textures.json",
            "human/FrameDebug.md",
            "debug/ai_events_raw_full.jsonl",
            "debug/ai_direct_object_diagnostics_full.json"
        };
    }

    private static void AppendCoreFileStatusObject(StringBuilder sb, string previousExportDir, string currentExportDir, bool trailingComma)
    {
        sb.AppendLine("  \"coreFiles\": [");
        string[] files = GetCoreAiFiles();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            bool currentExists = !string.IsNullOrEmpty(currentExportDir) &&
                                 (File.Exists(Path.Combine(currentExportDir, file)) ||
                                  string.Equals(file, "ai_baseline_comparison.json", StringComparison.OrdinalIgnoreCase));
            bool previousExists = !string.IsNullOrEmpty(previousExportDir) && File.Exists(Path.Combine(previousExportDir, file));
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", file, true);
            AppendJsonPropertyInline(sb, "currentExists", currentExists, true);
            AppendJsonPropertyInline(sb, "previousExists", previousExists, true);
            AppendJsonPropertyInline(sb, "status", currentExists && previousExists ? "kept" : currentExists ? "added" : previousExists ? "removed" : "missing", false);
            sb.Append(i + 1 < files.Length ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");
        sb.AppendLine("  \"legacyFiles\": {");
        WriteJsonProperty(sb, "previousHadAiEventsJsonl", !string.IsNullOrEmpty(previousExportDir) && File.Exists(Path.Combine(previousExportDir, "ai_events.jsonl")), true, 4);
        WriteJsonProperty(sb, "currentUsesAiEventsRawJsonl", !string.IsNullOrEmpty(currentExportDir) && File.Exists(Path.Combine(currentExportDir, "ai_events_raw.jsonl")), true, 4);
        WriteJsonProperty(sb, "previousHadFrameDebugPrefixedFiles", PreviousUsesOldFrameDebugSchema(previousExportDir), false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendFileSizeComparisonObject(StringBuilder sb, string previousExportDir, string currentExportDir, bool trailingComma)
    {
        AppendFileSizeComparisonObject(sb, previousExportDir, currentExportDir, -1, trailingComma);
    }

    private static void AppendFileSizeComparisonObject(StringBuilder sb, string previousExportDir, string currentExportDir, long currentBaselineSizeOverride, bool trailingComma)
    {
        sb.AppendLine("  \"fileSizeBytes\": [");
        string[] files = GetCoreAiFiles();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            long currentSize = file == "ai_baseline_comparison.json" && currentBaselineSizeOverride >= 0
                ? currentBaselineSizeOverride
                : GetFileSizeSafe(Path.Combine(currentExportDir ?? "", file));
            long previousSize = GetFileSizeSafe(Path.Combine(previousExportDir ?? "", file));
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "path", file, true);
            AppendJsonPropertyInline(sb, "current", currentSize.ToString(CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "previous", previousSize.ToString(CultureInfo.InvariantCulture), true);
            AppendJsonPropertyInline(sb, "delta", (currentSize - previousSize).ToString(CultureInfo.InvariantCulture), false);
            sb.Append(i + 1 < files.Length ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static long GetFileSizeSafe(string path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void AppendUnresolvedCategoryDeltaObject(StringBuilder sb, List<IssueEntry> currentRows, string previousQualityJson, bool trailingComma)
    {
        var names = new SortedSet<string>(currentRows.Select(r => r.name));
        foreach (string known in new[] { "Canvas.RenderSubBatch", "UI.RenderOverlays", "UICamera.MeshWithoutGraphic", "UI.UnknownMesh", "SRPBatch", "PostProcessing", "RenderFeature", "RenderFeature.ColorTint", "DynamicGeometry.UnresolvedRenderer", "Clear/Copy/Depth", "Unknown Mesh", "Reflection/API unavailable", "Other" })
            names.Add(known);

        sb.AppendLine("  \"unresolvedCategoryDelta\": [");
        int i = 0;
        foreach (string name in names)
        {
            int current = currentRows.FirstOrDefault(r => r.name == name)?.count ?? 0;
            int previous = ExtractIssueCount(previousQualityJson, name);
            sb.Append("    {");
            AppendJsonPropertyInline(sb, "category", name, true);
            AppendJsonPropertyInline(sb, "current", current, true);
            AppendJsonPropertyInline(sb, "previous", previous, true);
            AppendJsonPropertyInline(sb, "delta", current - previous, false);
            sb.Append(++i < names.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append("  ]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static int ExtractJsonInt(string json, string propertyName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) return 0;
        string marker = "\"" + propertyName + "\"";
        int index = json.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return 0;
        int colon = json.IndexOf(':', index + marker.Length);
        if (colon < 0) return 0;
        int start = colon + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        if (end <= start) return 0;
        int value;
        return int.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
    }

    private static string ExtractJsonString(string json, string propertyName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) return "";
        string marker = "\"" + propertyName + "\"";
        int index = json.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return "";
        int colon = json.IndexOf(':', index + marker.Length);
        if (colon < 0) return "";
        int quote = json.IndexOf('"', colon + 1);
        if (quote < 0) return "";
        int end = quote + 1;
        bool escape = false;
        while (end < json.Length)
        {
            char c = json[end];
            if (escape)
            {
                escape = false;
            }
            else if (c == '\\')
            {
                escape = true;
            }
            else if (c == '"')
            {
                return json.Substring(quote + 1, end - quote - 1);
            }
            end++;
        }
        return "";
    }

    private static int ExtractIssueCount(string json, string issueName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(issueName)) return 0;
        string marker = "\"name\": \"" + issueName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        int index = json.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return 0;
        int countIndex = json.IndexOf("\"count\"", index, StringComparison.Ordinal);
        if (countIndex < 0) return 0;
        int colon = json.IndexOf(':', countIndex);
        if (colon < 0) return 0;
        int start = colon + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        int value;
        return end > start && int.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
    }

    private static IEnumerable<RenderFeatureSnapshot> BuildRenderFeatureSnapshotsUncached()
    {
        var asset = UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset as ScriptableObject;
        foreach (var rendererData in GetRendererDataObjects(asset))
        {
            foreach (var feature in GetRendererFeatures(rendererData))
            {
                yield return new RenderFeatureSnapshot
                {
                    rendererName = rendererData != null ? rendererData.name : "",
                    rendererType = rendererData != null ? rendererData.GetType().Name : "",
                    rendererAssetPath = rendererData != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(rendererData)) : "",
                    featureName = feature != null ? feature.name : "",
                    featureType = feature != null ? feature.GetType().FullName : "",
                    active = feature != null && GetFeatureActive(feature),
                    featureAssetPath = feature != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(feature)) : "",
                    materialShaderRefs = feature != null ? SummarizeObjectFields(feature, 32) : ""
                };
            }
        }
    }

    private static RenderFeatureSnapshot FindRenderFeatureCandidate(FrameEventInfo evt, List<RenderFeatureSnapshot> renderFeatures)
    {
        if (evt == null) return null;
        string name = evt.eventName ?? "";
        foreach (var feature in renderFeatures ?? new List<RenderFeatureSnapshot>())
        {
            if (string.IsNullOrEmpty(feature.featureName)) continue;
            if (name.IndexOf(feature.featureName, StringComparison.OrdinalIgnoreCase) >= 0)
                return feature;
        }

        string category = ClassifyUnresolvedEvent(evt);
        if (category == "RenderFeature" || category == "RenderFeature.ColorTint" || category == "PostProcessing" || category == "ColorGradingLUT")
        {
            return (renderFeatures ?? new List<RenderFeatureSnapshot>()).FirstOrDefault(f => f.active) ?? new RenderFeatureSnapshot
            {
                featureName = category,
                active = true
            };
        }

        return null;
    }

    private static List<KeyValuePair<string, int>> BuildStageStats(IEnumerable<FrameEventInfo> events)
    {
        return BuildSimpleStats(events, e => ParseStageName(e.eventName));
    }

    private static List<KeyValuePair<string, int>> BuildSimpleStats<T>(IEnumerable<T> events, Func<T, string> selector)
    {
        return events
            .Select(selector)
            .Where(v => !string.IsNullOrEmpty(v) && v != "-")
            .GroupBy(v => v)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(10)
            .ToList();
    }

    private static List<KeyValuePair<string, int>> BuildCameraStageStats(IEnumerable<FrameEventInfo> events)
    {
        return BuildSimpleStats(events, BuildCameraStageKey);
    }

    private static void AppendDataQualityObject(StringBuilder sb, List<FrameEventInfo> events, bool trailingComma)
    {
        int total = events != null ? events.Count : 0;
        int withObject = events != null ? events.Count(e => !string.IsNullOrEmpty(e.gameObjectName) || !string.IsNullOrEmpty(e.gameObjectPath)) : 0;
        int withFrameDebuggerGameObject = events != null ? events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerGameObjectPath)) : 0;
        int withFrameDebuggerRenderer = events != null ? events.Count(e => !string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int withMaterial = events != null ? events.Count(e => !string.IsNullOrEmpty(e.materialName)) : 0;
        int withShader = events != null ? events.Count(e => !string.IsNullOrEmpty(BestShaderName(e))) : 0;
        int withFull = events != null ? events.Count(e => (!string.IsNullOrEmpty(e.gameObjectName) || !string.IsNullOrEmpty(e.gameObjectPath)) && !string.IsNullOrEmpty(e.materialName) && !string.IsNullOrEmpty(BestShaderName(e))) : 0;
        int withDirectMeshName = events != null ? events.Count(e => !string.IsNullOrEmpty(e.meshName)) : 0;
        int withFrameDebuggerMeshes = events != null ? events.Count(HasFrameDebuggerDetailMeshes) : 0;
        int withFrameDebuggerDetailMeshNames = events != null ? events.Count(e => e.detailMeshNames != null && e.detailMeshNames.Count > 0) : 0;
        int withFrameDebuggerMeshInstanceIds = events != null ? events.Count(e => e.detailMeshInstanceIds != null && e.detailMeshInstanceIds.Count > 0) : 0;
        int frameEventDataAttempted = events != null ? events.Count(e => e.frameEventDataAttempted) : 0;
        int frameEventDataSuccess = events != null ? events.Count(e => e.frameEventDataSuccess) : 0;
        int frameEventDataRawDataFields = events != null ? events.Count(e => e.rawDataFields != null && e.rawDataFields.Count > 0) : 0;
        int withBatchBreakCause = events != null ? events.Count(e => !string.IsNullOrEmpty(e.batchBreakCause)) : 0;
        int withLightMode = events != null ? events.Count(e => !string.IsNullOrEmpty(e.passLightMode)) : 0;
        int uiSubBatch = events != null ? events.Count(IsUiSubBatchEvent) : 0;
        var uiBatchMap = events != null ? BuildUiBatchCandidateMap(events) : new Dictionary<int, UiBatchCandidate>();
        var particleMap = events != null ? BuildParticleCandidateMap(events) : new Dictionary<int, List<ParticleCandidate>>();
        var srpBatchMap = events != null ? BuildSrpBatchCandidateMap(events) : new Dictionary<int, SrpBatchCandidate>();
        int unresolvedUiSubBatch = events != null ? events.Count(e => IsUiSubBatchEvent(e) && IsAnalysisUnresolvedEvent(e, uiBatchMap.ContainsKey(e.index) ? uiBatchMap[e.index] : null, srpBatchMap.ContainsKey(e.index) ? srpBatchMap[e.index] : null, particleMap.ContainsKey(e.index) ? particleMap[e.index] : null)) : 0;
        int uiSubBatchCandidateCovered = events != null ? events.Count(e => IsUiSubBatchEvent(e) && uiBatchMap.ContainsKey(e.index)) : 0;
        int uiSubBatchWithoutFrameDebuggerGraphic = events != null ? events.Count(e => IsUiSubBatchEvent(e) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int meshExpectedEvents = events != null ? events.Count(IsMeshDetailExpectedEvent) : 0;
        int meshExpectedButMissingDetailMeshes = events != null ? events.Count(e => IsMeshDetailExpectedEvent(e) && !HasFrameDebuggerDetailMeshes(e)) : 0;
        int meshNotExpectedEvents = events != null ? events.Count(e => !IsMeshDetailExpectedEvent(e)) : 0;
        int srpBatchMeshExpectedEvents = events != null ? events.Count(IsSrpBatchEvent) : 0;
        int srpBatchMeshDetailCoveredEvents = events != null ? events.Count(e => IsSrpBatchEvent(e) && HasFrameDebuggerDetailMeshes(e)) : 0;
        int meshEventDirectRendererCoveredEvents = events != null ? events.Count(e => IsNonUiMeshEvent(e) && !string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int srpBatchMeshDetailMissing = events != null ? events.Count(e => IsSrpBatchEvent(e) && !HasFrameDebuggerDetailMeshes(e)) : 0;
        int ordinaryMeshDirectRendererCovered = events != null ? events.Count(e => IsOrdinaryMeshEvent(e) && !string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int ordinaryMeshExpectedButMissingDetailMeshes = events != null ? events.Count(e => IsOrdinaryMeshEvent(e) && !HasFrameDebuggerDetailMeshes(e) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int uiMeshExpectedButNoGraphic = events != null ? events.Count(e => IsUiMeshAttributionEvent(e) && !uiBatchMap.ContainsKey(e.index) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath)) : 0;
        int dynamicGeometryDirectRendererCovered = events != null ? events.Count(e => IsDynamicGeometryEvent(e) && !string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int dynamicGeometryMissingRenderer = events != null ? events.Count(e => IsDynamicGeometryEvent(e) && string.IsNullOrEmpty(e.frameDebuggerRendererPath) && !HasFrameDebuggerDetailMeshes(e)) : 0;
        int batchedEvents = events != null ? events.Count(IsBatchedFrameDebuggerEvent) : 0;
        int batchedEventsWithoutDirectObject = events != null ? events.Count(e => IsBatchedFrameDebuggerEvent(e) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int nonBatchedEvents = events != null ? events.Count(e => !IsBatchedFrameDebuggerEvent(e)) : 0;
        int directNonBatchedEvents = events != null ? events.Count(e => !IsBatchedFrameDebuggerEvent(e) && HasDirectAttribution(e)) : 0;
        int nonBatchedMeshEvents = events != null ? events.Count(e => IsNonUiMeshEvent(e) && !IsBatchedFrameDebuggerEvent(e)) : 0;
        int directNonBatchedMeshEvents = events != null ? events.Count(e => IsNonUiMeshEvent(e) && !IsBatchedFrameDebuggerEvent(e) && HasDirectAttribution(e)) : 0;
        var uiGraphics = BuildUiGraphicRecords();
        int uiSnapshotCount = uiGraphics.Count;
        int activeUiSnapshotCount = uiGraphics.Count(g => g.active && g.enabled);
        int uiSnapshotWithScreenRect = uiGraphics.Count(g => !string.IsNullOrEmpty(g.screenRect));
        int uiSnapshotTextCount = uiGraphics.Count(g => g.isTextComponent);
        int uiSnapshotWithFingerprint = uiGraphics.Count(g => !string.IsNullOrEmpty(g.resourceFingerprint));
        var rendererRecords = BuildSceneRendererRecords();
        int rendererSnapshotWithFingerprint = rendererRecords.Count(r => !string.IsNullOrEmpty(r.resourceFingerprint));
        int rendererSnapshotWithTexture = rendererRecords.Count(r => !string.IsNullOrEmpty(r.textureName));
        var particles = GetSceneParticleSystems();
        int particleSnapshotCount = particles.Count;
        int activeParticleSnapshotCount = particles.Count(IsActiveParticleSystemForFrame);
        int particleSnapshotWithFingerprint = GetActiveParticleCandidates().Count(p => !string.IsNullOrEmpty(p.resourceFingerprint));
        int candidateAttributionCount = uiBatchMap.Count + srpBatchMap.Count + particleMap.SelectMany(kv => kv.Value).Select(c => c.id).Distinct().Count();

        sb.AppendLine("  \"dataQuality\": {");
        WriteJsonProperty(sb, "eventsWithObject", withObject, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerGameObject", withFrameDebuggerGameObject, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerRenderer", withFrameDebuggerRenderer, true, 4);
        WriteJsonProperty(sb, "eventsWithMaterial", withMaterial, true, 4);
        WriteJsonProperty(sb, "eventsWithShader", withShader, true, 4);
        WriteJsonProperty(sb, "eventsWithObjectMaterialShader", withFull, true, 4);
        WriteJsonProperty(sb, "eventsWithDirectMeshName", withDirectMeshName, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerMeshes", withFrameDebuggerMeshes, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerDetailMeshNames", withFrameDebuggerDetailMeshNames, true, 4);
        WriteJsonProperty(sb, "eventsWithFrameDebuggerMeshInstanceIds", withFrameDebuggerMeshInstanceIds, true, 4);
        WriteJsonProperty(sb, "meshExpectedEvents", meshExpectedEvents, true, 4);
        WriteJsonProperty(sb, "meshExpectedButMissingDetailMeshes", meshExpectedButMissingDetailMeshes, true, 4);
        WriteJsonProperty(sb, "meshNotExpectedEvents", meshNotExpectedEvents, true, 4);
        WriteJsonProperty(sb, "srpBatchMeshExpectedEvents", srpBatchMeshExpectedEvents, true, 4);
        WriteJsonProperty(sb, "srpBatchMeshDetailCoveredEvents", srpBatchMeshDetailCoveredEvents, true, 4);
        WriteJsonProperty(sb, "srpBatchMeshDetailMissing", srpBatchMeshDetailMissing, true, 4);
        WriteJsonProperty(sb, "meshEventDirectRendererCoveredEvents", meshEventDirectRendererCoveredEvents, true, 4);
        WriteJsonProperty(sb, "ordinaryMeshDirectRendererCovered", ordinaryMeshDirectRendererCovered, true, 4);
        WriteJsonProperty(sb, "ordinaryMeshExpectedButMissingDetailMeshes", ordinaryMeshExpectedButMissingDetailMeshes, true, 4);
        WriteJsonProperty(sb, "uiMeshExpectedButNoGraphic", uiMeshExpectedButNoGraphic, true, 4);
        WriteJsonProperty(sb, "dynamicGeometryDirectRendererCovered", dynamicGeometryDirectRendererCovered, true, 4);
        WriteJsonProperty(sb, "dynamicGeometryMissingRenderer", dynamicGeometryMissingRenderer, true, 4);
        WriteJsonProperty(sb, "frameEventDataAttemptedCount", frameEventDataAttempted, true, 4);
        WriteJsonProperty(sb, "frameEventDataSuccessCount", frameEventDataSuccess, true, 4);
        WriteJsonProperty(sb, "frameEventDataRawDataFieldEventCount", frameEventDataRawDataFields, true, 4);
        WriteJsonProperty(sb, "eventsWithBatchBreakCause", withBatchBreakCause, true, 4);
        WriteJsonProperty(sb, "eventsWithLightMode", withLightMode, true, 4);
        WriteJsonProperty(sb, "eventsWithoutObjectMaterialShader", Math.Max(0, total - withFull), true, 4);
        WriteJsonProperty(sb, "directAttributionRate", FormatRate(withFull, total), true, 4);
        WriteJsonProperty(sb, "directAttributionRateExcludingBatched", FormatRate(directNonBatchedEvents, nonBatchedEvents), true, 4);
        WriteJsonProperty(sb, "directAttributionRateForNonBatchedMesh", FormatRate(directNonBatchedMeshEvents, nonBatchedMeshEvents), true, 4);
        WriteJsonProperty(sb, "uiSubBatchEvents", uiSubBatch, true, 4);
        WriteJsonProperty(sb, "uiSubBatchWithoutFrameDebuggerGraphic", uiSubBatchWithoutFrameDebuggerGraphic, true, 4);
        WriteJsonProperty(sb, "uiSubBatchCandidateCovered", uiSubBatchCandidateCovered, true, 4);
        WriteJsonProperty(sb, "uiSubBatchUnresolved", unresolvedUiSubBatch, true, 4);
        WriteJsonProperty(sb, "unresolvedUiSubBatchEvents", unresolvedUiSubBatch, true, 4);
        WriteJsonProperty(sb, "uiDirectAttributionRate", FormatRate(uiSubBatch - unresolvedUiSubBatch, uiSubBatch), true, 4);
        WriteJsonProperty(sb, "uiCandidateCoverageRate", FormatRate(uiSubBatchCandidateCovered, uiSubBatch), true, 4);
        WriteJsonProperty(sb, "srpMeshCoverageRate", FormatRate(srpBatchMeshDetailCoveredEvents, srpBatchMeshExpectedEvents), true, 4);
        WriteJsonProperty(sb, "batchedEventCount", batchedEvents, true, 4);
        WriteJsonProperty(sb, "batchedEventsWithoutDirectObjectExpected", batchedEventsWithoutDirectObject, true, 4);
        WriteJsonProperty(sb, "candidateAttributionCount", candidateAttributionCount, true, 4);
        WriteJsonProperty(sb, "uiBatchCandidateCount", uiBatchMap.Count, true, 4);
        WriteJsonProperty(sb, "srpBatchCandidateCount", srpBatchMap.Count, true, 4);
        WriteJsonProperty(sb, "particleCandidateCount", particleMap.SelectMany(kv => kv.Value).Select(c => c.id).Distinct().Count(), true, 4);
        WriteJsonProperty(sb, "uiSnapshotObjectCount", uiSnapshotCount, true, 4);
        WriteJsonProperty(sb, "activeUiSnapshotObjectCount", activeUiSnapshotCount, true, 4);
        WriteJsonProperty(sb, "uiSnapshotWithScreenRect", uiSnapshotWithScreenRect, true, 4);
        WriteJsonProperty(sb, "uiSnapshotTextObjectCount", uiSnapshotTextCount, true, 4);
        WriteJsonProperty(sb, "uiSnapshotWithFingerprint", uiSnapshotWithFingerprint, true, 4);
        WriteJsonProperty(sb, "rendererSnapshotWithFingerprint", rendererSnapshotWithFingerprint, true, 4);
        WriteJsonProperty(sb, "rendererSnapshotWithTexture", rendererSnapshotWithTexture, true, 4);
        WriteJsonProperty(sb, "particleSnapshotObjectCount", particleSnapshotCount, true, 4);
        WriteJsonProperty(sb, "activeParticleSnapshotObjectCount", activeParticleSnapshotCount, true, 4);
        WriteJsonProperty(sb, "particleSnapshotWithFingerprint", particleSnapshotWithFingerprint, true, 4);
        AppendDataSufficiencyObject(sb, "dataSufficiency", events, false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTimingMetadataObject(StringBuilder sb, bool trailingComma)
    {
        sb.AppendLine("  \"timing\": {");
        WriteJsonProperty(sb, "hasTiming", false, true, 4);
        WriteJsonProperty(sb, "costRankSource", "draw_count_only", true, 4);
        WriteJsonProperty(sb, "timingAvailable", false, true, 4);
        WriteJsonProperty(sb, "costRankingBasis", "draw_count_only", true, 4);
        AppendStringArray(sb, "cannotConclude", GetFrameDebuggerCannotConclude(), false, 4);
        sb.Append("  }");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendDataSufficiencyObject(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma, int indent = 2)
    {
        events = events ?? new List<FrameEventInfo>();
        int total = events.Count;
        int directObjectRows = events.Count(HasDirectAttribution);
        int directObjectMaterialShaderRows = events.Count(e =>
            HasDirectAttribution(e) &&
            !string.IsNullOrEmpty(e.materialName) &&
            !string.IsNullOrEmpty(BestShaderName(e)));
        int frameEventDataSuccess = events.Count(e => e.frameEventDataSuccess);
        int detailMeshRows = events.Count(HasFrameDebuggerDetailMeshes);
        int meshExpectedEvents = events.Count(IsMeshDetailExpectedEvent);
        int meshMissingDetail = events.Count(e => IsMeshDetailExpectedEvent(e) && !HasFrameDebuggerDetailMeshes(e));
        int dynamicGeometryMissing = events.Count(e => IsDynamicGeometryEvent(e) && string.IsNullOrEmpty(e.frameDebuggerRendererPath) && !HasFrameDebuggerDetailMeshes(e));
        int srpBatchMissing = events.Count(e => IsSrpBatchEvent(e) && !HasFrameDebuggerDetailMeshes(e));
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        int uiSubBatch = events.Count(IsUiSubBatchEvent);
        int uiCovered = events.Count(e => IsUiSubBatchEvent(e) && uiBatchMap.ContainsKey(e.index));
        string renderDocDir = GetCurrentLinkedRenderDocAnalysisDirectory();
        bool hasRenderDocDir = !string.IsNullOrEmpty(renderDocDir) && Directory.Exists(renderDocDir);
        bool hasDeepEventFiles = hasRenderDocDir && Directory.EnumerateFiles(renderDocDir, "*deep*event*", SearchOption.TopDirectoryOnly).Any();
        bool hasRenderDocIndex = hasRenderDocDir &&
                                 File.Exists(Path.Combine(renderDocDir, "pass_table.json")) &&
                                 (File.Exists(Path.Combine(renderDocDir, "pipeline_index.json")) || File.Exists(Path.Combine(renderDocDir, "pipeline_state_changes.jsonl")));

        var enoughFor = new List<string>();
        if (total > 0) enoughFor.Add("camera_stage_event_count");
        if (hasRenderDocIndex) enoughFor.Add("renderdoc_pass_resource_state_structure");
        if (BuildSceneRendererRecords().Any(r => !string.IsNullOrEmpty(r.resourceFingerprint)) ||
            BuildUiGraphicRecords().Any(g => !string.IsNullOrEmpty(g.resourceFingerprint)) ||
            GetActiveParticleCandidates().Any(p => !string.IsNullOrEmpty(p.resourceFingerprint)))
            enoughFor.Add("runtime_resource_candidate_matching");
        if (uiSubBatch > 0 && uiCovered == uiSubBatch) enoughFor.Add("ui_canvas_order_candidate_attribution");
        if (hasDeepEventFiles) enoughFor.Add("deep_event_sampled_gpu_event_state");

        var notEnoughFor = new List<string>();
        if (directObjectMaterialShaderRows == 0) notEnoughFor.Add("direct_object_material_shader_ownership");
        if (meshExpectedEvents > 0 && meshMissingDetail > 0) notEnoughFor.Add("mesh_or_renderer_level_draw_ownership");
        if (!hasDeepEventFiles && hasRenderDocIndex) notEnoughFor.Add("deep_event_confirmed_draw_level_state_until_preflight_runs");
        notEnoughFor.Add("gpu_cpu_timing_or_cost_ranking");

        var why = new List<string>();
        if (frameEventDataSuccess == 0 && total > 0) why.Add("Unity internal GetFrameEventData returned false for exported events.");
        if (directObjectMaterialShaderRows == 0 && total > 0) why.Add("Frame Debugger direct GameObject/Renderer/Material/Shader rows are empty.");
        if (meshMissingDetail > 0) why.Add(meshMissingDetail.ToString(CultureInfo.InvariantCulture) + " mesh-expected events lack detail mesh names or mesh instance ids.");
        if (dynamicGeometryMissing > 0) why.Add(dynamicGeometryMissing.ToString(CultureInfo.InvariantCulture) + " DynamicGeometry events lack direct renderer/detail mesh attribution.");
        if (srpBatchMissing > 0) why.Add(srpBatchMissing.ToString(CultureInfo.InvariantCulture) + " SRPBatch events lack detail mesh expansion.");
        if (!hasDeepEventFiles && hasRenderDocIndex) why.Add("DeepEvent files are produced after export by preflight; read deep_event_preflight_status.json at report time.");
        why.Add("Frame Debugger export has no GPU/CPU timing counters.");

        var currentCaptureGaps = new List<string>();
        if (frameEventDataSuccess == 0) currentCaptureGaps.Add("unity_internal_frameeventdata_unavailable_in_this_capture");
        if (meshMissingDetail > 0) currentCaptureGaps.Add("some_mesh_expected_events_lack_detail_mesh_names_or_mesh_instance_ids");
        if (directObjectMaterialShaderRows == 0) currentCaptureGaps.Add("direct_object_material_shader_owner_columns_unavailable_in_frame_debugger_rows");
        if (!hasDeepEventFiles && hasRenderDocIndex) currentCaptureGaps.Add("deep_event_preflight_must_finish_before_report_to_use_draw_level_state");

        var external = new List<string>();
        external.Add("profiler_or_gpu_counter_capture_for_timing_priority");
        external.Add("screenshot_or_pixel_history_for_visual_correctness_claims");

        string status;
        if (total == 0)
            status = "insufficient_no_frame_events";
        else if (directObjectMaterialShaderRows == 0 && detailMeshRows == 0)
            status = "enough_for_stage_pass_structure_not_object_ownership";
        else
            status = "usable_with_attribution_limits";

        string pad = new string(' ', indent);
        sb.Append(pad).Append("\"").Append(EscapeJson(name)).AppendLine("\": {");
        WriteJsonProperty(sb, "status", status, true, indent + 2);
        WriteJsonProperty(sb, "singleCaptureMode", true, true, indent + 2);
        WriteJsonProperty(sb, "mustNotRequestAnotherFrameDebuggerExport", true, true, indent + 2);
        WriteJsonProperty(sb, "currentExportEnoughForObjectLevelOptimization", directObjectMaterialShaderRows > 0 || detailMeshRows > 0, true, indent + 2);
        WriteJsonProperty(sb, "currentExportEnoughForStagePassStructure", total > 0 && hasRenderDocIndex, true, indent + 2);
        WriteJsonProperty(sb, "currentExportIncludesObjectInventory", BuildSceneRendererRecords().Count > 0 || BuildUiGraphicRecords().Count > 0 || GetActiveParticleCandidates().Count > 0, true, indent + 2);
        WriteJsonProperty(sb, "currentExportIncludesEventAttributionIndex", true, true, indent + 2);
        WriteJsonProperty(sb, "reExportCanImproveAttribution", false, true, indent + 2);
        WriteJsonProperty(sb, "externalProfilerNeededForTiming", true, true, indent + 2);
        AppendStringArray(sb, "enoughFor", enoughFor.ToArray(), true, indent + 2);
        AppendStringArray(sb, "notEnoughFor", notEnoughFor.ToArray(), true, indent + 2);
        AppendStringArray(sb, "whyNotEnough", why.ToArray(), true, indent + 2);
        AppendStringArray(sb, "currentCaptureGaps", currentCaptureGaps.ToArray(), true, indent + 2);
        AppendStringArray(sb, "externalDataRequirements", external.ToArray(), true, indent + 2);
        WriteJsonProperty(sb, "reportRule", "Use ai_object_inventory.json and ai_event_attribution_index.jsonl before declaring ownership missing. If an optimization action mainly depends on currentCaptureGaps or externalDataRequirements, do not present it as P1; place the limitation under 仍需补充的数据 and do not request another Frame Debugger export.", false, indent + 2);
        sb.Append(pad).Append("}");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static string[] GetFrameDebuggerCannotConclude()
    {
        return new[]
        {
            "GPU耗时排序",
            "CPU耗时排序",
            "带宽成本",
            "Overdraw实际面积",
            "Shader ALU/Texture采样成本",
            "RenderFeature临时RT分配耗时"
        };
    }

    private static string GetEvidenceConfidence(FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates)
    {
        if (HasDirectAttribution(evt)) return "direct";
        if (uiBatch != null && uiBatch.graphics.Count > 0) return "inferred_high";
        if (srpBatch != null)
            return srpBatch.confidence;
        if (particleCandidates != null && particleCandidates.Count > 0)
        {
            if (particleCandidates.Any(c => c.confidence == "direct")) return "direct";
            if (particleCandidates.Any(c => c.confidence == "inferred_high")) return "inferred_high";
            if (particleCandidates.Any(c => c.confidence == "inferred_medium")) return "inferred_medium";
            return "inferred_low";
        }
        string category = ClassifyUnresolvedEvent(evt);
        if (!string.IsNullOrEmpty(category))
            return GetUnresolvedCategoryConfidence(category);
        if (evt != null && IsSceneSnapshotRelevantStage(evt)) return "snapshot_only";
        return "data_gap";
    }

    private static bool HasDirectAttribution(FrameEventInfo evt)
    {
        return evt != null &&
               (!string.IsNullOrEmpty(evt.gameObjectName) || !string.IsNullOrEmpty(evt.gameObjectPath)) &&
               (!string.IsNullOrEmpty(evt.materialName) || !string.IsNullOrEmpty(BestShaderName(evt)));
    }

    private static bool IsSceneSnapshotRelevantStage(FrameEventInfo evt)
    {
        string stage = ParseStageName(evt != null ? evt.eventName : "");
        return stage == "UI" || stage == "DrawTransparentObjects" || stage == "PostProcessing" || stage.Contains("RenderFeature");
    }

    private static string BuildConfidenceReason(FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates)
    {
        if (HasDirectAttribution(evt))
            return "Frame Debugger event row resolved object/material/shader evidence.";
        if (uiBatch != null && uiBatch.graphics.Count > 0)
            return "Canvas.RenderSubBatch mapped to runtime Graphic batch by camera sequence, canvas order, material, shader, texture, and mask state.";
        if (srpBatch != null)
            return srpBatch.reason;
        if (particleCandidates != null && particleCandidates.Count > 0)
        {
            if (particleCandidates.Any(c => c.confidence == "direct"))
                return "Frame event matched active ParticleSystem by direct FrameDebugger renderer evidence.";
            return "Frame event matched active ParticleSystem candidates by material/shader/path heuristic.";
        }
        if (evt != null && IsSceneSnapshotRelevantStage(evt))
            return "Only scene snapshot data is available for this event category.";
        return "No object/material/shader/candidate attribution available.";
    }

    private static string ClassifyUnresolvedEvent(FrameEventInfo evt)
    {
        if (evt == null) return "Reflection/API unavailable";
        if (!IsUnresolvedEvent(evt)) return "";

        string name = evt.eventName ?? "";
        string type = evt.typeName ?? "";
        string normalizedName = name.ToLowerInvariant();
        string normalizedType = type.ToLowerInvariant();
        string stage = ParseStageName(name);
        string camera = ParseCameraName(name);
        if (normalizedName.Contains("ugui.rendering.renderoverlays") || normalizedName.Contains("canvas.renderoverlays"))
            return "UI.RenderOverlays";
        if (normalizedName.Contains("canvas.rendersubbatch")) return "Canvas.RenderSubBatch";
        if (normalizedType.Contains("srpbatch") || normalizedName.Contains("srpbatch")) return "SRPBatch";
        if (normalizedName.Contains("clear") || normalizedName.Contains("copy") || normalizedName.Contains("depth") || normalizedName.Contains("stencil")) return "Clear/Copy/Depth";
        if (stage == "PostProcessing" || normalizedName.Contains("bloom") || normalizedName.Contains("colorgrading")) return "PostProcessing";
        if (normalizedName.Contains("colortint")) return "RenderFeature.ColorTint";
        if (normalizedName.Contains("renderfeature")) return "RenderFeature";
        if (normalizedType.Contains("dynamicgeometry") || normalizedName.Contains("draw dynamic")) return "DynamicGeometry.UnresolvedRenderer";
        if (camera.IndexOf("UICamera", StringComparison.OrdinalIgnoreCase) >= 0 && IsNonUiMeshEvent(evt)) return "UICamera.MeshWithoutGraphic";
        if ((stage == "UI" || normalizedName.Contains("ugui")) && IsNonUiMeshEvent(evt)) return "UI.UnknownMesh";
        if (normalizedName.Contains("draw mesh") || normalizedName.Contains("draw") || normalizedType.Contains("mesh")) return "Unknown Mesh";
        if (!string.IsNullOrEmpty(s_reflectionError)) return "Reflection/API unavailable";
        return "Other";
    }

    private static string ClassifyUnresolvedEvent(FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates)
    {
        if (!IsAnalysisUnresolvedEvent(evt, uiBatch, srpBatch, particleCandidates))
            return "";
        return ClassifyUnresolvedEvent(evt);
    }

    private static bool IsAnalysisUnresolvedEvent(FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates)
    {
        if (evt == null) return true;
        if (HasDirectAttribution(evt)) return false;
        if (uiBatch != null && uiBatch.graphics != null && uiBatch.graphics.Count > 0) return false;
        if (srpBatch != null && srpBatch.renderers != null && srpBatch.renderers.Count > 0) return false;
        if (particleCandidates != null && particleCandidates.Count > 0) return false;
        return true;
    }

    private static string GetAttributionKind(FrameEventInfo evt)
    {
        if (evt == null) return "";
        string name = evt.eventName ?? "";
        if (name.IndexOf("ColorTint", StringComparison.OrdinalIgnoreCase) >= 0) return "RenderFeature.ColorTint";
        if (IsUiSubBatchEvent(evt)) return "Canvas.RenderSubBatch";
        if (name.Contains("UGUI.Rendering.RenderOverlays") || name.Contains("Canvas.RenderOverlays")) return "UI.RenderOverlays";
        if (IsSrpBatchEvent(evt)) return "SRPBatch";
        if (IsDynamicGeometryEvent(evt)) return "DynamicGeometry";
        if (IsNonUiMeshEvent(evt)) return "Mesh";
        return ParseStageName(name);
    }

    private static string GetAttributionSource(FrameEventInfo evt, UiBatchCandidate uiBatch, SrpBatchCandidate srpBatch, List<ParticleCandidate> particleCandidates)
    {
        if (HasDirectAttribution(evt)) return "frame_debugger_direct_object";
        if (uiBatch != null && uiBatch.graphics != null && uiBatch.graphics.Count > 0) return "ui_batch_candidate";
        if (srpBatch != null && srpBatch.renderers != null && srpBatch.renderers.Count > 0) return "srp_batch_candidate";
        if (particleCandidates != null && particleCandidates.Count > 0) return "particle_candidate";
        if (HasFrameDebuggerDetailMeshes(evt)) return "frame_debugger_detail_mesh";
        return "";
    }

    private static bool IsDirectObjectMissingExpected(FrameEventInfo evt)
    {
        return evt != null &&
               string.IsNullOrEmpty(evt.frameDebuggerGameObjectPath) &&
               string.IsNullOrEmpty(evt.frameDebuggerRendererPath) &&
               IsBatchedFrameDebuggerEvent(evt);
    }

    private static List<IssueEntry> BuildTopDirectDrawObjects(List<FrameEventInfo> events)
    {
        return events
            .Where(HasDirectAttribution)
            .GroupBy(e => !string.IsNullOrEmpty(e.gameObjectPath) ? e.gameObjectPath : e.gameObjectName)
            .Select(g => new IssueEntry
            {
                name = g.Key,
                count = g.Count(),
                confidence = "direct",
                evidence = string.Join(",", g.Select(e => (e.index + 1).ToString(CultureInfo.InvariantCulture)).Take(16).ToArray()),
                limitations = "Draw count only; no timing."
            })
            .OrderByDescending(e => e.count)
            .ThenBy(e => e.name)
            .Take(10)
            .ToList();
    }

    private static List<IssueEntry> BuildTopInferredUiBatches(List<FrameEventInfo> events)
    {
        return BuildUiBatchCandidateMap(events)
            .Values
            .OrderByDescending(c => c.graphics.Count)
            .ThenBy(c => c.mappedEventIndex)
            .Take(10)
            .Select(c => new IssueEntry
            {
                name = "uiBatchCandidateId:" + c.id.ToString(CultureInfo.InvariantCulture) + " " + c.canvasPath,
                count = c.graphics.Count,
                confidence = "inferred_high",
                evidence = "eventIndex:" + c.mappedEventIndex.ToString(CultureInfo.InvariantCulture),
                limitations = "Canvas.RenderSubBatch has no direct Graphic reference; mapped by runtime order heuristic."
            })
            .ToList();
    }

    private static List<IssueEntry> BuildTopActiveParticlesInFrame(List<FrameEventInfo> events)
    {
        return BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value)
            .GroupBy(c => c.id)
            .Select(g => g.First())
            .Where(c => c.matchScore >= 75 &&
                        c.confidence != "inferred_low" &&
                        (string.Equals(c.confidence, "direct", StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(c.materialName) && !string.IsNullOrEmpty(c.shaderName))))
            .OrderByDescending(c => c.aliveParticles)
            .ThenBy(c => c.path)
            .Take(10)
            .Select(c => new IssueEntry
            {
                name = "particleCandidateId:" + c.id.ToString(CultureInfo.InvariantCulture) + " " + c.path,
                count = c.aliveParticles,
                confidence = c.confidence,
                evidence = c.materialName + " / " + c.shaderName,
                limitations = c.reason
            })
            .ToList();
    }

    private static List<IssueEntry> BuildTopActiveParticleRoots(List<FrameEventInfo> events)
    {
        return BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value)
            .GroupBy(c => GetPrefabRootFromPath(c.path))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => new IssueEntry
            {
                name = g.Key,
                count = g.Sum(c => Math.Max(0, c.aliveParticles)),
                confidence = "inferred_medium",
                evidence = string.Join(",", g.Select(c => "particleCandidateId:" + c.id.ToString(CultureInfo.InvariantCulture)).Distinct().Take(16).ToArray()),
                limitations = "Active particle candidates mapped to frame events by material/shader/path heuristic; draw count and timing still require Frame Debugger/Profiler correlation."
            })
            .OrderByDescending(e => e.count)
            .ThenBy(e => e.name)
            .Take(10)
            .ToList();
    }

    private static List<IssueEntry> BuildTopMappedParticleMaterials(List<FrameEventInfo> events)
    {
        return BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value)
            .GroupBy(c => !string.IsNullOrEmpty(c.materialPath) ? c.materialPath : c.materialName)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => new IssueEntry
            {
                name = g.Key,
                count = g.Sum(c => Math.Max(0, c.aliveParticles)),
                confidence = "inferred_medium",
                evidence = string.Join(",", g.Select(c => c.path).Distinct().Take(8).ToArray()),
                limitations = "Alive particle count is a runtime snapshot; cost ranking is not timing-based."
            })
            .OrderByDescending(e => e.count)
            .ThenBy(e => e.name)
            .Take(10)
            .ToList();
    }

    private static List<IssueEntry> BuildTopUnresolvedStages(List<FrameEventInfo> events)
    {
        events = events ?? new List<FrameEventInfo>();
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        var srpBatchMap = BuildSrpBatchCandidateMap(events);
        var particleMap = BuildParticleCandidateMap(events);
        return events
            .Select(e =>
            {
                uiBatchMap.TryGetValue(e.index, out var uiBatch);
                srpBatchMap.TryGetValue(e.index, out var srpBatch);
                particleMap.TryGetValue(e.index, out var particleCandidates);
                return new { evt = e, category = ClassifyUnresolvedEvent(e, uiBatch, srpBatch, particleCandidates) };
            })
            .Where(x => !string.IsNullOrEmpty(x.category))
            .GroupBy(x => x.category)
            .Select(g => new IssueEntry
            {
                name = g.Key,
                count = g.Count(),
                confidence = GetUnresolvedCategoryConfidence(g.Key),
                evidence = string.Join(",", g.Select(x => (x.evt.index + 1).ToString(CultureInfo.InvariantCulture)).Take(16).ToArray()),
                limitations = GetUnresolvedCategoryLimitations(g.Key)
            })
            .OrderByDescending(e => e.count)
            .ThenBy(e => e.name)
            .ToList();
    }

    private static string GetUnresolvedCategoryConfidence(string category)
    {
        if (category == "Clear/Copy/Depth") return "expected_missing";
        if (category == "Canvas.RenderSubBatch" || category == "UI.RenderOverlays") return "expected_missing";
        if (category == "RenderFeature.ColorTint") return "render_feature_stage_only";
        if (category == "PostProcessing" || category == "RenderFeature") return "stage_only";
        return "data_gap";
    }

    private static string GetUnresolvedCategoryLimitations(string category)
    {
        if (category == "Clear/Copy/Depth")
            return "Clear/copy/depth/stencil events normally do not expose object attribution; do not treat as lost renderer evidence.";
        if (category == "UICamera.MeshWithoutGraphic" || category == "UI.UnknownMesh")
            return "UICamera mesh event lacks direct Graphic/UI batch attribution; treat as UI-specific unresolved mesh, not ordinary scene Mesh.";
        if (category == "RenderFeature.ColorTint")
            return "ColorTint render feature event lacks a direct renderer/object; treat as feature-level attribution unless a RenderFeature candidate is present.";
        if (category == "DynamicGeometry.UnresolvedRenderer")
            return "DynamicGeometry event lacks direct renderer/detail mesh evidence; particle or generated-geometry attribution may need candidate matching.";
        return "No direct object/material/shader/candidate attribution.";
    }

    private static List<FrameEventInfo> BuildAnalysisUnresolvedEvents(List<FrameEventInfo> events)
    {
        events = events ?? new List<FrameEventInfo>();
        var uiBatchMap = BuildUiBatchCandidateMap(events);
        var srpBatchMap = BuildSrpBatchCandidateMap(events);
        var particleMap = BuildParticleCandidateMap(events);
        return events
            .Where(e =>
            {
                uiBatchMap.TryGetValue(e.index, out var uiBatch);
                srpBatchMap.TryGetValue(e.index, out var srpBatch);
                particleMap.TryGetValue(e.index, out var particleCandidates);
                return IsAnalysisUnresolvedEvent(e, uiBatch, srpBatch, particleCandidates);
            })
            .ToList();
    }

    private static List<IssueEntry> BuildTopDataGaps(List<FrameEventInfo> events)
    {
        var rows = new List<IssueEntry>();
        int total = events != null ? events.Count : 0;
        int noDirect = events != null ? events.Count(e => !HasDirectAttribution(e)) : 0;
        int noMaterial = events != null ? events.Count(e => string.IsNullOrEmpty(e.materialName)) : 0;
        int noShader = events != null ? events.Count(e => string.IsNullOrEmpty(BestShaderName(e))) : 0;
        int meshExpectedEvents = events != null ? events.Count(IsMeshDetailExpectedEvent) : 0;
        int meshExpectedButMissingDetailMeshes = events != null ? events.Count(e => IsMeshDetailExpectedEvent(e) && !HasFrameDebuggerDetailMeshes(e)) : 0;
        int meshNotExpectedEvents = events != null ? events.Count(e => !IsMeshDetailExpectedEvent(e)) : 0;
        int srpBatchMeshDetailMissing = events != null ? events.Count(e => IsSrpBatchEvent(e) && !HasFrameDebuggerDetailMeshes(e)) : 0;
        int ordinaryMeshExpectedButMissingDetailMeshes = events != null ? events.Count(e => IsOrdinaryMeshEvent(e) && !HasFrameDebuggerDetailMeshes(e) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        var uiBatchMap = events != null ? BuildUiBatchCandidateMap(events) : new Dictionary<int, UiBatchCandidate>();
        var srpBatchMap = events != null ? BuildSrpBatchCandidateMap(events) : new Dictionary<int, SrpBatchCandidate>();
        var particleMap = events != null ? BuildParticleCandidateMap(events) : new Dictionary<int, List<ParticleCandidate>>();
        int uiMeshExpectedButNoGraphic = events != null ? events.Count(e => IsUiMeshAttributionEvent(e) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) && !uiBatchMap.ContainsKey(e.index)) : 0;
        int dynamicGeometryMissingRenderer = events != null ? events.Count(e => IsDynamicGeometryEvent(e) && string.IsNullOrEmpty(e.frameDebuggerRendererPath) && !HasFrameDebuggerDetailMeshes(e)) : 0;
        int frameEventDataFailed = events != null ? events.Count(e => e.frameEventDataAttempted && !e.frameEventDataSuccess) : 0;
        int uiSubBatch = events != null ? events.Count(IsUiSubBatchEvent) : 0;
        int uiSubBatchWithoutFrameDebuggerGraphic = events != null ? events.Count(e => IsUiSubBatchEvent(e) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int uiSubBatchUnresolved = events != null ? events.Count(e => IsUiSubBatchEvent(e) && IsAnalysisUnresolvedEvent(e, uiBatchMap.ContainsKey(e.index) ? uiBatchMap[e.index] : null, srpBatchMap.ContainsKey(e.index) ? srpBatchMap[e.index] : null, particleMap.ContainsKey(e.index) ? particleMap[e.index] : null)) : 0;
        int batchedEventsWithoutDirectObject = events != null ? events.Count(e => IsBatchedFrameDebuggerEvent(e) && string.IsNullOrEmpty(e.frameDebuggerGameObjectPath) && string.IsNullOrEmpty(e.frameDebuggerRendererPath)) : 0;
        int rendererFingerprintCount = BuildSceneRendererRecords().Count(r => !string.IsNullOrEmpty(r.resourceFingerprint));
        int uiFingerprintCount = BuildUiGraphicRecords().Count(g => !string.IsNullOrEmpty(g.resourceFingerprint));
        int particleFingerprintCount = GetActiveParticleCandidates().Count(p => !string.IsNullOrEmpty(p.resourceFingerprint));

        rows.Add(new IssueEntry { name = "eventsWithoutDirectAttribution", count = noDirect, confidence = "summary_gap", evidence = noDirect.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " events lack direct object/material/shader attribution", limitations = "Summary only. Many batched/UI/clear/postprocess events are expected to lack direct objects; prefer directAttributionRateExcludingBatched, uiCandidateCoverageRate, srpMeshCoverageRate, and split mesh gaps." });
        rows.Add(new IssueEntry { name = "resourceFingerprintsWeak", count = rendererFingerprintCount + uiFingerprintCount + particleFingerprintCount == 0 ? total : 0, confidence = "data_gap", evidence = "renderer/ui/particle resource fingerprints are empty", limitations = "Object-to-RenderDoc matching should stay at stage/pass level until ai_resource_fingerprints.json has renderer/UI/particle rows." });
        rows.Add(new IssueEntry { name = "getFrameEventDataFailed", count = frameEventDataFailed, confidence = "data_gap", evidence = frameEventDataFailed.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " events failed Unity internal GetFrameEventData reflection; see ai_frameeventdata_diagnostics.json", limitations = "Internal FrameDebuggerEventData detail fields may be unavailable for most events." });
        rows.Add(new IssueEntry { name = "eventsWithoutMaterial", count = noMaterial, confidence = "summary_gap", evidence = noMaterial.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " events have empty materialName", limitations = "Summary only. Do not treat as a production issue without checking event kind and candidate coverage." });
        rows.Add(new IssueEntry { name = "eventsWithoutShader", count = noShader, confidence = "summary_gap", evidence = noShader.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " events have empty shaderName", limitations = "Summary only. Prefer attributed event rows and candidate coverage metrics." });
        rows.Add(new IssueEntry { name = "meshExpectedButMissingDetailMeshes", count = meshExpectedButMissingDetailMeshes, confidence = "summary_gap", evidence = meshExpectedButMissingDetailMeshes.ToString(CultureInfo.InvariantCulture) + "/" + meshExpectedEvents.ToString(CultureInfo.InvariantCulture) + " mesh-expected events lack FrameDebuggerEventData detail mesh names or mesh instance ids", limitations = "Summary only; use srp/ordinary/dynamic/ui split rows before drawing conclusions." });
        rows.Add(new IssueEntry { name = "srpBatchMeshDetailMissing", count = srpBatchMeshDetailMissing, confidence = "data_gap", evidence = srpBatchMeshDetailMissing.ToString(CultureInfo.InvariantCulture) + " SRPBatch events lack FrameDebuggerEventData detail mesh names or mesh instance ids", limitations = "SRP batch expansion cannot be proven without detail mesh fields." });
        rows.Add(new IssueEntry { name = "ordinaryMeshExpectedButMissingDetailMeshes", count = ordinaryMeshExpectedButMissingDetailMeshes, confidence = "data_gap", evidence = ordinaryMeshExpectedButMissingDetailMeshes.ToString(CultureInfo.InvariantCulture) + " ordinary Mesh events lack both detail mesh and direct renderer evidence", limitations = "Ordinary mesh attribution may need direct FrameDebugger renderer or scene renderer matching." });
        rows.Add(new IssueEntry { name = "uiMeshExpectedButNoGraphic", count = uiMeshExpectedButNoGraphic, confidence = "data_gap", evidence = uiMeshExpectedButNoGraphic.ToString(CultureInfo.InvariantCulture) + " UI mesh/overlay events lack direct Graphic/GameObject evidence", limitations = "Use UI batch candidates or UI Details Profiler data before treating as unresolved." });
        rows.Add(new IssueEntry { name = "dynamicGeometryMissingRenderer", count = dynamicGeometryMissingRenderer, confidence = "data_gap", evidence = dynamicGeometryMissingRenderer.ToString(CultureInfo.InvariantCulture) + " DynamicGeometry events lack direct renderer/detail mesh evidence", limitations = "Dynamic particle/geometry attribution may need direct renderer or material/path candidate evidence." });
        rows.Add(new IssueEntry { name = "meshNotExpectedEvents", count = meshNotExpectedEvents, confidence = "expected_missing", evidence = meshNotExpectedEvents.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " events are clear/postprocess/UI/renderfeature or otherwise not expected to expose mesh detail", limitations = "Do not treat this as missing renderer evidence." });
        rows.Add(new IssueEntry { name = "uiSubBatchWithoutFrameDebuggerGraphic", count = uiSubBatchWithoutFrameDebuggerGraphic, confidence = "expected_missing", evidence = uiSubBatchWithoutFrameDebuggerGraphic.ToString(CultureInfo.InvariantCulture) + "/" + uiSubBatch.ToString(CultureInfo.InvariantCulture) + " Canvas.RenderSubBatch events lack a direct FrameDebugger Graphic/GameObject reference", limitations = "Batched UI events often have no single direct Graphic; use candidate coverage instead." });
        rows.Add(new IssueEntry { name = "uiSubBatchUnresolved", count = uiSubBatchUnresolved, confidence = "data_gap", evidence = uiSubBatchUnresolved.ToString(CultureInfo.InvariantCulture) + "/" + uiSubBatch.ToString(CultureInfo.InvariantCulture) + " Canvas.RenderSubBatch events remain unresolved after candidate attribution", limitations = "Needs UI Details Profiler batch GameObject data or stronger UI batch mapping." });
        rows.Add(new IssueEntry { name = "batchedDirectObjectMissExpected", count = batchedEventsWithoutDirectObject, confidence = "expected_missing", evidence = batchedEventsWithoutDirectObject.ToString(CultureInfo.InvariantCulture) + " batched events have no single FrameDebugger direct object", limitations = "Do not include batched direct misses in ordinary direct-object regression claims." });
        rows.Add(new IssueEntry { name = "timingUnavailable", count = total, confidence = "data_gap", evidence = "timingAvailable=false", limitations = "Cannot rank CPU/GPU/bandwidth cost." });

        return rows
            .Where(r => r.count > 0)
            .OrderBy(r => r.confidence == "expected_missing" ? 2 : r.confidence == "summary_gap" ? 1 : 0)
            .ThenByDescending(r => r.count)
            .ThenBy(r => r.name)
            .ToList();
    }

    private static List<string> BuildEmptyOrWeakFieldList(List<FrameEventInfo> events)
    {
        var rows = new List<string>();
        if (events == null || events.Count == 0)
        {
            rows.Add("No events captured.");
            return rows;
        }

        if (events.Count(e => !string.IsNullOrEmpty(e.materialName)) < events.Count / 2)
            rows.Add("materialName coverage below 50%.");
        if (events.Count(e => !string.IsNullOrEmpty(BestShaderName(e))) < events.Count / 2)
            rows.Add("shaderName coverage below 50%.");
        if (events.Count(e => !string.IsNullOrEmpty(e.gameObjectPath)) < events.Count / 2)
            rows.Add("gameObjectPath coverage below 50%.");
        if (events.Count(e => !string.IsNullOrEmpty(e.batchBreakCause)) == 0)
            rows.Add("batchBreakCause is empty for all events.");
        rows.Add("Timing fields are unavailable by design in Frame Debugger export.");
        return rows;
    }

    private static string BuildCameraStageKey(FrameEventInfo evt)
    {
        return JoinKey(ParseCameraName(evt != null ? evt.eventName : ""), ParseStageName(evt != null ? evt.eventName : ""));
    }

    private static string JoinKey(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return "";
        return a + " / " + b;
    }

    private static bool IsUiSubBatchEvent(FrameEventInfo evt)
    {
        return evt != null && !string.IsNullOrEmpty(evt.eventName) && evt.eventName.Contains("Canvas.RenderSubBatch");
    }

    private static bool IsUnresolvedEvent(FrameEventInfo evt)
    {
        return evt == null ||
               (string.IsNullOrEmpty(evt.gameObjectName) &&
                string.IsNullOrEmpty(evt.materialName) &&
                string.IsNullOrEmpty(BestShaderName(evt)));
    }

    private static string GetAttributionLevel(FrameEventInfo evt)
    {
        if (evt == null) return "none";
        bool hasObject = !string.IsNullOrEmpty(evt.gameObjectName) || !string.IsNullOrEmpty(evt.gameObjectPath);
        bool hasMaterial = !string.IsNullOrEmpty(evt.materialName);
        bool hasShader = !string.IsNullOrEmpty(BestShaderName(evt));
        if (hasObject && hasMaterial && hasShader) return "object+material+shader";
        if (hasMaterial && hasShader) return "material+shader";
        if (hasObject) return "object";
        if (hasShader) return "shader";
        return "none";
    }

    private static string GetPrefabRootFromPath(string hierarchyPath)
    {
        if (string.IsNullOrEmpty(hierarchyPath)) return "";
        string[] parts = hierarchyPath.Split('/');
        if (parts.Length == 0) return "";
        if (parts.Length == 1) return parts[0];
        return parts[0] + "/" + parts[1];
    }

    private static string ExtractRenderStateForEvent(FrameEventInfo evt)
    {
        if (evt == null || string.IsNullOrEmpty(evt.resolvedShaderPassSummary)) return "";
        string passName = BestPassName(evt);
        string[] passes = evt.resolvedShaderPassSummary.Split('|');
        string fallback = passes.Select(p => p.Trim()).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
        if (string.IsNullOrEmpty(passName)) return fallback;
        return passes.Select(p => p.Trim()).FirstOrDefault(p => p.IndexOf(passName, StringComparison.OrdinalIgnoreCase) >= 0) ?? fallback;
    }

    private static IEnumerable<UnityEngine.Object> FindSceneObjects(Type type)
    {
        if (type == null) yield break;
        UnityEngine.Object[] objects;
        try { objects = Resources.FindObjectsOfTypeAll(type); }
        catch { yield break; }

        foreach (var obj in objects)
        {
            if (obj == null || EditorUtility.IsPersistent(obj)) continue;
            var go = obj as GameObject;
            if (go == null && obj is Component component)
                go = component.gameObject;
            if (go == null || !go.scene.IsValid()) continue;
            yield return obj;
        }
    }

    private static ExportBuildCache GetExportCache(List<FrameEventInfo> events)
    {
        if (s_exportCache == null)
            return new ExportBuildCache { events = events };
        if (!ReferenceEquals(s_exportCache.events, events))
            s_exportCache = new ExportBuildCache { events = events };
        return s_exportCache;
    }

    private static RuntimePlayerSnapshot GetRuntimePlayerSnapshot()
    {
        if (s_exportCache == null)
            return new RuntimePlayerSnapshot { status = "no_export_cache" };
        if (s_exportCache.runtimeSnapshotParseAttempted)
            return s_exportCache.runtimeSnapshot ?? new RuntimePlayerSnapshot { status = "parse_failed" };

        s_exportCache.runtimeSnapshotParseAttempted = true;
        s_exportCache.runtimeSnapshot = ParseRuntimePlayerSnapshot(s_exportCache.runtimeSnapshotJson);
        return s_exportCache.runtimeSnapshot;
    }

    private static RuntimePlayerSnapshot ParseRuntimePlayerSnapshot(string json)
    {
        var snapshot = new RuntimePlayerSnapshot();
        if (string.IsNullOrWhiteSpace(json))
        {
            snapshot.status = "empty";
            return snapshot;
        }

        try
        {
            JObject root = JObject.Parse(json);
            snapshot.success = TokenBool(root["success"]);
            snapshot.status = snapshot.success ? "received" : TokenString(root["status"]);
            snapshot.error = TokenString(root["error"]);
            snapshot.frameCount = TokenInt(root["frameCount"], 0);
            snapshot.scene = TokenString(root["scene"]);

            foreach (var token in (root["cameras"] as JArray) ?? new JArray())
            {
                var obj = token as JObject;
                if (obj == null) continue;
                snapshot.cameras.Add(new RuntimeCameraRecord
                {
                    path = TokenString(obj["path"]),
                    name = TokenString(obj["name"]),
                    enabled = TokenBool(obj["enabled"]),
                    activeInHierarchy = TokenBool(obj["activeInHierarchy"]),
                    depth = TokenFloat(obj["depth"], 0),
                    clearFlags = TokenString(obj["clearFlags"]),
                    cullingMask = TokenInt(obj["cullingMask"], -1),
                    cullingMaskNames = TokenString(obj["cullingMaskNames"]),
                    orthographic = TokenBool(obj["orthographic"]),
                    orthographicSize = TokenFloat(obj["orthographicSize"], 0),
                    fieldOfView = TokenFloat(obj["fieldOfView"], 0),
                    nearClipPlane = TokenFloat(obj["nearClipPlane"], 0),
                    farClipPlane = TokenFloat(obj["farClipPlane"], 0),
                    allowHDR = TokenBool(obj["allowHDR"]),
                    allowMSAA = TokenBool(obj["allowMSAA"]),
                    pixelWidth = TokenInt(obj["pixelWidth"], 0),
                    pixelHeight = TokenInt(obj["pixelHeight"], 0),
                    scaledPixelWidth = TokenInt(obj["scaledPixelWidth"], 0),
                    scaledPixelHeight = TokenInt(obj["scaledPixelHeight"], 0),
                    aspect = TokenFloat(obj["aspect"], 0),
                    rect = TokenString(obj["rect"]),
                    cameraType = TokenString(obj["cameraType"]),
                    actualRenderingPath = TokenString(obj["actualRenderingPath"]),
                    renderingPath = TokenString(obj["renderingPath"]),
                    useOcclusionCulling = TokenBool(obj["useOcclusionCulling"]),
                    targetTexture = TokenString(obj["targetTexture"]),
                    targetTextureMeta = TokenString(obj["targetTextureMeta"]),
                    pixelRect = TokenString(obj["pixelRect"]),
                    urpAdditionalData = TokenString(obj["urpAdditionalData"])
                });
            }

            foreach (var token in (root["renderers"] as JArray) ?? new JArray())
            {
                var obj = token as JObject;
                if (obj == null) continue;
                MaterialParts parts = ParseMaterialParts(TokenString(obj["material"]), TokenString(obj["materialName"]), TokenString(obj["shaderName"]), TokenInt(obj["renderQueue"], -1));
                string path = TokenString(obj["path"]);
                if (string.IsNullOrEmpty(path)) continue;
                snapshot.renderers.Add(new RendererRecord
                {
                    source = "runtime_player_snapshot",
                    path = path,
                    name = !string.IsNullOrEmpty(TokenString(obj["name"])) ? TokenString(obj["name"]) : LastPathSegment(path),
                    rendererType = TokenString(obj["type"]),
                    layer = TokenInt(obj["layer"], 0),
                    active = TokenBool(obj["activeInHierarchy"], true),
                    enabled = TokenBool(obj["enabled"], true),
                    instanceId = TokenInt(obj["instanceId"], 0),
                    meshName = TokenString(obj["meshName"]),
                    meshInstanceId = TokenInt(obj["meshInstanceId"], 0),
                    subMeshIndex = 0,
                    materialName = parts.materialName,
                    materialInstanceId = TokenInt(obj["materialInstanceId"], 0),
                    shaderName = parts.shaderName,
                    renderQueue = parts.renderQueue,
                    textureName = FirstToken(TokenString(obj["textureName"]), TextureNameFromSummary(TokenString(obj["mainTexture"]))),
                    textureMeta = TokenString(obj["mainTexture"]),
                    materialTextures = TokenString(obj["materialTextures"]),
                    bounds = TokenString(obj["bounds"]),
                    materialFingerprint = TokenString(obj["materialFingerprint"]),
                    resourceFingerprint = BuildRuntimeRendererFingerprint(path, TokenString(obj["meshName"]), parts.materialName, parts.shaderName, FirstToken(TokenString(obj["textureName"]), TextureNameFromSummary(TokenString(obj["mainTexture"]))), parts.renderQueue),
                    sortingGroup = TokenString(obj["sortingGroup"]),
                    sortingLayerId = TokenInt(obj["sortingLayerId"], 0),
                    sortingOrder = TokenInt(obj["sortingOrder"], 0),
                    prefabRoot = GetPrefabRootFromPath(path)
                });
            }

            foreach (var token in (root["uiGraphics"] as JArray) ?? new JArray())
            {
                var obj = token as JObject;
                if (obj == null) continue;
                MaterialParts parts = ParseMaterialParts(TokenString(obj["material"]), TokenString(obj["materialName"]), TokenString(obj["shaderName"]), TokenInt(obj["renderQueue"], -1));
                string path = TokenString(obj["path"]);
                if (string.IsNullOrEmpty(path)) continue;
                var record = new UiGraphicRecord
                {
                    source = "runtime_player_snapshot",
                    path = path,
                    type = TokenString(obj["type"]),
                    canvasPath = TokenString(obj["canvasPath"]),
                    rootCanvasPath = TokenString(obj["rootCanvasPath"]),
                    canvasCamera = TokenString(obj["canvasCamera"]),
                    canvasRenderMode = TokenString(obj["canvasRenderMode"]),
                    canvasSortingLayerId = TokenInt(obj["canvasSortingLayerId"], 0),
                    canvasSortingOrder = TokenInt(obj["canvasSortingOrder"], 0),
                    canvasScaleFactor = TokenFloat(obj["canvasScaleFactor"], 0),
                    canvasReferencePixelsPerUnit = TokenFloat(obj["canvasReferencePixelsPerUnit"], 0),
                    depth = TokenInt(obj["depth"], -1),
                    active = TokenBool(obj["activeInHierarchy"], true),
                    enabled = TokenBool(obj["enabled"], true),
                    canvasEnabled = TokenBool(obj["canvasEnabled"], true),
                    raycastTarget = TokenBool(obj["raycastTarget"]),
                    materialName = parts.materialName,
                    shaderName = parts.shaderName,
                    textureName = FirstToken(TokenString(obj["textureName"]), TextureNameFromSummary(TokenString(obj["mainTexture"]))),
                    textureMeta = TokenString(obj["textureMeta"]),
                    materialTextures = TokenString(obj["materialTextures"]),
                    spriteName = TokenString(obj["spriteName"]),
                    rect = TokenString(obj["rect"]),
                    screenRect = TokenString(obj["screenRect"]),
                    isTextComponent = TokenBool(obj["isTextComponent"]),
                    textLength = TokenInt(obj["textLength"], 0),
                    fontName = TokenString(obj["fontName"]),
                    fontMaterialName = FirstToken(TokenString(obj["fontMaterialName"]), TokenString(obj["fontSharedMaterialName"])),
                    materialFingerprint = TokenString(obj["materialFingerprint"]),
                    sortingGroup = TokenString(obj["sortingGroup"]),
                    maskState = "runtime_snapshot"
                };
                string runtimeMaskState = TokenString(obj["maskState"]);
                if (!string.IsNullOrEmpty(runtimeMaskState))
                    record.maskState = runtimeMaskState;
                record.canvasRenderer = TokenString(obj["canvasRenderer"]);
                record.resourceFingerprint = BuildRuntimeUiFingerprint(record);
                record.batchKey = BuildUiBatchKey(record);
                snapshot.uiGraphics.Add(record);
            }

            foreach (var token in (root["particles"] as JArray) ?? new JArray())
            {
                var obj = token as JObject;
                if (obj == null) continue;
                MaterialParts parts = ParseMaterialParts(TokenString(obj["material"]), TokenString(obj["materialName"]), TokenString(obj["shaderName"]), TokenInt(obj["renderQueue"], -1));
                string path = TokenString(obj["path"]);
                if (string.IsNullOrEmpty(path)) continue;
                snapshot.particles.Add(new ParticleCandidate
                {
                    source = "runtime_player_snapshot",
                    id = StablePositiveHash(path),
                    path = path,
                    materialName = parts.materialName,
                    shaderName = parts.shaderName,
                    aliveParticles = TokenInt(obj["aliveParticles"], 0),
                    maxParticles = TokenInt(obj["maxParticles"], 0),
                    rendererEnabled = TokenBool(obj["rendererEnabled"], true),
                    rendererMode = TokenString(obj["rendererMode"]),
                    sortingLayerId = TokenInt(obj["sortingLayerId"], 0),
                    sortingOrder = TokenInt(obj["sortingOrder"], 0),
                    textureName = FirstToken(TokenString(obj["textureName"]), TextureNameFromSummary(TokenString(obj["mainTexture"]))),
                    textureMeta = TokenString(obj["mainTexture"]),
                    materialTextures = TokenString(obj["materialTextures"]),
                    bounds = TokenString(obj["bounds"]),
                    materialFingerprint = TokenString(obj["materialFingerprint"]),
                    resourceFingerprint = BuildRuntimeRendererFingerprint(path, "", parts.materialName, parts.shaderName, FirstToken(TokenString(obj["textureName"]), TextureNameFromSummary(TokenString(obj["mainTexture"]))), parts.renderQueue),
                    sortingGroup = TokenString(obj["sortingGroup"]),
                    confidence = "snapshot_only",
                    reason = "Remote Player runtime ParticleSystem snapshot."
                });
            }

            foreach (var token in (root["textures"] as JArray) ?? new JArray())
            {
                var obj = token as JObject;
                if (obj == null) continue;
                snapshot.textures.Add(new RuntimeTextureRecord
                {
                    name = TokenString(obj["name"]),
                    type = TokenString(obj["type"]),
                    instanceId = TokenInt(obj["instanceId"], 0),
                    width = TokenInt(obj["width"], 0),
                    height = TokenInt(obj["height"], 0),
                    dimension = TokenString(obj["dimension"]),
                    format = TokenString(obj["format"]),
                    mipMapCount = TokenInt(obj["mipMapCount"], 0),
                    antiAliasing = TokenInt(obj["antiAliasing"], 0),
                    depth = TokenInt(obj["depth"], 0),
                    memoryBytes = TokenInt(obj["memoryBytes"], 0)
                });
            }

            foreach (var token in (root["textureThumbnails"] as JArray) ?? new JArray())
            {
                var obj = token as JObject;
                if (obj == null) continue;
                snapshot.textureThumbnails.Add(new RuntimeTextureThumbnailRecord
                {
                    name = TokenString(obj["name"]),
                    type = TokenString(obj["type"]),
                    instanceId = TokenInt(obj["instanceId"], 0),
                    width = TokenInt(obj["width"], 0),
                    height = TokenInt(obj["height"], 0),
                    thumbnailWidth = TokenInt(obj["thumbnailWidth"], 0),
                    thumbnailHeight = TokenInt(obj["thumbnailHeight"], 0),
                    pngBase64 = TokenString(obj["pngBase64"])
                });
            }

            snapshot.available = snapshot.success && (snapshot.cameras.Count > 0 || snapshot.renderers.Count > 0 || snapshot.uiGraphics.Count > 0 || snapshot.particles.Count > 0 || snapshot.textures.Count > 0 || snapshot.textureThumbnails.Count > 0);
            return snapshot;
        }
        catch (Exception ex)
        {
            snapshot.status = "parse_failed";
            snapshot.error = ex.GetType().Name + ": " + ex.Message;
            return snapshot;
        }
    }

    private struct MaterialParts
    {
        public string materialName;
        public string shaderName;
        public int renderQueue;
    }

    private static MaterialParts ParseMaterialParts(string summary, string materialName, string shaderName, int renderQueue)
    {
        var parts = new MaterialParts
        {
            materialName = materialName ?? "",
            shaderName = shaderName ?? "",
            renderQueue = renderQueue
        };
        if (string.IsNullOrEmpty(summary))
            return parts;

        int open = summary.IndexOf('[');
        int close = summary.LastIndexOf(']');
        if (string.IsNullOrEmpty(parts.materialName))
            parts.materialName = open > 0 ? summary.Substring(0, open) : summary;
        if (open >= 0 && close > open)
        {
            string inner = summary.Substring(open + 1, close - open - 1);
            string[] chunks = inner.Split(';');
            if (chunks.Length > 0 && string.IsNullOrEmpty(parts.shaderName))
                parts.shaderName = chunks[0];
            foreach (string chunk in chunks)
            {
                string trimmed = chunk.Trim();
                if (trimmed.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(trimmed.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int q))
                    parts.renderQueue = q;
            }
        }
        return parts;
    }

    private static string TextureNameFromSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        int space = summary.IndexOf(' ');
        return space > 0 ? summary.Substring(0, space) : summary;
    }

    private static string FirstToken(string a, string b)
    {
        return !string.IsNullOrEmpty(a) ? a : (b ?? "");
    }

    private static string BuildRuntimeRendererFingerprint(string path, string meshName, string materialName, string shaderName, string textureName, int renderQueue)
    {
        return string.Join("|", new[]
        {
            "path=" + (path ?? ""),
            "mesh=" + (meshName ?? ""),
            "mat=" + (materialName ?? ""),
            "shader=" + (shaderName ?? ""),
            "tex=" + (textureName ?? ""),
            "queue=" + renderQueue.ToString(CultureInfo.InvariantCulture)
        });
    }

    private static string BuildRuntimeUiFingerprint(UiGraphicRecord record)
    {
        if (record == null) return "";
        return string.Join("|", new[]
        {
            "path=" + record.path,
            "canvas=" + record.canvasPath,
            "type=" + record.type,
            "mat=" + record.materialName,
            "shader=" + record.shaderName,
            "tex=" + record.textureName,
            "sprite=" + record.spriteName,
            "depth=" + record.depth.ToString(CultureInfo.InvariantCulture),
            "screenRect=" + record.screenRect
        });
    }

    private static string TokenString(JToken token)
    {
        return token != null ? token.ToString() : "";
    }

    private static int TokenInt(JToken token, int fallback)
    {
        if (token == null) return fallback;
        if (token.Type == JTokenType.Integer) return token.Value<int>();
        return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    private static float TokenFloat(JToken token, float fallback)
    {
        if (token == null) return fallback;
        if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) return token.Value<float>();
        return float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
    }

    private static bool TokenBool(JToken token, bool fallback = false)
    {
        if (token == null) return fallback;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        return bool.TryParse(token.ToString(), out bool value) ? value : fallback;
    }

    private static List<ParticleSystem> GetSceneParticleSystems()
    {
        if (s_exportCache != null)
        {
            if (s_exportCache.sceneParticleSystems == null)
            {
                s_exportCache.sceneParticleSystems = FindSceneObjects(typeof(ParticleSystem))
                    .OfType<ParticleSystem>()
                    .OrderBy(p => GetHierarchyPath(p.gameObject))
                    .ToList();
            }
            return s_exportCache.sceneParticleSystems;
        }

        return FindSceneObjects(typeof(ParticleSystem))
            .OfType<ParticleSystem>()
            .OrderBy(p => GetHierarchyPath(p.gameObject))
            .ToList();
    }

    private static List<ParticleCandidate> GetActiveParticleCandidates()
    {
        var runtime = GetRuntimePlayerSnapshot();
        if (runtime.available && runtime.particles.Count > 0)
        {
            if (s_exportCache != null)
            {
                if (s_exportCache.activeParticleCandidates == null)
                    s_exportCache.activeParticleCandidates = runtime.particles
                        .Where(p => p.aliveParticles > 0 && !string.IsNullOrEmpty(p.path))
                        .OrderByDescending(p => p.aliveParticles)
                        .ThenBy(p => p.path)
                        .ToList();
                return s_exportCache.activeParticleCandidates;
            }

            return runtime.particles
                .Where(p => p.aliveParticles > 0 && !string.IsNullOrEmpty(p.path))
                .OrderByDescending(p => p.aliveParticles)
                .ThenBy(p => p.path)
                .ToList();
        }

        if (s_exportCache != null)
        {
            if (s_exportCache.activeParticleCandidates == null)
            {
                s_exportCache.activeParticleCandidates = GetSceneParticleSystems()
                    .Where(IsActiveParticleSystemForFrame)
                    .Select(BuildParticleCandidate)
                    .Where(c => !string.IsNullOrEmpty(c.path) && c.aliveParticles > 0)
                    .ToList();
            }
            return s_exportCache.activeParticleCandidates;
        }

        return GetSceneParticleSystems()
            .Where(IsActiveParticleSystemForFrame)
            .Select(BuildParticleCandidate)
            .Where(c => !string.IsNullOrEmpty(c.path) && c.aliveParticles > 0)
            .ToList();
    }

    private static List<Camera> GetSceneCameras()
    {
        if (s_exportCache != null)
        {
            if (s_exportCache.sceneCameras == null)
            {
                s_exportCache.sceneCameras = FindSceneObjects(typeof(Camera))
                    .OfType<Camera>()
                    .OrderBy(c => c.depth)
                    .ThenBy(c => GetHierarchyPath(c.gameObject))
                    .ToList();
            }
            return s_exportCache.sceneCameras;
        }

        return FindSceneObjects(typeof(Camera))
            .OfType<Camera>()
            .OrderBy(c => c.depth)
            .ThenBy(c => GetHierarchyPath(c.gameObject))
            .ToList();
    }

    private static List<RenderFeatureSnapshot> GetRenderFeatureSnapshots()
    {
        if (s_exportCache != null)
        {
            if (s_exportCache.renderFeatureSnapshots == null)
                s_exportCache.renderFeatureSnapshots = BuildRenderFeatureSnapshotsUncached().ToList();
            return s_exportCache.renderFeatureSnapshots;
        }

        return BuildRenderFeatureSnapshotsUncached().ToList();
    }

    private static T GetComponentInParentSafe<T>(Component component) where T : Component
    {
        try { return component != null ? component.GetComponentInParent<T>(true) : null; }
        catch { return null; }
    }

    private static string GetCanvasPath(Component component)
    {
        Canvas canvas = GetComponentInParentSafe<Canvas>(component);
        return canvas != null ? GetHierarchyPath(canvas.gameObject) : "";
    }

    private static Dictionary<int, UiBatchCandidate> BuildUiBatchCandidateMap(List<FrameEventInfo> events)
    {
        var cache = GetExportCache(events);
        if (cache.uiBatchCandidateMap != null)
            return cache.uiBatchCandidateMap;
        cache.uiBatchCandidateMap = BuildUiBatchCandidateMapUncached(events);
        return cache.uiBatchCandidateMap;
    }

    private static Dictionary<int, UiBatchCandidate> BuildUiBatchCandidateMapUncached(List<FrameEventInfo> events)
    {
        var map = new Dictionary<int, UiBatchCandidate>();
        if (events == null || events.Count == 0) return map;

        var uiEvents = events.Where(IsUiSubBatchEvent).OrderBy(e => e.index).ToList();
        if (uiEvents.Count == 0) return map;

        var records = BuildUiGraphicRecords()
            .Where(r => r.active && r.enabled && r.depth >= 0)
            .OrderBy(r => r.canvasSortingLayerId)
            .ThenBy(r => r.canvasSortingOrder)
            .ThenBy(r => r.depth)
            .ThenBy(r => r.path)
            .ToList();
        if (records.Count == 0) return map;

        var cameraSequence = new Dictionary<string, int>();
        var batchesByCamera = new Dictionary<string, List<UiBatchCandidate>>();
        int nextId = 1;

        foreach (var evt in uiEvents)
        {
            string camera = ParseCameraName(evt.eventName);
            if (!cameraSequence.TryGetValue(camera, out int sequence))
                sequence = 0;

            if (!batchesByCamera.TryGetValue(camera, out var batches))
            {
                var cameraRecords = records.Where(r => CameraMatchesUiEvent(camera, r)).ToList();
                if (cameraRecords.Count == 0)
                    cameraRecords = records;
                batches = BuildUiBatchesFromRecords(cameraRecords, camera);
                batchesByCamera[camera] = batches;
            }

            UiBatchCandidate candidate = sequence < batches.Count ? CloneUiBatchCandidate(batches[sequence]) : null;
            if (candidate == null && sequence < records.Count)
            {
                candidate = BuildUiBatchesFromRecords(records, camera).ElementAtOrDefault(sequence);
                if (candidate != null)
                    candidate = CloneUiBatchCandidate(candidate);
            }

            if (candidate != null)
            {
                candidate.id = nextId++;
                candidate.mappedEventIndex = evt.index + 1;
                candidate.sequenceIndex = sequence;
                candidate.camera = camera;
                candidate.confidence = candidate.graphics.Count > 0 ? "inferred_high" : "inferred_low";
                candidate.reason = "Mapped by Canvas.RenderSubBatch order within camera plus runtime Graphic depth/material/texture/mask grouping.";
                map[evt.index] = candidate;
            }

            cameraSequence[camera] = sequence + 1;
        }

        return map;
    }

    private static Dictionary<int, List<ParticleCandidate>> BuildParticleCandidateMap(List<FrameEventInfo> events)
    {
        var cache = GetExportCache(events);
        if (cache.particleCandidateMap != null)
            return cache.particleCandidateMap;
        cache.particleCandidateMap = BuildParticleCandidateMapUncached(events);
        return cache.particleCandidateMap;
    }

    private static Dictionary<int, List<ParticleCandidate>> BuildParticleCandidateMapUncached(List<FrameEventInfo> events)
    {
        var map = new Dictionary<int, List<ParticleCandidate>>();
        if (events == null || events.Count == 0) return map;

        var particles = GetActiveParticleCandidates();
        if (particles.Count == 0) return map;

        foreach (var evt in events)
        {
            var candidates = particles
                .Select(p => ScoreParticleCandidate(p, evt))
                .Where(p => p != null && p.matchScore >= 40)
                .OrderByDescending(p => p.matchScore)
                .ThenByDescending(p => p.aliveParticles)
                .ThenBy(p => p.path)
                .Take(3)
                .ToList();
            if (candidates.Count == 0) continue;

            foreach (var candidate in candidates)
                candidate.mappedToFrameEvent = true;
            map[evt.index] = candidates;
        }

        return map;
    }

    private static Dictionary<int, SrpBatchCandidate> BuildSrpBatchCandidateMap(List<FrameEventInfo> events)
    {
        var cache = GetExportCache(events);
        if (cache.srpBatchCandidateMap != null)
            return cache.srpBatchCandidateMap;
        cache.srpBatchCandidateMap = BuildSrpBatchCandidateMapUncached(events);
        return cache.srpBatchCandidateMap;
    }

    private static Dictionary<int, SrpBatchCandidate> BuildSrpBatchCandidateMapUncached(List<FrameEventInfo> events)
    {
        var map = new Dictionary<int, SrpBatchCandidate>();
        if (events == null || events.Count == 0) return map;

        var renderers = BuildSceneRendererRecords()
            .Where(r => r.active && r.enabled && !string.IsNullOrEmpty(r.path))
            .ToList();
        if (renderers.Count == 0) return map;

        foreach (var evt in events)
        {
            if (evt == null) continue;
            bool srpBatch = string.Equals(evt.typeName, "SRPBatch", StringComparison.OrdinalIgnoreCase) ||
                            ClassifyUnresolvedEvent(evt) == "SRPBatch";
            bool hasFrameDebuggerDetails = HasFrameDebuggerDetailMeshes(evt);
            if (!srpBatch && !hasFrameDebuggerDetails) continue;

            string camera = ParseCameraName(evt.eventName);
            string stage = ParseStageName(evt.eventName);
            var scored = renderers
                .Select(r => new { renderer = r, score = ScoreRendererCandidate(r, evt, camera, stage), matchedBy = GetRendererMatchedBy(r, evt, camera, stage) })
                .Where(x => x.score >= (hasFrameDebuggerDetails ? 55 : 35))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.renderer.path)
                .Take(8)
                .ToList();
            if (scored.Count == 0) continue;

            int bestScore = scored[0].score;
            bool hasGenericMeshName = HasGenericFrameDebuggerMeshName(evt);
            var candidate = new SrpBatchCandidate
            {
                eventIndex = evt.index + 1,
                camera = camera,
                stage = stage,
                eventType = evt.typeName,
                lightMode = evt.passLightMode,
                batchCause = evt.batchBreakCause,
                matchScore = bestScore,
                matchedBy = scored.SelectMany(x => x.matchedBy).Distinct().OrderBy(v => v).ToArray(),
                eventMeshNames = GetEventMeshNames(evt),
                frameDebuggerDetailMeshNames = GetFrameDebuggerDetailMeshNames(evt),
                frameDebuggerMeshInstanceIds = evt.detailMeshInstanceIds != null ? evt.detailMeshInstanceIds.ToList() : new List<string>(),
                renderers = scored.Select(x => x.renderer).ToList(),
                hasFrameDebuggerDetailMeshes = hasFrameDebuggerDetails,
                confidence = GetSrpBatchConfidence(hasFrameDebuggerDetails, scored.Count, bestScore, hasGenericMeshName),
                reason = hasFrameDebuggerDetails
                    ? BuildSrpBatchReason(scored.Count, hasGenericMeshName, scored.Any(x => string.Equals(x.renderer.source, "runtime_player_snapshot", StringComparison.Ordinal)))
                    : (scored.Any(x => string.Equals(x.renderer.source, "runtime_player_snapshot", StringComparison.Ordinal))
                        ? "No Frame Debugger mesh details were exposed; candidate group is inferred from remote Player renderer snapshot plus stage/renderQueue visibility only."
                        : "No Frame Debugger mesh details were exposed; candidate group is inferred from camera/stage/renderQueue visibility only.")
            };
            map[evt.index] = candidate;
        }

        return map;
    }

    private static List<RendererRecord> BuildSceneRendererRecords()
    {
        if (s_exportCache != null)
        {
            if (s_exportCache.sceneRendererRecords == null)
                s_exportCache.sceneRendererRecords = BuildSceneRendererRecordsUncached();
            return s_exportCache.sceneRendererRecords;
        }

        return BuildSceneRendererRecordsUncached();
    }

    private static List<RendererRecord> BuildSceneRendererRecordsUncached()
    {
        var runtime = GetRuntimePlayerSnapshot();
        if (runtime.available && runtime.renderers.Count > 0)
            return runtime.renderers
                .Where(r => !string.IsNullOrEmpty(r.path))
                .OrderBy(r => r.path)
                .ThenBy(r => r.subMeshIndex)
                .ToList();

        var records = new List<RendererRecord>();
        foreach (var renderer in FindSceneObjects(typeof(Renderer)).OfType<Renderer>())
        {
            if (renderer == null || renderer is ParticleSystemRenderer) continue;
            Mesh mesh = GetRendererMesh(renderer);
            Material[] materials = renderer.sharedMaterials ?? new Material[0];
            int materialCount = Math.Max(1, materials.Length);
            for (int i = 0; i < materialCount; i++)
            {
                Material material = i < materials.Length ? materials[i] : null;
                Shader shader = material != null ? material.shader : null;
                Texture mainTexture = GetMaterialMainTexture(material);
                string shaderPath = shader != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(shader)) : "";
                string rendererPath = GetHierarchyPath(renderer.gameObject);
                records.Add(new RendererRecord
                {
                    path = rendererPath,
                    name = renderer.gameObject.name,
                    rendererType = renderer.GetType().Name,
                    layer = renderer.gameObject.layer,
                    active = renderer.gameObject.activeInHierarchy,
                    enabled = renderer.enabled,
                    instanceId = renderer.GetInstanceID(),
                    meshName = mesh != null ? mesh.name : "",
                    meshPath = mesh != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(mesh)) : "",
                    meshInstanceId = mesh != null ? mesh.GetInstanceID() : 0,
                    subMeshIndex = i,
                    materialName = material != null ? material.name : "",
                    materialPath = material != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(material)) : "",
                    materialInstanceId = material != null ? material.GetInstanceID() : 0,
                    shaderName = shader != null ? shader.name : "",
                    shaderPath = shaderPath,
                    shaderPassSummary = shader != null ? BuildShaderPassSummary(shader) : "",
                    renderQueue = material != null ? material.renderQueue : -1,
                    textureName = mainTexture != null ? mainTexture.name : "",
                    textureMeta = TextureSummary(mainTexture),
                    materialTextures = BuildMaterialTextureSummary(material, 12),
                    bounds = renderer.bounds.ToString(),
                    materialFingerprint = MaterialFingerprint(material),
                    resourceFingerprint = BuildRuntimeRendererFingerprint(rendererPath, mesh != null ? mesh.name : "", material != null ? material.name : "", shader != null ? shader.name : "", mainTexture != null ? mainTexture.name : "", material != null ? material.renderQueue : -1),
                    sortingGroup = SortingGroupSummary(renderer.gameObject),
                    sortingLayerId = renderer.sortingLayerID,
                    sortingOrder = renderer.sortingOrder,
                    rendererPriority = TryGetRendererPriority(renderer),
                    prefabRoot = GetPrefabRootFromPath(rendererPath)
                });
            }
        }
        return records;
    }

    private static int ScoreRendererCandidate(RendererRecord renderer, FrameEventInfo evt, string camera, string stage)
    {
        if (renderer == null || evt == null) return 0;
        int score = 0;
        var eventMeshes = GetEventMeshNames(evt);
        if (eventMeshes.Count > 0 && !string.IsNullOrEmpty(renderer.meshName) &&
            eventMeshes.Any(m => string.Equals(m, renderer.meshName, StringComparison.OrdinalIgnoreCase)))
            score += 80;

        if (!string.IsNullOrEmpty(evt.meshName) && !string.IsNullOrEmpty(renderer.meshName) &&
            string.Equals(evt.meshName, renderer.meshName, StringComparison.OrdinalIgnoreCase))
            score += 80;

        if (!string.IsNullOrEmpty(evt.materialName) && string.Equals(evt.materialName, renderer.materialName, StringComparison.OrdinalIgnoreCase))
            score += 25;
        if (!string.IsNullOrEmpty(evt.materialAssetPath) && string.Equals(evt.materialAssetPath, renderer.materialPath, StringComparison.OrdinalIgnoreCase))
            score += 30;
        if (!string.IsNullOrEmpty(BestShaderName(evt)) && string.Equals(BestShaderName(evt), renderer.shaderName, StringComparison.OrdinalIgnoreCase))
            score += 20;

        if (CameraCanSeeLayer(camera, renderer.layer))
            score += 10;
        if (StageMatchesRenderQueue(stage, renderer.renderQueue))
            score += 10;
        if (!string.IsNullOrEmpty(evt.passLightMode) && !string.IsNullOrEmpty(renderer.shaderPassSummary) &&
            renderer.shaderPassSummary.IndexOf("LightMode=" + evt.passLightMode, StringComparison.OrdinalIgnoreCase) >= 0)
            score += 15;

        if (eventMeshes.Count == 0 && string.IsNullOrEmpty(evt.meshName) && score > 0)
            score = Math.Min(score, 55);
        return score;
    }

    private static string[] GetRendererMatchedBy(RendererRecord renderer, FrameEventInfo evt, string camera, string stage)
    {
        var matched = new List<string>();
        var eventMeshes = GetEventMeshNames(evt);
        if (eventMeshes.Count > 0 && !string.IsNullOrEmpty(renderer.meshName) &&
            eventMeshes.Any(m => string.Equals(m, renderer.meshName, StringComparison.OrdinalIgnoreCase)))
            matched.Add("meshName");
        if (!string.IsNullOrEmpty(evt.meshName) && string.Equals(evt.meshName, renderer.meshName, StringComparison.OrdinalIgnoreCase))
            matched.Add("directMeshName");
        if (!string.IsNullOrEmpty(evt.materialName) && string.Equals(evt.materialName, renderer.materialName, StringComparison.OrdinalIgnoreCase))
            matched.Add("materialName");
        if (!string.IsNullOrEmpty(BestShaderName(evt)) && string.Equals(BestShaderName(evt), renderer.shaderName, StringComparison.OrdinalIgnoreCase))
            matched.Add("shaderName");
        if (CameraCanSeeLayer(camera, renderer.layer))
            matched.Add("cameraCullingMask");
        if (StageMatchesRenderQueue(stage, renderer.renderQueue))
            matched.Add("renderQueueStage");
        if (!string.IsNullOrEmpty(evt.passLightMode) && !string.IsNullOrEmpty(renderer.shaderPassSummary) &&
            renderer.shaderPassSummary.IndexOf("LightMode=" + evt.passLightMode, StringComparison.OrdinalIgnoreCase) >= 0)
            matched.Add("lightMode");
        return matched.ToArray();
    }

    private static List<string> GetEventMeshNames(FrameEventInfo evt)
    {
        var meshes = new List<string>();
        if (evt == null) return meshes;
        if (!string.IsNullOrEmpty(evt.meshName))
            AddUnique(meshes, evt.meshName);
        foreach (var mesh in evt.detailMeshNames)
            AddUnique(meshes, mesh);
        return meshes;
    }

    private static List<string> GetFrameDebuggerDetailMeshNames(FrameEventInfo evt)
    {
        var meshes = new List<string>();
        if (evt == null) return meshes;
        foreach (var mesh in evt.detailMeshNames)
            AddUnique(meshes, mesh);
        return meshes;
    }

    private static string GetSrpBatchConfidence(bool hasFrameDebuggerDetails, int rendererCandidateCount, int bestScore, bool hasGenericMeshName)
    {
        if (!hasFrameDebuggerDetails)
            return "inferred_low";
        if (rendererCandidateCount == 1 && bestScore >= 100 && !hasGenericMeshName)
            return "inferred_high";
        if (bestScore >= 70 && !hasGenericMeshName)
            return "inferred_medium";
        if (bestScore >= 85 && rendererCandidateCount == 1)
            return "inferred_medium";
        return "inferred_low";
    }

    private static string BuildSrpBatchReason(int rendererCandidateCount, bool hasGenericMeshName, bool usesRuntimeSnapshot)
    {
        string reason = usesRuntimeSnapshot
            ? "Matched Frame Debugger mesh details against remote Player runtime Renderer index, then filtered by stage, renderQueue, material/shader, and LightMode."
            : "Matched Frame Debugger mesh details against active scene Renderer index, then filtered by camera, stage, renderQueue, material/shader, and LightMode.";
        if (rendererCandidateCount > 1)
            reason += " Multiple renderer candidates remain, so this is not exact object attribution.";
        if (hasGenericMeshName)
            reason += " Generic mesh names such as Quad or numeric names reduce attribution confidence.";
        return reason;
    }

    private static bool HasGenericFrameDebuggerMeshName(FrameEventInfo evt)
    {
        if (evt == null || evt.detailMeshNames == null)
            return false;
        return evt.detailMeshNames.Any(IsGenericMeshName);
    }

    private static bool IsGenericMeshName(string meshName)
    {
        if (string.IsNullOrWhiteSpace(meshName))
            return true;
        string value = meshName.Trim();
        if (value.Length <= 1)
            return true;
        int numeric;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            return true;
        return string.Equals(value, "Quad", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Plane", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Cube", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Mesh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Default-Mesh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFrameDebuggerDetailMeshes(FrameEventInfo evt)
    {
        return evt != null &&
               ((evt.detailMeshNames != null && evt.detailMeshNames.Count > 0) ||
                (evt.detailMeshInstanceIds != null && evt.detailMeshInstanceIds.Count > 0));
    }

    private static bool CameraCanSeeLayer(string cameraName, int layer)
    {
        var runtime = GetRuntimePlayerSnapshot();
        if (runtime.available && runtime.cameras.Count > 0)
        {
            if (string.IsNullOrEmpty(cameraName)) return true;
            var runtimeCamera = runtime.cameras.FirstOrDefault(c =>
                string.Equals(c.name, cameraName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.path, cameraName, StringComparison.OrdinalIgnoreCase) ||
                c.path.EndsWith("/" + cameraName, StringComparison.OrdinalIgnoreCase));
            if (runtimeCamera == null || runtimeCamera.cullingMask < 0) return true;
            return (runtimeCamera.cullingMask & (1 << layer)) != 0;
        }

        if (string.IsNullOrEmpty(cameraName)) return true;
        Camera camera = GetSceneCameras()
            .FirstOrDefault(c => string.Equals(c.name, cameraName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(GetHierarchyPath(c.gameObject), cameraName, StringComparison.OrdinalIgnoreCase) ||
                                 GetHierarchyPath(c.gameObject).EndsWith("/" + cameraName, StringComparison.OrdinalIgnoreCase));
        if (camera == null) return true;
        return (camera.cullingMask & (1 << layer)) != 0;
    }

    private static bool StageMatchesRenderQueue(string stage, int renderQueue)
    {
        if (string.IsNullOrEmpty(stage) || renderQueue < 0) return true;
        if (stage == "DrawOpaqueObjects") return renderQueue <= 2500;
        if (stage == "DrawTransparentObjects" || stage == "RenderBackGroundTransparent" || stage == "UI") return renderQueue >= 2501;
        return true;
    }

    private static ParticleCandidate BuildParticleCandidate(ParticleSystem particleSystem)
    {
        var renderer = particleSystem != null ? particleSystem.GetComponent<ParticleSystemRenderer>() : null;
        var material = renderer != null ? renderer.sharedMaterial : null;
        Texture mainTexture = GetMaterialMainTexture(material);
        string path = particleSystem != null ? GetHierarchyPath(particleSystem.gameObject) : "";
        return new ParticleCandidate
        {
            id = GetParticleCandidateId(particleSystem),
            path = path,
            materialName = material != null ? material.name : "",
            materialPath = material != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(material)) : "",
            shaderName = material != null && material.shader != null ? material.shader.name : "",
            aliveParticles = particleSystem != null ? particleSystem.particleCount : 0,
            maxParticles = particleSystem != null ? particleSystem.main.maxParticles : 0,
            rendererEnabled = renderer != null && renderer.enabled,
            rendererMode = renderer != null ? renderer.renderMode.ToString() : "",
            sortingLayerId = renderer != null ? renderer.sortingLayerID : 0,
            sortingOrder = renderer != null ? renderer.sortingOrder : 0,
            textureName = mainTexture != null ? mainTexture.name : "",
            textureMeta = TextureSummary(mainTexture),
            materialTextures = BuildMaterialTextureSummary(material, 12),
            bounds = renderer != null ? renderer.bounds.ToString() : "",
            materialFingerprint = MaterialFingerprint(material),
            resourceFingerprint = BuildRuntimeRendererFingerprint(path, "", material != null ? material.name : "", material != null && material.shader != null ? material.shader.name : "", mainTexture != null ? mainTexture.name : "", material != null ? material.renderQueue : -1),
            sortingGroup = SortingGroupSummary(particleSystem != null ? particleSystem.gameObject : null),
            confidence = "inferred_medium",
            reason = "Active ParticleSystem matched by material/shader/path heuristic."
        };
    }

    private static ParticleCandidate ScoreParticleCandidate(ParticleCandidate particle, FrameEventInfo evt)
    {
        if (particle == null || evt == null || particle.aliveParticles <= 0) return null;
        var matchedBy = new List<string>();
        int score = 0;

        if (!string.IsNullOrEmpty(evt.gameObjectPath) &&
            (evt.gameObjectPath.IndexOf(particle.path, StringComparison.OrdinalIgnoreCase) >= 0 ||
             particle.path.IndexOf(evt.gameObjectPath, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            score += 40;
            matchedBy.Add("path");
        }
        if (!string.IsNullOrEmpty(particle.materialPath) && string.Equals(particle.materialPath, evt.materialAssetPath, StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
            matchedBy.Add("materialPath");
        }
        if (!string.IsNullOrEmpty(particle.materialName) && string.Equals(particle.materialName, evt.materialName, StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
            matchedBy.Add("materialName");
        }
        if (!string.IsNullOrEmpty(particle.shaderName) && string.Equals(particle.shaderName, BestShaderName(evt), StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            matchedBy.Add("shaderName");
        }
        bool directRenderer = !string.IsNullOrEmpty(evt.frameDebuggerRendererPath) &&
                              string.Equals(evt.frameDebuggerRendererPath, particle.path, StringComparison.OrdinalIgnoreCase);
        if (directRenderer)
        {
            score += 100;
            matchedBy.Add("frameDebuggerRendererPath");
        }

        string stage = ParseStageName(evt.eventName);
        if (score < 40 || (stage != "DrawTransparentObjects" && stage != "UI" && score < 70))
            return null;

        bool hasPath = matchedBy.Contains("path");
        bool hasMaterial = matchedBy.Contains("materialPath") || matchedBy.Contains("materialName");
        bool hasShader = matchedBy.Contains("shaderName");
        string confidence = directRenderer
            ? "direct"
            : hasPath && hasMaterial && hasShader
            ? "inferred_high"
            : (hasPath && hasMaterial) || (hasMaterial && hasShader)
                ? "inferred_medium"
                : "inferred_low";

        var result = new ParticleCandidate
        {
            id = particle.id,
            path = particle.path,
            materialName = particle.materialName,
            materialPath = particle.materialPath,
            shaderName = particle.shaderName,
            aliveParticles = particle.aliveParticles,
            maxParticles = particle.maxParticles,
            rendererEnabled = particle.rendererEnabled,
            rendererMode = particle.rendererMode,
            sortingLayerId = particle.sortingLayerId,
            sortingOrder = particle.sortingOrder,
            textureName = particle.textureName,
            textureMeta = particle.textureMeta,
            materialTextures = particle.materialTextures,
            bounds = particle.bounds,
            materialFingerprint = particle.materialFingerprint,
            resourceFingerprint = particle.resourceFingerprint,
            sortingGroup = particle.sortingGroup,
            mappedToFrameEvent = true,
            matchScore = score,
            matchedBy = matchedBy.ToArray(),
            confidence = confidence,
            reason = confidence == "direct"
                ? "FrameDebugger direct Renderer path matched this active ParticleSystem renderer."
                : confidence == "inferred_high"
                ? "Active ParticleSystem matched by path, material, and shader evidence."
                : confidence == "inferred_medium"
                    ? "Active ParticleSystem matched by path+material or material+shader evidence."
                    : "Active ParticleSystem matched by weak or partial evidence only."
        };
        return result;
    }

    private static bool IsActiveParticleSystemForFrame(ParticleSystem particleSystem)
    {
        return particleSystem != null &&
               particleSystem.gameObject.activeInHierarchy &&
               particleSystem.particleCount > 0 &&
               (particleSystem.isPlaying || particleSystem.particleCount > 0);
    }

    private static int GetParticleCandidateId(ParticleSystem particleSystem)
    {
        return particleSystem != null ? StablePositiveHash(GetHierarchyPath(particleSystem.gameObject)) : 0;
    }

    private static int StablePositiveHash(string value)
    {
        unchecked
        {
            int hash = 23;
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
            }
            return hash == int.MinValue ? int.MaxValue : Math.Abs(hash);
        }
    }

    private static List<UiGraphicRecord> BuildUiGraphicRecords()
    {
        if (s_exportCache != null)
        {
            if (s_exportCache.uiGraphicRecords == null)
                s_exportCache.uiGraphicRecords = BuildUiGraphicRecordsUncached();
            return s_exportCache.uiGraphicRecords;
        }

        return BuildUiGraphicRecordsUncached();
    }

    private static List<UiGraphicRecord> BuildUiGraphicRecordsUncached()
    {
        var runtime = GetRuntimePlayerSnapshot();
        if (runtime.available && runtime.uiGraphics.Count > 0)
            return runtime.uiGraphics
                .Where(r => !string.IsNullOrEmpty(r.path))
                .OrderBy(r => r.canvasSortingLayerId)
                .ThenBy(r => r.canvasSortingOrder)
                .ThenBy(r => r.depth)
                .ThenBy(r => r.path)
                .ToList();

        Type graphicType = FindType("UnityEngine.UI.Graphic", null);
        if (graphicType == null) return new List<UiGraphicRecord>();

        return FindSceneObjects(graphicType)
            .OfType<Component>()
            .Select(BuildUiGraphicRecord)
            .Where(r => !string.IsNullOrEmpty(r.path))
            .OrderBy(r => r.canvasSortingLayerId)
            .ThenBy(r => r.canvasSortingOrder)
            .ThenBy(r => r.depth)
            .ThenBy(r => r.path)
            .ToList();
    }

    private static UiGraphicRecord BuildUiGraphicRecord(Component graphic)
    {
        var record = new UiGraphicRecord();
        if (graphic == null) return record;

        Canvas canvas = GetComponentInParentSafe<Canvas>(graphic);
        Material material = GetReflectedObject<Material>(graphic, "materialForRendering") ?? GetReflectedObject<Material>(graphic, "material");
        Texture texture = GetReflectedObject<Texture>(graphic, "mainTexture");
        CanvasRenderer canvasRenderer = graphic.GetComponent<CanvasRenderer>();
        RectTransform rectTransform = graphic.transform as RectTransform;
        Canvas rootCanvas = canvas != null ? canvas.rootCanvas : null;

        record.path = GetHierarchyPath(graphic.gameObject);
        record.type = graphic.GetType().Name;
        record.canvasPath = canvas != null ? GetHierarchyPath(canvas.gameObject) : "";
        record.rootCanvasPath = rootCanvas != null ? GetHierarchyPath(rootCanvas.gameObject) : "";
        record.canvasCamera = canvas != null && canvas.worldCamera != null ? GetHierarchyPath(canvas.worldCamera.gameObject) : "";
        record.canvasRenderMode = canvas != null ? canvas.renderMode.ToString() : "";
        record.canvasSortingLayerId = canvas != null ? canvas.sortingLayerID : 0;
        record.canvasSortingOrder = canvas != null ? canvas.sortingOrder : 0;
        record.canvasScaleFactor = canvas != null ? canvas.scaleFactor : 0f;
        record.canvasReferencePixelsPerUnit = canvas != null ? canvas.referencePixelsPerUnit : 0f;
        record.depth = GetReflectedInt(graphic, "depth", -1);
        record.active = graphic.gameObject.activeInHierarchy;
        record.enabled = GetReflectedBool(graphic, "enabled");
        record.canvasEnabled = canvas != null && canvas.enabled;
        record.raycastTarget = GetReflectedBool(graphic, "raycastTarget");
        record.materialName = material != null ? material.name : "";
        record.materialPath = material != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(material)) : "";
        record.shaderName = material != null && material.shader != null ? material.shader.name : "";
        record.shaderPath = material != null && material.shader != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(material.shader)) : "";
        record.textureName = texture != null ? texture.name : "";
        record.texturePath = texture != null ? ToProjectRelativePath(AssetDatabase.GetAssetPath(texture)) : "";
        record.textureMeta = TextureSummary(texture);
        record.materialTextures = BuildMaterialTextureSummary(material, 12);
        record.spriteName = GetReflectedObject<UnityEngine.Object>(graphic, "sprite") != null ? GetReflectedObject<UnityEngine.Object>(graphic, "sprite").name : "";
        record.rect = rectTransform != null ? rectTransform.rect.ToString() : "";
        record.screenRect = rectTransform != null ? ScreenRectString(rectTransform, canvas) : "";
        record.isTextComponent = IsTextComponent(graphic);
        record.textLength = GetReflectedStringLength(graphic, "text");
        record.fontName = GetReflectedObject<UnityEngine.Object>(graphic, "font") != null ? GetReflectedObject<UnityEngine.Object>(graphic, "font").name : "";
        record.fontMaterialName = FirstToken(GetReflectedObject<UnityEngine.Object>(graphic, "fontMaterial") != null ? GetReflectedObject<UnityEngine.Object>(graphic, "fontMaterial").name : "", GetReflectedObject<UnityEngine.Object>(graphic, "fontSharedMaterial") != null ? GetReflectedObject<UnityEngine.Object>(graphic, "fontSharedMaterial").name : "");
        record.materialFingerprint = MaterialFingerprint(material);
        record.sortingGroup = SortingGroupSummary(graphic.gameObject);
        record.resourceFingerprint = BuildRuntimeUiFingerprint(record);
        record.maskState = GetGraphicMaskState(graphic);
        record.canvasRenderer = SummarizeCanvasRenderer(canvasRenderer);
        record.prefabAssetPath = GetPrefabAssetPath(graphic.gameObject);
        record.batchKey = BuildUiBatchKey(record);
        return record;
    }

    private static List<UiBatchCandidate> BuildUiBatchesFromRecords(List<UiGraphicRecord> records, string camera)
    {
        var batches = new List<UiBatchCandidate>();
        UiBatchCandidate current = null;
        string lastKey = null;

        foreach (var record in records)
        {
            if (record == null || string.IsNullOrEmpty(record.batchKey)) continue;

            if (current == null || !string.Equals(lastKey, record.batchKey, StringComparison.Ordinal))
            {
                current = new UiBatchCandidate
                {
                    camera = camera,
                    canvasPath = record.canvasPath,
                    batchKey = record.batchKey,
                    materialName = record.materialName,
                    materialPath = record.materialPath,
                    shaderName = record.shaderName,
                    textureName = record.textureName,
                    texturePath = record.texturePath,
                    maskState = record.maskState,
                    sequenceIndex = batches.Count
                };
                batches.Add(current);
                lastKey = record.batchKey;
            }

            current.graphics.Add(record);
        }

        return batches;
    }

    private static UiBatchCandidate CloneUiBatchCandidate(UiBatchCandidate source)
    {
        if (source == null) return null;
        return new UiBatchCandidate
        {
            id = source.id,
            mappedEventIndex = source.mappedEventIndex,
            sequenceIndex = source.sequenceIndex,
            camera = source.camera,
            canvasPath = source.canvasPath,
            batchKey = source.batchKey,
            materialName = source.materialName,
            materialPath = source.materialPath,
            shaderName = source.shaderName,
            textureName = source.textureName,
            texturePath = source.texturePath,
            maskState = source.maskState,
            confidence = source.confidence,
            reason = source.reason,
            graphics = new List<UiGraphicRecord>(source.graphics)
        };
    }

    private static bool CameraMatchesUiEvent(string eventCamera, UiGraphicRecord record)
    {
        if (record == null) return false;
        if (string.IsNullOrEmpty(eventCamera)) return true;
        if (string.IsNullOrEmpty(record.canvasCamera))
            return string.Equals(record.canvasRenderMode, "ScreenSpaceOverlay", StringComparison.OrdinalIgnoreCase);

        string cameraName = LastPathSegment(record.canvasCamera);
        return string.Equals(cameraName, eventCamera, StringComparison.OrdinalIgnoreCase) ||
               record.canvasCamera.IndexOf(eventCamera, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string LastPathSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }

    private static string BuildUiBatchKey(UiGraphicRecord record)
    {
        if (record == null) return "";
        return string.Join("|", new[]
        {
            record.canvasPath,
            !string.IsNullOrEmpty(record.materialPath) ? record.materialPath : record.materialName,
            record.shaderName,
            !string.IsNullOrEmpty(record.texturePath) ? record.texturePath : record.textureName,
            record.maskState
        });
    }

    private static void AppendUiBatchCandidateObject(StringBuilder sb, UiBatchCandidate candidate, string pad)
    {
        sb.Append(pad).Append("{");
        AppendJsonPropertyInline(sb, "id", candidate.id, true);
        AppendJsonPropertyInline(sb, "mappedEventIndex", candidate.mappedEventIndex, true);
        AppendJsonPropertyInline(sb, "sequenceIndex", candidate.sequenceIndex, true);
        AppendJsonPropertyInline(sb, "camera", candidate.camera, true);
        AppendJsonPropertyInline(sb, "confidence", candidate.confidence, true);
        AppendJsonPropertyInline(sb, "source", candidate.graphics.Count > 0 ? candidate.graphics[0].source : "", true);
        AppendJsonPropertyInline(sb, "reason", candidate.reason, true);
        AppendJsonPropertyInline(sb, "method", "canvas_order_sequence_match", true);
        AppendStringArrayInline(sb, "matchedBy", new[] { "camera", "canvas", "material", "shader", "texture", "graphicDepthRange", "maskState" }, true);
        AppendJsonPropertyInline(sb, "risk", "dynamic layout, nested canvases, or engine-side material mutation may shift order", true);
        AppendJsonPropertyInline(sb, "canvasPath", candidate.canvasPath, true);
        AppendJsonPropertyInline(sb, "batchKey", candidate.batchKey, true);
        AppendJsonPropertyInline(sb, "materialName", candidate.materialName, true);
        AppendJsonPropertyInline(sb, "materialPath", candidate.materialPath, true);
        AppendJsonPropertyInline(sb, "shaderName", candidate.shaderName, true);
        AppendJsonPropertyInline(sb, "textureName", candidate.textureName, true);
        AppendJsonPropertyInline(sb, "texturePath", candidate.texturePath, true);
        AppendJsonPropertyInline(sb, "maskState", candidate.maskState, true);
        sb.Append("\"graphics\": [");
        int limit = Math.Min(candidate.graphics.Count, 32);
        for (int i = 0; i < limit; i++)
        {
            var graphic = candidate.graphics[i];
            if (i > 0) sb.Append(", ");
            sb.Append("{");
            AppendJsonPropertyInline(sb, "path", graphic.path, true);
            AppendJsonPropertyInline(sb, "source", graphic.source, true);
            AppendJsonPropertyInline(sb, "type", graphic.type, true);
            AppendJsonPropertyInline(sb, "depth", graphic.depth, true);
            AppendJsonPropertyInline(sb, "materialName", graphic.materialName, true);
            AppendJsonPropertyInline(sb, "textureName", graphic.textureName, true);
            AppendJsonPropertyInline(sb, "spriteName", graphic.spriteName, true);
            AppendJsonPropertyInline(sb, "screenRect", graphic.screenRect, true);
            AppendJsonPropertyInline(sb, "resourceFingerprint", graphic.resourceFingerprint, true);
            AppendJsonPropertyInline(sb, "sortingGroup", graphic.sortingGroup, true);
            AppendJsonPropertyInline(sb, "maskState", graphic.maskState, true);
            AppendJsonPropertyInline(sb, "prefabAssetPath", graphic.prefabAssetPath, false);
            sb.Append("}");
        }
        sb.Append("]}");
    }

    private static string GetGraphicMaterialKey(Component graphic)
    {
        Material material = GetReflectedObject<Material>(graphic, "materialForRendering") ?? GetReflectedObject<Material>(graphic, "material");
        if (material == null) return "";
        string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(material));
        return string.IsNullOrEmpty(path) ? material.name : material.name + " @ " + path;
    }

    private static string GetGraphicTextureKey(Component graphic)
    {
        Texture texture = GetReflectedObject<Texture>(graphic, "mainTexture");
        if (texture == null) return "";
        string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(texture));
        return string.IsNullOrEmpty(path) ? texture.name : texture.name + " @ " + path;
    }

    private static Texture GetMaterialMainTexture(Material material)
    {
        if (material == null) return null;
        try { return material.mainTexture; }
        catch { return null; }
    }

    private static string MaterialFingerprint(Material material)
    {
        if (material == null) return "";
        string shader = material.shader != null ? material.shader.name : "";
        Texture texture = GetMaterialMainTexture(material);
        return "mat=" + material.name +
               "|shader=" + shader +
               "|queue=" + material.renderQueue.ToString(CultureInfo.InvariantCulture) +
               "|mainTex=" + (texture != null ? texture.name : "") +
               "|id=" + material.GetInstanceID().ToString(CultureInfo.InvariantCulture);
    }

    private static string TextureSummary(Texture texture)
    {
        if (texture == null) return "";
        string format = "";
        int aa = 1;
        int depth = 0;
        try
        {
            if (texture is Texture2D texture2D) format = texture2D.format.ToString();
            else if (texture is RenderTexture renderTexture)
            {
                format = renderTexture.format + "/" + renderTexture.graphicsFormat;
                aa = renderTexture.antiAliasing;
                depth = renderTexture.depth;
            }
            else if (texture is Cubemap cubemap) format = cubemap.format.ToString();
        }
        catch { }

        return texture.name +
               "[" + texture.GetType().Name +
               ";" + texture.width.ToString(CultureInfo.InvariantCulture) + "x" + texture.height.ToString(CultureInfo.InvariantCulture) +
               ";" + format +
               ";mip=" + texture.mipmapCount.ToString(CultureInfo.InvariantCulture) +
               ";msaa=" + aa.ToString(CultureInfo.InvariantCulture) +
               ";depth=" + depth.ToString(CultureInfo.InvariantCulture) + "]";
    }

    private static string SortingGroupSummary(GameObject go)
    {
        if (go == null) return "";
        try
        {
            var group = go.GetComponentInParent<UnityEngine.Rendering.SortingGroup>();
            if (group == null) return "";
            return GetHierarchyPath(group.gameObject) +
                   ";layerId=" + group.sortingLayerID.ToString(CultureInfo.InvariantCulture) +
                   ";order=" + group.sortingOrder.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static bool IsTextComponent(Component component)
    {
        if (component == null) return false;
        string type = component.GetType().FullName ?? component.GetType().Name;
        return type.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0 ||
               type.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int GetReflectedStringLength(object target, string memberName)
    {
        object value = GetReflectedValue(target, memberName);
        string text = value as string;
        return string.IsNullOrEmpty(text) ? 0 : text.Length;
    }

    private static string ScreenRectString(RectTransform rectTransform, Canvas canvas)
    {
        if (rectTransform == null) return "";
        var corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 max = min;
        for (int i = 1; i < 4; i++)
        {
            Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return min.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               min.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               (max.x - min.x).ToString("0.###", CultureInfo.InvariantCulture) + "," +
               (max.y - min.y).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string GetGraphicMaskState(Component graphic)
    {
        if (graphic == null) return "";
        var components = graphic.GetComponentsInParent<Component>(true);
        bool hasMask = components.Any(c => c != null && c.GetType().Name == "Mask");
        bool hasRectMask = components.Any(c => c != null && c.GetType().Name == "RectMask2D");
        bool hasMaskable = GetReflectedBool(graphic, "maskable");
        if (!hasMask && !hasRectMask && !hasMaskable) return "no-mask";
        var parts = new List<string>();
        if (hasMask) parts.Add("Mask");
        if (hasRectMask) parts.Add("RectMask2D");
        if (hasMaskable) parts.Add("maskable");
        return string.Join("+", parts);
    }

    private static string SummarizeCanvasRenderer(CanvasRenderer canvasRenderer)
    {
        if (canvasRenderer == null) return "";
        var parts = new List<string>();
        AddReflectedPart(parts, canvasRenderer, "materialCount");
        AddReflectedPart(parts, canvasRenderer, "popMaterialCount");
        AddReflectedPart(parts, canvasRenderer, "hasMoved");
        try
        {
            var getMaterial = typeof(CanvasRenderer).GetMethod("GetMaterial", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
            var mat = getMaterial != null ? getMaterial.Invoke(canvasRenderer, new object[] { 0 }) as Material : null;
            if (mat != null) parts.Add("material=" + mat.name + "@" + ToProjectRelativePath(AssetDatabase.GetAssetPath(mat)));
        }
        catch { }
        return string.Join("; ", parts);
    }

    private static void AddReflectedPart(List<string> parts, object target, string memberName)
    {
        object value = GetReflectedValue(target, memberName);
        if (value != null)
            parts.Add(memberName + "=" + Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string GetParticleMaterialKey(ParticleSystem particleSystem)
    {
        if (particleSystem == null) return "";
        var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
        if (renderer == null || renderer.sharedMaterial == null) return "";
        Material material = renderer.sharedMaterial;
        string path = ToProjectRelativePath(AssetDatabase.GetAssetPath(material));
        return string.IsNullOrEmpty(path) ? material.name : material.name + " @ " + path;
    }

    private static string GetPrefabAssetPath(GameObject go)
    {
        if (go == null) return "";
        try
        {
            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            string path = source != null ? AssetDatabase.GetAssetPath(source) : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return ToProjectRelativePath(path);
        }
        catch { return ""; }
    }

    private static T GetReflectedObject<T>(object target, string name) where T : UnityEngine.Object
    {
        object value = GetReflectedValue(target, name);
        return value as T;
    }

    private static bool GetReflectedBool(object target, string name, bool defaultValue = false)
    {
        object value = GetReflectedValue(target, name);
        if (value is bool b) return b;
        return defaultValue;
    }

    private static int GetReflectedInt(object target, string name, int defaultValue = 0)
    {
        object value = GetReflectedValue(target, name);
        if (value == null) return defaultValue;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return defaultValue; }
    }

    private static object GetReflectedValue(object target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name)) return null;
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(target, null);
        }
        catch { }
        try
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(target);
        }
        catch { }
        return null;
    }

    private static string LayerMaskToNames(int mask)
    {
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            string name = LayerMask.LayerToName(i);
            names.Add(string.IsNullOrEmpty(name) ? i.ToString(CultureInfo.InvariantCulture) : name);
        }
        return string.Join("|", names);
    }

    private static string SummarizeObjectFields(UnityEngine.Object obj, int maxItems)
    {
        if (obj == null) return "";
        var items = new List<string>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in obj.GetType().GetFields(flags))
        {
            if (items.Count >= maxItems) break;
            if (field.IsStatic) continue;
            try
            {
                object value = field.GetValue(obj);
                string summary = SummarizeReflectedValue(value);
                if (!string.IsNullOrEmpty(summary))
                    items.Add(field.Name + "=" + summary);
            }
            catch { }
        }
        foreach (var prop in obj.GetType().GetProperties(flags))
        {
            if (items.Count >= maxItems) break;
            if (prop.GetIndexParameters().Length > 0) continue;
            try
            {
                object value = prop.GetValue(obj, null);
                string summary = SummarizeReflectedValue(value);
                if (!string.IsNullOrEmpty(summary))
                    items.Add(prop.Name + "=" + summary);
            }
            catch { }
        }
        return string.Join("; ", items);
    }

    private static string SummarizeReflectedValue(object value)
    {
        if (value == null) return "";
        if (value is Material mat)
            return "Material:" + mat.name + "@" + ToProjectRelativePath(AssetDatabase.GetAssetPath(mat)) + (mat.shader != null ? "[" + mat.shader.name + "]" : "");
        if (value is Shader shader)
            return "Shader:" + shader.name + "@" + ToProjectRelativePath(AssetDatabase.GetAssetPath(shader));
        if (value is Texture tex)
            return "Texture:" + tex.name + "@" + ToProjectRelativePath(AssetDatabase.GetAssetPath(tex));
        if (value is RenderTexture rt)
            return "RenderTexture:" + rt.name + "(" + rt.width.ToString(CultureInfo.InvariantCulture) + "x" + rt.height.ToString(CultureInfo.InvariantCulture) + ")";
        if (value is UnityEngine.Object obj)
            return obj.GetType().Name + ":" + obj.name + "@" + ToProjectRelativePath(AssetDatabase.GetAssetPath(obj));
        Type type = value.GetType();
        if (type.IsPrimitive || value is string || value is Enum)
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        return "";
    }

    private static IEnumerable<ScriptableObject> GetRendererDataObjects(ScriptableObject pipelineAsset)
    {
        if (pipelineAsset == null) yield break;
        var seen = new HashSet<int>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var memberValue in EnumerateMemberValues(pipelineAsset, flags))
        {
            foreach (var obj in FlattenUnityObjects(memberValue).OfType<ScriptableObject>())
            {
                string typeName = obj.GetType().Name;
                if (!typeName.Contains("RendererData")) continue;
                if (seen.Add(obj.GetInstanceID()))
                    yield return obj;
            }
        }
    }

    private static IEnumerable<ScriptableObject> GetRendererFeatures(ScriptableObject rendererData)
    {
        if (rendererData == null) yield break;
        var seen = new HashSet<int>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var memberValue in EnumerateMemberValues(rendererData, flags))
        {
            foreach (var obj in FlattenUnityObjects(memberValue).OfType<ScriptableObject>())
            {
                string typeName = obj.GetType().Name;
                if (!typeName.Contains("Feature")) continue;
                if (seen.Add(obj.GetInstanceID()))
                    yield return obj;
            }
        }
    }

    private static IEnumerable<object> EnumerateMemberValues(object target, BindingFlags flags)
    {
        foreach (var field in target.GetType().GetFields(flags))
        {
            if (field.IsStatic) continue;
            object value = null;
            try { value = field.GetValue(target); } catch { }
            if (value != null) yield return value;
        }

        foreach (var prop in target.GetType().GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object value = null;
            try { value = prop.GetValue(target, null); } catch { }
            if (value != null) yield return value;
        }
    }

    private static IEnumerable<UnityEngine.Object> FlattenUnityObjects(object value)
    {
        if (value == null) yield break;
        if (value is UnityEngine.Object obj)
        {
            yield return obj;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable && !(value is string))
        {
            foreach (var item in enumerable)
            {
                if (item is UnityEngine.Object child)
                    yield return child;
            }
        }
    }

    private static bool GetFeatureActive(ScriptableObject feature)
    {
        if (feature == null) return false;
        object active = GetReflectedValue(feature, "m_Active") ?? GetReflectedValue(feature, "isActive");
        if (active is bool b) return b;
        return true;
    }

    private static List<MaterialStatEntry> BuildMaterialStats(IEnumerable<FrameEventInfo> events)
    {
        var map = new Dictionary<string, MaterialStatEntry>();
        foreach (var evt in events)
        {
            if (string.IsNullOrEmpty(evt.materialName)) continue;
            string key = !string.IsNullOrEmpty(evt.materialAssetPath) ? evt.materialAssetPath : evt.materialName;
            if (!map.TryGetValue(key, out var stat))
            {
                stat = new MaterialStatEntry
                {
                    materialName = evt.materialName,
                    materialAssetPath = evt.materialAssetPath,
                    shaderName = BestShaderName(evt),
                    shaderAssetPath = evt.resolvedShaderAssetPath,
                    instancingEnabled = evt.materialInstancingEnabled,
                    keywords = evt.materialKeywords,
                    textures = evt.materialTextures,
                    properties = evt.materialProperties,
                    renderQueue = evt.materialRenderQueue
                };
                map[key] = stat;
            }
            stat.drawCallCount++;
            if (!string.IsNullOrEmpty(BestPassName(evt))) stat.passNames.Add(BestPassName(evt));
            if (!string.IsNullOrEmpty(evt.gameObjectName)) stat.objectNames.Add(evt.gameObjectName);
        }
        return map.Values.OrderByDescending(s => s.drawCallCount).ThenBy(s => s.materialName).ToList();
    }

    private static List<ShaderStatEntry> BuildShaderStats(IEnumerable<FrameEventInfo> events)
    {
        var map = new Dictionary<string, ShaderStatEntry>();
        foreach (var evt in events)
        {
            string shaderName = BestShaderName(evt);
            if (string.IsNullOrEmpty(shaderName)) continue;
            string key = !string.IsNullOrEmpty(evt.resolvedShaderAssetPath) ? evt.resolvedShaderAssetPath : shaderName;
            if (!map.TryGetValue(key, out var stat))
            {
                stat = new ShaderStatEntry
                {
                    shaderName = shaderName,
                    shaderAssetPath = evt.resolvedShaderAssetPath,
                    passSummary = evt.resolvedShaderPassSummary
                };
                map[key] = stat;
            }
            stat.drawCallCount++;
            if (!string.IsNullOrEmpty(BestPassName(evt))) stat.passNames.Add(BestPassName(evt));
            if (!string.IsNullOrEmpty(evt.materialName)) stat.materialNames.Add(evt.materialName);
            if (!string.IsNullOrEmpty(evt.gameObjectName)) stat.objectNames.Add(evt.gameObjectName);
        }
        return map.Values.OrderByDescending(s => s.drawCallCount).ThenBy(s => s.shaderName).ToList();
    }

    private static string BestShaderName(FrameEventInfo evt)
    {
        if (evt == null) return "";
        if (!string.IsNullOrEmpty(evt.shaderName)) return evt.shaderName;
        return evt.resolvedShaderName ?? "";
    }

    private static string BestPassName(FrameEventInfo evt)
    {
        if (evt == null) return "";
        if (!string.IsNullOrEmpty(evt.passName)) return evt.passName;
        if (!string.IsNullOrEmpty(evt.passLightMode)) return evt.passLightMode;
        return evt.typeName ?? "";
    }

    private static string ParseCameraName(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return "";
        const string marker = "RenderSingleCameraInternal: ";
        int start = eventName.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += marker.Length;
        int slash = eventName.IndexOf('/', start);
        if (slash < 0) return eventName.Substring(start).Trim();
        return eventName.Substring(start, slash - start).Trim();
    }

    private static string ParseStageName(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return "";
        if (eventName.Contains("DrawOpaqueObjects")) return "DrawOpaqueObjects";
        if (eventName.Contains("DrawTransparentObjects")) return "DrawTransparentObjects";
        if (eventName.Contains("Render Shadow")) return "RenderShadow";
        if (eventName.Contains("Render Back Ground Transparent")) return "RenderBackGroundTransparent";
        if (eventName.Contains("Render PostProcessing Effects")) return "PostProcessing";
        if (eventName.Contains("ColorGradingLUT")) return "ColorGradingLUT";
        if (eventName.Contains("Canvas.RenderSubBatch") || eventName.Contains("UGUI.Rendering")) return "UI";
        if (eventName.Contains("Clear")) return "Clear";

        string[] parts = eventName.Split('/');
        if (parts.Length >= 3)
            return parts[2].Trim();
        if (parts.Length > 0)
            return parts[parts.Length - 1].Trim();
        return eventName;
    }

    private static void AppendSuspiciousHighlights(StringBuilder sb, List<FrameEventInfo> events)
    {
        sb.AppendLine("  \"suspiciousHighlights\": {");
        WriteJsonProperty(sb, "transparentDrawEvents", events.Count(e => ParseStageName(e.eventName) == "DrawTransparentObjects"), true, 4);
        WriteJsonProperty(sb, "eventsWithoutResolvedShader", events.Count(e => string.IsNullOrEmpty(BestShaderName(e))), true, 4);
        WriteJsonProperty(sb, "eventsWithoutResolvedMaterial", events.Count(e => string.IsNullOrEmpty(e.materialName)), true, 4);
        WriteJsonProperty(sb, "eventsWithBatchBreakCause", events.Count(e => !string.IsNullOrEmpty(e.batchBreakCause)), true, 4);
        WriteJsonProperty(sb, "eventsWithGameObject", events.Count(e => !string.IsNullOrEmpty(e.gameObjectName)), true, 4);
        AppendAggregateArray(sb, "topTransparentMaterials", BuildSimpleStats(events.Where(e => ParseStageName(e.eventName) == "DrawTransparentObjects"), e => e.materialName), true, 4);
        AppendAggregateArray(sb, "topTransparentShaders", BuildSimpleStats(events.Where(e => ParseStageName(e.eventName) == "DrawTransparentObjects"), e => BestShaderName(e)), false, 4);
        sb.AppendLine("  }");
    }

    private static void AppendAggregateArray(StringBuilder sb, string name, List<KeyValuePair<string, int>> rows, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append(pad).Append("  {");
            AppendJsonPropertyInline(sb, "name", row.Key, true);
            AppendJsonPropertyInline(sb, "draws", row.Value, false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append(pad).Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendIssueTable(StringBuilder sb, string name, List<IssueEntry> rows, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append(pad).Append("  {");
            AppendJsonPropertyInline(sb, "name", row.name, true);
            AppendJsonPropertyInline(sb, "count", row.count, true);
            AppendJsonPropertyInline(sb, "confidence", row.confidence, true);
            AppendJsonPropertyInline(sb, "evidence", row.evidence, true);
            AppendJsonPropertyInline(sb, "limitations", row.limitations, false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append(pad).Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTopUiBatchCandidateTable(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        var rows = BuildUiBatchCandidateMap(events)
            .Values
            .OrderByDescending(c => c.graphics.Count)
            .ThenBy(c => c.mappedEventIndex)
            .Take(10)
            .ToList();

        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append(pad).Append("  {");
            AppendJsonPropertyInline(sb, "eventIndex", row.mappedEventIndex, true);
            AppendJsonPropertyInline(sb, "candidateId", row.id, true);
            AppendJsonPropertyInline(sb, "canvasPath", row.canvasPath, true);
            AppendJsonPropertyInline(sb, "firstGraphicPath", GetRepresentativeUiPath(row), true);
            AppendJsonPropertyInline(sb, "materialName", row.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", row.shaderName, true);
            AppendJsonPropertyInline(sb, "textureName", row.textureName, true);
            AppendJsonPropertyInline(sb, "graphicCount", row.graphics.Count, true);
            AppendJsonPropertyInline(sb, "confidence", row.confidence, true);
            AppendJsonPropertyInline(sb, "limitations", GetUiBatchRiskNote(), false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append(pad).Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTopParticleRootTable(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        var rows = BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value.Select(c => new { eventIndex = kv.Key + 1, candidate = c }))
            .GroupBy(x => GetPrefabRootFromPath(x.candidate.path))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g =>
            {
                var uniqueParticles = g.Select(x => x.candidate).GroupBy(c => c.id).Select(pg => pg.First()).ToList();
                return new
                {
                    rootPath = g.Key,
                    particleSystemCount = uniqueParticles.Count,
                    aliveParticleCount = uniqueParticles.Sum(c => Math.Max(0, c.aliveParticles)),
                    mappedEventCount = g.Select(x => x.eventIndex).Distinct().Count(),
                    eventIndexes = g.Select(x => x.eventIndex.ToString(CultureInfo.InvariantCulture)).Distinct().Take(10).ToArray()
                };
            })
            .OrderByDescending(r => r.aliveParticleCount)
            .ThenByDescending(r => r.mappedEventCount)
            .ThenBy(r => r.rootPath)
            .Take(10)
            .ToList();

        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append(pad).Append("  {");
            AppendJsonPropertyInline(sb, "rootPath", row.rootPath, true);
            AppendJsonPropertyInline(sb, "particleSystemCount", row.particleSystemCount, true);
            AppendJsonPropertyInline(sb, "aliveParticleCount", row.aliveParticleCount, true);
            AppendJsonPropertyInline(sb, "mappedEventCount", row.mappedEventCount, true);
            AppendStringArrayInline(sb, "eventIndexes", row.eventIndexes, true);
            AppendJsonPropertyInline(sb, "sourceFile", "ai_event_evidence.jsonl", false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append(pad).Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTopParticleCandidateTable(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        var rows = BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value.Select(c => new { eventIndex = kv.Key + 1, candidate = c }))
            .Where(x => IsStrongParticleTopCandidate(x.candidate))
            .GroupBy(x => x.candidate.id)
            .Select(g =>
            {
                var first = g.OrderByDescending(x => x.candidate.matchScore).ThenByDescending(x => x.candidate.aliveParticles).First();
                return new
                {
                    eventIndexes = g.Select(x => x.eventIndex.ToString(CultureInfo.InvariantCulture)).Distinct().Take(10).ToArray(),
                    candidate = first.candidate,
                    mappedEventCount = g.Select(x => x.eventIndex).Distinct().Count()
                };
            })
            .OrderByDescending(r => r.candidate.aliveParticles)
            .ThenByDescending(r => r.candidate.matchScore)
            .ThenBy(r => r.candidate.path)
            .Take(10)
            .ToList();

        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append(pad).Append("  {");
            AppendJsonPropertyInline(sb, "candidateId", row.candidate.id, true);
            AppendJsonPropertyInline(sb, "path", row.candidate.path, true);
            AppendJsonPropertyInline(sb, "materialName", row.candidate.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", row.candidate.shaderName, true);
            AppendJsonPropertyInline(sb, "aliveParticleCount", row.candidate.aliveParticles, true);
            AppendJsonPropertyInline(sb, "mappedEventCount", row.mappedEventCount, true);
            AppendJsonPropertyInline(sb, "matchScore", row.candidate.matchScore, true);
            AppendStringArrayInline(sb, "matchedBy", row.candidate.matchedBy, true);
            AppendStringArrayInline(sb, "eventIndexes", row.eventIndexes, true);
            AppendJsonPropertyInline(sb, "sourceFile", "ai_event_evidence.jsonl", false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append(pad).Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendParticleCandidateSummaryObject(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        var candidates = BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value.Select(c => new { eventIndex = kv.Key + 1, candidate = c }))
            .ToList();
        var unique = candidates
            .Select(x => x.candidate)
            .GroupBy(c => c.id)
            .Select(g => g.OrderByDescending(c => c.matchScore).ThenByDescending(c => c.aliveParticles).First())
            .ToList();

        int direct = unique.Count(c => string.Equals(c.confidence, "direct", StringComparison.OrdinalIgnoreCase));
        int high = unique.Count(c => string.Equals(c.confidence, "inferred_high", StringComparison.OrdinalIgnoreCase));
        int medium = unique.Count(c => string.Equals(c.confidence, "inferred_medium", StringComparison.OrdinalIgnoreCase));
        int low = unique.Count(c => string.Equals(c.confidence, "inferred_low", StringComparison.OrdinalIgnoreCase));
        int strong = unique.Count(IsStrongParticleTopCandidate);

        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": {");
        WriteJsonProperty(sb, "mappedEventCount", candidates.Select(x => x.eventIndex).Distinct().Count(), true, indent + 2);
        WriteJsonProperty(sb, "uniqueParticleCandidateCount", unique.Count, true, indent + 2);
        WriteJsonProperty(sb, "strongTopCandidateCount", strong, true, indent + 2);
        WriteJsonProperty(sb, "directCount", direct, true, indent + 2);
        WriteJsonProperty(sb, "inferredHighCount", high, true, indent + 2);
        WriteJsonProperty(sb, "inferredMediumCount", medium, true, indent + 2);
        WriteJsonProperty(sb, "inferredLowCount", low, true, indent + 2);
        WriteJsonProperty(sb, "topListFilter", "topActiveParticlesInFrame requires aliveParticles>0, confidence direct/inferred_high/inferred_medium, matchScore>=70, and material+shader or direct renderer evidence.", true, indent + 2);
        sb.Append(pad).Append("  \"lowConfidenceActiveParticles\": [");
        var lowRows = unique
            .Where(c => !IsStrongParticleTopCandidate(c))
            .OrderByDescending(c => c.aliveParticles)
            .ThenByDescending(c => c.matchScore)
            .ThenBy(c => c.path)
            .Take(5)
            .ToList();
        for (int i = 0; i < lowRows.Count; i++)
        {
            var c = lowRows[i];
            if (i > 0) sb.Append(", ");
            sb.Append("{");
            AppendJsonPropertyInline(sb, "candidateId", c.id, true);
            AppendJsonPropertyInline(sb, "path", c.path, true);
            AppendJsonPropertyInline(sb, "materialName", c.materialName, true);
            AppendJsonPropertyInline(sb, "shaderName", c.shaderName, true);
            AppendJsonPropertyInline(sb, "aliveParticleCount", c.aliveParticles, true);
            AppendJsonPropertyInline(sb, "matchScore", c.matchScore, true);
            AppendJsonPropertyInline(sb, "confidence", c.confidence, true);
            AppendStringArrayInline(sb, "matchedBy", c.matchedBy, false);
            sb.Append("}");
        }
        sb.AppendLine("],");
        WriteJsonProperty(sb, "sourceFile", "ai_event_evidence.jsonl", false, indent + 2);
        sb.Append(pad).Append("}");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static bool IsStrongParticleTopCandidate(ParticleCandidate candidate)
    {
        if (candidate == null) return false;
        if (candidate.aliveParticles <= 0) return false;
        if (string.Equals(candidate.confidence, "direct", StringComparison.OrdinalIgnoreCase))
            return candidate.matchScore >= 70;
        if (string.Equals(candidate.confidence, "inferred_low", StringComparison.OrdinalIgnoreCase)) return false;
        bool hasMaterial = !string.IsNullOrEmpty(candidate.materialName) || !string.IsNullOrEmpty(candidate.materialPath);
        bool hasShader = !string.IsNullOrEmpty(candidate.shaderName);
        bool matchedMaterial = candidate.matchedBy != null && (candidate.matchedBy.Contains("materialPath") || candidate.matchedBy.Contains("materialName"));
        bool matchedShader = candidate.matchedBy != null && candidate.matchedBy.Contains("shaderName");
        return candidate.matchScore >= 70 && hasMaterial && hasShader && matchedMaterial && matchedShader;
    }

    private static void AppendTopParticleMaterialTable(StringBuilder sb, string name, List<FrameEventInfo> events, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        var rows = BuildParticleCandidateMap(events)
            .SelectMany(kv => kv.Value.Select(c => new { eventIndex = kv.Key + 1, candidate = c }))
            .GroupBy(x => !string.IsNullOrEmpty(x.candidate.materialPath) ? x.candidate.materialPath : x.candidate.materialName)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g =>
            {
                var uniqueParticles = g.Select(x => x.candidate).GroupBy(c => c.id).Select(pg => pg.First()).ToList();
                return new
                {
                    material = g.Key,
                    shaderName = uniqueParticles.Select(c => c.shaderName).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "",
                    particleSystemCount = uniqueParticles.Count,
                    aliveParticleCount = uniqueParticles.Sum(c => Math.Max(0, c.aliveParticles)),
                    mappedEventCount = g.Select(x => x.eventIndex).Distinct().Count(),
                    eventIndexes = g.Select(x => x.eventIndex.ToString(CultureInfo.InvariantCulture)).Distinct().Take(10).ToArray()
                };
            })
            .OrderByDescending(r => r.aliveParticleCount)
            .ThenByDescending(r => r.mappedEventCount)
            .ThenBy(r => r.material)
            .Take(10)
            .ToList();

        sb.Append(pad).Append('"').Append(EscapeJson(name)).AppendLine("\": [");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            sb.Append(pad).Append("  {");
            AppendJsonPropertyInline(sb, "material", row.material, true);
            AppendJsonPropertyInline(sb, "shaderName", row.shaderName, true);
            AppendJsonPropertyInline(sb, "particleSystemCount", row.particleSystemCount, true);
            AppendJsonPropertyInline(sb, "aliveParticleCount", row.aliveParticleCount, true);
            AppendJsonPropertyInline(sb, "mappedEventCount", row.mappedEventCount, true);
            AppendStringArrayInline(sb, "eventIndexes", row.eventIndexes, true);
            AppendJsonPropertyInline(sb, "sourceFile", "ai_event_evidence.jsonl", false);
            sb.Append(i + 1 < rows.Count ? "}," : "}");
            sb.AppendLine();
        }
        sb.Append(pad).Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendStringArray(StringBuilder sb, string name, string[] values, bool trailingComma, int indent = 2)
    {
        string pad = new string(' ', indent);
        sb.Append(pad).Append('"').Append(EscapeJson(name)).Append("\": ");
        AppendStringArrayLiteral(sb, values);
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendStringArrayInline(StringBuilder sb, string name, string[] values, bool trailingComma)
    {
        sb.Append('"').Append(EscapeJson(name)).Append("\": ");
        AppendStringArrayLiteral(sb, values);
        if (trailingComma) sb.Append(", ");
    }

    private static void AppendStringArrayLiteral(StringBuilder sb, string[] values)
    {
        sb.Append("[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Json(values[i]));
        }
        sb.Append("]");
    }

    private static void AppendDictionaryInline(StringBuilder sb, string name, Dictionary<string, string> dict, bool trailingComma)
    {
        sb.Append('"').Append(EscapeJson(name)).Append("\": {");
        if (dict != null)
        {
            int i = 0;
            foreach (var kv in dict.OrderBy(kv => kv.Key))
            {
                if (i++ > 0) sb.Append(", ");
                sb.Append(Json(kv.Key)).Append(": ").Append(Json(kv.Value));
            }
        }
        sb.Append("}");
        if (trailingComma) sb.Append(", ");
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, string value, bool trailingComma, int indent = 2)
    {
        sb.Append(new string(' ', indent)).Append('"').Append(EscapeJson(name)).Append("\": ").Append(Json(value));
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, int value, bool trailingComma, int indent = 2)
    {
        sb.Append(new string(' ', indent)).Append('"').Append(EscapeJson(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, long value, bool trailingComma, int indent = 2)
    {
        sb.Append(new string(' ', indent)).Append('"').Append(EscapeJson(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, bool value, bool trailingComma, int indent = 2)
    {
        sb.Append(new string(' ', indent)).Append('"').Append(EscapeJson(name)).Append("\": ").Append(value ? "true" : "false");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendJsonPropertyInline(StringBuilder sb, string name, string value, bool trailingComma)
    {
        sb.Append('"').Append(EscapeJson(name)).Append("\": ").Append(Json(value));
        if (trailingComma) sb.Append(", ");
    }

    private static void AppendJsonPropertyInline(StringBuilder sb, string name, int value, bool trailingComma)
    {
        sb.Append('"').Append(EscapeJson(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (trailingComma) sb.Append(", ");
    }

    private static void AppendJsonPropertyInline(StringBuilder sb, string name, bool value, bool trailingComma)
    {
        sb.Append('"').Append(EscapeJson(name)).Append("\": ").Append(value ? "true" : "false");
        if (trailingComma) sb.Append(", ");
    }

    private static string Json(string value)
    {
        return "\"" + EscapeJson(value ?? "") + "\"";
    }

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void WriteUtf8NoBom(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string ToProjectRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        string normalized = path.Replace("\\", "/");
        string project = Directory.GetCurrentDirectory().Replace("\\", "/").TrimEnd('/');
        if (normalized.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase))
            return normalized.Substring(project.Length + 1);
        return normalized;
    }

    private string BuildMarkdownContent()
    {
        var sb = new StringBuilder(8192);

        // Header
        sb.AppendLine("# Frame Debugger Export");
        sb.AppendLine();
        sb.AppendLine($"- **导出时间**: {_captureTimestamp}");
        sb.AppendLine($"- **Unity 版本**: {Application.unityVersion}");
        sb.AppendLine($"- **捕获场景**: {GetCaptureSceneNameForMetadata()}");
        sb.AppendLine($"- **场景来源**: {GetCaptureSceneSourceForMetadata()}");
        if (_linkedCaptureContext != null)
            sb.AppendLine($"- **导出时 Editor 场景**: {_linkedCaptureContext.editorSceneAtExport}（不是远端模拟器捕获场景）");
        sb.AppendLine($"- **总事件数**: {_capturedEvents.Count}");
        sb.AppendLine();

        // Section 1: Render Order
        AppendRenderOrderSection(sb);

        // Section 2: Pass Statistics
        AppendPassStatsSection(sb);

        // Section 3: Detailed Events
        AppendDetailSection(sb);

        return sb.ToString();
    }

    private void AppendRenderOrderSection(StringBuilder sb)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 1. 渲染顺序列表");
        sb.AppendLine();
        sb.AppendLine("| # | Event | Type | Shader | Pass | RenderTarget | Vertices | Indices |");
        sb.AppendLine("|---|-------|------|--------|------|-------------|----------|---------|");

        foreach (var evt in _capturedEvents)
        {
            string rtInfo = !string.IsNullOrEmpty(evt.renderTargetName)
                ? $"{evt.renderTargetName}"
                : "-";
            if (evt.renderTargetWidth > 0 && evt.renderTargetHeight > 0)
                rtInfo += $" ({evt.renderTargetWidth}x{evt.renderTargetHeight})";

            sb.AppendLine($"| {evt.index + 1} " +
                          $"| {EscapeMd(evt.eventName)} " +
                          $"| {EscapeMd(evt.typeName)} " +
                          $"| {EscapeMd(evt.shaderName)} " +
                          $"| {EscapeMd(evt.passName)} " +
                          $"| {EscapeMd(rtInfo)} " +
                          $"| {(evt.vertexCount > 0 ? evt.vertexCount.ToString("N0") : "-")} " +
                          $"| {(evt.indexCount > 0 ? evt.indexCount.ToString("N0") : "-")} |");
        }
        sb.AppendLine();
    }

    private void AppendPassStatsSection(StringBuilder sb)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 2. Pass 统计");
        sb.AppendLine();

        if (_passStats == null || _passStats.Count == 0)
        {
            sb.AppendLine("*(无统计数据)*");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Pass Name | Draw Calls | Total Vertices | Total Indices | Shaders |");
        sb.AppendLine("|-----------|-----------|---------------|--------------|---------|");

        var sorted = _passStats.Values.OrderByDescending(s => s.drawCallCount).ToList();
        foreach (var stat in sorted)
        {
            string shaders = stat.shaderNames.Count > 3
                ? string.Join(", ", stat.shaderNames.Take(3)) + $" ... (+{stat.shaderNames.Count - 3})"
                : string.Join(", ", stat.shaderNames);

            sb.AppendLine($"| {EscapeMd(stat.passName)} " +
                          $"| {stat.drawCallCount} " +
                          $"| {stat.totalVertices:N0} " +
                          $"| {stat.totalIndices:N0} " +
                          $"| {EscapeMd(shaders)} |");
        }

        sb.AppendLine();

        // Totals
        long totalVerts = sorted.Sum(s => s.totalVertices);
        long totalIndices = sorted.Sum(s => s.totalIndices);
        int totalDrawCalls = sorted.Sum(s => s.drawCallCount);
        sb.AppendLine($"**合计**: {totalDrawCalls} Draw Calls, {totalVerts:N0} Vertices, {totalIndices:N0} Indices");
        sb.AppendLine();
    }

    private void AppendDetailSection(StringBuilder sb)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 3. 详细事件信息");
        sb.AppendLine();

        foreach (var evt in _capturedEvents)
        {
            sb.AppendLine($"### Event #{evt.index + 1}: {EscapeMd(evt.eventName)}");
            sb.AppendLine();
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");

            AppendRow(sb, "Type", evt.typeName);
            AppendRow(sb, "Shader", evt.shaderName);
            AppendRow(sb, "Pass", !string.IsNullOrEmpty(evt.passName)
                ? (evt.passIndex >= 0 ? $"{evt.passName} (index: {evt.passIndex})" : evt.passName)
                : "-");
            if (!string.IsNullOrEmpty(evt.passLightMode))
                AppendRow(sb, "LightMode", evt.passLightMode);

            string rtInfo = !string.IsNullOrEmpty(evt.renderTargetName) ? evt.renderTargetName : "-";
            if (evt.renderTargetWidth > 0 && evt.renderTargetHeight > 0)
                rtInfo += $" ({evt.renderTargetWidth}x{evt.renderTargetHeight})";
            AppendRow(sb, "Render Target", rtInfo);

            if (evt.vertexCount > 0) AppendRow(sb, "Vertex Count", evt.vertexCount.ToString("N0"));
            if (evt.indexCount > 0) AppendRow(sb, "Index Count", evt.indexCount.ToString("N0"));
            if (evt.instanceCount > 0) AppendRow(sb, "Instance Count", evt.instanceCount.ToString("N0"));
            if (evt.drawCallCount > 0) AppendRow(sb, "Draw Call Count", evt.drawCallCount.ToString("N0"));
            if (!string.IsNullOrEmpty(evt.shaderKeywords)) AppendRow(sb, "Keywords", evt.shaderKeywords);
            if (!string.IsNullOrEmpty(evt.batchBreakCause)) AppendRow(sb, "Batch Break", evt.batchBreakCause);
            if (!string.IsNullOrEmpty(evt.gameObjectName)) AppendRow(sb, "GameObject", evt.gameObjectName);
            if (!string.IsNullOrEmpty(evt.gameObjectPath)) AppendRow(sb, "GameObject Path", evt.gameObjectPath);
            if (!string.IsNullOrEmpty(evt.rendererType)) AppendRow(sb, "Renderer", evt.rendererType);
            if (!string.IsNullOrEmpty(evt.rendererMaterials)) AppendRow(sb, "Renderer Materials", evt.rendererMaterials);
            if (!string.IsNullOrEmpty(evt.meshName)) AppendRow(sb, "Mesh", evt.meshName);
            if (!string.IsNullOrEmpty(evt.meshAssetPath)) AppendRow(sb, "Mesh Path", evt.meshAssetPath);
            if (!string.IsNullOrEmpty(evt.materialName)) AppendRow(sb, "Resolved Material", evt.materialName);
            if (!string.IsNullOrEmpty(evt.materialAssetPath)) AppendRow(sb, "Material Path", evt.materialAssetPath);
            if (!string.IsNullOrEmpty(evt.materialProperties)) AppendRow(sb, "Material Properties", evt.materialProperties);
            if (!string.IsNullOrEmpty(evt.resolvedShaderName)) AppendRow(sb, "Resolved Shader", evt.resolvedShaderName);
            if (!string.IsNullOrEmpty(evt.resolvedShaderAssetPath)) AppendRow(sb, "Shader Path", evt.resolvedShaderAssetPath);
            if (!string.IsNullOrEmpty(evt.resolvedShaderPassSummary)) AppendRow(sb, "Shader Pass Summary", evt.resolvedShaderPassSummary);
            if (evt.materialRenderQueue >= 0) AppendRow(sb, "Material RenderQueue", evt.materialRenderQueue.ToString());
            if (evt.meshSubset >= 0) AppendRow(sb, "Mesh Subset", evt.meshSubset.ToString());

            // Extra fields
            foreach (var kv in evt.extraFields)
            {
                AppendRow(sb, kv.Key, kv.Value);
            }

            sb.AppendLine();
        }
    }

    private static void AppendRow(StringBuilder sb, string prop, string val)
    {
        if (string.IsNullOrEmpty(val) || val == "-") return;
        sb.AppendLine($"| **{EscapeMd(prop)}** | {EscapeMd(val)} |");
    }

    private static string EscapeMd(string s)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        return s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
    }

    #endregion

    #region Export Confirm Window

    private sealed class FrameDebuggerAIExportConfirmWindow : EditorWindow
    {
        private bool autoAnalyze;

        public static void Open()
        {
            var window = CreateInstance<FrameDebuggerAIExportConfirmWindow>();
            window.titleContent = new GUIContent("Frame Debugger AI 导出确认");
            window.autoAnalyze = EditorPrefs.GetBool(AutoAnalyzeAfterExportPrefKey, false);
            window.minSize = new Vector2(460, 158);
            window.maxSize = new Vector2(680, 240);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            string exportRoot = GetDefaultAiExportRoot();
            EditorGUILayout.LabelField("Frame Debugger AI 导出", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "将自动读取当前 Frame Debugger 捕获事件，导出完整 AI Bundle 到固定目录。\n" +
                "输出目录：" + exportRoot,
                MessageType.Info);
            autoAnalyze = EditorGUILayout.ToggleLeft("生成后自动调用 AI 工具分析", autoAnalyze);

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("取消", GUILayout.Width(96)))
                Close();
            if (GUILayout.Button("开始导出", GUILayout.Width(120)))
            {
                EditorPrefs.SetBool(AutoAnalyzeAfterExportPrefKey, autoAnalyze);
                Close();
                CaptureAndExportAiBundleToDefaultDirectoryAsync(autoAnalyze);
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    private sealed class FrozenFrameLinkedCaptureWindow : EditorWindow
    {
        private bool autoAnalyze;
        private string targetMatch = "";
        private string targetUrl = "";

        public static void Open()
        {
            var window = CreateInstance<FrozenFrameLinkedCaptureWindow>();
            window.titleContent = new GUIContent("RenderDoc 联动帧导出");
            window.autoAnalyze = EditorPrefs.GetBool(AutoAnalyzeAfterExportPrefKey, false);
            window.targetMatch = EditorPrefs.GetString(RenderDocTargetMatchPrefKey, "");
            window.targetUrl = EditorPrefs.GetString(RenderDocTargetUrlPrefKey, "");
            window.minSize = new Vector2(600, 260);
            window.maxSize = new Vector2(860, 380);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("RenderDoc + Frame Debugger 联动帧导出", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "前提：游戏/模拟器已经通过 RenderDoc 启动或注入，并且 Unity 能发现同一个 Development Player。\n\n" +
                "点击下面按钮后会自动：尝试选择 Unity 远端 Player -> 启用 Frame Debugger 冻结远端帧 -> 触发 RenderDoc 抓帧并复制 .rdc -> 解析 RenderDoc AI 索引 -> 导出 Frame Debugger AI Bundle。",
                MessageType.Info);

            targetMatch = EditorGUILayout.TextField(new GUIContent("RenderDoc 目标匹配", "可空。多目标时填进程名、包名、PID、API 或 URL 的一部分。"), targetMatch);
            targetUrl = EditorGUILayout.TextField(new GUIContent("RenderDoc URL", "可空。必要时填 localhost、adb://device 等。"), targetUrl);
            autoAnalyze = EditorGUILayout.ToggleLeft("导出后自动调用 AI 工具分析", autoAnalyze);

            GUILayout.Space(10);
            if (GUILayout.Button("一键完成 RenderDoc 抓帧解析 + Frame Debugger 导出", GUILayout.Height(34)))
            {
                EditorPrefs.SetBool(AutoAnalyzeAfterExportPrefKey, autoAnalyze);
                EditorPrefs.SetString(RenderDocTargetMatchPrefKey, targetMatch ?? "");
                EditorPrefs.SetString(RenderDocTargetUrlPrefKey, targetUrl ?? "");
                Close();
                CaptureRenderDocAndExportLinkedFrameDebuggerAsync(autoAnalyze, targetMatch, targetUrl);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("关闭", GUILayout.Width(96)))
                Close();
            EditorGUILayout.EndHorizontal();
        }
    }

    #endregion
}

[InitializeOnLoad]
public static class FrameDebuggerAiExportToolbarInstaller
{
    private const string ButtonName = "FrameDebuggerAiExportButton";
    private const string ButtonLayerName = "FrameDebuggerAiExportButtonLayer";
    private static double nextInstallTime;

    static FrameDebuggerAiExportToolbarInstaller()
    {
        EditorApplication.delayCall += () => InstallIfPossible();
        EditorApplication.update += OnEditorUpdate;
    }

    private static void OnEditorUpdate()
    {
        if (EditorApplication.timeSinceStartup < nextInstallTime)
            return;

        nextInstallTime = EditorApplication.timeSinceStartup + 1.0;
        InstallIfPossible();
    }

    private static bool InstallIfPossible()
    {
        Type frameDebuggerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.FrameDebuggerWindow")
                                      ?? typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.FrameDebuggerWindow");
        if (frameDebuggerWindowType == null)
            return false;

        var windows = Resources.FindObjectsOfTypeAll(frameDebuggerWindowType);
        bool hasWindow = false;
        foreach (var windowObject in windows)
        {
            var window = windowObject as EditorWindow;
            if (window == null || window.rootVisualElement == null)
                continue;

            hasWindow = true;
            if (window.rootVisualElement.Q<VisualElement>(ButtonLayerName) != null)
                continue;

            var button = new Button(FrameDebuggerExport.ShowAiExportConfirmWindow)
            {
                name = ButtonName,
                text = "AI",
                tooltip = "导出 Frame Debugger 捕获供 AI 分析。"
            };
            button.style.width = 30;
            button.style.minWidth = 30;
            button.style.height = 18;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.pickingMode = PickingMode.Position;

            var layer = new VisualElement
            {
                name = ButtonLayerName,
                pickingMode = PickingMode.Ignore
            };
            layer.style.position = Position.Absolute;
            layer.style.top = 2;
            layer.style.left = 6;
            layer.style.width = 32;
            layer.style.height = 20;
            layer.style.flexDirection = FlexDirection.Row;
            layer.style.justifyContent = Justify.Center;
            layer.style.alignItems = Align.Center;
            layer.style.display = DisplayStyle.Flex;

            layer.Add(button);
            window.rootVisualElement.Add(layer);
        }

        return hasWindow;
    }
}
