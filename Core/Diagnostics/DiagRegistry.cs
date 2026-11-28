using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public static class DiagRegistry
{
    private static readonly Dictionary<string, IDiagModule> modules = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IDiagModule module) => modules[module.ShortName] = module;
    public static IDiagModule Get(string shortName) => modules.GetValueOrDefault(shortName);
    public static IEnumerable<IDiagModule> All => modules.Values;

    public static void EnableAll()
    {
        foreach (var m in modules.Values)
            if (m.IsAvailable) m.Enable();
    }

    public static void DisableAll()
    {
        foreach (var m in modules.Values) m.Disable();
    }

    public static void ResetAll()
    {
        foreach (var m in modules.Values) m.Reset();
    }

    public static void DumpAll(ICoreClientAPI api)
    {
        DiagLog.Header(api, "diagnostic dump (all modules)");
        // Unavailable modules can never be enabled, so they are only listed while some module is on.
        bool anyEnabled = modules.Values.Any(m => m.Enabled);
        foreach (var m in modules.Values)
        {
            if (!m.IsAvailable)
            {
                if (anyEnabled) DiagLog.Line(api, m.ShortName, "unavailable");
                continue;
            }
            if (!m.Enabled) continue;
            m.Dump(api);
        }
        DiagLog.Footer(api);
    }

    public static void ListModules(ICoreClientAPI api)
    {
        api.ShowChatMessage("[OptiTime/diag] available modules (* = enabled):");
        foreach (var m in modules.Values)
        {
            string marker = m.Enabled ? "*" : " ";
            string avail = m.IsAvailable ? "" : " [unavailable]";
            api.ShowChatMessage($"  {m.ShortName}{marker}  {m.DisplayName}{avail}");
        }
    }

    public static void Clear() => modules.Clear();
}
