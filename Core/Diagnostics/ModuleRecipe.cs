using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleRecipe : IDiagModule
{
    public string ShortName => "recipe";
    public string DisplayName => "Recipe Lookup";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long lookups;
    private static long hits;
    private static long invalidations;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref lookups, 0);
        Interlocked.Exchange(ref hits, 0);
        Interlocked.Exchange(ref invalidations, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long l = Interlocked.Read(ref lookups);
        long h = Interlocked.Read(ref hits);
        long inv = Interlocked.Read(ref invalidations);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = l > 0 ? 100.0 * h / l : 0;
        DiagLog.Line(api, "recipe", $"lookups={l} hits={h} ({pct:F1}%) misses={l - h} invalidations={inv} elapsedS={elapsedS:F1}");
    }

    public static void OnLookup(bool wasHit)
    {
        if (!enabled) return;
        Interlocked.Increment(ref lookups);
        if (wasHit) Interlocked.Increment(ref hits);
    }

    public static void OnInvalidation()
    {
        if (!enabled) return;
        Interlocked.Increment(ref invalidations);
    }
}
