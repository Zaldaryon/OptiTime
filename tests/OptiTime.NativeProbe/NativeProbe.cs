using System.Diagnostics;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OptiTime.NativeProbe;

public sealed class NativeProbe : ModSystem
{
    private static ICoreClientAPI? api;
    private static long warmUntil;
    private static long lastStart;
    private static int count;
    private static bool done;
    private const int SampleCount = 2400;
    private static readonly double[] CadenceMs = new double[SampleCount];
    private static readonly double[] HandlerMs = new double[SampleCount];
    private static readonly long[] Allocations = new long[SampleCount];
    private static readonly bool?[] Focused = new bool?[SampleCount];
    private Harmony? harmony;
    private static string? controlledOutput;
    private static Func<bool>? readFocus;
    private static object? platform;
    private static object? window;
    private static int phase;
    private static long sampleUntil;
    private static readonly List<NativeSample> Samples = new(20000);
    private static readonly string[] Modes = ["foreground-uncapped", "foreground-60fps", "background"];
    private static double[]? pose;
    private static double startHours;
    private static int[]? startGc;
    private static double[]? sampleStartPose;
    private static readonly object[] PhaseResults = new object[3];
    private readonly record struct NativeSample(double CadenceMs, double HandlerMs, long AllocatedBytes, bool Focused);

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI clientApi)
    {
        api = clientApi;
        controlledOutput = Environment.GetEnvironmentVariable("OPTITIME_NATIVE_PROFILE");
        phase = 0;
        Samples.Clear();
        clientApi.Event.LevelFinalize += () =>
        {
            // Install after mod startup so measurement hooks cannot affect conflict detection.
            if (harmony == null)
            {
                var method = AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:window_RenderFrame")
                    ?? throw new InvalidOperationException("Native window_RenderFrame hook unavailable; no measurement possible.");
                harmony = new Harmony("optitime.private.nativeprobe");
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(NativeProbe), nameof(Begin)),
                    postfix: new HarmonyMethod(typeof(NativeProbe), nameof(End)));
                if (controlledOutput != null)
                    harmony.Patch(AccessTools.Method("Vintagestory.Client.ScreenManager:OnNewFrame"),
                        prefix: new HarmonyMethod(typeof(NativeProbe), nameof(FixCamera)));
            }
            count = 0;
            lastStart = 0;
            done = false;
            if (controlledOutput != null)
            {
                clientApi.SendChatMessage("/time set 12");
                clientApi.SendChatMessage("/time calendarspeedmul 0");
                var pos = clientApi.World.Player.Entity.Pos;
                pose = [pos.X, pos.Y, pos.Z, 0, Math.PI];
            }
            warmUntil = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            clientApi.Logger.Notification("[OptiTime] Native probe: level finalized; warming for 30 seconds.");
        };
    }

    private static void Begin(object __instance, out (long Time, long Allocated) __state)
    {
        __state = default;
        long now = Stopwatch.GetTimestamp();
        if (controlledOutput != null)
        {
            BeginControlled(__instance, now, out __state);
            return;
        }
        if (api == null || warmUntil == 0 || done || now < warmUntil) return;
        __state = (now, GC.GetTotalAllocatedBytes(false));
        if (lastStart != 0) CadenceMs[count] = (now - lastStart) * 1000.0 / Stopwatch.Frequency;
        lastStart = now;
        Focused[count] = AccessTools.Property(__instance.GetType(), "IsFocused")?.GetValue(__instance) as bool?;
    }

    private static void End((long Time, long Allocated) __state)
    {
        if (__state.Time == 0 || api == null || done) return;
        if (controlledOutput != null)
        {
            EndControlled(__state);
            return;
        }
        HandlerMs[count] = (Stopwatch.GetTimestamp() - __state.Time) * 1000.0 / Stopwatch.Frequency;
        Allocations[count] = GC.GetTotalAllocatedBytes(false) - __state.Allocated;
        if (++count != SampleCount) return;
        done = true;
        string path = Path.Combine(api.DataBasePath, "native-probe.json");
        var result = new
        {
            measurement = "native ClientPlatformWindows.window_RenderFrame (includes platform pacing)",
            sampleCount = count,
            warmupSeconds = 30,
            cadenceMs = CadenceMs.Skip(1).ToArray(),
            handlerMs = HandlerMs,
            processAllocatedBytes = Allocations,
            focused = Focused,
            settings = ReadSettings(),
            playerPosition = api.World.Player.Entity.Pos.XYZ.ToString(),
            note = "Process-wide allocation includes server/background threads. Handler duration includes pacing; neither is GPU time. Scene and focus must be controlled externally."
        };
        File.WriteAllText(path, JsonSerializer.Serialize(result) + "\n");
        api.Logger.Notification("[OptiTime] Native probe complete: " + path);
    }

    private static void BeginControlled(object instance, long now, out (long Time, long Allocated) state)
    {
        state = default;
        if (api == null || warmUntil == 0 || done) return;
        if (platform == null)
        {
            platform = instance;
            readFocus = (Func<bool>)AccessTools.Property(instance.GetType(), "IsFocused").GetMethod!.CreateDelegate(typeof(Func<bool>), instance);
            window = AccessTools.Field(instance.GetType(), "window").GetValue(instance);
            ConfigurePhase(now, 45);
        }
        if (now < warmUntil) return;
        if (readFocus!() != (phase != 2))
        {
            if (Samples.Count > 0)
                api.Logger.Warning("[OptiTime] Native focus changed; discard phase and restart warm-up.");
            Samples.Clear();
            sampleUntil = 0;
            lastStart = 0;
            warmUntil = now + 3 * Stopwatch.Frequency;
            return;
        }
        if (sampleUntil == 0)
        {
            sampleUntil = now + 15 * Stopwatch.Frequency;
            startHours = api.World.Calendar.TotalHours;
            var pos = api.World.Player.Entity.Pos;
            sampleStartPose = [pos.X, pos.Y, pos.Z, pos.Yaw, pos.Pitch];
            startGc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        }
        state = (now, GC.GetTotalAllocatedBytes(false));
    }

    private static void FixCamera()
    {
        if (api == null || pose == null) return;
        api.Input.MouseYaw = (float)pose[3];
        api.Input.MousePitch = (float)pose[4];
        api.World.Player.Entity.Pos.Yaw = (float)pose[3];
        api.World.Player.Entity.Pos.Pitch = (float)pose[4];
    }

    private static void ConfigurePhase(long now, int warmupSeconds)
    {
        var settings = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
        if (phase == 0)
        {
            var state = AccessTools.Property(window!.GetType(), "WindowState");
            state.SetValue(window, Enum.ToObject(state.PropertyType, 0));
            var size = AccessTools.Property(window.GetType(), "ClientSize");
            size.SetValue(window, Activator.CreateInstance(size.PropertyType, 1280, 720));
            var location = AccessTools.Property(window.GetType(), "Location");
            location.SetValue(window, Activator.CreateInstance(location.PropertyType, 100, 100));
        }
        int fps = phase == 1 ? 60 : 241;
        AccessTools.Property(settings, "MaxFPS").SetValue(null, fps);
        AccessTools.Property(settings, "VsyncMode").SetValue(null, 0);
        AccessTools.Property(platform!.GetType(), "MaxFps").SetValue(platform, (float)fps);
        var vsync = AccessTools.Property(window!.GetType(), "VSync");
        vsync.SetValue(window, Enum.ToObject(vsync.PropertyType, 0));
        lastStart = 0;
        sampleUntil = 0;
        Samples.Clear();
        warmUntil = now + warmupSeconds * Stopwatch.Frequency;
        File.WriteAllText(controlledOutput + ".phase.json", JsonSerializer.Serialize(new { mode = Modes[phase], warmupSeconds }) + "\n");
        api!.Logger.Notification("[OptiTime] Controlled native phase: " + Modes[phase]);
    }

    private static void EndControlled((long Time, long Allocated) state)
    {
        long end = Stopwatch.GetTimestamp();
        if (readFocus!() != (phase != 2))
        {
            api!.Logger.Warning("[OptiTime] Native focus changed; discard phase and restart warm-up.");
            ConfigurePhase(end, 5);
            return;
        }
        double cadence = lastStart == 0 ? 0 : (state.Time - lastStart) * 1000.0 / Stopwatch.Frequency;
        Samples.Add(new NativeSample(cadence, (end - state.Time) * 1000.0 / Stopwatch.Frequency,
            GC.GetTotalAllocatedBytes(false) - state.Allocated, readFocus!()));
        lastStart = state.Time;
        if (end < sampleUntil) return;
        var pos = api!.World.Player.Entity.Pos;
        PhaseResults[phase] = new
        {
            mode = Modes[phase], seconds = 15, samples = Samples.ToArray(), settings = ReadSettings(),
            initialPose = pose, startPose = sampleStartPose, endPose = new[] { pos.X, pos.Y, pos.Z, pos.Yaw, pos.Pitch },
            startHours, endHours = api.World.Calendar.TotalHours,
            gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - startGc![i]).ToArray(),
            effectivePlatformMaxFps = AccessTools.Property(platform!.GetType(), "MaxFps").GetValue(platform)
        };
        if (++phase < Modes.Length)
        {
            ConfigurePhase(end, 10);
            return;
        }
        done = true;
        File.WriteAllText(controlledOutput!, JsonSerializer.Serialize(new
        {
            measurement = "native platform frame cadence and handler time; includes pacing, not GPU time",
            phases = PhaseResults,
            note = "Same save restored before each process. Weather/entities remain dynamic. Process allocation includes background threads. Reject mixed focus."
        }) + "\n");
        api.Logger.Notification("[OptiTime] Controlled native comparison complete: " + controlledOutput);
    }

    private static Dictionary<string, object> ReadSettings()
    {
        var type = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
        string[] names = ["MaxFPS", "VsyncMode", "ViewDistance", "ShadowMapQuality", "FieldOfView"];
        var result = names.ToDictionary(name => name,
            name => type == null ? "unavailable" : AccessTools.Property(type, name)?.GetValue(null) ?? "unavailable");
        result["FrameWidth"] = api!.Render.FrameWidth;
        result["FrameHeight"] = api.Render.FrameHeight;
        return result;
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(harmony.Id);
        api = null;
        platform = null;
        window = null;
        readFocus = null;
    }
}
