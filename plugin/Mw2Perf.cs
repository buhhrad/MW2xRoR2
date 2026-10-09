using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MW2RoR2
{
    /// Frame hitch probe: sections time themselves with Begin/End, and a slow frame logs what ran in
    /// it (plus whether the GC collected). Off unless a slow frame happens, so it costs ~nothing.
    static class Mw2Perf
    {
        static readonly Dictionary<string, long> ticks = new Dictionary<string, long>();
        static int gcBefore;
        static long frameStart;
        static int frame, killFrame = -1000;

        /// A kill happened this frame (slow frames say how long after one they came).
        public static void Kill() => killFrame = frame;

        public static long Begin() => Stopwatch.GetTimestamp();

        public static void End(string label, long start)
        {
            long d = Stopwatch.GetTimestamp() - start;
            ticks.TryGetValue(label, out long t);
            ticks[label] = t + d;
        }

        /// Once per frame (Plugin.Update): report the previous frame if it took longer than `slowMs`.
        public static void Frame(float slowMs = 40f)
        {
            long now = Stopwatch.GetTimestamp();
            double frameMs = frameStart == 0 ? 0 : (now - frameStart) * 1000.0 / Stopwatch.Frequency;
            int gc = GC.CollectionCount(0);
            if (frameMs > slowMs)
            {
                var s = new System.Text.StringBuilder($"MW2 perf: slow frame {frameMs:F0} ms, {frame - 1 - killFrame} frames after a kill, gc {gc - gcBefore}");
                foreach (var kv in ticks) s.Append($", {kv.Key} {kv.Value * 1000.0 / Stopwatch.Frequency:F1}");
                Plugin.Log.LogInfo(s.ToString());
            }
            ticks.Clear();
            gcBefore = gc;
            frameStart = now;
            frame++;
        }
    }
}
