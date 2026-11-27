using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public static class FrameRateOptimization
    {
        // Defaults preserve historical behaviour. Overridden by OptiTimeConfig if set.
        public const double DefaultUndershootPercent = 0.075;
        public const double DefaultYieldThresholdMs = 0.25;
        public const int DefaultSpinWaitIterations = 32;
        public const long FocusLossDebounceMs = 300L;
        public const int MinLogicalProcessorsForSpinWait = 5;
        internal const int MinimumBackgroundFps = 5;

        private const uint TimerPeriodMs = 1u;
        private const uint TimerrNoError = 0u;

        private static readonly AccessTools.FieldRef<ClientPlatformWindows, Stopwatch> frameStopWatchRef;
        private static readonly AccessTools.FieldRef<FrameProfilerUtil, ProfileEntryRange> currentEntryRef;
        private static readonly MethodInfo sleepMethod = AccessTools.Method(typeof(Thread), nameof(Thread.Sleep), new[] { typeof(int) });
        private static readonly MethodInfo replacementSleepMethod = AccessTools.Method(typeof(FrameRateOptimization), nameof(WaitForRemainingFrameTime));
        private static readonly long focusLossDebounceTicks = Stopwatch.Frequency * FocusLossDebounceMs / 1000L;
        private static readonly object timerResolutionLock = new object();
        private static readonly EventHandler processExitHandler = OnProcessExit;

        private static bool frameStopWatchAvailable;
        private static bool preciseFramePacingEnabled;
        private static bool backgroundFpsLimiterEnabled;
        private static int backgroundMaxFps = 20;
        private static double undershootPercent = DefaultUndershootPercent;
        private static double yieldThresholdMs = DefaultYieldThresholdMs;
        private static int spinWaitIterations = DefaultSpinWaitIterations;
        private static bool timerResolutionActive;
        private static bool processExitRegistered;
        private static long focusLostTimestampTicks;
        private static int logicalProcessorCount = Environment.ProcessorCount;
        private static System.Func<bool> isWindowsProvider = OperatingSystem.IsWindows;
        private static System.Func<uint, uint> timeBeginPeriodProvider = TimeBeginPeriod;
        private static System.Func<uint, uint> timeEndPeriodProvider = TimeEndPeriod;
        private static System.Func<long> timestampProvider = Stopwatch.GetTimestamp;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
        private static extern uint TimeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
        private static extern uint TimeEndPeriod(uint uPeriod);

        static FrameRateOptimization()
        {
            try
            {
                frameStopWatchRef = AccessTools.FieldRefAccess<ClientPlatformWindows, Stopwatch>("frameStopWatch");
                frameStopWatchAvailable = frameStopWatchRef != null;
            }
            catch
            {
                frameStopWatchAvailable = false;
            }

            try
            {
                currentEntryRef = AccessTools.FieldRefAccess<FrameProfilerUtil, ProfileEntryRange>("currentEntry");
            }
            catch
            {
                currentEntryRef = null;
            }
        }

        public static void Configure(OptiTimeConfig config)
        {
            preciseFramePacingEnabled = config?.PreciseFramePacingEnabled ?? false;
            backgroundFpsLimiterEnabled = config?.BackgroundFpsLimiterEnabled ?? false;

            int configuredBackgroundFps = config?.BackgroundMaxFps ?? 20;
            if (configuredBackgroundFps <= 0)
            {
                backgroundFpsLimiterEnabled = false;
                backgroundMaxFps = 20;
            }
            else
            {
                backgroundMaxFps = Math.Clamp(configuredBackgroundFps, MinimumBackgroundFps, 240);
            }

            // Tunable pacing knobs, clamped to sane ranges.
            undershootPercent = Math.Clamp(config?.PreciseFramePacingUndershootPercent ?? DefaultUndershootPercent, 0.01, 0.25);
            yieldThresholdMs = Math.Clamp(config?.PreciseFramePacingYieldThresholdMs ?? DefaultYieldThresholdMs, 0.05, 2.0);
            spinWaitIterations = Math.Clamp(config?.PreciseFramePacingSpinIterations ?? DefaultSpinWaitIterations, 8, 256);

            if (!backgroundFpsLimiterEnabled)
            {
                focusLostTimestampTicks = 0L;
            }

            UpdateTimerResolution(preciseFramePacingEnabled);
        }

        public static void Cleanup()
        {
            preciseFramePacingEnabled = false;
            backgroundFpsLimiterEnabled = false;
            backgroundMaxFps = 20;
            focusLostTimestampTicks = 0L;
            UpdateTimerResolution(false);
        }

        internal static void OnProcessExit(object sender, EventArgs e)
        {
            UpdateTimerResolution(false);
        }

        private static void UpdateTimerResolution(bool shouldBeActive)
        {
            lock (timerResolutionLock)
            {
                if (shouldBeActive)
                {
                    if (timerResolutionActive || !isWindowsProvider())
                    {
                        return;
                    }

                    try
                    {
                        if (!processExitRegistered)
                        {
                            AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                            processExitRegistered = true;
                        }

                        if (timeBeginPeriodProvider(TimerPeriodMs) == TimerrNoError)
                        {
                            timerResolutionActive = true;
                        }
                    }
                    catch
                    {
                        // Keep frame pacing functional even if winmm.dll is unavailable.
                    }
                }
                else
                {
                    if (!timerResolutionActive)
                    {
                        return;
                    }

                    timerResolutionActive = false;
                    try
                    {
                        timeEndPeriodProvider(TimerPeriodMs);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static void OnNewFrame_Postfix(float dt)
        {
            var platform = ScreenManager.Platform;
            if (platform == null)
                return;

            bool focused = platform.IsFocused;
            bool focusLossSustained = false;

            if (focused)
            {
                focusLostTimestampTicks = 0L;
            }
            else
            {
                long nowTicks = timestampProvider();
                if (nowTicks <= 0L)
                    nowTicks = 1L;

                if (focusLostTimestampTicks == 0L || nowTicks < focusLostTimestampTicks)
                {
                    focusLostTimestampTicks = nowTicks;
                }
                else if (nowTicks - focusLostTimestampTicks >= focusLossDebounceTicks)
                {
                    focusLossSustained = true;
                }
            }

            if (backgroundFpsLimiterEnabled && focusLossSustained)
            {
                platform.MaxFps = backgroundMaxFps;
            }

            ModuleBgFps.OnFrame(!focusLossSustained);
            ProfilingHelper.RecordFrame(dt, focused, platform.MaxFps);
        }

        /// <summary>
        /// Guards against a vanilla race in FrameProfilerUtil.Leave() where currentEntry
        /// can be null if profiling is enabled mid-frame (after Begin() already returned early).
        /// Our frame pacing patch widens the window for this race.
        /// </summary>
        public static bool Leave_Prefix(FrameProfilerUtil __instance)
        {
            if (currentEntryRef == null)
                return true;

            return currentEntryRef(__instance) != null;
        }

        public static IEnumerable<CodeInstruction> TranspileRenderFrameSleep(IEnumerable<CodeInstruction> instructions)
        {
            bool replaced = false;

            foreach (var code in instructions)
            {
                if (!replaced && code.opcode == OpCodes.Call && Equals(code.operand, sleepMethod))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, replacementSleepMethod);
                    replaced = true;
                    continue;
                }

                yield return code;
            }

            if (!replaced)
                throw new InvalidOperationException("[OptiTime] Failed to replace Thread.Sleep in ClientPlatformWindows.window_RenderFrame");
        }

        public static void WaitForRemainingFrameTime(int millisecondsTimeout, ClientPlatformWindows instance)
        {
            if (millisecondsTimeout <= 0)
                return;

            if (!preciseFramePacingEnabled || instance == null || !frameStopWatchAvailable)
            {
                Thread.Sleep(millisecondsTimeout);
                ProfilingHelper.RecordFramePacingFallback(millisecondsTimeout);
                return;
            }

            if (ClientSettings.VsyncMode == 1 || instance.MaxFps <= 10f || instance.MaxFps >= 241f)
            {
                Thread.Sleep(millisecondsTimeout);
                ProfilingHelper.RecordFramePacingFallback(millisecondsTimeout);
                return;
            }

            Stopwatch frameStopWatch;
            try
            {
                frameStopWatch = frameStopWatchRef(instance);
            }
            catch
            {
                frameStopWatch = null;
            }

            if (frameStopWatch == null)
            {
                Thread.Sleep(millisecondsTimeout);
                ProfilingHelper.RecordFramePacingFallback(millisecondsTimeout);
                return;
            }

            long targetTicks = (long)(Stopwatch.Frequency / (double)instance.MaxFps);
            if (targetTicks <= 0L)
            {
                Thread.Sleep(millisecondsTimeout);
                ProfilingHelper.RecordFramePacingFallback(millisecondsTimeout);
                return;
            }

            long remainingTicks = targetTicks - frameStopWatch.ElapsedTicks;
            if (remainingTicks <= 0L)
                return;

            long finalWaitTicks = Math.Max(
                Stopwatch.Frequency / 2000,
                (long)(targetTicks * undershootPercent)
            );

            int coarseSleepMs = 0;
            if (remainingTicks > finalWaitTicks)
            {
                coarseSleepMs = (int)((remainingTicks - finalWaitTicks) * 1000L / Stopwatch.Frequency);
                if (coarseSleepMs > 0)
                {
                    Thread.Sleep(coarseSleepMs);
                }
            }

            long yieldThresholdTicks = (long)(Stopwatch.Frequency * (yieldThresholdMs / 1000.0));
            if (yieldThresholdTicks < 1L)
                yieldThresholdTicks = 1L;

            int yieldCount = 0;
            int spinCount = 0;
            bool allowSpin = logicalProcessorCount >= MinLogicalProcessorsForSpinWait;

            while ((remainingTicks = targetTicks - frameStopWatch.ElapsedTicks) > 0L)
            {
                if (!allowSpin || remainingTicks > yieldThresholdTicks)
                {
                    Thread.Yield();
                    yieldCount++;
                }
                else
                {
                    Thread.SpinWait(spinWaitIterations);
                    spinCount++;
                }
            }

            double overshootMs = Math.Max(0.0, (frameStopWatch.ElapsedTicks - targetTicks) * 1000.0 / Stopwatch.Frequency);
            ProfilingHelper.RecordFramePacingWait(coarseSleepMs, yieldCount, spinCount, overshootMs);
        }
    }
}
