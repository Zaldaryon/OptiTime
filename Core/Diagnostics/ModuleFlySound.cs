using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleFlySound : IDiagModule
{
    public string ShortName => "flysound";
    public string DisplayName => "Fly Sound";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long callsTotal;
    private static long callsSuppressed;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref callsTotal, 0);
        Interlocked.Exchange(ref callsSuppressed, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref callsTotal);
        long suppressed = Interlocked.Read(ref callsSuppressed);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = total > 0 ? 100.0 * suppressed / total : 0;
        DiagLog.Line(api, "flysound", $"calls={total} suppressed={suppressed} ({pct:F1}%) elapsedS={elapsedS:F1}");
    }

    public static void OnCall(bool wasSuppressed)
    {
        if (!enabled) return;
        Interlocked.Increment(ref callsTotal);
        if (wasSuppressed) Interlocked.Increment(ref callsSuppressed);
    }
}
