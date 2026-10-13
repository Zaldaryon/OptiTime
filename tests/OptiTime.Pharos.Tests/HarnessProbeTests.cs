using Zaldaryon.Pharos.Platform;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Proves the harness itself works before any assertion about the mod is
/// trusted. Every later class assumes these four things hold: the client boots,
/// chunks arrive, commands reach the mod, and the mod answers.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class HarnessProbeTests : OptiTimeScenarioBase
{
    [ClientServerScenario]
    public async Task ClientBootsWithAGpuContextAndTheModLoaded()
    {
        await WarmUpAsync();

        HeadlessClient client = Client!;

        // A fixture-mode client would satisfy the first two and fail the rest.
        // Engine mode is the only one that stages the mod at all.
        Assert.True(client.IsEngineMode, "The client booted in fixture mode; no mod was staged.");
        Assert.True(client.IsJoined, "The client never finished joining the server.");

        // Without a real GL context every rendering assertion below would be
        // reading a blank framebuffer and passing for the wrong reason.
        GlRendererInfo gl = Assert.IsType<GlRendererInfo>(client.RendererInfo);
        Assert.True(gl.SupportsCoreVersion(4, 3),
            $"OpenGL {gl.ParsedVersion} ({gl.Renderer}) is below the 4.3 that Pharos' " +
            "inspection APIs need. Every draw-call, shader and leak assertion in this " +
            "suite would silently degrade to a no-op on an older context.");

        Assert.True(LoadedChunks() > 0,
            "No chunks loaded. The client never left startup, so nothing measured " +
            "after this point would mean anything.");

        // The mod stages into the client only when the path in
        // OptiTimeConfig.ModAssembly actually resolves. A typo there produces a
        // client that boots perfectly and has no mod in it.
        Assert.True(client.Commands.Exists(OptiTimeConfig.ModId),
            $"The client has no '{OptiTimeConfig.ModId}' command. Either the mod did not load, " +
            $"or '{OptiTimeConfig.ModAssembly}' did not resolve to the built zip.");
    }

    [ClientServerScenario]
    public async Task ModLoadsAndRegistersItsCommand()
    {
        await WarmUpAsync();

        CommandOutput status = await RunCommandOkAsync(OptiTimeConfig.Command);

        Assert.False(string.IsNullOrWhiteSpace(status.Text),
            "'.optitime' succeeded but printed nothing. The player gets no feedback either.");
    }

    /// <summary>
    /// The diag framework writes to the log as well as to chat. If that channel
    /// is not there, every counter assertion in the suite silently compares
    /// against a missing dump and passes for the wrong reason, so it is checked
    /// on its own rather than being taken on trust.
    /// </summary>
    [ClientServerScenario]
    public async Task DiagCountersReachTheClientLog()
    {
        DiagCounters counters = await CaptureDiagAsync("occlusion", frames: 120);

        Assert.True(counters.Present,
            "'.optitime diag occlusion dump' printed no '[OptiTime/diag/occlusion]' line to the " +
            "client log. Every counter assertion in this suite parses that channel.");
    }
}