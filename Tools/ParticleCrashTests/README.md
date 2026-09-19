# 离线编译与托管回归

从项目根目录运行：

```powershell
python Tools/ParticleCrashTests/run_extended.py
```

脚本不启动 Unity、不执行原生粒子或 GPU。它使用本机 Unity 自带的 C# 编译器和已有 Bee 引用，输出到 `TempDiag/crash_fix_compile/`。

## 前提

- 当前工程基线为 Unity 6000.4.7f1。先用该版本导入工程，生成 `Library/Bee` 编译引用。
- `unity_environment.py` 按 ProjectVersion 查找 Editor，也支持 `--editor <Editor目录>` 或 `UNITY_EDITOR_PATH`。
- `run_offline.py` 当前读取固定的 `Library/Bee/artifacts/1900b0aEDbg.dag` 编辑器缓存；不同配置产生其他目录时需要调整脚本，不能在全新未导入工程中直接运行。
- 缺少 Player 编译缓存时明确跳过该项。需要实际 Player 脚本编译时使用[验证记录](../../Docs/Validation.md)中的 Unity 入口。

## 覆盖范围

编译编辑器配置的官方 URP、粒子包、项目运行时代码和两个编辑器程序集；有缓存时编译 Player 配置的 URP、粒子包和项目运行时代码。编译输入排除本地 Overdraw 实验，不需要内嵌管线源码。

| 检查组 | 项数 | 主要内容 |
| --- | --- | --- |
| LogicHarness | 26 | 计时器、回调、采集器、计时生命周期与 29 列 CSV |
| UpdaterHarness | 30 | 共享组、模拟所有者、显式 Dirty、可见性、统一调度和异常隔离 |
| UtilityHarness | 7 | 形状缩放、缺失 Renderer、排序与无效数值 |
| RendererOptimizationHarness | 21 | 裁剪恢复、材质提交、绑定检查和按需网格分配 |
| RecordingControlsHarness | 6 | 手动采集、预热、停止和配置隔离 |
| ParticleQuantityHarness | 6 | 粒子选择与原状态恢复 |

合计 96 项；`run_offline.py` 的 11 项调度检查是其中子集。检查通过只证明被抽取代码在托管替身下的状态与计算；部分计时器检查仍覆盖保留的旧 `Advance` 方法，不是当前全局刻度调度的完整原生验证。

历史 before 文件不分发，缺失时会跳过；存在时预期失败用于回归对照。`BakeReplayHarness.cs` 依赖本地历史 CSV，仅供历史分析；其旧调度重放数字不代表当前运行时。`ReadbackHarness.cs` 是历史 Overdraw 检查，当前入口不再执行。

原生 BakeMesh、实际图像、Player 运行和稳定性需要另行验证，见[当前验证记录](../../Docs/Validation.md)。
