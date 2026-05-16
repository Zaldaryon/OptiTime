using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

/// <summary>
/// Diagnostic module for entity interpolation. Delegates to the existing
/// EntityInterpolationOptimization diagnostic state (histogram, F1 trigger rate).
/// </summary>
public sealed class ModuleEntityInterp : IDiagModule
{
    public string ShortName => "entityinterp";
    public string DisplayName => "Entity Interpolation";
    public bool Enabled { get; private set; }
    public bool IsAvailable => true;

    public void Enable()
    {
        Enabled = true;
        EntityInterpolationOptimization.SetDiagnosticEnabled(true);
    }

    public void Disable()
    {
        Enabled = false;
        EntityInterpolationOptimization.SetDiagnosticEnabled(false);
    }

    public void Reset()
    {
        EntityInterpolationOptimization.ResetDiagnostic();
    }

    public void Dump(ICoreClientAPI api)
    {
        EntityInterpolationOptimization.DumpDiagnostic(api);
    }
}
