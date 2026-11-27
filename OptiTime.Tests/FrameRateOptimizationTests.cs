using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class FrameRateOptimizationTests : IDisposable
    {
        private static bool currentTestFocus = true;

        private readonly Harmony focusHarmony;
        private readonly ClientPlatformAbstract previousPlatform;
        private readonly int? previousVsyncMode;

        static FrameRateOptimizationTests()
        {
            string gameDirectory = FindGameDirectory();
            if (gameDirectory == null)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Lib"), Path.Combine(gameDirectory, "Mods") })
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }

                return null;
            };
        }

        public FrameRateOptimizationTests()
        {
            previousPlatform = ScreenManager.Platform;
            focusHarmony = new Harmony("com.zaldaryon.optitime.test.focus");
            currentTestFocus = true;

            try
            {
                previousVsyncMode = ClientSettings.VsyncMode;
            }
            catch
            {
                previousVsyncMode = null;
            }

            FrameRateOptimization.Cleanup();
            ProfilingHelper.SetFramePacingDiagEnabled(false);
            ProfilingHelper.ResetFramePacing();
        }

        public void Dispose()
        {
            try
            {
                FrameRateOptimization.Cleanup();
            }
            catch
            {
            }

            RestoreStaticField("isWindowsProvider", new Func<bool>(OperatingSystem.IsWindows));
            RestoreStaticMethodDelegate("timeBeginPeriodProvider", "TimeBeginPeriod");
            RestoreStaticMethodDelegate("timeEndPeriodProvider", "TimeEndPeriod");
            RestoreStaticField("timestampProvider", new Func<long>(Stopwatch.GetTimestamp));
            RestoreStaticField("logicalProcessorCount", Environment.ProcessorCount);

            focusHarmony.UnpatchAll("com.zaldaryon.optitime.test.focus");
            ScreenManager.Platform = previousPlatform;
            currentTestFocus = true;

            if (previousVsyncMode.HasValue)
            {
                try
                {
                    ClientSettings.Inst.Int["vsyncMode"] = previousVsyncMode.Value;
                }
                catch
                {
                }
            }

            ProfilingHelper.SetFramePacingDiagEnabled(false);
            ProfilingHelper.ResetFramePacing();
        }

        [Fact]
        public void FocusLossDebounce_IgnoresTransientFlickersUnder300MsAndClampsSustainedUnfocusedFrames()
        {
            var platform = CreateFakePlatformWithFocusHook();
            ScreenManager.Platform = platform;

            long syntheticTicks = Stopwatch.Frequency * 10L;
            FieldInfo timestampField = AccessTools.Field(typeof(FrameRateOptimization), "timestampProvider");
            if (timestampField != null)
            {
                timestampField.SetValue(null, new Func<long>(() => syntheticTicks));
            }

            FrameRateOptimization.Configure(new OptiTimeConfig
            {
                BackgroundFpsLimiterEnabled = true,
                BackgroundMaxFps = 20,
                PreciseFramePacingEnabled = false
            });

            // Focused frame keeps foreground FPS cap
            currentTestFocus = true;
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            // First unfocused frame (0 ms elapsed): transient flicker must NOT clamp
            currentTestFocus = false;
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            // Still under 300 ms (16 ms, then 299 ms): must NOT clamp
            syntheticTicks += MsToTicks(16);
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            syntheticTicks += MsToTicks(283); // 299 ms total
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            // At 300 ms sustained focus loss: must clamp to backgroundMaxFps
            syntheticTicks += MsToTicks(1); // 300 ms total
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(20f, platform.MaxFps);

            // Regaining focus restores foreground cap and resets debounce timer
            currentTestFocus = true;
            syntheticTicks += MsToTicks(16);
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            // Second transient flicker starts a fresh 300 ms debounce window
            currentTestFocus = false;
            syntheticTicks += MsToTicks(50);
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            syntheticTicks += MsToTicks(200); // 200 ms into second streak
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(144f, platform.MaxFps);

            syntheticTicks += MsToTicks(100); // 300 ms into second streak
            platform.MaxFps = 144f;
            FrameRateOptimization.OnNewFrame_Postfix(0.016f);
            Assert.Equal(20f, platform.MaxFps);
        }

        [Fact]
        public void WaitForRemainingFrameTime_YieldsWithoutSpinningAtOrBelowFourLogicalCores()
        {
            FieldInfo procCountField = AccessTools.Field(typeof(FrameRateOptimization), "logicalProcessorCount");
            Assert.NotNull(procCountField);

            ClientSettings.Inst.Int["vsyncMode"] = 0;

            var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
            platform.MaxFps = 200f; // 5 ms frame target
            FieldInfo stopwatchField = AccessTools.Field(typeof(ClientPlatformWindows), "frameStopWatch");
            Assert.NotNull(stopwatchField);

            FrameRateOptimization.Configure(new OptiTimeConfig
            {
                PreciseFramePacingEnabled = true,
                PreciseFramePacingUndershootPercent = 0.25,
                PreciseFramePacingYieldThresholdMs = 2.0,
                PreciseFramePacingSpinIterations = 16
            });

            ProfilingHelper.SetFramePacingDiagEnabled(true);

            // <= 4 logical cores must never call Thread.SpinWait
            foreach (int lowCoreCount in new[] { 1, 2, 4 })
            {
                procCountField.SetValue(null, lowCoreCount);
                ProfilingHelper.ResetFramePacing();

                var sw = Stopwatch.StartNew();
                stopwatchField.SetValue(platform, sw);
                FrameRateOptimization.WaitForRemainingFrameTime(5, platform);

                var stats = ProfilingHelper.GetFramePacingStats();
                long yieldLoops = ReadProfilingCounter("preciseYieldLoops");
                long spinLoops = ReadProfilingCounter("preciseSpinLoops");

                Assert.Equal(1, stats.precise);
                Assert.True(yieldLoops > 0, $"Expected yield loops > 0 at {lowCoreCount} logical cores.");
                Assert.Equal(0, spinLoops);
            }

            // >= 5 logical cores allow SpinWait within yieldThresholdMs
            foreach (int highCoreCount in new[] { 5, 8 })
            {
                procCountField.SetValue(null, highCoreCount);
                ProfilingHelper.ResetFramePacing();

                var sw = Stopwatch.StartNew();
                stopwatchField.SetValue(platform, sw);
                FrameRateOptimization.WaitForRemainingFrameTime(5, platform);

                var stats = ProfilingHelper.GetFramePacingStats();
                long spinLoops = ReadProfilingCounter("preciseSpinLoops");

                Assert.Equal(1, stats.precise);
                Assert.True(spinLoops > 0, $"Expected spin loops > 0 at {highCoreCount} logical cores.");
            }
        }

        [Fact]
        public void WindowsTimerResolution_Requests1MsWhenActiveAndPairsEndPeriodAcrossDisableCleanupAndExit()
        {
            MethodInfo beginExtern = AccessTools.Method(typeof(FrameRateOptimization), "TimeBeginPeriod", new[] { typeof(uint) });
            MethodInfo endExtern = AccessTools.Method(typeof(FrameRateOptimization), "TimeEndPeriod", new[] { typeof(uint) });
            Assert.NotNull(beginExtern);
            Assert.NotNull(endExtern);

            DllImportAttribute beginImport = beginExtern.GetCustomAttribute<DllImportAttribute>();
            DllImportAttribute endImport = endExtern.GetCustomAttribute<DllImportAttribute>();
            Assert.NotNull(beginImport);
            Assert.NotNull(endImport);
            Assert.Equal("winmm.dll", beginImport.Value);
            Assert.Equal("timeBeginPeriod", beginImport.EntryPoint);
            Assert.Equal("winmm.dll", endImport.Value);
            Assert.Equal("timeEndPeriod", endImport.EntryPoint);

            FieldInfo isWindowsField = AccessTools.Field(typeof(FrameRateOptimization), "isWindowsProvider");
            FieldInfo beginField = AccessTools.Field(typeof(FrameRateOptimization), "timeBeginPeriodProvider");
            FieldInfo endField = AccessTools.Field(typeof(FrameRateOptimization), "timeEndPeriodProvider");
            MethodInfo onProcessExit = AccessTools.Method(typeof(FrameRateOptimization), "OnProcessExit", new[] { typeof(object), typeof(EventArgs) });
            Assert.NotNull(isWindowsField);
            Assert.NotNull(beginField);
            Assert.NotNull(endField);
            Assert.NotNull(onProcessExit);

            bool fakeWindows = true;
            uint beginReturnCode = 0u;
            var beginCalls = new List<uint>();
            var endCalls = new List<uint>();

            isWindowsField.SetValue(null, new Func<bool>(() => fakeWindows));
            beginField.SetValue(null, new Func<uint, uint>(period =>
            {
                beginCalls.Add(period);
                return beginReturnCode;
            }));
            endField.SetValue(null, new Func<uint, uint>(period =>
            {
                endCalls.Add(period);
                return 0u;
            }));

            // Enable on Windows -> timeBeginPeriod(1) called once; duplicate Configure(true) is idempotent
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = true });
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = true });
            Assert.Equal(new[] { 1u }, beginCalls);
            Assert.Empty(endCalls);

            // Disable via Configure(false) -> timeEndPeriod(1) paired once; duplicate Configure(false) is idempotent
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = false });
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = false });
            Assert.Equal(new[] { 1u }, beginCalls);
            Assert.Equal(new[] { 1u }, endCalls);

            // Enable -> Cleanup() pairs timeEndPeriod(1) once
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = true });
            FrameRateOptimization.Cleanup();
            FrameRateOptimization.Cleanup();
            Assert.Equal(new[] { 1u, 1u }, beginCalls);
            Assert.Equal(new[] { 1u, 1u }, endCalls);

            // Enable -> ProcessExit pairs timeEndPeriod(1) once; subsequent Cleanup() does not double-release
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = true });
            onProcessExit.Invoke(null, new object[] { null, EventArgs.Empty });
            FrameRateOptimization.Cleanup();
            Assert.Equal(new[] { 1u, 1u, 1u }, beginCalls);
            Assert.Equal(new[] { 1u, 1u, 1u }, endCalls);

            // Non-zero timeBeginPeriod failure (TIMERR_NOCANDO = 97) must NOT call timeEndPeriod on cleanup
            beginReturnCode = 97u;
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = true });
            FrameRateOptimization.Cleanup();
            Assert.Equal(new[] { 1u, 1u, 1u, 1u }, beginCalls);
            Assert.Equal(new[] { 1u, 1u, 1u }, endCalls);

            // Non-Windows host must call neither timeBeginPeriod nor timeEndPeriod
            beginReturnCode = 0u;
            fakeWindows = false;
            FrameRateOptimization.Configure(new OptiTimeConfig { PreciseFramePacingEnabled = true });
            FrameRateOptimization.Cleanup();
            Assert.Equal(4, beginCalls.Count);
            Assert.Equal(3, endCalls.Count);
        }

        private ClientPlatformWindows CreateFakePlatformWithFocusHook()
        {
            MethodInfo isFocusedGetter = AccessTools.PropertyGetter(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.IsFocused));
            Assert.NotNull(isFocusedGetter);

            focusHarmony.Patch(
                isFocusedGetter,
                prefix: new HarmonyMethod(typeof(FrameRateOptimizationTests), nameof(IsFocusedPrefix)));

            return (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        }

        private static bool IsFocusedPrefix(ref bool __result)
        {
            __result = currentTestFocus;
            return false;
        }

        private static long MsToTicks(long milliseconds)
        {
            return Stopwatch.Frequency * milliseconds / 1000L;
        }

        private static long ReadProfilingCounter(string fieldName)
        {
            FieldInfo field = AccessTools.Field(typeof(ProfilingHelper), fieldName);
            Assert.NotNull(field);
            return (long)field.GetValue(null);
        }

        private static void RestoreStaticField(string fieldName, object value)
        {
            FieldInfo field = AccessTools.Field(typeof(FrameRateOptimization), fieldName);
            field?.SetValue(null, value);
        }

        private static void RestoreStaticMethodDelegate(string fieldName, string methodName)
        {
            FieldInfo field = AccessTools.Field(typeof(FrameRateOptimization), fieldName);
            MethodInfo method = AccessTools.Method(typeof(FrameRateOptimization), methodName, new[] { typeof(uint) });
            if (field != null && method != null)
            {
                field.SetValue(null, Delegate.CreateDelegate(field.FieldType, method));
            }
        }

        private static string FindGameDirectory()
        {
            foreach (string candidate in new[]
            {
                Environment.GetEnvironmentVariable("VINTAGE_STORY"),
                Environment.GetEnvironmentVariable("VINTAGE_STORY_HOME"),
                Environment.GetEnvironmentVariable("VINTAGE_STORY_PATH"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "VintageStory"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vintagestory")
            })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "VintagestoryAPI.dll")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
