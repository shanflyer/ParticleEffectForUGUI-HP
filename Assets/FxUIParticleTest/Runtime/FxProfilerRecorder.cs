using System.Collections.Generic;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Coffee.UIExtensions;
using UnityEngine;

namespace FxUIParticleTest
{
    /// <summary>
    /// 轻量采集器:CPU 帧时间(ms)/ GC(字节)/ 渲染统计(Editor 用 UnityStats 填充)。
    /// 切换 Case 自动重开采集,满 recordSeconds 或再次切换/退出时写一份 CSV(含 mean/p50/p95)。
    /// </summary>
    public class FxProfilerRecorder : MonoBehaviour
    {
        public float recordSeconds = 30f;
        public float warmupSeconds = 5f;
        // Diagnostic runs only. Leave disabled for comparable performance baselines.
        public bool captureDetailedTimings;
        IDisposable _timingRequest;
        const int MaxSampleFrames = 36000;
        static readonly CultureInfo CsvCulture = CultureInfo.InvariantCulture;

        static readonly string[] StatNames =
        {
            "cpu_frame_ms", "cpu_work_ms", "gc_bytes", "draw_calls", "batches", "setpass", "triangles", "vertices",
        };

        ProfilerRecorder _cpu;
        ProfilerRecorder _cpuWork;
        ProfilerRecorder _gc;
        readonly ProfilerRecorder[] _renderStats = new ProfilerRecorder[5];
        static readonly string[] RenderStatNames =
        {
            "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count",
        };
        const string CsvHeader = "frame,time_s,cpu_frame_ms,cpu_work_ms,gc_bytes,draw_calls,batches,setpass,triangles,vertices,particle_frame,prepare_ms,simulate_ms,bake_ms,combine_ms,submit_ms,bake_ops,baked_vertices,setmesh_ops,material_updates,meshes_created,active_renderers,merged_renderers,fallback_effects,timing_enabled,bridge_cache_hits,bridge_compare_ms,mask_mesh_submissions,mask_resolve_cache_hits";
        struct Sample
        {
            public int frame;
            public float time;
            public double cpu, work;
            public long gc, draws, batches, setpass, triangles, vertices;
            public UIParticleProfiler.Frame particle;
            public double Column(int column)
            {
                switch (column)
                {
                    case 0: return cpu; case 1: return work; case 2: return gc;
                    case 3: return draws; case 4: return batches; case 5: return setpass;
                    case 6: return triangles; default: return vertices;
                }
            }
        }
        readonly List<Sample> _rows = new List<Sample>(2048);
        int _validCounters;

        string _caseName = "Manual";
        float _startTime;
        int _startFrame;
        bool _recording;
        bool _started;
        bool _sampling;
        float _sampleStart;
        int _sampleStartFrame;

        public bool IsRecording => _recording;
        public bool IsSampling => _recording && _sampling;

        void Update()
        {
            if (!_recording) return;
            if (!_sampling)
            {
                // 预热期:等粒子进入稳态再采样,保证 A/B 两侧窗口状态一致
                if (Time.unscaledTime - _startTime >= warmupSeconds)
                {
                    _sampling = true;
                    _sampleStart = Time.unscaledTime;
                    _sampleStartFrame = Time.frameCount;
                }
                return;
            }
            if (Time.unscaledTime - _sampleStart >= recordSeconds || _rows.Count >= MaxSampleFrames) StopAndWrite();
        }

        void LateUpdate()
        {
            if (!_recording || !_sampling || _rows.Count >= MaxSampleFrames) return;
            var sample = new Sample
            {
                frame = Time.frameCount - _sampleStartFrame,
                time = Time.unscaledTime - _sampleStart,
                cpu = _cpu.Valid ? _cpu.LastValue * 1e-6 : 0,
                work = _cpuWork.Valid ? _cpuWork.LastValue * 1e-6 : 0,
                gc = _gc.Valid ? _gc.LastValue : 0,
                particle = UIParticleProfiler.completed
            };
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            sample.draws = UnityEditor.UnityStats.drawCalls;
            sample.batches = UnityEditor.UnityStats.batches;
            sample.setpass = UnityEditor.UnityStats.setPassCalls;
            sample.triangles = UnityEditor.UnityStats.triangles;
            sample.vertices = UnityEditor.UnityStats.vertices;
#else
            sample.draws = _renderStats[0].Valid ? _renderStats[0].LastValue : 0;
            sample.batches = _renderStats[1].Valid ? _renderStats[1].LastValue : 0;
            sample.setpass = _renderStats[2].Valid ? _renderStats[2].LastValue : 0;
            sample.triangles = _renderStats[3].Valid ? _renderStats[3].LastValue : 0;
            sample.vertices = _renderStats[4].Valid ? _renderStats[4].LastValue : 0;
#endif
            _rows.Add(sample);
        }

        void OnDisable()
        {
            try { if (_recording) StopAndWrite(); }
            finally { StopCounters(); }
        }

        void OnDestroy() { StopCounters(); }

        // Every successfully started native recorder must be disposed, including
        // partial initialization and component-disable paths.
        void StopCounters()
        {
            _timingRequest?.Dispose();
            _timingRequest = null;
            DisposeCounter(ref _cpu);
            DisposeCounter(ref _cpuWork);
            DisposeCounter(ref _gc);
            for (var i = 0; i < _renderStats.Length; i++)
            {
                DisposeCounter(ref _renderStats[i]);
            }
            _started = false;
        }

        static void DisposeCounter(ref ProfilerRecorder recorder)
        {
            try { if (recorder.Valid) recorder.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
            finally { recorder = default; }
        }

        public void BeginRecording(string caseName)
        {
            if (!isActiveAndEnabled) return;
            if (_recording) StopAndWrite();
            if (_started)
            {
                StopCounters();
                _started = false;
            }

            recordSeconds = float.IsNaN(recordSeconds) || float.IsInfinity(recordSeconds)
                ? 30f : Mathf.Clamp(recordSeconds, 0.1f, 600f);
            warmupSeconds = float.IsNaN(warmupSeconds) || float.IsInfinity(warmupSeconds)
                ? 5f : Mathf.Clamp(warmupSeconds, 0, 600f);
            _caseName = caseName ?? "Manual";
            _rows.Clear();
            try
            {
                _cpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
                _cpuWork = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time (excluding vsync)");
                _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
#if !UNITY_EDITOR || UNITY_6000_0_OR_NEWER
                for (var i = 0; i < _renderStats.Length; i++)
                    _renderStats[i] = ProfilerRecorder.StartNew(ProfilerCategory.Render, RenderStatNames[i]);
#endif
                if (captureDetailedTimings) _timingRequest = UIParticleProfiler.BeginDetailedTiming();
            }
            catch (Exception e)
            {
                StopCounters();
                _rows.Clear();
                Debug.LogException(e);
                return;
            }
            _validCounters = (_cpu.Valid ? 1 : 0) | (_cpuWork.Valid ? 2 : 0) | (_gc.Valid ? 4 : 0);
            for (var i = 0; i < _renderStats.Length; i++)
                if (_renderStats[i].Valid) _validCounters |= 1 << (i + 3);
            // Use bounded counters without enabling the full profiler capture.
            _recording = true;
            _started = true;
            _sampling = false;
            _startTime = Time.unscaledTime;
            _startFrame = Time.frameCount;
            Debug.Log($"[FxProfilerRecorder] 开始采集:{caseName} 预热{warmupSeconds:F0}s + 采样{recordSeconds:F0}s");
        }

        public void StopAndWrite()
        {
            if (!_recording) return;
            _recording = false;
            if (_started)
            {
                StopCounters();
                _started = false;
            }

            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "FxUIParticleBaseline");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}_{Sanitize(_caseName)}.csv");
                var frames = _rows.Count;
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine($"# device={SystemInfo.deviceModel} gpu={SystemInfo.graphicsDeviceName} " +
                                $"unity={Application.unityVersion} case={_caseName} frames={frames} warmup={warmupSeconds.ToString("F0", CsvCulture)}s implementation={UIParticleProfiler.Implementation} validCounters={_validCounters} timingValidity=per-row");
                    w.WriteLine(CsvHeader);
                    var line = new StringBuilder(256);
                    for (var i = 0; i < _rows.Count; i++)
                    {
                        var row = _rows[i];
                        line.Clear();
                        line.Append(row.frame).Append(',').Append(row.time.ToString("F4", CsvCulture));
                        for (var col = 0; col < 8; col++)
                            line.Append(',').Append(row.Column(col).ToString(col < 2 ? "F3" : "F0", CsvCulture));
                        var q = row.particle;
                        line.Append(',').Append(q.frame);
                        line.Append(',').Append(q.prepareMs.ToString("F4", CsvCulture));
                        line.Append(',').Append(q.simulateMs.ToString("F4", CsvCulture));
                        line.Append(',').Append(q.bakeMs.ToString("F4", CsvCulture));
                        line.Append(',').Append(q.combineMs.ToString("F4", CsvCulture));
                        line.Append(',').Append(q.submitMs.ToString("F4", CsvCulture));
                        line.Append(',').Append(q.bakeOps).Append(',').Append(q.bakedVertices);
                        line.Append(',').Append(q.setMeshOps).Append(',').Append(q.materialUpdates);
                        line.Append(',').Append(q.meshesCreated).Append(',').Append(q.activeRenderers);
                        line.Append(',').Append(q.mergedRenderers).Append(',').Append(q.fallbackEffects);
                        line.Append(',').Append(q.detailedTiming ? 1 : 0);
                        line.Append(',').Append(q.bridgeCacheHits);
                        line.Append(',').Append(q.bridgeCompareMs.ToString("F4", CsvCulture));
                        line.Append(',').Append(q.maskMeshSubmissions).Append(',').Append(q.maskResolveCacheHits);
                        w.WriteLine(line.ToString());
                    }
                    w.Write(BuildSummary());
                }
                Debug.Log($"[FxProfilerRecorder] {frames} 帧 → {path}");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Debug.LogError($"[FxProfilerRecorder] CSV 保存失败，计数器已释放: {e.Message}");
            }
            finally { _rows.Clear(); }
        }

        string BuildSummary()
        {
            var sb = new StringBuilder("# SUMMARY");
            for (var col = 0; col < 8; col++)
            {
                var vals = new List<double>(_rows.Count);
                for (var i = 0; i < _rows.Count; i++) vals.Add(_rows[i].Column(col));
                if (vals.Count == 0) continue;
                vals.Sort();
                double mean = 0;
                foreach (var v in vals) mean += v;
                mean /= vals.Count;
                var p50 = vals[vals.Count / 2];
                var p95 = vals[Mathf.Clamp((int)(vals.Count * 0.95f), 0, vals.Count - 1)];
                sb.Append("\n# ").Append(StatNames[col]).Append(": mean=").Append(mean.ToString("G", CsvCulture))
                    .Append(" p50=").Append(p50.ToString("G", CsvCulture)).Append(" p95=")
                    .Append(p95.ToString("G", CsvCulture)).Append(" (n=").Append(vals.Count).Append(')');
            }
            return sb.ToString();
        }

        static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString(0, Math.Min(80, sb.Length));
        }
    }
}
