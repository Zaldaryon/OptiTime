using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OptiTime
{
    /// <summary>
    /// Lightweight profiling helper that uses the game's FrameProfiler when available and
    /// tracks simple counters plus rolling frame statistics for debug dumps.
    /// Disabled by default to avoid overhead.
    /// </summary>
    public static class ProfilingHelper
    {
        private const int FrameHistoryCapacity = 240;
        private const float SpikeThresholdMs = 33.3f;

        private static bool enabled = false;
        private static volatile bool framePacingDiagEnabled = false;
        private static ICoreClientAPI capi = null;
        private static object frameProfiler = null;
        private static MethodInfo markString = null;
        private static MethodInfo markStringObject = null;
        private static readonly Dictionary<string, long> counters = new Dictionary<string, long>();
        private static readonly System.Threading.Lock counterLock = new();
        private static readonly float[] frameHistoryMs = new float[FrameHistoryCapacity];
        private static readonly bool[] frameFocusHistory = new bool[FrameHistoryCapacity];

        private static int frameHistoryCount = 0;
        private static int frameHistoryIndex = 0;
        private static long totalFramesRecorded = 0;
        private static float lastObservedMaxFps = 0f;

        private static long preciseWaitFrames = 0;
        private static long preciseFallbackFrames = 0;
        private static long preciseSleepMsTotal = 0;
        private static long preciseYieldLoops = 0;
        private static long preciseSpinLoops = 0;
        private static double preciseOvershootMsTotal = 0;
        private static double preciseOvershootMsMax = 0;

        // Ring buffer for p99 overshoot calculation
        private const int OvershootRingSize = 1024;
        private static readonly double[] overshootRing = new double[OvershootRingSize];
        private static int overshootRingIndex = 0;
        private static int overshootRingCount = 0;

        private static string lastSpikeSummary = null;

        public static bool Enabled => enabled;

        public static void Initialize(ICoreClientAPI api, bool enable)
        {
            capi = api;
            TryBindProfiler(api);
            ResetCounters();
            enabled = enable;
        }

        public static void SetEnabled(bool enable)
        {
            enabled = enable;
        }

        public static void SetFramePacingDiagEnabled(bool enable)
        {
            framePacingDiagEnabled = enable;
        }

        public static void Cleanup()
        {
            enabled = false;
            capi = null;
            frameProfiler = null;
            markString = null;
            markStringObject = null;
            ResetCounters();
        }

        public static void Mark(string tag, string detail = null, bool countOnly = false)
        {
            if (!enabled) return;

            lock (counterLock)
            {
                counters.TryGetValue(tag, out long val);
                counters[tag] = val + 1;
            }

            if (countOnly) return;

            try
            {
                if (frameProfiler != null && markStringObject != null && detail != null)
                {
                    markStringObject.Invoke(frameProfiler, new object[] { tag, detail });
                }
                else if (frameProfiler != null && markString != null)
                {
                    markString.Invoke(frameProfiler, new object[] { tag });
                }
            }
            catch
            {
                // Ignore profiler errors to avoid impacting gameplay
            }
        }

        public static void RecordFrame(float dt, bool focused, float currentMaxFps)
        {
            if (!enabled) return;

            float frameMs = dt * 1000f;
            if (float.IsNaN(frameMs) || float.IsInfinity(frameMs) || frameMs < 0f)
                return;

            string spikeSummary = null;
            if (frameMs >= SpikeThresholdMs)
            {
                spikeSummary = BuildSpikeSummary();
            }

            lock (counterLock)
            {
                if (frameHistoryCount < FrameHistoryCapacity)
                {
                    frameHistoryCount++;
                }

                frameHistoryMs[frameHistoryIndex] = frameMs;
                frameFocusHistory[frameHistoryIndex] = focused;
                frameHistoryIndex = (frameHistoryIndex + 1) % FrameHistoryCapacity;
                totalFramesRecorded++;
                lastObservedMaxFps = currentMaxFps;

                if (!string.IsNullOrEmpty(spikeSummary))
                {
                    lastSpikeSummary = spikeSummary;
                }
            }
        }

        public static void RecordFramePacingWait(int coarseSleepMs, int yieldCount, int spinCount, double overshootMs)
        {
            if (!enabled && !framePacingDiagEnabled) return;

            lock (counterLock)
            {
                preciseWaitFrames++;
                preciseSleepMsTotal += coarseSleepMs;
                preciseYieldLoops += yieldCount;
                preciseSpinLoops += spinCount;
                preciseOvershootMsTotal += overshootMs;
                if (overshootMs > preciseOvershootMsMax)
                {
                    preciseOvershootMsMax = overshootMs;
                }
                overshootRing[overshootRingIndex] = overshootMs;
                overshootRingIndex = (overshootRingIndex + 1) % OvershootRingSize;
                if (overshootRingCount < OvershootRingSize) overshootRingCount++;
            }
        }

        public static void RecordFramePacingFallback(int sleepMs)
        {
            if (!enabled && !framePacingDiagEnabled) return;

            lock (counterLock)
            {
                preciseFallbackFrames++;
                preciseSleepMsTotal += sleepMs;
            }
        }

        public static (long precise, long fallback, long sleepMs, double avgOvershoot, double maxOvershoot, double p99Overshoot) GetFramePacingStats()
        {
            lock (counterLock)
            {
                double avg = preciseWaitFrames > 0 ? preciseOvershootMsTotal / preciseWaitFrames : 0;
                double p99 = 0;
                if (overshootRingCount > 0)
                {
                    var sorted = new double[overshootRingCount];
                    Array.Copy(overshootRing, 0, sorted, 0, overshootRingCount);
                    Array.Sort(sorted);
                    int idx = (int)Math.Ceiling(sorted.Length * 0.99) - 1;
                    if (idx >= sorted.Length) idx = sorted.Length - 1;
                    p99 = sorted[idx];
                }
                return (preciseWaitFrames, preciseFallbackFrames, preciseSleepMsTotal, avg, preciseOvershootMsMax, p99);
            }
        }

        public static void ResetFramePacing()
        {
            lock (counterLock)
            {
                preciseWaitFrames = 0;
                preciseFallbackFrames = 0;
                preciseSleepMsTotal = 0;
                preciseYieldLoops = 0;
                preciseSpinLoops = 0;
                preciseOvershootMsTotal = 0;
                preciseOvershootMsMax = 0;
                Array.Clear(overshootRing, 0, overshootRing.Length);
                overshootRingIndex = 0;
                overshootRingCount = 0;
            }
        }

        public static void Dump(ICoreClientAPI api)
        {
            if (!enabled)
            {
                api.ShowChatMessage("[OptiTime] Profiling is OFF");
                return;
            }

            float[] snapshot;
            int sampleCount;
            long totalFrames;
            float lastCap;
            long pacingFrames;
            long fallbackFrames;
            long sleepMsTotal;
            long yieldLoops;
            long spinLoops;
            double overshootAvg;
            double overshootMax;
            string spikeSummary;
            List<string> counterLines = new List<string>();
            bool[] focusSnapshot;

            lock (counterLock)
            {
                sampleCount = frameHistoryCount;
                snapshot = new float[sampleCount];
                focusSnapshot = new bool[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    int sourceIndex = (frameHistoryIndex - sampleCount + i + FrameHistoryCapacity) % FrameHistoryCapacity;
                    snapshot[i] = frameHistoryMs[sourceIndex];
                    focusSnapshot[i] = frameFocusHistory[sourceIndex];
                }

                totalFrames = totalFramesRecorded;
                lastCap = lastObservedMaxFps;
                pacingFrames = preciseWaitFrames;
                fallbackFrames = preciseFallbackFrames;
                sleepMsTotal = preciseSleepMsTotal;
                yieldLoops = preciseYieldLoops;
                spinLoops = preciseSpinLoops;
                overshootAvg = preciseWaitFrames > 0 ? preciseOvershootMsTotal / preciseWaitFrames : 0;
                overshootMax = preciseOvershootMsMax;
                spikeSummary = lastSpikeSummary;

                foreach (var kv in counters)
                {
                    counterLines.Add($"{kv.Key}: {kv.Value}");
                }
            }

            double avgFrameMs = 0;
            float minMs = 0;
            float maxMs = 0;
            int focused = 0;
            int unfocused = 0;
            int over16 = 0;
            int over33 = 0;
            int over50 = 0;

            if (sampleCount > 0)
            {
                minMs = float.MaxValue;

                for (int i = 0; i < sampleCount; i++)
                {
                    float frameMs = snapshot[i];
                    avgFrameMs += frameMs;
                    if (frameMs < minMs) minMs = frameMs;
                    if (frameMs > maxMs) maxMs = frameMs;
                    if (frameMs > 16.7f) over16++;
                    if (frameMs > 33.3f) over33++;
                    if (frameMs > 50f) over50++;
                    if (focusSnapshot[i]) focused++;
                    else unfocused++;
                }

                avgFrameMs /= sampleCount;
            }

            float[] sortedSnapshot = (float[])snapshot.Clone();
            Array.Sort(sortedSnapshot);
            float p95 = Percentile(sortedSnapshot, 0.95f);
            float p99 = Percentile(sortedSnapshot, 0.99f);
            float worst1 = WorstPercentAverage(sortedSnapshot, 0.01f);
            float avgFps = (float)(avgFrameMs > 0.0001 ? 1000.0 / avgFrameMs : 0.0);

            api.ShowChatMessage("=== [OptiTime] Profiling ===");
            api.ShowChatMessage($"frames: window={sampleCount} total={totalFrames} avg={avgFrameMs:0.00}ms ({avgFps:0.0} fps) cap={lastCap:0}");
            api.ShowChatMessage($"p95={p95:0.00}ms p99={p99:0.00}ms worst1%={worst1:0.00}ms");
            api.ShowChatMessage($"min={minMs:0.00}ms max={maxMs:0.00}ms >16.7={over16} >33.3={over33} >50={over50}");
            api.ShowChatMessage($"focused={focused} unfocused={unfocused}");
            api.ShowChatMessage($"framepace: precise={pacingFrames} fallback={fallbackFrames} sleepMs={sleepMsTotal}");
            api.ShowChatMessage($"framepace: yields={yieldLoops} spins={spinLoops} overAvg={overshootAvg:0.000}ms overMax={overshootMax:0.000}ms");

            if (!string.IsNullOrEmpty(spikeSummary))
            {
                api.ShowChatMessage($"last spike: {spikeSummary}");
            }

            counterLines.Sort(StringComparer.Ordinal);
            int emitted = 0;
            foreach (string line in counterLines)
            {
                api.ShowChatMessage(line);
                if (++emitted >= 12)
                {
                    api.ShowChatMessage("... (counter output limited)");
                    break;
                }
            }
        }

        public static void ResetCounters()
        {
            lock (counterLock)
            {
                counters.Clear();

                Array.Clear(frameHistoryMs, 0, frameHistoryMs.Length);
                Array.Clear(frameFocusHistory, 0, frameFocusHistory.Length);
                frameHistoryCount = 0;
                frameHistoryIndex = 0;
                totalFramesRecorded = 0;
                lastObservedMaxFps = 0f;

                preciseWaitFrames = 0;
                preciseFallbackFrames = 0;
                preciseSleepMsTotal = 0;
                preciseYieldLoops = 0;
                preciseSpinLoops = 0;
                preciseOvershootMsTotal = 0;
                preciseOvershootMsMax = 0;
                Array.Clear(overshootRing, 0, overshootRing.Length);
                overshootRingIndex = 0;
                overshootRingCount = 0;
                lastSpikeSummary = null;
            }
        }

        private static float Percentile(float[] sortedValues, float percentile)
        {
            if (sortedValues == null || sortedValues.Length == 0)
                return 0f;

            int index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
            if (index < 0) index = 0;
            if (index >= sortedValues.Length) index = sortedValues.Length - 1;
            return sortedValues[index];
        }

        private static float WorstPercentAverage(float[] sortedValues, float percent)
        {
            if (sortedValues == null || sortedValues.Length == 0)
                return 0f;

            int sampleSize = Math.Max(1, (int)Math.Ceiling(sortedValues.Length * percent));
            double total = 0;
            for (int i = sortedValues.Length - sampleSize; i < sortedValues.Length; i++)
            {
                total += sortedValues[i];
            }

            return (float)(total / sampleSize);
        }

        private static string BuildSpikeSummary()
        {
            try
            {
                var profiler = capi?.World?.FrameProfiler;
                ProfileEntryRange root = profiler?.PrevRootEntry;
                if (root == null)
                    return null;

                List<(string code, long ticks)> entries = new List<(string code, long ticks)>();

                if (root.Marks != null)
                {
                    foreach (var mark in root.Marks)
                    {
                        entries.Add((mark.Key, mark.Value.ElapsedTicks));
                    }
                }

                if (root.ChildRanges != null)
                {
                    foreach (var child in root.ChildRanges)
                    {
                        entries.Add((child.Key, child.Value.ElapsedTicks));
                    }
                }

                if (entries.Count == 0)
                    return null;

                entries.Sort((a, b) => b.ticks.CompareTo(a.ticks));

                int count = Math.Min(3, entries.Count);
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) builder.Append(" | ");
                    double ms = entries[i].ticks * 1000.0 / Stopwatch.Frequency;
                    builder.Append(entries[i].code).Append('=').Append(ms.ToString("0.00")).Append("ms");
                }

                return builder.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void TryBindProfiler(ICoreClientAPI api)
        {
            try
            {
                var profiler = api?.World?.FrameProfiler;
                if (profiler == null) return;

                frameProfiler = profiler;
                var type = profiler.GetType();
                markString = AccessToolsShim.Method(type, "Mark", new Type[] { typeof(string) });
                markStringObject = AccessToolsShim.Method(type, "Mark", new Type[] { typeof(string), typeof(object) });
            }
            catch
            {
                frameProfiler = null;
                markString = null;
                markStringObject = null;
            }
        }
    }

    /// <summary>
    /// Minimal AccessTools substitute for local reflection without importing Harmony here.
    /// </summary>
    internal static class AccessToolsShim
    {
        public static MethodInfo Method(Type type, string name, Type[] args)
        {
            try
            {
                return type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
            }
            catch
            {
                return null;
            }
        }
    }
}
