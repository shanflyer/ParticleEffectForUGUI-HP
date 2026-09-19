# UI Particle — HP / Unity 6

这是 [ParticleEffectForUGUI-HP](https://github.com/shanflyer/ParticleEffectForUGUI-HP) 的包目录，基于 mob-sakai 的 UI Particle 4.14.0 修改，采用 [MIT](LICENSE.md) 许可。包版本号保留上游基线；本分支变化见[仓库更新记录](https://github.com/shanflyer/ParticleEffectForUGUI-HP/blob/main/CHANGELOG.md)。

当前工程基线为 Unity 6000.4.7f1、UGUI 2.0.0。包本身不依赖 URP；不沿用上游旧版 Unity 兼容和性能测试声明。

## 安装

在 Package Manager 选择从 Git URL 添加包：

```text
https://github.com/shanflyer/ParticleEffectForUGUI-HP.git?path=Packages/src
```

可追加 `#<commit SHA>` 固定版本，也可引用本地 `package.json`。本包与上游具有相同包名，不应同时安装。OpenUPM 的上游包不包含本分支修改。

## 使用

将 ParticleSystem 特效放入 Canvas，在特效根添加 `UIParticle`，设置缩放并验证材质的 UI Stencil / RectMask2D 裁剪支持。通过 Package Manager 的 Samples 可导入 Demo。

本包使用 BakeMesh / BakeTrailsMesh 生成 UI 几何，支持 Mesh Sharing 和粒子 SpriteMask；也能接入符合条件的独立 MeshRenderer、LineRenderer、TrailRenderer。不需要为每个效果添加 Camera + RenderTexture 合成。

- [完整接入与限制](https://github.com/shanflyer/ParticleEffectForUGUI-HP/blob/main/Docs/Integration.md)
- [性能开关与默认值](https://github.com/shanflyer/ParticleEffectForUGUI-HP/blob/main/Docs/Performance.md)
- [SpriteMask Shader 契约](https://github.com/shanflyer/ParticleEffectForUGUI-HP/blob/main/Docs/SpriteMask.md)
- [验证记录](https://github.com/shanflyer/ParticleEffectForUGUI-HP/blob/main/Docs/Validation.md)

上游通用组件资料见 [原项目](https://github.com/mob-sakai/ParticleEffectForUGUI)。其中安装地址、旧版兼容性与性能数字不代表本分支；[包内历史更新记录](CHANGELOG.md)保留上游历史。
