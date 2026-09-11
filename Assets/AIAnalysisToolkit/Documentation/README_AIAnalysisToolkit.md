# AI Analysis Toolkit

This folder is the standalone Unity AI analysis toolkit root.

## Contents

- `Editor/ProfilerAI`
  - Profiler capture export.
  - `.data` to AI evidence export.
  - Raw frame request processing.
  - AI command settings, status panel, and analysis launcher.
- `Editor/FrameDebuggerExport`
  - Unity Frame Debugger export.
  - RenderDoc linked capture export.
  - Linked render-analysis prompt and evidence generation.
- `Editor/FxAI`
  - Selected effect capture package export for AI analysis.
- `Runtime/Diagnostics`
  - Optional development-build runtime snapshot endpoint used by linked Frame Debugger analysis.

## Unity Menus

- `Tools/AI 分析/Profiler/...`
- `Tools/AI 分析/Frame Debugger/...`
- `Tools/AI 分析/FX/...`
- `Tools/AI 分析/AI 工具设置...`
- `Tools/AI 分析/打开分析面板`
- Asset or GameObject context menu: `AI特效分析/录制特效分析包`

## Scope

This toolkit intentionally does not include the normal FX editor tools, atlas tools, shaders, art conversion tools, prefabs, project assets, or generated export folders.

The code is organized so the whole `Assets/AIAnalysisToolkit` folder can be copied or exported as a Unity package.

## Dependencies

- Unity Editor APIs, including Profiler and Frame Debugger internals.
- `Newtonsoft.Json`.
- The configured external AI command, normally Codex or Claude, depending on the settings panel.
- RenderDoc is optional and only required for linked RenderDoc capture analysis.

For projects with assembly definitions, make sure the editor assembly that contains this toolkit can reference the project's required editor/runtime assemblies.
