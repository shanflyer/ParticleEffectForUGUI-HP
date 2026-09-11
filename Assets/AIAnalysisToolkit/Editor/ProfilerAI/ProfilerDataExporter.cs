using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public static class ProfilerDataExporter
{
    private const string DefaultInputPath = "profile.data";
    private const int MaxThreadProbeCount = 128;
    private const int SummaryTopMarkerCount = 64;
    private const int DigestTopMarkerCount = 40;
    private const int DigestTopFrameCount = 30;
    private const int DigestTopSamplePerFrameCount = 30;
    private const double ContextMinSelfMs = 0.02;
    private const double ContextMinTotalMs = 0.2;
    private const double WrapperContextMinTotalMs = 3.0;
    private const double TinySampleSkipMaxMs = 0.0001;
    private const double TinySubtreeSkipMaxMs = 0.002;
    private const double TopSampleMinSelfMs = 0.001;
    private const double TopSampleMinTotalMs = 0.01;
    private const int SampleProgressMask = 0xFFFF;
    private const int EstimatedSampleTsvFixedChars = 64;
    private static readonly bool WriteFullMarkerJsonl = false;
    private const string RawFrameExtractorPath = "profiler_export_frame.py";
    private const string RawFrameCommandPath = "extract_raw_frame.cmd";
    private const string ProjectRawFrameCommandPath = "Tools/ProfilerAI/extract_raw_frame.cmd";
    private const string RawFrameExtractorTemplatePath = "Assets/AIAnalysisToolkit/Editor/ProfilerAI/profiler_export_frame.py";
    private const string RawFrameRequestPath = "ProfilerAIExports/raw_frame_request.json";
    private const string RawFrameRequestResultPath = "ProfilerAIExports/raw_frame_request_result.json";
    private const string RawFrameRequestStatusPath = "ProfilerAIExports/raw_frame_request_status.json";
    private const string AutoAnalyzeAfterExportPrefKey = "ProfilerAI.Export.AutoAnalyzeAfterExport";
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static bool exportCancelRequested;
    private static bool rawFrameRequestProcessing;

    private sealed class ExportContext
    {
        public string SourceProfilePath;
        public string OutputDir;
        public bool IncludeRawSamples;
        public string CaptureScope;
        public int SourceFirstFrame;
        public int SourceLastFrame;
        public int FirstFrame;
        public int LastFrame;
        public bool Canceled;
        public int FrameCount;
        public int ThreadRowCount;
        public int SampleRowCount;
        public int ExportedSampleRowCount;
        public int NoiseSampleRowCount;
        public int NoiseSubtreeRootCount;
        public int TinySampleRowCount;
        public int TinySubtreeRootCount;
        public long EstimatedSampleTsvChars;
        public long EstimatedExportedSampleTsvChars;
        public long EstimatedNoiseSampleTsvChars;
        public double NoiseTotalMs;
        public double NoiseSelfMs;
        public double TinySampleTotalMs;
        public double TinySampleSelfMs;
        public long FrameParseAndQueueMs;
        public long RawWriterFinalizeMs;
        public long SummaryWriteMs;
        public long TotalExportMs;
        public double TimingGetViewMs;
        public double TimingReadSamplesMs;
        public double TimingTreeMs;
        public double TimingAggregateMs;
        public double TimingRawTsvBuildMs;
        public double TimingWriterEnqueueMs;
        public double TimingDisposeViewMs;
        public double TotalThreadMs;
        public double MainThreadMs;
        public double RenderThreadMs;
        public readonly Dictionary<string, MarkerStat> MarkerStats = new Dictionary<string, MarkerStat>();
        public readonly Dictionary<MarkerContextKey, MarkerContextStat> MarkerContextStats = new Dictionary<MarkerContextKey, MarkerContextStat>();
        public readonly Dictionary<int, FrameDigestStat> FrameStats = new Dictionary<int, FrameDigestStat>();
        public readonly Dictionary<string, MarkerNameInfo> MarkerNameInfoCache = new Dictionary<string, MarkerNameInfo>();
        public readonly Dictionary<CallPathCacheKey, string> CallPathCache = new Dictionary<CallPathCacheKey, string>();
    }

    private sealed class MarkerStat
    {
        public string Name;
        public double TotalMs;
        public double SelfMs;
        public double MaxSelfMs;
        public double MaxTotalMs;
        public int MaxSelfFrame;
        public int MaxTotalFrame;
        public int Count;
        public int FirstFrame = int.MaxValue;
        public int LastFrame = int.MinValue;
    }

    private sealed class MarkerContextStat
    {
        public string Name;
        public string ThreadName;
        public string CallPath;
        public double TotalMs;
        public double SelfMs;
        public double MaxSelfMs;
        public double MaxTotalMs;
        public int MaxSelfFrame;
        public int MaxTotalFrame;
        public double MaxChildrenMs;
        public string MaxChildrenMarkerHint;
        public int Count;
        public int FirstFrame = int.MaxValue;
        public int LastFrame = int.MinValue;
        public int MaxFrame;
        public int MaxThreadIndex;
        public int MaxSampleIndex;
    }

    private struct MarkerContextKey : IEquatable<MarkerContextKey>
    {
        public readonly string ThreadName;
        public readonly string CallPath;
        private readonly int hash;

        public MarkerContextKey(string threadName, string callPath)
        {
            ThreadName = threadName ?? string.Empty;
            CallPath = callPath ?? string.Empty;
            unchecked
            {
                hash = (ThreadName.GetHashCode() * 397) ^ CallPath.GetHashCode();
            }
        }

        public bool Equals(MarkerContextKey other)
        {
            return string.Equals(ThreadName, other.ThreadName, StringComparison.Ordinal) &&
                   string.Equals(CallPath, other.CallPath, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is MarkerContextKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return hash;
        }
    }

    private sealed class MarkerNameInfo
    {
        public string Name;
        public bool IsNoise;
        public bool IsPlayerLoop;
        public bool IsEditorLoop;
        public bool IsProfiler;
        public bool IsEditorConnection;
        public bool IsNoiseSubtree;
        public bool IsIdle;
        public bool IsWait;
        public bool IsGfxWait;
        public bool IsRenderPipeline;
        public bool IsScriptableRenderer;
        public bool IsTransparent;
        public bool IsShadows;
        public bool IsSrpBatcher;
        public bool IsCulling;
        public bool IsBrg;
        public bool IsParticle;
        public bool IsGc;
        public bool ForceContextIndex;
        public int TsvEscapedNameChars;
        public MarkerStat MarkerStat;
    }

    private struct CallPathCacheKey : IEquatable<CallPathCacheKey>
    {
        public int Count;
        public string N0;
        public string N1;
        public string N2;
        public string N3;
        public string N4;
        public string N5;
        public string N6;
        public string N7;
        public string N8;
        public string N9;
        public int Hash;

        public bool Equals(CallPathCacheKey other)
        {
            return Count == other.Count &&
                   string.Equals(N0, other.N0, StringComparison.Ordinal) &&
                   string.Equals(N1, other.N1, StringComparison.Ordinal) &&
                   string.Equals(N2, other.N2, StringComparison.Ordinal) &&
                   string.Equals(N3, other.N3, StringComparison.Ordinal) &&
                   string.Equals(N4, other.N4, StringComparison.Ordinal) &&
                   string.Equals(N5, other.N5, StringComparison.Ordinal) &&
                   string.Equals(N6, other.N6, StringComparison.Ordinal) &&
                   string.Equals(N7, other.N7, StringComparison.Ordinal) &&
                   string.Equals(N8, other.N8, StringComparison.Ordinal) &&
                   string.Equals(N9, other.N9, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is CallPathCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Hash;
        }
    }

    private sealed class CallPathTokenTable
    {
        public readonly List<string> Tokens = new List<string>();
        private readonly Dictionary<string, int> tokenToIndex = new Dictionary<string, int>();

        public int GetIndex(string token)
        {
            token = string.IsNullOrEmpty(token) ? "<unnamed>" : token;
            if (tokenToIndex.TryGetValue(token, out int index))
                return index;

            index = Tokens.Count;
            Tokens.Add(token);
            tokenToIndex.Add(token, index);
            return index;
        }
    }

    private sealed class StringTable
    {
        public readonly List<string> Values = new List<string>();
        private readonly Dictionary<string, int> valueToIndex = new Dictionary<string, int>();

        public int GetIndex(string value)
        {
            value = value ?? string.Empty;
            if (valueToIndex.TryGetValue(value, out int index))
                return index;

            index = Values.Count;
            Values.Add(value);
            valueToIndex.Add(value, index);
            return index;
        }
    }

    private sealed class SuspectedHotspot
    {
        public MarkerContextStat Stat;
        public int SelfMsRank;
        public int TotalMsRank;
        public string Reason;
        public double Score;
        public bool AlreadyInTopContexts;
    }

    private sealed class SampleRef
    {
        public int ThreadIndex;
        public string ThreadName;
        public int SampleIndex;
        public int ParentIndex;
        public int Depth;
        public string Name;
        public double TotalMs;
        public double SelfMs;
        public int ChildCount;
    }

    private sealed class FrameDigestStat
    {
        public int Frame;
        public int ThreadCount;
        public int SampleCount;
        public double MainThreadMs;
        public double RenderThreadMs;
        public double PlayerLoopMs;
        public double EditorLoopMs;
        public double ProfilerMs;
        public double IdleMs;
        public double WaitMs;
        public double GfxWaitMs;
        public double RenderPipelineMs;
        public double ScriptableRendererMs;
        public double TransparentMs;
        public double ShadowsMs;
        public double SrpBatcherMs;
        public double CullingMs;
        public double BrgMs;
        public double ParticleMs;
        public double GcMs;
        public double MainThreadActionableSelfMs;
        public readonly List<SampleRef> TopActionableSamples = new List<SampleRef>();

        public double MainThreadNoiseAdjustedMs
        {
            get
            {
                return MainThreadActionableSelfMs;
            }
        }
    }

    private sealed class ExportOptions
    {
        public bool IncludeRawSamples;
        public bool UseFrameRange;
        public int FirstFrame;
        public int LastFrame;
        public string CaptureScope;

        public static ExportOptions Full()
        {
            return new ExportOptions { IncludeRawSamples = false, CaptureScope = "full" };
        }

        public static ExportOptions RawFrameRange(int firstFrame, int lastFrame)
        {
            return new ExportOptions
            {
                IncludeRawSamples = true,
                UseFrameRange = true,
                FirstFrame = firstFrame,
                LastFrame = lastFrame,
                CaptureScope = "rawFrameRange"
            };
        }

        public static ExportOptions CurrentProfilerView(int firstFrame, int lastFrame)
        {
            return new ExportOptions
            {
                IncludeRawSamples = false,
                UseFrameRange = true,
                FirstFrame = firstFrame,
                LastFrame = lastFrame,
                CaptureScope = "currentProfilerView"
            };
        }
    }

    [Serializable]
    private sealed class RawFrameRequest
    {
        public string exportDir;
        public string profilePath;
        public int[] frames;
        public int frame = -1;
        public int firstFrame = -1;
        public int lastFrame = -1;
        public string outDir;
    }

    [Serializable]
    private sealed class ExportMetadata
    {
        public string sourceProfilePath;
    }

    private sealed class RawFrameDataViewApi
    {
        private readonly Func<object, bool> valid;
        private readonly Func<object, int> sampleCount;
        private readonly Func<object, string> threadName;
        private readonly Func<object, string> threadGroupName;
        private readonly Func<object, int, string> getSampleName;
        private readonly Func<object, int, double> getSampleTimeMs;
        private readonly Func<object, int, int> getSampleChildrenCount;
        private readonly Func<object, int, int> getSampleChildrenCountRecursive;

        public RawFrameDataViewApi(Type type)
        {
            var validProperty = GetProperty(type, "valid");
            var sampleCountProperty = GetProperty(type, "sampleCount");
            var threadNameProperty = GetProperty(type, "threadName");
            var threadGroupNameProperty = GetProperty(type, "threadGroupName");
            var sampleNameMethod = FindInstanceMethod(type, "GetSampleName", typeof(int));
            var sampleTimeMethod = FindInstanceMethod(type, "GetSampleTimeMs", typeof(int)) ?? FindInstanceMethod(type, "GetSampleTimeMilliseconds", typeof(int));
            var sampleChildrenCountMethod = FindInstanceMethod(type, "GetSampleChildrenCount", typeof(int));
            var sampleChildrenCountRecursiveMethod = FindInstanceMethod(type, "GetSampleChildrenCountRecursive", typeof(int));

            if (validProperty == null || sampleCountProperty == null || sampleNameMethod == null || sampleTimeMethod == null || sampleChildrenCountMethod == null || sampleChildrenCountRecursiveMethod == null)
                throw new MissingMemberException(type.FullName, "Required RawFrameDataView member");

            valid = CompilePropertyGetter<bool>(type, validProperty);
            sampleCount = CompilePropertyGetter<int>(type, sampleCountProperty);
            threadName = threadNameProperty == null ? EmptyStringGetter : CompilePropertyGetter<string>(type, threadNameProperty);
            threadGroupName = threadGroupNameProperty == null ? EmptyStringGetter : CompilePropertyGetter<string>(type, threadGroupNameProperty);
            getSampleName = CompileIntMethod<string>(type, sampleNameMethod);
            getSampleTimeMs = CompileIntMethod<double>(type, sampleTimeMethod);
            getSampleChildrenCount = CompileIntMethod<int>(type, sampleChildrenCountMethod);
            getSampleChildrenCountRecursive = CompileIntMethod<int>(type, sampleChildrenCountRecursiveMethod);
        }

        public bool IsValid(object view)
        {
            return valid(view);
        }

        public int SampleCount(object view)
        {
            return sampleCount(view);
        }

        public string ThreadName(object view)
        {
            return threadName(view);
        }

        public string ThreadGroupName(object view)
        {
            return threadGroupName(view);
        }

        public string SampleName(object view, int sample)
        {
            return getSampleName(view, sample);
        }

        public double SampleTimeMs(object view, int sample)
        {
            return getSampleTimeMs(view, sample);
        }

        public int SampleChildrenCount(object view, int sample)
        {
            return getSampleChildrenCount(view, sample);
        }

        public int SampleChildrenCountRecursive(object view, int sample)
        {
            return getSampleChildrenCountRecursive(view, sample);
        }
    }

    private sealed class AsyncTextChunkWriter : IDisposable
    {
        private readonly BlockingCollection<string> queue = new BlockingCollection<string>(64);
        private readonly Thread thread;
        private readonly string path;
        private Exception workerException;

        public AsyncTextChunkWriter(string path)
        {
            this.path = path;
            thread = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "Profiler AI TSV Writer"
            };
            thread.Start();
        }

        public void Enqueue(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return;

            if (workerException != null)
                throw new IOException("Profiler export writer failed.", workerException);

            queue.Add(chunk);
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join();
            queue.Dispose();

            if (workerException != null)
                throw new IOException("Profiler export writer failed.", workerException);
        }

        private void WriteLoop()
        {
            try
            {
                using (var writer = new StreamWriter(path, false, Utf8NoBom, 1024 * 1024))
                {
                    foreach (string chunk in queue.GetConsumingEnumerable())
                        writer.Write(chunk);
                }
            }
            catch (Exception ex)
            {
                workerException = ex;
                queue.CompleteAdding();
            }
        }
    }

    private static readonly Dictionary<Type, RawFrameDataViewApi> RawFrameDataViewApis = new Dictionary<Type, RawFrameDataViewApi>();
    private static readonly Func<object, string> EmptyStringGetter = _ => string.Empty;

    [MenuItem("Tools/AI 分析/Profiler/保存当前 Profiler + AI 数据...")]
    public static void SaveCurrentCaptureAndExportAiData()
    {
        ShowExportConfirmWindow("完整索引导出（raw 按需）", "导出当前 Profiler 捕获的完整 AI 索引。raw 帧数据仍按需生成。", ExportOptions.Full());
    }

    public static void ShowSaveCurrentCaptureMenu(VisualElement anchor)
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("完整索引导出（raw 按需）"), false, () =>
            ShowExportConfirmWindow("完整索引导出（raw 按需）", "导出当前 Profiler 捕获的完整 AI 索引。raw 帧数据仍按需生成。", ExportOptions.Full()));

        if (TryGetCurrentProfilerWindowFrameRange(out int firstFrame, out int lastFrame))
        {
            string label = "只导出当前界面帧索引 (" +
                           firstFrame.ToString(CultureInfo.InvariantCulture) +
                           "-" +
                           lastFrame.ToString(CultureInfo.InvariantCulture) +
                           ")";
            menu.AddItem(new GUIContent(label), false, () =>
                ShowExportConfirmWindow(label, "只导出 Profiler 界面当前可见帧范围的 AI 索引。", ExportOptions.CurrentProfilerView(firstFrame, lastFrame)));
        }
        else
        {
            menu.AddDisabledItem(new GUIContent("只导出当前界面帧索引（没有可用帧）"));
        }

        if (anchor != null)
        {
            Rect rect = anchor.worldBound;
            menu.DropDown(rect);
        }
        else
        {
            menu.ShowAsContext();
        }
    }

    private static void ShowExportConfirmWindow(string title, string description, ExportOptions options)
    {
        ProfilerAIExportConfirmWindow.Open(title, description, options);
    }

    private sealed class ProfilerAIExportConfirmWindow : EditorWindow
    {
        private string exportTitle;
        private string description;
        private ExportOptions options;
        private bool autoAnalyze;

        public static void Open(string exportTitle, string description, ExportOptions options)
        {
            var window = CreateInstance<ProfilerAIExportConfirmWindow>();
            window.titleContent = new GUIContent("Profiler AI 导出确认");
            window.exportTitle = exportTitle;
            window.description = description;
            window.options = options;
            window.autoAnalyze = EditorPrefs.GetBool(AutoAnalyzeAfterExportPrefKey, false);
            window.minSize = new Vector2(420, 150);
            window.maxSize = new Vector2(620, 220);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(exportTitle ?? "Profiler AI 导出", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(description ?? string.Empty, MessageType.Info);
            autoAnalyze = EditorGUILayout.ToggleLeft("生成后自动调用 AI 工具分析", autoAnalyze);

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("取消", GUILayout.Width(96)))
                Close();
            if (GUILayout.Button("开始导出", GUILayout.Width(120)))
            {
                EditorPrefs.SetBool(AutoAnalyzeAfterExportPrefKey, autoAnalyze);
                var exportOptions = options ?? ExportOptions.Full();
                Close();
                SaveCurrentCaptureAndExportAiData(exportOptions, autoAnalyze ? ProfilerAIClaudeRunner.StartAnalysis : (Action<string>)null);
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    private static void SaveCurrentCaptureAndExportAiData(ExportOptions options)
    {
        SaveCurrentCaptureAndExportAiData(options, null);
    }

    private static void SaveCurrentCaptureAndExportAiData(ExportOptions options, Action<string> onExported)
    {
        CreateTimestampedExportPaths(out string outputDir, out string profilePath);

        try
        {
            var profilerDriverType = GetProfilerDriverType();
            TrySaveProfile(profilerDriverType, profilePath);
            var context = ExportLoadedProfile(profilePath, outputDir, options);
            EditorUtility.RevealInFinder(outputDir);
            Debug.Log(BuildExportFinishedMessage(context));
            if (onExported != null)
                onExported(outputDir);
        }
        catch (OperationCanceledException ex)
        {
            Debug.LogWarning(ex.Message + " 部分导出目录：" + outputDir);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Profiler AI 导出失败", ex.Message, "确定");
        }
    }

    [MenuItem("Tools/AI 分析/Profiler/从 .data 导出 AI 数据...")]
    public static void ExportAiDataFromFile()
    {
        ExportAiDataFromFile(ExportOptions.Full());
    }

    [MenuItem("Tools/AI 分析/Profiler/处理 AI Raw Frame 请求")]
    public static void ProcessRawFrameRequest()
    {
        ProcessRawFrameRequestInternal(true, true);
    }

    public static void ProcessRawFrameRequestIfPending()
    {
        if (rawFrameRequestProcessing || EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        string requestPath = Path.Combine(Directory.GetCurrentDirectory(), RawFrameRequestPath);
        if (!File.Exists(requestPath))
            return;

        DateTime lastWrite = File.GetLastWriteTimeUtc(requestPath);
        if ((DateTime.UtcNow - lastWrite).TotalSeconds < 1.0)
            return;

        string key = GetRawFrameRequestEditorPrefsKey();
        long processedTicks = 0;
        long.TryParse(EditorPrefs.GetString(key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out processedTicks);
        if (lastWrite.Ticks <= processedTicks)
            return;

        ProcessRawFrameRequestInternal(false, false);
    }

    public static void ProcessRawFrameRequestFromFileEvent()
    {
        if (rawFrameRequestProcessing || EditorApplication.isCompiling || EditorApplication.isUpdating)
            return;

        ProcessRawFrameRequestInternal(false, false);
    }

    public static void WriteRawFrameRequestStatus(bool profilerWindowOpen)
    {
        string statusPath = Path.Combine(Directory.GetCurrentDirectory(), RawFrameRequestStatusPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statusPath));
            var sb = new StringBuilder(256);
            sb.AppendLine("{");
            WriteJsonProperty(sb, "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
            WriteJsonProperty(sb, "profilerWindowOpen", profilerWindowOpen, true);
            WriteJsonProperty(sb, "autoProcessingActive", profilerWindowOpen && !rawFrameRequestProcessing && !EditorApplication.isCompiling && !EditorApplication.isUpdating, true);
            WriteJsonProperty(sb, "requestPath", RawFrameRequestPath, true);
            WriteJsonProperty(sb, "resultPath", RawFrameRequestResultPath, false);
            sb.AppendLine("}");
            WriteUtf8NoBom(statusPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogWarning("写入 Profiler AI Raw Frame 请求状态失败：" + ex.Message);
        }
    }

    private static void ProcessRawFrameRequestInternal(bool showDialogs, bool force)
    {
        string requestPath = Path.Combine(Directory.GetCurrentDirectory(), RawFrameRequestPath);
        if (!File.Exists(requestPath))
        {
            if (showDialogs)
                EditorUtility.DisplayDialog("缺少 Raw Frame 请求", "没有找到请求文件：\n" + requestPath, "确定");
            return;
        }

        if (rawFrameRequestProcessing)
            return;

        DateTime requestWriteTime = File.GetLastWriteTimeUtc(requestPath);
        if (!force)
        {
            string key = GetRawFrameRequestEditorPrefsKey();
            long processedTicks = 0;
            long.TryParse(EditorPrefs.GetString(key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out processedTicks);
            if (requestWriteTime.Ticks <= processedTicks)
                return;
        }

        rawFrameRequestProcessing = true;
        try
        {
            var request = ReadRawFrameRequest(File.ReadAllText(requestPath, Encoding.UTF8));
            if (request == null)
                throw new InvalidOperationException("Raw frame request JSON is empty or invalid.");

            string exportDir = ResolveProjectRelativePath(request.exportDir);
            if (string.IsNullOrEmpty(exportDir))
                throw new InvalidOperationException("Raw frame request must include `exportDir`.");
            if (!Directory.Exists(exportDir))
                throw new DirectoryNotFoundException(exportDir);

            string profilePath = string.IsNullOrEmpty(request.profilePath)
                ? FindSourceProfilePathFromExport(exportDir)
                : ResolveProjectRelativePath(request.profilePath);
            if (!File.Exists(profilePath))
                throw new FileNotFoundException("Profiler source data not found.", profilePath);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var outputDirs = new List<string>();
            int firstResultFrame = -1;
            int lastResultFrame = -1;
            int totalSampleRows = 0;
            LoadProfile(profilePath);

            if (request.frames != null && request.frames.Length > 0)
            {
                var uniqueFrames = request.frames.Where(f => f >= 0).Distinct().OrderBy(f => f).ToList();
                if (uniqueFrames.Count == 0)
                    throw new InvalidOperationException("Raw frame request `frames` array does not contain valid frame indexes.");

                firstResultFrame = uniqueFrames[0];
                lastResultFrame = uniqueFrames[uniqueFrames.Count - 1];
                foreach (int frame in uniqueFrames)
                {
                    string outputDir = Path.Combine(exportDir, "raw_frames", "frame_" + frame.ToString(CultureInfo.InvariantCulture));
                    string stagingDir = outputDir + ".partial_" + stamp;
                    Directory.CreateDirectory(Path.GetDirectoryName(stagingDir) ?? exportDir);
                    var context = ExportLoadedProfile(profilePath, stagingDir, ExportOptions.RawFrameRange(frame, frame));
                    ReplaceDirectoryAtomically(stagingDir, outputDir, stamp);
                    outputDirs.Add(outputDir);
                    totalSampleRows += context.ExportedSampleRowCount;
                }
            }
            else
            {
                int firstFrame = request.frame >= 0 ? request.frame : request.firstFrame;
                int lastFrame = request.frame >= 0 ? request.frame : (request.lastFrame >= 0 ? request.lastFrame : request.firstFrame);
                if (firstFrame < 0 || lastFrame < firstFrame)
                    throw new InvalidOperationException("Raw frame request must include `frame`, `frames`, or `firstFrame/lastFrame`.");

                string outputDir = string.IsNullOrEmpty(request.outDir)
                    ? Path.Combine(exportDir, "raw_frames", firstFrame == lastFrame ? "frame_" + firstFrame.ToString(CultureInfo.InvariantCulture) : "frames_" + firstFrame.ToString(CultureInfo.InvariantCulture) + "_" + lastFrame.ToString(CultureInfo.InvariantCulture))
                    : ResolveProjectRelativePath(request.outDir);

                string stagingDir = outputDir + ".partial_" + stamp;
                Directory.CreateDirectory(Path.GetDirectoryName(stagingDir) ?? exportDir);
                var context = ExportLoadedProfile(profilePath, stagingDir, ExportOptions.RawFrameRange(firstFrame, lastFrame));
                ReplaceDirectoryAtomically(stagingDir, outputDir, stamp);
                outputDirs.Add(outputDir);
                firstResultFrame = firstFrame;
                lastResultFrame = lastFrame;
                totalSampleRows = context.ExportedSampleRowCount;
            }

            WriteRawFrameRequestResult("ok", outputDirs, firstResultFrame, lastResultFrame, totalSampleRows, null);
            EditorPrefs.SetString(GetRawFrameRequestEditorPrefsKey(), requestWriteTime.Ticks.ToString(CultureInfo.InvariantCulture));
            if (showDialogs)
                EditorUtility.RevealInFinder(outputDirs.Count > 0 ? outputDirs[0] : exportDir);
            Debug.Log("Profiler raw frame request finished: " + string.Join(", ", outputDirs.Select(ToProjectRelativePath).ToArray()));
        }
        catch (Exception ex)
        {
            WriteRawFrameRequestResult("failed", new List<string>(), -1, -1, 0, ex.Message);
            EditorPrefs.SetString(GetRawFrameRequestEditorPrefsKey(), requestWriteTime.Ticks.ToString(CultureInfo.InvariantCulture));
            Debug.LogException(ex);
            if (showDialogs)
                EditorUtility.DisplayDialog("Raw Frame 请求失败", ex.Message, "确定");
        }
        finally
        {
            rawFrameRequestProcessing = false;
        }
    }

    private static void ExportAiDataFromFile(ExportOptions options)
    {
        string inputPath = EditorUtility.OpenFilePanel("加载 Unity Profiler 数据", string.Empty, "data");
        if (string.IsNullOrEmpty(inputPath))
            return;

        string outputDir = EditorUtility.SaveFolderPanel("Profiler AI 导出目录", Directory.GetCurrentDirectory(), "profiler_ai_export");
        if (string.IsNullOrEmpty(outputDir))
            return;

        try
        {
            LoadProfile(inputPath);
            var context = ExportLoadedProfile(inputPath, outputDir, options);
            EditorUtility.RevealInFinder(outputDir);
            Debug.Log(BuildExportFinishedMessage(context));
        }
        catch (OperationCanceledException ex)
        {
            Debug.LogWarning(ex.Message + " 部分导出目录：" + outputDir);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Profiler AI 导出失败", ex.Message, "确定");
        }
    }

    public static void Export()
    {
        string inputPath = ResolveProjectRelativePath(GetCommandLineValue("-profilerInput", DefaultInputPath));
        string outputDir = ResolveProjectRelativePath(GetCommandLineValue("-profilerOutput", CreateTimestampedOutputDir()));
        bool includeRawSamples = string.Equals(GetCommandLineValue("-profilerRawSamples", "false"), "true", StringComparison.OrdinalIgnoreCase);
        bool hasFirstFrame = TryGetCommandLineInt("-profilerFirstFrame", out int firstFrame);
        bool hasLastFrame = TryGetCommandLineInt("-profilerLastFrame", out int lastFrame);
        if (hasFirstFrame && !hasLastFrame)
            lastFrame = firstFrame;

        try
        {
            LoadProfile(inputPath);
            ExportOptions options;
            if (hasFirstFrame)
            {
                options = includeRawSamples ? ExportOptions.RawFrameRange(firstFrame, lastFrame) : ExportOptions.CurrentProfilerView(firstFrame, lastFrame);
            }
            else
            {
                options = includeRawSamples ? new ExportOptions { IncludeRawSamples = true, CaptureScope = "fullRaw" } : ExportOptions.Full();
            }

            ExportLoadedProfile(inputPath, outputDir, options);
        }
        finally
        {
            EditorApplication.Exit(0);
        }
    }

    public static void ExportLoadedProfile(string sourceProfilePath, string outputDir)
    {
        ExportLoadedProfile(sourceProfilePath, outputDir, ExportOptions.Full());
    }

    private static ExportContext ExportLoadedProfile(string sourceProfilePath, string outputDir, ExportOptions options)
    {
        try
        {
            return ExportLoadedProfileInternal(sourceProfilePath, outputDir, options);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static ExportContext ExportLoadedProfileInternal(string sourceProfilePath, string outputDir, ExportOptions options)
    {
        exportCancelRequested = false;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(outputDir);
        DeleteStaleRedundantFiles(outputDir);

        var profilerDriverType = GetProfilerDriverType();
        int firstFrame = GetStaticInt(profilerDriverType, "firstFrameIndex");
        int lastFrame = GetStaticInt(profilerDriverType, "lastFrameIndex");
        int sourceFirstFrame = firstFrame;
        int sourceLastFrame = lastFrame;

        if (options.UseFrameRange)
        {
            firstFrame = Math.Max(firstFrame, options.FirstFrame);
            lastFrame = Math.Min(lastFrame, options.LastFrame);
            if (firstFrame > lastFrame)
                throw new InvalidOperationException("Requested Profiler frame range is outside loaded capture: " +
                                                    options.FirstFrame.ToString(CultureInfo.InvariantCulture) +
                                                    "-" +
                                                    options.LastFrame.ToString(CultureInfo.InvariantCulture));
        }

        var getRawFrameDataViewMethod = profilerDriverType.GetMethod(
            "GetRawFrameDataView",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(int), typeof(int) },
            null);

        if (getRawFrameDataViewMethod == null)
            throw new Exception("ProfilerDriver.GetRawFrameDataView(int,int) not found.");

        var getRawFrameDataView = CompileStaticFrameThreadMethod(getRawFrameDataViewMethod);

        var context = new ExportContext
        {
            SourceProfilePath = sourceProfilePath,
            OutputDir = outputDir,
            IncludeRawSamples = options.IncludeRawSamples,
            CaptureScope = options.CaptureScope,
            SourceFirstFrame = sourceFirstFrame,
            SourceLastFrame = sourceLastFrame,
            FirstFrame = firstFrame,
            LastFrame = lastFrame
        };

        AsyncTextChunkWriter samplesTsv = null;
        AsyncTextChunkWriter threadsTsv = null;
        AsyncTextChunkWriter timingTsv = null;
        var frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ShowExportProgress("准备 Profiler 导出", firstFrame, lastFrame, firstFrame, 0.02f);
            timingTsv = new AsyncTextChunkWriter(Path.Combine(outputDir, "ai_profiler_export_timing.tsv"));
            timingTsv.Enqueue("frame\tthreadIndex\tthreadName\tsampleCount\texportedSampleCount\tgetViewMs\tmetaMs\treadSamplesMs\ttreeMs\taggregateMs\trawTsvBuildMs\twriterEnqueueMs\tdisposeViewMs\tthreadWallMs\n");

            if (options.IncludeRawSamples)
            {
                samplesTsv = new AsyncTextChunkWriter(Path.Combine(outputDir, "ai_profiler_samples.tsv"));
                threadsTsv = new AsyncTextChunkWriter(Path.Combine(outputDir, "ai_profiler_threads.tsv"));
                samplesTsv.Enqueue("frame\tthreadIndex\tsampleIndex\tparentIndex\tdepth\tname\ttotalMs\tselfMs\tchildCount\n");
                threadsTsv.Enqueue("frame\tthreadIndex\tthreadGroup\tthreadName\tsampleCount\texportedSampleCount\ttotalMs\n");
            }

            int requestedLastFrame = lastFrame;
            int lastCompletedFrame = firstFrame - 1;
            int frameCount = Math.Max(1, requestedLastFrame - firstFrame + 1);
            try
            {
                for (int frame = firstFrame; frame <= requestedLastFrame; frame++)
                {
                    int processed = frame - firstFrame;
                    float frameProgress = 0.05f + 0.85f * (processed / (float)frameCount);
                    if (processed == 0 || processed == frameCount - 1 || processed % 5 == 0)
                        ShowExportProgress("解析 Profiler 帧", firstFrame, requestedLastFrame, frame, frameProgress);

                    ExportFrame(frame, firstFrame, requestedLastFrame, frameProgress, getRawFrameDataView, context, samplesTsv, threadsTsv, timingTsv);
                    lastCompletedFrame = frame;

                    if (exportCancelRequested)
                        throw new OperationCanceledException("Profiler AI 导出已取消。");
                }
            }
            catch (OperationCanceledException)
            {
                context.Canceled = true;
                context.LastFrame = lastCompletedFrame;
            }

            frameStopwatch.Stop();
            context.FrameParseAndQueueMs = frameStopwatch.ElapsedMilliseconds;
                ShowExportProgress("收尾 raw sample 文件", firstFrame, requestedLastFrame, Math.Max(firstFrame, lastCompletedFrame), 0.92f, false);
        }
        finally
        {
            var rawFinalizeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (samplesTsv != null)
                samplesTsv.Dispose();
            if (threadsTsv != null)
                threadsTsv.Dispose();
            if (timingTsv != null)
                timingTsv.Dispose();
            rawFinalizeStopwatch.Stop();
            context.RawWriterFinalizeMs = rawFinalizeStopwatch.ElapsedMilliseconds;
        }

            ShowExportProgress("写入 AI 摘要文件", firstFrame, lastFrame, Math.Max(firstFrame, context.LastFrame), 0.95f, false);
        var summaryStopwatch = System.Diagnostics.Stopwatch.StartNew();
        RecalculateEstimatedTsvSizes(context);
        if (WriteFullMarkerJsonl)
            WriteMarkerFiles(context);

        WriteSummaryJson(context);
        WriteDigestJson(context);
        WriteAiGuideMarkdown(context);
        WriteRawFrameExtractorScript(context);
        WriteRawFrameCommandScript(context);
        summaryStopwatch.Stop();
        totalStopwatch.Stop();
        context.SummaryWriteMs = summaryStopwatch.ElapsedMilliseconds;
        context.TotalExportMs = totalStopwatch.ElapsedMilliseconds;
        return context;
    }

    private static void DeleteStaleRedundantFiles(string outputDir)
    {
        string[] staleFileNames =
        {
            "ai_profiler_frames.jsonl",
            "ai_profiler_threads.jsonl",
            "ai_profiler_samples.jsonl",
            "ai_profiler_markers.jsonl",
            "frames.csv",
            "threads.csv",
            "markers.csv",
            "raw_frame_data_view_methods.txt",
            "README.txt",
            "export_info.txt",
            "analysis_summary.txt"
        };

        foreach (string fileName in staleFileNames)
        {
            string path = Path.Combine(outputDir, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void ExportFrame(
        int frame,
        int firstFrame,
        int lastFrame,
        float frameProgress,
        Func<int, int, object> getRawFrameDataView,
        ExportContext context,
        AsyncTextChunkWriter samplesTsv,
        AsyncTextChunkWriter threadsTsv,
        AsyncTextChunkWriter timingTsv)
    {
        double totalThreadMs = 0;
        double mainThreadMs = 0;
        double renderThreadMs = 0;
        int threadCount = 0;
        int frameSampleCount = 0;
        var frameStat = GetFrameStat(context, frame);

        for (int thread = 0; thread < MaxThreadProbeCount; thread++)
        {
            if ((thread & 3) == 0)
                ShowExportProgress("解析 Profiler 线程", firstFrame, lastFrame, frame, frameProgress, false);

            long threadStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long getViewStartTicks = threadStartTicks;
            var view = getRawFrameDataView(frame, thread);
            double getViewMs = ElapsedMs(getViewStartTicks);
            if (view == null)
                break;

            var disposable = view as IDisposable;
            int sampleCount = 0;
            int threadExportedSampleCount = 0;
            string threadName = string.Empty;
            double metaMs = 0;
            double readSamplesMs = 0;
            double treeMs = 0;
            double aggregateMs = 0;
            double rawTsvBuildMs = 0;
            double writerEnqueueMs = 0;
            double disposeViewMs = 0;
            try
            {
                long metaStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                var api = GetRawFrameDataViewApi(view.GetType());
                if (!api.IsValid(view))
                    break;

                sampleCount = api.SampleCount(view);
                threadName = api.ThreadName(view);
                string threadGroupName = api.ThreadGroupName(view);
                bool isMainThread = !string.IsNullOrEmpty(threadName) && threadName.IndexOf("Main Thread", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isRenderThread = !string.IsNullOrEmpty(threadName) && threadName.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0;
                metaMs = ElapsedMs(metaStartTicks);

                var sampleTimes = new double[sampleCount];
                var directChildTimes = new double[sampleCount];
                var childCounts = new int[sampleCount];
                var recursiveChildCounts = new int[sampleCount];
                var depths = new int[sampleCount];
                var parentIndices = new int[sampleCount];
                var sampleNames = new string[sampleCount];
                bool includeRawSamples = samplesTsv != null;
                bool[] exportedSamples = includeRawSamples ? new bool[sampleCount] : null;
                int[] exportedParentIndices = includeRawSamples ? new int[sampleCount] : null;
                int[] exportedDepths = includeRawSamples ? new int[sampleCount] : null;
                int[] exportedChildCounts = includeRawSamples ? new int[sampleCount] : null;
                double[] sampleSelfTimes = null;
                bool[] noiseSamples = null;

                long readSamplesStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    if ((sample & SampleProgressMask) == 0)
                ShowExportProgress("解析 Profiler Samples", firstFrame, lastFrame, frame, frameProgress, false);

                    sampleTimes[sample] = api.SampleTimeMs(view, sample);
                    childCounts[sample] = api.SampleChildrenCount(view, sample);
                    recursiveChildCounts[sample] = api.SampleChildrenCountRecursive(view, sample);
                    sampleNames[sample] = api.SampleName(view, sample);
                    depths[sample] = -1;
                    parentIndices[sample] = -1;
                    if (includeRawSamples)
                    {
                        exportedParentIndices[sample] = -1;
                        exportedDepths[sample] = 0;
                    }
                }
                readSamplesMs = ElapsedMs(readSamplesStartTicks);

                long treeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                AssignTreeMetadata(childCounts, recursiveChildCounts, depths, parentIndices);
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    int parentIndex = parentIndices[sample];
                    if (parentIndex >= 0)
                        directChildTimes[parentIndex] += sampleTimes[sample];
                }
                treeMs = ElapsedMs(treeStartTicks);

                double threadTotalMs = 0;
                StringBuilder sampleChunk = null;
                if (samplesTsv != null)
                {
                    sampleChunk = new StringBuilder(Math.Min(Math.Max(sampleCount * 96, 4096), 1024 * 1024));
                    sampleSelfTimes = new double[sampleCount];
                    noiseSamples = new bool[sampleCount];
                }

                long aggregateStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    if ((sample & SampleProgressMask) == 0)
                ShowExportProgress("聚合 Profiler Samples", firstFrame, lastFrame, frame, frameProgress, false);

                    var markerInfo = GetMarkerNameInfo(context, sampleNames[sample]);
                    sampleNames[sample] = markerInfo.Name;
                    string name = markerInfo.Name;
                    double totalMs = sampleTimes[sample];
                    int depth = depths[sample];
                    if (depth == 0)
                        threadTotalMs += totalMs;

                    if (!includeRawSamples && ShouldSkipNoiseSubtree(markerInfo))
                    {
                        int skippedRows = 1 + Math.Max(0, recursiveChildCounts[sample]);
                        context.NoiseSubtreeRootCount++;
                        context.NoiseSampleRowCount += skippedRows;
                        context.NoiseTotalMs += totalMs;
                        context.NoiseSelfMs += Math.Max(0, totalMs - directChildTimes[sample]);
                        sample += skippedRows - 1;
                        continue;
                    }

                    if (!includeRawSamples && !markerInfo.IsNoise && ShouldSkipTinySubtree(markerInfo, totalMs))
                    {
                        int skippedRows = 1 + Math.Max(0, recursiveChildCounts[sample]);
                        context.TinySubtreeRootCount++;
                        context.TinySampleRowCount += skippedRows;
                        context.TinySampleTotalMs += totalMs;
                        context.TinySampleSelfMs += Math.Max(0, totalMs - directChildTimes[sample]);
                        sample += skippedRows - 1;
                        continue;
                    }

                    double selfMs = Math.Max(0, totalMs - directChildTimes[sample]);
                    if (sampleSelfTimes != null)
                        sampleSelfTimes[sample] = selfMs;
                    int parentIndex = parentIndices[sample];
                    bool isNoise = markerInfo.IsNoise;
                    bool isTinySkipped = !isNoise && ShouldSkipTinySample(markerInfo, totalMs, selfMs);
                    if (noiseSamples != null)
                        noiseSamples[sample] = isNoise || isTinySkipped;

                    if (!isNoise && !isTinySkipped)
                    {
                        threadExportedSampleCount++;
                        if (includeRawSamples)
                        {
                            exportedSamples[sample] = true;
                            int exportedParentIndex = -1;
                            if (parentIndex >= 0)
                                exportedParentIndex = exportedSamples[parentIndex] ? parentIndex : exportedParentIndices[parentIndex];
                            exportedParentIndices[sample] = exportedParentIndex;
                            exportedDepths[sample] = exportedParentIndex >= 0 ? exportedDepths[exportedParentIndex] + 1 : 0;
                            if (exportedParentIndex >= 0 && exportedParentIndex < exportedChildCounts.Length)
                                exportedChildCounts[exportedParentIndex]++;
                        }
                    }
                    else if (includeRawSamples)
                    {
                        int exportedParentIndex = -1;
                        if (parentIndex >= 0)
                            exportedParentIndex = exportedSamples[parentIndex] ? parentIndex : exportedParentIndices[parentIndex];
                        exportedParentIndices[sample] = exportedParentIndex;
                        exportedDepths[sample] = exportedParentIndex >= 0 ? exportedDepths[exportedParentIndex] + 1 : 0;
                    }

                    if (isNoise)
                    {
                        context.NoiseSampleRowCount++;
                        context.NoiseTotalMs += totalMs;
                        context.NoiseSelfMs += selfMs;
                        continue;
                    }

                    if (isTinySkipped)
                    {
                        context.TinySampleRowCount++;
                        context.TinySampleTotalMs += totalMs;
                        context.TinySampleSelfMs += selfMs;
                    }
                    else
                    {
                        AddMarkerStat(context, markerInfo, frame, totalMs, selfMs);
                        if (ShouldIndexMarkerContext(markerInfo, totalMs, selfMs))
                        {
                            double childrenMs = totalMs - selfMs;
                            string callPath = GetCallPath(context, sample, parentIndices, sampleNames);
                            string childMarkerHint = childrenMs >= 0.05 ? FindLargestDirectChild(sample, sampleTimes, childCounts, recursiveChildCounts, sampleNames) : string.Empty;
                            AddMarkerContextStat(context, name, threadName, callPath, frame, thread, sample, totalMs, selfMs, childrenMs, childMarkerHint);
                        }

                        AddFrameDigestSample(frameStat, isMainThread, markerInfo, totalMs, selfMs);
                        AddFrameTopSample(frameStat, thread, threadName, sample, parentIndex, depth, markerInfo, totalMs, selfMs, childCounts[sample]);
                    }

                }
                aggregateMs = ElapsedMs(aggregateStartTicks);

                if (sampleChunk != null)
                {
                    long rawTsvStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (int sample = 0; sample < sampleCount; sample++)
                    {
                        if ((sample & SampleProgressMask) == 0)
                ShowExportProgress("写入 raw sample TSV", firstFrame, lastFrame, frame, frameProgress, false);

                        if (noiseSamples[sample])
                            continue;

                        int beforeLength = sampleChunk.Length;
                        AppendSampleTsvLine(
                            sampleChunk,
                            frame,
                            thread,
                            sample,
                            exportedParentIndices[sample],
                            exportedDepths[sample],
                            sampleNames[sample],
                            sampleTimes[sample],
                            sampleSelfTimes[sample],
                            exportedChildCounts[sample]);
                        int lineLength = sampleChunk.Length - beforeLength;
                        context.EstimatedExportedSampleTsvChars += lineLength;
                    }
                    rawTsvBuildMs = ElapsedMs(rawTsvStartTicks);
                }

                if (samplesTsv != null && sampleChunk != null && sampleChunk.Length > 0)
                {
                    long enqueueStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    samplesTsv.Enqueue(sampleChunk.ToString());
                    writerEnqueueMs += ElapsedMs(enqueueStartTicks);
                }

                totalThreadMs += threadTotalMs;
                if (isMainThread)
                {
                    mainThreadMs += threadTotalMs;
                    frameStat.MainThreadMs += threadTotalMs;
                }
                if (isRenderThread)
                {
                    renderThreadMs += threadTotalMs;
                    frameStat.RenderThreadMs += threadTotalMs;
                }

                frameSampleCount += sampleCount;
                context.ExportedSampleRowCount += threadExportedSampleCount;
                threadCount++;

                if (threadsTsv != null)
                {
                    long enqueueStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    threadsTsv.Enqueue(BuildThreadTsvLine(frame, thread, threadGroupName, threadName, sampleCount, threadExportedSampleCount, threadTotalMs));
                    writerEnqueueMs += ElapsedMs(enqueueStartTicks);
                }
            }
            finally
            {
                if (disposable != null)
                {
                    long disposeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    disposable.Dispose();
                    disposeViewMs = ElapsedMs(disposeStartTicks);
                }

                double threadWallMs = ElapsedMs(threadStartTicks);
                context.TimingGetViewMs += getViewMs;
                context.TimingReadSamplesMs += readSamplesMs;
                context.TimingTreeMs += treeMs;
                context.TimingAggregateMs += aggregateMs;
                context.TimingRawTsvBuildMs += rawTsvBuildMs;
                context.TimingWriterEnqueueMs += writerEnqueueMs;
                context.TimingDisposeViewMs += disposeViewMs;

                if (timingTsv != null)
                    timingTsv.Enqueue(BuildTimingTsvLine(frame, thread, threadName, sampleCount, threadExportedSampleCount, getViewMs, metaMs, readSamplesMs, treeMs, aggregateMs, rawTsvBuildMs, writerEnqueueMs, disposeViewMs, threadWallMs));
            }
        }

        context.FrameCount++;
        context.ThreadRowCount += threadCount;
        context.SampleRowCount += frameSampleCount;
        context.TotalThreadMs += totalThreadMs;
        context.MainThreadMs += mainThreadMs;
        context.RenderThreadMs += renderThreadMs;
        frameStat.ThreadCount = threadCount;
        frameStat.SampleCount = frameSampleCount;
    }

    private static void AddMarkerStat(ExportContext context, MarkerNameInfo marker, int frame, double totalMs, double selfMs)
    {
        var stat = marker.MarkerStat;
        if (stat == null)
        {
            stat = new MarkerStat { Name = marker.Name };
            marker.MarkerStat = stat;
            context.MarkerStats.Add(marker.Name, stat);
        }

        stat.TotalMs += totalMs;
        stat.SelfMs += selfMs;
        if (selfMs > stat.MaxSelfMs)
        {
            stat.MaxSelfMs = selfMs;
            stat.MaxSelfFrame = frame;
        }

        if (totalMs > stat.MaxTotalMs)
        {
            stat.MaxTotalMs = totalMs;
            stat.MaxTotalFrame = frame;
        }

        stat.Count++;
        stat.FirstFrame = Math.Min(stat.FirstFrame, frame);
        stat.LastFrame = Math.Max(stat.LastFrame, frame);
    }

    private static void AddMarkerContextStat(
        ExportContext context,
        string name,
        string threadName,
        string callPath,
        int frame,
        int threadIndex,
        int sampleIndex,
        double totalMs,
        double selfMs,
        double childrenMs,
        string childMarkerHint)
    {
        name = string.IsNullOrEmpty(name) ? "<unnamed>" : name;
        threadName = threadName ?? string.Empty;
        callPath = callPath ?? name;

        var key = new MarkerContextKey(threadName, callPath);
        if (!context.MarkerContextStats.TryGetValue(key, out var stat))
        {
            stat = new MarkerContextStat
            {
                Name = name,
                ThreadName = threadName,
                CallPath = callPath
            };
            context.MarkerContextStats.Add(key, stat);
        }

        stat.TotalMs += totalMs;
        stat.SelfMs += selfMs;
        stat.Count++;
        stat.FirstFrame = Math.Min(stat.FirstFrame, frame);
        stat.LastFrame = Math.Max(stat.LastFrame, frame);

        if (selfMs > stat.MaxSelfMs)
        {
            stat.MaxSelfMs = selfMs;
            stat.MaxSelfFrame = frame;
            stat.MaxFrame = frame;
            stat.MaxThreadIndex = threadIndex;
            stat.MaxSampleIndex = sampleIndex;
        }

        if (totalMs > stat.MaxTotalMs)
        {
            stat.MaxTotalMs = totalMs;
            stat.MaxTotalFrame = frame;
            stat.MaxChildrenMs = Math.Max(0, childrenMs);
            stat.MaxChildrenMarkerHint = stat.MaxChildrenMs > 0.001 && !string.Equals(childMarkerHint, name, StringComparison.Ordinal) ? childMarkerHint : string.Empty;
        }
    }

    private static FrameDigestStat GetFrameStat(ExportContext context, int frame)
    {
        if (!context.FrameStats.TryGetValue(frame, out var stat))
        {
            stat = new FrameDigestStat { Frame = frame };
            context.FrameStats.Add(frame, stat);
        }

        return stat;
    }

    private static void AddFrameDigestSample(FrameDigestStat stat, bool isMainThread, MarkerNameInfo marker, double totalMs, double selfMs)
    {
        if (isMainThread && !marker.IsNoise)
            stat.MainThreadActionableSelfMs += selfMs;

        if (marker.IsPlayerLoop)
            stat.PlayerLoopMs += totalMs;
        if (marker.IsEditorLoop)
            stat.EditorLoopMs += totalMs;
        if (marker.IsProfiler)
            stat.ProfilerMs += totalMs;
        if (marker.IsIdle)
            stat.IdleMs += totalMs;
        if (marker.IsWait)
            stat.WaitMs += totalMs;
        if (marker.IsGfxWait)
            stat.GfxWaitMs += totalMs;
        if (marker.IsRenderPipeline)
            stat.RenderPipelineMs += totalMs;
        if (marker.IsScriptableRenderer)
            stat.ScriptableRendererMs += totalMs;
        if (marker.IsTransparent)
            stat.TransparentMs += totalMs;
        if (marker.IsShadows)
            stat.ShadowsMs += totalMs;
        if (marker.IsSrpBatcher)
            stat.SrpBatcherMs += totalMs;
        if (marker.IsCulling)
            stat.CullingMs += totalMs;
        if (marker.IsBrg)
            stat.BrgMs += totalMs;
        if (marker.IsParticle)
            stat.ParticleMs += totalMs;
        if (marker.IsGc)
            stat.GcMs += totalMs;
    }

    private static void AddFrameTopSample(
        FrameDigestStat stat,
        int threadIndex,
        string threadName,
        int sampleIndex,
        int parentIndex,
        int depth,
        MarkerNameInfo marker,
        double totalMs,
        double selfMs,
        int childCount)
    {
        if (marker.IsNoise)
            return;

        if (selfMs < TopSampleMinSelfMs && totalMs < TopSampleMinTotalMs)
            return;

        if (stat.TopActionableSamples.Count >= DigestTopSamplePerFrameCount)
        {
            var weakest = stat.TopActionableSamples[stat.TopActionableSamples.Count - 1];
            if (selfMs < weakest.SelfMs || (selfMs == weakest.SelfMs && totalMs <= weakest.TotalMs))
                return;
        }

        var sample = new SampleRef
        {
            ThreadIndex = threadIndex,
            ThreadName = threadName ?? string.Empty,
            SampleIndex = sampleIndex,
            ParentIndex = parentIndex,
            Depth = depth,
            Name = marker.Name,
            TotalMs = totalMs,
            SelfMs = selfMs,
            ChildCount = childCount
        };

        AddTopSampleBySelfMs(stat.TopActionableSamples, sample);
    }

    private static void AddTopSampleBySelfMs(List<SampleRef> samples, SampleRef sample)
    {
        if (samples.Count >= DigestTopSamplePerFrameCount)
        {
            var weakest = samples[samples.Count - 1];
            if (sample.SelfMs < weakest.SelfMs || (sample.SelfMs == weakest.SelfMs && sample.TotalMs <= weakest.TotalMs))
                return;
        }

        samples.Add(sample);
        samples.Sort((a, b) =>
        {
            int selfCompare = b.SelfMs.CompareTo(a.SelfMs);
            return selfCompare != 0 ? selfCompare : b.TotalMs.CompareTo(a.TotalMs);
        });

        if (samples.Count > DigestTopSamplePerFrameCount)
            samples.RemoveAt(samples.Count - 1);
    }

    private static void WriteMarkerFiles(ExportContext context)
    {
        var orderedMarkers = context.MarkerStats.Values.OrderByDescending(s => s.SelfMs).ThenByDescending(s => s.TotalMs).ToList();

        using (var markerJsonl = new StreamWriter(Path.Combine(context.OutputDir, "ai_profiler_markers.jsonl"), false, Utf8NoBom))
        {
            foreach (var stat in orderedMarkers)
                WriteMarkerJsonLine(markerJsonl, stat);
        }
    }

    private static void RecalculateEstimatedTsvSizes(ExportContext context)
    {
        long actionableBytes = 0;
        foreach (var stat in context.MarkerStats.Values)
        {
            long markerBytes = (long)stat.Count * (EstimatedSampleTsvFixedChars + EstimateTsvEscapedChars(stat.Name));
            actionableBytes += markerBytes;
        }

        long noiseBytes = (long)context.NoiseSampleRowCount * EstimatedSampleTsvFixedChars;
        long tinyBytes = (long)context.TinySampleRowCount * EstimatedSampleTsvFixedChars;
        context.EstimatedSampleTsvChars = actionableBytes + noiseBytes + tinyBytes;
        context.EstimatedNoiseSampleTsvChars = noiseBytes;
        if (!context.IncludeRawSamples || context.EstimatedExportedSampleTsvChars == 0)
            context.EstimatedExportedSampleTsvChars = actionableBytes;
    }

    private static void WriteSummaryJson(ExportContext context)
    {
        var allMarkers = context.MarkerStats.Values.ToList();
        var topSelf = allMarkers
            .OrderByDescending(s => s.SelfMs)
            .ThenByDescending(s => s.TotalMs)
            .Take(SummaryTopMarkerCount)
            .ToList();

        var topTotal = allMarkers
            .OrderByDescending(s => s.TotalMs)
            .ThenByDescending(s => s.SelfMs)
            .Take(SummaryTopMarkerCount)
            .ToList();
        var selfRanks = BuildMarkerRanks(topSelf);
        var totalRanks = BuildMarkerRanks(topTotal);
        var topMarkers = topSelf
            .Concat(topTotal)
            .GroupBy(s => s.Name)
            .Select(g => g.First())
            .OrderBy(s => selfRanks.TryGetValue(s.Name, out int rank) ? rank : int.MaxValue)
            .ThenBy(s => totalRanks.TryGetValue(s.Name, out int rank) ? rank : int.MaxValue)
            .ThenByDescending(s => s.SelfMs)
            .ToList();

        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "profiler-ai-export/v2", true);
        WriteJsonProperty(sb, "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
        WriteJsonProperty(sb, "unityVersion", Application.unityVersion, true);
        WriteJsonProperty(sb, "pathBasis", "Unity project root; exported paths are project-relative when possible", true);
        WriteJsonProperty(sb, "projectPath", ".", true);
        WriteJsonProperty(sb, "sourceProfilePath", ToProjectRelativePath(context.SourceProfilePath), true);
        WriteJsonProperty(sb, "captureScope", context.CaptureScope, true);
        WriteJsonProperty(sb, "canceled", context.Canceled, true);
        WriteJsonProperty(sb, "sourceFirstFrame", context.SourceFirstFrame, true);
        WriteJsonProperty(sb, "sourceLastFrame", context.SourceLastFrame, true);
        WriteJsonProperty(sb, "firstFrame", context.FirstFrame, true);
        WriteJsonProperty(sb, "lastFrame", context.LastFrame, true);
        WriteJsonProperty(sb, "frameCount", context.FrameCount, true);
        WriteJsonProperty(sb, "threadRows", context.ThreadRowCount, true);
        WriteJsonProperty(sb, "exportTiming", "ai_profiler_export_timing.tsv", true);
        WriteJsonProperty(sb, "rawSamplesNoisePolicy", "noise rows removed; parentIndex/depth rewired to nearest exported non-noise ancestor", true);
        WriteJsonProperty(sb, "sampleRows", context.ExportedSampleRowCount, true);
        WriteJsonProperty(sb, "sampleRowsOriginal", context.SampleRowCount, true);
        WriteJsonProperty(sb, "noiseSampleRowsRemoved", context.NoiseSampleRowCount, true);
        WriteJsonProperty(sb, "noiseSampleRowPercent", Percent(context.NoiseSampleRowCount, context.SampleRowCount), true);
        WriteJsonProperty(sb, "noiseSubtreeRootsSkipped", context.NoiseSubtreeRootCount, true);
        WriteJsonProperty(sb, "tinySampleRowsSkipped", context.TinySampleRowCount, true);
        WriteJsonProperty(sb, "tinySampleRowPercent", Percent(context.TinySampleRowCount, context.SampleRowCount), true);
        WriteJsonProperty(sb, "tinySubtreeRootsSkipped", context.TinySubtreeRootCount, true);
        WriteJsonProperty(sb, "markerCount", context.MarkerStats.Count, true);
        WriteJsonProperty(sb, "estimatedOriginalSampleTsvBytes", context.EstimatedSampleTsvChars, true);
        WriteJsonProperty(sb, "estimatedSampleTsvBytes", context.EstimatedExportedSampleTsvChars, true);
        WriteJsonProperty(sb, "estimatedNoiseSampleTsvBytesRemoved", context.EstimatedNoiseSampleTsvChars, true);
        WriteJsonProperty(sb, "estimatedNoiseSampleTsvBytePercent", Percent(context.EstimatedNoiseSampleTsvChars, context.EstimatedSampleTsvChars), true);
        WriteJsonProperty(sb, "estimatedActionableSampleTsvBytes", context.EstimatedExportedSampleTsvChars, true);
        WriteJsonProperty(sb, "totalThreadMs", context.TotalThreadMs, true);
        WriteJsonProperty(sb, "exportTimingReadSamplesMs", context.TimingReadSamplesMs, true);
        WriteJsonProperty(sb, "exportTimingTreeMs", context.TimingTreeMs, true);
        WriteJsonProperty(sb, "exportTimingAggregateMs", context.TimingAggregateMs, true);
        WriteJsonProperty(sb, "exportTimingRawTsvBuildMs", context.TimingRawTsvBuildMs, true);
        WriteJsonProperty(sb, "noiseTotalMsRemoved", context.NoiseTotalMs, true);
        WriteJsonProperty(sb, "noiseSelfMsRemoved", context.NoiseSelfMs, true);
        WriteJsonProperty(sb, "tinySampleTotalMsSkipped", context.TinySampleTotalMs, true);
        WriteJsonProperty(sb, "tinySampleSelfMsSkipped", context.TinySampleSelfMs, true);
        WriteJsonProperty(sb, "mainThreadMs", context.MainThreadMs, true);
        WriteJsonProperty(sb, "renderThreadMs", context.RenderThreadMs, true);
        WriteMarkerRankedArray(sb, "topMarkers", topMarkers, selfRanks, totalRanks, false);
        sb.AppendLine("}");

        WriteUtf8NoBom(Path.Combine(context.OutputDir, "ai_profiler_summary.json"), sb.ToString());
    }

    private static void WriteDigestJson(ExportContext context)
    {
        var contextsBySelf = context.MarkerContextStats.Values
            .OrderByDescending(s => s.SelfMs)
            .ThenByDescending(s => s.TotalMs)
            .Take(DigestTopMarkerCount)
            .ToList();

        var contextRanksByTotal = BuildContextRanksByTotal(context.MarkerContextStats.Values);
        var suspectedHotspots = BuildSuspectedHotspots(context.MarkerContextStats.Values, contextRanksByTotal);
        var stringTable = BuildDigestStringTable(contextsBySelf, suspectedHotspots);

        var frames = context.FrameStats.Values.ToList();
        var worstMainFrames = frames.OrderByDescending(f => f.MainThreadMs).Take(DigestTopFrameCount).ToList();
        var worstMainNoiseAdjustedFrames = frames.OrderByDescending(f => f.MainThreadNoiseAdjustedMs).Take(DigestTopFrameCount).ToList();
        var worstRenderFrames = frames.OrderByDescending(f => f.RenderPipelineMs).Take(DigestTopFrameCount).ToList();
        var worstBrgFrames = frames.OrderByDescending(f => f.BrgMs).Take(DigestTopFrameCount).ToList();
        var callPathTable = BuildCallPathTokenTable(contextsBySelf.Concat(suspectedHotspots.Select(s => s.Stat)));

        WriteWorstFramesJson(context, worstMainFrames, worstMainNoiseAdjustedFrames, worstRenderFrames, worstBrgFrames);

        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "profiler-ai-digest/v3.3", true);
        WriteJsonProperty(sb, "purpose", "Compact first-read file for AI analysis. For worst-frame sample snapshots read ai_profiler_worstframes_samples.json.", true);
        WriteJsonProperty(sb, "pathBasis", "Unity project root; exported paths are project-relative when possible", true);
        WriteJsonProperty(sb, "sourceSummary", "ai_profiler_summary.json", true);
        WriteJsonProperty(sb, "worstFrames", "ai_profiler_worstframes.json", true);
        WriteJsonProperty(sb, "worstFrameSamples", "ai_profiler_worstframes_samples.json", true);
        WriteJsonProperty(sb, "rawSamples", context.IncludeRawSamples ? "ai_profiler_samples.tsv" : "not exported", true);
        WriteJsonProperty(sb, "threadTable", context.IncludeRawSamples ? "ai_profiler_threads.tsv" : "not exported", true);
        WriteJsonProperty(sb, "rawFrameExtractor", RawFrameExtractorPath, true);
        WriteJsonProperty(sb, "projectRawFrameExtractor", ProjectRawFrameCommandPath, true);
        WriteJsonProperty(sb, "editorRawFrameRequest", RawFrameRequestPath, true);
        WriteJsonProperty(sb, "editorRawFrameRequestResult", RawFrameRequestResultPath, true);
        WriteJsonProperty(sb, "editorRawFrameRequestStatus", RawFrameRequestStatusPath, true);
        WriteJsonProperty(sb, "editorRawFrameRequestRequiresProfilerWindowOpen", true, true);
        WriteJsonProperty(sb, "exportTiming", "ai_profiler_export_timing.tsv", true);
        WriteJsonProperty(sb, "markerAggregates", WriteFullMarkerJsonl ? "ai_profiler_markers.jsonl" : "not exported by default; use summary topMarkers or targeted raw frame extraction", true);
        WriteJsonProperty(sb, "environment", BuildEnvironmentSummary(), true);
        WriteJsonProperty(sb, "captureScope", context.CaptureScope, true);
        WriteJsonProperty(sb, "canceled", context.Canceled, true);
        WriteJsonProperty(sb, "sourceFirstFrame", context.SourceFirstFrame, true);
        WriteJsonProperty(sb, "sourceLastFrame", context.SourceLastFrame, true);
        WriteStringArray(sb, "metricWarnings", new[]
        {
            "Frame category fields such as renderPipelineMs, scriptableRendererMs, cullingMs, brgMs, shadowsMs and srpBatcherMs are inclusive name-bucket indexes.",
            "Do not add those category fields together as real frame time because nested samples can be counted more than once.",
            "topMarkerContextsBySelfMs skips tiny per-sample contexts below selfMs 0.02ms and totalMs 0.2ms unless the marker matches project/render keywords; non-key wrapper contexts require totalMs >= 3.0ms; use summary topMarkers for marker-name aggregates.",
            "In compact index exports, known profiler/editor/wait/audio noise roots are skipped as whole subtrees and counted in noiseSubtreeRootsSkipped.",
            "Non-key samples with selfMs <= 0.0001 and totalMs <= 0.0001 are skipped from marker/context/top-sample aggregation and counted in tinySampleRowsSkipped.",
            "In compact index exports, non-key subtrees with root totalMs <= 0.002 are skipped as a whole and counted in tinySubtreeRootsSkipped/tinySampleRowsSkipped. Targeted raw frame extraction remains available for exact tree inspection.",
            "Use topMarkerContexts first, then ai_profiler_worstframes.json for per-frame sample snapshots."
        }, true);
        WriteProjectSearchHints(sb, true);
        WriteStringTable(sb, stringTable, true);
        WriteJsonProperty(sb, "stringTableNote", "local to ai_profiler_digest.json only; do not reuse indexes from other files", true);
        WriteJsonProperty(sb, "firstFrame", context.FirstFrame, true);
        WriteJsonProperty(sb, "lastFrame", context.LastFrame, true);
        WriteJsonProperty(sb, "frameCount", context.FrameCount, true);
        WriteJsonProperty(sb, "rawSamplesNoisePolicy", "noise rows removed; parentIndex/depth rewired to nearest exported non-noise ancestor", true);
        WriteJsonProperty(sb, "sampleRows", context.ExportedSampleRowCount, true);
        WriteJsonProperty(sb, "sampleRowsOriginal", context.SampleRowCount, true);
        WriteJsonProperty(sb, "noiseSampleRowsRemoved", context.NoiseSampleRowCount, true);
        WriteJsonProperty(sb, "noiseSampleRowPercent", Percent(context.NoiseSampleRowCount, context.SampleRowCount), true);
        WriteJsonProperty(sb, "noiseSubtreeRootsSkipped", context.NoiseSubtreeRootCount, true);
        WriteJsonProperty(sb, "tinySampleRowsSkipped", context.TinySampleRowCount, true);
        WriteJsonProperty(sb, "tinySampleRowPercent", Percent(context.TinySampleRowCount, context.SampleRowCount), true);
        WriteJsonProperty(sb, "tinySubtreeRootsSkipped", context.TinySubtreeRootCount, true);
        WriteJsonProperty(sb, "estimatedOriginalSampleTsvBytes", context.EstimatedSampleTsvChars, true);
        WriteJsonProperty(sb, "estimatedSampleTsvBytes", context.EstimatedExportedSampleTsvChars, true);
        WriteJsonProperty(sb, "estimatedNoiseSampleTsvBytesRemoved", context.EstimatedNoiseSampleTsvChars, true);
        WriteJsonProperty(sb, "estimatedNoiseSampleTsvBytePercent", Percent(context.EstimatedNoiseSampleTsvChars, context.EstimatedSampleTsvChars), true);
        WriteJsonProperty(sb, "estimatedActionableSampleTsvBytes", context.EstimatedExportedSampleTsvChars, true);
        WriteJsonProperty(sb, "noiseTotalMsRemoved", context.NoiseTotalMs, true);
        WriteJsonProperty(sb, "noiseSelfMsRemoved", context.NoiseSelfMs, true);
        WriteJsonProperty(sb, "tinySampleTotalMsSkipped", context.TinySampleTotalMs, true);
        WriteJsonProperty(sb, "tinySampleSelfMsSkipped", context.TinySampleSelfMs, true);
        WriteJsonProperty(sb, "markerCount", context.MarkerStats.Count, true);
        WriteStringArray(sb, "noiseFilters", new[]
        {
            "<unnamed>",
            "Idle",
            "Semaphore.WaitForSignal",
            "EditorLoop",
            "Profiler.*",
            "ProfilerFrameData.*",
            "ProfilerHistory.*",
            "Audio.Thread",
            "MasterDSP",
            "MemoryManager.FallbackAllocation",
            "EditorConnection.*",
            "root samples such as Main Thread and PlayerLoop"
        }, true);
        WriteCallPathTokenArray(sb, callPathTable, true);
        WriteTopContextsDecoded(sb, "topContextsDecoded", contextsBySelf.Take(10).ToList(), contextRanksByTotal, true);
        WriteMarkerContextTable(sb, "topMarkerContextsBySelfMs", contextsBySelf, contextRanksByTotal, callPathTable, stringTable, true);
        WriteSuspectedHotspotTable(sb, "suspectedHotspots", suspectedHotspots, callPathTable, stringTable, false);
        sb.AppendLine("}");

        WriteUtf8NoBom(Path.Combine(context.OutputDir, "ai_profiler_digest.json"), sb.ToString());
    }

    private static void WriteAiGuideMarkdown(ExportContext context)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("# Unity Profiler AI Analysis Guide");
        sb.AppendLine();
        sb.AppendLine("This folder is exported for machine analysis, not manual Profiler UI reading.");
        sb.AppendLine("Use this file as the protocol for choosing which data file to read next.");
        sb.AppendLine("The final analysis report must be written in Chinese.");
        sb.AppendLine("The exported digest contains call-path-aware hotspot indexes; prefer those over marker-name-only rankings.");
        sb.AppendLine();
        sb.AppendLine("## Read Order");
        sb.AppendLine();
        sb.AppendLine("1. Read `ai_profiler_digest.json` first. It is the compact index with noise-filtered context hotspots and metadata.");
        sb.AppendLine("2. Read `ai_profiler_worstframes.json` for the small deduplicated worst-frame overview.");
        sb.AppendLine("3. Read `ai_profiler_worstframes_samples.json` only after selecting frames that need per-frame top sample snapshots.");
        sb.AppendLine("4. Read `ai_profiler_summary.json` only when capture metadata or marker-name aggregate rankings are needed.");
        sb.AppendLine("5. If full raw call-tree data is needed and tool execution is restricted, choose only the frames that require deeper inspection and write them to `" + RawFrameRequestPath + "` as `frames:[...]`. Do not use the currently selected Profiler UI frame as input. While the Profiler window is open, Unity auto-processes new requests and writes `" + RawFrameRequestResultPath + "`.");
        sb.AppendLine("6. Before writing a raw-frame request, read `" + RawFrameRequestStatusPath + "`. If `profilerWindowOpen` is false or the file is stale/missing, tell the user to open the Unity Profiler window.");
        sb.AppendLine("7. If `" + RawFrameRequestResultPath + "` does not update after writing a request, tell the user to open the Unity Profiler window. Auto-processing is intentionally tied to the Profiler window being open.");
        sb.AppendLine("8. If the Profiler window is closed or auto-processing is not available, use `Tools/AI 分析/Profiler/处理 AI Raw Frame 请求` as a manual fallback.");
        sb.AppendLine("9. If command execution is available, run the project-level wrapper `" + ProjectRawFrameCommandPath + " <exportDir> <frameIndex>` for the specific high-cost frame.");
        sb.AppendLine("10. Read raw TSV only for targeted full call-tree inspection by `frame`, `threadIndex`, `sampleIndex`, `parentIndex`, or `name`.");
        sb.AppendLine("11. Do not directly read `profile.data`; it is a Unity binary capture for Unity Profiler or `" + RawFrameExtractorPath + "` only.");
        sb.AppendLine();
        sb.AppendLine("## Main Files");
        sb.AppendLine();
        sb.AppendLine("| File | Purpose | Typical Use |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| `ai_profiler_digest.json` | AI first-read index | Find actionable marker contexts, project search hints, and noise removal stats. |");
        sb.AppendLine("| `ai_profiler_worstframes.json` | Small worst-frame overview | Pick suspicious frames without reading per-frame sample arrays. |");
        sb.AppendLine("| `ai_profiler_worstframes_samples.json` | Worst-frame top sample snapshots | Inspect top 30 actionable samples by self time for already-selected frames. |");
        sb.AppendLine("| `ai_profiler_summary.json` | Full summary | Confirm frame range, row counts, Unity version, merged marker rankings, and max frame fields. |");
        if (WriteFullMarkerJsonl)
            sb.AppendLine("| `ai_profiler_markers.jsonl` | One aggregate marker per line | Search for marker names such as `BatchRendererGroup`, `DrawTransparentObjects`, `SRPBatcher`, `Shadow`, `Particle`. |");
        sb.AppendLine("| `ai_profiler_export_timing.tsv` | Exporter self-timing by frame/thread | Diagnose whether export time is spent in Unity sample reads, tree rebuild, aggregation, raw TSV build, or writer enqueue. |");
        sb.AppendLine("| `" + ProjectRawFrameCommandPath + "` | Project-level raw frame extractor wrapper | Preferred AI entrypoint. Run from project root with `<exportDir> <frameIndex>`. |");
        sb.AppendLine("| `" + RawFrameRequestPath + "` | Editor-side raw frame request file | Lowest-permission path: AI writes this JSON, open Profiler window auto-processes it. |");
        sb.AppendLine("| `" + RawFrameRequestStatusPath + "` | Editor-side raw frame request status | Read before writing a request; `profilerWindowOpen=true` means auto-processing is active. |");
        sb.AppendLine("| `" + RawFrameRequestResultPath + "` | Editor-side raw frame request result | Read after Unity processes the request to find the generated raw frame folder or error. |");
        sb.AppendLine("| `" + RawFrameCommandPath + "` | Export-local raw frame extractor wrapper | Run `extract_raw_frame.cmd <frameIndex>` from this folder if using the export folder directly. |");
        sb.AppendLine("| `" + RawFrameExtractorPath + "` | On-demand raw frame extractor implementation | Used by `" + RawFrameCommandPath + "`; call it directly only if custom frame ranges or output folders are needed. |");
        sb.AppendLine("| `ai_profiler_threads.tsv` | One frame/thread row, only present in on-demand raw exports or explicit raw mode | Map sample `threadIndex` to source thread metadata without repeating it on every sample. |");
        sb.AppendLine("| `ai_profiler_samples.tsv` | One exported non-noise sample per row, only present in on-demand raw exports or explicit raw mode | Rebuild the noise-pruned frame/thread call tree or inspect children/parents around one hotspot. |");
        sb.AppendLine("| Source profile from Capture Metadata | Original Unity binary capture, stored next to this export folder when saved from the Profiler window | Do not parse directly as text/JSON; use Unity Profiler or `" + RawFrameCommandPath + "`. |");
        sb.AppendLine();
        sb.AppendLine("## Noise Rules");
        sb.AppendLine();
        sb.AppendLine("Do not treat these as optimization targets unless explicitly asked:");
        sb.AppendLine();
        sb.AppendLine("- `<unnamed>`");
        sb.AppendLine("- `Idle`");
        sb.AppendLine("- `Semaphore.WaitForSignal`");
        sb.AppendLine("- `EditorLoop`");
        sb.AppendLine("- `Profiler.*`, `ProfilerFrameData.*`, `ProfilerHistory.*`");
        sb.AppendLine("- `EditorConnection.*`");
        sb.AppendLine("- Audio background markers such as `Audio.Thread` and `MasterDSP`");
        sb.AppendLine("- Root/container markers such as `Main Thread` and `PlayerLoop`");
        sb.AppendLine();
        sb.AppendLine("Prefer `topMarkerContextsBySelfMs`, `suspectedHotspots`, and `ai_profiler_worstframes.json` over marker-name-only arrays.");
        sb.AppendLine("These noise rows are excluded from marker/context/top-sample aggregation. Only removal counters keep their row count and time share.");
        sb.AppendLine("In compact index exports, known profiler/editor/wait/audio noise roots are skipped as whole subtrees and reported as `noiseSubtreeRootsSkipped`. Container roots such as `EditorLoop`, `PlayerLoop`, and `Main Thread` are not subtree-skipped because they can contain real project samples.");
        sb.AppendLine("When `ai_profiler_samples.tsv` is generated, it has already removed these noise rows. Use `noiseSampleRowsRemoved`, `estimatedNoiseSampleTsvBytesRemoved`, `noiseTotalMsRemoved`, and `noiseSelfMsRemoved` to report what was removed.");
        sb.AppendLine("After noise removal, `parentIndex`, `depth`, and `childCount` are rewired to the nearest exported non-noise ancestor. `sampleIndex` remains the original Unity sample index, so gaps are expected.");
        sb.AppendLine("Non-key tiny samples with `selfMs <= 0.0001` and `totalMs <= 0.0001` are skipped from marker/context/top-sample aggregation. They are counted in `tinySampleRowsSkipped`, `tinySampleTotalMsSkipped`, and `tinySampleSelfMsSkipped`.");
        sb.AppendLine("In compact index exports, non-key tiny subtrees with root `totalMs <= 0.002` are skipped as a whole. Their root count is reported as `tinySubtreeRootsSkipped`; their skipped rows are included in `tinySampleRowsSkipped`. Use `" + RawFrameExtractorPath + "` for the exact raw tree if one of those frames later needs full inspection.");
        sb.AppendLine();
        sb.AppendLine("## Raw Sample Schema");
        sb.AppendLine();
        sb.AppendLine("`ai_profiler_samples.tsv` is tab-separated to reduce export time and repeated JSON field overhead.");
        sb.AppendLine("Columns:");
        sb.AppendLine();
        sb.AppendLine("- `frame`: Unity Profiler frame index.");
        sb.AppendLine("- `threadIndex`: source thread index. Join with `ai_profiler_threads.tsv` on `frame + threadIndex` for `threadName/threadGroup`.");
        sb.AppendLine("- `sampleIndex`: original Unity raw sample index within that frame/thread. Values are not contiguous because noise rows are removed.");
        sb.AppendLine("- `parentIndex`: exported parent sample index in the same frame/thread, rewired past removed noise rows, or `-1` for root.");
        sb.AppendLine("- `depth`: exported call-tree depth after noise removal.");
        sb.AppendLine("- `name`: marker/sample name.");
        sb.AppendLine("- `totalMs`: inclusive time.");
        sb.AppendLine("- `selfMs`: exclusive time.");
        sb.AppendLine("- `childCount`: direct exported child sample count after noise removal.");
        sb.AppendLine();
        sb.AppendLine("TSV escaping rule: field text uses `\\\\` for backslash, `\\t` for tab, `\\r` for carriage return, and `\\n` for newline. Reverse those escapes when reconstructing exact marker names.");
        sb.AppendLine();
        sb.AppendLine("## On-Demand Raw Frame Export");
        sb.AppendLine();
        sb.AppendLine("The main export intentionally does not write full raw samples for every frame. First use `ai_profiler_digest.json`, `ai_profiler_worstframes.json`, and `ai_profiler_worstframes_samples.json` to identify the expensive frames, then generate raw data only for those frames.");
        sb.AppendLine("The Profiler UI's current selected frame is ignored by raw request processing. Raw requests explicitly load this export's `profile.data` and parse only the requested `frame`, `frames`, or `firstFrame/lastFrame` values.");
        sb.AppendLine("Do not request every peak mechanically. Request only the frames needed to answer the current analysis question, for example one representative BRG spike and one GC spike if both are relevant.");
        sb.AppendLine("The main export does not depend on Python. The Python script and command wrapper are only fallback second-pass tools; prefer the editor-side request file when possible.");
        sb.AppendLine("Raw extraction writes to a temporary folder first and replaces the target only after Unity exits successfully, so canceling a later extraction does not delete an older successful `raw_frames/frame_*` result.");
        sb.AppendLine();
        sb.AppendLine("Lowest-permission request-file path. AI can write this file without executing local programs:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{\"exportDir\":\"" + Escape(ToProjectRelativePath(context.OutputDir)) + "\",\"frames\":[<frameIndex1>,<frameIndex2>]}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("The AI should choose this `frames` list from `ai_profiler_digest.json`, `ai_profiler_worstframes.json`, and `ai_profiler_worstframes_samples.json`; do not mechanically request every peak. Unity loads the export's `profile.data` automatically and parses only the requested frames. Each requested non-contiguous frame is written to its own `raw_frames/frame_<frameIndex>/` folder. Before writing the request, read `" + RawFrameRequestStatusPath + "`. If `profilerWindowOpen` is false, ask the user to open the Unity Profiler window. Request processing is file-event driven through Unity's editor-side watcher, not a focus-dependent polling loop. Closing the Profiler window stops auto-processing. If the result file does not update after a few seconds, report that the Profiler window is probably closed and ask the user to open it. If auto-processing is unavailable, use `Tools/AI 分析/Profiler/处理 AI Raw Frame 请求` as a manual fallback.");
        sb.AppendLine();
        sb.AppendLine("Request forms:");
        sb.AppendLine();
        sb.AppendLine("- `{\"exportDir\":\"" + Escape(ToProjectRelativePath(context.OutputDir)) + "\",\"frames\":[563,567,606]}`: preferred for AI-selected non-contiguous peak frames.");
        sb.AppendLine("- `{\"exportDir\":\"" + Escape(ToProjectRelativePath(context.OutputDir)) + "\",\"frame\":563}`: one selected frame.");
        sb.AppendLine("- `{\"exportDir\":\"" + Escape(ToProjectRelativePath(context.OutputDir)) + "\",\"firstFrame\":560,\"lastFrame\":570}`: contiguous range only when the analysis really needs a range.");
        sb.AppendLine();
        sb.AppendLine("After processing, read `" + RawFrameRequestResultPath + "`. For `frames:[...]`, use `outputDirs` to find each generated raw frame folder.");
        sb.AppendLine();
        sb.AppendLine("Command-line fallback from the Unity project root:");
        sb.AppendLine();
        sb.AppendLine("```cmd");
        sb.AppendLine(ProjectRawFrameCommandPath.Replace("/", "\\") + " <exportDir> <frameIndex>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("The export-local wrapper is also copied into this export folder:");
        sb.AppendLine();
        sb.AppendLine("```cmd");
        sb.AppendLine(RawFrameCommandPath + " <frameIndex>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Direct Python invocation remains available for custom ranges:");
        sb.AppendLine();
        sb.AppendLine("```powershell");
        sb.AppendLine("python " + RawFrameExtractorPath + " --profile " + EscapeMarkdown(ToProjectRelativePath(context.SourceProfilePath)) + " --frame <frameIndex>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Optional range form:");
        sb.AppendLine();
        sb.AppendLine("```powershell");
        sb.AppendLine("python " + RawFrameExtractorPath + " --profile " + EscapeMarkdown(ToProjectRelativePath(context.SourceProfilePath)) + " --first-frame <first> --last-frame <last>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("The script creates `raw_frames/frame_<frameIndex>/` beside this export folder and writes that folder's `ai_profiler_samples.tsv`, `ai_profiler_threads.tsv`, and summaries. Analyze those generated TSV files after the expensive frame is known.");
        sb.AppendLine();
        sb.AppendLine("## Digest Interpretation");
        sb.AppendLine();
        sb.AppendLine("- Start with `topContextsDecoded`; it expands the top 10 context hotspots into plain JSON with readable `name`, `threadName`, and `callPath`.");
        sb.AppendLine("- Prefer `topMarkerContextsBySelfMs` over marker-name-only arrays when identifying root causes.");
        sb.AppendLine("- `topMarkerContextsBySelfMs` also includes `totalMsRank`; use that instead of reading a duplicated total-time context array.");
        sb.AppendLine("- `topMarkerContextsBySelfMs` is thresholded for Deep Profile exports: tiny samples below `selfMs < 0.02ms` and `totalMs < 0.2ms` are skipped unless their marker name matches render/project keywords; non-key wrapper contexts require `totalMs >= 3.0ms`. Marker-name aggregate tops remain available in `ai_profiler_summary.json`.");
        sb.AppendLine("- `topMarkerContexts*` uses `callPathIndex`; reconstruct the path by mapping each integer through `callPathTokens`.");
        sb.AppendLine("- Repeated strings such as marker names, thread names, child hints, and reasons are encoded through the current file's `stringTable`; fields named `name`, `threadName`, `maxChildrenMarkerHint`, or `reason` may be integer indexes into that table.");
        sb.AppendLine("- `stringTable` indexes are local to each file. Do not reuse indexes between `ai_profiler_digest.json` and `ai_profiler_worstframes_samples.json`; reload the table for each file.");
        sb.AppendLine("- `callPathTokens` is separate from `stringTable`. Use `columnRefs` on each table to know whether a column references `stringTable`, `callPathTokens`, or plain numeric values.");
        sb.AppendLine("- Large arrays use `columns` + `rows`; map row values by column position instead of expecting JSON object field names on every row.");
        sb.AppendLine("- `suspectedHotspots.alreadyInTopContexts` is kept in the schema for clarity; exported rows are filtered to `false` so the list only contains anomalies outside the top context table.");
        sb.AppendLine("- Numeric profiler times are rounded to 4 decimal places.");
        sb.AppendLine("- `maxSelfFrame` and `maxTotalFrame` identify where each marker or marker context peaked.");
        sb.AppendLine("- `maxChildrenMs` and `maxChildrenMarkerHint` explain large `maxTotalMs - maxSelfMs` gaps.");
        sb.AppendLine("- `ai_profiler_worstframes.json` entries include only `topActionableSamplesBySelfMs`; noise samples and full call paths are intentionally omitted to keep the file readable.");
        sb.AppendLine("- `projectSearchHints` is structured data for code/resource search paths; use it before falling back to broad repository search.");
        sb.AppendLine("- `renderPipelineMs`, `scriptableRendererMs`, `cullingMs`, `brgMs`, `shadowsMs`, and similar frame buckets are inclusive name-based indexes. Do not add them together as real frame time.");
        sb.AppendLine("- `mainThreadNoiseAdjustedMs` subtracts common Editor/Profiler/Idle/Wait noise from `mainThreadMs` and is usually a better first sort key than raw main-thread time.");
        sb.AppendLine();
        sb.AppendLine("To inspect a hotspot, filter samples by the target `frame` and `threadIndex`, find the marker by `name`, then use `parentIndex` and `sampleIndex` to recover its local call tree.");
        sb.AppendLine();
        sb.AppendLine("## Project Code And Asset Investigation");
        sb.AppendLine();
        sb.AppendLine("Profiler data is only the first step. After identifying a hotspot, inspect the project code and assets to find the concrete suspicious implementation point.");
        sb.AppendLine("All project paths in exported metadata are relative to the Unity project root. Prefer `rg`/file search over guessing.");
        sb.AppendLine();
        sb.AppendLine("Recommended investigation order:");
        sb.AppendLine();
        sb.AppendLine("1. Search marker names, class names, method names, shader names, scene names, and renderer/pass names from `ai_profiler_digest.json` in the project.");
        sb.AppendLine("2. Inspect matching C# scripts, shaders, materials, scenes, prefabs, and URP assets before proposing fixes.");
        sb.AppendLine("3. For BRG/custom particle renderer hotspots, prioritize `Assets/Learn`, `Assets/Scripts/CustomParticle`, custom shaders, render-system scripts, active scene objects, and URP renderer settings.");
        sb.AppendLine("4. For URP/render-pass hotspots, inspect active URP Asset, Renderer asset, camera settings, shadows, SSAO, transparency, render scale, MSAA, and post-processing settings.");
        sb.AppendLine("5. For Particle markers, inspect ParticleSystemRenderer settings, material/shader, sorting mode, max particles, simulation space, trails, lights, collision, sub-emitters, and whether native renderer is still enabled.");
        sb.AppendLine("6. For GC/managed script markers, search the owning script and check per-frame allocations, LINQ, foreach over Unity collections, string building/logging, GetComponent/Find calls, and temporary arrays/lists.");
        sb.AppendLine();
        sb.AppendLine("When reporting a suspected point, include the concrete file path, object/asset path if available, method or shader pass name, and why the profiler evidence points there. If a marker cannot be mapped to project code, say so explicitly instead of inventing a source.");
        sb.AppendLine();
        sb.AppendLine("## Optimization Recommendation Rules");
        sb.AppendLine();
        sb.AppendLine("Optimization suggestions must connect profiler evidence to project evidence.");
        sb.AppendLine("For each recommendation, include:");
        sb.AppendLine();
        sb.AppendLine("- 可疑点：具体脚本/资源/设置路径。");
        sb.AppendLine("- Profiler 证据：相关 frame、thread、marker/callPath、totalMs/selfMs/count/max。");
        sb.AppendLine("- 原因判断：为什么这个代码或资源可能造成该 marker 热点。");
        sb.AppendLine("- 修改建议：具体到代码结构、设置项、资源改法或验证实验。");
        sb.AppendLine("- 风险和验证：可能影响的渲染正确性/排序/兼容性，以及下一次应该看哪些 marker。");
        sb.AppendLine();
        sb.AppendLine("## Final Report Requirements");
        sb.AppendLine();
        sb.AppendLine("The final report must be in Chinese and should not be a generic profiler explanation.");
        sb.AppendLine("It should include:");
        sb.AppendLine();
        sb.AppendLine("- 结论摘要：直接说明主要性能问题和是否与当前优化目标相关。");
        sb.AppendLine("- 关键证据：引用具体 `frame`、`marker name`、`totalMs`、`selfMs`、`count`、`avg/max`。");
        sb.AppendLine("- 噪声剔除：说明哪些 `Idle`、等待、Editor/Profiler 自身开销被排除。");
        sb.AppendLine("- 渲染相关分析：优先关注 BRG、URP、透明物体、阴影、SRPBatcher、Particle 相关 marker。");
        sb.AppendLine("- 项目证据：结合项目代码、资源、shader、scene、URP 配置，指出具体可疑点。");
        sb.AppendLine("- 建议修改：给出可执行的代码或设置方向，不要只写泛泛建议。");
        sb.AppendLine("- 后续验证：说明下一次 Profiler 应该重点看哪些 marker 或 frame。");
        sb.AppendLine();
        sb.AppendLine("## Capture Metadata");
        sb.AppendLine();
        sb.AppendLine("- Unity version: `" + EscapeMarkdown(Application.unityVersion) + "`");
        sb.AppendLine("- Path basis: `Unity project root; exported paths are project-relative when possible`");
        sb.AppendLine("- Project path: `.`");
        sb.AppendLine("- Source profile: `" + EscapeMarkdown(ToProjectRelativePath(context.SourceProfilePath)) + "`");
        sb.AppendLine("- Environment: `" + EscapeMarkdown(BuildEnvironmentSummary()) + "`");
        sb.AppendLine("- Capture scope: `" + EscapeMarkdown(context.CaptureScope) + "`");
        sb.AppendLine("- Canceled: `" + (context.Canceled ? "true" : "false") + "`");
        sb.AppendLine("- Source frame range: `" + context.SourceFirstFrame.ToString(CultureInfo.InvariantCulture) + "-" + context.SourceLastFrame.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Frame range: `" + context.FirstFrame.ToString(CultureInfo.InvariantCulture) + "-" + context.LastFrame.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Frames: `" + context.FrameCount.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Raw sample rows original: `" + context.SampleRowCount.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Raw sample rows exported: `" + context.ExportedSampleRowCount.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Noise sample rows removed: `" + context.NoiseSampleRowCount.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Noise subtree roots skipped: `" + context.NoiseSubtreeRootCount.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Estimated noise TSV bytes removed: `" + context.EstimatedNoiseSampleTsvChars.ToString(CultureInfo.InvariantCulture) + "`");
        sb.AppendLine("- Noise totalMs removed: `" + F(context.NoiseTotalMs) + "`");
        sb.AppendLine("- Noise selfMs removed: `" + F(context.NoiseSelfMs) + "`");
        sb.AppendLine("- Markers: `" + context.MarkerStats.Count.ToString(CultureInfo.InvariantCulture) + "`");

        WriteUtf8NoBom(Path.Combine(context.OutputDir, "AI_ANALYSIS_GUIDE.md"), sb.ToString());
    }

    private static void WriteRawFrameExtractorScript(ExportContext context)
    {
        string templatePath = Path.Combine(Directory.GetCurrentDirectory(), RawFrameExtractorTemplatePath);
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Raw frame extractor template not found.", templatePath);

        string script = File.ReadAllText(templatePath, Encoding.UTF8);
        WriteUtf8NoBom(Path.Combine(context.OutputDir, RawFrameExtractorPath), script);
    }

    private static void WriteRawFrameCommandScript(ExportContext context)
    {
        string profilePath = ToProjectRelativePath(context.SourceProfilePath).Replace("\"", "\"\"");
        var sb = new StringBuilder(1024);
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableDelayedExpansion");
        sb.AppendLine("if \"%~1\"==\"\" (");
        sb.AppendLine("  echo Usage: extract_raw_frame.cmd FRAME_INDEX");
        sb.AppendLine("  echo Example: extract_raw_frame.cmd " + context.FirstFrame.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("  exit /b 2");
        sb.AppendLine(")");
        sb.AppendLine("set FRAME=%~1");
        sb.AppendLine("where python >nul 2>nul");
        sb.AppendLine("if %ERRORLEVEL%==0 (");
        sb.AppendLine("  python \"%~dp0" + RawFrameExtractorPath + "\" --profile \"" + profilePath + "\" --frame %FRAME%");
        sb.AppendLine("  exit /b !ERRORLEVEL!");
        sb.AppendLine(")");
        sb.AppendLine("where py >nul 2>nul");
        sb.AppendLine("if %ERRORLEVEL%==0 (");
        sb.AppendLine("  py -3 \"%~dp0" + RawFrameExtractorPath + "\" --profile \"" + profilePath + "\" --frame %FRAME%");
        sb.AppendLine("  exit /b !ERRORLEVEL!");
        sb.AppendLine(")");
        sb.AppendLine("echo Python was not found on PATH. Install Python or run the command from a Python-enabled shell.");
        sb.AppendLine("exit /b 9009");
        WriteUtf8NoBom(Path.Combine(context.OutputDir, RawFrameCommandPath), sb.ToString());
    }

    private static MarkerNameInfo GetMarkerNameInfo(ExportContext context, string name)
    {
        name = string.IsNullOrEmpty(name) ? "<unnamed>" : name;
        if (!context.MarkerNameInfoCache.TryGetValue(name, out var info))
        {
            info = BuildMarkerNameInfo(name);
            context.MarkerNameInfoCache.Add(name, info);
        }

        return info;
    }

    private static MarkerNameInfo BuildMarkerNameInfo(string name)
    {
        name = string.IsNullOrEmpty(name) ? "<unnamed>" : name;

        var info = new MarkerNameInfo { Name = name };
        info.TsvEscapedNameChars = EstimateTsvEscapedChars(name);
        info.IsPlayerLoop = name == "PlayerLoop";
        info.IsEditorLoop = name == "EditorLoop";
        info.IsProfiler = name.StartsWith("Profiler.", StringComparison.Ordinal) ||
                          name.StartsWith("ProfilerFrameData.", StringComparison.Ordinal) ||
                          name.StartsWith("ProfilerHistory.", StringComparison.Ordinal);
        info.IsEditorConnection = name.StartsWith("EditorConnection.", StringComparison.Ordinal);
        info.IsIdle = name == "Idle";
        info.IsWait = name.IndexOf("WaitFor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      name.IndexOf("Semaphore.Wait", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsGfxWait = name.IndexOf("Gfx.Wait", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsRenderPipeline = name.IndexOf("RenderPipelineManager.DoRenderLoop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("UniversalRender", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("RenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsScriptableRenderer = name.IndexOf("ScriptableRenderer", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsTransparent = name.IndexOf("DrawTransparentObjects", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsShadows = name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsSrpBatcher = name.IndexOf("SRPBatcher", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsCulling = name.IndexOf("Cull", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("Culling", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsBrg = name.IndexOf("BatchRendererGroup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("BRG", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsParticle = name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
        info.IsGc = name.IndexOf("GarbageCollect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("GC.", StringComparison.OrdinalIgnoreCase) >= 0;
        info.ForceContextIndex = info.IsBrg ||
                                 info.IsParticle ||
                                 info.IsScriptableRenderer ||
                                 info.IsRenderPipeline ||
                                 name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 info.IsCulling ||
                                 info.IsShadows ||
                                 info.IsSrpBatcher ||
                                 info.IsGc;
        info.IsNoise = name == "<unnamed>" ||
                       info.IsIdle ||
                       name == "Semaphore.WaitForSignal" ||
                       info.IsEditorLoop ||
                       name == "Main Thread" ||
                       info.IsPlayerLoop ||
                       name == "Audio.Thread" ||
                       name == "MasterDSP" ||
                       name == "MemoryManager.FallbackAllocation" ||
                       info.IsProfiler ||
                       info.IsEditorConnection ||
                       info.IsWait;
        info.IsNoiseSubtree = info.IsProfiler ||
                              info.IsEditorConnection ||
                              info.IsIdle ||
                              info.IsWait ||
                              info.IsGfxWait ||
                              name == "Audio.Thread" ||
                              name == "MasterDSP" ||
                              name == "MemoryManager.FallbackAllocation";
        return info;
    }

    private static bool ShouldIndexMarkerContext(MarkerNameInfo marker, double totalMs, double selfMs)
    {
        if (selfMs >= ContextMinSelfMs)
            return true;

        if (marker.ForceContextIndex && totalMs >= ContextMinTotalMs)
            return true;

        return totalMs >= WrapperContextMinTotalMs;
    }

    private static bool ShouldSkipTinySample(MarkerNameInfo marker, double totalMs, double selfMs)
    {
        if (marker.ForceContextIndex)
            return false;

        return selfMs <= TinySampleSkipMaxMs && totalMs <= TinySampleSkipMaxMs;
    }

    private static bool ShouldSkipTinySubtree(MarkerNameInfo marker, double totalMs)
    {
        if (marker.ForceContextIndex)
            return false;

        return totalMs <= TinySubtreeSkipMaxMs;
    }

    private static bool ShouldSkipNoiseSubtree(MarkerNameInfo marker)
    {
        return marker.IsNoiseSubtree;
    }

    private static string BuildEnvironmentSummary()
    {
        string activeScene = string.Empty;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.IsValid())
            activeScene = scene.name;

        string qualityName = string.Empty;
        if (QualitySettings.names != null && QualitySettings.GetQualityLevel() >= 0 && QualitySettings.GetQualityLevel() < QualitySettings.names.Length)
            qualityName = QualitySettings.names[QualitySettings.GetQualityLevel()];

        string graphicsApi = SystemInfo.graphicsDeviceType.ToString();
        string activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
        string urpAssetName = QualitySettings.renderPipeline == null ? string.Empty : QualitySettings.renderPipeline.name;

        return "mode=Editor" +
               "; activeBuildTarget=" + activeBuildTarget +
               "; graphicsApi=" + graphicsApi +
               "; activeScene=" + activeScene +
               "; qualityLevel=" + QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture) +
               "; qualityName=" + qualityName +
               "; activeRenderPipelineAsset=" + urpAssetName +
               "; vSyncCount=" + QualitySettings.vSyncCount.ToString(CultureInfo.InvariantCulture) +
               "; deepProfiling=unknown" +
               "; profilerModules=unknown";
    }

    private static bool TryGetCurrentProfilerWindowFrameRange(out int firstFrame, out int lastFrame)
    {
        firstFrame = -1;
        lastFrame = -1;

        var profilerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProfilerWindow");
        if (profilerWindowType == null)
            return false;

        EditorWindow profilerWindow = null;
        if (EditorWindow.focusedWindow != null && profilerWindowType.IsInstanceOfType(EditorWindow.focusedWindow))
            profilerWindow = EditorWindow.focusedWindow;

        if (profilerWindow == null)
        {
            var windows = Resources.FindObjectsOfTypeAll(profilerWindowType);
            foreach (var windowObject in windows)
            {
                profilerWindow = windowObject as EditorWindow;
                if (profilerWindow != null)
                    break;
            }
        }

        if (profilerWindow == null)
            return false;

        if (!TryGetInstanceInt(profilerWindowType, profilerWindow, "firstAvailableFrameIndex", out firstFrame) &&
            !TryGetInstanceInt(profilerWindowType, profilerWindow, "firstFrameIndex", out firstFrame))
        {
            return false;
        }

        if (!TryGetInstanceInt(profilerWindowType, profilerWindow, "lastAvailableFrameIndex", out lastFrame) &&
            !TryGetInstanceInt(profilerWindowType, profilerWindow, "lastFrameIndex", out lastFrame))
        {
            return false;
        }

        return firstFrame >= 0 && lastFrame >= firstFrame;
    }

    private static void LoadProfile(string inputPath)
    {
        var profilerDriverType = GetProfilerDriverType();
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Profiler input file not found.", inputPath);

        if (!TryInvokeStatic(profilerDriverType, "LoadProfile", inputPath, false))
        {
            if (!TryInvokeStatic(profilerDriverType, "LoadProfile", inputPath))
                throw new MissingMethodException(profilerDriverType.FullName, "LoadProfile");
        }
    }

    private static void TrySaveProfile(Type profilerDriverType, string profilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath));
        if (TryInvokeStatic(profilerDriverType, "SaveProfile", profilePath))
            return;

        if (TryInvokeStatic(profilerDriverType, "SaveProfile", profilePath, false))
            return;

        if (TryInvokeStatic(profilerDriverType, "SaveProfile", profilePath, true))
            return;

        Debug.LogWarning("ProfilerDriver.SaveProfile(string) not found. Parsed files will still be exported from the loaded profiler data.");
    }

    private static Type GetProfilerDriverType()
    {
        var unityEditorAsm = typeof(EditorApplication).Assembly;
        var profilerDriverType = unityEditorAsm.GetType("UnityEditorInternal.ProfilerDriver");
        if (profilerDriverType == null)
            throw new Exception("UnityEditorInternal.ProfilerDriver not found.");

        return profilerDriverType;
    }

    private static bool TryInvokeStatic(Type type, string name, params object[] args)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name != name)
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != args.Length)
                continue;

            bool compatible = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (args[i] == null)
                    continue;

                if (!parameters[i].ParameterType.IsInstanceOfType(args[i]) && parameters[i].ParameterType != args[i].GetType())
                {
                    compatible = false;
                    break;
                }
            }

            if (!compatible)
                continue;

            method.Invoke(null, args);
            return true;
        }

        return false;
    }

    private static int GetStaticInt(Type type, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (prop != null)
            return Convert.ToInt32(prop.GetValue(null));

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null)
            return Convert.ToInt32(field.GetValue(null));

        throw new MissingMemberException(type.FullName, name);
    }

    private static bool GetBool(Type type, object target, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null)
            throw new MissingMemberException(type.FullName, name);

        return Convert.ToBoolean(prop.GetValue(target));
    }

    private static int GetInt(Type type, object target, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null)
            throw new MissingMemberException(type.FullName, name);

        return Convert.ToInt32(prop.GetValue(target));
    }

    private static bool TryGetInstanceInt(Type type, object target, string name, out int value)
    {
        value = 0;

        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null)
        {
            value = Convert.ToInt32(prop.GetValue(target), CultureInfo.InvariantCulture);
            return true;
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            value = Convert.ToInt32(field.GetValue(target), CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static string GetString(Type type, object target, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null)
            return string.Empty;

        return Convert.ToString(prop.GetValue(target));
    }

    private static string CallStringOptional(Type type, object target, int arg, params string[] names)
    {
        foreach (var name in names)
        {
            var method = FindInstanceMethod(type, name, typeof(int));
            if (method != null)
                return Convert.ToString(method.Invoke(target, new object[] { arg }));
        }

        return string.Empty;
    }

    private static double CallDoubleOptional(Type type, object target, int arg, params string[] names)
    {
        foreach (var name in names)
        {
            var method = FindInstanceMethod(type, name, typeof(int));
            if (method != null)
                return Convert.ToDouble(method.Invoke(target, new object[] { arg }), CultureInfo.InvariantCulture);
        }

        return 0;
    }

    private static int CallIntOptional(Type type, object target, int arg, params string[] names)
    {
        foreach (var name in names)
        {
            var method = FindInstanceMethod(type, name, typeof(int));
            if (method != null)
                return Convert.ToInt32(method.Invoke(target, new object[] { arg }), CultureInfo.InvariantCulture);
        }

        return 0;
    }

    private static MethodInfo FindInstanceMethod(Type type, string name, params Type[] parameterTypes)
    {
        return type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            parameterTypes,
            null);
    }

    private static PropertyInfo GetProperty(Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static Func<object, T> CompilePropertyGetter<T>(Type declaringType, PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var typedInstance = Expression.Convert(instance, declaringType);
        var propertyAccess = Expression.Property(typedInstance, property);
        var converted = Expression.Convert(propertyAccess, typeof(T));
        return Expression.Lambda<Func<object, T>>(converted, instance).Compile();
    }

    private static Func<object, int, T> CompileIntMethod<T>(Type declaringType, MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var index = Expression.Parameter(typeof(int), "index");
        var typedInstance = Expression.Convert(instance, declaringType);
        var call = Expression.Call(typedInstance, method, index);
        var converted = Expression.Convert(call, typeof(T));
        return Expression.Lambda<Func<object, int, T>>(converted, instance, index).Compile();
    }

    private static Func<int, int, object> CompileStaticFrameThreadMethod(MethodInfo method)
    {
        var frame = Expression.Parameter(typeof(int), "frame");
        var thread = Expression.Parameter(typeof(int), "thread");
        var call = Expression.Call(method, frame, thread);
        var converted = Expression.Convert(call, typeof(object));
        return Expression.Lambda<Func<int, int, object>>(converted, frame, thread).Compile();
    }

    private static RawFrameDataViewApi GetRawFrameDataViewApi(Type type)
    {
        if (!RawFrameDataViewApis.TryGetValue(type, out var api))
        {
            api = new RawFrameDataViewApi(type);
            RawFrameDataViewApis.Add(type, api);
        }

        return api;
    }

    private static void AssignTreeMetadata(
        int[] childCounts,
        int[] recursiveChildCounts,
        int[] depths,
        int[] parentIndices)
    {
        int sampleCount = depths.Length;
        var ancestorSamples = new int[Math.Min(sampleCount, 256)];
        var ancestorEnds = new int[ancestorSamples.Length];
        int stackSize = 0;

        for (int sample = 0; sample < sampleCount; sample++)
        {
            while (stackSize > 0 && sample > ancestorEnds[stackSize - 1])
                stackSize--;

            depths[sample] = stackSize;
            parentIndices[sample] = stackSize > 0 ? ancestorSamples[stackSize - 1] : -1;

            if (childCounts[sample] <= 0)
                continue;

            if (stackSize == ancestorSamples.Length)
            {
                Array.Resize(ref ancestorSamples, ancestorSamples.Length * 2);
                Array.Resize(ref ancestorEnds, ancestorEnds.Length * 2);
            }

            ancestorSamples[stackSize] = sample;
            ancestorEnds[stackSize] = Math.Min(sampleCount - 1, sample + Math.Max(0, recursiveChildCounts[sample]));
            stackSize++;
        }
    }

    private static string GetCallPath(ExportContext context, int sample, int[] parentIndices, string[] sampleNames)
    {
        var key = BuildCallPathCacheKey(sample, parentIndices, sampleNames);
        if (context.CallPathCache.TryGetValue(key, out string callPath))
            return callPath;

        callPath = BuildCallPath(key);
        context.CallPathCache.Add(key, callPath);
        return callPath;
    }

    private static CallPathCacheKey BuildCallPathCacheKey(int sample, int[] parentIndices, string[] sampleNames)
    {
        const int maxDepth = 10;
        var key = new CallPathCacheKey();
        int cursor = sample;
        int count = 0;
        int hash = 17;

        while (cursor >= 0 && cursor < sampleNames.Length && count < maxDepth)
        {
            string name = sampleNames[cursor];
            if (string.IsNullOrEmpty(name))
                name = "<unnamed>";
            SetCallPathName(ref key, count, name);
            unchecked
            {
                hash = (hash * 397) ^ name.GetHashCode();
            }
            count++;
            cursor = parentIndices[cursor];
        }

        key.Count = count;
        unchecked
        {
            key.Hash = (hash * 397) ^ count;
        }
        return key;
    }

    private static string BuildCallPath(CallPathCacheKey key)
    {
        int count = key.Count;
        if (count == 0)
            return "<unnamed>";

        int totalLength = 0;
        for (int i = 0; i < count; i++)
            totalLength += GetCallPathName(key, i).Length;

        var sb = new StringBuilder(totalLength + Math.Max(0, count - 1) * 3);
        for (int i = count - 1; i >= 0; i--)
        {
            if (i != count - 1)
                sb.Append(" > ");
            sb.Append(GetCallPathName(key, i));
        }

        return sb.ToString();
    }

    private static void SetCallPathName(ref CallPathCacheKey key, int index, string name)
    {
        switch (index)
        {
            case 0: key.N0 = name; break;
            case 1: key.N1 = name; break;
            case 2: key.N2 = name; break;
            case 3: key.N3 = name; break;
            case 4: key.N4 = name; break;
            case 5: key.N5 = name; break;
            case 6: key.N6 = name; break;
            case 7: key.N7 = name; break;
            case 8: key.N8 = name; break;
            case 9: key.N9 = name; break;
        }
    }

    private static string GetCallPathName(CallPathCacheKey key, int index)
    {
        switch (index)
        {
            case 0: return key.N0;
            case 1: return key.N1;
            case 2: return key.N2;
            case 3: return key.N3;
            case 4: return key.N4;
            case 5: return key.N5;
            case 6: return key.N6;
            case 7: return key.N7;
            case 8: return key.N8;
            case 9: return key.N9;
            default: return string.Empty;
        }
    }

    private static string FindLargestDirectChild(
        int sample,
        double[] sampleTimes,
        int[] childCounts,
        int[] recursiveChildCounts,
        string[] sampleNames)
    {
        string childMarkerHint = string.Empty;
        double childMarkerMs = 0;
        string parentName = string.IsNullOrEmpty(sampleNames[sample]) ? "<unnamed>" : sampleNames[sample];

        int child = sample + 1;
        for (int i = 0; i < childCounts[sample] && child < sampleTimes.Length; i++)
        {
            string childName = string.IsNullOrEmpty(sampleNames[child]) ? "<unnamed>" : sampleNames[child];
            if (string.Equals(childName, parentName, StringComparison.Ordinal))
            {
                child += 1 + Math.Max(0, recursiveChildCounts[child]);
                continue;
            }

            double time = sampleTimes[child];
            if (time > childMarkerMs)
            {
                childMarkerMs = time;
                childMarkerHint = childName;
            }

            child += 1 + Math.Max(0, recursiveChildCounts[child]);
        }

        return childMarkerHint;
    }

    private static string CreateTimestampedOutputDir()
    {
        string dirName = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(Directory.GetCurrentDirectory(), "ProfilerAIExports", dirName);
    }

    private static void CreateTimestampedExportPaths(out string outputDir, out string profilePath)
    {
        string dirName = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string root = Path.Combine(Directory.GetCurrentDirectory(), "ProfilerAIExports");
        outputDir = Path.Combine(root, dirName);
        profilePath = Path.Combine(outputDir, "profile.data");
    }

    private static string ResolveProjectRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
            return path;

        return Path.Combine(Directory.GetCurrentDirectory(), path);
    }

    private static string GetRawFrameRequestEditorPrefsKey()
    {
        return "ProfilerAI.RawFrameRequestProcessedTicks." + Application.dataPath;
    }

    private static string FindSourceProfilePathFromExport(string exportDir)
    {
        string[] metadataFiles =
        {
            Path.Combine(exportDir, "ai_profiler_summary.json"),
            Path.Combine(exportDir, "ai_profiler_digest.json")
        };

        foreach (string metadataFile in metadataFiles)
        {
            if (!File.Exists(metadataFile))
                continue;

            var metadata = ReadExportMetadata(File.ReadAllText(metadataFile, Encoding.UTF8));
            if (metadata != null && !string.IsNullOrEmpty(metadata.sourceProfilePath))
                return ResolveProjectRelativePath(metadata.sourceProfilePath);
        }

        string fallback = Path.Combine(exportDir, "profile.data");
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException("Could not find sourceProfilePath in export metadata or profile.data in export folder.", exportDir);
    }

    private static RawFrameRequest ReadRawFrameRequest(string json)
    {
        return new RawFrameRequest
        {
            exportDir = ExtractJsonString(json, "exportDir"),
            profilePath = ExtractJsonString(json, "profilePath"),
            frames = ExtractJsonIntArray(json, "frames"),
            frame = ExtractJsonInt(json, "frame", -1),
            firstFrame = ExtractJsonInt(json, "firstFrame", -1),
            lastFrame = ExtractJsonInt(json, "lastFrame", -1),
            outDir = ExtractJsonString(json, "outDir")
        };
    }

    private static ExportMetadata ReadExportMetadata(string json)
    {
        return new ExportMetadata
        {
            sourceProfilePath = ExtractJsonString(json, "sourceProfilePath")
        };
    }

    private static string ExtractJsonString(string json, string key)
    {
        int valueStart = FindJsonValueStart(json, key);
        if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '"')
            return string.Empty;

        valueStart++;
        var sb = new StringBuilder();
        bool escaping = false;
        for (int i = valueStart; i < json.Length; i++)
        {
            char c = json[i];
            if (escaping)
            {
                switch (c)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(c); break;
                }
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (c == '"')
                return sb.ToString();

            sb.Append(c);
        }

        return string.Empty;
    }

    private static int ExtractJsonInt(string json, string key, int fallback)
    {
        int valueStart = FindJsonValueStart(json, key);
        if (valueStart < 0)
            return fallback;

        int i = valueStart;
        if (i < json.Length && json[i] == '-')
            i++;

        while (i < json.Length && char.IsDigit(json[i]))
            i++;

        if (i == valueStart || (i == valueStart + 1 && json[valueStart] == '-'))
            return fallback;

        return int.TryParse(json.Substring(valueStart, i - valueStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    private static int[] ExtractJsonIntArray(string json, string key)
    {
        int valueStart = FindJsonValueStart(json, key);
        if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '[')
            return null;

        int end = json.IndexOf(']', valueStart + 1);
        if (end < 0)
            return null;

        string body = json.Substring(valueStart + 1, end - valueStart - 1);
        var values = new List<int>();
        int i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ','))
                i++;

            int start = i;
            if (i < body.Length && body[i] == '-')
                i++;

            while (i < body.Length && char.IsDigit(body[i]))
                i++;

            if (i > start && int.TryParse(body.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                values.Add(value);

            while (i < body.Length && body[i] != ',')
                i++;
        }

        return values.Count == 0 ? null : values.ToArray();
    }

    private static int FindJsonValueStart(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
            return -1;

        string quotedKey = "\"" + key + "\"";
        int keyIndex = json.IndexOf(quotedKey, StringComparison.Ordinal);
        if (keyIndex < 0)
            return -1;

        int colonIndex = json.IndexOf(':', keyIndex + quotedKey.Length);
        if (colonIndex < 0)
            return -1;

        int valueStart = colonIndex + 1;
        while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
            valueStart++;

        return valueStart;
    }

    private static void ReplaceDirectoryAtomically(string stagingDir, string outputDir, string stamp)
    {
        string backupDir = null;
        if (Directory.Exists(outputDir))
        {
            backupDir = outputDir + ".previous_" + stamp;
            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, true);
            Directory.Move(outputDir, backupDir);
        }

        try
        {
            Directory.Move(stagingDir, outputDir);
        }
        catch
        {
            if (!Directory.Exists(outputDir) && !string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
                Directory.Move(backupDir, outputDir);
            throw;
        }

        if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
            Directory.Delete(backupDir, true);
    }

    private static void WriteRawFrameRequestResult(string status, List<string> outputDirs, int firstFrame, int lastFrame, int sampleRows, string error)
    {
        string resultPath = Path.Combine(Directory.GetCurrentDirectory(), RawFrameRequestResultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
        var sb = new StringBuilder(512);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "status", status, true);
        WriteJsonProperty(sb, "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
        WriteJsonProperty(sb, "outputDir", outputDirs != null && outputDirs.Count > 0 ? ToProjectRelativePath(outputDirs[0]) : string.Empty, true);
        WriteStringArray(sb, "outputDirs", outputDirs == null ? new string[0] : outputDirs.Select(ToProjectRelativePath).ToArray(), true);
        WriteJsonProperty(sb, "firstFrame", firstFrame, true);
        WriteJsonProperty(sb, "lastFrame", lastFrame, true);
        WriteJsonProperty(sb, "sampleRows", sampleRows, true);
        WriteJsonProperty(sb, "error", error ?? string.Empty, false);
        sb.AppendLine("}");
        WriteUtf8NoBom(resultPath, sb.ToString());
    }

    private static string ToProjectRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        try
        {
            string root = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                return ".";

            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(rootWithSeparator.Length).Replace('\\', '/');
        }
        catch
        {
            // Metadata only: avoid leaking machine-specific absolute paths if normalization fails.
        }

        string fileName = Path.GetFileName(path);
        return string.IsNullOrEmpty(fileName) ? "external" : "external:" + fileName;
    }

    private static string BuildExportFinishedMessage(ExportContext context)
    {
        return "Profiler AI 导出" + (context.Canceled ? "已取消（保留已完成帧）" : "完成") + "：" + ToProjectRelativePath(context.OutputDir) +
               " | total=" + context.TotalExportMs.ToString(CultureInfo.InvariantCulture) + "ms" +
               ", parse=" + context.FrameParseAndQueueMs.ToString(CultureInfo.InvariantCulture) + "ms" +
               ", rawFinalize=" + context.RawWriterFinalizeMs.ToString(CultureInfo.InvariantCulture) + "ms" +
               ", summary=" + context.SummaryWriteMs.ToString(CultureInfo.InvariantCulture) + "ms" +
               ", readSamples=" + F(context.TimingReadSamplesMs) + "ms" +
               ", tree=" + F(context.TimingTreeMs) + "ms" +
               ", aggregate=" + F(context.TimingAggregateMs) + "ms" +
               ", rawTsvBuild=" + F(context.TimingRawTsvBuildMs) + "ms" +
               ", samples=" + context.ExportedSampleRowCount.ToString(CultureInfo.InvariantCulture) +
               "/" + context.SampleRowCount.ToString(CultureInfo.InvariantCulture) +
               ", rawSamples=" + (context.IncludeRawSamples ? "exported" : "skipped");
    }

    private static double ElapsedMs(long startTicks)
    {
        return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static string GetCommandLineValue(string key, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return fallback;
    }

    private static bool TryGetCommandLineInt(string key, out int value)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        value = 0;
        return false;
    }

    private static void ShowExportProgress(string stage, int firstFrame, int lastFrame, int currentFrame, float progress, bool cancelAtFrameBoundary = true)
    {
        if (Application.isBatchMode)
            return;

        progress = Math.Max(0f, Math.Min(1f, progress));
        string info = "帧范围 " + firstFrame.ToString(CultureInfo.InvariantCulture) +
                      "-" + lastFrame.ToString(CultureInfo.InvariantCulture) +
                      "，当前 " + currentFrame.ToString(CultureInfo.InvariantCulture) +
                      (exportCancelRequested
                          ? "\n已请求取消。当前帧处理完后写入部分结果。"
                          : "\n点击 Cancel 会在当前帧后停止，并保留已完成帧。");

        if (EditorUtility.DisplayCancelableProgressBar("Profiler AI 导出", stage + "\n" + info, progress))
            exportCancelRequested = true;

        if (cancelAtFrameBoundary && exportCancelRequested)
            throw new OperationCanceledException("Profiler AI 导出已取消。");
    }

    private static void AppendSampleTsvLine(
        StringBuilder writer,
        int frame,
        int threadIndex,
        int sampleIndex,
        int parentIndex,
        int depth,
        string name,
        double totalMs,
        double selfMs,
        int childCount)
    {
        writer.Append(frame);
        writer.Append('\t');
        writer.Append(threadIndex);
        writer.Append('\t');
        writer.Append(sampleIndex);
        writer.Append('\t');
        writer.Append(parentIndex);
        writer.Append('\t');
        writer.Append(depth);
        writer.Append('\t');
        writer.Append(Tsv(name));
        writer.Append('\t');
        writer.Append(F(totalMs));
        writer.Append('\t');
        writer.Append(F(selfMs));
        writer.Append('\t');
        writer.Append(childCount);
        writer.Append('\n');
    }

    private static int EstimateTsvEscapedChars(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            count += c == '\\' || c == '\t' || c == '\r' || c == '\n' ? 2 : 1;
        }

        return count;
    }

    private static string BuildThreadTsvLine(int frame, int threadIndex, string threadGroup, string threadName, int sampleCount, int exportedSampleCount, double totalMs)
    {
        var sb = new StringBuilder(128);
        sb.Append(frame);
        sb.Append('\t');
        sb.Append(threadIndex);
        sb.Append('\t');
        sb.Append(Tsv(threadGroup));
        sb.Append('\t');
        sb.Append(Tsv(threadName));
        sb.Append('\t');
        sb.Append(sampleCount);
        sb.Append('\t');
        sb.Append(exportedSampleCount);
        sb.Append('\t');
        sb.Append(F(totalMs));
        sb.Append('\n');
        return sb.ToString();
    }

    private static string BuildTimingTsvLine(
        int frame,
        int threadIndex,
        string threadName,
        int sampleCount,
        int exportedSampleCount,
        double getViewMs,
        double metaMs,
        double readSamplesMs,
        double treeMs,
        double aggregateMs,
        double rawTsvBuildMs,
        double writerEnqueueMs,
        double disposeViewMs,
        double threadWallMs)
    {
        var sb = new StringBuilder(160);
        sb.Append(frame);
        sb.Append('\t');
        sb.Append(threadIndex);
        sb.Append('\t');
        sb.Append(Tsv(threadName));
        sb.Append('\t');
        sb.Append(sampleCount);
        sb.Append('\t');
        sb.Append(exportedSampleCount);
        sb.Append('\t');
        sb.Append(F(getViewMs));
        sb.Append('\t');
        sb.Append(F(metaMs));
        sb.Append('\t');
        sb.Append(F(readSamplesMs));
        sb.Append('\t');
        sb.Append(F(treeMs));
        sb.Append('\t');
        sb.Append(F(aggregateMs));
        sb.Append('\t');
        sb.Append(F(rawTsvBuildMs));
        sb.Append('\t');
        sb.Append(F(writerEnqueueMs));
        sb.Append('\t');
        sb.Append(F(disposeViewMs));
        sb.Append('\t');
        sb.Append(F(threadWallMs));
        sb.Append('\n');
        return sb.ToString();
    }

    private static void WriteMarkerJsonLine(StreamWriter writer, MarkerStat stat)
    {
        writer.Write("{\"name\":");
        writer.Write(Json(stat.Name));
        writer.Write(",\"totalMs\":");
        writer.Write(F(stat.TotalMs));
        writer.Write(",\"selfMs\":");
        writer.Write(F(stat.SelfMs));
        writer.Write(",\"count\":");
        writer.Write(stat.Count);
        writer.Write(",\"avgTotalMs\":");
        writer.Write(F(stat.TotalMs / Math.Max(1, stat.Count)));
        writer.Write(",\"avgSelfMs\":");
        writer.Write(F(stat.SelfMs / Math.Max(1, stat.Count)));
        writer.Write(",\"maxTotalMs\":");
        writer.Write(F(stat.MaxTotalMs));
        writer.Write(",\"maxSelfMs\":");
        writer.Write(F(stat.MaxSelfMs));
        writer.Write(",\"maxTotalFrame\":");
        writer.Write(stat.MaxTotalFrame);
        writer.Write(",\"maxSelfFrame\":");
        writer.Write(stat.MaxSelfFrame);
        writer.Write(",\"firstFrame\":");
        writer.Write(stat.FirstFrame);
        writer.Write(",\"lastFrame\":");
        writer.Write(stat.LastFrame);
        writer.WriteLine("}");
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, string value, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).Append("\": ").Append(Json(value));
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteUtf8NoBom(string path, string content)
    {
        File.WriteAllText(path, content, Utf8NoBom);
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, int value, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, long value, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, double value, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).Append("\": ").Append(F(value));
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, bool value, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).Append("\": ").Append(value ? "true" : "false");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static double Percent(long value, long total)
    {
        if (total <= 0)
            return 0;

        return value * 100.0 / total;
    }

    private static void WriteMarkerArray(StringBuilder sb, string name, List<MarkerStat> markers, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": [");
        for (int i = 0; i < markers.Count; i++)
        {
            var stat = markers[i];
            sb.Append("    {");
            sb.Append("\"name\": ").Append(Json(stat.Name));
            sb.Append(", \"selfMs\": ").Append(F(stat.SelfMs));
            sb.Append(", \"totalMs\": ").Append(F(stat.TotalMs));
            sb.Append(", \"count\": ").Append(stat.Count);
            sb.Append(", \"avgSelfMs\": ").Append(F(stat.SelfMs / Math.Max(1, stat.Count)));
            sb.Append(", \"avgTotalMs\": ").Append(F(stat.TotalMs / Math.Max(1, stat.Count)));
            sb.Append(", \"maxSelfMs\": ").Append(F(stat.MaxSelfMs));
            sb.Append(", \"maxTotalMs\": ").Append(F(stat.MaxTotalMs));
            sb.Append(", \"maxSelfFrame\": ").Append(stat.MaxSelfFrame);
            sb.Append(", \"maxTotalFrame\": ").Append(stat.MaxTotalFrame);
            sb.Append(", \"firstFrame\": ").Append(stat.FirstFrame);
            sb.Append(", \"lastFrame\": ").Append(stat.LastFrame);
            sb.Append(i == markers.Count - 1 ? "}" : "},");
            sb.AppendLine();
        }

        sb.Append("  ]");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static Dictionary<string, int> BuildMarkerRanks(List<MarkerStat> markers)
    {
        var ranks = new Dictionary<string, int>();
        for (int i = 0; i < markers.Count; i++)
            if (!ranks.ContainsKey(markers[i].Name))
                ranks.Add(markers[i].Name, i + 1);
        return ranks;
    }

    private static void WriteMarkerRankedArray(StringBuilder sb, string name, List<MarkerStat> markers, Dictionary<string, int> selfRanks, Dictionary<string, int> totalRanks, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": [");
        for (int i = 0; i < markers.Count; i++)
        {
            var stat = markers[i];
            sb.Append("    {");
            sb.Append("\"name\": ").Append(Json(stat.Name));
            sb.Append(", \"selfMsRank\": ").Append(selfRanks.TryGetValue(stat.Name, out int selfRank) ? selfRank : 0);
            sb.Append(", \"totalMsRank\": ").Append(totalRanks.TryGetValue(stat.Name, out int totalRank) ? totalRank : 0);
            sb.Append(", \"selfMs\": ").Append(F(stat.SelfMs));
            sb.Append(", \"totalMs\": ").Append(F(stat.TotalMs));
            sb.Append(", \"count\": ").Append(stat.Count);
            sb.Append(", \"avgSelfMs\": ").Append(F(stat.SelfMs / Math.Max(1, stat.Count)));
            sb.Append(", \"avgTotalMs\": ").Append(F(stat.TotalMs / Math.Max(1, stat.Count)));
            sb.Append(", \"maxSelfMs\": ").Append(F(stat.MaxSelfMs));
            sb.Append(", \"maxTotalMs\": ").Append(F(stat.MaxTotalMs));
            sb.Append(", \"maxSelfFrame\": ").Append(stat.MaxSelfFrame);
            sb.Append(", \"maxTotalFrame\": ").Append(stat.MaxTotalFrame);
            sb.Append(", \"firstFrame\": ").Append(stat.FirstFrame);
            sb.Append(", \"lastFrame\": ").Append(stat.LastFrame);
            sb.Append(i == markers.Count - 1 ? "}" : "},");
            sb.AppendLine();
        }

        sb.Append("  ]");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static CallPathTokenTable BuildCallPathTokenTable(IEnumerable<MarkerContextStat> markers)
    {
        var table = new CallPathTokenTable();
        foreach (var marker in markers)
            foreach (string token in SplitCallPath(marker.CallPath))
                table.GetIndex(token);

        return table;
    }

    private static Dictionary<MarkerContextStat, int> BuildContextRanksByTotal(IEnumerable<MarkerContextStat> markers)
    {
        var ranks = new Dictionary<MarkerContextStat, int>();
        int rank = 1;
        foreach (var marker in markers.OrderByDescending(s => s.TotalMs).ThenByDescending(s => s.SelfMs))
            ranks[marker] = rank++;

        return ranks;
    }

    private static StringTable BuildDigestStringTable(IEnumerable<MarkerContextStat> contexts, IEnumerable<SuspectedHotspot> hotspots)
    {
        var table = new StringTable();
        foreach (var context in contexts)
        {
            table.GetIndex(context.Name);
            table.GetIndex(context.ThreadName);
            table.GetIndex(context.MaxChildrenMarkerHint);
        }

        foreach (var hotspot in hotspots)
        {
            table.GetIndex(hotspot.Stat.Name);
            table.GetIndex(hotspot.Stat.MaxChildrenMarkerHint);
            table.GetIndex(hotspot.Reason);
        }

        return table;
    }

    private static List<SuspectedHotspot> BuildSuspectedHotspots(IEnumerable<MarkerContextStat> markers, Dictionary<MarkerContextStat, int> totalRanks)
    {
        var rankedBySelf = markers
            .OrderByDescending(s => s.SelfMs)
            .ThenByDescending(s => s.TotalMs)
            .ToList();

        var selfRanks = new Dictionary<MarkerContextStat, int>();
        for (int i = 0; i < rankedBySelf.Count; i++)
            selfRanks[rankedBySelf[i]] = i + 1;

        var results = new List<SuspectedHotspot>();
        foreach (var stat in rankedBySelf)
        {
            int selfRank = selfRanks[stat];
            int totalRank = totalRanks.TryGetValue(stat, out int foundTotalRank) ? foundTotalRank : 0;
            double avgSelfMs = stat.SelfMs / Math.Max(1, stat.Count);
            double spikeRatio = avgSelfMs > 0.0001 ? stat.MaxSelfMs / avgSelfMs : 0;
            double childGapRatio = stat.MaxTotalMs > 0.0001 ? stat.MaxChildrenMs / stat.MaxTotalMs : 0;
            string reason = null;
            double score = 0;

            if (selfRank > DigestTopMarkerCount &&
                     childGapRatio >= 0.6 &&
                     stat.MaxChildrenMs >= 0.25 &&
                     !string.Equals(stat.MaxChildrenMarkerHint, stat.Name, StringComparison.Ordinal))
            {
                reason = "large child-time gap: " + (string.IsNullOrEmpty(stat.MaxChildrenMarkerHint) ? "unknown child" : stat.MaxChildrenMarkerHint);
                score = 700 + childGapRatio * 100 + stat.MaxChildrenMs;
            }
            else if (spikeRatio >= 4 && stat.MaxSelfMs >= 0.15)
            {
                reason = "spiky maxSelfMs versus average self time";
                score = 500 + spikeRatio + stat.MaxSelfMs;
            }
            else if (totalRank > 0 && totalRank <= 8 && selfRank > 20)
            {
                reason = "high total time rank but lower self time rank";
                score = 400 + (20 - totalRank);
            }

            if (reason == null)
                continue;

            results.Add(new SuspectedHotspot
            {
                Stat = stat,
                SelfMsRank = selfRank,
                TotalMsRank = totalRank,
                Reason = reason,
                Score = score,
                AlreadyInTopContexts = selfRank <= DigestTopMarkerCount
            });
        }

        return results
            .Where(s => !s.AlreadyInTopContexts)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.SelfMsRank)
            .Take(12)
            .ToList();
    }

    private static string[] SplitCallPath(string callPath)
    {
        if (string.IsNullOrEmpty(callPath))
            return new[] { "<unnamed>" };

        return callPath.Split(new[] { " > " }, StringSplitOptions.None);
    }

    private static void WriteCallPathTokenArray(StringBuilder sb, CallPathTokenTable table, bool trailingComma)
    {
        sb.AppendLine("  \"callPathTokens\": [");
        for (int i = 0; i < table.Tokens.Count; i++)
        {
            sb.Append("    ").Append(Json(table.Tokens[i]));
            sb.Append(i == table.Tokens.Count - 1 ? string.Empty : ",");
            sb.AppendLine();
        }

        sb.Append("  ]");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteStringTable(StringBuilder sb, StringTable table, bool trailingComma)
    {
        sb.AppendLine("  \"stringTable\": [");
        for (int i = 0; i < table.Values.Count; i++)
        {
            sb.Append("    ").Append(Json(table.Values[i]));
            sb.Append(i == table.Values.Count - 1 ? string.Empty : ",");
            sb.AppendLine();
        }

        sb.Append("  ]");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteCallPathIndexInline(StringBuilder sb, CallPathTokenTable table, string callPath)
    {
        string[] tokens = SplitCallPath(callPath);
        sb.Append("[");
        for (int i = 0; i < tokens.Length; i++)
        {
            sb.Append(table.GetIndex(tokens[i]));
            sb.Append(i == tokens.Length - 1 ? string.Empty : ",");
        }

        sb.Append("]");
    }

    private static void WriteMarkerContextTable(StringBuilder sb, string name, List<MarkerContextStat> markers, Dictionary<MarkerContextStat, int> totalRanks, CallPathTokenTable callPathTable, StringTable stringTable, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": {");
        sb.AppendLine("    \"columns\": [\"name\",\"threadName\",\"callPathIndex\",\"selfMs\",\"totalMs\",\"selfMsRank\",\"totalMsRank\",\"count\",\"avgSelfMs\",\"avgTotalMs\",\"maxSelfMs\",\"maxTotalMs\",\"maxSelfFrame\",\"maxTotalFrame\",\"maxChildrenMs\",\"maxChildrenMarkerHint\",\"maxFrame\",\"maxThreadIndex\",\"maxSampleIndex\",\"firstFrame\",\"lastFrame\"],");
        sb.AppendLine("    \"columnRefs\": {\"name\":\"stringTable\",\"threadName\":\"stringTable\",\"callPathIndex\":\"callPathTokens\",\"maxChildrenMarkerHint\":\"stringTable\"},");
        sb.AppendLine("    \"rows\": [");
        for (int i = 0; i < markers.Count; i++)
        {
            var stat = markers[i];
            sb.Append("      [");
            sb.Append(stringTable.GetIndex(stat.Name)).Append(",");
            sb.Append(stringTable.GetIndex(stat.ThreadName)).Append(",");
            WriteCallPathIndexInline(sb, callPathTable, stat.CallPath);
            sb.Append(",").Append(F(stat.SelfMs));
            sb.Append(",").Append(F(stat.TotalMs));
            sb.Append(",").Append(i + 1);
            sb.Append(",").Append(totalRanks.TryGetValue(stat, out int totalRank) ? totalRank : 0);
            sb.Append(",").Append(stat.Count);
            sb.Append(",").Append(F(stat.SelfMs / Math.Max(1, stat.Count)));
            sb.Append(",").Append(F(stat.TotalMs / Math.Max(1, stat.Count)));
            sb.Append(",").Append(F(stat.MaxSelfMs));
            sb.Append(",").Append(F(stat.MaxTotalMs));
            sb.Append(",").Append(stat.MaxSelfFrame);
            sb.Append(",").Append(stat.MaxTotalFrame);
            sb.Append(",").Append(F(stat.MaxChildrenMs));
            sb.Append(",").Append(stringTable.GetIndex(stat.MaxChildrenMarkerHint));
            sb.Append(",").Append(stat.MaxFrame);
            sb.Append(",").Append(stat.MaxThreadIndex);
            sb.Append(",").Append(stat.MaxSampleIndex);
            sb.Append(",").Append(stat.FirstFrame);
            sb.Append(",").Append(stat.LastFrame);
            sb.Append(i == markers.Count - 1 ? "]" : "],");
            sb.AppendLine();
        }

        sb.AppendLine("    ]");
        sb.Append("  }");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteTopContextsDecoded(StringBuilder sb, string name, List<MarkerContextStat> markers, Dictionary<MarkerContextStat, int> totalRanks, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": [");
        for (int i = 0; i < markers.Count; i++)
        {
            var stat = markers[i];
            sb.Append("    {");
            sb.Append("\"rank\": ").Append(i + 1);
            sb.Append(", \"name\": ").Append(Json(stat.Name));
            sb.Append(", \"threadName\": ").Append(Json(stat.ThreadName));
            sb.Append(", \"callPath\": ").Append(Json(stat.CallPath));
            sb.Append(", \"selfMs\": ").Append(F(stat.SelfMs));
            sb.Append(", \"totalMs\": ").Append(F(stat.TotalMs));
            sb.Append(", \"totalMsRank\": ").Append(totalRanks.TryGetValue(stat, out int totalRank) ? totalRank : 0);
            sb.Append(", \"count\": ").Append(stat.Count);
            sb.Append(", \"avgSelfMs\": ").Append(F(stat.SelfMs / Math.Max(1, stat.Count)));
            sb.Append(", \"avgTotalMs\": ").Append(F(stat.TotalMs / Math.Max(1, stat.Count)));
            sb.Append(", \"maxSelfMs\": ").Append(F(stat.MaxSelfMs));
            sb.Append(", \"maxTotalMs\": ").Append(F(stat.MaxTotalMs));
            sb.Append(", \"maxSelfFrame\": ").Append(stat.MaxSelfFrame);
            sb.Append(", \"maxTotalFrame\": ").Append(stat.MaxTotalFrame);
            sb.Append(", \"maxChildrenMs\": ").Append(F(stat.MaxChildrenMs));
            sb.Append(", \"maxChildrenMarkerHint\": ").Append(Json(stat.MaxChildrenMarkerHint));
            sb.Append(", \"maxFrame\": ").Append(stat.MaxFrame);
            sb.Append(", \"maxThreadIndex\": ").Append(stat.MaxThreadIndex);
            sb.Append(", \"maxSampleIndex\": ").Append(stat.MaxSampleIndex);
            sb.Append(i == markers.Count - 1 ? "}" : "},");
            sb.AppendLine();
        }

        sb.Append("  ]");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteSuspectedHotspotTable(StringBuilder sb, string name, List<SuspectedHotspot> hotspots, CallPathTokenTable callPathTable, StringTable stringTable, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": {");
        sb.AppendLine("    \"columns\": [\"selfMsRank\",\"totalMsRank\",\"alreadyInTopContexts\",\"name\",\"callPathIndex\",\"maxSelfMs\",\"avgSelfMs\",\"maxChildrenMs\",\"maxChildrenMarkerHint\",\"reason\"],");
        sb.AppendLine("    \"columnRefs\": {\"name\":\"stringTable\",\"callPathIndex\":\"callPathTokens\",\"maxChildrenMarkerHint\":\"stringTable\",\"reason\":\"stringTable\"},");
        sb.AppendLine("    \"rows\": [");
        for (int i = 0; i < hotspots.Count; i++)
        {
            var hotspot = hotspots[i];
            var stat = hotspot.Stat;
            sb.Append("      [");
            sb.Append(hotspot.SelfMsRank).Append(",");
            sb.Append(hotspot.TotalMsRank).Append(",");
            sb.Append(hotspot.AlreadyInTopContexts ? "true" : "false").Append(",");
            sb.Append(stringTable.GetIndex(stat.Name)).Append(",");
            WriteCallPathIndexInline(sb, callPathTable, stat.CallPath);
            sb.Append(",").Append(F(stat.MaxSelfMs));
            sb.Append(",").Append(F(stat.SelfMs / Math.Max(1, stat.Count)));
            sb.Append(",").Append(F(stat.MaxChildrenMs));
            sb.Append(",").Append(stringTable.GetIndex(stat.MaxChildrenMarkerHint));
            sb.Append(",").Append(stringTable.GetIndex(hotspot.Reason));
            sb.Append(i == hotspots.Count - 1 ? "]" : "],");
            sb.AppendLine();
        }

        sb.AppendLine("    ]");
        sb.Append("  }");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteProjectSearchHints(StringBuilder sb, bool trailingComma)
    {
        sb.AppendLine("  \"projectSearchHints\": {");
        sb.AppendLine("    \"brgCustomRenderer\": [\"Assets/Learn\", \"Assets/Scripts/CustomParticle\"],");
        sb.AppendLine("    \"urpRendererFeature\": [\"Assets/Settings\", \"Assets/Graphics\", \"Packages/com.unity.render-pipelines.universal\"],");
        sb.AppendLine("    \"customShaders\": [\"Assets\", \"Packages\"],");
        sb.AppendLine("    \"particleSystems\": [\"Assets/Scenes\", \"Assets/Prefabs\", \"Assets/Resources\"]");
        sb.Append("  }");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteWorstFramesJson(
        ExportContext context,
        List<FrameDigestStat> worstMainFrames,
        List<FrameDigestStat> worstMainNoiseAdjustedFrames,
        List<FrameDigestStat> worstRenderFrames,
        List<FrameDigestStat> worstBrgFrames)
    {
        var sampleFrames = worstMainFrames
            .Concat(worstMainNoiseAdjustedFrames)
            .Concat(worstRenderFrames)
            .Concat(worstBrgFrames)
            .GroupBy(f => f.Frame)
            .Select(g => g.First())
            .OrderByDescending(f => f.MainThreadNoiseAdjustedMs)
            .ThenByDescending(f => f.MainThreadMs)
            .Take(DigestTopFrameCount)
            .ToList();
        var overviewFrames = worstMainFrames
            .Concat(worstMainNoiseAdjustedFrames)
            .Concat(worstRenderFrames)
            .Concat(worstBrgFrames)
            .GroupBy(f => f.Frame)
            .Select(g => g.First())
            .OrderByDescending(f => f.MainThreadNoiseAdjustedMs)
            .ThenByDescending(f => f.MainThreadMs)
            .Take(DigestTopFrameCount)
            .ToList();
        var mainRanks = BuildFrameRanks(worstMainFrames);
        var noiseAdjustedRanks = BuildFrameRanks(worstMainNoiseAdjustedFrames);
        var renderRanks = BuildFrameRanks(worstRenderFrames);
        var brgRanks = BuildFrameRanks(worstBrgFrames);

        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "profiler-ai-worstframes/v2", true);
        WriteJsonProperty(sb, "sourceDigest", "ai_profiler_digest.json", true);
        WriteJsonProperty(sb, "sampleDetails", "ai_profiler_worstframes_samples.json", true);
        WriteJsonProperty(sb, "captureScope", context.CaptureScope, true);
        WriteJsonProperty(sb, "firstFrame", context.FirstFrame, true);
        WriteJsonProperty(sb, "lastFrame", context.LastFrame, true);
        WriteStringArray(sb, "notes", new[]
        {
            "This file is the small first-read worst-frame overview. Per-frame top samples live in ai_profiler_worstframes_samples.json.",
            "combinedWorstFrames is deduplicated and includes rank columns for the original sort views.",
            "Use ai_profiler_worstframes_samples.json only after selecting frames that need sample-level inspection."
        }, true);
        WriteCombinedWorstFrameSummaryArray(sb, "combinedWorstFrames", overviewFrames, mainRanks, noiseAdjustedRanks, renderRanks, brgRanks, false);
        sb.AppendLine("}");

        WriteUtf8NoBom(Path.Combine(context.OutputDir, "ai_profiler_worstframes.json"), sb.ToString());
        WriteWorstFrameSamplesJson(context, sampleFrames);
    }

    private static Dictionary<int, int> BuildFrameRanks(List<FrameDigestStat> frames)
    {
        var ranks = new Dictionary<int, int>();
        for (int i = 0; i < frames.Count; i++)
            if (!ranks.ContainsKey(frames[i].Frame))
                ranks.Add(frames[i].Frame, i + 1);
        return ranks;
    }

    private static void WriteWorstFrameSamplesJson(ExportContext context, List<FrameDigestStat> sampleFrames)
    {
        var stringTable = BuildWorstFrameStringTable(sampleFrames);
        var sb = new StringBuilder(16384);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "schemaVersion", "profiler-ai-worstframes-samples/v1", true);
        WriteJsonProperty(sb, "sourceDigest", "ai_profiler_digest.json", true);
        WriteJsonProperty(sb, "sourceWorstFrames", "ai_profiler_worstframes.json", true);
        WriteJsonProperty(sb, "captureScope", context.CaptureScope, true);
        WriteJsonProperty(sb, "firstFrame", context.FirstFrame, true);
        WriteJsonProperty(sb, "lastFrame", context.LastFrame, true);
        WriteStringArray(sb, "notes", new[]
        {
            "Each frame keeps at most 30 actionable samples ordered by selfMs descending.",
            "Samples are already noise-pruned; sampleIndex remains the original Unity sample index and parentIndex is rewired to the exported parent.",
            "This file is for second-read sample inspection after ai_profiler_worstframes.json identifies interesting frames."
        }, true);
        WriteStringTable(sb, stringTable, true);
        WriteJsonProperty(sb, "stringTableNote", "local to ai_profiler_worstframes_samples.json only; do not reuse indexes from other files", true);
        WriteCompactWorstFrameArray(sb, "frames", sampleFrames, stringTable, false);
        sb.AppendLine("}");

        WriteUtf8NoBom(Path.Combine(context.OutputDir, "ai_profiler_worstframes_samples.json"), sb.ToString());
    }

    private static StringTable BuildWorstFrameStringTable(IEnumerable<FrameDigestStat> frames)
    {
        var table = new StringTable();
        foreach (var frame in frames)
        {
            foreach (var sample in frame.TopActionableSamples)
            {
                table.GetIndex(sample.ThreadName);
                table.GetIndex(sample.Name);
            }
        }

        return table;
    }

    private static void WriteFrameSummaryArray(StringBuilder sb, string name, List<FrameDigestStat> frames, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": {");
        sb.AppendLine("    \"columns\": [\"frame\",\"mainThreadMs\",\"mainThreadNoiseAdjustedMs\",\"renderPipelineMs\",\"brgMs\",\"particleMs\",\"gcMs\",\"threadCount\",\"sampleCount\"],");
        sb.AppendLine("    \"columnRefs\": {},");
        sb.AppendLine("    \"rows\": [");
        for (int i = 0; i < frames.Count; i++)
        {
            var stat = frames[i];
            sb.Append("      [");
            sb.Append(stat.Frame);
            sb.Append(",").Append(F(stat.MainThreadMs));
            sb.Append(",").Append(F(stat.MainThreadNoiseAdjustedMs));
            sb.Append(",").Append(F(stat.RenderPipelineMs));
            sb.Append(",").Append(F(stat.BrgMs));
            sb.Append(",").Append(F(stat.ParticleMs));
            sb.Append(",").Append(F(stat.GcMs));
            sb.Append(",").Append(stat.ThreadCount);
            sb.Append(",").Append(stat.SampleCount);
            sb.Append(i == frames.Count - 1 ? "]" : "],");
            sb.AppendLine();
        }

        sb.AppendLine("    ]");
        sb.Append("  }");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteCombinedWorstFrameSummaryArray(
        StringBuilder sb,
        string name,
        List<FrameDigestStat> frames,
        Dictionary<int, int> mainRanks,
        Dictionary<int, int> noiseAdjustedRanks,
        Dictionary<int, int> renderRanks,
        Dictionary<int, int> brgRanks,
        bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": {");
        sb.AppendLine("    \"columns\": [\"frame\",\"mainThreadRank\",\"noiseAdjustedRank\",\"renderPipelineRank\",\"brgRank\",\"mainThreadMs\",\"mainThreadNoiseAdjustedMs\",\"renderPipelineMs\",\"brgMs\",\"particleMs\",\"gcMs\",\"threadCount\",\"sampleCount\"],");
        sb.AppendLine("    \"columnRefs\": {},");
        sb.AppendLine("    \"rows\": [");
        for (int i = 0; i < frames.Count; i++)
        {
            var stat = frames[i];
            sb.Append("      [");
            sb.Append(stat.Frame);
            sb.Append(",").Append(mainRanks.TryGetValue(stat.Frame, out int mainRank) ? mainRank : 0);
            sb.Append(",").Append(noiseAdjustedRanks.TryGetValue(stat.Frame, out int noiseRank) ? noiseRank : 0);
            sb.Append(",").Append(renderRanks.TryGetValue(stat.Frame, out int renderRank) ? renderRank : 0);
            sb.Append(",").Append(brgRanks.TryGetValue(stat.Frame, out int brgRank) ? brgRank : 0);
            sb.Append(",").Append(F(stat.MainThreadMs));
            sb.Append(",").Append(F(stat.MainThreadNoiseAdjustedMs));
            sb.Append(",").Append(F(stat.RenderPipelineMs));
            sb.Append(",").Append(F(stat.BrgMs));
            sb.Append(",").Append(F(stat.ParticleMs));
            sb.Append(",").Append(F(stat.GcMs));
            sb.Append(",").Append(stat.ThreadCount);
            sb.Append(",").Append(stat.SampleCount);
            sb.Append(i == frames.Count - 1 ? "]" : "],");
            sb.AppendLine();
        }

        sb.AppendLine("    ]");
        sb.Append("  }");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteCompactWorstFrameArray(StringBuilder sb, string name, List<FrameDigestStat> frames, StringTable stringTable, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": {");
        sb.AppendLine("    \"columns\": [\"frame\",\"mainThreadMs\",\"mainThreadNoiseAdjustedMs\",\"renderPipelineMs\",\"brgMs\",\"particleMs\",\"gcMs\",\"threadCount\",\"sampleCount\",\"topActionableSamplesBySelfMs\"],");
        sb.AppendLine("    \"sampleColumns\": [\"threadIndex\",\"threadName\",\"sampleIndex\",\"parentIndex\",\"depth\",\"name\",\"selfMs\",\"totalMs\",\"childCount\"],");
        sb.AppendLine("    \"columnRefs\": {\"sample.threadName\":\"stringTable\",\"sample.name\":\"stringTable\"},");
        sb.AppendLine("    \"rows\": [");
        for (int i = 0; i < frames.Count; i++)
        {
            var stat = frames[i];
            sb.Append("      [");
            sb.Append(stat.Frame);
            sb.Append(",").Append(F(stat.MainThreadMs));
            sb.Append(",").Append(F(stat.MainThreadNoiseAdjustedMs));
            sb.Append(",").Append(F(stat.RenderPipelineMs));
            sb.Append(",").Append(F(stat.BrgMs));
            sb.Append(",").Append(F(stat.ParticleMs));
            sb.Append(",").Append(F(stat.GcMs));
            sb.Append(",").Append(stat.ThreadCount);
            sb.Append(",").Append(stat.SampleCount);
            sb.Append(",");
            WriteCompactSampleRefRowsInline(sb, stat.TopActionableSamples, stringTable);
            sb.Append(i == frames.Count - 1 ? "]" : "],");
            sb.AppendLine();
        }

        sb.AppendLine("    ]");
        sb.Append("  }");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static void WriteCompactSampleRefRowsInline(StringBuilder sb, List<SampleRef> samples, StringTable stringTable)
    {
        sb.Append("[");
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            sb.Append("[");
            sb.Append(sample.ThreadIndex);
            sb.Append(",").Append(stringTable.GetIndex(sample.ThreadName));
            sb.Append(",").Append(sample.SampleIndex);
            sb.Append(",").Append(sample.ParentIndex);
            sb.Append(",").Append(sample.Depth);
            sb.Append(",").Append(stringTable.GetIndex(sample.Name));
            sb.Append(",").Append(F(sample.SelfMs));
            sb.Append(",").Append(F(sample.TotalMs));
            sb.Append(",").Append(sample.ChildCount);
            sb.Append(i == samples.Count - 1 ? "]" : "],");
        }

        sb.Append("]");
    }

    private static void WriteStringArray(StringBuilder sb, string name, string[] values, bool trailingComma)
    {
        sb.Append("  \"").Append(Escape(name)).AppendLine("\": [");
        for (int i = 0; i < values.Length; i++)
        {
            sb.Append("    ").Append(Json(values[i]));
            sb.Append(i == values.Length - 1 ? string.Empty : ",");
            sb.AppendLine();
        }

        sb.Append("  ]");
        sb.AppendLine(trailingComma ? "," : string.Empty);
    }

    private static string Json(string value)
    {
        return "\"" + Escape(value) + "\"";
    }

    private static string Tsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\t", "\\t")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length + 16);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
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

    private static string EscapeMarkdown(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Replace("\\", "\\\\").Replace("`", "\\`");
    }

    private static string F(double value)
    {
        return value.ToString("F4", CultureInfo.InvariantCulture);
    }
}

[InitializeOnLoad]
public static class ProfilerAiExportToolbarInstaller
{
    private const string ButtonName = "ProfilerAiExportButton";
    private const string ButtonLayerName = "ProfilerAiExportButtonLayer";
    private const string RequestDirectory = "ProfilerAIExports";
    private const string RequestFileName = "raw_frame_request.json";
    private static double nextInstallTime;
    private static FileSystemWatcher requestWatcher;
    private static SynchronizationContext unityContext;

    static ProfilerAiExportToolbarInstaller()
    {
        unityContext = SynchronizationContext.Current;
        EditorApplication.delayCall += () => InstallIfPossible();
        EditorApplication.delayCall += EnsureRequestWatcher;
        EditorApplication.update += OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += DisposeRequestWatcher;
        EditorApplication.quitting += DisposeRequestWatcher;
    }

    private static void OnEditorUpdate()
    {
        if (EditorApplication.timeSinceStartup < nextInstallTime)
            return;

        nextInstallTime = EditorApplication.timeSinceStartup + 1.0;
        bool profilerWindowOpen = InstallIfPossible();
        ProfilerDataExporter.WriteRawFrameRequestStatus(profilerWindowOpen);
        EnsureRequestWatcher();
    }

    private static void EnsureRequestWatcher()
    {
        string requestDir = Path.Combine(Directory.GetCurrentDirectory(), RequestDirectory);
        Directory.CreateDirectory(requestDir);

        if (requestWatcher != null && string.Equals(requestWatcher.Path, requestDir, StringComparison.OrdinalIgnoreCase))
            return;

        DisposeRequestWatcher();
        requestWatcher = new FileSystemWatcher(requestDir, RequestFileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        requestWatcher.Created += OnRawFrameRequestFileChanged;
        requestWatcher.Changed += OnRawFrameRequestFileChanged;
        requestWatcher.Renamed += OnRawFrameRequestFileChanged;
    }

    private static void DisposeRequestWatcher()
    {
        if (requestWatcher == null)
            return;

        requestWatcher.EnableRaisingEvents = false;
        requestWatcher.Created -= OnRawFrameRequestFileChanged;
        requestWatcher.Changed -= OnRawFrameRequestFileChanged;
        requestWatcher.Renamed -= OnRawFrameRequestFileChanged;
        requestWatcher.Dispose();
        requestWatcher = null;
    }

    private static void OnRawFrameRequestFileChanged(object sender, FileSystemEventArgs args)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(350);
            var context = unityContext;
            if (context != null)
            {
                context.Post(__ => TryProcessRawFrameRequestFromWatcher(), null);
            }
            else
            {
                EditorApplication.delayCall += TryProcessRawFrameRequestFromWatcher;
            }
        });
    }

    private static void TryProcessRawFrameRequestFromWatcher()
    {
        if (!InstallIfPossible())
        {
            ProfilerDataExporter.WriteRawFrameRequestStatus(false);
            return;
        }

        ProfilerDataExporter.WriteRawFrameRequestStatus(true);
        ProfilerDataExporter.ProcessRawFrameRequestFromFileEvent();
    }

    private static bool InstallIfPossible()
    {
        var profilerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProfilerWindow");
        if (profilerWindowType == null)
            return false;

        var windows = Resources.FindObjectsOfTypeAll(profilerWindowType);
        bool hasProfilerWindow = false;
        foreach (var windowObject in windows)
        {
            var window = windowObject as EditorWindow;
            if (window == null || window.rootVisualElement == null)
                continue;

            hasProfilerWindow = true;
            if (window.rootVisualElement.Q<VisualElement>(ButtonLayerName) != null)
                continue;

            Button button = null;
            button = new Button(() => ProfilerDataExporter.ShowSaveCurrentCaptureMenu(button))
            {
                name = ButtonName,
                text = "AI",
            tooltip = "导出 Profiler 捕获供 AI 分析。"
            };

            button.style.width = 30;
            button.style.minWidth = 30;
            button.style.height = 18;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;

            var layer = new VisualElement
            {
                name = ButtonLayerName,
                pickingMode = PickingMode.Ignore
            };

            layer.style.position = Position.Absolute;
            layer.style.top = 2;
            layer.style.right = 116;
            layer.style.width = 32;
            layer.style.height = 20;
            layer.style.flexDirection = FlexDirection.Row;
            layer.style.justifyContent = Justify.Center;
            layer.style.alignItems = Align.Center;
            layer.style.display = DisplayStyle.Flex;
            button.pickingMode = PickingMode.Position;

            layer.Add(button);
            window.rootVisualElement.Add(layer);
        }

        return hasProfilerWindow;
    }
}
