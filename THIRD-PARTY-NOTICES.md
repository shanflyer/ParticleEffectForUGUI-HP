# 第三方声明 / Third-Party Notices

本仓库（`ParticleEffectForUGUI-HP`）自身代码以 MIT 许可发布（见 [`LICENSE`](./LICENSE)）。
仓库内包含或依赖以下第三方组件，其版权与许可归各自作者所有。分发本仓库时请一并保留本文件与相关许可文本。

---

## 1. ParticleEffectForUGUI (UI Particle)

- 位置：`Packages/src/`（已 Fork 并修改）
- 作者：mob-sakai
- 版本：4.14.0（上游基线）
- 许可：**MIT License**
- 来源：<https://github.com/mob-sakai/ParticleEffectForUGUI>
- 许可全文：见 [`Packages/src/LICENSE.md`](./Packages/src/LICENSE.md)

> `Copyright 2018-2026 mob-sakai`
>
> 本 Fork 在上游基础上新增/修改了性能优化相关代码（`UIParticle.cs`、`UIParticleRenderer.cs`、
> `UIParticleUpdater.cs`、`ParticleBakeClock.cs`、`UIParticleProfiler.cs` 等），
> 以及对比评测工程与工具。上游 MIT 许可与版权声明已原样保留。

## 2. Coffee.Internal（通过 Git URL 依赖，未随仓库分发）

在 `Packages/manifest.json` 中以 Git URL 引用，克隆工程时由 Unity 自动拉取：

| 包 | 许可 | 来源 |
| --- | --- | --- |
| `com.coffee.development` | MIT | <https://github.com/mob-sakai/Coffee.Internal.git?path=Packages/Development> |
| `com.coffee.minimal-resource` | MIT | <https://github.com/mob-sakai/Coffee.Internal.git?path=Packages/MinimalResource> |
| `com.coffee.nano-monitor` | MIT (mob-sakai) | <https://github.com/mob-sakai/Coffee.Internal.git?path=Packages/NanoMonitor> |

## 3. Unity 官方包

以下包随 Unity 分发，适用 Unity 各自的许可条款（Unity Companion License / Unity Package 条款）：

| 包 | 版本 | 说明 |
| --- | --- | --- |
| `com.unity.render-pipelines.universal` | 14.0.11 | **内嵌于** `Packages/com.unity.render-pipelines.universal@14.0.11`（含作者新增的 Editor-only `OverDrawRenderFeature` 诊断功能），许可见该目录下 `LICENSE.md` |
| `com.unity.render-pipelines.core` | 14.0.11 | 由 URP 间接依赖 |
| `com.unity.shadergraph` | 14.0.11 | 由 URP 间接依赖 |
| `com.unity.test-framework` | 1.1.33 | 测试框架 |
| `com.unity.ide.rider` | 3.0.36 | Rider 集成 |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | Newtonsoft.Json 封装（Json.NET 为 **MIT**，© James Newton-King） |
| `com.unity.ai.navigation` | 1.1.5 | AI Navigation |

> **关于内嵌 URP**：`Packages/com.unity.render-pipelines.universal@14.0.11` 是对官方 URP 14.0.11
> 的源码内嵌副本，并附加了一个自定义的 OverDraw 诊断渲染特性（`Runtime/RendererFeatures/OverDrawRenderFeature.cs`
> 与 `Runtime/OverDraw/*`）。若不需要该诊断功能，可在 `Packages/manifest.json` 中把它改回
> `"com.unity.render-pipelines.universal": "14.0.11"` 并删除内嵌目录，由 Package Manager 解析官方包。

## 4. 第一方 / 作者自有

| 组件 | 说明 |
| --- | --- |
| `Packages/com.shanflyer.framework` | 作者（shanflyer）自有的 ShanFlyer 框架程序集骨架（内嵌） |
| `Assets/FxUIParticleTest`、`Tools/`、`Docs/` | 作者为本项目编写的测试工程、工具与文档 |
| `Assets/AIAnalysisToolkit` | 作者编写的 Profiler / FrameDebugger 导出与 AI 分析辅助工具 |

---

## 商标

Unity、Unity Logo 是 Unity Technologies 的商标。本项目为独立开源项目，与 Unity Technologies 及
上游 `ParticleEffectForUGUI` 作者之间不存在官方隶属或背书关系。
