using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleShadowVeg : IDiagModule
{
    public string ShortName => "shadowveg";
    public string DisplayName => "Shadow Far Vegetation Cull";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long framesTotal;
    private static long framesCulled;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref framesTotal, 0);
        Interlocked.Exchange(ref framesCulled, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref framesTotal);
        long culled = Interlocked.Read(ref framesCulled);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        double elapsedS = elapsedMs / 1000.0;
        double pct = total > 0 ? 100.0 * culled / total : 0;
        DiagLog.Line(api, "shadowveg", $"shadowPasses={total} vegCulled={culled} ({pct:F1}%) elapsedS={elapsedS:F1}");
    }

    public static void OnShadowFrame(bool vegWasCulled)
    {
        if (!enabled) return;
        Interlocked.Increment(ref framesTotal);
        if (vegWasCulled) Interlocked.Increment(ref framesCulled);
    }
}
