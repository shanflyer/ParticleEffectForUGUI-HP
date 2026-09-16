# Changelog

本项目基于 [ParticleEffectForUGUI](https://github.com/mob-sakai/ParticleEffectForUGUI)（MIT）之上做性能定制 Fork。
上游包自身的版本历史见 [`Packages/src/CHANGELOG.md`](./Packages/src/CHANGELOG.md)。

## [Unreleased]

- 修复 SpriteMask 的原生低位写入泄漏到相邻 Scroll View，导致 Viewport 外 Toggle 重新显示：HP 接管源遮罩原生绘制，按引用计数恢复状态，并在相机剔除前同步新增/重挂接遮罩。

- Canvas SpriteMask 桥接：按排序范围、Sorting Layer 和嵌套 SortingGroup 解析有效遮罩集合，支持 None / Inside / Outside、多遮罩并集与父级 UGUI Mask。
- 使用独立的写入/清除 Shader，复用原粒子 Shader 的 Stencil 状态；现有粒子 Shader 未修改。自定义兼容 Shader 提供显式注册 API，不兼容或 Stencil 位不足时明确报错并停止对应输出。
- 遮罩状态独立于烘焙降频、静态缓存和共享网格更新；有遮罩的共享组使用统一的独立 Renderer 布局。
- 新增原生/HP 对照场景、动态遮罩/对象池控制、Built-in 与 URP GPU 像素回归、Windows Player 构建验证及接入文档。
- 离线回归工具明确跳过未随开源工程分发的历史基线和未生成的 Player 编译缓存。

## [1.0.0] - 2026-09-11

首个公开版本。

### 新增 / Added

- 新建独立工程 `ParticleEffectForUGUI-HP`（Unity 2022.3.49f1 + URP 14.0.11），内嵌 Fork 版 `com.coffee.ui-particle` 4.14.0。
- 性能优化开关（`UIParticle` 静态属性）：
  - `mergeRenderers`：同一 `UIParticle` 下全部非 Trail 粒子系统合并到单一 Renderer 烘焙与提交。
  - `useGroupCache`：MeshSharing 组成员查找走字典缓存。
  - `bakeFPS`：粒子网格烘焙降频（Hz），配合 `ParticleBakeClock` 错峰调度。
  - `earlyCull`：0/1/2 档裁剪（渲染裁剪 / 渲染 + 模拟裁剪）。
  - `staticMeshCache`：静态时整帧跳过 Simulate/Bake/Combine/SetMesh，并提供显式失效 API。
  - `fastBindingMode`：运行时绑定状态改为显式通知，跳过每帧复核。
- `UIParticleProfiler`：基于引用计数的常驻 Profiler 计时。
- 合并 Mesh 按需分配 / 释放、共享组局部失效与可见性聚合。
- 新增 UI 粒子 vs RenderTexture 对比评测工程（`Assets/FxUIParticleTest`）：
  20 特效 / 98 子粒子系统 / 20 独立 Canvas，支持分组缩放、RT 分辨率切换、逐帧 CSV 采集。
- 编辑器菜单：生成特效库、构建基线场景、Android 打包部署、只读合批诊断。
- 离线回归工具（`Tools/ParticleCrashTests`，98 项检查）与 Profiler 导出工具（`Tools/ProfilerAI`）。
- 自定义 URP OverDraw 诊断渲染特性（Editor-only）。
- 开源配套：`LICENSE`（MIT）、`README.md`、`THIRD-PARTY-NOTICES.md`、`CONTRIBUTING.md`、`.gitattributes`、`.github/`。

### 变更 / Changed

- 工程 `productName` 改为 `ParticleEffectForUGUI-HP`。
- `Packages/manifest.json` 显式声明内嵌依赖（`com.coffee.ui-particle`、`com.shanflyer.framework`、`com.unity.render-pipelines.universal`）。

### 注意 / Notes

- 上述优化开关默认关闭，保持与原版一致的行为。
- `bakeFPS`、`earlyCull = 2`、`fastBindingMode` 会改变运行时语义，对比评测时须记录配置。
