using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleBgFps : IDiagModule
{
    public string ShortName => "bgfps";
    public string DisplayName => "Background FPS Limiter";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long framesFocused;
    private static long framesUnfocused;
    private static long unfocusedMs;
    private static long lastUnfocusedFrameMs;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref framesFocused, 0);
        Interlocked.Exchange(ref framesUnfocused, 0);
        Interlocked.Exchange(ref unfocusedMs, 0);
        Interlocked.Exchange(ref lastUnfocusedFrameMs, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long focused = Interlocked.Read(ref framesFocused);
        long unfocused = Interlocked.Read(ref framesUnfocused);
        long unfMs = Interlocked.Read(ref unfocusedMs);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double unfocusedFps = unfMs > 0 ? unfocused * 1000.0 / unfMs : 0;
        DiagLog.Line(api, "bgfps", $"focused={focused} unfocused={unfocused} unfocusedFps={unfocusedFps:F1} elapsedS={elapsedS:F1}");
    }

    public static void OnFrame(bool isFocused)
    {
        if (!enabled) return;
        long now = System.Environment.TickCount64;
        if (isFocused)
        {
            Interlocked.Increment(ref framesFocused);
            Interlocked.Exchange(ref lastUnfocusedFrameMs, 0);
        }
        else
        {
            Interlocked.Increment(ref framesUnfocused);
            long last = Interlocked.Read(ref lastUnfocusedFrameMs);
            if (last > 0)
                Interlocked.Add(ref unfocusedMs, now - last);
            Interlocked.Exchange(ref lastUnfocusedFrameMs, now);
        }
    }
}
