# ParticleEffectForUGUI-HP

基于 [mob-sakai/ParticleEffectForUGUI](https://github.com/mob-sakai/ParticleEffectForUGUI) 4.14.0 的 Unity 6 分支。保留 Unity ParticleSystem 的制作与模拟方式，通过 `BakeMesh / BakeTrailsMesh → Mesh → CanvasRenderer` 把特效接入 UGUI。

本仓库包含粒子包、对比评测工程和验证工具。不包含内嵌渲染管线源码或自定义 Renderer Feature；评测工程使用官方 URP 包。

## 当前环境

| 项目 | 版本 / 范围 |
| --- | --- |
| Unity Editor | 6000.4.7f1 |
| 评测工程 | URP 17.4.0、UGUI 2.0.0 |
| 包 | `com.coffee.ui-particle`，位于 `Packages/src`；4.14.0 是上游基线版本 |
| 已验证环境 | Windows Editor、D3D11；详见[验证记录](Docs/Validation.md) |

粒子包本身不依赖 URP。Android/iOS 是待验证目标，不表示本版已通过移动端性能、画面或稳定性测试。也不沿用上游旧版的 Unity 2018+ 兼容声明。

## 功能与限制

- 粒子本体、粒子 Trails 接入 Canvas，参与 UI 层级、CanvasGroup 和 UGUI 遮罩；Shader 必须具备对应的 Stencil / 裁剪能力。
- 支持 Mesh Sharing：复用粒子模拟和几何结果，消费者仍各自提交 UI 输出，不等于 GPU Instancing 或单次 Draw Call。
- 支持独立 MeshRenderer、LineRenderer、TrailRenderer。Mesh 要求可读、单子网格；三者均要求单材质。不支持 SkinnedMeshRenderer。
- 支持粒子 SpriteMask，包括 Inside / Outside、多遮罩并集、父级 UGUI Mask 和共享副本独立遮罩。独立 Mesh/Line/Trail 不自动接入这套 SpriteMask 桥接。
- 提供合并输出、共享组缓存、降频、不可见时减少工作、暂停缓存、显式绑定通知，以及桥接网格和遮罩提交缓存。范围和代价见[性能说明](Docs/Performance.md)。

本方案仍承担粒子烘焙、几何处理、Canvas 批处理和 GPU 绘制成本。功能验证通过不代表已经测得整帧加速。

## 使用

### 打开评测工程

1. 克隆本仓库，用 Unity 6000.4.7f1 打开根目录。首次导入需要联网解析 `Packages/manifest.json` 中的 Git 依赖。
2. 打开 `Assets/FxUIParticleTest/Scenes/FxRTComparison.unity` 并运行。
3. 切换 UI / RenderTexture、组数和性能配置，使用界面按钮采集。输出位置与指标定义见[评测说明](Assets/FxUIParticleTest/RTComparison.md)。

SpriteMask 对照场景位于 `Assets/FxUIParticleTest/SpriteMaskDemo/SpriteMaskComparison.unity`。包内 Demo 可通过 Package Manager 的 Samples 导入。

### 引入现有项目

将本仓库的 `Packages/src` 作为本地包引用，或在 Package Manager 中使用：

```text
https://github.com/shanflyer/ParticleEffectForUGUI-HP.git?path=Packages/src
```

需要固定版本时，在 URL 末尾追加 `#<commit SHA>`。不要同时安装上游同名包。只引入该包不需要复制评测工程的 URP 配置或诊断工具。

把特效置于 Canvas 内，在特效根节点添加 `UIParticle`，调整缩放并确认材质支持 UI。独立 Renderer 的接管条件、刷新 API 和排序见[接入说明](Docs/Integration.md)。

## 文档

| 文档 | 内容 |
| --- | --- |
| [接入说明](Docs/Integration.md) | Renderer 支持范围、排序、共享、状态恢复与 Dirty API |
| [性能说明](Docs/Performance.md) | 开关默认值、缓存与裁剪边界、指标和未实现功能 |
| [SpriteMask](Docs/SpriteMask.md) | Shader 契约、遮罩作用域、Stencil 限制 |
| [验证记录](Docs/Validation.md) | 当前版本结果、复跑步骤与未覆盖范围 |
| [评测工程](Assets/FxUIParticleTest/RTComparison.md) | RT/UI 对比、CSV 与测量口径 |
| [离线检查](Tools/ParticleCrashTests/README.md) | 编译与托管回归 |
| [更新记录](CHANGELOG.md) | 本分支的版本变化 |

贡献说明见 [CONTRIBUTING.md](CONTRIBUTING.md)。本项目自有代码采用 [MIT](LICENSE)，第三方声明与原始许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
