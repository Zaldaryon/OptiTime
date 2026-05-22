using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

/// <summary>
/// Reads frame pacing stats from ProfilingHelper. No new hooks needed.
/// </summary>
public sealed class ModuleFramePace : IDiagModule
{
    public string ShortName => "framepace";
    public string DisplayName => "Frame Pacing";
    public bool Enabled { get; private set; }
    public bool IsAvailable { get; }

    public ModuleFramePace(bool available) => IsAvailable = available;

    public void Enable()
    {
        if (!IsAvailable) return;
        Enabled = true;
        ProfilingHelper.SetFramePacingDiagEnabled(true);
    }

    public void Disable() { Enabled = false; ProfilingHelper.SetFramePacingDiagEnabled(false); }
    public void Reset() { ProfilingHelper.ResetFramePacing(); }

    public void Dump(ICoreClientAPI api)
    {
        if (!IsAvailable)
        {
            DiagLog.Line(api, "framepace", "DISABLED in config (PreciseFramePacingEnabled=false)");
            return;
        }
        var (precise, fallback, sleepMs, avgOvershoot, maxOvershoot, p99Overshoot) = ProfilingHelper.GetFramePacingStats();
        DiagLog.Line(api, "framepace", $"precise={precise} fallback={fallback} sleepMs={sleepMs} avgOvershoot={avgOvershoot:F2} p99Overshoot={p99Overshoot:F2} maxOvershoot={maxOvershoot:F2}");
    }
}
