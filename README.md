# ParticleEffectForUGUI-HP

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
![Unity](https://img.shields.io/badge/Unity-2022.3.49f1-57b9d3.svg)
![URP](https://img.shields.io/badge/URP-14.0.11-57b9d3.svg)
![Platform](https://img.shields.io/badge/platform-Android%20%7C%20iOS-lightgrey.svg)

> 一个面向**移动端大量 UI 粒子特效**的性能定制 Fork，基于 [mob-sakai/ParticleEffectForUGUI](https://github.com/mob-sakai/ParticleEffectForUGUI)（UI Particle，MIT 许可）。
> 仓库同时包含完整的 **UI 粒子 vs RenderTexture 对比评测工程**与离线回归工具。

**English TL;DR** — This is a performance-oriented fork of `ParticleEffectForUGUI` (upstream v4.14.0, MIT) plus a reproducible Unity 2022.3 + URP 14 benchmark project that compares UI-particle rendering against the classic Camera + RenderTexture approach. See [Custom optimizations](#定制优化特性) and [Benchmark scenes](#测试与评测场景).

---

## 目录

- [项目定位](#项目定位)
- [定制优化特性](#定制优化特性)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [工程目录结构](#工程目录结构)
- [测试与评测场景](#测试与评测场景)
- [离线回归与诊断工具](#离线回归与诊断工具)
- [性能开关速查](#性能开关速查)
- [文档](#文档)
- [开发说明](#开发说明)
- [已知限制](#已知限制)
- [致谢与第三方](#致谢与第三方)
- [许可证](#许可证)

---

## 项目定位

上游 `ParticleEffectForUGUI` 是一个非常成熟的 **Shuriken → UGUI 适配层**：它保留 Unity ParticleSystem 的编辑器与模拟能力，把粒子通过 `CanvasRenderer` 绘制，并正确参与 UGUI 的 sibling 顺序、`Mask` / `RectMask2D`、`CanvasGroup`，无需额外 Camera 或 RenderTexture。

但它并非为「大量不同粒子特效同时出现」而设计。其主要 CPU 成本随 **ParticleSystem 数量**增长，而不是只随粒子数量增长；一个复杂 UI 特效往往包含 5~10 个子 ParticleSystem，于是会产生大量 `Graphic` / `CanvasRenderer` / `BakeMesh` 调用 / 材质状态提交。

本 Fork 的目标是：

1. **保留** `BakeMesh → CanvasRenderer` 的核心思路与全部功能语义；
2. **减少** `BakeMesh` 调用次数与每次烘焙后的 Mesh 处理；
3. **裁剪**通用兼容逻辑，把热路径做成受控的高性能版本；
4. 提供**可复现的对比评测场景**与开关，用数据而非直觉来验证收益与代价。

> 所有优化开关默认关闭，保持与原版一致的行为；需要时逐项开启并做 A/B 对比。

---

## 定制优化特性

所有开关集中在 `UIParticle` 的静态属性上，运行时即可切换（详见 [`Packages/src/Runtime/UIParticle.cs`](./Packages/src/Runtime/UIParticle.cs)）。

| 开关 | 类型 | 作用 | 代价 / 生效条件 |
| --- | --- | --- | --- |
| `UIParticle.mergeRenderers` | `bool` | 把同一个 `UIParticle` 下全部非 Trail 的 ParticleSystem 合并到**一个** `UIParticleRenderer` 中烘焙与提交，显著降低 Renderer / 材质提交 / 合批节点数量 | 需粒子系统「可合并」（材质、贴图、Trail 材质、顶点流一致）；含 `AnimatableProperties`（CanvasAnimator）的效果自动回退原版路径；用户自定义 `MaterialPropertyBlock` 会阻止合并 |
| `UIParticle.useGroupCache` | `bool` | MeshSharing 组成员查找改为**字典缓存**，替代原版的全局扫描 | 缓存需要随组成员增删失效；仅共享开启时才有收益 |
| `UIParticle.bakeFPS` | `int` | 全局粒子网格**烘焙降频**（Hz）。`0` = 每帧烘焙；`30` = 高帧率下上限约 30Hz，低帧率隔帧烘焙，各 Renderer 错峰调度 | **会改变更新频率，不是等画质优化**；比较时须记录该值 |
| `UIParticle.earlyCull` | `int` | `0` 关闭；`1` RenderCull：UGUI 已判裁剪/出屏时仅停止 Bake/Combine/SetMesh，模拟继续以保持时间连续；`2` = RenderCull + FullCull：整页累积 alpha≈0 时连模拟一起停，恢复可见后从停点继续 | 依赖 UGUI 的 `Cull(clipRect, validRect)` 回调；MeshSharing 按整组可见性裁剪 |
| `UIParticle.staticMeshCache` | `bool` | 全部系统暂停且相关 Transform / Canvas 状态未变时，整帧跳过 Simulate/Bake/Combine/SetMesh | 运行时修改绑定状态需调用 `MarkParticleDirty()` / `InvalidateMeshCache()` 显式失效 |
| `UIParticle.fastBindingMode` | `bool` | 把运行时绑定状态的「自动侦测」改为「调用方显式通知」，跳过每帧对材质 / 贴图 / trail / renderer 的复核 | 开启后修改 `sharedMaterial`、`trailMaterial`、`mainTexture`、`trails.enabled`、TSA sprite 等须调用 `MarkBindingDirty()`；仅当项目承诺运行期基本不改这些时开启 |

此外还包含：

- `ParticleBakeClock`：把烘焙频率从「每帧」解耦为「按 Hz + 错峰相位」，配合 `bakeFPS` 使用；
- `UIParticleProfiler`：`BeginDetailedTiming()` 引用计数的常驻 Profiler 计时（默认只留计数、不取 Stopwatch）；
- 合并 Mesh 的**按需分配**与失去模拟权时的释放；
- 共享组的**局部失效**与可见性聚合、两相错峰调度（0.25 / 0.75）。

> 更完整的优化路线、已完成项与后续计划见 [`Docs/FurtherOptimizationPlan_20260911.md`](./Docs/FurtherOptimizationPlan_20260911.md)。

---

## 环境要求

| 项目 | 版本 |
| --- | --- |
| Unity | **2022.3.49f1**（`m_EditorVersionWithRevision: 4dae1bb8668d`） |
| 渲染管线 | **Universal RP 14.0.11**（内嵌于 `Packages/com.unity.render-pipelines.universal@14.0.11`） |
| UI Particle 包 | **com.coffee.ui-particle 4.14.0**（Fork 版，内嵌于 `Packages/src`） |
| 目标平台 | Android / iOS（兼容 Editor 与桌面平台） |
| 其他依赖 | `com.unity.test-framework` 1.1.33、`com.unity.nuget.newtonsoft-json` 3.2.2、`com.unity.ai.navigation` 1.1.5、Rider 3.0.36 |

> `Packages/manifest.json` 中的 `com.coffee.development` / `com.coffee.minimal-resource` / `com.coffee.nano-monitor` 通过 Git URL 从 `mob-sakai/Coffee.Internal` 拉取，首次打开工程需要联网。

---

## 快速开始

1. 安装 **Unity 2022.3.49f1**（Unity Hub）。
2. 克隆仓库并用 Unity Hub 打开工程根目录：

   ```bash
   git clone https://github.com/shanflyer/ParticleEffectForUGUI-HP.git
   ```

   首次导入会拉取 Git 依赖并编译，耗时较长。

3. 打开对比评测场景并进入 Play Mode：

   ```text
   Assets/FxUIParticleTest/Scenes/FxRTComparison.unity
   ```

4. 在 Game 视图按屏幕上的按钮切换 **UI 粒子 / RenderTexture** 两种模式与各项优化开关，
   点「开始采集」即可得到逐帧 CSV 与 `comparison.csv`。

> 想单独体验上游 Demo：把场景 `Assets/Scenes/...` 或通过 **Package Manager → UI Particle → Samples → Demo → Import** 导入示例。

---

## 工程目录结构

```text
ParticleEffectForUGUI-HP/
├─ Assets/
│  ├─ FxUIParticleTest/          # 本仓库的核心测试工程
│  │  ├─ Runtime/                #   FxRTComparison / FxBaselineController / FxProfilerRecorder
│  │  ├─ Editor/                 #   菜单：生成特效库、构建基线场景、Android 打包部署、合批诊断
│  │  ├─ Effects/                #   EF_00 ~ EF_29 共 30 个粒子特效 Prefab
│  │  ├─ Scenes/                 #   FxRTComparison.unity / FxUIParticle_Baseline.unity
│  │  ├─ ComparisonParticle.shader / ComparisonComposite.shader
│  │  └─ RTComparison.md         #   场景说明与采集方法
│  ├─ AIAnalysisToolkit/         # FrameDebugger / Profiler 的导出与 AI 分析辅助工具
│  ├─ Demo/                      # 上游 Demo 资源（CustomView / Performance Demo）
│  ├─ Editor/ Scenes/ Settings/ Tests/   # 工程级设置与测试
│  └─ Samples -> Packages/src/Samples~   # 指向内嵌包示例（不入库，见「开发说明」）
├─ Packages/
│  ├─ src/                       # Fork 后的 UI Particle 包（含全部定制优化）
│  ├─ com.shanflyer.framework/   # 项目自有框架（内嵌）
│  ├─ com.unity.render-pipelines.universal@14.0.11/  # 内嵌 URP（含自定义 OverDraw 诊断 Feature）
│  ├─ manifest.json / packages-lock.json
├─ ProjectSettings/              # Unity 工程设置（ProjectVersion = 2022.3.49f1）
├─ Docs/                         # 方案与优化文档
├─ Tools/                        # 离线回归、Profiler 导出脚本
├─ LICENSE / README.md / THIRD-PARTY-NOTICES.md / CONTRIBUTING.md / CHANGELOG.md
```

---

## 测试与评测场景

### `FxRTComparison.unity` — UI 粒子 vs RenderTexture 对比

详见 [`Assets/FxUIParticleTest/RTComparison.md`](./Assets/FxUIParticleTest/RTComparison.md)。要点：

- 控制界面主 Canvas + **20 个独立特效 Canvas**（各带 `CanvasGroup` 与背景），底部 UI / 粒子层 / 顶部 UI 三层交错；
- 共 **20 个特效、98 个子粒子系统**（10 种参数各两份，相同种子，便于测试网格共享）；
- **RT 模式**：20 台相机 + 20 张 RT，经 `RawImage` 显示，分辨率 128 / 256 / 512 / 1024 / 2048（默认 256，ARGB32 + 24 位深度，无 MSAA/MipMap/HDR）；
- **UI 模式**：把 `UIParticle` 放到相同的粒子层，两种模式复用相同槽位、分组、发射参数、材质与种子；
- 采集：目标 200 FPS、关闭 VSync，预热 5 秒、采样 30 秒；输出逐帧 CSV 与 `comparison.csv`（均值 / P95），含帧间隔、主线程/渲染线程、GC、内存、Draw Calls、SetPass、三角形、顶点、GPU 时间、存活粒子数、RT 理论占用等 37 列；
- 分组 -1 / +1 可动态调整组数（0–100）并自动缩放布局，文件名记录组数、RT 分辨率与全部开关。

### `FxUIParticle_Baseline.unity` — 基线场景

由编辑器菜单生成，用于不依赖动态搭建的最小复现基线。

### 编辑器菜单

| 菜单 | 说明 |
| --- | --- |
| `FxUIParticle/1. Generate Effect Library (30 Effects)` | 生成/刷新 `EF_00 ~ EF_29` 特效库 |
| `FxUIParticle/2. Build Baseline Scene` | 构建基线场景 |
| `FxUIParticle/3. Prepare Android Build (IL2CPP+ARM64+Scene)` | 切换 Android / IL2CPP / ARM64 并配置场景 |
| `FxUIParticle/4. Build And Deploy APK` | 构建 `APK/00.apk` → `adb install -r` → 启动主 Activity |
| `FxUIParticle/5. Batch Diagnostic (Editor)` | 只读合批诊断：统计材质实例去重、`materialCount` 分布与材质「游程」数 |

> `adb` 路径通过环境变量 `ANDROID_SDK_ROOT` / `ANDROID_HOME` 解析（`platform-tools/adb.exe`），未设置时回退到 PATH 上的 `adb`。

---

## 离线回归与诊断工具

- `Tools/ParticleCrashTests/` — 不启动 Unity 的离线编译与逻辑回归：
  使用本机 Unity 自带的 C# 编译器与 FXC，抽取真实运行时源码配合托管替身，覆盖调度器、计时器、Renderer 优化、采集控制、粒子数量配置、工具函数等，合计 **98 个修复后检查**。
  运行：`python Tools/ParticleCrashTests/run_extended.py`（详见 [README](./Tools/ParticleCrashTests/README.md)）。

- `Tools/ProfilerAI/` — 从 Unity Profiler 原始数据中抽取单帧、转换为可分析格式的辅助脚本。

- `Assets/AIAnalysisToolkit/` — 编辑器内的 FrameDebugger / Profiler 导出与（可选）AI 分析辅助工具。

---

## 性能开关速查

```csharp
using Coffee.UIExtensions;
using UnityEngine;

// 一次性设置全局开关（建议在进入战斗/界面时按需切换）
UIParticle.mergeRenderers  = true;  // 合并同一 UIParticle 下的全部非 Trail 粒子系统
UIParticle.useGroupCache   = true;  // MeshSharing 组成员查找走字典缓存
UIParticle.bakeFPS         = 30;    // 烘焙降频到约 30Hz（0 = 每帧）
UIParticle.earlyCull       = 1;     // 0 关 / 1 渲染裁剪 / 2 渲染 + 模拟裁剪
UIParticle.staticMeshCache = true;  // 静态时整帧跳过烘焙
UIParticle.fastBindingMode = false; // 运行时绑定状态由调用方显式通知
```

> 指标定义、口径与「哪些优化在什么条件下才有收益」的讨论，见 `Docs/` 下两份文档。

---

## 文档

| 文档 | 内容 |
| --- | --- |
| [`Docs/ParticleEffectForUGUI_项目引入与定制优化方案.md`](./Docs/ParticleEffectForUGUI_项目引入与定制优化方案.md) | 上游工作流与瓶颈分析、Fork 策略与优化方案 |
| [`Docs/FurtherOptimizationPlan_20260911.md`](./Docs/FurtherOptimizationPlan_20260911.md) | 已完成的前置项、下一步优先级清单与架构边界 |
| [`Assets/FxUIParticleTest/RTComparison.md`](./Assets/FxUIParticleTest/RTComparison.md) | 对比场景的负载、开关、采集与口径说明 |
| [`Packages/src/README.md`](./Packages/src/README.md) | 上游 UI Particle 包的完整使用文档 |

---

## 开发说明

- **`Assets/Samples`**：本工程中该路径是指向 `Packages/src/Samples~` 的目录链接，用于在编辑器内直接浏览上游示例；
  它属于**本地开发便利项，不入库**。克隆后如需示例，请在 **Package Manager → UI Particle → Samples** 中导入。
- **`*.csproj` / `*.sln` / `Library/` / `Logs/` / `UserSettings/` / `Temp/`** 均为 Unity 生成物，已被 `.gitignore` 忽略，请勿提交。
- 本仓库对上游包源码的改动集中在 `Packages/src/`（尤其是 `UIParticle*.cs`、新增的 `ParticleBakeClock.cs` / `UIParticleProfiler.cs`），便于与上游同步。

---

## 已知限制

- 本 Fork 的优化收益与**具体的特效构成、材质分布、是否可合并**强相关；对「全部同材质、可合并」的压测场景收益最大，真实项目中混合材质场景需逐项实测。
- `bakeFPS < 60`、`earlyCull = 2`、`fastBindingMode = true` 都会**改变行为语义**（更新频率 / 模拟连续性 / 绑定自动侦测），对比时必须记录开关键值，且不要在采集过程中修改配置。
- 自定义的 `OverDrawRenderFeature` 仅在 **Editor + 支持 Compute Shader / AsyncGPUReadback** 的设备上生效。
- `Assets/FxUIParticleTest/Effects` 中的特效资源仅用于性能评测，不代表生产美术规范。
- 本仓库未附带 CI（需要 Unity 许可证）；提交前请本地用 2022.3.49f1 打开工程确认 **0 编译错误**。

---

## 致谢与第三方

- **ParticleEffectForUGUI (UI Particle)** — [mob-sakai](https://github.com/mob-sakai) — MIT License — <https://github.com/mob-sakai/ParticleEffectForUGUI>
- **Unity Universal RP / Test Framework / Newtonsoft Json / AI Navigation** — Unity Technologies — 随 Unity 分发的相应许可
- 完整的第三方清单与许可证见 [`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md)。

---

## 许可证

本项目以 **MIT License** 发布，详见 [`LICENSE`](./LICENSE)。
上游 `ParticleEffectForUGUI` 的原始版权声明已一并保留。