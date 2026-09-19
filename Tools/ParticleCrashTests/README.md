# 离线回归检查

在项目根目录执行：

```powershell
python Tools/ParticleCrashTests/run_extended.py
```

脚本按 `ProjectSettings/ProjectVersion.txt` 查找 Editor，也支持 `--editor <Editor目录>` 或 `UNITY_EDITOR_PATH`；自动识别其 .NET Runtime 版本。先用该版本打开工程一次以生成 Bee 编译引用。

使用本机 Unity 6000.4.7f1 附带的 C# 编译器和现存 Bee 引用参数。仅写入 `TempDiag/crash_fix_compile/`；不启动 Unity，不进入 Play Mode，不创建图形上下文，不运行粒子/显卡/内存压力场景。

- 编译编辑器配置下的 URP、粒子库、项目运行时代码和两个编辑器程序集。
- 编译 Player 配置下的 URP、粒子库和项目运行时代码；这不是打包或运行 Player。
- `UpdaterHarness.cs`：实际调度器源码 + 托管替身，30 项，含局部缓存失效、Primary 交接、按工作量分配相位、共享组可见性及公开 Dirty API 的组传播。公开 API 方法体也从实际 UIParticle 源码抽取。
- `LogicHarness.cs`：实际计时器、回调分发、采集器源码 + 托管替身，26 项，含低帧率更新、高帧率限频、相位变化下时间守恒、数值采样零分配、按需计时生命周期和 29 列 CSV 验证。
- `RendererOptimizationHarness.cs`：抽取实际 Renderer 的材质提交、清空、绑定检查、Mesh 分配/释放及 Cull/缓存失效方法，21 项，含 Sprite/材质贴图读取计数与动态变化检测。Unity 对象用托管替身代替。
- `RecordingControlsHarness.cs`：抽取实际开始、停止、配置切换和按钮状态方法，6 项，验证停止后不自动重开、预热/采样/完成状态与活动采样配置隔离；另核对场景默认手动采集与 156 特效基线。
- `ParticleQuantityHarness.cs`：抽取实际数量配置快照类，6 项，验证按系统选择、空选择退出更新、精确重建 UIParticle 列表、恢复禁用/暂停状态，并确保单个系统的发射参数不被百分比缩放。
- `UtilityHarness.cs`：实际粒子和向量工具源码 + 托管替身，7 项；同样运行本轮修改前的工具源码，确认原来的 7 项失败。

合计 **96 个不重复的修复后检查**。`run_offline.py` 的 11 项初轮调度检查是上述 30 项的子集，不能重复计数。初轮原代码的 7 项失败、本轮原工具代码的 7 项失败是预期的回归对照，不是修改后失败。

`BakeReplayHarness.cs` 单独读取 2026-09-07 12:58:37 的已拉取 CSV，通过实际 ParticleBakeClock 重放 98 帧时间：纠正前调度 98 次，纠正后 50 次，模拟累计时间均为 7.548697 秒。该回放不执行 BakeMesh，也不能预测修复后的 FPS。

托管替身验证状态流、资源归还和计算逻辑，不证明 Unity 原生 BakeMesh、GPU 驱动、画面一致性或长时间内存稳定。历史蓝屏是否消除仍不能由这些检查判定。

Unity 6 原生 EditMode 测试与 Player 脚本编译见 `Docs/Unity6FeatureCompletion.md`。旧 Player 缓存不存在时会明确跳过对应离线步骤；原生 `CompilePlayerScripts` 可独立验证 Player 配置。
