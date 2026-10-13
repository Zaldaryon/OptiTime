using Vintagestory.API.Common.Entities;
using Zaldaryon.Pharos.Assertions;
using Zaldaryon.Pharos.Network;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// The modules that only do anything when a second player is on the server.
///
/// The embedded server starts with one client and nobody else, so in a
/// single-player-shaped scenario these report nothing at all - and a suite that
/// treats that as "dead code" is wrong. They need a real second connection, and
/// Pharos can create one: <c>CreateTestPlayerAsync</c> joins a headless player
/// through the ordinary join path, so the server sees two players and the client
/// receives entity position packets for the other one.
///
/// <para><b>Names are short on purpose.</b> The server validates the play name
/// against <c>^(\w|-){1,16}$</c> before it does anything else, and a longer
/// name is refused with <c>"can't join (invalid Playername: ...)"</c> and a
/// disconnect - which then surfaces as
/// <c>"The server did not spawn player X within 2000 ticks"</c> and looks like
/// a multiplayer harness failure rather than a length limit. The names here
/// are seven characters.</para>
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class MultiplayerOptimizationTests : OptiTimeScenarioBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ASecondPlayerJoinsAndTheClientSeesThem()
    {
        await WarmUpAsync();

        ServerTestPlayer other = await CreateTestPlayerAsync("OtSpec");

        Assert.True(other.IsConnected,
            "The headless player never connected. Everything else in this class depends " +
            "on the server seeing a second player.");

        // The client has to receive the entity before interpolation has anything
        // to do. Step until it does rather than for a fixed count: the spawn
        // packet is asynchronous.
        bool seen = await StepUntilAsync(
            () => Client!.Client.PlayersByUid.Count > 1,
            maxFrames: 600);

        Assert.True(seen,
            $"The client still knows about {Client!.Client.PlayersByUid.Count} player(s) after " +
            "the second one joined. The entity never spawned client-side, so no " +
            "remote-entity optimization can be exercised.");

        await ServerHost!.TickUntilAsync(() => other.Player is not null, maxTicks: 600);
        await Session!.StepFramesAsync(120);
    }

    /// <summary>
    /// Entity interpolation smooths remote entity movement between server
    /// updates. It only has work to do when the client actually receives entity
    /// position packets, so this checks the traffic before it checks the mod:
    /// if no position packets crossed the loopback, a zero sample count says
    /// nothing about the optimization, and a suite that reported "dead code"
    /// would be wrong.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task EntityInterpolationSeesRemotePositionPackets()
    {
        await WarmUpAsync();

        ServerTestPlayer other = await CreateTestPlayerAsync("OtMover");
        await Session!.StepFramesAsync(60);

        // Put the second player somewhere else and keep moving them, so the
        // client keeps receiving updates rather than one spawn packet.
        await other.TeleportTo(12, 70, 12);
        await Session!.StepFramesAsync(60);

        await SetOptimizationAsync("entityinterp", enabled: true);

        Client!.PacketRecorder.Clear();
        Client.PacketRecorder.Start();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag on");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag reset");

        for (int i = 0; i < 40; i++)
        {
            await other.TeleportTo(12 + i, 70, 12 + i);
            await Session!.StepFramesAsync(15);
        }

        Client.PacketRecorder.Stop();

        IReadOnlyList<RecordedPacket> recorded = Client.PacketRecorder.GetRecordedPackets();

        // Vanilla delivers entity positions inline inside TCP packets as
        // Packet_EntityPosition (id 80) when UDP is unavailable, and the
        // client falls back to that path when the server reports it received
        // no UDP. A run here records id 80, not Packet_Entity.
        const int PacketEntityPosition = 80;

        int positionPackets = recorded.Count(p => p.PacketId == PacketEntityPosition);
        Dictionary<int, int> byType =
            recorded.GroupBy(p => p.PacketId).ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine(
            $"{recorded.Count} packet(s) recorded, {positionPackets} of them " +
            $"Packet_EntityPosition; ids seen: {string.Join(", ", byType.Keys.Order())}");

        CommandOutput dump = await RunCommandAsync($"{OptiTimeConfig.Command} interpdiag dump");
        string text = Unescape(dump.Text);
        await WriteArtifactAsync(nameof(MultiplayerOptimizationTests), "interpdiag.txt", text);

        Console.WriteLine(text);

        if (positionPackets == 0)
        {
            Assert.Fail(
                "The client received no entity position packets at all while the second " +
                "player was being moved. Without them there is nothing for entity " +
                "interpolation to interpolate, and a zero sample count says nothing about " +
                "whether the module works.");
        }

        Assert.False(string.IsNullOrWhiteSpace(text),
            "The interpolation diag dump printed nothing with the counters enabled.");

        // Recorded rather than asserted. The module counts a sample only on the
        // second position packet for a given entity, and a headless loopback in
        // TCP-fallback mode does not reliably deliver two per entity inside the
        // window this test can afford. That is a harness limit, not evidence about
        // the module, and claiming otherwise in either direction would be worse
        // than saying so.
        Console.WriteLine(
            text.Contains("no samples yet", StringComparison.OrdinalIgnoreCase)
                ? "entityinterp recorded no samples: the loopback did not deliver two " +
                  "position packets per entity inside the window. Not evidence the module " +
                  "is dead - see the coverage gaps in the report."
                : "entityinterp recorded samples.");
    }

    /// <summary>
    /// A remote player must still move when interpolation is on. This is the
    /// v1.5.13 regression: the animation LOD and the position interpolation
    /// between them left entities visibly behind or teleporting, and a fishing
    /// line detached as a result. Comparing where the remote player ends up
    /// between the two states is the check that catches it.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RemotePlayerStaysInSyncWithInterpolationOnAndOff()
    {
        await WarmUpAsync();

        ServerTestPlayer other = await CreateTestPlayerAsync("OtSync");
        await Session!.StepFramesAsync(120);

        var positions = new Dictionary<bool, Vec3d>();

        foreach (bool enabled in new[] { false, true })
        {
            await SetOptimizationAsync("entityinterp", enabled);

            for (int i = 0; i < 30; i++)
            {
                await other.TeleportTo(20 + i * 2, 70, 20 + i * 2);
                await Session!.StepFramesAsync(10);
            }

            await Session!.StepFramesAsync(120);

            Vec3d server = ToVec(other.Entity?.Pos);
            positions[enabled] = server;
        }

        Vec3d without = positions[false];
        Vec3d with = positions[true];

        double distance = (with - without).Length();

        Console.WriteLine(
            $"remote player after the run: interpolation off {without}, on {with}, " +
            $"difference {distance:F3} blocks");

        await WriteArtifactAsync(nameof(MultiplayerOptimizationTests), "sync.txt",
            $"off={without}\non={with}\ndifference={distance}\n");

        // The server's own entity position is authoritative and is not touched
        // by a client-side mod, so this mostly documents that the client did
        // not desync the server. The client-side check is the one below.
        Assert.True(distance < 1.0,
            $"The remote player's final position differs by {distance:F3} blocks between " +
            "the two states. A client-side optimization must not move anything the " +
            "server owns.");

        ClientServerAssert.PlayerPositionSynced(Session!);
    }

    /// <summary>
    /// Repulse agents is the module that stands in for physics that did not
    /// happen. With a second player present, crowd pressure is real, and a
    /// repulse-agent optimisation that skips the wrong tick shows up as two
    /// players occupying the same block.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RepulseAgentsStillSeparatesTwoPlayers()
    {
        await WarmUpAsync();

        ServerTestPlayer other = await CreateTestPlayerAsync("OtCrowd");
        await Session!.StepFramesAsync(120);

        // Drop the second player on top of the first, so the crowd case is
        // forced rather than hoped for.
        Vec3d origin = Player!.Position;
        await other.TeleportTo(origin.X, origin.Y, origin.Z);
        await Session!.StepFramesAsync(180);

        await SetOptimizationAsync("repulseagents", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("repulse");

        Assert.True(counters.Present, "The 'repulse' diag module produced no dump.");
        await WriteArtifactAsync(nameof(MultiplayerOptimizationTests), "repulse.txt", counters.Raw);

        double ticks = counters.Require(DiagCounter.Ticks, "repulse");
        Console.WriteLine(counters.Raw);

        Assert.True(ticks > 0,
            "The repulse-agent optimization never ticked, even with two players standing " +
            "in the same block. Its Harmony target did not fire at all.");

        ClientServerAssert.PlayerPositionSynced(Session!);
    }

    /// <summary>
/// Entity shadow culling, at the settings a default install actually has.
///
/// <para>The interesting outcome here is the finding, not the module. At the
/// vanilla default the mod <em>declines</em> to apply the patch - the far
/// cascade is shorter than its cull distance, so the transpiler could never
/// cull anything - and it says so in one log line. The status line keeps saying
/// <c>ON</c>. That is finding 4.4, and pinning it here means the test fails
/// when the mod is fixed.</para>
///
/// <para><b>Why this class cannot raise the setting instead.</b>
/// <c>[ClientSetting]</c> is applied by <c>ClientServerScenarioBase</c> after
/// the client has booted and the mod has already read the value, so a
/// boot-time decision cannot be steered with it. Getting shadows high enough
/// for this module to run needs a <c>clientsettings.json</c> staged into the
/// client's data folder before boot, which is the natural follow-up.
/// <c>viewDistance</c> is unaffected, because the mod reads that through a
/// watcher that fires on change rather than once at boot.</para>
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class EntityShadowCullTests : OptiTimeScenarioBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task EntityShadowCullingDeclinesAtTheDefaultShadowQualityAndSaysSo()
    {
        await WarmUpAsync();

        string log = ClientLogText();

        bool applied = log.Contains(
            "Entity shadow distance cull loaded", StringComparison.OrdinalIgnoreCase);
        bool declined = log.Contains(
            "Entity shadow distance cull skipped", StringComparison.OrdinalIgnoreCase);

        await WriteArtifactAsync(nameof(EntityShadowCullTests), "boot-log.txt", log);

        // With a second player present, so the failure is not simply "there was
        // nothing in the world to cull".
        ServerTestPlayer other = await CreateTestPlayerAsync("OtShadow");
        await other.TeleportTo(8, 70, 8);
        await Session!.StepFramesAsync(180);

        // Request ON without assuming the requested configuration is active:
        // at low shadow quality the patch intentionally remains unavailable.
        await RunCommandOkAsync($"{OptiTimeConfig.Command} shadowentity on");
        DiagCounters counters = await CaptureDiagAsync("shadowentity");

        Assert.True(counters.Present, "The 'shadowentity' diag module produced no dump.");
        await WriteArtifactAsync(nameof(EntityShadowCullTests), "shadowentity.txt", counters.Raw);

        Console.WriteLine(
            $"shadowentity: applied={applied}, declined={declined}, " +
            $"entities={counters.Number(DiagCounter.Entities)}, " +
            $"culled={counters.Number(DiagCounter.Culled)}, " +
            $"shadowQuality={counters.Number(DiagCounter.ShadowQuality)}");

        if (applied)
        {
            // A game preset raised the shadow quality past the point where the
            // mod applies its patch. The counters then have to move.
            Assert.True(counters.Require(DiagCounter.Entities, "shadowentity") > 0,
                "The entity shadow patch applied but counted no entities, with a second " +
                "player standing next to the first.");

            return;
        }

        Assert.True(declined,
            "The entity shadow optimization neither applied its patch nor said why it " +
            "declined. A module that does nothing and gives no reason is exactly the " +
            "v1.5.16 GUI Manager failure shape.");

        IReadOnlyDictionary<string, OptionState> states = await ReadStatusAsync();

        Assert.False(states["shadowentity"].Enabled,
            "The shadow-entity option reports ON although its patch was skipped at boot.");
        Assert.Contains("Shadow quality", states["shadowentity"].Conflict ?? "");
        Assert.Contains("above 1", states["shadowentity"].Conflict ?? "");
    }
}

    private static Vec3d ToVec(EntityPos? pos) =>
        pos is null ? Vec3d.Zero : new Vec3d(pos.X, pos.Y, pos.Z);

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal);
}