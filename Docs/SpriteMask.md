# Canvas 中保留 SpriteMask

HP 读取原 `ParticleSystemRenderer.maskInteraction`，在 Canvas 中为每个粒子输出建立：

`初始化临时 Stencil 位 → 写入有效 SpriteMask 的并集 → 原粒子材质绘制 → 清除临时位`

桥接不替换原粒子 Shader。内部的
`Hidden/UIParticle/SpriteMask` 只负责写入/清除，不输出颜色。

## 接入

1. 保留特效中的 SpriteMask、Sprite 和 SortingGroup；照常设置 Renderer 的 Masking。
2. 用 UIParticle 渲染特效，不需要额外挂桥接组件。
3. 已有 `UI/Additive` 和 Unity `UI/Default` 直接支持。
4. 其他 Shader 如已具有完整、可配置的 UI Stencil 状态，审核后在项目初始化时注册：

```csharp
using Coffee.UIExtensions;
using UnityEngine;

public static class ParticleStencilSetup
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        UIParticleSpriteMask.RegisterStencilShader(Shader.Find("Your/ExistingParticleShader"));
    }
}
```

编辑模式预览也需要调用同样的注册 API（例如项目自己的 `[InitializeOnLoadMethod]`）。
注册是对现有 Shader 能力的声明，不会为 Shader 增加功能。

需核实实际绘制 Pass 的状态为：

```shaderlab
Stencil
{
    Ref [_Stencil]
    Comp [_StencilComp]
    Pass [_StencilOp]
    ReadMask [_StencilReadMask]
    WriteMask [_StencilWriteMask]
    Fail Keep
    ZFail Keep
}
```

这些属性必须同时存在于 Properties。没有该状态的 Shader 需要补充以上属性/状态；
不要改原贴图、颜色、Blend、顶点或片元计算。**仅有同名 Properties 不代表支持。**
不要注册具有固定 Stencil、多 Pass 冲突或其他自定义 Stencil 用途的 Shader。
不支持的材质会输出一次明确错误并隐藏对应粒子；兼容条件恢复后自动恢复。

## 遮罩语义

- `None`：不增加 SpriteMask 测试，保留原有材质/UGUI 行为。
- `VisibleInsideMask`：可见于有效遮罩的**并集**内；有效集合为空时不显示。
- `VisibleOutsideMask`：可见于并集外；有效集合为空时正常显示。
- Custom Range 按 Sorting Layer **实际排序值**及 Order 比较，范围为 `(Back, Front]`。
- 同级 SortingGroup 内使用 Renderer 的排序；外层/全局遮罩使用跨越该作用域的
  SortingGroup 排序。局部遮罩不影响兄弟组；嵌套 `sortAtRoot` 跳过外层作用域。
- Sprite 顶点、三角形、图集 UV、Pivot、Alpha Cutoff、关联分离 Alpha 纹理参与写入。
- HP 的效果缩放同样作用于遮罩。Relative 围绕 UIParticle 原点缩放；Absolute 围绕
  当前发射器原点缩放，与粒子输出的空间映射保持一致。

## UGUI 与绘制组织

HP 接管的 SpriteMask 使用 `forceRenderingOff` 阻止原生绘制，保留 `enabled` 作为
动画/逻辑输入。仅清理桥接的高位不足以隔离：原生 SpriteMask 仍会写入低位，可能让
旁边 Scroll View 中本应裁掉的 Toggle 显示出来。接管在相机剔除之前同步；作用于
特效内所有 SpriteMask（包括暂时无有效粒子的遮罩），以及实际影响 HP 粒子的外部遮罩。
多个 HP 共享遮罩时按引用计数管理，最后一个停用后恢复原 `forceRenderingOff` 状态。
这里是对源遮罩原生绘制的接管：同一个 SpriteMask 不应同时服务尚未转换成 HP 的
原生 Renderer；原生效果和 HP 效果应使用各自的遮罩实例及 SortingGroup。

临时位固定使用 `0x80`。父级 UGUI Mask 使用低位时，粒子执行组合的 Equal 测试，
Inside 的参考值包含临时位，Outside 不包含；粒子本身不写 Stencil。
写入只改变临时位，清除也只改变该位，不触碰父级 Mask 的位。

最多支持七层父级 UGUI Mask。八层占满时明确报错并隐藏对应输出。
项目若有其他自定义 RendererFeature、UI Shader 使用 `0x80`，必须先协调位分配；
不能与本桥接同时占用该位。目标缓冲需要 Stencil（验证使用 24-bit depth/stencil RT）。

每个输出区间包含 N 个有效遮罩写入节点和两个清理节点。多个区间依次复用同一位，
不因遮罩数量增加而消耗更多 Stencil 位。粒子本体和 Trail 各有独立区间。
当前优先保证正确性，没有把相同遮罩集合的相邻粒子进一步合并。

有 SpriteMask Masking 的特效会退回独立 Renderer；共享网格组内所有成员也使用同样
布局，防止合并网格发送到错误的输出槽位。**网格仍可共享，Stencil 材质和遮罩节点
每个输出独立。**解除 Masking 后保守保留独立布局；切换 `mergeRenderers` 可重新评估合并。

遮罩关系每次 HP 更新重新解析；启用、禁用、排序、组重挂接、Sprite、Cutoff 和变换
不依赖 `MarkBindingDirty()`，也不会被 `bakeFPS`、暂停、静态网格缓存或 fast binding 跳过。
材质本身替换仍遵循 HP 原有的 fast binding 通知约定。
场景扫描每次 HP 更新最多一次，没有 Masking 的项目不会扫描 SpriteMask。
节点复用，几何和材质分别失效，未变化时跳过重复网格提交；对象池停用时撤下绘制。
原地修改同一个 Sprite 的网格/UV 后调用 `effect.MarkSpriteMaskDirty()`。
透明或裁剪隐藏时可减少写入节点工作，恢复可见时重新解析。
独立 MeshRenderer / LineRenderer / TrailRenderer 不自动接入这里的粒子 SpriteMask 语义。

## 对照场景与验证

- 场景：`Assets/FxUIParticleTest/SpriteMaskDemo/SpriteMaskComparison.unity`
- 重建菜单：`FxTest → SpriteMask → Create Native vs HP Scene`
- 左侧原生、右侧 HP；上排 Inside、下排 Outside；每个效果包含两个重叠遮罩。
- Play 后选中 `Mask Animation and Pool Controls`，切换动画、Cutoff、对象池开关。
- 原生示例使用 `Sprites/Default`、HP 示例使用原有 `UI/Additive`，颜色混合可能不同；
  该场景用于比较遮罩形状。自动测试另行逐像素验证 HP 原材质的颜色/Alpha/混合保持不变。

GPU 对照的当前结果、环境和复跑入口见[验证记录](Validation.md)。运行 GPU 验证需要独立工程副本和图形设备；不能加 `-nographics`。当前 Unity 6 / URP 17 对照通过不代表移动端、XR 或所有自定义 Shader 均已验证。
