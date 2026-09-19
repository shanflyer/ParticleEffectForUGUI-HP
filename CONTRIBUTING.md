# 贡献指南

本仓库是 UI Particle 的 HP / Unity 6 分支。问题和 PR 请提交到 [shanflyer/ParticleEffectForUGUI-HP](https://github.com/shanflyer/ParticleEffectForUGUI-HP)，基于 `main` 开发。

## 问题报告

提供 Unity、管线和平台版本，最小复现步骤，以及相关配置：全局性能开关、Mesh Sharing、renderMeshes/renderLines/sortBySourceOrder、Mask 和 Shader。附上错误日志；性能问题还需测量条件、Profiler 或 CSV，不只提供 FPS。

## 修改与验证

- 包代码位于 `Packages/src`，评测和验证位于 `Assets/FxUIParticleTest`、`Assets/Tests`、`Tools`。
- 按改动范围运行对应检查，说明实际执行了什么、未覆盖什么。当前基线为 Unity 6000.4.7f1，入口见[验证记录](Docs/Validation.md)。
- 性能改动需说明生效条件、额外缓存/比较成本及行为变化；不要用调用次数下降替代整帧性能结论。
- 更新接入文档与 `CHANGELOG.md`，区分已实现行为和建议方案。
- 保留现有代码风格、版权和许可；热路径优先复用缓冲，并正确恢复接管的原生状态。

不提交 Unity 生成缓存、采集结果、临时验证工程、内嵌管线源码或自定义管线实验。评测工程所需官方包依赖和配置资源可以提交。

本项目自有贡献采用 [MIT](LICENSE) 许可；第三方内容保留原许可。
