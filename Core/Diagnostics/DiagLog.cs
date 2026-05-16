using Vintagestory.API.Client;

namespace OptiTime.Diagnostics;

public static class DiagLog
{
    public static void Header(ICoreClientAPI api, string title)
    {
        string line = $"=== [OptiTime] {title} ===";
        api.ShowChatMessage(line);
        api.Logger.Notification(line);
    }

    public static void Line(ICoreClientAPI api, string moduleShortName, string content)
    {
        string line = $"[OptiTime/diag/{moduleShortName}] {content}";
        api.ShowChatMessage(line);
        api.Logger.Notification(line);
    }

    public static void Footer(ICoreClientAPI api)
    {
        string line = "=== end diag dump ===";
        api.ShowChatMessage(line);
        api.Logger.Notification(line);
    }
}
