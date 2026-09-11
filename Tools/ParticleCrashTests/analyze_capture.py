"""Read copied emulator captures without modifying them or running the player."""
import csv
import hashlib
import io
import json
import math
from pathlib import Path
import re
import statistics

ROOT = Path(__file__).resolve().parents[2]
CAPTURE = ROOT / "DeviceData/20260907_regression_pull"
records = []
for path in sorted((CAPTURE / "FxUIParticleBaseline").glob("*.csv")):
    raw = path.read_bytes()
    lines = raw.decode("utf-8-sig").splitlines()
    rows = list(csv.DictReader(io.StringIO("\n".join(line for line in lines if not line.startswith("#")))))
    rows = [row for row in rows if row.get("frame", "").isdigit()]
    count = int(re.search(r" frames=(\d+)", lines[0])[1])
    assert count == len(rows), path.name
    result = {"file": path.name, "sha256": hashlib.sha256(raw).hexdigest(), "header": lines[0], "n": len(rows)}
    if rows:
        result["span"] = float(rows[-1]["time_s"]) - float(rows[0]["time_s"])
        frames = [int(row["frame"]) for row in rows]
        assert frames == list(range(frames[0], frames[0] + len(rows))), path.name
        result["fps_elapsed"] = (len(rows) - 1) / result["span"] if result["span"] > 0 else None
        for column in ("cpu_frame_ms", "cpu_work_ms", "gc_bytes", "draw_calls", "batches", "setpass", "triangles", "vertices"):
            if column not in rows[0]: continue
            values = [float(row[column]) for row in rows]
            assert all(math.isfinite(value) for value in values), path.name
            result[column] = statistics.mean(values)
        result["fps_cpu"] = 1000 / result["cpu_frame_ms"] if result.get("cpu_frame_ms", 0) > 0 else None
    records.append(result)

(ROOT / "TempDiag/emulator_regression_summary_20260907.json").write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
today = [r for r in records if r["file"].startswith("20260907")]
selected = [r for r in records if r["file"].startswith(("20260903_143510", "20260903_143529"))] + today
text = [
    "# 模拟器性能回归核对（2026-09-07）", "",
    f"已从 emulator-5554 拉取 {len(records)} 份原始 CSV，{sum(r['n'] > 0 for r in records)} 份包含采样；今天新增 {len(today)} 份，其中 {sum(r['n'] > 0 for r in today)} 份包含采样。原始 CSV 未改动。",
    "",
    "设备记录均标识 HUAWEI CET-AL00 / Adreno (TM) 640，Unity 2022.3.49f1。adb 包信息显示本次 APK 更新于 2026-09-07 12:57:50，versionName=1.0、versionCode=1；版本号没有区分源码修订。",
    "",
    "FPS = (末帧编号 − 首帧编号) / (末 time_s − 首 time_s)。核对帧编号连续、记录数与头部 frames 一致；不使用瞬时峰值。CPU Frame 含等帧时间；cpu_work_ms 在这些记录中全为 0，不能作为有效的纯 CPU 工作耗时。零帧记录排除性能比较。",
    "",
    "下列均为 G / 39 槽 × 4 = 156 个特效。配置以 CSV 首行完整 case 为准；新文件名截断到 Mer，无法仅凭文件名判断 Merge 状态。",
    "",
    "| 记录时间及原始数据 | 开关 | 帧数 | 首末帧跨度(s) | 平均 FPS | CPU Frame均值(ms) | 平均顶点数 |",
    "| --- | --- | ---: | ---: | ---: | ---: | ---: |",
]
for r in selected:
    stamp = r["file"][:15]
    case = re.search(r" (Cache(?:ON|OFF) .+?) frames=", r["header"])[1]
    values = f"{r['span']:.2f} | {r['fps_elapsed']:.2f} | {r['cpu_frame_ms']:.2f} | {r['vertices']:.0f}" if r["n"] > 1 else "— | — | — | —"
    text.append(f"| [{stamp}](FxUIParticleBaseline/{r['file']}) | {case} | {r['n']} | {values} |")
text.extend([
    "", "## 已确认的回归与纠正", "",
    "先前修改把 Bake30 从‘高帧率限频、低帧率隔帧’改成严格 30Hz 上限。当前每帧约 77ms，超过 33.3ms，因此每帧都通过门限，Bake30 丢失原来的减负功能。这是本次修改引入的行为回归。",
    "",
    "已恢复 max(1 / bakeFPS, 2 × 当前帧时长) 的自适应调度，并恢复 Renderer 错峰。调度余量/相位与 scaled、unscaled 模拟累计时间分别保存；每次消费模拟累计后清零，避免旧版本重复模拟余量的缺陷。没有减少 156 个实例或修改粒子资源。",
    "",
    "使用实际 ParticleBakeClock 源码重放今天 12:58:37 的 98 帧时间：纠正前通过烘焙门限 98 次，纠正后 50 次；两者模拟累计（含末尾待消费时间）均为 7.548697 秒。输入首帧 dt 采用 cpu_frame_ms，其余来自 time_s 差值；CSV time_s 仅保留两位小数，因此这是近似帧时间的调度验证。回放不执行原生 BakeMesh，不代表已测得新的 FPS。",
    "",
    "## 比较边界和仍需验证的部分", "",
    "- 9 月 3 日 14:35 的相邻样本：Bake60 / MergeON 约 16.50 FPS，Bake30 / MergeON 约 22.54 FPS。今天相邻样本：Bake60 / MergeOFF 约 12.64 FPS，Bake30 / MergeOFF 约 12.99 FPS。Bake30 收益消失与上述调度回归一致，但无法由 CSV 分摊全部耗时差异。",
    "- 今天 8 份记录的完整头部均为 MergeOFF，缺少本次 MergeON 数据。不能把旧 MergeON 与新 MergeOFF 当成所有配置一致的比较。现有按钮仍切换 mergeRenderers；没有自动把 Merge 开关关闭的代码。",
    "- 两段 Bake30 样本平均顶点约 324,531 与 450,437，相差约 39%；特效个数相同也不代表实际几何负载一致。随机粒子状态、共享修复等影响尚未通过运行时分析分离。",
    "- 历史记录并非全部超过 20 FPS，例如 9 月 2 日 09:10:01 的 CacheON/GammaSKIP/Bake30/MergeOFF 约 13.97 FPS。因此不能把所有旧记录概括为固定 20 多 FPS。",
    "- Merge 的多材质/纹理不兼容回退仍存在，可能改变实际 Renderer 数；本次没有 MergeON 采样，不能据此认定它造成当前差值。",
    "- 逐项核对当前 30 个压测 Prefab：每个内部的粒子/拖尾使用同一材质，UVModule 均未启用，AnimatableProperties 为空，均符合当前 CanMerge 的兼容条件。没有发现本压测资源被该兼容门限整体禁用合并；网格超限回退仍需看运行时实际顶点数。",
    "- 源码路径核对：Group Cache 的字典查询分支、GammaSKIP 的 Canvas 标志与顶点循环跳过、RenderCull/FullCull、暂停时的 Static Cache 和空闲快速路径仍在。Bake30 是本轮已确认并纠正的优化行为退化；其余路径是否增加耗时仍需要运行时分项数据，不能仅凭保留代码判定性能不变。",
    "- CSV 未独立记录实际 Sharing 状态、暂停状态或源码版本；CaseName 中的 MeshSharing 是场景名称。StaticON 的提升也不能脱离暂停状态解释。多段仅 1–8 秒，容易受短时波动影响。",
    "",
    "验证：49 项离线托管回归通过；5 个 Editor 配置程序集和 3 个 Player 配置程序集编译通过。调度回放单独通过。没有启动 Unity、重新打包/安装或在模拟器切换压测；设备上的 APK 仍为纠正前版本，尚不能宣称 FPS 已恢复。",
    "",
    "后续有效对照需要相同 APK 配置、156 实例、相同 Sharing/Merge/Gamma/裁剪/暂停状态下的 Bake60 与 Bake30 样本，记录完整预热和采样窗口，核对实际顶点量及烘焙次数。",
])
(CAPTURE / "PerformanceRegression.md").write_text("\n".join(text) + "\n", encoding="utf-8")
print(f"Verified {len(records)} CSV headers, row counts and frame continuity; report saved to {CAPTURE / 'PerformanceRegression.md'}")
