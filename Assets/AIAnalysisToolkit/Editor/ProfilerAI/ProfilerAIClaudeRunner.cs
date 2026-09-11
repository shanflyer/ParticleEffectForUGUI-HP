using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class ProfilerAIClaudeRunner
{
    private const string CommandPrefKey = "ProfilerAI.Claude.Command";
    private const string ArgumentsPrefKey = "ProfilerAI.Claude.Arguments";
    private const string ModelPrefKey = "ProfilerAI.Claude.Model";
    private const string ProviderPrefKey = "ProfilerAI.Claude.Provider";
    private const string VisibleTerminalPrefKey = "ProfilerAI.Claude.VisibleTerminal";
    private const string LastStatusPathPrefKey = "ProfilerAI.Claude.LastStatusPath";
    private const string ProviderClaude = "claude";
    private const string ProviderCodex = "codex";
    private const string DefaultCommand = ProviderClaude;
    private const string DefaultCodexCommand = ProviderCodex;
    private const string DefaultCodexArguments = "--dangerously-bypass-approvals-and-sandbox";
    private const string DefaultModel = "";
    private const string LegacyPrintArguments = "-p --permission-mode bypassPermissions --output-format text";
    private const string LegacyInteractiveArguments = "--permission-mode bypassPermissions";
    private const string LegacyStreamArguments = "-p --permission-mode bypassPermissions --output-format stream-json --verbose --include-partial-messages";
    private const string LegacyRestrictedArguments = "-p --permission-mode bypassPermissions --output-format stream-json --verbose --include-partial-messages --disallowedTools Agent Bash";
    private const string LegacyToolRestrictedArguments = "-p --permission-mode bypassPermissions --output-format stream-json --verbose --include-partial-messages --tools Read Grep Glob Write";
    private const string DefaultArguments = "-p --permission-mode bypassPermissions --output-format stream-json --verbose --include-partial-messages";
    private const string PromptFileName = "cc_profiler_prompt.md";
    private const string ReportFileName = "AI_ANALYSIS_REPORT.md";
    private const string LogFileName = "cc_analysis.log";
    private const string StreamFileName = "cc_analysis_stream.jsonl";
    private const string StatusFileName = "cc_analysis_status.json";
    public const double StaleOutputTimeoutSeconds = 300.0;
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
    private static Process runningProcess;
    private static ActiveRun activeRun;

    private sealed class ActiveRun
    {
        public string StatusPath;
        public string Command;
        public string Provider;
        public string Arguments;
        public string Model;
        public string LaunchCommand;
        public string PromptPath;
        public string ReportPath;
        public string LogPath;
        public string StreamPath;
        public int ProcessId;
        public DateTime LastOutputUtc;
        public bool IsTerminal;
    }

    private sealed class ModelOption
    {
        public string Label;
        public string Value;
    }

    [MenuItem("Tools/AI 分析/Profiler/分析最近一次 AI 导出")]
    public static void AnalyzeLatestExport()
    {
        string exportDir = FindLatestExportDir();
        if (string.IsNullOrEmpty(exportDir))
        {
            EditorUtility.DisplayDialog("Profiler AI", "没有找到完整的 ProfilerAIExports 导出目录。", "确定");
            return;
        }

        StartAnalysis(exportDir);
    }

    [MenuItem("Tools/AI 分析/Profiler/选择 AI 导出目录并分析...")]
    public static void AnalyzeSelectedExport()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "ProfilerAIExports");
        string exportDir = EditorUtility.OpenFolderPanel("选择 Profiler AI 导出目录", Directory.Exists(root) ? root : Directory.GetCurrentDirectory(), string.Empty);
        if (string.IsNullOrEmpty(exportDir))
            return;

        StartAnalysis(exportDir);
    }

    [MenuItem("Tools/AI 分析/Profiler/对比多个 AI 导出...")]
    public static void CompareExports()
    {
        ProfilerAICompareExportsWindow.Open();
    }

    [MenuItem("Tools/AI 分析/AI 工具设置...")]
    public static void OpenSettings()
    {
        ProfilerAIClaudeSettingsWindow.Open();
    }

    [MenuItem("Tools/AI 分析/打开分析面板")]
    public static void OpenStatus()
    {
        string statusPath = EditorPrefs.GetString(LastStatusPathPrefKey, string.Empty);
        if (string.IsNullOrEmpty(statusPath) || !File.Exists(statusPath))
        {
            string exportDir = FindLatestAnalysisDir();
            if (!string.IsNullOrEmpty(exportDir))
                statusPath = Path.Combine(exportDir, StatusFileName);
        }

        ProfilerAIClaudeStatusWindow.Open(statusPath);
    }

    public static void StartAnalysis(string exportDir)
    {
        exportDir = Path.GetFullPath(exportDir);
        if (!Directory.Exists(exportDir))
        {
            EditorUtility.DisplayDialog("Profiler AI", "导出目录不存在：\n" + exportDir, "确定");
            return;
        }

        string guidePath = Path.Combine(exportDir, "AI_ANALYSIS_GUIDE.md");
        string digestPath = Path.Combine(exportDir, "ai_profiler_digest.json");
        if (!File.Exists(guidePath) || !File.Exists(digestPath))
        {
            EditorUtility.DisplayDialog("Profiler AI", "该目录缺少 AI 导出文件：\n" + exportDir, "确定");
            return;
        }

        string projectRoot = Directory.GetCurrentDirectory();
        string promptPath = Path.Combine(exportDir, PromptFileName);
        string reportPath = Path.Combine(exportDir, ReportFileName);
        string promptText = BuildPrompt(projectRoot, exportDir, reportPath);
        string terminalPrompt =
            "解析 " + exportDir.Replace("\\", "/") +
            " 这个 Unity Profiler AI 导出目录，结合项目代码生成中文性能分析报告，写入 " +
            reportPath.Replace("\\", "/") + "。不要修改项目代码。";

        StartAnalysisWithPrompt(projectRoot, exportDir, promptPath, reportPath, promptText, terminalPrompt);
    }

    [MenuItem("Tools/AI 分析/Frame Debugger/综合分析最近一次 RenderDoc 联动导出")]
    public static void AnalyzeLatestLinkedRenderExport()
    {
        string exportDir = FindLatestLinkedRenderExportDir();
        if (string.IsNullOrEmpty(exportDir))
        {
            EditorUtility.DisplayDialog("联动渲染 AI", "没有找到带 linked_capture.json 且 RenderDoc 已解析的 FrameDebuggerAIExports 导出目录。", "确定");
            return;
        }

        StartLinkedRenderAnalysis(exportDir);
    }

    [MenuItem("Tools/AI 分析/Frame Debugger/选择 RenderDoc 联动导出并综合分析...")]
    public static void AnalyzeSelectedLinkedRenderExport()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "FrameDebuggerAIExports");
        string exportDir = EditorUtility.OpenFolderPanel("选择 RenderDoc 联动 Frame Debugger 导出目录", Directory.Exists(root) ? root : Directory.GetCurrentDirectory(), string.Empty);
        if (string.IsNullOrEmpty(exportDir))
            return;

        StartLinkedRenderAnalysis(exportDir);
    }

    public static void StartFrameDebugAnalysis(string exportDir)
    {
        exportDir = Path.GetFullPath(exportDir);
        if (!Directory.Exists(exportDir))
        {
            EditorUtility.DisplayDialog("Frame Debugger AI", "导出目录不存在：\n" + exportDir, "确定");
            return;
        }

        string guidePath = Path.Combine(exportDir, "AI_ANALYSIS_GUIDE.md");
        string summaryPath = Path.Combine(exportDir, "ai_summary.json");
        if (!File.Exists(guidePath) || !File.Exists(summaryPath))
        {
            EditorUtility.DisplayDialog("Frame Debugger AI", "该目录缺少 Frame Debugger AI 导出文件：\n" + exportDir, "确定");
            return;
        }

        string linkedCapturePath = Path.Combine(exportDir, "linked_capture.json");
        if (File.Exists(linkedCapturePath))
        {
            StartLinkedRenderAnalysis(exportDir, linkedCapturePath);
            return;
        }

        string projectRoot = Directory.GetCurrentDirectory();
        string promptPath = Path.Combine(exportDir, "cc_framedebug_prompt.md");
        string reportPath = Path.Combine(exportDir, "AI_FRAMEDEBUG_REPORT.md");
        string promptText = BuildFrameDebugPrompt(projectRoot, exportDir, reportPath);
        string terminalPrompt =
            "解析 " + exportDir.Replace("\\", "/") +
            " 这个 Unity Frame Debugger AI 导出目录，结合项目材质和 Shader 生成中文渲染分析报告，写入 " +
            reportPath.Replace("\\", "/") + "。不要修改项目代码。";

        StartAnalysisWithPrompt(projectRoot, exportDir, promptPath, reportPath, promptText, terminalPrompt);
    }

    public static void StartLinkedRenderAnalysis(string frameDebugExportDir, string linkedCapturePath = "")
    {
        frameDebugExportDir = Path.GetFullPath(frameDebugExportDir);
        if (string.IsNullOrWhiteSpace(linkedCapturePath))
            linkedCapturePath = Path.Combine(frameDebugExportDir, "linked_capture.json");
        linkedCapturePath = Path.GetFullPath(linkedCapturePath);

        if (!Directory.Exists(frameDebugExportDir) || !File.Exists(Path.Combine(frameDebugExportDir, "ai_summary.json")))
        {
            EditorUtility.DisplayDialog("联动渲染 AI", "Frame Debugger AI 导出目录无效：\n" + frameDebugExportDir, "确定");
            return;
        }
        if (!File.Exists(linkedCapturePath))
        {
            EditorUtility.DisplayDialog("联动渲染 AI", "缺少 linked_capture.json：\n" + linkedCapturePath, "确定");
            return;
        }

        string linkedJson = File.ReadAllText(linkedCapturePath, Encoding.UTF8);
        string renderDocAnalysisDir = ExtractJsonString(linkedJson, "renderDocAnalysisDirectory");
        if (string.IsNullOrWhiteSpace(renderDocAnalysisDir))
        {
            EditorUtility.DisplayDialog("联动渲染 AI", "linked_capture.json 中没有 renderDocAnalysisDirectory。请先用联动按钮完成 RenderDoc 抓帧解析。", "确定");
            return;
        }
        renderDocAnalysisDir = Path.GetFullPath(renderDocAnalysisDir);
        if (!Directory.Exists(renderDocAnalysisDir) ||
            !File.Exists(Path.Combine(renderDocAnalysisDir, "analysis_ready_summary.json")) ||
            !File.Exists(Path.Combine(renderDocAnalysisDir, "pass_table.json")))
        {
            EditorUtility.DisplayDialog("联动渲染 AI", "RenderDoc 解析目录不存在或不完整：\n" + renderDocAnalysisDir, "确定");
            return;
        }

        string projectRoot = Directory.GetCurrentDirectory();
        string promptPath = Path.Combine(frameDebugExportDir, "cc_linked_render_prompt.md");
        string reportPath = Path.Combine(frameDebugExportDir, "AI_LINKED_RENDER_REPORT.md");
        string auditPath = Path.Combine(frameDebugExportDir, "AI_LINKED_ANALYSIS_AUDIT.json");
        string promptText = BuildLinkedRenderPrompt(projectRoot, frameDebugExportDir, renderDocAnalysisDir, linkedCapturePath, reportPath, auditPath);
        string preAnalysisScriptPath = PrepareLinkedDeepEventPreflightScript(projectRoot, frameDebugExportDir, renderDocAnalysisDir, linkedCapturePath);
        string terminalPrompt =
            "综合分析 Unity Frame Debugger 与 RenderDoc 联动导出。先读取 " +
            promptPath.Replace("\\", "/") +
            "，同时分析 Frame Debugger 目录 " + frameDebugExportDir.Replace("\\", "/") +
            " 和 RenderDoc 解析目录 " + renderDocAnalysisDir.Replace("\\", "/") +
            "，输出一份给人读的中文综合渲染问题报告到 " + reportPath.Replace("\\", "/") +
            "，并把机器审计/读取计算过程写入 " + auditPath.Replace("\\", "/") +
            "。不要分别输出两个报告，不要修改项目代码。";

        StartAnalysisWithPrompt(projectRoot, frameDebugExportDir, promptPath, reportPath, promptText, terminalPrompt, preAnalysisScriptPath);
    }

    [MenuItem("Tools/AI 分析/FX/分析最近一次 FX 导出")]
    public static void AnalyzeLatestFxExport()
    {
        string exportDir = FindLatestFxExportDir();
        if (string.IsNullOrEmpty(exportDir))
        {
            EditorUtility.DisplayDialog("FX AI", "没有找到完整的 FxAIAnalysisExports 导出目录。", "确定");
            return;
        }

        StartFxAnalysis(exportDir);
    }

    [MenuItem("Tools/AI 分析/FX/选择 FX 导出目录并分析...")]
    public static void AnalyzeSelectedFxExport()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "FxAIAnalysisExports");
        string exportDir = EditorUtility.OpenFolderPanel("选择 FX AI 导出目录", Directory.Exists(root) ? root : Directory.GetCurrentDirectory(), string.Empty);
        if (string.IsNullOrEmpty(exportDir))
            return;

        StartFxAnalysis(exportDir);
    }

    public static void StartFxAnalysis(string exportDir)
    {
        exportDir = Path.GetFullPath(exportDir);
        if (!Directory.Exists(exportDir))
        {
            EditorUtility.DisplayDialog("FX AI", "导出目录不存在：\n" + exportDir, "确定");
            return;
        }

        string guidePath = Path.Combine(exportDir, "AI_ANALYSIS_GUIDE.md");
        string manifestPath = Path.Combine(exportDir, "manifest.json");
        if (!File.Exists(guidePath) || !File.Exists(manifestPath))
        {
            EditorUtility.DisplayDialog("FX AI", "该目录缺少 FX AI 导出文件：\n" + exportDir, "确定");
            return;
        }

        string projectRoot = Directory.GetCurrentDirectory();
        string promptPath = Path.Combine(exportDir, "cc_fx_prompt.md");
        string reportPath = Path.Combine(exportDir, "AI_FX_ANALYSIS_REPORT.md");
        string promptText = BuildFxPrompt(projectRoot, exportDir, reportPath);
        string terminalPrompt =
            "解析 " + exportDir.Replace("\\", "/") +
            " 这个 Unity FX AI 导出目录。先读取 cc_fx_prompt.md、AI_ANALYSIS_GUIDE.md、manifest.json、full/、particles/ 下的图片和 settings.json，生成中文特效优化分析报告，写入 " +
            reportPath.Replace("\\", "/") + "。不要修改项目代码或资源。";

        StartAnalysisWithPrompt(projectRoot, exportDir, promptPath, reportPath, promptText, terminalPrompt);
    }

    public static void StartAnalysisAuto(string exportDir)
    {
        exportDir = Path.GetFullPath(exportDir);
        if (File.Exists(Path.Combine(exportDir, "manifest.json")) &&
            Directory.Exists(Path.Combine(exportDir, "particles")))
        {
            StartFxAnalysis(exportDir);
            return;
        }

        if (File.Exists(Path.Combine(exportDir, "ai_summary.json")) &&
            File.Exists(Path.Combine(exportDir, "ai_event_evidence.jsonl")))
        {
            StartFrameDebugAnalysis(exportDir);
            return;
        }

        StartAnalysis(exportDir);
    }

    public static void StartCompareAnalysis(string[] exportDirs)
    {
        if (exportDirs == null)
            exportDirs = Array.Empty<string>();

        exportDirs = exportDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (exportDirs.Length < 2)
        {
            EditorUtility.DisplayDialog("Profiler AI", "至少选择两个 AI 导出目录才能对比。", "确定");
            return;
        }

        foreach (string exportDir in exportDirs)
        {
            if (!Directory.Exists(exportDir) ||
                !File.Exists(Path.Combine(exportDir, "AI_ANALYSIS_GUIDE.md")) ||
                !File.Exists(Path.Combine(exportDir, "ai_profiler_digest.json")))
            {
                EditorUtility.DisplayDialog("Profiler AI", "目录不是完整 AI 导出：\n" + exportDir, "确定");
                return;
            }
        }

        string projectRoot = Directory.GetCurrentDirectory();
        string compareDir = Path.Combine(projectRoot, "ProfilerAIExports", "Compare_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(compareDir);

        string promptPath = Path.Combine(compareDir, PromptFileName);
        string reportPath = Path.Combine(compareDir, "AI_COMPARE_REPORT.md");
        string promptText = BuildComparePrompt(projectRoot, exportDirs, reportPath);
        string terminalPrompt =
            "对比这些 Unity Profiler AI 导出目录：" + string.Join(" ; ", exportDirs.Select(dir => dir.Replace("\\", "/")).ToArray()) +
            "。结合项目代码输出中文对比分析报告，写入 " + reportPath.Replace("\\", "/") + "。不要修改项目代码。";

        StartAnalysisWithPrompt(projectRoot, compareDir, promptPath, reportPath, promptText, terminalPrompt);
    }

    private static void StartAnalysisWithPrompt(string projectRoot, string analysisDir, string promptPath, string reportPath, string promptText, string terminalPrompt, string preAnalysisScriptPath = "")
    {
        string logPath = Path.Combine(analysisDir, LogFileName);
        string streamPath = Path.Combine(analysisDir, StreamFileName);
        string statusPath = Path.Combine(analysisDir, StatusFileName);
        string provider = GetProvider();
        string command = ResolveToolCommand(provider, GetCommand(provider));
        string arguments = string.Equals(provider, ProviderClaude, StringComparison.OrdinalIgnoreCase)
            ? NormalizeArguments(EditorPrefs.GetString(ArgumentsPrefKey, DefaultArguments))
            : EditorPrefs.GetString(ArgumentsPrefKey, string.Empty);
        string model = EditorPrefs.GetString(ModelPrefKey, DefaultModel);

        EditorPrefs.SetString(LastStatusPathPrefKey, statusPath);
        File.WriteAllText(promptPath, promptText, Utf8NoBom);
        File.WriteAllText(logPath, string.Empty, Utf8NoBom);
        File.WriteAllText(streamPath, string.Empty, Utf8NoBom);

        if (GetVisibleTerminal())
        {
            StartVisibleTerminalAnalysis(provider, projectRoot, analysisDir, command, model, promptPath, reportPath, logPath, streamPath, statusPath, terminalPrompt, preAnalysisScriptPath);
            return;
        }

        if (!string.Equals(provider, ProviderClaude, StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog("AI 分析", "Unity 面板流式模式目前只支持 Claude Code。Codex 请使用“弹出终端”模式。", "确定");
            return;
        }

        string launchArguments = BuildClaudeArguments(arguments, projectRoot, model);
        string launchCommand = command + " " + launchArguments;
        WriteStatus(statusPath, "starting_stream_analysis", command, arguments, model, launchCommand, promptPath, reportPath, logPath, streamPath, -1, -1, null);
        ProfilerAIClaudeStatusWindow.Open(statusPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = launchArguments,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.EnvironmentVariables["NO_COLOR"] = "1";

        try
        {
            if (runningProcess != null && !runningProcess.HasExited)
            {
                EditorUtility.DisplayDialog("AI 分析", "AI 分析正在运行。", "确定");
                return;
            }

            var outputLock = new object();
            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Process.Start 未返回进程。");

            runningProcess = process;
            activeRun = new ActiveRun
            {
                StatusPath = statusPath,
                Command = command,
                Provider = provider,
                Arguments = arguments,
                Model = model,
                LaunchCommand = launchCommand,
                PromptPath = promptPath,
                ReportPath = reportPath,
                LogPath = logPath,
                StreamPath = streamPath,
                ProcessId = process.Id,
                LastOutputUtc = DateTime.UtcNow,
                IsTerminal = false
            };
            EditorApplication.update -= MonitorActiveRun;
            EditorApplication.update += MonitorActiveRun;
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                    return;
                lock (outputLock)
                {
                    if (activeRun != null)
                        activeRun.LastOutputUtc = DateTime.UtcNow;
                    File.AppendAllText(streamPath, args.Data + Environment.NewLine, Utf8NoBom);
                    File.AppendAllText(logPath, args.Data + Environment.NewLine, Utf8NoBom);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                    return;
                lock (outputLock)
                {
                    if (activeRun != null)
                        activeRun.LastOutputUtc = DateTime.UtcNow;
                    File.AppendAllText(logPath, "[stderr] " + args.Data + Environment.NewLine, Utf8NoBom);
                }
            };
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) =>
            {
                int exitCode = process.ExitCode;
                bool isActiveProcess = activeRun != null && activeRun.ProcessId == process.Id;
                if (isActiveProcess)
                {
                    WriteStatus(statusPath, exitCode == 0 ? "completed" : "failed", command, arguments, model, launchCommand, promptPath, reportPath, logPath, streamPath, process.Id, exitCode, exitCode == 0 ? null : "AI 工具退出码：" + exitCode.ToString(CultureInfo.InvariantCulture));
                    runningProcess = null;
                    activeRun = null;
                    EditorApplication.update -= MonitorActiveRun;
                }
                process.Dispose();
                EditorApplication.delayCall += () =>
                {
                    AssetDatabase.Refresh();
                    ProfilerAIClaudeStatusWindow.Open(statusPath);
                    Debug.Log("AI 分析流式运行结束：" + ToProjectRelativePath(reportPath));
                };
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Write(File.ReadAllText(promptPath, Encoding.UTF8));
            process.StandardInput.Close();

            WriteStatus(statusPath, "running_stream_analysis", command, arguments, model, launchCommand, promptPath, reportPath, logPath, streamPath, process.Id, -1, null);
            ProfilerAIClaudeStatusWindow.Open(statusPath);
            Debug.Log("AI 分析流式运行已启动：" + ToProjectRelativePath(analysisDir));
        }
        catch (Exception ex)
        {
            WriteStatus(statusPath, "start_failed", command, arguments, model, launchCommand, promptPath, reportPath, logPath, streamPath, -1, -1, ex.Message);
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("AI 分析", "启动 AI 工具失败：\n" + ex.Message, "确定");
        }
    }

    private static void StartVisibleTerminalAnalysis(
        string provider,
        string projectRoot,
        string exportDir,
        string command,
        string model,
        string promptPath,
        string reportPath,
        string logPath,
        string streamPath,
        string statusPath,
        string terminalPrompt)
    {
        StartVisibleTerminalAnalysis(provider, projectRoot, exportDir, command, model, promptPath, reportPath, logPath, streamPath, statusPath, terminalPrompt, "");
    }

    private static void StartVisibleTerminalAnalysis(
        string provider,
        string projectRoot,
        string exportDir,
        string command,
        string model,
        string promptPath,
        string reportPath,
        string logPath,
        string streamPath,
        string statusPath,
        string terminalPrompt,
        string preAnalysisScriptPath)
    {
        if (runningProcess != null && !runningProcess.HasExited)
        {
            EditorUtility.DisplayDialog("AI 分析", "AI 分析正在运行。", "确定");
            return;
        }

        provider = NormalizeProvider(provider);
        string scriptPath = Path.Combine(exportDir, "run_ai_analysis.cmd");
        string toolArguments = string.Equals(provider, ProviderCodex, StringComparison.OrdinalIgnoreCase)
            ? GetArguments()
            : string.Empty;
        string launchArguments = BuildInteractiveArguments(provider, projectRoot, model, terminalPrompt, toolArguments);
        string toolTitle = GetProviderLabel(provider);
        string scriptText =
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            "title " + toolTitle + " - AI Analysis\r\n" +
            "cd /d " + QuoteForBatchArg(projectRoot) + "\r\n" +
            "echo AI analysis - " + toolTitle + "\r\n" +
            "echo Export: " + exportDir + "\r\n" +
            "echo Report: " + reportPath + "\r\n" +
            "echo.\r\n" +
            BuildPreAnalysisBatchCall(preAnalysisScriptPath) +
            "call " + QuoteExecutableForBatch(command) + " " + launchArguments + "\r\n" +
            "echo.\r\n" +
            "echo AI tool exited with %ERRORLEVEL%.\r\n";
        File.WriteAllText(scriptPath, scriptText, Utf8NoBom);
        File.WriteAllText(logPath, "可见终端模式，" + toolTitle + " 输出显示在弹出的命令行窗口。\r\n脚本: " + scriptPath + "\r\n", Utf8NoBom);

        string shellCommand = "cmd.exe /k " + QuoteForCmd(scriptPath);
        WriteStatus(statusPath, "starting_terminal_analysis", command, launchArguments, model, shellCommand, promptPath, reportPath, logPath, streamPath, -1, -1, null);
        ProfilerAIClaudeStatusWindow.Open(statusPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/k " + QuoteForCmd(scriptPath),
            WorkingDirectory = projectRoot,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Process.Start 未返回进程。");

            runningProcess = process;
            activeRun = new ActiveRun
            {
                StatusPath = statusPath,
                Command = command,
                Arguments = launchArguments,
                Model = model,
                LaunchCommand = shellCommand,
                PromptPath = promptPath,
                ReportPath = reportPath,
                LogPath = logPath,
                StreamPath = streamPath,
                ProcessId = process.Id,
                LastOutputUtc = DateTime.UtcNow,
                IsTerminal = true
            };
            EditorApplication.update -= MonitorActiveRun;
            EditorApplication.update += MonitorActiveRun;

            process.EnableRaisingEvents = true;
            process.Exited += (_, __) =>
            {
                int exitCode = process.ExitCode;
                bool isActiveProcess = activeRun != null && activeRun.ProcessId == process.Id;
                if (isActiveProcess)
                {
                    WriteStatus(statusPath, exitCode == 0 ? "completed" : "failed", command, launchArguments, model, shellCommand, promptPath, reportPath, logPath, streamPath, process.Id, exitCode, exitCode == 0 ? null : toolTitle + " 终端退出码：" + exitCode.ToString(CultureInfo.InvariantCulture));
                    runningProcess = null;
                    activeRun = null;
                    EditorApplication.update -= MonitorActiveRun;
                }
                process.Dispose();
                EditorApplication.delayCall += () =>
                {
                    AssetDatabase.Refresh();
                    ProfilerAIClaudeStatusWindow.Open(statusPath);
                };
            };

            WriteStatus(statusPath, "running_terminal_analysis", command, launchArguments, model, shellCommand, promptPath, reportPath, logPath, streamPath, process.Id, -1, null);
            Debug.Log("AI 分析 " + toolTitle + " 可见终端已启动：" + ToProjectRelativePath(exportDir));
        }
        catch (Exception ex)
        {
            WriteStatus(statusPath, "start_failed", command, launchArguments, model, shellCommand, promptPath, reportPath, logPath, streamPath, -1, -1, ex.Message);
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("AI 分析", "启动 " + toolTitle + " 终端失败：\n" + ex.Message, "确定");
        }
    }

    public static string GetCommand()
    {
        return GetCommand(GetProvider());
    }

    private static string GetCommand(string provider)
    {
        provider = NormalizeProvider(provider);
        string command = EditorPrefs.GetString(CommandPrefKey, string.Empty);
        if (string.IsNullOrWhiteSpace(command) || IsOtherProviderDefaultCommand(command, provider))
            return GetDefaultCommand(provider);
        return command;
    }

    public static string GetArguments()
    {
        if (string.Equals(GetProvider(), ProviderCodex, StringComparison.OrdinalIgnoreCase))
        {
            string arguments = EditorPrefs.GetString(ArgumentsPrefKey, DefaultCodexArguments);
            return string.IsNullOrWhiteSpace(arguments) ? DefaultCodexArguments : arguments.Trim();
        }
        return NormalizeArguments(EditorPrefs.GetString(ArgumentsPrefKey, DefaultArguments));
    }

    public static string GetModel()
    {
        return EditorPrefs.GetString(ModelPrefKey, DefaultModel);
    }

    public static string GetProvider()
    {
        return NormalizeProvider(EditorPrefs.GetString(ProviderPrefKey, ProviderClaude));
    }

    public static void GetModelOptions(string provider, string currentModel, out string[] labels, out string[] values)
    {
        var labelList = new List<string> { "默认" };
        var valueList = new List<string> { string.Empty };

        foreach (ModelOption option in ReadModelOptions(provider))
        {
            if (string.IsNullOrWhiteSpace(option.Value) || valueList.Contains(option.Value))
                continue;

            valueList.Add(option.Value);
            labelList.Add(string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label);
        }

        labels = labelList.ToArray();
        values = valueList.ToArray();
    }

    public static bool GetVisibleTerminal()
    {
        return EditorPrefs.GetBool(VisibleTerminalPrefKey, true);
    }

    public static void SaveSettings(string provider, string command, string arguments, string model, bool visibleTerminal)
    {
        provider = NormalizeProvider(provider);
        EditorPrefs.SetString(ProviderPrefKey, provider);
        EditorPrefs.SetString(CommandPrefKey, string.IsNullOrWhiteSpace(command) ? GetDefaultCommand(provider) : command.Trim());
        EditorPrefs.SetString(ArgumentsPrefKey, string.IsNullOrWhiteSpace(arguments) ? (provider == ProviderCodex ? DefaultCodexArguments : DefaultArguments) : arguments.Trim());
        EditorPrefs.SetString(ModelPrefKey, string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim());
        EditorPrefs.SetBool(VisibleTerminalPrefKey, visibleTerminal);
    }

    public static void SaveModel(string model)
    {
        EditorPrefs.SetString(ModelPrefKey, string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim());
    }

    public static void ResetSettings()
    {
        EditorPrefs.DeleteKey(CommandPrefKey);
        EditorPrefs.DeleteKey(ArgumentsPrefKey);
        EditorPrefs.DeleteKey(ModelPrefKey);
        EditorPrefs.DeleteKey(ProviderPrefKey);
        EditorPrefs.DeleteKey(VisibleTerminalPrefKey);
    }

    public static void StopAnalysisFromStatus(string statusPath, string reason)
    {
        if (string.IsNullOrEmpty(statusPath) || !File.Exists(statusPath))
            return;

        string statusText = File.ReadAllText(statusPath, Encoding.UTF8);
        int processId = ParseJsonInt(statusText, "processId", -1);
        KillProcess(processId);

        if (activeRun != null && string.Equals(Path.GetFullPath(activeRun.StatusPath), Path.GetFullPath(statusPath), StringComparison.OrdinalIgnoreCase))
        {
            runningProcess = null;
            activeRun = null;
            EditorApplication.update -= MonitorActiveRun;
        }

        WriteStatus(
            statusPath,
            "stopped",
            ExtractJsonString(statusText, "command"),
            ExtractJsonString(statusText, "arguments"),
            ExtractJsonString(statusText, "model"),
            ExtractJsonString(statusText, "shellCommand"),
            ResolveProjectPath(ExtractJsonString(statusText, "promptPath")),
            ResolveProjectPath(ExtractJsonString(statusText, "reportPath")),
            ResolveProjectPath(ExtractJsonString(statusText, "logPath")),
            ResolveProjectPath(ExtractJsonString(statusText, "streamPath")),
            processId,
            -1,
            reason);

        AssetDatabase.Refresh();
    }

    private static void MonitorActiveRun()
    {
        if (activeRun == null)
            return;

        if (runningProcess == null || runningProcess.HasExited)
            return;

        DateTime streamWriteUtc = File.Exists(activeRun.StreamPath) ? File.GetLastWriteTimeUtc(activeRun.StreamPath) : DateTime.MinValue;
        DateTime newestOutputUtc = activeRun.IsTerminal ? DateTime.MinValue : (streamWriteUtc > activeRun.LastOutputUtc ? streamWriteUtc : activeRun.LastOutputUtc);
        if (!File.Exists(activeRun.ReportPath))
            return;

        DateTime reportWriteUtc = File.GetLastWriteTimeUtc(activeRun.ReportPath);
        if ((DateTime.UtcNow - reportWriteUtc).TotalSeconds < 10.0 || (!activeRun.IsTerminal && (DateTime.UtcNow - newestOutputUtc).TotalSeconds < 10.0))
            return;

        WriteStatus(activeRun.StatusPath, "completed_report_detected", activeRun.Command, activeRun.Arguments, activeRun.Model, activeRun.LaunchCommand, activeRun.PromptPath, activeRun.ReportPath, activeRun.LogPath, activeRun.StreamPath, activeRun.ProcessId, 0, null);

        if (!activeRun.IsTerminal)
        {
            try
            {
                runningProcess.Kill();
            }
            catch
            {
            }
        }

        runningProcess = null;
        activeRun = null;
        EditorApplication.update -= MonitorActiveRun;
        AssetDatabase.Refresh();
        ProfilerAIClaudeStatusWindow.Open(EditorPrefs.GetString(LastStatusPathPrefKey, string.Empty));
    }

    private static void KillProcess(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            var taskkill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/PID " + processId.ToString(CultureInfo.InvariantCulture) + " /T /F",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (taskkill != null)
                taskkill.Dispose();
        }
        catch
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                    process.Kill();
                process.Dispose();
            }
            catch
            {
            }
        }
    }

    private static string FindLatestExportDir()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "ProfilerAIExports");
        if (!Directory.Exists(root))
            return null;

        return Directory.GetDirectories(root)
            .Where(dir => File.Exists(Path.Combine(dir, "AI_ANALYSIS_GUIDE.md")) &&
                          File.Exists(Path.Combine(dir, "ai_profiler_digest.json")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string FindLatestAnalysisDir()
    {
        string projectRoot = Directory.GetCurrentDirectory();
        string[] roots =
        {
            Path.Combine(projectRoot, "ProfilerAIExports"),
            Path.Combine(projectRoot, "FrameDebuggerAIExports"),
            Path.Combine(projectRoot, "FxAIAnalysisExports")
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetDirectories(root))
            .Where(IsCompleteAnalysisDir)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsCompleteAnalysisDir(string dir)
    {
        return Directory.Exists(dir) &&
               File.Exists(Path.Combine(dir, "AI_ANALYSIS_GUIDE.md")) &&
               (File.Exists(Path.Combine(dir, "ai_profiler_digest.json")) ||
                File.Exists(Path.Combine(dir, "ai_summary.json")) ||
                File.Exists(Path.Combine(dir, "manifest.json")));
    }

    private static string FindLatestFxExportDir()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "FxAIAnalysisExports");
        if (!Directory.Exists(root))
            return null;

        return Directory.GetDirectories(root)
            .Where(dir => File.Exists(Path.Combine(dir, "AI_ANALYSIS_GUIDE.md")) &&
                          File.Exists(Path.Combine(dir, "manifest.json")) &&
                          Directory.Exists(Path.Combine(dir, "particles")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string FindLatestLinkedRenderExportDir()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "FrameDebuggerAIExports");
        if (!Directory.Exists(root))
            return null;

        return Directory.GetDirectories(root, "FrameDebugAI_*")
            .Where(IsCompleteLinkedRenderExportDir)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsCompleteLinkedRenderExportDir(string dir)
    {
        if (!Directory.Exists(dir) ||
            !File.Exists(Path.Combine(dir, "AI_ANALYSIS_GUIDE.md")) ||
            !File.Exists(Path.Combine(dir, "ai_summary.json")) ||
            !File.Exists(Path.Combine(dir, "linked_capture.json")))
            return false;

        try
        {
            string linkedJson = File.ReadAllText(Path.Combine(dir, "linked_capture.json"), Encoding.UTF8);
            string renderDocAnalysisDir = ExtractJsonString(linkedJson, "renderDocAnalysisDirectory");
            return !string.IsNullOrWhiteSpace(renderDocAnalysisDir) &&
                   Directory.Exists(renderDocAnalysisDir) &&
                   File.Exists(Path.Combine(renderDocAnalysisDir, "analysis_ready_summary.json")) &&
                   File.Exists(Path.Combine(renderDocAnalysisDir, "pass_table.json"));
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPrompt(string projectRoot, string exportDir, string reportPath)
    {
        string relativeExportDir = ToProjectRelativePath(exportDir);
        string relativeReportPath = ToProjectRelativePath(reportPath);
        var sb = new StringBuilder(4096);
        sb.AppendLine("解析 `" + relativeExportDir.Replace("\\", "/") + "` 这个 Unity Profiler AI 导出目录。");
        sb.AppendLine();
        sb.AppendLine("项目根目录: `" + projectRoot.Replace("\\", "/") + "`");
        sb.AppendLine("Profiler AI 导出目录: `" + relativeExportDir.Replace("\\", "/") + "`");
        sb.AppendLine("最终报告文件: `" + relativeReportPath.Replace("\\", "/") + "`");
        sb.AppendLine();
        sb.AppendLine("请像正常 AI 编程助手会话一样自行读取该目录下的 guide、summary、digest、worstframes 和 samples，结合项目代码给出中文性能分析报告。");
        sb.AppendLine("不要修改项目代码；只把完整 Markdown 报告写入最终报告文件。");
        sb.AppendLine("如果数据已经足够，就直接生成报告，不要为了形式继续扫项目。");
        return sb.ToString();
    }

    private static string BuildFrameDebugPrompt(string projectRoot, string exportDir, string reportPath)
    {
        string relativeExportDir = ToProjectRelativePath(exportDir);
        string relativeReportPath = ToProjectRelativePath(reportPath);
        var sb = new StringBuilder(6144);
        sb.AppendLine("解析 `" + relativeExportDir.Replace("\\", "/") + "` 这个 Unity Frame Debugger AI 导出目录。");
        sb.AppendLine();
        sb.AppendLine("项目根目录: `" + projectRoot.Replace("\\", "/") + "`");
        sb.AppendLine("Frame Debugger AI 导出目录: `" + relativeExportDir.Replace("\\", "/") + "`");
        sb.AppendLine("最终报告文件: `" + relativeReportPath.Replace("\\", "/") + "`");
        sb.AppendLine();
        sb.AppendLine("请按以下顺序读取导出数据：");
        sb.AppendLine("1. `AI_ANALYSIS_GUIDE.md`");
        sb.AppendLine("2. `ai_data_quality.json` / `ai_data_quality.md`");
        sb.AppendLine("3. `ai_baseline_comparison.json`");
        sb.AppendLine("4. `ai_summary.json`");
        sb.AppendLine("5. `ai_event_evidence.jsonl`");
        sb.AppendLine("6. `ai_event_analysis.jsonl`");
        sb.AppendLine("7. `ai_frameeventdata_diagnostics.json`");
        sb.AppendLine("8. `ai_direct_object_diagnostics.json`");
        sb.AppendLine("9. `ai_events_raw.jsonl`");
        sb.AppendLine("10. `ai_ui_batches.jsonl`");
        sb.AppendLine("11. SRPBatch 候选为空或偏少时读取 `ai_srp_batch_diagnostics.json`");
        sb.AppendLine("12. 针对 UI / Particle / Renderer / Camera / RenderFeature 读取 `snapshots/ui_graphics.jsonl`、`snapshots/particles.jsonl`、`snapshots/renderers_compact.jsonl`、`snapshots/cameras.json`、`snapshots/renderfeatures.json`；`snapshots/renderers.jsonl` 是可选 debug 文件，不一定存在");
        sb.AppendLine("13. 使用 `dictionaries/*.json` 展开 compact evidence 里的 id；`human/FrameDebug.md` 只作为人工显示补充");
        sb.AppendLine("14. `debug/ai_events_raw_full.jsonl` 和 `debug/ai_direct_object_diagnostics_full.json` 是可选 debug 明细，只有 compact/root 文件不足时再读");
        sb.AppendLine();
        sb.AppendLine("分析要求：");
        sb.AppendLine("- 报告使用中文 Markdown，直接写入最终报告文件。");
        sb.AppendLine("- 不要修改项目代码。");
        sb.AppendLine("- 先判断这份 Frame Debugger 导出是否足够好用：哪些字段能支撑 AI 判断，哪些字段仍缺失或不可靠。");
        sb.AppendLine("- 报告前半部分只放“可证明的问题”：必须有 eventIndex、stage、camera、material/shader/object 或 renderFeature/camera/ui snapshot 证据。");
        sb.AppendLine("- 必须使用 `confidence` 字段分层：direct、inferred_high、inferred_medium、inferred_low、snapshot_only、unknown。不要把 snapshot_only 当成当前帧开销。");
        sb.AppendLine("- 明确写出 `timingAvailable=false` 和 `costRankingBasis=draw_count_only`，不要推断 GPU耗时排序、CPU耗时排序、带宽成本、Overdraw实际面积、Shader ALU/Texture采样成本。");
        sb.AppendLine("- 对 `Canvas.RenderSubBatch`，优先使用 `ai_event_evidence.jsonl` / `ai_event_analysis.jsonl` 里的 candidateAttributions，再查 `ai_ui_batches.jsonl`。这是运行时 scene graph 反推 batch，比普通 UI snapshot 强，但仍需标注为 heuristic。");
        sb.AppendLine("- `unresolvedCategory` 只表示 direct 和 candidate 都没有的真正未归因事件；有 UI/SRP/Particle candidate 的事件即使缺 direct object，也不要再算 unresolved。归因类型看 `attributionKind`，证据来源看 `attributionSource`。");
        sb.AppendLine("- 对 SRPBatch / mesh batch，区分 `directMeshName`、`frameDebuggerDetailMeshNames`、`frameDebuggerMeshInstanceIds`、`rendererCandidateMeshNames`；只有 detail mesh / mesh instance id 才算 Unity FrameDebuggerEventData 详情证据。");
        sb.AppendLine("- 判断 mesh 详情缺失时必须使用 `meshExpectedEvents`、`meshExpectedButMissingDetailMeshes`、`meshNotExpectedEvents`、`srpBatchMeshExpectedEvents`、`srpBatchMeshDetailCoveredEvents`、`meshEventDirectRendererCoveredEvents`，不要把 Clear/PostProcess/UI/RenderFeature/Procedural 这类本来没有 mesh 的事件算作缺失。");
        sb.AppendLine("- 如果 `eventsWithFrameDebuggerGameObject` / `eventsWithFrameDebuggerRenderer` 为 0，必须读取 `ai_direct_object_diagnostics.json`。不要直接判定导出失败；SRPBatch 和 Canvas.RenderSubBatch 这类批次事件通常没有单一 GameObject/Renderer。");
        sb.AppendLine("- `uiSubBatchWithoutFrameDebuggerGraphic` 是预期缺失口径；UI 是否可分析主要看 `uiSubBatchCandidateCovered` 和 `uiSubBatchUnresolved`。");
        sb.AppendLine("- 粒子候选按 direct / inferred_high / inferred_medium / inferred_low 分层：direct 来自 FrameDebugger renderer；path+material+shader 为 high；path+material 或 material+shader 为 medium；只有 path 或只有材质为 low。");
        sb.AppendLine("- baseline 里如果 `directAttributionRate` 下降，必须同时看 `directAttributionRateExcludingBatched`、`directAttributionRateForNonBatchedMesh`、`uiCandidateCoverageRate`、`srpMeshCoverageRate`，避免把采样帧批次变多误判为导出器退化。");
        sb.AppendLine("- 如果要进一步追 UI 批次对应的真实 GameObject，先读本次导出的 `ai_object_inventory.json`、`ai_event_attribution_index.jsonl`、`ai_ui_batch_details.jsonl`；仍无法确认时降级为 capture_data_quality，不要把“下一轮补导出”写成优化动作。");
        sb.AppendLine("- 如果 `GetFrameEventData` 只有少量成功，必须优先报告 `ai_frameeventdata_diagnostics.json` 里的 limit/失败原因，不要把空 SRPBatch 候选解释成场景里没有 SRPBatch 对象。");
        sb.AppendLine("- 后半部分单独放“数据不足导致无法确认的问题”：没有 direct object reference 的 Canvas.RenderSubBatch 可以写候选 batch/Graphic，但不要写成直接证明。");
        sb.AppendLine("- 把 setpass / draw call 增多的证据绑定到具体 material、shader、pass、lightMode、renderQueue、keywords、texture/property 差异和 batchBreakCause。");
        sb.AppendLine("- 对透明头发、半透明穿插、不同材质导致无法合批、Shader Pass 拆分目的等问题，优先用导出的事件和项目 Shader/Material 路径做判断。");
        sb.AppendLine("- 区分确定结论和推断结论；推断必须写清楚来自哪些字段。");
        sb.AppendLine("- 给出按优先级排序的优化方向和改后验证指标；验证指标可以要求 Profiler/GPU timing/截图对比，但不要要求重新补一次 Frame Debugger 导出作为优化动作。");
        return sb.ToString();
    }

    private static string BuildLinkedRenderPrompt(string projectRoot, string frameDebugDir, string renderDocDir, string linkedCapturePath, string reportPath, string auditPath)
    {
        string relativeFrameDebugDir = ToProjectRelativePath(frameDebugDir);
        string relativeRenderDocDir = ToProjectRelativePath(renderDocDir);
        string relativeLinkedCapturePath = ToProjectRelativePath(linkedCapturePath);
        string relativeReportPath = ToProjectRelativePath(reportPath);
        string relativeAuditPath = ToProjectRelativePath(auditPath);
        var sb = new StringBuilder(12288);

        sb.AppendLine("# Unity + RenderDoc 联动帧综合分析任务");
        sb.AppendLine();
        sb.AppendLine("你不是分别分析两个导出目录，而是要把它们当作同一帧/同一冻结状态的两类证据，输出一份综合中文报告。");
        sb.AppendLine();
        sb.AppendLine("项目根目录: `" + projectRoot.Replace("\\", "/") + "`");
        sb.AppendLine("Unity Frame Debugger 导出目录: `" + relativeFrameDebugDir.Replace("\\", "/") + "`");
        sb.AppendLine("RenderDoc 解析目录: `" + relativeRenderDocDir.Replace("\\", "/") + "`");
        sb.AppendLine("联动元数据: `" + relativeLinkedCapturePath.Replace("\\", "/") + "`");
        sb.AppendLine("最终人读报告: `" + relativeReportPath.Replace("\\", "/") + "`");
        sb.AppendLine("机器审计文件: `" + relativeAuditPath.Replace("\\", "/") + "`");
        sb.AppendLine();
        sb.AppendLine("## 报告目标");
        sb.AppendLine();
        sb.AppendLine("- 这份报告的目的不是证明分析过程充分，而是给出真正可执行的渲染优化方案。");
        sb.AppendLine("- 人读报告必须优先回答：该改哪里、为什么该改、到哪个 Unity event / RenderDoc pass/event 复查、改完看什么指标。");
        sb.AppendLine("- 不要把大量篇幅用于分析过程、文件读取顺序、证据收集流水、DeepEvent 执行日志或智能体分工；这些只能写入机器审计 JSON。");
        sb.AppendLine("- 如果当前数据只能支持 stage/pass 级结论，也要把建议落到具体相机、阶段、RenderFeature、passId、eventId 范围和验证指标，不能写泛泛的 Unity 优化常识。");
        sb.AppendLine("- 数据缺口只能作为“为什么无法点名对象”的边界说明，不允许盖过真正的优化内容。");
        sb.AppendLine();
        sb.AppendLine("## 必须执行的分析组织方式");
        sb.AppendLine();
        sb.AppendLine("- 最终报告必须是“问题驱动”的综合分析，不是先写 FrameDebugger 一节、再写 RenderDoc 一节、最后拼一个对照表。");
        sb.AppendLine("- 如果当前 AI 工具支持子智能体/Task/Agent，请主动拆成至少 3 个取证任务，但子任务只返回证据候选，不输出独立报告：");
        sb.AppendLine("  1. `RenderDocPipelineAgent`: 只看 RenderDoc 解析目录，提取 GPU pass、pipeline state、RT/depth、resource/shader/draw 参数异常候选。");
        sb.AppendLine("  2. `UnityAttributionAgent`: 只看 Frame Debugger 导出和项目资源，提取 Unity 对象、Canvas、Renderer、Material、Shader、RenderFeature、batch break 归因候选。");
        sb.AppendLine("  3. `CorrelationAgent`: 根据 linked_capture、pass/draw 顺序、RT/Shader/Resource/Viewport/Draw 特征，把证据候选合成为最终问题。");
        sb.AppendLine("- 如果当前 AI 工具不支持真实子智能体，也必须在一次分析中按上述三个角色分阶段取证，然后只输出一份问题中心的综合报告。");
        sb.AppendLine("- 每个最终问题都要有一个统一结论，然后在该问题内部写 Unity 侧证据、RenderDoc 侧证据、关联依据。");
        sb.AppendLine("- 每个最终问题都必须给出人工复查定位：Unity Frame Debugger eventIndex/stage/camera，以及 RenderDoc passId/eventId/sampleEvents/stateChangeEvents。");
        sb.AppendLine("- 不要只写 `23 -> 86 -> 228 -> 后处理链` 这种压缩模式；如果使用模式描述，必须同时给出可复查的 pass/event 表。");
        sb.AppendLine("- 不要把数据质量缺口伪装成渲染问题；数据质量问题单独归类为 capture_data_quality。");
        sb.AppendLine("- 开始分级前必须读取 `ai_object_inventory.json`、`ai_event_attribution_index.jsonl`、`ai_analysis_blocking_policy.json.analysisBlockingPolicy.dataSufficiency` 和 `ai_data_quality.json.dataQuality.dataSufficiency`。这是单次抓帧工作流：先用本次导出的对象库存和事件归因索引，仍无法确认时降级，不要建议下一轮补导出。");
        sb.AppendLine("- 不要把 RenderDoc native API 层的重复提交直接写成 Unity 业务重复渲染；必须先执行 `capture_artifact` 判定。");
        sb.AppendLine("- 命中 `capture_artifact` 规则的候选不得进入人读报告的 `优化结论摘要`、`优先处理清单` 或 `综合问题列表`；只能写入机器审计 JSON，必要时在人读报告 `已排除的问题` 中用一句话说明已排除。");
        sb.AppendLine("- 不要把读取文件列表、event/shader 抽样列表、相似度计算细节、DeepEvent 执行流水写进人读报告；这些必须写进机器审计文件。");
        sb.AppendLine("- DeepEvent 的目的不是生成一个单独文件，而是为某个最终问题补足证据。每个已读取的 `deep_event_*.json` 必须被归入对应问题的 RenderDoc 证据、结论或排除项；如果没有改变结论，要在 audit 写明原因。");
        sb.AppendLine("- 子智能体/分阶段取证必须通过 CorrelationAgent 合并成同一份问题清单。不得把各 agent 的结论并排粘贴，也不得让各 agent 互不引用。");
        sb.AppendLine("- 如果子智能体可能超时或环境不稳定，直接在当前会话顺序执行 RenderDocPipelineAgent / UnityAttributionAgent / CorrelationAgent 三阶段；超时的子智能体输出不算证据，也不能影响最终报告。");
        sb.AppendLine("- P1/P2 优先级必须受证据门槛约束：snapshot-only、widthHeight-only、weakMatches、UI runtime-order heuristic、缺少 owner 不得排成 P1，除非同时有 RenderDoc pass/draw/state 结构热点和明确 Unity event/stage 定位。");
        sb.AppendLine("- P1 必须有当前报告即可执行的项目侧 owner 或改法，例如具体 camera、Canvas、RenderFeature、material/shader 组或 runtime path。若主要动作会变成 `补导出`、`下一轮再点名`、`需要更多数据`，不要作为优化项输出，改放入 `仍需补充的数据`。");
        sb.AppendLine("- P2 不等于“没问题”；P2 表示有可执行的结构性优化候选，但缺少耗时、对象 owner 或可见正确性证据，不能升为 P1。如果最终报告没有 P0/P1，必须在摘要后写 `分级说明`，解释为什么全部被证据门槛压到 P2/P3。");
        sb.AppendLine("- `优化结论摘要` 只放本帧已定位、能行动的问题；snapshot-only 候选、弱关联对象、只需要补采的数据，放到 `仍需补充的数据`，不要放进摘要。");
        sb.AppendLine("- 不要修改项目代码或资源；只写最终报告文件和机器审计文件。");
        sb.AppendLine();
        sb.AppendLine("## 总读取顺序");
        sb.AppendLine();
        sb.AppendLine("1. 先读 Frame Debugger 目录里的 `AI_LINKED_ANALYSIS_GUIDE.md`（如果存在），它是联动综合分析的主规则。");
        sb.AppendLine("2. 再读联动元数据 `linked_capture.json`，确认 `sameFrameGuarantee`、`renderDocAnalysisStatus`、`renderDocAnalysisDirectory`、目标进程/API/frameNumber、`sceneSource`。");
        sb.AppendLine("   - 如果 `sceneSource=unavailable_for_remote_development_player`，报告里必须写远端场景未知；不要把 `editorSceneAtExport` 当成模拟器捕获场景。");
        sb.AppendLine("3. 读 Frame Debugger: `AI_ANALYSIS_GUIDE.md`、`ai_summary.json`、`ai_data_quality.json`、`ai_runtime_resolution_snapshot.json`、`ai_transparent_submission_snapshot.json`、`ai_object_inventory.json`、`ai_event_attribution_index.jsonl`、`ai_event_evidence.jsonl`、`ai_event_analysis.jsonl`。");
        sb.AppendLine("4. 读 RenderDoc: `AI_ANALYSIS_GUIDE.md`、`analysis_ready_summary.json`、`pass_table.json`、`resource_table.json`。");
        sb.AppendLine("   - 如果存在 `deep_event_preflight_status.json`，先读取它；若 status=completed，必须优先读取其中列出的 `deep_event_*.json`，并把它们用于支持、降级或排除具体问题。");
        sb.AppendLine("5. 对可疑 Unity event，优先查 `ai_event_attribution_index.jsonl` 和 `ai_object_inventory.json`，再查 `ai_events_raw.jsonl`、`ai_ui_batches.jsonl`、`ai_srp_batch_candidates.jsonl`、`snapshots/renderers.jsonl` / `snapshots/renderers_compact.jsonl`、`dictionaries/materials.json`、`dictionaries/shaders.json`。");
        sb.AppendLine("6. 对可疑 RenderDoc pass/event，用 `pipeline_index.json` 定位 `pipeline_state_changes.jsonl`，不要整体读取 `pipeline_state_changes.jsonl`。");
        sb.AppendLine("7. 需要 shader 细节时，用 `shader_index.json` 定位 `shader_table.jsonl`，不要整体读取 `shader_table.jsonl`。");
        sb.AppendLine("8. 需要单个 GPU event 深挖时，按 RenderDoc 目录的 `AI_ANALYSIS_GUIDE.md` 调用 DeepEvent，并读取生成的 `deep_event_*.json` 后再下结论；不要把“建议用户 deep event”作为最终结论。DeepEvent 输出必须服务最终问题，不能只登记为一个文件。");
        sb.AppendLine();
        sb.AppendLine("## 融合规则");
        sb.AppendLine();
        sb.AppendLine("- Frame Debugger 的强项：Unity 对象名、GameObject/Renderer/Material/Shader 路径、Canvas/UI 反推、SRPBatch/Particle 候选、batch break、RenderFeature、Camera 和项目资源映射。");
        sb.AppendLine("- RenderDoc 的强项：真实 GPU API 事件、RenderTarget/Depth/Viewport/Scissor/Pipeline state、resource binding、shader reflection、draw 参数、RT 格式/尺寸、可疑状态和资源角色。");
        sb.AppendLine("- 不能强行假设 Unity eventIndex 等于 RenderDoc eventId。只能根据 pass 顺序、draw 数量、RT/Depth、Shader/Resource、Viewport、Clear/Draw 模式、UI/PostProcess/Transparent/Opaque 推断对应关系，并标注置信度。");
        sb.AppendLine("- 每个问题的证据等级使用：`confirmed_both_sides`、`confirmed_renderdoc_only`、`confirmed_unity_only`、`inferred_correlated`、`data_gap`。");
        sb.AppendLine("- 禁止使用 `high`、`medium`、`low`、`medium-low`、`high for RenderDoc` 等自由置信度标签。");
        sb.AppendLine("- 每个问题的分类使用：`render_correctness`、`submission_structure`、`capture_artifact`、`capture_data_quality`、`negative_finding`。");
        sb.AppendLine("- 读取 `ai_unity_renderdoc_correlation_seed.json` 时，`resourceMatches` / `passMatches` / `drawLevelCandidates` 才是主候选；`weakMatches` 或 `matchedBy=widthHeight` 只能作为搜索入口，不能作为报告证据。");
        sb.AppendLine("- `capture_data_quality` 只说明本次单帧证据边界，不是优化问题；如果只来自 snapshot-only、weakMatches、UI heuristic 或宽高匹配，默认 P3，并移到 `仍需补充的数据`。");
        sb.AppendLine("- `inferred_correlated` 可以是 P1 仅限两类：RenderDoc 有明确重 pass/draw/state/RT 结构热点，且 Unity 有明确 camera/stage/eventIndex 对应；或两边都确认同一 RenderFeature/UI 相机阶段问题。对象级候选不足不能单独提升优先级。");
        sb.AppendLine("- `capture_artifact` 专门用于 RenderDoc TargetControl 抓帧边界、Unity Frame Debugger 远端冻结/读回、native readback/copy/blit/present 边界导致的 API 层重复提交。");
        sb.AppendLine("- 如果 RenderDoc 出现相似 pass/draw 序列，但序列跨过 `vkCmdCopyImageToBuffer`、`vkCmdBlitImage`、readback/copy、`vkQueuePresentKHR` 或 Frame Debugger 冻结/读回相关边界，而 Unity Frame Debugger 只显示一轮逻辑事件，默认归类为 `capture_artifact` 或 `negative_finding`，不能写成项目重复渲染，也不能作为优化问题进入问题列表。");
        sb.AppendLine("- 只有同时满足以下任一条件，才允许报告“业务重复渲染”：Unity Frame Debugger 也显示同一相机/阶段/对象/材质的重复逻辑事件；或 runtime snapshot/resource fingerprint + DeepEvent 证明两段都是正常渲染 pass 且不跨调试读回/复制/present 边界。");
        sb.AppendLine("- DeepEvent 用于检查边界附近的真实 API 命令；如果证据包含 `vkCmdCopyImageToBuffer`、`vkCmdBlitImage`、readback/copy 或 present 链路，它优先支持伪影排除结论，而不是优化问题。");
        sb.AppendLine("- `ai_deep_event_sampling_plan.json` 中 `matchedBy=representativePassSample` 的样本只是诊断探针，不能单独生成报告问题；如果它们只证明两段 pass 高度相似且跨调试边界，应从人读问题列表排除。");
        sb.AppendLine("- 优先关注渲染异常，不只是性能：材质丢失、Unity error shader/粉色 shader 风险、Shader/Pass 不匹配、Mesh/Index/Vertex 参数异常、RT/Depth 绑定异常、透明排序/Blend/ZTest/ZWrite 异常、PostProcess feedback、Viewport/Scissor 异常、资源格式/尺寸异常。");
        sb.AppendLine("- 性能只作为第二类问题：pass 组织、状态切换、draw 提交碎片、重复材质/Shader/Texture、过大 RT/Texture/Buffer、过多透明/UI pass。不要伪造 GPU/CPU 耗时。");
        sb.AppendLine("- 高证据提交组必须展开：任一 RenderDoc pass/group 满足 `drawCount >= 50`、`stateChangeEvents.Count >= 50` 或 state-change/draw 比例 >= 0.5 时，必须在对应问题中给出详细表格，不能压缩成一句话或一个泛泛结论。");
        sb.AppendLine("- 透明/UI 提交必须按链路拆开：HDR transparent、sRGB/overlay transparent、UI、postprocess-like 需要分别列 RT/depth、draw、state change、sample events、blend/depth 行为、主要 shader/state/resource 组。缺少对象 owner 时仍要写足 pass/stage 级证据。");
        sb.AppendLine("- FinalBlit/尺寸问题必须先分清两层：Unity 是否真的执行 FinalBlit，和尺寸差异是否是项目问题。Unity 有 `FinalBlit`/`DrawProcedural`/`Hidden/Universal/CoreBlit`/`m_RenderTargetIsBackBuffer=True` 只能证明 FinalBlit 存在，不能单独证明分辨率错误。");
        sb.AppendLine("- 如果 Unity GameView/backbuffer 尺寸、RenderDoc 中间 RT 尺寸、RenderDoc swapchain 尺寸三者不一致，且没有截图/pixel-history 或项目相机/render scale 配置证明可见拉伸/裁剪，默认归类为 `capture_data_quality` 或至多 P3，不得放入首要优化清单。");
        sb.AppendLine("- 如果 Frame Debugger markerPath 或 RenderDoc shader 名匿名，要明确写这是数据限制，并说明用了什么替代特征做归因。");
        sb.AppendLine();
        sb.AppendLine("## 最终报告结构");
        sb.AppendLine();
        sb.AppendLine("必须写入最终人读综合报告文件，结构如下：");
        sb.AppendLine();
        sb.AppendLine("1. `优化结论摘要`：3-6 条最重要的可执行结论，按优化优先级排序。每条必须包含具体定位，例如 Unity eventIndex/stage 或 RenderDoc passId/eventId。");
        sb.AppendLine("   - 如果全部 actionable items 都是 P2/P3，摘要后必须添加 `分级说明`：说明没有 P1 是因为缺少耗时、对象 owner 或可见正确性证据；同时说明 P2 仍是结构性优化候选，不是无问题。");
        sb.AppendLine("2. `优先处理清单`：表格列出 P0/P1/P2、优化对象/阶段、定位、建议动作、预期改善、验证指标。这里是报告重点。");
        sb.AppendLine("3. `综合问题列表`：只按最终问题组织，不按 Unity/RenderDoc 来源组织。每个问题内部包含两边证据和关联依据。");
        sb.AppendLine("   - 每个问题必须包含 `人工复查定位`，方便人直接在 Frame Debugger / RenderDoc 中查到。");
        sb.AppendLine("   - 对重复序列、pass 链路、状态切换密集这类问题，必须给表格：passId、eventId range、draw count、role、RT/depth、sample eventIds、对应 Unity eventIndex/stage。");
        sb.AppendLine("   - 对透明/UI 高 draw 或高 state-change 问题，必须给扩展表：链路类型、RT/depth、draw、state-change、sample events、主要 shader/state/resource 组、Unity 对应 stage/eventIndex。");
        sb.AppendLine("   - FinalBlit/尺寸差异只有在证明项目侧相机/render scale/输出配置导致可见问题时才进入这里；只有窗口/swapchain/中间 RT 宽高不一致时，放入 `关键证据限制` 或 `仍需补充的数据`。");
        sb.AppendLine("   - 每个问题必须包含 `优化动作` 和 `验证指标`，不能只写风险说明。");
        sb.AppendLine("   - `capture_artifact`、`negative_finding`、纯数据质量问题不得放在这里。");
        sb.AppendLine("4. `关键证据限制`：压缩说明会影响结论的限制，最多 5 条。不要把数据质量写成主体。");
        sb.AppendLine("5. `已排除的问题`：例如没有错误 shader、没有 zero-index draw 等，不能放进高风险问题里。");
        sb.AppendLine("6. `仍需补充的数据`：只列真正阻止对象级归因或耗时判断的数据缺口；每条要说明来自 `currentCaptureGaps` 还是 `externalDataRequirements`。不要泛泛写“补导出”，也不要把重新导出当成优化动作。");
        sb.AppendLine();
        sb.AppendLine("人读报告不要包含单独的 `分析执行记录` 章节，不要记录完整读取流水或计算过程。只在具体问题的 `人工复查定位` 中保留人需要点击/搜索的编号。");
        sb.AppendLine();
        sb.AppendLine("每个问题至少包含：");
        sb.AppendLine("- 问题名称");
        sb.AppendLine("- 分类：render_correctness / submission_structure / capture_artifact / capture_data_quality / negative_finding");
        sb.AppendLine("- 严重程度：P0/P1/P2/P3");
        sb.AppendLine("- 证据等级");
        sb.AppendLine("- Unity 侧证据：eventIndex/stage/camera/material/shader/object/renderFeature/batchBreakCause，能找到多少写多少");
        sb.AppendLine("- RenderDoc 侧证据：passId/eventId/stateHash/resourceId/shaderId/RT/depth/blend/depth/raster/draw 参数，能找到多少写多少");
        sb.AppendLine("- 关联依据：为什么认为两边对应或相关");
        sb.AppendLine("- 人工复查定位：Unity 中查哪些 eventIndex/stage/camera；RenderDoc 中查哪些 passId/eventId/sampleEvents/stateChangeEvents");
        sb.AppendLine("- 风险说明");
        sb.AppendLine("- 优化动作：项目侧能执行的检查或改法");
        sb.AppendLine("- 验证指标：下一次 Frame Debugger / RenderDoc / Profiler 应该下降或变化的具体计数、pass、draw、state change、RT 尺寸或截图指标");
        sb.AppendLine();
        sb.AppendLine("## 必须额外写入机器审计 JSON 文件");
        sb.AppendLine();
        sb.AppendLine("把读取与计算过程写入 `" + relativeAuditPath.Replace("\\", "/") + "`。这是给自动化/AI 复查用的 sidecar，不是给人读的报告。");
        sb.AppendLine("必须是合法 JSON，可以紧凑，不需要 Markdown。建议结构：");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"schemaVersion\": \"linked-render-analysis-audit/v1\",");
        sb.AppendLine("  \"filesRead\": [],");
        sb.AppendLine("  \"pipelineEventsRead\": [],");
        sb.AppendLine("  \"shaderIdsRead\": [],");
        sb.AppendLine("  \"deepEvents\": [{ \"eventId\": 0, \"status\": \"generated|skipped|failed\", \"path\": \"\", \"reason\": \"\" }],");
        sb.AppendLine("  \"correlationCandidates\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"issueId\": \"ISSUE-001\",");
        sb.AppendLine("      \"unityLocators\": [],");
        sb.AppendLine("      \"renderDocLocators\": [],");
        sb.AppendLine("      \"scoreBreakdown\": {");
        sb.AppendLine("        \"passOrder\": \"\",");
        sb.AppendLine("        \"targetMatch\": \"\",");
        sb.AppendLine("        \"drawCountMatch\": \"\",");
        sb.AppendLine("        \"shaderOverlap\": \"\",");
        sb.AppendLine("        \"stateHashOverlap\": \"\"");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("禁止事项：");
        sb.AppendLine("- 不要只建议使用 DeepEvent；需要时自己调用并读取结果。");
        sb.AppendLine("- 不要把 DeepEvent 当成单独成果；DeepEvent 只算最终问题的证据来源。");
        sb.AppendLine("- 不要用 agent 分工代替融合结论；agent 只做取证，最终报告只允许一个综合问题清单。");
        sb.AppendLine("- 不要整体读取 RenderDoc 大 JSONL。");
        sb.AppendLine("- 不要把 draw count 当 GPU time。");
        sb.AppendLine("- 不要把 inferred/candidate 当 direct evidence。");
        sb.AppendLine("- 不要只给抽象链路模式，必须给人能复查的具体编号、范围和采样事件。");
        sb.AppendLine("- 不要把文件读取列表、shaderId/eventId 大列表、关联打分明细、DeepEvent 执行流水写进人读报告；写到机器审计 JSON。");
        sb.AppendLine("- 不要泛泛讲 Unity 渲染优化常识，必须绑定本帧具体证据。");
        return sb.ToString();
    }

    private static string BuildFxPrompt(string projectRoot, string exportDir, string reportPath)
    {
        string relativeExportDir = ToProjectRelativePath(exportDir);
        string relativeReportPath = ToProjectRelativePath(reportPath);
        var sb = new StringBuilder(8192);
        sb.AppendLine("解析 `" + relativeExportDir.Replace("\\", "/") + "` 这个 Unity FX AI 导出目录，输出中文 Markdown 特效优化报告。");
        sb.AppendLine();
        sb.AppendLine("项目根目录: `" + projectRoot.Replace("\\", "/") + "`");
        sb.AppendLine("FX AI 导出目录: `" + relativeExportDir.Replace("\\", "/") + "`");
        sb.AppendLine("最终报告文件: `" + relativeReportPath.Replace("\\", "/") + "`");
        sb.AppendLine();
        sb.AppendLine("必须先读取：");
        sb.AppendLine("1. `AI_ANALYSIS_GUIDE.md`");
        sb.AppendLine("2. `manifest.json`");
        sb.AppendLine("3. `full/full_contact_sheet.png`，以及 `full/views/front`、`full/views/side`、`full/views/top` 下的 contact sheet");
        sb.AppendLine("4. 每个 `particles/<index_name>/settings.json`");
        sb.AppendLine("5. 每个 `particles/<index_name>/solo/solo_contact_sheet.png`");
        sb.AppendLine("6. 每个 `particles/<index_name>/without_this/without_this_contact_sheet.png`");
        sb.AppendLine("7. 必要时再看对应目录中的单帧 PNG，确认开始、爆发、衰减、残留阶段");
        sb.AppendLine();
        sb.AppendLine("分析方式：");
        sb.AppendLine("- 先看完整效果，判断整体表现：方向、打击点、主体火焰/烟/光/拖尾/火星/残影/冲击层的时间关系。");
        sb.AppendLine("- 对每个子粒子，必须同时比较 solo 和 without_this：solo 说明它自己是什么，without_this 说明它对整体缺失了什么。");
        sb.AppendLine("- 不要只根据 `settings.json` 下结论；设置只能解释原因，视觉贡献必须以图片为准。");
        sb.AppendLine("- 对透明烟雾、additive 光、trail、宽幅半透明片、长生命周期残留要特别标记，因为这些通常是 overdraw 和视觉噪声风险。");
        sb.AppendLine("- 如果某个子粒子 solo 明显但 without_this 几乎看不出差异，标记为低视觉贡献高可优化候选。");
        sb.AppendLine("- 如果某个子粒子 without_this 后打击点、速度感、轮廓或空间方向明显丢失，标记为高视觉重要性，即使参数看起来贵也不要直接建议删除。");
        sb.AppendLine("- 区分确定结论和推断结论；看不清的地方直接写不确定，并说明需要什么补充视角或运行时 Profiler 数据。");
        sb.AppendLine();
        sb.AppendLine("每个子粒子输出固定结构：");
        sb.AppendLine("- 路径 / 名称");
        sb.AppendLine("- 视觉角色：主表现、辅助光、烟雾体积、拖尾方向、火星细节、冲击闪光、残留氛围等");
        sb.AppendLine("- 视觉重要性：高 / 中 / 低，并说明来自 solo/without_this 的证据");
        sb.AppendLine("- 成本风险：高 / 中 / 低，依据包括粒子数、生命周期、发射率、trail、材质/贴图、透明覆盖范围、shader、排序层级");
        sb.AppendLine("- 优化建议：删除、降发射、缩生命周期、缩小 size、降低透明覆盖、合并材质/贴图、改 trail、改贴图、保留不动等");
        sb.AppendLine("- 风险：改动后会丢失什么视觉效果");
        sb.AppendLine();
        sb.AppendLine("最终报告必须包含：");
        sb.AppendLine("1. 整体效果概述");
        sb.AppendLine("2. 优先级排序的优化清单，P0/P1/P2");
        sb.AppendLine("3. 每个子粒子的作用和建议表格");
        sb.AppendLine("4. 不建议动的关键层");
        sb.AppendLine("5. 建议美术/技术进一步验证的点，例如移动端 overdraw、Frame Debugger、Profiler 粒子耗时、真机截图对比");
        sb.AppendLine();
        sb.AppendLine("禁止事项：");
        sb.AppendLine("- 不要修改项目代码或资源。");
        sb.AppendLine("- 不要直接给出批量改 maxParticles 的建议，除非 solo/without_this 和实际粒子数都支持。");
        sb.AppendLine("- 不要把所有透明层都简单归为可删；必须说明视觉损失。");
        sb.AppendLine("- 不要输出泛泛而谈的 Unity 粒子优化常识，必须绑定该导出目录里的具体子粒子和证据。");
        sb.AppendLine();
        sb.AppendLine("把完整 Markdown 报告写入 `" + relativeReportPath.Replace("\\", "/") + "`。");
        return sb.ToString();
    }

    private static string BuildComparePrompt(string projectRoot, string[] exportDirs, string reportPath)
    {
        string relativeReportPath = ToProjectRelativePath(reportPath);
        var sb = new StringBuilder(8192);
        sb.AppendLine("对比多个 Unity Profiler AI 导出目录，输出中文 Markdown 对比分析报告。");
        sb.AppendLine();
        sb.AppendLine("项目根目录: `" + projectRoot.Replace("\\", "/") + "`");
        sb.AppendLine("最终报告文件: `" + relativeReportPath.Replace("\\", "/") + "`");
        sb.AppendLine();
        sb.AppendLine("待对比导出目录:");
        for (int i = 0; i < exportDirs.Length; i++)
        {
            string dir = exportDirs[i];
            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture) + ". `" + ToProjectRelativePath(dir).Replace("\\", "/") + "`");
        }

        sb.AppendLine();
        sb.AppendLine("要求:");
        sb.AppendLine("- 分别读取每个目录下的 `AI_ANALYSIS_GUIDE.md`、`ai_profiler_digest.json`、`ai_profiler_summary.json`、`ai_profiler_worstframes.json`，必要时再读取 `ai_profiler_worstframes_samples.json`。");
        sb.AppendLine("- 先分别总结每份导出的性能画像，再做横向对比：共同问题、差异问题、退化/改善点、稳定复现点。");
        sb.AppendLine("- 结合项目代码、Prefab、Shader、URP 配置或资源路径判断原因，但不要修改项目代码。");
        sb.AppendLine("- 报告必须包含：对比结论、每份导出关键证据、共同瓶颈、差异瓶颈、优先级、改后验证指标。");
        sb.AppendLine("- 将完整 Markdown 报告写入 `" + relativeReportPath.Replace("\\", "/") + "`。");
        return sb.ToString();
    }

    private static string BuildClaudeArguments(string arguments, string projectRoot, string model)
    {
        var sb = new StringBuilder(512);
        if (!string.IsNullOrWhiteSpace(arguments))
            sb.Append(arguments.Trim()).Append(' ');
        if (!string.IsNullOrWhiteSpace(model) && !HasCliOption(arguments, "--model"))
            sb.Append("--model ").Append(QuoteForCmd(model.Trim())).Append(' ');
        sb.Append("--add-dir ").Append(QuoteForCmd(projectRoot));
        return sb.ToString();
    }

    private static string BuildInteractiveArguments(string provider, string projectRoot, string model, string prompt, string toolArguments = "")
    {
        provider = NormalizeProvider(provider);
        var sb = new StringBuilder(512);
        if (string.Equals(provider, ProviderClaude, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(QuoteForBatchArg(prompt)).Append(' ');
            sb.Append("--permission-mode bypassPermissions ");
            if (!string.IsNullOrWhiteSpace(model))
                sb.Append("--model ").Append(QuoteForBatchArg(model.Trim())).Append(' ');
            sb.Append("--add-dir ").Append(QuoteForBatchArg(projectRoot));
        }
        else
        {
            string codexArguments = string.IsNullOrWhiteSpace(toolArguments) ? DefaultCodexArguments : toolArguments.Trim();
            if (!string.IsNullOrWhiteSpace(codexArguments))
                sb.Append(codexArguments).Append(' ');
            if (!string.IsNullOrWhiteSpace(model) && !HasCliOption(codexArguments, "--model") && !HasCliOption(codexArguments, "-m"))
                sb.Append("--model ").Append(QuoteForBatchArg(model.Trim())).Append(' ');
            if (!HasCliOption(codexArguments, "--cd") && !HasCliOption(codexArguments, "-C"))
                sb.Append("--cd ").Append(QuoteForBatchArg(projectRoot)).Append(' ');
            sb.Append(QuoteForBatchArg(prompt));
        }
        return sb.ToString().Trim();
    }

    private static bool HasCliOption(string arguments, string option)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return false;
        return arguments.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ResolveToolCommand(string provider, string command)
    {
        provider = NormalizeProvider(provider);
        if (string.Equals(provider, ProviderCodex, StringComparison.OrdinalIgnoreCase))
            return ResolveCodexCommand(command);

        return ResolveClaudeCommand(command);
    }

    private static string ResolveCodexCommand(string command)
    {
        if (!string.IsNullOrWhiteSpace(command) &&
            !string.Equals(command.Trim(), DefaultCodexCommand, StringComparison.OrdinalIgnoreCase))
        {
            return command.Trim();
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] binDirs =
        {
            Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin"),
            Path.Combine(localAppData, "OpenAI", "Codex", "bin")
        };

        foreach (string binDir in binDirs)
        {
            string stableCodex = Path.Combine(binDir, "codex.exe");
            if (File.Exists(stableCodex))
                return stableCodex;
        }

        foreach (string binDir in binDirs)
        {
            if (!Directory.Exists(binDir))
                continue;

            string versioned = Directory.GetFiles(binDir, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(versioned))
                return versioned;
        }

        return DefaultCodexCommand;
    }

    private static string ResolveClaudeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || string.Equals(command.Trim(), DefaultCommand, StringComparison.OrdinalIgnoreCase))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string npmClaudeCmd = Path.Combine(appData, "npm", "claude.cmd");
            string npmClaudeExe = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (File.Exists(npmClaudeExe))
                return npmClaudeExe;

            string npmClaude = Path.Combine(appData, "npm", "claude");
            if (File.Exists(npmClaude))
                return npmClaude;

            if (File.Exists(npmClaudeCmd))
                return npmClaudeCmd;

            return DefaultCommand;
        }

        return command.Trim();
    }

    private static string NormalizeProvider(string provider)
    {
        if (string.Equals(provider, ProviderCodex, StringComparison.OrdinalIgnoreCase))
            return ProviderCodex;
        return ProviderClaude;
    }

    private static string GetDefaultCommand(string provider)
    {
        return string.Equals(NormalizeProvider(provider), ProviderCodex, StringComparison.OrdinalIgnoreCase)
            ? DefaultCodexCommand
            : DefaultCommand;
    }

    private static string GetProviderLabel(string provider)
    {
        return string.Equals(NormalizeProvider(provider), ProviderCodex, StringComparison.OrdinalIgnoreCase)
            ? "Codex"
            : "Claude Code";
    }

    private static bool IsOtherProviderDefaultCommand(string command, string provider)
    {
        if (string.IsNullOrWhiteSpace(command))
            return true;

        string trimmed = command.Trim();
        provider = NormalizeProvider(provider);
        if (string.Equals(provider, ProviderCodex, StringComparison.OrdinalIgnoreCase))
            return string.Equals(trimmed, DefaultCommand, StringComparison.OrdinalIgnoreCase);

        return string.Equals(trimmed, DefaultCodexCommand, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ModelOption> ReadModelOptions(string provider)
    {
        provider = NormalizeProvider(provider);
        if (string.Equals(provider, ProviderCodex, StringComparison.OrdinalIgnoreCase))
            return ReadCodexModelOptions();

        return new List<ModelOption>();
    }

    private static List<ModelOption> ReadCodexModelOptions()
    {
        var options = new List<ModelOption>();
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");
        if (!File.Exists(path))
            return options;

        string json = File.ReadAllText(path, Encoding.UTF8);
        MatchCollection slugMatches = Regex.Matches(json, "\"slug\"\\s*:\\s*\"([^\"]+)\"");
        for (int i = 0; i < slugMatches.Count; i++)
        {
            int start = slugMatches[i].Index;
            int end = i + 1 < slugMatches.Count ? slugMatches[i + 1].Index : json.Length;
            string block = json.Substring(start, end - start);
            if (!Regex.IsMatch(block, "\"visibility\"\\s*:\\s*\"list\"", RegexOptions.IgnoreCase))
                continue;

            string slug = slugMatches[i].Groups[1].Value;
            Match display = Regex.Match(block, "\"display_name\"\\s*:\\s*\"([^\"]+)\"");
            options.Add(new ModelOption
            {
                Value = slug,
                Label = display.Success ? display.Groups[1].Value : slug
            });
        }

        return options;
    }

    private static string NormalizeArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return DefaultArguments;

        arguments = arguments.Trim();
        if (string.Equals(arguments, LegacyPrintArguments, StringComparison.OrdinalIgnoreCase))
            return DefaultArguments;
        if (string.Equals(arguments, LegacyInteractiveArguments, StringComparison.OrdinalIgnoreCase))
            return DefaultArguments;
        if (string.Equals(arguments, LegacyStreamArguments, StringComparison.OrdinalIgnoreCase))
            return DefaultArguments;
        if (string.Equals(arguments, LegacyRestrictedArguments, StringComparison.OrdinalIgnoreCase))
            return DefaultArguments;
        if (string.Equals(arguments, LegacyToolRestrictedArguments, StringComparison.OrdinalIgnoreCase))
            return DefaultArguments;

        return arguments;
    }

    private static string ResolveProjectPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        if (Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int ParseJsonInt(string json, string name, int fallback)
    {
        string value = ExtractJsonNumber(json, name);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            return result;
        return fallback;
    }

    private static string ExtractJsonString(string json, string name)
    {
        string needle = "\"" + name + "\"";
        int start = json.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        start += needle.Length;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != ':')
            return string.Empty;
        start++;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != '"')
            return string.Empty;
        start++;

        var sb = new StringBuilder();
        bool escaping = false;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (escaping)
            {
                switch (c)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
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
                break;

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string ExtractJsonNumber(string json, string name)
    {
        string needle = "\"" + name + "\"";
        int start = json.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        start += needle.Length;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != ':')
            return string.Empty;
        start++;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;

        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
            end++;
        return end > start ? json.Substring(start, end - start) : string.Empty;
    }

    private static string QuoteForCmd(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private static string QuoteForBatchArg(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private static string QuoteExecutableForBatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string trimmed = value.Trim();
        if (trimmed.IndexOfAny(new[] { '\\', '/', ' ' }) >= 0 || Path.IsPathRooted(trimmed))
            return QuoteForBatchArg(trimmed);

        return trimmed;
    }

    private static string PrepareLinkedDeepEventPreflightScript(string projectRoot, string frameDebugExportDir, string renderDocAnalysisDir, string linkedCapturePath)
    {
        try
        {
            string linkedJson = File.Exists(linkedCapturePath) ? File.ReadAllText(linkedCapturePath, Encoding.UTF8) : "";
            string rdcPath = ExtractJsonString(linkedJson, "renderDocCapturePath");
            string toolPath = Path.Combine(projectRoot, "Tools", "RenderDocAIAnalyzer_Portable", "DeepEvent.exe");
            string planPath = Path.Combine(frameDebugExportDir, "ai_deep_event_sampling_plan.json");
            string eventsPath = Path.Combine(frameDebugExportDir, "deep_event_preflight_events.txt");
            string statusPath = Path.Combine(frameDebugExportDir, "deep_event_preflight_status.json");
            string scriptPath = Path.Combine(frameDebugExportDir, "run_deep_event_preflight.cmd");
            string outDir = renderDocAnalysisDir;

            var sb = new StringBuilder(4096);
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("set \"TOOL=" + toolPath + "\"");
            sb.AppendLine("set \"RDC=" + rdcPath + "\"");
            sb.AppendLine("set \"OUT=" + outDir + "\"");
            sb.AppendLine("set \"PLAN=" + planPath + "\"");
            sb.AppendLine("set \"EVENTS=" + eventsPath + "\"");
            sb.AppendLine("set \"STATUS=" + statusPath + "\"");
            sb.AppendLine("set \"MAX_EVENTS=16\"");
            sb.AppendLine("echo DeepEvent preflight");
            sb.AppendLine("if not exist \"%TOOL%\" (");
            sb.AppendLine("  powershell -NoProfile -Command \"$o=[pscustomobject]@{status='skipped'; reason='DeepEvent.exe missing'; tool=$env:TOOL; generatedAtUtc=(Get-Date).ToUniversalTime().ToString('o')}; $o | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $env:STATUS\"");
            sb.AppendLine("  echo DeepEvent.exe missing: %TOOL%");
            sb.AppendLine("  exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine("if not exist \"%RDC%\" (");
            sb.AppendLine("  powershell -NoProfile -Command \"$o=[pscustomobject]@{status='skipped'; reason='RDC missing'; rdc=$env:RDC; generatedAtUtc=(Get-Date).ToUniversalTime().ToString('o')}; $o | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $env:STATUS\"");
            sb.AppendLine("  echo RDC missing: %RDC%");
            sb.AppendLine("  exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine("if not exist \"%PLAN%\" (");
            sb.AppendLine("  powershell -NoProfile -Command \"$o=[pscustomobject]@{status='skipped'; reason='sampling plan missing'; plan=$env:PLAN; generatedAtUtc=(Get-Date).ToUniversalTime().ToString('o')}; $o | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $env:STATUS\"");
            sb.AppendLine("  echo sampling plan missing: %PLAN%");
            sb.AppendLine("  exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; $p=Get-Content -Raw $env:PLAN | ConvertFrom-Json; $ids=@($p.eventIdOrResourceIdQueue | Where-Object { $_ -match '^\\d+$' } | Select-Object -First ([int]$env:MAX_EVENTS)); if($ids.Count -eq 0){ throw 'No numeric event ids in sampling plan.' }; $ids | Set-Content -Encoding ASCII $env:EVENTS\"");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  powershell -NoProfile -Command \"$o=[pscustomobject]@{status='failed'; reason='cannot parse sampling plan'; plan=$env:PLAN; generatedAtUtc=(Get-Date).ToUniversalTime().ToString('o')}; $o | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $env:STATUS\"");
            sb.AppendLine("  exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine("if not exist \"%OUT%\" mkdir \"%OUT%\"");
            sb.AppendLine("for /f \"usebackq delims=\" %%E in (\"%EVENTS%\") do (");
            sb.AppendLine("  echo DeepEvent %%E");
            sb.AppendLine("  \"%TOOL%\" -Rdc \"%RDC%\" -EventId %%E -OutDir \"%OUT%\" --no-pause");
            sb.AppendLine("  if errorlevel 1 echo DeepEvent %%E failed with %ERRORLEVEL%");
            sb.AppendLine(")");
            sb.AppendLine("powershell -NoProfile -Command \"$events=@(); if(Test-Path $env:EVENTS){$events=@(Get-Content -LiteralPath $env:EVENTS | ForEach-Object { [string]$_ })}; $files=@(); if(Test-Path $env:OUT){$files=@(Get-ChildItem -LiteralPath $env:OUT -Filter 'deep_event_*.json' -File | Select-Object -ExpandProperty Name | ForEach-Object { [string]$_ })}; $o=[pscustomobject]@{status='completed'; requestedEvents=$events; outputFiles=$files; outDir=$env:OUT; generatedAtUtc=(Get-Date).ToUniversalTime().ToString('o')}; $o | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $env:STATUS\"");
            sb.AppendLine("exit /b 0");
            File.WriteAllText(scriptPath, sb.ToString(), Utf8NoBom);
            return scriptPath;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("DeepEvent preflight script generation failed: " + ex.Message);
            return "";
        }
    }

    private static string BuildPreAnalysisBatchCall(string preAnalysisScriptPath)
    {
        if (string.IsNullOrWhiteSpace(preAnalysisScriptPath))
            return string.Empty;

        return
            "if exist " + QuoteForBatchArg(preAnalysisScriptPath) + " (\r\n" +
            "  echo Running pre-analysis data collection...\r\n" +
            "  call " + QuoteForBatchArg(preAnalysisScriptPath) + "\r\n" +
            "  echo.\r\n" +
            ")\r\n";
    }

    private static void WriteStatus(
        string path,
        string status,
        string command,
        string arguments,
        string model,
        string shellCommand,
        string promptPath,
        string reportPath,
        string logPath,
        string streamPath,
        int processId,
        int exitCode,
        string error)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("{");
        WriteJsonProperty(sb, "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
        WriteJsonProperty(sb, "status", status, true);
        WriteJsonProperty(sb, "command", command, true);
        WriteJsonProperty(sb, "arguments", arguments, true);
        WriteJsonProperty(sb, "model", string.IsNullOrEmpty(model) ? "default" : model, true);
        WriteJsonProperty(sb, "shellCommand", shellCommand, true);
        WriteJsonProperty(sb, "promptPath", ToProjectRelativePath(promptPath), true);
        WriteJsonProperty(sb, "reportPath", ToProjectRelativePath(reportPath), true);
        WriteJsonProperty(sb, "logPath", ToProjectRelativePath(logPath), true);
        WriteJsonProperty(sb, "streamPath", ToProjectRelativePath(streamPath), true);
        WriteJsonProperty(sb, "processId", processId, true);
        WriteJsonProperty(sb, "exitCode", exitCode, !string.IsNullOrEmpty(error));
        if (!string.IsNullOrEmpty(error))
            WriteJsonProperty(sb, "error", error, false);
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, string value, bool trailingComma)
    {
        sb.Append("  \"").Append(EscapeJson(name)).Append("\": \"").Append(EscapeJson(value)).Append('"');
        if (trailingComma)
            sb.Append(',');
        sb.AppendLine();
    }

    private static void WriteJsonProperty(StringBuilder sb, string name, int value, bool trailingComma)
    {
        sb.Append("  \"").Append(EscapeJson(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (trailingComma)
            sb.Append(',');
        sb.AppendLine();
    }

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string ToProjectRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        string root = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(root.Length + 1).Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }
}

public sealed class ProfilerAICompareExportsWindow : EditorWindow
{
    private readonly List<string> exportDirs = new List<string>();
    private readonly List<bool> selected = new List<bool>();
    private Vector2 scroll;

    public static void Open()
    {
        var window = GetWindow<ProfilerAICompareExportsWindow>("Profiler AI 对比");
        window.minSize = new Vector2(620, 320);
        window.Show();
        window.RefreshExports();
    }

    private void OnEnable()
    {
        RefreshExports();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("对比多个 AI 导出", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("选择两个或更多 ProfilerAIExports 导出目录，会生成一个 Compare_时间戳目录并调用当前设置中的 AI 工具输出对比报告。", MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("刷新列表"))
            RefreshExports();
        if (GUILayout.Button("添加目录..."))
            AddFolder();
        if (GUILayout.Button("全不选"))
            SetAll(false);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < exportDirs.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            selected[i] = EditorGUILayout.Toggle(selected[i], GUILayout.Width(20));
            EditorGUILayout.LabelField(BuildExportLabel(exportDirs[i]));
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        int count = selected.Count(value => value);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("已选择", count.ToString(CultureInfo.InvariantCulture));
        GUI.enabled = count >= 2;
        if (GUILayout.Button("开始对比分析"))
        {
            ProfilerAIClaudeRunner.StartCompareAnalysis(exportDirs.Where((dir, index) => selected[index]).ToArray());
            Close();
        }
        GUI.enabled = true;
    }

    private void RefreshExports()
    {
        exportDirs.Clear();
        selected.Clear();

        string root = Path.Combine(Directory.GetCurrentDirectory(), "ProfilerAIExports");
        if (Directory.Exists(root))
        {
            foreach (string dir in Directory.GetDirectories(root)
                         .Where(IsCompleteExport)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                exportDirs.Add(dir);
                selected.Add(selected.Count < 2);
            }
        }

        Repaint();
    }

    private void AddFolder()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "ProfilerAIExports");
        string dir = EditorUtility.OpenFolderPanel("添加 Profiler AI 导出目录", Directory.Exists(root) ? root : Directory.GetCurrentDirectory(), string.Empty);
        if (string.IsNullOrEmpty(dir))
            return;

        dir = Path.GetFullPath(dir);
        if (!IsCompleteExport(dir))
        {
            EditorUtility.DisplayDialog("Profiler AI", "该目录不是完整 AI 导出：\n" + dir, "确定");
            return;
        }

        if (exportDirs.Any(existing => string.Equals(Path.GetFullPath(existing), dir, StringComparison.OrdinalIgnoreCase)))
            return;

        exportDirs.Add(dir);
        selected.Add(true);
    }

    private void SetAll(bool value)
    {
        for (int i = 0; i < selected.Count; i++)
            selected[i] = value;
    }

    private static bool IsCompleteExport(string dir)
    {
        return Directory.Exists(dir) &&
               File.Exists(Path.Combine(dir, "AI_ANALYSIS_GUIDE.md")) &&
               File.Exists(Path.Combine(dir, "ai_profiler_digest.json"));
    }

    private static string BuildExportLabel(string dir)
    {
        return ToProjectRelativePath(dir) + "    " + Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string ToProjectRelativePath(string path)
    {
        string root = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(root.Length + 1).Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }
}

public sealed class ProfilerAIClaudeSettingsWindow : EditorWindow
{
    private static readonly string[] ProviderButtonLabels = { "Claude Code", "Codex" };
    private static readonly string[] ProviderButtonValues = { "claude", "codex" };
    private string provider;
    private string command;
    private string arguments;
    private string model;
    private bool visibleTerminal;

    public static void Open()
    {
        var window = GetWindow<ProfilerAIClaudeSettingsWindow>("AI 分析工具设置");
        window.minSize = new Vector2(540, 150);
        window.Show();
    }

    private void OnEnable()
    {
        provider = ProfilerAIClaudeRunner.GetProvider();
        command = ProfilerAIClaudeRunner.GetCommand();
        arguments = ProfilerAIClaudeRunner.GetArguments();
        model = ProfilerAIClaudeRunner.GetModel();
        visibleTerminal = ProfilerAIClaudeRunner.GetVisibleTerminal();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("AI 分析工具设置", EditorStyles.boldLabel);
        int providerIndex = Array.IndexOf(ProviderButtonValues, provider);
        if (providerIndex < 0)
            providerIndex = 0;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("调用工具", GUILayout.Width(64));
        int nextProviderIndex = GUILayout.Toolbar(providerIndex, ProviderButtonLabels);
        EditorGUILayout.EndHorizontal();
        if (nextProviderIndex != providerIndex)
        {
            provider = ProviderButtonValues[nextProviderIndex];
            command = provider == "codex" ? "codex" : "claude";
            model = string.Empty;
            if (provider == "codex")
                arguments = "--dangerously-bypass-approvals-and-sandbox";
        }

        command = EditorGUILayout.TextField("命令", command);
        arguments = EditorGUILayout.TextField(provider == "codex" ? "启动参数" : "流式参数", arguments);
        DrawModelDropdown();
        visibleTerminal = EditorGUILayout.Toggle("弹出工具终端", visibleTerminal);
        EditorGUILayout.HelpBox(visibleTerminal
            ? (provider == "codex"
                ? "当前使用 Codex 可见终端模式：默认参数会跳过审批与沙盒。需要更保守时，把启动参数改为 --ask-for-approval on-request --sandbox workspace-write。"
                : "当前使用可见终端模式：Unity 会弹出所选工具的命令行窗口并自动提交分析任务；面板只监控报告和状态。")
            : "当前使用 Unity 面板流式模式：目前只支持 Claude Code；Codex 请勾选弹出终端。", MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("保存"))
        {
            ProfilerAIClaudeRunner.SaveSettings(provider, command, arguments, model, visibleTerminal);
            Close();
        }

        if (GUILayout.Button("恢复默认"))
        {
            ProfilerAIClaudeRunner.ResetSettings();
            provider = ProfilerAIClaudeRunner.GetProvider();
            command = ProfilerAIClaudeRunner.GetCommand();
            arguments = ProfilerAIClaudeRunner.GetArguments();
            model = ProfilerAIClaudeRunner.GetModel();
            visibleTerminal = ProfilerAIClaudeRunner.GetVisibleTerminal();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawModelDropdown()
    {
        ProfilerAIClaudeRunner.GetModelOptions(provider, model, out string[] labels, out string[] values);
        int selected = Array.IndexOf(values, model ?? string.Empty);
        if (selected < 0)
        {
            selected = 0;
            model = string.Empty;
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("模型", GUILayout.Width(64));
        int next = EditorGUILayout.Popup(selected, labels);
        if (next != selected && next >= 0 && next < values.Length)
            model = values[next];
        EditorGUILayout.EndHorizontal();

        if (values.Length <= 1)
            EditorGUILayout.HelpBox(provider == "codex"
                ? "没有从 Codex 本机模型缓存读取到可选模型，将使用默认模型。"
                : "Claude Code 没有提供可查询的模型列表接口，这里只使用默认模型；需要临时切换可在弹出的 Claude Code 窗口里使用 /model。", MessageType.Info);
    }
}

public sealed class ProfilerAIClaudeStatusWindow : EditorWindow
{
    private string statusPath;
    private double nextRepaintTime;
    private Vector2 thinkingScroll;

    public static void Open(string path)
    {
        var window = GetWindow<ProfilerAIClaudeStatusWindow>("AI 分析");
        window.minSize = new Vector2(560, 240);
        window.statusPath = path;
        window.Show();
        window.Repaint();
    }

    private void OnEnable()
    {
        EditorApplication.update += Tick;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Tick;
    }

    private void Tick()
    {
        if (EditorApplication.timeSinceStartup < nextRepaintTime)
            return;

        nextRepaintTime = EditorApplication.timeSinceStartup + 1.0;
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("AI 性能分析", EditorStyles.boldLabel);

        if (string.IsNullOrEmpty(statusPath) || !File.Exists(statusPath))
        {
            EditorGUILayout.HelpBox("还没有状态文件。请先从 Tools/AI 分析 启动分析。", MessageType.Info);
            return;
        }

        string statusText = File.ReadAllText(statusPath, Encoding.UTF8);
        string status = ExtractJsonString(statusText, "status");
        string statusModel = ExtractJsonString(statusText, "model");
        string reportPath = ResolveProjectPath(ExtractJsonString(statusText, "reportPath"));
        string logPath = ResolveProjectPath(ExtractJsonString(statusText, "logPath"));
        string streamPath = ResolveProjectPath(ExtractJsonString(statusText, "streamPath"));
        string promptPath = ResolveProjectPath(ExtractJsonString(statusText, "promptPath"));
        string processId = ExtractJsonNumber(statusText, "processId");
        string exitCode = ExtractJsonNumber(statusText, "exitCode");
        string error = ExtractJsonString(statusText, "error");
        double staleSeconds = GetStreamStaleSeconds(streamPath);
        bool running = IsRunningStatus(status);
        bool terminalMode = IsTerminalStatus(status);

        EditorGUILayout.LabelField("状态", FormatStatus(status));
        DrawModelSelector();
        EditorGUILayout.LabelField("本次模型", FormatModel(statusModel));
        if (!terminalMode)
            EditorGUILayout.LabelField("用量", BuildUsageSummary(streamPath));
        EditorGUILayout.LabelField("进程", processId + "  退出码 " + exitCode);
        if (running && !terminalMode)
            EditorGUILayout.LabelField("输出间隔", FormatDuration(staleSeconds));
        EditorGUILayout.LabelField("报告", ToProjectRelativePath(reportPath));
        EditorGUILayout.LabelField("日志", ToProjectRelativePath(logPath) + "  " + GetFileSizeText(logPath));
        if (!terminalMode)
            EditorGUILayout.LabelField("流文件", ToProjectRelativePath(streamPath) + "  " + GetFileSizeText(streamPath));
        if (!string.IsNullOrEmpty(error))
            EditorGUILayout.HelpBox(error, MessageType.Warning);
        if (terminalMode)
            EditorGUILayout.HelpBox("当前是可见终端模式：分析过程显示在弹出的命令行窗口中，Unity 面板只监控报告文件。", MessageType.Info);
        if (running && !terminalMode && staleSeconds >= ProfilerAIClaudeRunner.StaleOutputTimeoutSeconds)
            EditorGUILayout.HelpBox("流式输出已经超过 5 分钟没有更新，可能是 AI 工具调用卡住。不会自动停止；需要时手动点“停止分析”。", MessageType.Warning);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("打开导出目录"))
            EditorUtility.RevealInFinder(Path.GetDirectoryName(statusPath));
        if (GUILayout.Button("打开报告"))
            RevealIfExists(reportPath);
        if (GUILayout.Button("打开日志"))
            RevealIfExists(logPath);
        if (GUILayout.Button("打开任务文件"))
            RevealIfExists(promptPath);
        if (GUILayout.Button("打开状态文件"))
            RevealIfExists(statusPath);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUI.enabled = running;
        if (GUILayout.Button("停止分析"))
        {
            ProfilerAIClaudeRunner.StopAnalysisFromStatus(statusPath, "用户在 AI 分析面板停止分析。");
            Repaint();
        }
        GUI.enabled = !running;
        if (GUILayout.Button("重新分析本目录"))
        {
            string exportDir = Path.GetDirectoryName(statusPath);
            if (!string.IsNullOrEmpty(exportDir))
                ProfilerAIClaudeRunner.StartAnalysisAuto(exportDir);
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        if (!terminalMode)
        {
            EditorGUILayout.LabelField("思考过程", EditorStyles.boldLabel);
            DrawThinkingTail(streamPath);
        }
    }

    private void DrawThinkingTail(string streamPath)
    {
        string text = BuildReadableThinkingTail(streamPath, 16000);
        var style = EditorStyles.textArea;
        float width = Mathf.Max(100.0f, position.width - 24.0f);
        float contentHeight = Mathf.Max(180.0f, style.CalcHeight(new GUIContent(text), width));

        thinkingScroll.y = float.MaxValue;
        thinkingScroll = EditorGUILayout.BeginScrollView(thinkingScroll, GUILayout.MinHeight(180), GUILayout.ExpandHeight(true));
        EditorGUILayout.TextArea(text, style, GUILayout.ExpandWidth(true), GUILayout.Height(contentHeight));
        EditorGUILayout.EndScrollView();
    }

    private static void DrawModelSelector()
    {
        string current = ProfilerAIClaudeRunner.GetModel();
        ProfilerAIClaudeRunner.GetModelOptions(ProfilerAIClaudeRunner.GetProvider(), current, out string[] labels, out string[] values);
        int selected = Array.IndexOf(values, current ?? string.Empty);
        if (selected < 0)
        {
            selected = 0;
            ProfilerAIClaudeRunner.SaveModel(string.Empty);
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("下次模型", GUILayout.Width(80));
        int next = EditorGUILayout.Popup(selected, labels);
        if (next != selected)
            ProfilerAIClaudeRunner.SaveModel(values[next]);

        EditorGUILayout.EndHorizontal();
    }

    private static void RevealIfExists(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            EditorUtility.RevealInFinder(path);
        else if (!string.IsNullOrEmpty(path))
            EditorUtility.DisplayDialog("AI 分析", "文件暂不存在：\n" + path, "确定");
    }

    private static string FormatStatus(string status)
    {
        switch (status)
        {
            case "starting_stream_analysis":
            case "starting_terminal_analysis":
                return "启动中";
            case "running_stream_analysis":
            case "running_terminal_analysis":
                return "分析中";
            case "completed":
                return "已完成";
            case "completed_report_detected":
                return "报告已生成";
            case "failed":
                return "失败";
            case "start_failed":
                return "启动失败";
            case "stalled":
                return "已停滞";
            case "stopped":
                return "已停止";
            case "":
            case null:
                return "未知";
            default:
                return status;
        }
    }

    private static string FormatModel(string model)
    {
        if (string.IsNullOrEmpty(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase))
            return "默认";
        return model;
    }

    private static bool IsRunningStatus(string status)
    {
        return string.Equals(status, "starting_stream_analysis", StringComparison.Ordinal) ||
               string.Equals(status, "running_stream_analysis", StringComparison.Ordinal) ||
               string.Equals(status, "starting_terminal_analysis", StringComparison.Ordinal) ||
               string.Equals(status, "running_terminal_analysis", StringComparison.Ordinal);
    }

    private static bool IsTerminalStatus(string status)
    {
        return string.Equals(status, "starting_terminal_analysis", StringComparison.Ordinal) ||
               string.Equals(status, "running_terminal_analysis", StringComparison.Ordinal);
    }

    private static double GetStreamStaleSeconds(string streamPath)
    {
        if (string.IsNullOrEmpty(streamPath) || !File.Exists(streamPath))
            return 0.0;
        return Math.Max(0.0, (DateTime.Now - File.GetLastWriteTime(streamPath)).TotalSeconds);
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60.0)
            return seconds.ToString("0", CultureInfo.InvariantCulture) + " 秒";
        return (seconds / 60.0).ToString("0.0", CultureInfo.InvariantCulture) + " 分钟";
    }

    private static string GetFileSizeText(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return "（未生成）";

        long bytes = new FileInfo(path).Length;
        if (bytes < 1024)
            return "（" + bytes.ToString(CultureInfo.InvariantCulture) + " B）";
        return "（" + (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB）";
    }

    private static string ReadFileTail(string path, int maxChars)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return string.Empty;

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length <= maxChars)
                return text;
            return text.Substring(text.Length - maxChars);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string BuildUsageSummary(string streamPath)
    {
        string text = ReadFileTail(streamPath, 200000);
        if (string.IsNullOrEmpty(text))
            return "等待流式输出";

        int input = ExtractLastInt(text, "input_tokens");
        int output = ExtractLastInt(text, "output_tokens");
        int cacheCreate = ExtractLastInt(text, "cache_creation_input_tokens");
        int cacheRead = ExtractLastInt(text, "cache_read_input_tokens");
        int remaining = ExtractLastInt(text, "remaining_tokens");
        if (remaining < 0)
            remaining = ExtractLastInt(text, "tokens_remaining");
        double cost = ExtractLastDouble(text, "total_cost_usd");
        if (cost < 0)
            cost = ExtractLastDouble(text, "cost_usd");

        bool hasUsage = input >= 0 || output >= 0 || cacheCreate >= 0 || cacheRead >= 0 || remaining >= 0 || cost >= 0;
        if (!hasUsage)
            return "暂未收到用量";

        var sb = new StringBuilder();
        if (input >= 0) sb.Append("输入 ").Append(input.ToString(CultureInfo.InvariantCulture)).Append("  ");
        if (output >= 0) sb.Append("输出 ").Append(output.ToString(CultureInfo.InvariantCulture)).Append("  ");
        if (cacheCreate >= 0) sb.Append("缓存写入 ").Append(cacheCreate.ToString(CultureInfo.InvariantCulture)).Append("  ");
        if (cacheRead >= 0) sb.Append("缓存读取 ").Append(cacheRead.ToString(CultureInfo.InvariantCulture)).Append("  ");
        if (remaining >= 0) sb.Append("剩余 ").Append(remaining.ToString(CultureInfo.InvariantCulture)).Append("  ");
        if (cost >= 0) sb.Append("费用 $").Append(cost.ToString("0.0000", CultureInfo.InvariantCulture));
        return sb.ToString().Trim();
    }

    private static string BuildReadableThinkingTail(string streamPath, int maxChars)
    {
        string raw = ReadFileTail(streamPath, 300000);
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(maxChars);
        ExtractReadableDeltas(raw, "\"thinking_delta\"", "\"thinking\"", sb, maxChars);
        if (sb.Length == 0)
            return "等待思考内容...";

        if (sb.Length > maxChars)
            return sb.ToString(sb.Length - maxChars, maxChars);
        return sb.ToString();
    }

    private static void ExtractReadableDeltas(string raw, string typeNeedle, string valueName, StringBuilder output, int maxChars)
    {
        int index = 0;
        while (index < raw.Length)
        {
            int typeIndex = raw.IndexOf(typeNeedle, index, StringComparison.Ordinal);
            if (typeIndex < 0)
                break;

            int valueIndex = raw.IndexOf(valueName, typeIndex, StringComparison.Ordinal);
            if (valueIndex < 0)
            {
                index = typeIndex + typeNeedle.Length;
                continue;
            }

            string value = ExtractJsonStringFrom(raw, valueIndex + valueName.Length);
            if (!string.IsNullOrEmpty(value))
            {
                output.Append(value);
                if (output.Length > maxChars * 2)
                    output.Remove(0, output.Length - maxChars);
            }

            index = valueIndex + valueName.Length;
        }
    }

    private static string ExtractJsonStringFrom(string json, int start)
    {
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != ':')
            return string.Empty;
        start++;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != '"')
            return string.Empty;
        start++;

        var sb = new StringBuilder();
        bool escaping = false;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (escaping)
            {
                switch (c)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
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
                break;

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int ExtractLastInt(string text, string name)
    {
        string value = ExtractLastJsonNumberText(text, name);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            return result;
        return -1;
    }

    private static double ExtractLastDouble(string text, string name)
    {
        string value = ExtractLastJsonNumberText(text, name);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return result;
        return -1;
    }

    private static string ExtractLastJsonNumberText(string text, string name)
    {
        string needle = "\"" + name + "\"";
        int start = text.LastIndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        start += needle.Length;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        if (start >= text.Length || text[start] != ':')
            return string.Empty;
        start++;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        int end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-' || text[end] == '+' || text[end] == '.'))
            end++;
        return end > start ? text.Substring(start, end - start) : string.Empty;
    }

    private static string ResolveProjectPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        if (Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ToProjectRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        string root = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(root.Length + 1).Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }

    private static string ExtractJsonString(string json, string name)
    {
        string needle = "\"" + name + "\"";
        int start = json.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        start += needle.Length;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != ':')
            return string.Empty;
        start++;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != '"')
            return string.Empty;
        start++;

        var sb = new StringBuilder();
        bool escaping = false;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (escaping)
            {
                switch (c)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
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
                break;

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string ExtractJsonNumber(string json, string name)
    {
        string needle = "\"" + name + "\"";
        int start = json.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0)
            return "?";

        start += needle.Length;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;
        if (start >= json.Length || json[start] != ':')
            return "?";
        start++;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
            start++;

        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
            end++;

        return end > start ? json.Substring(start, end - start) : "?";
    }
}
