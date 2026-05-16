using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleGuiMgr : IDiagModule
{
    public string ShortName => "guimgr";
    public string DisplayName => "GUI Manager";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long renderCalls;
    private static long renderCallsNoLinq;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref renderCalls, 0);
        Interlocked.Exchange(ref renderCallsNoLinq, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref renderCalls);
        long noLinq = Interlocked.Read(ref renderCallsNoLinq);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = total > 0 ? 100.0 * noLinq / total : 0;
        DiagLog.Line(api, "guimgr", $"renderCalls={total} noLinqPath={noLinq} ({pct:F1}%) elapsedS={elapsedS:F1}");
    }

    public static void OnRender(bool usedNoLinqPath)
    {
        if (!enabled) return;
        Interlocked.Increment(ref renderCalls);
        if (usedNoLinqPath) Interlocked.Increment(ref renderCallsNoLinq);
    }
}
