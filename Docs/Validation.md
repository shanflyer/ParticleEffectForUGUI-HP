# 验证记录与复跑

## 当前结果（2026-09-19）

环境：Unity 6000.4.7f1、UGUI 2.0.0；GPU 对照使用 URP 17.4.0、Windows / D3D11。

| 验证 | 结果 | 范围 |
| --- | --- | --- |
| Unity 原生测试 | 34/34 通过 | 桥接、坐标、状态恢复、混合排序、缓存、降频与隐藏恢复；含进入 Play Mode 的用例 |
| SpriteMask GPU 图像与状态对照 | 62/62 通过 | 原生与 HP 遮罩、动态更新、共享、父级 Mask、位清理与异常恢复 |
| 托管回归 | 96/96 通过 | 6 组检查；基础调度 11 项是其中子集，不另行相加 |
| 编辑器离线编译 | 5 个程序集通过 | 使用本机 Unity/Bee 引用，排除本地管线实验 |
| Player 配置离线编译 | 3 个程序集通过 | 官方 URP、UIParticle、Assembly-CSharp |
| Unity 原生 Player 脚本编译 | 26 个程序集通过 | 迁移阶段执行；不是完整 Player 构建或运行 |

排除管线源码后的发布检查重跑了离线编译和 96 项托管回归。34 项原生测试与 62 项 GPU 对照是同日此前运行的结果，不声称是提交后重新跑的完整套件。

本机结果文件为 `TempDiag/optimization-tests.xml`、`TempDiag/PerformanceValidation/Logs/sprite-mask-validation-urp.txt` 和 `TempDiag/crash_fix_compile/` 下的输出；这些是生成文件，不随仓库分发。早期 26 项原生测试已被本轮 34 项覆盖，不累加计数。

## 复跑

在独立克隆或验证副本中运行。关闭该副本的 Unity Editor，再依次执行以下命令；替换 `$editor` 和 `$validationProject` 为实际路径。GPU 验证入口会创建/替换当前场景并退出 Editor，不应对正在编辑的工程执行。

```powershell
$editor = 'D:/Unity/6000.4.7f1/Editor/Unity.exe'
$validationProject = 'D:/Validation/ParticleEffectForUGUI-HP'
& $editor -batchmode -nographics -projectPath $validationProject -runTests -testPlatform EditMode -testResults "$validationProject/TestResults/editmode.xml" -logFile "$validationProject/Logs/editmode.log"
& $editor -batchmode -projectPath $validationProject -executeMethod FxUIParticleTest.Unity6Validation.CompilePlayerScripts -logFile "$validationProject/Logs/player-scripts.log"
& $editor -batchmode -force-d3d11 -projectPath $validationProject -executeMethod Coffee.UIExtensions.UIParticleSpriteMaskValidation.RunUrpBatch -logFile "$validationProject/Logs/sprite-mask-gpu.log"
```

每个进程结束后检查结果再启动下一项；GPU 验证不能加 `-nographics`。先创建日志/结果目录。离线检查的条件与命令见 [Tools/ParticleCrashTests](../Tools/ParticleCrashTests/README.md)。

## 遮罩覆盖范围

自动对照包含 Inside / Outside / None、重叠遮罩并集、排序范围端点、Sorting Layer、嵌套 SortingGroup、动态图集 UV、缩放、对象池、共享副本和父级 UGUI Mask。

另验证相邻 Scroll View 不受原生 SpriteMask 低位污染、清理临时 Stencil 位、源状态恢复，以及八层父级 Mask / 不兼容 Shader 隐藏输出后的恢复。异常用例的预期错误日志是验证的一部分。

2026-09-16 曾在 Unity 2022.3 / URP 14 环境验证 Built-in、URP 并构建 Windows Player；这是历史记录，不代表 Unity 6 当前版本已重复完成这些构建和平台测试。

## 未覆盖与测量边界

尚未完成本版 Android/iOS 真机、XR、HDRP、SpriteSkin、分离 Alpha 真机路径、长时间稳定性或完整 Player 运行验证。原地 Sprite 修改需要显式 Dirty，不能由普通 Sprite 切换测试推断所有修改方式均已覆盖。

托管替身不执行 Unity 原生粒子或 GPU；脚本编译不等于成功构建/运行；像素对照不证明帧率提升。当前未发布真机性能收益或新的 RT/UI 对比性能结论。
