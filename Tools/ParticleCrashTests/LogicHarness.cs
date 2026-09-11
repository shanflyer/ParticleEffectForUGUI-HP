// Managed stand-ins only: no Unity/native profiler/graphics code is executed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Coffee.UIExtensions;
using Coffee.UIParticleInternal;
using FxUIParticleTest;

namespace UnityEngine
{
    public class MonoBehaviour { public bool isActiveAndEnabled = true; }
    public static class Time { public static float unscaledTime; public static int frameCount; }
    public static class Mathf
    {
        public static float Clamp(float x, float a, float b) => Math.Min(b, Math.Max(a, x));
        public static int Clamp(int x, int a, int b) => Math.Min(b, Math.Max(a, x));
    }
    public static class Application { public static string persistentDataPath; public static string unityVersion = "managed-test"; }
    public static class SystemInfo { public static string deviceModel = "test", graphicsDeviceName = "none"; }
    public static class Debug
    {
        public static int errors;
        public static void Log(object x) { }
        public static void LogException(Exception x) { errors++; }
        public static void LogError(object x) { errors++; }
    }
}
namespace UnityEngine.Profiling
{
    public static class Profiler { public static void BeginSample(string x) { } public static void EndSample() { } }
}
namespace Unity.Profiling
{
    public struct ProfilerCategory { public static ProfilerCategory Internal, Memory, Render; }
    public struct ProfilerRecorder
    {
        static int nextId;
        public static int starts, failAt;
        public static readonly HashSet<int> live = new HashSet<int>();
        int id;
        public bool Valid => live.Contains(id);
        public long LastValue => 12500000;
        public static ProfilerRecorder StartNew(ProfilerCategory c, string name)
        {
            if (++starts == failAt) throw new InvalidOperationException("Injected recorder startup failure");
            var result = new ProfilerRecorder { id = ++nextId }; live.Add(result.id); return result;
        }
        public void Dispose() { live.Remove(id); }
    }
}
namespace Coffee.UIParticleInternal
{
    internal static class Logger { public static void Log(object x, string y) { } }
}
static class LogicHarness
{
    static int passed, failed;
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Near(float a, float b) { Assert(Math.Abs(a - b) < 0.0001, $"{a} != {b}"); }
    static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static object Call(object obj, string method)
        => obj.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, null);
    static T Field<T>(object obj, string field)
        => (T)obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
    static FxProfilerRecorder Recorder(string name)
    {
        Unity.Profiling.ProfilerRecorder.failAt = 0;
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "TempDiag/crash_fix_compile/recorder-tests", name);
        Directory.CreateDirectory(folder);
        UnityEngine.Application.persistentDataPath = folder;
        return new FxProfilerRecorder { warmupSeconds = 0 };
    }
    static int Main()
    {
        Test("bake clock conserves elapsed scaled and unscaled time", () => {
            var clock = new ParticleBakeClock(); float scaled = 0, real = 0, expected = 0;
            for (var i = 0; i < 80; i++)
            {
                var dt = i % 3 == 0 ? 0.023f : 0.011f; expected += dt;
                if (clock.Advance(dt * 0.5f, dt, 30, false, out var s, out var u)) { scaled += s; real += u; }
            }
            clock.Advance(0, 0, 30, true, out var tailS, out var tailU);
            Near(real + tailU, expected); Near(scaled + tailS, expected * 0.5f);
        });
        Test("forced redraw does not invent simulation time", () => {
            var clock = new ParticleBakeClock();
            clock.Advance(.016f, .016f, 30, true, out var s, out var u); Near(s, .016f);
            clock.Advance(0, 0, 30, true, out s, out u); Near(s, 0); Near(u, 0);
        });
        Test("unscaled baking continues while scaled time is stopped", () => {
            var clock = new ParticleBakeClock();
            clock.Advance(0, .016f, 30, false, out var s, out var u); Near(s, 0); Near(u, .016f);
            Assert(!clock.Advance(0, .02f, 30, false, out s, out u), "gate did not close");
            Assert(clock.Advance(0, .02f, 30, false, out s, out u), "unscaled gate stuck"); Near(s, 0); Near(u, .04f);
        });
        Test("bake mode changes consume pending time exactly once", () => {
            var clock = new ParticleBakeClock(); clock.Advance(.01f, .01f, 30, false, out _, out _);
            clock.Advance(.01f, .01f, 30, false, out _, out _);
            Assert(clock.Advance(.01f, .01f, 0, false, out var s, out _), "OFF did not bake"); Near(s, .02f);
            clock.Advance(.01f, .01f, 0, false, out s, out _); Near(s, .01f);
        });
        Test("invalid deltas never enter simulation", () => {
            var clock = new ParticleBakeClock(); clock.Advance(float.NaN, float.PositiveInfinity, 30, true, out var s, out var u);
            Near(s, 0); Near(u, 0); clock.Advance(-1, -1, 30, true, out s, out u); Near(s, 0); Near(u, 0);
        });
        Test("Bake30 still halves bake work at 14 FPS without slowing simulation", () => {
            var clock = new ParticleBakeClock(); int bakes = 0; float simulated = 0;
            for (var i = 0; i < 28; i++)
                if (clock.Advance(1f / 14, 1f / 14, 30, false, out var s, out _)) { bakes++; simulated += s; }
            Assert(bakes == 14, $"expected 14 bakes in 28 frames, got {bakes}");
            clock.Advance(0, 0, 30, true, out var pending, out _); Near(simulated + pending, 2);
        });
        Test("Bake30 caps high frame rate work near 30 Hz", () => {
            var clock = new ParticleBakeClock(); int bakes = 0; float simulated = 0;
            for (var i = 0; i < 120; i++)
                if (clock.Advance(1f / 120, 1f / 120, 30, false, out var s, out _)) { bakes++; simulated += s; }
            Assert(bakes == 30, $"expected 30 bakes, got {bakes}");
            clock.Advance(0, 0, 30, true, out var pending, out _); Near(simulated + pending, 1);
        });
        Test("renderer phases spread low frame rate bakes without inventing time", () => {
            var a = new ParticleBakeClock(); var b = new ParticleBakeClock();
            a.Advance(.08f, .08f, 30, true, out _, out _, 0);
            b.Advance(.08f, .08f, 30, true, out var s, out _, .5f); Near(s, .08f);
            Assert(!a.Advance(.08f, .08f, 30, false, out _, out _, 0), "phase A did not wait");
            Assert(b.Advance(.08f, .08f, 30, false, out s, out _, .5f), "phase B did not stagger"); Near(s, .08f);
            Assert(a.Advance(.08f, .08f, 30, false, out s, out _, 0), "phase A stuck"); Near(s, .16f);
            Assert(!b.Advance(.08f, .08f, 30, false, out _, out _, .5f), "phase B did not wait");
        });
        Test("adaptive bake scheduling conserves time through hitches and mode changes", () => {
            var clock = new ParticleBakeClock(); float expected = 0, scaled = 0, unscaled = 0;
            for (var i = 0; i < 240; i++)
            {
                var dt = i % 37 == 0 ? .3f : i % 2 == 0 ? .015f : .076f;
                expected += dt;
                if (clock.Advance(dt * .25f, dt, i % 19 == 0 ? 0 : 30, i % 23 == 0, out var s, out var u, .7f))
                { scaled += s; unscaled += u; }
            }
            clock.Advance(0, 0, 0, true, out var tailS, out var tailU);
            Near(scaled + tailS, expected * .25f); Near(unscaled + tailU, expected);
        });
        Test("callback self-removal cannot skip remaining callbacks", () => {
            var action = new FastAction(); int count = 0; Action first = null;
            first = () => { action.Remove(first); count++; };
            action.Add(first); action.Add(() => count++); action.Invoke(); Assert(count == 2, "remaining callback skipped");
        });
        Test("callbacks added during invocation start next invocation", () => {
            var action = new FastAction(); int count = 0; bool added = false;
            action.Add(() => { if (!added) { added = true; action.Add(() => count++); } });
            action.Invoke(); Assert(count == 0, "new callback ran in same pass"); action.Invoke(); Assert(count == 1, "new callback lost");
        });
        Test("throwing callback does not block later callback", () => {
            var action = new FastAction(); int count = 0; action.Add(() => throw new Exception("injected"));
            action.Add(() => count++); action.Invoke(); Assert(count == 1, "exception stopped dispatch");
        });
        Test("partial recorder startup disposes acquired native handles", () => {
            var recorder = Recorder("partial"); Unity.Profiling.ProfilerRecorder.failAt = Unity.Profiling.ProfilerRecorder.starts + 3;
            recorder.BeginRecording("partial"); Assert(!recorder.IsRecording && Unity.Profiling.ProfilerRecorder.live.Count == 0, "partial startup leaked");
        });
        Test("repeat recording and disable release all counters", () => {
            var recorder = Recorder("repeat"); recorder.BeginRecording("a"); var active = Unity.Profiling.ProfilerRecorder.live.Count;
            recorder.BeginRecording("a"); Assert(Unity.Profiling.ProfilerRecorder.live.Count == active, "restart leaked handles");
            Call(recorder, "OnDisable"); Assert(Unity.Profiling.ProfilerRecorder.live.Count == 0 && !recorder.IsRecording, "disable leaked handles");
        });
        Test("disabled recorder does not start native counters", () => {
            var recorder = Recorder("disabled"); recorder.isActiveAndEnabled = false; recorder.BeginRecording("disabled");
            Assert(!recorder.IsRecording && Unity.Profiling.ProfilerRecorder.live.Count == 0, "disabled recorder started");
        });
        Test("CSV uses invariant numeric columns and unique filenames", () => {
            var recorder = Recorder("locale"); var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                for (var i = 0; i < 2; i++)
                {
                    recorder.BeginRecording("same"); Call(recorder, "Update");
                    UnityEngine.Time.unscaledTime += .5f; Call(recorder, "LateUpdate"); recorder.StopAndWrite();
                }
                var files = Directory.GetFiles(Path.Combine(UnityEngine.Application.persistentDataPath, "FxUIParticleBaseline"), "*.csv");
                Assert(files.Length >= 2, "same-second captures overwritten");
                var row = File.ReadAllLines(files.Last()).First(x => x.Length > 0 && char.IsDigit(x[0]));
                Assert(row.Split(',').Length == 25 && row.Contains("12.500"), "locale split CSV numbers");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("CSV IO failure leaves recorder stopped and buffers released", () => {
            var recorder = Recorder("io-error"); var file = Path.Combine(UnityEngine.Application.persistentDataPath, "file");
            File.WriteAllText(file, "test"); UnityEngine.Application.persistentDataPath = file;
            recorder.BeginRecording("write-error"); recorder.StopAndWrite();
            Assert(!recorder.IsRecording && Unity.Profiling.ProfilerRecorder.live.Count == 0 && Field<System.Collections.IList>(recorder, "_rows").Count == 0, "IO failure leaked resources");
        });
        Test("non-finite capture duration is bounded at startup", () => {
            var recorder = Recorder("bounds"); recorder.recordSeconds = float.NaN; recorder.warmupSeconds = float.PositiveInfinity;
            recorder.BeginRecording(null); Assert(recorder.recordSeconds == 30 && recorder.warmupSeconds == 5, "unbounded duration");
            Call(recorder, "OnDisable");
        });
        Test("sample buffer has a hard frame limit even if duration changes", () => {
            var recorder = Recorder("sample-limit"); recorder.BeginRecording("bounded"); Call(recorder, "Update");
            recorder.recordSeconds = float.PositiveInfinity;
            var limit = (int)typeof(FxProfilerRecorder).GetField("MaxSampleFrames", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            var rows = Field<System.Collections.IList>(recorder, "_rows");
            // Repeated references use only a small managed list; no native sampling or pressure loop.
            var empty = Activator.CreateInstance(rows.GetType().GetGenericArguments()[0]);
            for (var i = 0; i < limit; i++) rows.Add(empty); var count = rows.Count;
            Call(recorder, "LateUpdate"); Assert(rows.Count == count, "sample buffer exceeded its limit");
            rows.Clear(); Call(recorder, "OnDestroy");
        });
        Test("steady-state sampling performs no managed allocation", () => {
            var recorder = Recorder("allocation"); recorder.BeginRecording("typed rows"); Call(recorder, "Update");
            var method = typeof(FxProfilerRecorder).GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
            var sample = (Action)Delegate.CreateDelegate(typeof(Action), recorder, method);
            for (var i = 0; i < 128; i++) sample();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 512; i++) sample();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated == 0, $"sampling allocated {allocated} bytes");
            Call(recorder, "OnDisable");
        });
        Test("CSV frame count matches typed samples and diagnostic columns", () => {
            var recorder = Recorder("schema");
            UIParticleProfiler.BeginFrame(123); UIParticleProfiler.current.bakeOps = 7; UIParticleProfiler.EndFrame();
            recorder.BeginRecording("schema"); Call(recorder, "Update"); Call(recorder, "LateUpdate"); recorder.StopAndWrite();
            var folder = Path.Combine(UnityEngine.Application.persistentDataPath, "FxUIParticleBaseline");
            var file = Directory.GetFiles(folder).OrderBy(File.GetLastWriteTimeUtc).Last();
            var lines = File.ReadAllLines(file);
            Assert(lines[0].Contains("frames=1 ") && lines[0].Contains("implementation="), "capture metadata incorrect");
            var row = lines.First(x => x.Length > 0 && char.IsDigit(x[0])).Split(',');
            Assert(row.Length == 25 && row[10] == "123" && row[16] == "7" && row[24] == "0", "diagnostic schema or frame alignment incorrect");
        });
        Test("default particle profiling keeps counters without timing", () => {
            UIParticleProfiler.BeginFrame(400);
            Assert(!UIParticleProfiler.timingEnabled && UIParticleProfiler.Timestamp() == 0, "default clock enabled");
            UIParticleProfiler.current.bakeOps = 9;
            UIParticleProfiler.EndStage(2, 0);
            Assert(UIParticleProfiler.Milliseconds(0) == 0, "disabled elapsed timing was sampled");
            UIParticleProfiler.EndFrame();
            Assert(UIParticleProfiler.completed.bakeOps == 9 && UIParticleProfiler.completed.bakeMs == 0, "counters lost or fake timing");
        });
        Test("overlapping timing requests are latched per frame and disposed once", () => {
            UIParticleProfiler.BeginFrame(401);
            var first = UIParticleProfiler.BeginDetailedTiming();
            var second = UIParticleProfiler.BeginDetailedTiming();
            Assert(!UIParticleProfiler.timingEnabled, "request changed partial frame");
            UIParticleProfiler.EndFrame(); UIParticleProfiler.BeginFrame(402);
            Assert(UIParticleProfiler.timingEnabled && UIParticleProfiler.Timestamp() != 0, "timing did not start");
            first.Dispose(); first.Dispose();
            UIParticleProfiler.EndFrame(); UIParticleProfiler.BeginFrame(403);
            Assert(UIParticleProfiler.timingEnabled, "one requester stopped another");
            second.Dispose();
            Assert(UIParticleProfiler.timingEnabled, "dispose corrupted partial frame");
            UIParticleProfiler.EndFrame(); UIParticleProfiler.BeginFrame(404);
            Assert(!UIParticleProfiler.timingEnabled, "timing request leaked"); UIParticleProfiler.EndFrame();
        });
        Test("diagnostic recorder releases timing after restart disable and failed startup", () => {
            var recorder = Recorder("timing-lifetime"); recorder.captureDetailedTimings = true;
            recorder.BeginRecording("first"); recorder.BeginRecording("second");
            UIParticleProfiler.BeginFrame(410); Assert(UIParticleProfiler.timingEnabled, "diagnostic recorder has no lease"); UIParticleProfiler.EndFrame();
            Call(recorder, "OnDisable");
            UIParticleProfiler.BeginFrame(411); Assert(!UIParticleProfiler.timingEnabled, "restart or disable leaked timing"); UIParticleProfiler.EndFrame();
            Unity.Profiling.ProfilerRecorder.failAt = Unity.Profiling.ProfilerRecorder.starts + 2;
            recorder.BeginRecording("failed");
            UIParticleProfiler.BeginFrame(412); Assert(!UIParticleProfiler.timingEnabled, "startup failure leaked timing"); UIParticleProfiler.EndFrame();
            Unity.Profiling.ProfilerRecorder.failAt = 0;
        });
        Test("CSV flags actual completed particle frame timing", () => {
            var recorder = Recorder("timing-schema"); recorder.captureDetailedTimings = true;
            recorder.BeginRecording("timed"); Call(recorder, "Update");
            UIParticleProfiler.BeginFrame(420); UIParticleProfiler.EndFrame();
            Call(recorder, "LateUpdate"); recorder.StopAndWrite();
            var folder = Path.Combine(UnityEngine.Application.persistentDataPath, "FxUIParticleBaseline");
            var file = Directory.GetFiles(folder).OrderBy(File.GetLastWriteTimeUtc).Last();
            var row = File.ReadAllLines(file).First(x => x.Length > 0 && char.IsDigit(x[0])).Split(',');
            Assert(row[10] == "420" && row[24] == "1", "timing validity does not match snapshot");
            UIParticleProfiler.BeginFrame(421); Assert(!UIParticleProfiler.timingEnabled, "stop leaked timing"); UIParticleProfiler.EndFrame();
        });
        Test("changing work phase cannot lose or duplicate pending simulation", () => {
            var clock = new ParticleBakeClock(); float elapsed = 0, simulated = 0;
            for (var i = 0; i < 80; i++)
            {
                elapsed += .07f;
                if (clock.Advance(.07f, .07f, 30, false, out var step, out _, i < 40 ? .25f : .75f)) simulated += step;
            }
            clock.Advance(0, 0, 30, true, out var tail, out _); Near(elapsed, simulated + tail);
        });
        Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
    }
}
