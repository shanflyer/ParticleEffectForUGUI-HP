# Unity 6 功能补全与迁移记录

目标环境：Unity **6000.4.7f1**、URP **17.4.0**、UGUI **2.0.0**。日期：2026-09-19。

对照用户提供的《UIParticle_HP_完整功能方案总结.md》，以实际源码和原生测试为准；该文档的伪代码、历史行为和补齐建议没有被直接视作已有功能。

## 功能核对

| 项目 | 本次处理 |
| --- | --- |
| 粒子 / 粒子 Trails 合并、组级回退 | 保留已有实现；修复 body/trail 对同一个原生 Renderer 的 enabled 恢复冲突 |
| SpriteMask、父级 Mask、共享副本独立遮罩 | 保留已有实现；增加 `MarkSpriteMaskDirty()`，支持同一 Sprite 原地修改几何 / UV 后显式失效 |
| Bake Clock、组缓存、错峰、裁剪、静态缓存、Fast Binding | 保留已有功能；显式 Dirty 同时失效同帧静态判断 |
| 独立 MeshRenderer | 新增单子网格、单材质、可读 Mesh 的 Canvas 输出；源 Mesh 只读，输出私有持有 |
| 独立 TrailRenderer / LineRenderer | 新增原生 BakeMesh 接入；保持源 enabled / emitting / widthMultiplier / minVertexDistance；只接管 forceRenderingOff |
| 首次扫描与开关变化 | 两种 Refresh 重载和 OnEnable 都收集桥接源；保留调用者指定的粒子列表；Inspector / API 开关变化触发重建 |
| 源状态恢复 | 禁用、重绑、销毁生成输出时恢复原 forceRenderingOff；原先已关闭原生绘制的源也保持隐藏 |
| 独立 Renderer 与 MeshSharing | 粒子共享不变；每个实例独立更新 Mesh/Line/Trail，包括 Auto 非所有者和孤立 Replica；PrimarySimulator 不显示自身桥接输出 |
| 混合输出排序 | 可开启 Sort By Source Order，按源 sorting layer/order、层级顺序绘制；保持粒子共享槽位索引不变，整组使用分离粒子输出 |
| 材质动画和网格修改器 | 桥接源支持 UIParticle 配置的 Animatable Properties，从该源读取 MaterialPropertyBlock；输出执行 IMeshModifier 并重算 Bounds |
| 拖尾异常快照 | 校验有限顶点、索引范围与三角形；复用 List 读取索引；空快照清空；不连续最多拒绝三次，下一份合法快照可重建基线；持续损坏不无限残留旧画面 |
| 世界空间粒子 | 绘制绕发射器缩放，模拟使用真实世界位置；分辨率变化按相对发射器的偏移重映射；距离发射从上一位置零步模拟后恢复本帧位置，再消费实际 dt |
| Unity 6 项目 | 迁移依赖、UGUI 程序集引用、URP 资源和设置；采集器改用有效的 ProfilerRecorder 统计接口 |

## 接入

将 Mesh / Line / Trail 放在 UIParticle 层级内，或对现有特效调用：

```csharp
effect.renderMeshes = true;        // 每个 UIParticle 的序列化设置，默认开启
effect.renderLines = true;         // 默认开启
effect.sortBySourceOrder = true;   // 可选：混合排序；默认关闭以保留旧粒子列表顺序
effect.RefreshParticles();
```

每个源只归最近的 UIParticle 所有，包括 inactive 子节点。不支持的源保持原生绘制，不被接管。Mesh 资源需开启 Read/Write、仅有一个子网格和一份非空材质；SkinnedMeshRenderer 不在范围内。Line / Trail 也要求单材质。

默认模式会检查已接管源的材质、贴图、Mesh、材质槽数量、归属和支持条件。新增源，或让之前不支持的源变为可用后，调用 `RefreshParticles()` / `MarkBindingDirty()`；Fast Binding 下修改绑定也需显式通知。

`RefreshParticles(selectedParticles)` 保留精确的粒子选择，同时重新收集独立 Renderer。修改 Sprite 原地顶点或 UV 后调用 `MarkSpriteMaskDirty()`；外部改粒子数据仍用 `MarkParticleDirty()`。

桥接材质沿用原 Shader。父级 UGUI Mask / RectMask2D 需要 Shader 支持对应 Stencil / UI 裁剪属性；不能把任意原生 Shader 自动转换为 UI Shader。

## Unity 6.4 坐标与运行语义

Mesh 使用 `W × T(O) × Scale(S) × T(-O) × L`。`O` 为效果根节点，`L` 为源 localToWorld，`W` 为输出 worldToLocal。

Unity 6.4 的实际测试显示，`BakeMesh(..., useTransform: false)` 的世界空间 Line 和 Trail 输出已在世界空间；仅 `LineRenderer.useWorldSpace == false` 时再乘源 localToWorld。不能对世界轨迹重复乘源 Transform。本实现保留三轴缩放和镜像，未采用原方案的 X/Y 绝对值平均缩放。

Trail 的最后一点跟随当前头部，`emitting = false` 不等于冻结最后一点。长帧后的三个恢复帧保持上次有效快照；空轨迹会清空。Full Cull 停止 UI 输出更新，但原生 Trail 仍继续记录轨迹。

独立 Renderer 和粒子共用 `bakeFPS`：0 表示每帧，正值使用全局绝对时间刻度同步更新。它们不受粒子暂停控制，保留独立源动画；不会因效果内没有 ParticleSystem 被误判为空闲。新增、缓存失效和裁剪恢复允许立即刷新。

Sort By Source Order 使用各源自身的 sorting layer/order 和层级作为稳定次序，不宣称复现所有原生透明物体的相机距离排序或嵌套 SortingGroup 排序。桥接对象没有 ParticleSystemRenderer 的 SpriteMask 交互输入，因此没有自动接入粒子 SpriteMask；仍支持父级 UGUI 遮罩。

## 工程迁移

- 主工程锁定 Unity 6000.4.7f1；URP 17.4.0 使用 Unity 官方包，由 Package Manager 解析（当前锁文件标记为 builtin）。
- 从仓库移除内嵌 URP 14 源码；不上传管线源码、自定义 Renderer Feature 或本地管线实验。评测工程保留官方 URP 包依赖与 Unity 6 配置资源。
- Unity 自动迁移的 URP Asset / Global Settings / 默认 Volume Profile 随工程保存。

## 验证与复跑

原生测试结果位于 `TempDiag/unity6-tests.xml`，Player 编译日志位于 `TempDiag/unity6-player.log`。这些生成日志不入库。

```powershell
$editor = 'D:/Unity/6000.4.7f1/Editor/Unity.exe'
& $editor -batchmode -nographics -projectPath $PWD -runTests -testPlatform EditMode -testResults TempDiag/unity6-tests.xml -logFile TempDiag/unity6-tests.log
& $editor -batchmode -nographics -projectPath $PWD -executeMethod FxUIParticleTest.Unity6Validation.CompilePlayerScripts -logFile TempDiag/unity6-player.log
python Tools/ParticleCrashTests/run_extended.py
& $editor -batchmode -force-d3d11 -projectPath $PWD -executeMethod Coffee.UIExtensions.UIParticleSpriteMaskValidation.RunUrpBatch -logFile TempDiag/unity6-gpu.log
```

每条 Unity 命令结束后再执行下一条。GPU 对照必须启用图形设备，不能加 `-nographics`。

已完成：

- EditMode：26/26 通过，其中新增 HP 功能用例 21 项，包括真实 Line/Trail BakeMesh 坐标、Mesh 私有输出、材质动画、网格修改器、销毁清理、原生状态恢复、混合排序、世界空间重映射、快照恢复。
- Unity 6 Windows Development Player 脚本编译：26 个程序集通过；未构建或运行完整 Player。
- 托管回归（本次发布范围）：96 项通过（26 + 30 + 7 + 21 + 6 + 6）；基础调度的 11 项是其中子集，不重复计数。
- 编辑器配置的五个程序集、Player 配置的 URP / UIParticle / Assembly-CSharp 三个程序集离线编译通过。
- SpriteMask：URP 17 / D3D11 下 62/62 原生图像与状态对照通过，结果见 `Logs/sprite-mask-validation-urp.txt`。

尚未测量 Android/iOS 真机性能、长时间稳定性或新增功能的性能收益。

## 动态输出性能优化（2026-09-19）

- 不自动增加 Canvas，由界面设计决定隔离边界。
- 桥接输出比较可复用缓冲中的顶点、法线、切线、颜色、UV0–7、索引和拓扑，同时检查变换和颜色空间。内容不变时跳过 CombineMeshes、边界重算和 SetMesh，材质动画仍更新。原地修改 Mesh 的顶点/UV/索引无需手动通知；存在 IMeshModifier 时保守地重新生成输出。
- 比较本身是 O(顶点+索引)，并占用复用缓冲；不是零成本缓存。Line/Trail 在到期帧仍需 BakeMesh 和快照校验，再判断是否跳过转换与提交。缓冲扩容可能分配内存，稳定容量下不按帧创建快照数组。
- `UIParticle.bakeFPS` 同时控制粒子和桥接输出，采用共同的非缩放时间刻度；不同时间启用的实例在首帧后对齐。粒子累积实际经过的 scaled/unscaled 时间，消费后清零，避免降频导致模拟变慢或重复推进。现在低于目标帧率时每帧更新，不再强制隔帧。全局同步可能集中 CPU 峰值，应对比 Canvas 更新收益与帧时间峰值。
- `earlyCull=1`：透明隐藏的输出停止烘焙/提交，粒子继续模拟；`earlyCull=2` 保留透明隐藏时暂停粒子模拟的已有语义。桥接原生 Line/Trail 一直保持启用以继续采样。完全裁剪保留 0.1 秒几何探测，恢复时允许立即刷新。
- 禁用的 Canvas 不参与共享组可见输出统计；桥接停止更新，恢复后读取最新源。共享粒子仍须为可见副本提供输出。
- SpriteMask 几何、材质分别失效：纯 cutoff/stencil/贴图参数变化不重新提交几何；重新启用会补交。隐藏输出停用其模板写入节点，恢复时重新解析遮罩。
- SpriteMask 同一轮更新内按源 ParticleSystemRenderer 复用匹配结果，body/trail 不重复筛选。场景查询仍在每轮需要遮罩时执行一次，使用无排序查询；没有跨帧缓存场景成员，确保任意外部脚本新增/删除/移动遮罩在下次更新生效。
- `MarkParticleDirty()` 强制现有输出缓存失效；原地更改 Sprite 几何仍调用 `MarkSpriteMaskDirty()`。

观察 `UIParticleProfiler` 的 `bridgeCacheHits`、`bridgeCompareMs`、`maskMeshSubmissions`、`maskResolveCacheHits`，结合原有 bake/combine/submit 统计和 Unity 的 Canvas/GPU Profiler。提交耗时不包含后续 Canvas 批处理或 GPU 时间。功能测试与调用次数验证不代表真机帧率提升。

本轮验证：隔离 Unity 6000.4.7f1 工程 `TempDiag/PerformanceValidation` 中 34/34 原生测试通过（含进入 Play Mode 的降频/透明隐藏恢复用例），URP 17 / D3D11 遮罩图像与状态对照 62/62 通过。原生结果 `TempDiag/optimization-tests.xml`，图像对照 `TempDiag/PerformanceValidation/Logs/sprite-mask-validation-urp.txt`。现有 CSV 末尾追加 `bridge_cache_hits,bridge_compare_ms,mask_mesh_submissions,mask_resolve_cache_hits` 四列，原有列位置不变；实现标识为 `hp-unity6-20260919-cache`。本轮没有真机帧率或整帧耗时结论。
