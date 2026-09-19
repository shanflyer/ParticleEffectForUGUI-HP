# SpriteMask 验证记录

2026-09-19：迁移到 Unity 6000.4.7f1 / URP 17.4.0 后重新运行 D3D11 GPU 对照，**62/62 通过**。本次原生 EditMode 及 Player 脚本编译结果见 [Unity 6 功能补全记录](Unity6FeatureCompletion.md)。下文保留历史验证环境。

日期：2026-09-16。实现与接入说明见 [SpriteMask.md](SpriteMask.md)。

## 后续修复：相邻 Scroll View 的 Toggle 泄漏

用户反馈后增加了“HP 特效旁边的 Scroll View，Toggle 位于 Viewport 外但与 SpriteMask
重叠”的用例。旧实现实际漏出 256 个绿色像素；此前的高位清理探针没有覆盖原生低位污染，
因此旧测试通过不足以证明完整隔离。

修复使用 `SpriteMaskNativeRendering` 接管源 SpriteMask 的原生绘制，保留 enabled 输入，
相机剔除前同步，最后一个 HP 使用者退出时恢复原状态。
本次 URP 回归 **62/62 通过**，相同用例漏出 **0** 个像素；同时覆盖 Masking=None、
排序范围外的自有遮罩、共享遮罩计数和原 forceRenderingOff 状态恢复。
下表及详细输出保留为此前版本的验证记录；本次未重跑 Built-in 或重新构建 Player。

| 验证 | 结果 |
| --- | --- |
| Built-in GPU 原生对照及状态检查 | 53 / 53 通过 |
| URP 14.0.11 GPU 原生对照及状态检查 | 54 / 54 通过（另检查实际使用 URP） |
| 原材质颜色 / Alpha / Additive 结果 | 遮罩内逐像素一致 |
| 遮罩清除 | 后续 UI 探针未检测到遗留位 |
| 既有 HP managed 扩展回归 | 6 组共 96 项通过（不含已排除的 Overdraw 检查）；基础调度 11 项另行通过 |
| 项目已有程序集离线编译 | 通过 |
| 独立验证工程 Windows x64 Development Player | Build Finished, Result: Success |
| 已有粒子 Shader 修改 | 0 |

环境：Unity 2022.3.49f1、Windows、Direct3D 11.0（feature level 11.1）、NVIDIA GeForce RTX 2060。
GPU 用例使用 128×128、24-bit depth/stencil RenderTexture；原生 ParticleSystemRenderer + SpriteMask
与 Canvas 输出分别渲染后读回比较。掩膜覆盖允许最多 8 个边缘像素差异，本次所有覆盖对照实际差异为 0。
材质颜色保持测试比较原始 RGB 值，不仅比较覆盖面积。

两条预期错误路径（八层父级 Mask、无兼容 Stencil 的 Shader）故意触发明确错误日志，
检查输出被隐藏并能恢复。测试通过不代表忽略了它们。

GPU 检查在独立 Editor batchmode 进程执行；Player 完成了构建，未声称通过 Player 真机渲染测试。
未执行 Android/iOS、XR、SpriteSkin 或分离 Alpha 真机路径。历史问题基线文件未随开源项目分发，
旧工具明确报告跳过；本机原工程没有 Player 编译缓存的检查也明确跳过，以本次独立工程真实构建补充验证。

## Built-in 详细结果

```text
PASS single / Inside native=768 HP=768 mismatch=0
PASS single / Outside native=8448 HP=8448 mismatch=0
PASS None preserves original shading native=9216 HP=9216 mismatch=0
PASS nonempty GPU output sanity check
PASS overlap is union / Inside native=1344 HP=1344 mismatch=0
PASS overlap / Outside native=7872 HP=7872 mismatch=0
PASS overlapping masks / only A in range native=768 HP=768 mismatch=0
PASS overlapping masks / only B in range native=768 HP=768 mismatch=0
PASS sorting order -2 native=0 HP=0 mismatch=0
PASS sorting order -1 native=0 HP=0 mismatch=0
PASS sorting order 0 native=768 HP=768 mismatch=0
PASS sorting order 1 native=768 HP=768 mismatch=0
PASS sorting order 2 native=0 HP=0 mismatch=0
PASS Sorting Layer value included (IDs intentionally reversed) native=768 HP=768 mismatch=0
PASS Sorting Layer value excluded (IDs intentionally reversed) native=0 HP=0 mismatch=0
PASS rotated / flipped / nonuniform mask native=699 HP=699 mismatch=0
PASS animated alpha cutoff native=932 HP=932 mismatch=0
PASS shared SortingGroup native=932 HP=932 mismatch=0
PASS mask outside SortingGroup native=932 HP=932 mismatch=0
PASS global range tests group order / excluded native=0 HP=0 mismatch=0
PASS global range tests group order / included native=932 HP=932 mismatch=0
PASS nested SortingGroup native=932 HP=932 mismatch=0
PASS nested sortAtRoot skips parent mask native=0 HP=0 mismatch=0
PASS sibling mask scope does not escape native=0 HP=0 mismatch=0
PASS disabled SortingGroup native=932 HP=932 mismatch=0
PASS pooled mask disabled / Inside native=0 HP=0 mismatch=0
PASS pooled mask disabled / Outside native=9216 HP=9216 mismatch=0
PASS pooled mask enabled native=8284 HP=8284 mismatch=0
PASS merge + fast binding retain masking native=8284 HP=8284 mismatch=0
PASS masked systems use isolated draw intervals
PASS source material stencil unchanged
PASS particle shader identity preserved
PASS paused static mesh / moving mask / bake10 native=699 HP=699 mismatch=0
PASS live Inside to Outside without rebind native=8517 HP=8517 mismatch=0
PASS live mask removal without rebind native=9216 HP=9216 mismatch=0
PASS HP object pool disable / re-enable native=8517 HP=8517 mismatch=0
PASS atlas subrect UV / custom pivot native=414 HP=414 mismatch=0
PASS live sprite animation updates mesh and UV native=699 HP=699 mismatch=0
PASS HP effect scale applies to mask and particles together native=1574 HP=1574 mismatch=0
PASS original tint / alpha / additive shading preserved pixel-for-pixel
PASS post-draw cleanup leaves no stencil bit for later UI
PASS Outside AND parent UGUI Mask native=3909 HP=3909 mismatch=0
PASS Inside AND parent UGUI Mask native=699 HP=699 mismatch=0
PASS seven parent masks retain one SpriteMask bit
PASS eight parent masks explicitly suppress unsupported output
PASS stencil exhaustion recovery native=699 HP=699 mismatch=0
PASS shader without contract fails closed
PASS shader compatibility recovery native=699 HP=699 mismatch=0
PASS shared replica Inside pixels use its own mask state native=699 HP=699 mismatch=0
PASS masked replica forces consistent unmerged shared group layout
PASS shared geometry retains per-output stencil material
PASS shared replica live Outside transition native=8517 HP=8517 mismatch=0
PASS live merge toggle preserves shared mask layout native=8517 HP=8517 mismatch=0
```

## URP 详细结果

```text
PASS single / Inside native=768 HP=768 mismatch=0
PASS single / Outside native=8448 HP=8448 mismatch=0
PASS None preserves original shading native=9216 HP=9216 mismatch=0
PASS nonempty GPU output sanity check
PASS URP renderer actually active
PASS overlap is union / Inside native=1344 HP=1344 mismatch=0
PASS overlap / Outside native=7872 HP=7872 mismatch=0
PASS overlapping masks / only A in range native=768 HP=768 mismatch=0
PASS overlapping masks / only B in range native=768 HP=768 mismatch=0
PASS sorting order -2 native=0 HP=0 mismatch=0
PASS sorting order -1 native=0 HP=0 mismatch=0
PASS sorting order 0 native=768 HP=768 mismatch=0
PASS sorting order 1 native=768 HP=768 mismatch=0
PASS sorting order 2 native=0 HP=0 mismatch=0
PASS Sorting Layer value included (IDs intentionally reversed) native=768 HP=768 mismatch=0
PASS Sorting Layer value excluded (IDs intentionally reversed) native=0 HP=0 mismatch=0
PASS rotated / flipped / nonuniform mask native=699 HP=699 mismatch=0
PASS animated alpha cutoff native=932 HP=932 mismatch=0
PASS shared SortingGroup native=932 HP=932 mismatch=0
PASS mask outside SortingGroup native=932 HP=932 mismatch=0
PASS global range tests group order / excluded native=0 HP=0 mismatch=0
PASS global range tests group order / included native=932 HP=932 mismatch=0
PASS nested SortingGroup native=932 HP=932 mismatch=0
PASS nested sortAtRoot skips parent mask native=0 HP=0 mismatch=0
PASS sibling mask scope does not escape native=0 HP=0 mismatch=0
PASS disabled SortingGroup native=932 HP=932 mismatch=0
PASS pooled mask disabled / Inside native=0 HP=0 mismatch=0
PASS pooled mask disabled / Outside native=9216 HP=9216 mismatch=0
PASS pooled mask enabled native=8284 HP=8284 mismatch=0
PASS merge + fast binding retain masking native=8284 HP=8284 mismatch=0
PASS masked systems use isolated draw intervals
PASS source material stencil unchanged
PASS particle shader identity preserved
PASS paused static mesh / moving mask / bake10 native=699 HP=699 mismatch=0
PASS live Inside to Outside without rebind native=8517 HP=8517 mismatch=0
PASS live mask removal without rebind native=9216 HP=9216 mismatch=0
PASS HP object pool disable / re-enable native=8517 HP=8517 mismatch=0
PASS atlas subrect UV / custom pivot native=414 HP=414 mismatch=0
PASS live sprite animation updates mesh and UV native=699 HP=699 mismatch=0
PASS HP effect scale applies to mask and particles together native=1574 HP=1574 mismatch=0
PASS original tint / alpha / additive shading preserved pixel-for-pixel
PASS post-draw cleanup leaves no stencil bit for later UI
PASS Outside AND parent UGUI Mask native=3909 HP=3909 mismatch=0
PASS Inside AND parent UGUI Mask native=699 HP=699 mismatch=0
PASS seven parent masks retain one SpriteMask bit
PASS eight parent masks explicitly suppress unsupported output
PASS stencil exhaustion recovery native=699 HP=699 mismatch=0
PASS shader without contract fails closed
PASS shader compatibility recovery native=699 HP=699 mismatch=0
PASS shared replica Inside pixels use its own mask state native=699 HP=699 mismatch=0
PASS masked replica forces consistent unmerged shared group layout
PASS shared geometry retains per-output stencil material
PASS shared replica live Outside transition native=8517 HP=8517 mismatch=0
PASS live merge toggle preserves shared mask layout native=8517 HP=8517 mismatch=0
```
