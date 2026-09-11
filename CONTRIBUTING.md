# 贡献指南 / Contributing

感谢你关注 `ParticleEffectForUGUI-HP`。本项目是 [mob-sakai/ParticleEffectForUGUI](https://github.com/mob-sakai/ParticleEffectForUGUI)
的性能定制 Fork，欢迎提交 Issue 与 Pull Request。

## 提交 Issue

请在 [Issues](https://github.com/shanflyer/ParticleEffectForUGUI-HP/issues) 中选择合适的模板，并尽量提供：

- Unity 版本（本工程基线为 **2022.3.49f1**）、渲染管线版本、目标平台；
- 复现步骤与最小复现场景；
- 相关开关的取值：`mergeRenderers` / `useGroupCache` / `bakeFPS` / `earlyCull` / `staticMeshCache` / `fastBindingMode` / `meshSharing`；
- 报错堆栈、Profiler 截图或采集出的 CSV。

> 若问题来自上游原版而非本 Fork 的改动，建议同时在上游仓库反馈。

## 提交 Pull Request

1. Fork 本仓库并基于 `main` 创建分支；
2. 保持改动**聚焦**：与上游同步的部分尽量只改 `Packages/src/` 下的必要文件；
3. 新增/修改优化时，请说明**收益前提、代价与生效条件**，并在 `CHANGELOG.md` 中记录；
4. 提交前请在 Unity 2022.3.49f1 中打开工程确认 **0 编译错误**，必要时运行 `Tools/ParticleCrashTests/run_extended.py`；
5. 不要在提交中包含 `Library/`、`Logs/`、`UserSettings/`、`Temp/`、`*.csproj`、`*.sln` 等生成物。

## 代码风格

- 跟随 `Packages/src` 现有风格（4 空格缩进、`m_`/`_` 前缀约定、文件内 `#region` 组织）；
- 面向性能的热路径请避免每帧分配，必要时使用现有对象池（`ObjectPool` / `ObjectRepository`）；
- 涉及行为语义变化的开关请**默认关闭**，并提供显式失效 API。

## 许可证

提交贡献即表示你同意以本仓库的 **MIT 许可**（见 [`LICENSE`](./LICENSE)）授权你的贡献。