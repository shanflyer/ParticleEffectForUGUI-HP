# ParticleEffectForUGUI 项目引入与定制优化方案

> 目标：评估并改造 `mob-sakai/ParticleEffectForUGUI`，使其更适合当前移动端 Unity 项目的 UI 粒子渲染需求。  
> 核心原则：**不原样引入，Fork 后做受限、高性能版本。**

---

## 1. 结论

`ParticleEffectForUGUI` 是一个非常成熟的 **Shuriken → UGUI 适配层**。

它最大的价值是：

- 保留 Unity ParticleSystem / Shuriken 的编辑器与模拟能力。
- 把粒子通过 `CanvasRenderer` 绘制。
- 正确参与 UGUI 的 sibling 顺序。
- 支持 `Mask` / `RectMask2D`。
- 支持 `CanvasGroup`。
- 不需要额外 Camera + RenderTexture。
- 支持 Trail。
- 支持 Overlay / Camera / World Space Canvas。
- 支持 Mesh Sharing。

但它并不是一个为“大量不同粒子特效同时出现”设计的高性能粒子渲染器。

对当前项目而言，原版最大的结构性问题是：

> **主要 CPU 成本更接近按 ParticleSystem 数量增长，而不是只按粒子数量增长。**

当前项目的典型特征正是：

- 特效种类多。
- 单个效果粒子数量不一定很多。
- 一个效果可能包含多个子 ParticleSystem。
- 移动端。
- 对 DrawCall、透明 Overdraw、CPU Renderer 开销敏感。
- 已有自己的 FX Shader、Atlas、Texture2DArray、材质规范和特效处理工具链。

因此最合理的做法是：

> **保留 BakeMesh → CanvasRenderer 的核心思路，砍掉大量通用兼容逻辑，并接入现有 FX 资产规范。**

最终可以形成项目自己的：

```text
FxUIParticle
或
ShanFlyer UI Renderer
```

---

# 2. 原版核心工作流程

原版主要结构：

```text
ParticleSystem
      ↓
ParticleSystemRenderer.BakeMesh()
      ↓
临时 Mesh
      ↓
坐标转换
      ↓
CombineMeshes
      ↓
RecalculateBounds
      ↓
必要的颜色 / MeshModifier 处理
      ↓
CanvasRenderer.SetMesh()
```

如果开启 Trail：

```text
ParticleSystem
   ├─ BakeMesh
   └─ BakeTrailsMesh
```

一个 ParticleSystem 通常对应一个 `UIParticleRenderer`。

Trail 通常还会再增加一个 Renderer。

因此：

```text
1 个 UI 特效
   │
   ├─ ParticleSystem A → CanvasRenderer A
   ├─ ParticleSystem B → CanvasRenderer B
   ├─ ParticleSystem C → CanvasRenderer C
   └─ Trail            → CanvasRenderer D
```

如果一个复杂 UI 特效包含 5~10 个 ParticleSystem，就会形成相当多的：

- Graphic
- CanvasRenderer
- BakeMesh 调用
- Mesh 处理
- 材质状态
- UGUI batch 节点

---

# 3. 原版真正的性能瓶颈

## 3.1 BakeMesh

核心调用：

```csharp
ParticleSystemRenderer.BakeMesh(...)
ParticleSystemRenderer.BakeTrailsMesh(...)
```

这是 Unity Engine API。

它不是简单把 ParticleSystem 放进 Burst Job 就能解决的问题。

因此优化重点应该首先放在：

> **减少 BakeMesh 调用次数。**

而不是先考虑如何 Burst 化后续 C#。

---

## 3.2 CombineMeshes

Bake 之后原版还会执行额外 Mesh 处理：

```text
BakeMesh
    ↓
CombineMeshes
    ↓
RecalculateBounds
    ↓
SetMesh
```

如果能够建立项目自己的 Fast Path，就应该尽可能避免其中部分步骤。

---

## 3.3 RecalculateBounds

每帧重新计算 Bounds 属于额外 CPU 成本。

对于严格受控的 UI Particle，可以考虑：

- 固定 Bounds。
- 使用预估最大 Bounds。
- 只有粒子配置发生变化时重新计算。
- 或由 Bake Mesh 的已有 Bounds 直接使用。

---

## 3.4 CanvasRenderer 数量

原版基本按 ParticleSystem 建立 UI Renderer。

因此：

```text
ParticleSystem 数量增加
        ↓
UIParticleRenderer 增加
        ↓
CanvasRenderer 增加
        ↓
UGUI 图元 / batch 节点增加
```

即使最终部分 DrawCall 能被 UGUI 合并，CPU 端管理成本仍然存在。

---

# 4. Mesh Sharing 到底解决什么

原版支持：

```text
None
Auto
Primary
PrimarySimulator
Replica
```

本质：

```text
相同粒子效果实例
          ↓
Primary 模拟 + BakeMesh
          ↓
共享 workerMesh
          ↓
Replica CanvasRenderer.SetMesh(sharedMesh)
```

它解决的是：

- 重复模拟。
- 重复 BakeMesh。
- 重复 Mesh 处理。

它**不是 GPU Instancing**。

也不能保证：

```text
10 个 Mesh Sharing 实例
=
1 DrawCall
```

最终 DrawCall 仍然取决于：

- Material
- Texture
- Stencil
- Mask
- Canvas 顺序
- UGUI batching

因此 Mesh Sharing 对以下场景非常有效：

```text
同一种金币爆炸 × 20
同一种星星 × 50
同一个循环光效 × 30
```

但对下面这种情况帮助有限：

```text
火花
烟雾
金币
星星
闪光
技能A
技能B
技能C
……
```

因为它们模拟结果不同，无法共享。

---

# 5. 项目改造总原则

建议 Fork 后形成两种模式：

```text
UIParticle Compatible Path
        +
UIParticle Fast Path
```

---

## Compatible Path

用于：

- 特殊资产。
- 复杂 ParticleSystem。
- 世界空间模拟。
- Custom Simulation Space。
- 特殊 Trail。
- 不符合项目规范的第三方资源。

目标：

> 保留原版兼容能力。

---

## Fast Path

项目自己的 UI 粒子规范。

限制：

```text
SimulationSpace = Local
PositionMode = Relative
ScalingMode = Local
```

默认禁止或限制：

```text
World Simulation
Custom Simulation Space
复杂 RateOverDistance
World Space Trail
特殊 MeshModifier
随实例变化的 Material Property
任意第三方 Shader
```

换来的好处：

- 更少矩阵转换。
- 更少兼容分支。
- 更容易合批。
- 更容易缓存。
- 更容易减少 Bake 次数。
- 更容易统一 Shader。
- 更容易统一纹理。
- 更容易做跨特效合批。

---

# 6. P0：必须优先完成的改造

---

## 6.1 Group Renderer 查询缓存

原版 Mesh Sharing 获取组成员时，会遍历活动 UIParticle。

结构类似：

```csharp
for (...)
{
    if (particle.groupId == groupId)
    {
        ...
    }
}
```

规模增大后会形成重复全局扫描。

### 改造

维护：

```csharp
Dictionary<int, UIParticleGroup> groups;
```

逻辑结构：

```text
GroupID
  │
  └─ UIParticleGroup
       ├─ Primary
       ├─ Replica0
       ├─ Replica1
       └─ Replica2
```

在：

```text
OnEnable
OnDisable
GroupId Changed
Renderer Layout Changed
```

时更新。

运行时直接：

```text
O(1) Group Lookup
```

而不是重复扫描 Active Particle List。

---

# 7. P0：禁止运行时 Animatable Material Instance

原版为了支持 ParticleSystem Renderer 的 MaterialPropertyBlock 等能力，会生成 modified material。

这对 UGUI batching 非常不友好。

因为：

```text
Renderer A → Material Instance A
Renderer B → Material Instance B
Renderer C → Material Instance C
```

即使 Shader 相同，也容易破坏 batch。

---

## 项目规范

禁止普通 UI Particle 依赖：

```text
MaterialPropertyBlock
每实例 Material Clone
每实例 Shader Property
```

实例参数应该优先进入：

```text
Vertex Color
UV1
UV2
UV3
CustomData
```

例如：

```text
Brightness
Contrast
Dissolve
Panner
Distortion Strength
Texture Index
Texture Slice
Custom Mask
```

目标：

```text
100 个 UIParticle
      ↓
真正共享 1 个 Material
```

---

# 8. P0：统一 UI Particle Shader

第三方任意 Shader 不应该直接进入项目正式管线。

建议固定：

```text
FXShader/UI_Particle_Alpha
FXShader/UI_Particle_Add
FXShader/UI_Particle_Premultiply
```

原则上 2~3 个基础 Shader 足够。

Shader 原生支持：

```text
UGUI Stencil
RectMask2D
CanvasGroup
UNITY_UI_CLIP_RECT

MainTex
Atlas
Panner
Brightness
Contrast
Dissolve
CustomData
TSA
Soft Particle（如确有需要）
```

---

## 为什么要统一 Shader

否则 UI Particle 很容易出现：

```text
Shader A
Shader B
Shader C
ShaderGraph D
Particle/Additive E
第三方 Shader F
```

最终：

```text
材质碎片化
+
DrawCall 碎片化
+
Mask 兼容问题
+
Shader Variant 增加
```

统一后：

```text
UI Particle
    ↓
少数 Shader Variant
    ↓
少数 Material
```

---

# 9. P0：接入现有 FX Atlas 管线

项目已经有粒子纹理自动合图能力。

UI Particle 应直接复用。

推荐优先：

```text
Atlas
```

而不是一开始就在 UGUI 上硬推 Texture2DArray。

---

## 目标

原本：

```text
Effect A → Texture A → Material A
Effect B → Texture B → Material B
Effect C → Texture C → Material C
```

改成：

```text
Texture A ┐
Texture B ├─ UI FX Atlas
Texture C ┘
       ↓
Shared Material
```

---

## Texture Sheet Animation

继续使用现有的 TSA 重映射逻辑：

```text
Original TilesX/Y
       ↓
Atlas Remap
       ↓
New TilesX/Y
       ↓
Frame Index Remap
```

这样可以：

- 保留 Shuriken 编辑体验。
- 保持同材质。
- 大幅减少材质数量。
- 改善 UGUI batching。

---

# 10. 为什么 UI 第一阶段优先 Atlas，而不是 Texture2DArray

场景粒子中：

```text
Texture2DArray
+
Slice
+
统一 Material
```

非常有效。

但是 UIParticle 走的是：

```text
CanvasRenderer
```

如果 Texture Slice 通过：

```text
_MySlice
```

作为每个 Renderer 不同的 Material Property：

```text
UIParticle A → Slice 1
UIParticle B → Slice 2
```

UGUI 可能无法稳定保持相同 batch。

---

## 后续可做的正确 TextureArray 方式

把 Slice 作为顶点数据：

```text
TEXCOORD2.x = TextureSlice
```

Shader：

```hlsl
float slice = input.texcoord2.x;

SAMPLE_TEXTURE2D_ARRAY(
    _MainTexArray,
    sampler_MainTexArray,
    uv,
    slice
);
```

这样：

```text
所有 CanvasRenderer
        ↓
真正相同 Material
        ↓
每个顶点决定 Slice
```

此时 Texture2DArray 才真正有价值。

因此优先级：

```text
第一阶段：Atlas
第二阶段：Texture2DArray + Vertex Slice
```

---

# 11. P0：不可见时提前停止 Bake

原版 CanvasRenderer 最终虽然会 Cull，但如果流程已经执行：

```text
Simulate
↓
BakeMesh
↓
Combine
↓
SetMesh
↓
Cull
```

那么绝大多数 CPU 成本已经付出。

---

## 应该建立 Early Culling

在 BakeMesh 之前判断：

```text
Active Hierarchy
Canvas Enabled
CanvasGroup Alpha
RectMask
Screen Rect
Tab / Page Visible State
UIParticle Visible State
```

---

## 两档 Culling

### RenderCull

```text
Simulation：继续
BakeMesh：停止
Canvas Update：停止
```

适用于：

> 粒子重新出现时必须保持时间连续。

---

### FullCull

```text
Simulation：停止
BakeMesh：停止
Render：停止
```

适用于：

> 完全不可见的 UI 页面、关闭的面板、Tab。

---

# 12. P0：Pause / Static Mesh 缓存

如果：

```text
ParticleSystem.Pause
+
Transform 没变
+
Canvas Scale 没变
```

就没有必要继续：

```text
BakeMesh
CombineMeshes
RecalculateBounds
SetMesh
```

---

## 增加状态

例如：

```csharp
bool meshDirty;
bool transformDirty;
bool canvasDirty;
bool simulationDirty;
```

只有：

```text
simulationDirty
||
transformDirty
||
canvasDirty
```

才更新。

否则：

```csharp
return;
```

CanvasRenderer 继续使用上一帧 Mesh。

---

# 13. P1：支持降低 Bake 更新频率

很多 UI 粒子没有必要按照：

```text
60Hz
```

更新 Mesh。

例如：

- 背景烟雾。
- Glow。
- Sparkle。
- 小型装饰粒子。
- 循环光点。

可以：

```text
Canvas Render = 60Hz
Particle Bake = 30Hz
```

中间帧直接复用上一帧 Mesh。

---

## 建议配置

```csharp
enum UIParticleUpdateRate
{
    EveryFrame,
    HalfRate,
    ThirdRate,
    QuarterRate
}
```

对应：

```text
60 FPS → 60 Hz
60 FPS → 30 Hz
60 FPS → 20 Hz
60 FPS → 15 Hz
```

也可以使用：

```csharp
int updateInterval;
```

---

## 推荐

默认：

```text
重要爆炸动画       → EveryFrame
普通 UI 粒子       → HalfRate
背景装饰           → ThirdRate
非常慢的烟雾/光效  → QuarterRate
```

---

# 14. P1：Fast Path

这是整个改造中最值得投入的一层。

---

## 14.1 限制 Simulation Space

Fast Path 默认：

```text
Simulation Space = Local
```

避免：

```text
World
Custom
```

产生的大量坐标转换。

---

## 14.2 限制 Position Mode

默认：

```text
Relative
```

使 Canvas 与粒子之间坐标关系稳定。

---

## 14.3 限制复杂 Scale 修正

统一项目 UI Canvas 规范后，可以减少：

```text
World Scale
Canvas Scale
Resolution Change
Screen Size
Transform Matrix
```

相关兼容逻辑。

---

## 14.4 尝试绕过 CombineMeshes

需要验证：

```text
BakeMesh 输出 Mesh
```

在 Fast Path 下是否可以直接满足 CanvasRenderer 坐标要求。

理想流程：

```text
BakeMesh
   ↓
必要 Vertex 修正
   ↓
CanvasRenderer.SetMesh
```

而不是：

```text
BakeMesh
   ↓
CombineMeshes
   ↓
RecalculateBounds
   ↓
CanvasRenderer.SetMesh
```

---

# 15. P1：减少一个 UI 特效内部的 CanvasRenderer 数量

这是一个非常重要的方向。

现在一个 UI 特效：

```text
ParticleSystem A
ParticleSystem B
ParticleSystem C
ParticleSystem D
ParticleSystem E
```

可能对应：

```text
Renderer A
Renderer B
Renderer C
Renderer D
Renderer E
```

即：

```text
5 ParticleSystem
=
5 CanvasRenderer
```

---

## 改造目标

在 Bake 后按照 Material Group 合并：

```text
PS A ┐
PS B │
PS C ├─ Same Material Group
PS D │
PS E ┘
       ↓
Combine
       ↓
1 CanvasRenderer
```

---

## 为什么现在比原版更容易做到

因为项目已经准备统一：

```text
Shader
Atlas
Material
```

原版不能假定不同 ParticleSystem 使用相同 Material。

而项目自己的规范可以。

因此：

```text
多个 ParticleSystem
      ↓
统一 Material
      ↓
一个 Render Mesh
```

是现实可行的。

---

# 16. Trail 的处理

Trail 应单独考虑。

原因：

```text
Particle Quad
```

与：

```text
Trail Geometry
```

的数据结构和排序要求可能不同。

建议第一版：

```text
Particle Geometry → Renderer 0
Trail Geometry    → Renderer 1
```

即使一个特效有：

```text
5 ParticleSystem + 2 Trail
```

也尽量压缩为：

```text
1 Particle Renderer
+
1 Trail Renderer
```

而不是原版的：

```text
5~7 个 CanvasRenderer
```

---

# 17. P2：跨 UIParticle BatchRoot

这是解决“大量不同效果”真正有意义的一步。

Mesh Sharing 解决：

```text
相同 Effect
```

BatchRoot 解决：

```text
不同 Effect
但 Render State 相同
```

---

## 条件

只有满足：

```text
同 Canvas
同 Material
同 Stencil
同 Mask
连续绘制顺序
```

才允许合批。

---

## 架构

```text
UIParticle A ┐
UIParticle B │
UIParticle C ├─ UIParticleBatchRoot
UIParticle D │
UIParticle E ┘
             ↓
        Combined UI Mesh
             ↓
       CanvasRenderer
```

---

## 注意

不同 Effect 的：

```text
Simulation
BakeMesh
```

仍然需要分别执行。

它优化的是：

```text
CanvasRenderer 数量
DrawCall
UGUI batch 节点
```

而不是 ParticleSystem 模拟本身。

---

# 18. BatchRoot 不能破坏 UI 排序

UGUI 最大的问题不是“能不能合”，而是：

> **合批不能破坏 sibling 顺序。**

例如：

```text
Particle A
Image B
Particle C
```

不能直接把：

```text
Particle A + Particle C
```

合成一个 CanvasRenderer。

因为 Image B 需要绘制在两者中间。

所以 BatchRoot 必须以：

```text
连续可合批区间
```

为基础。

例如：

```text
Particle A
Particle B
Particle C
Image D
Particle E
Particle F
```

最多变成：

```text
Batch ABC
Image D
Batch EF
```

---

# 19. Mask / Stencil 必须作为 Batch Key

Batch Key 至少包含：

```csharp
struct UIParticleBatchKey
{
    Canvas canvas;
    Material material;
    Texture texture;

    int stencilId;
    int stencilComp;
    int stencilOp;
    int stencilReadMask;
    int stencilWriteMask;

    RectMask2D rectMask;
}
```

任何 Mask 状态不同：

```text
不能合。
```

---

# 20. CanvasGroup Alpha

不要因为不同 CanvasGroup Alpha 就生成不同 Material。

优先：

```text
CanvasGroup Alpha
    ↓
Vertex Color Alpha
```

这样继续共享：

```text
同一个 Material
```

---

# 21. Bounds 优化

原版会：

```text
RecalculateBounds
```

可以考虑三种策略。

---

## Strategy A：Static Bounds

美术或导入阶段计算：

```text
Max Particle Size
Max Velocity
Max Lifetime
Emitter Shape
```

生成保守 Bounds。

适合：

```text
UI Local Simulation
```

---

## Strategy B：Bake Bounds

只在：

```text
Particle Count
Emitter Settings
Transform
```

变化时重新计算。

---

## Strategy C：Huge UI Bounds

对于很小数量且 Bounds 本身不会影响大量过绘制的 UI Particle：

```text
直接使用固定足够大的 Bounds。
```

避免每帧 Recalculate。

这个需要实际 Profile 决定。

---

# 22. Material Group 合并设计

可以建立：

```csharp
struct UIParticleRenderKey
{
    Material Material;
    Texture Texture;
    int RenderMode;
    int StencilState;
    int MaskId;
}
```

然后：

```text
UIParticle
   ↓
ParticleSystemRenderer Bake
   ↓
RenderKey
   ↓
RenderGroup
   ↓
Combined Mesh
   ↓
CanvasRenderer
```

---

# 23. 缓存结构建议

```csharp
sealed class FxUIParticleRuntime
{
    Dictionary<int, FxUIParticleGroup> sharingGroups;

    Dictionary<UIParticleRenderKey, FxUIRenderGroup> renderGroups;

    List<FxUIParticle> activeParticles;
}
```

---

## Mesh Sharing Group

```csharp
sealed class FxUIParticleGroup
{
    FxUIParticle primary;

    List<FxUIParticle> replicas;

    Mesh sharedParticleMesh;

    Mesh sharedTrailMesh;
}
```

---

## Render Group

```csharp
sealed class FxUIRenderGroup
{
    Material material;

    Mesh combinedMesh;

    CanvasRenderer canvasRenderer;

    List<FxUIParticleRenderer> sources;
}
```

---

# 24. 不建议第一阶段做的事情

---

## 24.1 不要先 Burst 化

主要成本：

```text
ParticleSystemRenderer.BakeMesh
```

属于 Unity Engine API。

首先应该：

```text
减少调用次数
减少 Renderer 数量
减少 Mesh 后处理
减少材质变化
减少不可见更新
```

而不是马上：

```text
Job
Burst
Unsafe
```

---

## 24.2 不要第一阶段重写 ParticleSystem

当前需求只是：

> UI Particle 高效接入。

如果直接变成：

```text
自研 Particle Simulation
+
自研 Renderer
+
自研 Editor
```

开发量会立刻膨胀。

可以先保留：

```text
Shuriken Simulation
```

仅替换：

```text
UI Render Layer
```

---

## 24.3 不要继续兼容所有第三方 Shader

项目自己的高性能版本应该明确：

```text
Compatible Path
```

负责第三方。

```text
Fast Path
```

负责正式项目。

不要让 Fast Path 最终又变成另一个“什么都能跑”的通用插件。

---

# 25. 推荐最终架构

```text
                     Shuriken
                        │
          ┌─────────────┴─────────────┐
          │                           │
     Same Effect                 Unique Effect
    Mesh Sharing                  Simulation
          │                           │
          └─────────────┬─────────────┘
                        │
                     BakeMesh
                        │
                 FxUIParticle FastPath
                        │
        ┌───────────────┼───────────────┐
        │               │               │
   Early Cull       Update Rate     Mesh Dirty
        │               │               │
        └───────────────┴───────────────┘
                        │
               Project Vertex Format
                        │
          ┌─────────────┴─────────────┐
          │                           │
       FX Atlas                Vertex CustomData
          │                           │
          └─────────────┬─────────────┘
                        │
                Material Render Group
                        │
                 Combined UI Mesh
                        │
                   CanvasRenderer
```

---

# 26. 更长期的最终方向

如果未来 UI 粒子数量真的达到非常大规模，继续依赖：

```text
Shuriken
↓
BakeMesh
↓
CanvasRenderer
```

终究会遇到上限。

真正高性能的最终方向应该是：

```text
CPU / ECS Simulation
        ↓
GraphicsBuffer
        ↓
GPU Particle Renderer
```

或者：

```text
GPU Simulation
        ↓
GraphicsBuffer
        ↓
Indirect Draw
```

即：

```text
数据一直保持在 GPU
```

避免：

```text
GPU/Engine Particle Data
        ↓
CPU Bake Mesh
        ↓
CPU Mesh Processing
        ↓
GPU Upload
```

---

# 27. 对当前项目最合理的定位

因此 `ParticleEffectForUGUI` 不应该被定位成：

> 项目的最终高性能粒子方案。

它更适合被定位成：

> **项目 UI Particle 的过渡层 + 编辑器兼容层 + Shuriken 数据入口。**

项目自己的优化版本：

```text
FxUIParticle
```

应该重点解决：

1. Shader 统一。
2. Atlas 统一。
3. Material 统一。
4. Mesh Sharing 缓存。
5. Early Cull。
6. Pause Mesh Cache。
7. 30Hz / 20Hz Bake。
8. Fast Local UI Path。
9. 一个 Effect 多 PS 合并。
10. 跨 Effect Render Batch。
11. Trail 独立压缩 Renderer。
12. 顶点 CustomData 替代 MaterialPropertyBlock。

---

# 28. 开发优先级

| 优先级 | 改造 | 主要收益 | 风险 |
|---|---|---|---|
| P0 | 统一 UI Particle Shader | DrawCall / 兼容性 | 低 |
| P0 | 接入 Atlas | Material / Texture 合并 | 低 |
| P0 | 禁止实例 Material Property | 保持 Batch | 低 |
| P0 | Mesh Sharing Group 缓存 | CPU | 低 |
| P0 | Early Culling | CPU | 低 |
| P0 | Pause / Static Mesh Cache | CPU | 低 |
| P1 | 30Hz / 20Hz Bake | CPU | 低 |
| P1 | Local Simulation Fast Path | CPU | 中 |
| P1 | 一个 Effect 多 PS 合并 Renderer | CPU / DrawCall | 中 |
| P1 | Trail Renderer 合并 | DrawCall | 中 |
| P2 | TextureArray + Vertex Slice | Texture / Batch | 中 |
| P2 | BatchRoot 跨 Effect 合批 | DrawCall | 高 |
| P3 | Mesh 后处理 Job/Burst | CPU | 中 |
| P3 | 完全自研 GPU Particle Renderer | 极限性能 | 很高 |

---

# 29. 推荐实施阶段

## Phase 1：低风险接入

先 Fork 原项目。

完成：

```text
统一 Shader
Atlas
Material 规范
Group Cache
Early Cull
Pause Cache
Update Rate
```

这个阶段不大改核心结构。

---

## Phase 2：Fast Path

增加：

```text
FxUIParticleFastRenderer
```

限定：

```text
Local Simulation
Relative Position
Project Shader
Project Atlas
No Animatable Material
```

验证是否可以绕过：

```text
部分 Matrix
CombineMeshes
RecalculateBounds
```

---

## Phase 3：Effect 内部合批

目标：

```text
5~10 ParticleSystem
↓
1~2 CanvasRenderer
```

Particle 与 Trail 可以分别形成一个 Renderer。

---

## Phase 4：跨 Effect BatchRoot

当实际 Profile 证明：

```text
CanvasRenderer
DrawCall
UGUI Rebuild
```

成为主要瓶颈时再做。

---

# 30. Profile 验收指标

不要只看：

```text
FPS
```

应该记录：

---

## CPU

重点：

```text
UIParticleRenderer.UpdateMesh
ParticleSystemRenderer.BakeMesh
BakeTrailsMesh
Mesh.CombineMeshes
Mesh.RecalculateBounds
Canvas.SendWillRenderCanvases
Canvas.BuildBatch
UI.Rendering
```

---

## Rendering

记录：

```text
Batches
SetPass Calls
Draw Calls
Vertices
Triangles
CanvasRenderer Count
```

---

## GC

目标：

```text
0 B / Frame
```

尤其关注：

```text
List
Dictionary temporary
Material clone
Mesh temporary
VertexHelper
GetGroupedRenderers
```

---

## 测试场景

至少准备：

### Case A

```text
1 Effect
5 ParticleSystems
```

### Case B

```text
10 个相同 Effect
```

测试 Mesh Sharing。

### Case C

```text
10 个不同 Effect
```

测试最差情况。

### Case D

```text
30 个不同 Effect
```

压力测试。

### Case E

```text
大量 Effect
+
Mask
+
RectMask2D
+
CanvasGroup
```

真实 UI 场景。

---

# 31. 预期收益来源

最重要的一点：

性能收益不是来自某一个“大优化”。

而是：

```text
减少 BakeMesh 次数
+
减少 UpdateMesh 次数
+
减少 CanvasRenderer
+
减少 Material
+
减少 DrawCall
+
减少不可见更新
+
减少 Mesh 后处理
```

最终叠加。

---

# 32. 最终判断

对于当前项目：

## 可以引入

但应该：

```text
Fork
+
项目级定制
```

而不是：

```text
Package Manager
+
直接长期使用原版
```

---

## 最值得保留的东西

```text
Shuriken Simulation
BakeMesh
CanvasRenderer
Mask / RectMask2D
UI Sorting
Mesh Sharing 思路
```

---

## 最值得改掉的东西

```text
全局 Group 扫描
每 PS 一个 Renderer 的默认架构
频繁 Bake
不可见仍 Bake
Pause 仍更新
每实例 Material 属性
第三方 Shader 泛兼容
过度 Simulation Space 兼容
```

---

# 33. 一句话方案

> **保留 ParticleEffectForUGUI 的 Shuriken → CanvasRenderer 入口，把它改造成只服务项目 FX 规范的高性能 UI Particle Renderer；第一阶段减少 Bake 和 Material，第二阶段减少 CanvasRenderer，第三阶段再做跨 Effect 合批。**

---

# 34. 上游项目

GitHub：

```text
https://github.com/mob-sakai/ParticleEffectForUGUI
```

建议：

```text
Fork upstream
```

并保留上游同步分支：

```text
upstream/main
```

项目自己的修改：

```text
project/main
```

这样后续仍可选择性合并：

- Unity 新版本兼容修复。
- Mask 修复。
- Trail 修复。
- Canvas API 变化。
- Unity 6.x 后续兼容修复。

而项目性能修改保持独立。

