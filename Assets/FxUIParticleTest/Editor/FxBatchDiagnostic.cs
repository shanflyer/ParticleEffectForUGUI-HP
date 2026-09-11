using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace FxUIParticleTest
{
    /// <summary>
    /// 合批诊断(只读,不改任何状态):统计场景内所有 Graphic(UIParticleRenderer 是
    /// internal 的 MaskableGraphic,用类型名识别)的 materialForRendering 去重实例数、
    /// CanvasRenderer.materialCount 分布,以及按 Canvas 层级遍历序的材质"游程"数——
    /// 游程数是理想合批下的 DC 下限近似(UGUI 合批只合并排序流中连续的同材质元素)。
    /// 用法:Play 模式跑 G 档几秒后,点菜单 FxUIParticle > 5. Batch Diagnostic (Editor)。
    /// </summary>
    public static class FxBatchDiagnostic
    {
        [MenuItem("FxUIParticle/5. Batch Diagnostic (Editor)")]
        public static void Run()
        {
            var graphics = Object.FindObjectsOfType<Graphic>(false);
            if (graphics.Length == 0)
            {
                Debug.Log("[FxBatchDiagnostic] 场景里没有 Graphic。先 Play 跑 G 档几秒再点本菜单。");
                return;
            }

            var matNames = new Dictionary<int, string>();
            var particleMat = new Dictionary<int, int>();
            var allMat = new Dictionary<int, int>();
            var crMatCountDist = new Dictionary<int, int>();
            var rootCanvases = new HashSet<Canvas>();
            int particleCount = 0;

            foreach (var g in graphics)
            {
                var isParticle = g.GetType().Name == "UIParticleRenderer";
                if (isParticle) particleCount++;

                if (isParticle && g.canvasRenderer != null)
                {
                    var mc = g.canvasRenderer.materialCount;
                    crMatCountDist.TryGetValue(mc, out var c);
                    crMatCountDist[mc] = c + 1;
                }

                var m = g.materialForRendering;
                var id = m != null ? m.GetInstanceID() : 0;
                if (!matNames.ContainsKey(id)) matNames[id] = m != null ? m.name : "<null>";
                allMat.TryGetValue(id, out var ac);
                allMat[id] = ac + 1;
                if (isParticle)
                {
                    particleMat.TryGetValue(id, out var pc);
                    particleMat[id] = pc + 1;
                }

                if (g.canvas != null)
                {
                    rootCanvases.Add(g.canvas.rootCanvas);
                }
            }

            // 层级遍历序材质游程:UGUI 按排序流做连续同材质合并,游程数 = 理想 DC 下限近似。
            var allSeq = new List<int>();
            var particleSeq = new List<int>();
            int nestedCanvasCount = 0;
            foreach (var root in rootCanvases)
            {
                Walk(root.transform, root, allSeq, particleSeq, matNames, ref nestedCanvasCount);
            }

            var sb = new StringBuilder();
            sb.AppendLine("[FxBatchDiagnostic] ===== 合批诊断 =====");
            sb.Append("Canvas: root x").Append(rootCanvases.Count)
              .Append(", 嵌套 x").Append(nestedCanvasCount).AppendLine();
            sb.Append("Graphic 总数: ").Append(graphics.Length)
              .Append(" (粒子渲染器 ").Append(particleCount)
              .Append(" / 普通 UI ").Append(graphics.Length - particleCount).AppendLine(")");
            sb.Append("materialForRendering 去重: 粒子 ").Append(particleMat.Count)
              .Append(" 种 / 全部 ").Append(allMat.Count).AppendLine(" 种");

            sb.AppendLine("粒子材质分布 TOP:");
            AppendTop(sb, particleMat, matNames);

            sb.Append("粒子 CR materialCount 分布: ");
            foreach (var kv in crMatCountDist) sb.Append(kv.Key).Append("个材质 x").Append(kv.Value).Append("  ");
            sb.AppendLine();

            sb.Append("全 Graphic 材质游程: ").Append(CountRuns(allSeq))
              .Append(" 次(元素 ").Append(allSeq.Count).Append(" → 理想 DC 下限 ≈ 游程数 + 顶点上限切分)");
            sb.AppendLine();
            sb.Append("仅粒子 材质游程: ").Append(CountRuns(particleSeq))
              .Append(" 次(元素 ").Append(particleSeq.Count).AppendLine(")");
            sb.AppendLine("解读:游程 ≈ 元素数 → 材质在排序流里完全交错,合批为零;游程 ≈ 材质种数 → 已按材质聚簇,DC 只受顶点上限切分。");

            Debug.Log(sb.ToString());
        }

        private static void Walk(
            Transform t, Canvas root, List<int> allSeq, List<int> particleSeq,
            Dictionary<int, string> matNames, ref int nestedCanvasCount)
        {
            if (!t.Equals(root.transform) && t.TryGetComponent<Canvas>(out var nested))
            {
                nestedCanvasCount++;
            }

            if (t.TryGetComponent<Graphic>(out var g) && g.isActiveAndEnabled && g.canvasRenderer != null)
            {
                var m = g.materialForRendering;
                var id = m != null ? m.GetInstanceID() : 0;
                if (!matNames.ContainsKey(id)) matNames[id] = m != null ? m.name : "<null>";
                allSeq.Add(id);
                if (g.GetType().Name == "UIParticleRenderer") particleSeq.Add(id);
            }

            var childCount = t.childCount;
            for (var i = 0; i < childCount; i++)
            {
                Walk(t.GetChild(i), root, allSeq, particleSeq, matNames, ref nestedCanvasCount);
            }
        }

        private static int CountRuns(List<int> seq)
        {
            if (seq.Count == 0) return 0;
            var runs = 1;
            for (var i = 1; i < seq.Count; i++)
            {
                if (seq[i] != seq[i - 1]) runs++;
            }

            return runs;
        }

        private static void AppendTop(StringBuilder sb, Dictionary<int, int> counts, Dictionary<int, string> names)
        {
            var items = new List<KeyValuePair<int, int>>(counts);
            items.Sort((a, b) => b.Value.CompareTo(a.Value));
            var top = Mathf.Min(8, items.Count);
            for (var i = 0; i < top; i++)
            {
                var kv = items[i];
                sb.Append("  ").Append(names.TryGetValue(kv.Key, out var n) ? n : "?")
                  .Append("  x").Append(kv.Value).AppendLine();
            }
        }
    }
}
