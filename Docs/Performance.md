# 性能行为与测量

实现入口位于 `Packages/src/Runtime/UIParticle.cs`、`UIParticleRenderer.cs` 及其 Bridge 分部文件。以下描述当前代码，不是待实现的优化规划。

## 全局开关

这些是全局静态设置，默认 bool 为 false、int 为 0；评测组件可在运行时覆盖。独立 Renderer 的 `renderMeshes/renderLines` 是实例设置且默认开启，不能概括为“所有新增功能默认关闭”。

| 设置 | 实际行为与边界 |
| --- | --- |
| `mergeRenderers` | 将同一 UIParticle 的兼容粒子本体和 Trails 烘焙后合并为 UI 输出；各源仍分别调用 BakeMesh / BakeTrailsMesh。主要减少输出节点和提交，不把多个系统变成一次原生烘焙。 |
| `useGroupCache` | 缓存 Mesh Sharing 组成员查询；不自动开启共享，也不消除所有组内遍历。 |
| `bakeFPS` | 0 为每帧；正值在运行时使用共同的非缩放时间刻度控制粒子与桥接更新。低于目标帧率时每帧更新；新增、失效和裁剪恢复可立即刷新，因此不是绝对调用上限。 |
| `earlyCull` | 0 关闭可选提前裁剪；1 在透明隐藏/已判完全裁剪时减少几何工作，粒子模拟继续按调度推进；2 在 alpha 隐藏条件下再暂停粒子模拟。 |
| `staticMeshCache` | 缓存暂停且相关变换/Canvas 状态未变的粒子输出；不等于冻结独立 Mesh/Line/Trail 或遮罩。外部修改粒子需显式失效。 |
| `fastBindingMode` | 跳过常规绑定复核；改材质、贴图、trail 或其他绑定输入后需 `MarkBindingDirty()`。不是渲染特性的开关。 |

编辑器预览不按运行时 bakeFPS 限频；暂停粒子是否重复生成输出还取决于静态缓存。粒子 Trails 正常跟随本体烘焙帧，避免独立推进模拟。降频累积实际 scaled/unscaled 时间后消费，不代表逐帧更新有完全相同的模拟轨迹或观感。

当前调度使用统一时间刻度，已不使用旧的 0.25 / 0.75 两相错峰。集中更新可能带来 CPU 峰值，需要与 Canvas 批处理耗时一起观察。

## 合并的实际检查范围

`CanMerge()` 检查粒子共享材质与有效贴图、Trail 材质/贴图兼容性，以及 SpriteMask interaction。Animatable Properties、源混合排序、共享组布局及顶点超限等条件也会影响合并或回退。

**当前没有对任意 MaterialPropertyBlock、顶点流布局、Render Mode 做完整的合并兼容性检查。** 不能假定这些差异会自动安全回退。含这类差异的资产应关闭合并并单独验证；关闭合并也不意味着任意 MPB 会自动迁移到 UI 材质。

Mesh Sharing 与合并是不同功能：前者减少重复效果的模拟和几何生成，后者减少同一效果的 UI 输出数量。二者都不能保证一个 Draw Call。

## 自动减少的工作

### 桥接网格

Mesh/Line/Trail 比较顶点、法线、切线、颜色、UV0–7、索引、拓扑及转换状态。未变化时跳过后续 CombineMeshes、几何处理和 SetMesh，材质动画继续更新。

比较仍为 O(顶点数 + 索引数)，并占用复用缓冲；扩容可能分配。Line/Trail 在到期帧仍须先 BakeMesh 才能比较结果。该缓存不表示所有活动 ParticleSystem 都会跳过 BakeMesh。

### 不可见输出

共享组按消费者可见性判断，仍有可见副本时必须继续提供粒子结果。禁用 Canvas 的消费者不计为可见输出；独立桥接会停止更新，恢复后读取最新源。

完全裁剪保留约 0.1 秒一次的几何探测，避免旧 Bounds 阻止恢复；因此不是永久零工作。没有实现对所有移出屏幕对象的独立 viewport 剔除。Line/Trail 原生组件继续采样轨迹，Full Cull 不会把整个原生特效系统停掉。

### SpriteMask

同轮更新按源 ParticleSystemRenderer 复用遮罩解析结果，body/trail 不重复筛选；场景查询每轮按需最多一次。几何与材质分别失效，纯参数变化不反复提交网格。原地修改 Sprite 几何仍需通知。

隐藏时停用对应写入节点，可见时恢复。遮罩关系不做跨帧成员缓存，以便识别外部增删、移动和排序变化。

## 指标

`UIParticleProfiler` 默认记录计数；通过 `BeginDetailedTiming()` 返回的 IDisposable 请求启用耗时统计，使用结束后释放。详细计时本身有额外开销。

关注 bake/combine/submit 的耗时与调用数，并结合 `bridgeCacheHits`、`bridgeCompareMs`、`maskMeshSubmissions`、`maskResolveCacheHits`。`submitMs` 只覆盖调用阶段，不包含后续 Canvas 批处理或 GPU 执行时间。

评测时固定资产、可见状态、屏幕尺寸、共享配置和画质；分别观察 CPU、Canvas 与 GPU。不能把 BakeMesh 时间全部视为相对原生粒子增加的净成本，原生粒子也需要准备渲染几何。

当前没有发布真机 FPS 提升或固定成本比例。验证结果见[验证记录](Validation.md)。

## 架构边界

本实现仍通过 Mesh 向 CanvasRenderer 提交。没有实现直接复用 Shuriken 原生 GPU 缓冲、向 Canvas 内部插入自定义 Draw、GPU 粒子或自定义粒子几何生成。

连续材质段合并、预算调度和独立 viewport 剔除也未实现。是否开发应由测量结果决定；旧规划中的建议不属于当前功能承诺。
