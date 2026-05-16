using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleParticles : IDiagModule
{
    public string ShortName => "particles";
    public string DisplayName => "Particles";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long spawnsRequested;
    private static long spawnsAllowed;
    private static long spawnsCulledViewDist;
    private static long spawnsCulledFrustum;
    private static long spawnsCulledOccupancy;
    private static long spawnsCulledFrameBudget;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref spawnsRequested, 0);
        Interlocked.Exchange(ref spawnsAllowed, 0);
        Interlocked.Exchange(ref spawnsCulledViewDist, 0);
        Interlocked.Exchange(ref spawnsCulledFrustum, 0);
        Interlocked.Exchange(ref spawnsCulledOccupancy, 0);
        Interlocked.Exchange(ref spawnsCulledFrameBudget, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long req = Interlocked.Read(ref spawnsRequested);
        long allowed = Interlocked.Read(ref spawnsAllowed);
        long vd = Interlocked.Read(ref spawnsCulledViewDist);
        long fr = Interlocked.Read(ref spawnsCulledFrustum);
        long occ = Interlocked.Read(ref spawnsCulledOccupancy);
        long fb = Interlocked.Read(ref spawnsCulledFrameBudget);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = req > 0 ? 100.0 * allowed / req : 0;
        DiagLog.Line(api, "particles", $"requested={req} allowed={allowed} ({pct:F1}%) culled byVD={vd} byFrustum={fr} byOccupancy={occ} byBudget={fb} elapsedS={elapsedS:F1}");
    }

    public static void OnSpawn(bool allowed, bool culledByViewDist, bool culledByFrustum, bool culledByOccupancy, bool culledByFrameBudget)
    {
        if (!enabled) return;
        Interlocked.Increment(ref spawnsRequested);
        if (allowed) Interlocked.Increment(ref spawnsAllowed);
        if (culledByViewDist) Interlocked.Increment(ref spawnsCulledViewDist);
        if (culledByFrustum) Interlocked.Increment(ref spawnsCulledFrustum);
        if (culledByOccupancy) Interlocked.Increment(ref spawnsCulledOccupancy);
        if (culledByFrameBudget) Interlocked.Increment(ref spawnsCulledFrameBudget);
    }
}
