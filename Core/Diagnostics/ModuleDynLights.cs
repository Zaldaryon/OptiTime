using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleDynLights : IDiagModule
{
    public string ShortName => "dynlights";
    public string DisplayName => "Dynamic Lights";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long framesTotal;
    private static long entitiesProcessed;
    private static long entitiesOverflow;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref framesTotal, 0);
        Interlocked.Exchange(ref entitiesProcessed, 0);
        Interlocked.Exchange(ref entitiesOverflow, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long frames = Interlocked.Read(ref framesTotal);
        long processed = Interlocked.Read(ref entitiesProcessed);
        long overflow = Interlocked.Read(ref entitiesOverflow);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double avgPerFrame = frames > 0 ? (double)processed / frames : 0;
        DiagLog.Line(api, "dynlights", $"frames={frames} entitiesLit={processed} avgPerFrame={avgPerFrame:F1} overflow={overflow} elapsedS={elapsedS:F1}");
    }

    /// <param name="rendered">Entities actually rendered as lights (capped by maxDynLights)</param>
    /// <param name="found">Total light-emitting entities found in radius</param>
    public static void OnFrame(int rendered, int found)
    {
        if (!enabled) return;
        Interlocked.Increment(ref framesTotal);
        Interlocked.Add(ref entitiesProcessed, rendered);
        if (found > rendered)
            Interlocked.Add(ref entitiesOverflow, found - rendered);
    }
}
