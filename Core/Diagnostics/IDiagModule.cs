using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public interface IDiagModule
{
    string ShortName { get; }
    string DisplayName { get; }
    bool Enabled { get; }
    bool IsAvailable { get; }
    void Enable();
    void Disable();
    void Reset();
    void Dump(ICoreClientAPI api);
}
