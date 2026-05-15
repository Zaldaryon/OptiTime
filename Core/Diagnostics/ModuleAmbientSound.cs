using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleAmbientSound : IDiagModule
{
    public string ShortName => "ambientsound";
    public string DisplayName => "Ambient Sounds";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long callsTotal;
    private static long callsSkipped;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref callsTotal, 0);
        Interlocked.Exchange(ref callsSkipped, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref callsTotal);
        long skipped = Interlocked.Read(ref callsSkipped);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = total > 0 ? 100.0 * skipped / total : 0;
        DiagLog.Line(api, "ambientsound", $"calls={total} skipped={skipped} ({pct:F1}%) elapsedS={elapsedS:F1}");
    }

    public static void OnCall(bool wasSkipped)
    {
        if (!enabled) return;
        Interlocked.Increment(ref callsTotal);
        if (wasSkipped) Interlocked.Increment(ref callsSkipped);
    }
}
