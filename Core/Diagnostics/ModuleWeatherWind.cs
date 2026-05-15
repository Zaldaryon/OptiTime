using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleWeatherWind : IDiagModule
{
    public string ShortName => "weatherwind";
    public string DisplayName => "Weather Wind";
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
        DiagLog.Line(api, "weatherwind", $"ticks={total} skipped={skipped} ({pct:F1}%) elapsedS={elapsedS:F1}");
    }

    public static void OnTick(bool wasSkipped)
    {
        if (!enabled) return;
        Interlocked.Increment(ref ticksTotal);
        if (wasSkipped) Interlocked.Increment(ref ticksSkipped);
    }
}
