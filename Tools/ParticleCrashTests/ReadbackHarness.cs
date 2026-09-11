// Counter body is extracted verbatim from OverDrawRenderFeature.cs by the runner.
// Native buffers/readback requests are replaced with these managed test doubles.
using System;
using UnityEngine;
using UnityEngine.Rendering;
namespace UnityEngine
{
    public class ComputeBuffer
    {
        public bool released;
        public ComputeBuffer(int count, int stride) { }
        public void Release() { released = true; }
    }
}
namespace UnityEngine.Rendering
{
    public struct AsyncGPUReadbackRequest
    {
        public bool hasError;
        public uint[] values;
        public T[] GetData<T>() => (T[])(object)values;
    }
}
static class ReadbackHarness
{
    static int passed, failed;
    static void Assert(bool value, string text) { if (!value) throw new Exception(text); }
    static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static int Main()
    {
        Test("pending readback retains buffer through pass disposal", () => {
            var counter = new OverdrawCounter(); var buffer = counter.ComputeBuffer; counter.dirty = true;
            counter.Release(); Assert(!buffer.released, "freed in-flight buffer");
            counter.CompleteReadback(new AsyncGPUReadbackRequest { values = new uint[4] });
            Assert(buffer.released && !counter.dirty, "completion did not release buffer");
        });
        Test("readback error still releases a disposed buffer", () => {
            var counter = new OverdrawCounter(); var buffer = counter.ComputeBuffer; counter.dirty = true;
            counter.Release(); counter.CompleteReadback(new AsyncGPUReadbackRequest { hasError = true });
            Assert(buffer.released && !counter.dirty && !counter.hasResult, "error leaked buffer or published data");
        });
        Test("completed result is copied before request storage expires", () => {
            var counter = new OverdrawCounter { dirty = true }; var values = new uint[] { 2, 1, 3, 3 };
            counter.CompleteReadback(new AsyncGPUReadbackRequest { values = values }); values[2] = 99;
            Assert(counter.hasResult && !counter.dirty && counter.overDrawArray[2] == 3, "retained request-owned storage");
            counter.Release();
        });
        Test("unsubmitted command cannot leave a counter busy forever", () => {
            var counter = new OverdrawCounter { dirty = true }; var buffer = counter.ComputeBuffer;
            counter.CancelUnsubmitted(); counter.Release(); Assert(buffer.released && !counter.dirty, "unsubmitted counter leaked");
        });
        Test("disposed counter cannot lazily recreate a GPU buffer", () => {
            var counter = new OverdrawCounter(); counter.Release();
            try { var buffer = counter.ComputeBuffer; throw new Exception("recreated a disposed buffer"); }
            catch (ObjectDisposedException) { }
        });
        Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
    }
}
