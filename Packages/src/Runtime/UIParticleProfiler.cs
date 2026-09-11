using System.Diagnostics;

namespace Coffee.UIExtensions
{
    // Value-only counters. Sampling never enables Unity's full profiler or allocates per frame.
    public static class UIParticleProfiler
    {
        public const string Implementation = "particle-system-count-20260908-v6";
        public struct Frame
        {
            public int frame, bakeOps, setMeshOps, materialUpdates, meshesCreated;
            public int activeRenderers, mergedRenderers, fallbackEffects;
            public long bakedVertices;
            public double prepareMs, simulateMs, bakeMs, combineMs, submitMs;
            public bool detailedTiming;
        }

        public static Frame current;
        public static Frame completed { get; private set; }
        public static bool collecting { get; private set; }
        private static int s_TimingRequests;
        public static bool timingEnabled => collecting && current.detailedTiming;

        /// <summary>
        /// Opt into detailed wall-clock timing on the main thread. Dispose to stop.
        /// Requests are reference counted and take effect at the next particle frame.
        /// Counters remain available without timing, including scheduler work estimates.
        /// </summary>
        public static System.IDisposable BeginDetailedTiming() => new TimingRequest();

        private sealed class TimingRequest : System.IDisposable
        {
            private bool _disposed;
            public TimingRequest() { s_TimingRequests++; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                s_TimingRequests--;
            }
        }

        public static long Timestamp() => timingEnabled ? Stopwatch.GetTimestamp() : 0;
        public static double Milliseconds(long start) => timingEnabled
            ? (Stopwatch.GetTimestamp() - start) * (1000.0 / Stopwatch.Frequency) : 0;
        internal static void BeginFrame(int frame)
        {
            current = new Frame { frame = frame, detailedTiming = s_TimingRequests > 0 };
            collecting = true;
        }
        internal static void EndFrame() { completed = current; collecting = false; }
        internal static byte Stage(string name)
        {
            switch (name)
            {
                case "[UIParticle] Bake Mesh > Simulate Particles": return 1;
                case "[UIParticleRenderer] Bake Mesh": return 2;
                case "[UIParticleRenderer] Combine Mesh": return 3;
                case "[UIParticleRenderer] Set Mesh": return 4;
                default: return 0;
            }
        }
        internal static void EndStage(byte stage, long started)
        {
            if (!timingEnabled || stage == 0) return;
            var ms = Milliseconds(started);
            switch (stage)
            {
                case 1: current.simulateMs += ms; break;
                case 2: current.bakeMs += ms; break;
                case 3: current.combineMs += ms; break;
                case 4: current.submitMs += ms; break;
            }
        }
    }
}
