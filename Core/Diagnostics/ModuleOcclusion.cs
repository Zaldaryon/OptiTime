using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleOcclusion : IDiagModule
{
    public string ShortName => "occlusion";
    public string DisplayName => "Occlusion Culling";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long framesActive;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref framesActive, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long active = Interlocked.Read(ref framesActive);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double fps = elapsedS > 0 ? active / elapsedS : 0;
        DiagLog.Line(api, "occlusion", $"framesWithDynamicThreshold={active} elapsedS={elapsedS:F1} rate={fps:F0}/s");
    }

    public static void OnFrame(bool occlusionActive)
    {
        if (!enabled) return;
        if (occlusionActive)
            Interlocked.Increment(ref framesActive);
    }
}
