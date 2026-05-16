using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleRepulse : IDiagModule
{
    public string ShortName => "repulse";
    public string DisplayName => "Repulse Agents";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long ticksTotal;
    private static long ticksSkipped;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref ticksTotal, 0);
        Interlocked.Exchange(ref ticksSkipped, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref ticksTotal);
        long skipped = Interlocked.Read(ref ticksSkipped);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = total > 0 ? 100.0 * skipped / total : 0;
        DiagLog.Line(api, "repulse", $"ticks={total} skipped={skipped} ({pct:F1}%) ranVanilla={total - skipped} elapsedS={elapsedS:F1}");
    }

    public static void OnTrigger()
    {
        if (!enabled) return;
        Interlocked.Increment(ref ticksTotal);
    }

    public static void OnSkipped()
    {
        if (!enabled) return;
        Interlocked.Increment(ref ticksSkipped);
    }
}
