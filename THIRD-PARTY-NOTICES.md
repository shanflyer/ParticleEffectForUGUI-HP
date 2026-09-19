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

在 `Packages/manifest.json` 中以 Git URL 引用，首次导入工程时由 Unity Package Manager 解析：

| 包 | 许可 | 来源 |
| --- | --- | --- |
| `com.coffee.development` | MIT | <https://github.com/mob-sakai/Coffee.Internal.git?path=Packages/Development> |
| `com.coffee.minimal-resource` | MIT | <https://github.com/mob-sakai/Coffee.Internal.git?path=Packages/MinimalResource> |
| `com.coffee.nano-monitor` | MIT (mob-sakai) | <https://github.com/mob-sakai/Coffee.Internal.git?path=Packages/NanoMonitor> |

## 3. Unity 官方包

以下为工程使用的主要 Unity 包；实际解析版本见 `Packages/packages-lock.json`，许可以各包附带文本为准：

| 包 | 版本 | 说明 |
| --- | --- | --- |
| `com.unity.render-pipelines.universal` | 17.4.0 | Unity 官方包（当前锁文件为 builtin），许可见包内 `LICENSE.md` |
| `com.unity.render-pipelines.core` | 17.4.0 | 由 URP 间接依赖 |
| `com.unity.shadergraph` | 17.4.0 | 由 URP 间接依赖 |
| `com.unity.test-framework` | 1.6.0 | 测试框架 |
| `com.unity.ide.rider` | 3.0.40 | Rider 集成 |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | Newtonsoft.Json 封装（Json.NET 为 **MIT**，© James Newton-King） |
| `com.unity.ai.navigation` | 2.0.13 | AI Navigation |

> 当前工程通过 Package Manager 获取官方 URP 17.4.0；仓库不分发内嵌管线源码或自定义 Overdraw 渲染特性。

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
