using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleEntityAnim : IDiagModule
{
    public string ShortName => "entityanim";
    public string DisplayName => "Entity Animations";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long entitiesConsidered;
    private static long animsFull;
    private static long animsLodMedium;
    private static long animsLodFar;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref entitiesConsidered, 0);
        Interlocked.Exchange(ref animsFull, 0);
        Interlocked.Exchange(ref animsLodMedium, 0);
        Interlocked.Exchange(ref animsLodFar, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref entitiesConsidered);
        long full = Interlocked.Read(ref animsFull);
        long medium = Interlocked.Read(ref animsLodMedium);
        long far = Interlocked.Read(ref animsLodFar);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        long skipped = medium + far;
        double pct = total > 0 ? 100.0 * skipped / total : 0;
        DiagLog.Line(api, "entityanim", $"entities={total} full={full} lodMedium={medium} lodFar={far} skipped={pct:F1}% elapsedS={elapsedS:F1}");
    }

    /// <param name="tier">0=close/always, 1=medium (every 2nd), 2=far (every 3rd)</param>
    /// <param name="updated">Whether animation was actually ticked this frame</param>
    public static void OnEntity(int tier, bool updated)
    {
        if (!enabled) return;
        Interlocked.Increment(ref entitiesConsidered);
        if (!updated)
        {
            if (tier == 1)
                Interlocked.Increment(ref animsLodMedium);
            else
                Interlocked.Increment(ref animsLodFar);
        }
        else if (tier == 0)
        {
            Interlocked.Increment(ref animsFull);
        }
    }
}
