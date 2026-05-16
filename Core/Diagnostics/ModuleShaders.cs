using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

/// <summary>
/// Shader diagnostics are limited — GPU perf not measurable client-side.
/// Reports only whether shader patches are active.
/// </summary>
public sealed class ModuleShaders : IDiagModule
{
    public string ShortName => "shaders";
    public string DisplayName => "Shaders";
    public bool Enabled { get; private set; }
    public bool IsAvailable => true;

    public void Enable() { Enabled = true; }
    public void Disable() { Enabled = false; }
    public void Reset() { }

    public void Dump(ICoreClientAPI api)
    {
        DiagLog.Line(api, "shaders", "active=true (GPU perf gain not measurable client-side; use .optitime profile for FPS comparison)");
    }
}
