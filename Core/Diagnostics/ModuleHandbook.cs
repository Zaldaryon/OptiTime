using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleHandbook : IDiagModule
{
    public string ShortName => "handbook";
    public string DisplayName => "Handbook";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long cacheLookups;
    private static long cacheHits;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref cacheLookups, 0);
        Interlocked.Exchange(ref cacheHits, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long lookups = Interlocked.Read(ref cacheLookups);
        long hits = Interlocked.Read(ref cacheHits);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = lookups > 0 ? 100.0 * hits / lookups : 0;
        string indexStatus = HandbookOptimization.IsIndexed ? "ready" : "pending";
        DiagLog.Line(api, "handbook", $"indexer={indexStatus} cacheLookups={lookups} hits={hits} ({pct:F1}%) misses={lookups - hits} elapsedS={elapsedS:F1}");
    }

    public static void OnCacheLookup(bool wasHit)
    {
        if (!enabled) return;
        Interlocked.Increment(ref cacheLookups);
        if (wasHit) Interlocked.Increment(ref cacheHits);
    }
}
