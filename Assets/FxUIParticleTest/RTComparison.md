# RT / UI 粒子对比评测

在 Unity 6000.4.7f1 中打开 `Assets/FxUIParticleTest/Scenes/FxRTComparison.unity` 并运行。控制器 `Runtime/FxRTComparison.cs` 在运行时创建负载和界面。

## 负载

默认 20 组，每组一个特效 Canvas，另有控制界面 Canvas；共 20 个特效、98 个子粒子系统。10 种参数各重复两次，相同种类使用相同种子，适合测试 Mesh Sharing。

每组按 `底部 UI → 粒子层 → 顶部 UI` 排列。UI 模式在粒子层放 UIParticle，RT 模式放 RawImage，两种模式使用相同的 UI 遮挡、发射配置、材质与种子。

场景使用加法材质，无拖尾、Mask、滚动或动画面板，不能代表全部生产资产。分组可调整为 0–100；0 组用于控制界面基线。增加组数会自动缩放布局，改变单组屏幕覆盖面积，因此不是固定覆盖面积的纯数量基准。

RT 模式每组一台特效相机和一张 RT，另有共同屏幕相机。RT 可选 128、256、512、1024、2048，默认 256；ARGB32 + 24 位深度，无 MSAA/MipMap/HDR。理论内存按颜色 4 字节、深度 4 字节估算，不包含管线临时资源和驱动开销。

## 配置与采集

UI 模式可分别切换合并、组缓存、30Hz 烘焙、earlyCull、静态缓存、Mesh Sharing、详细计时和顶点色 Gamma 设置。开关行为见[性能说明](../../Docs/Performance.md)。RT 模式中 UI 开关仅是下一次 UI 模式的预设。

1. 设置组数、RT 分辨率和 UI 配置。
2. 点击开始采集：重启当前模式，默认预热 5 秒、采样 30 秒；可提前停止并保存。
3. 自动对比可按 RT → UI 或 UI → RT 执行。正式比较应两个顺序都测，采集期间配置锁定。
4. 在 Console 查看保存路径：`Application.persistentDataPath/FxRTComparison/<日期时间>/`。

控制器设定目标 200 FPS、VSync=0，停用时恢复原设置。目标不保证设备实际达到该帧率；在刷新率上限附近需结合 CPU/GPU 时间分析。正式采集关闭 Deep Profile；详细计时也会引入测量开销。

## 文件与口径

| 输出 | 内容 |
| --- | --- |
| 逐帧 CSV | 当前 `FullHeader` 为 37 列，含帧间隔、线程时间、GC/内存、绘制统计、GPU 时间、粒子数、RT 估算与 UIParticle 阶段统计 |
| `comparison.csv` | 每个有效指标的样本数、均值与 P95 |
| `.meta.txt` / `environment.txt` | 组数、系统数、布局缩放、屏幕、色彩空间、设备与配置 |

缺失计数器记录 NaN；GPU 计时需要设备支持及 Frame Timing Stats。主线程和渲染线程可能包含等待，不可相加；排除 VSync 的指标也不等于纯 CPU 工作时间。FrameTiming 可能来自延迟帧，不强行与当前帧相减。

这里的 CSV 与基线场景 `FxProfilerRecorder` 的 CSV 不同：后者为 29 列，末尾四列是桥接缓存和遮罩统计；RTComparison 尚未导出这四列。不要混用两者的列位置或实现标识。

采样缓冲预分配，采集结束后写盘；缓冲容量有限，读取结果时以实际样本数为准。降频、RT 分辨率和 Gamma 配置可能影响画面，需与性能一起记录。

本版尚无新发布的 RT/UI 真机性能结果。功能测试不能替代此场景的实际画面与性能采集。
