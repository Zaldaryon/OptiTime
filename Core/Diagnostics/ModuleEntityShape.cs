using System.Threading;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public sealed class ModuleEntityShape : IDiagModule
{
    public string ShortName => "entityshape";
    public string DisplayName => "Entity Reshape Budget";
    public bool Enabled => enabled;
    public bool IsAvailable => true;

    private static volatile bool enabled;
    private static long reshapes;
    private static long deferrals;
    private static long afterWait;
    private static long startMs;

    public void Enable() { enabled = true; Reset(); }
    public void Disable() { enabled = false; }

    public void Reset()
    {
        Interlocked.Exchange(ref reshapes, 0);
        Interlocked.Exchange(ref deferrals, 0);
        Interlocked.Exchange(ref afterWait, 0);
        Interlocked.Exchange(ref startMs, System.Environment.TickCount64);
    }

    public void Dump(ICoreClientAPI api)
    {
        long started = Interlocked.Read(ref reshapes);
        long deferred = Interlocked.Read(ref deferrals);
        long waited = Interlocked.Read(ref afterWait);
        long elapsedMs = System.Environment.TickCount64 - Interlocked.Read(ref startMs);
        DiagLog.Line(api, "entityshape", $"reshapes={started} afterWait={waited} deferrals={deferred} waiting={EntityShapeOptimization.WaitingCount} elapsedS={elapsedMs / 1000.0:F1}");
    }

    /// <param name="waited">Whether the reshape had been deferred before it ran.</param>
    public static void OnReshape(bool waited)
    {
        if (!enabled) return;
        Interlocked.Increment(ref reshapes);
        if (waited) Interlocked.Increment(ref afterWait);
    }

    public static void OnDeferred()
    {
        if (!enabled) return;
        Interlocked.Increment(ref deferrals);
    }
}
