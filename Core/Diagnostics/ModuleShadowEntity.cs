using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime.Diagnostics;

public sealed class ModuleShadowEntity : IDiagModule
{
    public string ShortName => "shadowentity";
    public string DisplayName => "Entity Shadow Distance Cull";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long entitiesConsidered;
    private static long entitiesCulled;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref entitiesConsidered, 0);
        Interlocked.Exchange(ref entitiesCulled, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref entitiesConsidered);
        long culled = Interlocked.Read(ref entitiesCulled);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = total > 0 ? 100.0 * culled / total : 0;
        int shadowQ = ClientSettings.ShadowMapQuality;
        int cascadeRange = shadowQ == 1 ? 60 : 150 + 120 * (shadowQ - 1);
        DiagLog.Line(api, "shadowentity", $"entities={total} culled={culled} ({pct:F1}%) shadowQuality={shadowQ} cascadeRange={cascadeRange} cullDist=80 elapsedS={elapsedS:F1}");
    }

    public static void OnEntity(bool wasCulled)
    {
        if (!enabled) return;
        Interlocked.Increment(ref entitiesConsidered);
        if (wasCulled) Interlocked.Increment(ref entitiesCulled);
    }
}
