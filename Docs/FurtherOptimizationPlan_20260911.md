# 进一步优化清单（基于既有优化规划，不依赖压测数据）

整理日期：2026-09-11。来源：前两轮 Runtime 评审规划 + 仓库现状核对。

## 一、规划中已完成的前置项

| 项目 | 现状 | 位置 |
| --- | --- | --- |
| 常驻 Profiler 计时 | 已改为 `BeginDetailedTiming()` 引用计数，默认只留计数、不取 Stopwatch | `UIParticleProfiler.cs` |
| Cull 恢复可见时补烘 | 已实现，恢复时 `InvalidateMeshCache()` 并重置探测时间 | `UIParticleRenderer.Cull` |
| StaticCache 显式失效 API | 已实现 `MarkParticleDirty()` / `InvalidateMeshCache()` | `UIParticle.cs`、`UIParticleUpdater.cs` |
| 合并 Mesh 按需分配 | 已实现，首次实际烘焙才建中间 Mesh，失去模拟权释放 | `EnsureMergedMeshes` / `ReleaseMergedMeshes` |
| 共享组局部失效、可见性聚合 | 已实现 | `UIParticleUpdater.cs` |
| 两相错峰调度 | 已实现 0.25 / 0.75 两桶 + 工作量估算 | `UIParticleUpdater.cs` |

## 二、第一优先级：正确性与收益前提

1. **收紧 Merge Compatibility**
   现在 `CanMerge()` 只比较 Material、贴图、Trail Material。缺少：
   - `ParticleSystemRenderer.GetPropertyBlock()` 非空时禁止合并（用户 MPB 差异会静默丢失）；
   - `GetActiveVertexStreams()` 不一致时禁止合并（Custom1/Custom2 流不同会错位）；
   - Render Mode（Billboard / Stretched / Mesh）不一致时禁止合并。
   属于正确性问题，应先于性能优化完成。

2. **补真实多材质测试用例**
   当前测试生成器强制一个 Effect 内所有 PS 用同一 Material，正好是 Merge 最理想情况。需要补：
   - Mixed Material：每个 Effect `A A B A C`；
   - Unique Stress：MeshSharing 关闭的大量独立特效。
   否则 RenderRun 的收益无法验证，现有收益也会被高估。

## 三、第二优先级：当前基线仍然能吃到的提升

3. **FastUI 三级 Fast Path**
   即使所有特效已成功 Merge，这一项仍能缩短现有热路径。
   - L1 Fast Simulation：限定 Local / Relative / Local Scaling、无 Custom Space、无 World Trail、RateOverDistance=0，砍掉 `ResolveResolutionChange` 与复杂 `GetWorldMatrix` 分支；
   - L2 Fast Transform：让 `UIParticleRenderer` Transform 与 PS local Transform 对齐，砍掉 worldToLocal 与组合矩阵；
   - L3 Direct Mesh：`BakeMesh(finalMesh)` 直接 `SetMesh`，砍掉 Combine、临时 Mesh、矩阵变换。
   落地前先做单 PS 最小 prototype，逐顶点对比现有路径，确认视觉等价。

4. **Bake Budget Scheduler（两相 → 预算制）**
   现在只有 0.25 / 0.75 两个时间位置，120Hz 下 4 个 slot 只用 2 个，仍有尖峰。
   用现有 `estimatedBakeCost` / `bakedVertices` 做每帧预算（如 1.5ms 或 80k 顶点），超出顺延，并加 `maxLatency` 下限保证每个 Effect 不低于 20–30Hz。
   同时可缓解低帧率下 `interval = max(1/fps, 2*realDelta)` 形成的 Bake 频率反馈震荡。

5. **真正的 Screen Cull**
   `earlyCull` 现在只吃 UGUI 的 `Cull(clipRect, validRect)` 回调，普通 UIParticle 移出 Canvas 不一定触发。
   用上一帧 `_lastBounds` 与根 Canvas viewport 求交，viewport 每 Canvas 缓存一次，覆盖真正的出屏剔除，对 ScrollView 大列表收益明显。

6. **共享 BakeCamera**
   目前每个 UIParticle 生成一个 `[generated] UIParticle BakingCamera`，且 `BakeMesh` 是同步调用。
   可全局共用一个相机，烘焙前只改 `orthographicSize`，省掉每实例的 GameObject / Transform / Camera 内存与初始化。

## 四、第三优先级：架构收敛

7. **Compatible Render Run（全或无 → 连续兼容段）**
   把 `AAA BB C AA` 拆成若干个单 Material 的连续 RenderGroup，Renderer 数量从 8 降到 4，且不破坏透明排序。
   注意：不要直接放宽到同一 Renderer 多 Material，Mask / Stencil 下 `materialForRendering` 与裸 Material 不一致会产生错误；分段方案天然规避。
   该项对当前“全部同材质”的压测场景没有新增收益，收益体现在真实项目的混合材质特效。

8. **Merged Mesh Pool**
   Lazy 分配已做，但失去模拟权会释放、重新接管再创建，存在资源抖动。按 Small / Medium / Large 分档池化。

9. **GroupState 直接索引**
   `GetPrimary()` 与 `GetGroupedRenderers(index)` 仍在扫描组内成员。收敛成 `GroupState { primary, members, renderersByIndex, version }`，把查找降到 O(1)。属尾部优化。

## 五、架构边界

如果做完以上，Profile 变成 `BakeMesh 75% / 其它 25%`，说明 UGUI、CanvasRenderer、分组查找、材质提交、Combine、Cull、调度能砍的已经砍完。再想大幅提升只有两条路：

- 自己读粒子数据 + Job/Burst 直接生成 UI Quad，绕过 `BakeMesh`；
- GPU Particle 路线。

## 六、建议执行顺序

1. 收紧 Merge Compatibility + 补多材质测试用例（正确性前提）
2. FastUI L1 → L2 → L3 最小 prototype 验证
3. Bake Budget Scheduler
4. Screen Cull + 共享 BakeCamera
5. Compatible Render Run
6. Mesh Pool、GroupState 索引
7. 视 Bake 占比决定是否走自绘 Quad / GPU 路线

排序理由：当前压测用例全部可合并，RenderRun 对现有基线无新增收益；FastUI 是少数能在现有基线上继续削减热路径的方向。
