using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleTickingBlocks : IDiagModule
{
    public string ShortName => "tickingblocks";
    public string DisplayName => "Ticking Blocks";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long allocsAvoided;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref allocsAvoided, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long allocs = Interlocked.Read(ref allocsAvoided);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double mbSaved = allocs * 24.0 / (1024 * 1024);
        double allocsPerSec = elapsedS > 0 ? allocs / elapsedS : 0;
        DiagLog.Line(api, "tickingblocks", $"allocsAvoided={allocs} gcBytesEst={mbSaved:F1}MB elapsedS={elapsedS:F1} rate={allocsPerSec:F0}/s");
    }

    public static void OnTick(int count)
    {
        if (!enabled) return;
        Interlocked.Add(ref allocsAvoided, count);
    }
}
