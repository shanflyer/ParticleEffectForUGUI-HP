// Replays recorded frame times through the actual managed scheduling code only.
// Does not execute Unity, particle simulation or graphics work.
using System;
using System.Globalization;
using System.IO;
using Coffee.UIExtensions;

static class BakeReplayHarness
{
    static int Main()
    {
        var files = Directory.GetFiles("DeviceData/20260907_regression_pull/FxUIParticleBaseline",
            "20260907_125837_*.csv");
        if (files.Length != 1) throw new Exception("Expected the captured 12:58:37 sample");
        var clock = new ParticleBakeClock();
        int frames = 0, bakes = 0;
        float previous = 0, elapsed = 0, simulated = 0;
        foreach (var line in File.ReadLines(files[0]))
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            var columns = line.Split(',');
            var time = float.Parse(columns[1], CultureInfo.InvariantCulture);
            var dt = frames == 0
                ? float.Parse(columns[2], CultureInfo.InvariantCulture) / 1000
                : time - previous;
            previous = time; elapsed += dt; frames++;
            if (clock.Advance(dt, dt, 30, false, out var step, out _))
            { bakes++; simulated += step; }
        }
        clock.Advance(0, 0, 30, true, out var pending, out _);
        if (Math.Abs(simulated + pending - elapsed) > .0001) throw new Exception("Simulation time lost or duplicated");
        Console.WriteLine($"frames={frames} bakes={bakes} elapsed={elapsed:F6} simulated_with_pending={simulated + pending:F6}");
        return 0;
    }
}
