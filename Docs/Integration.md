# 接入与功能边界

当前基线为 Unity 6000.4.7f1、UGUI 2.0.0；评测工程使用 URP 17.4.0。`Packages/src` 是可单独引入的包，不需要自定义渲染管线。

## 基本接入

1. 将特效放入 Canvas，在根节点添加 `UIParticle`，保留子 ParticleSystem 及其 Renderer。
2. 调整 UIParticle 的 Scale、Position Mode、Auto Scaling 等设置，确认输出位置。
3. 使用支持目标 UI 功能的材质。UGUI `Mask` 需要 Stencil；`RectMask2D` 需要裁剪逻辑。组件不会自动转换任意原生 Shader。

粒子与 UI 按 Canvas 层级绘制；粒子几何由 Unity 的 `BakeMesh` / `BakeTrailsMesh` 生成。内部烘焙相机用于几何朝向等计算，不是额外的 Camera + RenderTexture 合成路径。

## 独立 Mesh、Line、Trail

| 设置 | 默认值 | 作用 |
| --- | --- | --- |
| `effect.renderMeshes` | `true` | 接管符合条件的 MeshRenderer |
| `effect.renderLines` | `true` | 接管符合条件的 LineRenderer / TrailRenderer |
| `effect.sortBySourceOrder` | `false` | 按源 Sorting Layer 排序值、Order、层级顺序组织输出 |

扫描包含 inactive 子节点；每个源只归最近的 UIParticle 所有。

- MeshRenderer 必须有 MeshFilter、可读 Mesh、一个子网格和一份非空材质。源 Mesh 不被修改，UI 输出单独持有。
- LineRenderer / TrailRenderer 必须只有一份非空材质；通过原生 BakeMesh 生成快照。
- SkinnedMeshRenderer、多材质或不可读 Mesh 不在接管范围内。不支持的源保留原生绘制，也就不会自动获得 UI 排序与遮罩。
- 被接管的源使用 `forceRenderingOff` 隐藏原生绘制。解绑、禁用或销毁输出时恢复原值；不改变源的 enabled、emitting、宽度和最小采样距离。原先已禁止原生绘制的源不会被强行显示。

新增源、修改支持条件或重新组织层级后调用：

```csharp
effect.RefreshParticles();
```

`RefreshParticles(selectedParticles)` 保留传入的粒子选择，并重新收集独立 Renderer。普通模式会复核已绑定源的材质、贴图、Mesh 和归属；Fast Binding 模式需要显式通知绑定变化。

## 排序、共享与暂停

`sortBySourceOrder=true` 会关闭相应共享组的粒子合并布局，以保留粒子与独立 Renderer 的交错顺序。它不复现相机距离透明排序，也不完整模拟嵌套 SortingGroup 的原生绘制顺序。

Mesh Sharing 只共享粒子结果。每个实例的独立 Mesh/Line/Trail 仍单独更新，包括 Auto 的非模拟所有者与孤立 Replica；PrimarySimulator 不显示自己的桥接输出。

独立 Renderer 不随 UIParticle 的粒子暂停而冻结，仍读取源动画和轨迹。它们与粒子共用全局 `bakeFPS`；编辑器预览、暂停粒子刷新及强制失效不属于严格限频承诺，详见[性能说明](Performance.md)。

独立 Mesh/Line/Trail 可配合父级 UGUI Mask / RectMask2D，但不自动支持[粒子 SpriteMask 桥接](SpriteMask.md)。

## 运行时修改

| 修改 | 通知方式 |
| --- | --- |
| 新增源、重建绑定、Fast Binding 下修改材质/贴图/trail 设置 | `MarkBindingDirty()` 或 `RefreshParticles()` |
| 外部改粒子数据，要求缓存立即失效 | `MarkParticleDirty()` |
| 原地改同一个 Sprite 的网格 / UV | `MarkSpriteMaskDirty()` |
| 原地改桥接 Mesh 的顶点 / UV / 索引 | 正常更新会比较内容；要求立即失效可用 `MarkParticleDirty()` |

桥接材质动画通过 UIParticle 的 Animatable Properties 从源 MaterialPropertyBlock 读取指定属性。不是自动复制任意 MPB。输出支持 IMeshModifier；存在修改器时保守重建几何。

## 坐标、快照与顶点限制

Mesh 输出按源 localToWorld、效果根节点缩放和 UI worldToLocal 转换，保留三轴缩放与镜像。Unity 6000.4.7f1 验证中，世界空间 Line/Trail 的 `BakeMesh(..., useTransform:false)` 已输出世界坐标；只有局部空间 Line 再应用源 Transform。

Line/Trail 快照检查有限坐标、三角形和索引范围。短暂不连续会保留上一份有效输出，空轨迹清空，持续异常不会无限保留旧画面。`emitting=false` 不代表 Trail 头部停止跟随 Transform；不可见时原生轨迹仍可继续采样。

当前实现将单个 UI 输出限制为 64,999 顶点。合并后超限会请求整组回退为分离输出；单个输出仍超限时不会提交该网格。合并和共享都不能消除这个限制。
