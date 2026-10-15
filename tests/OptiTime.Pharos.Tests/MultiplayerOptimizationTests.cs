using Vintagestory.API.Common.Entities;
using Vintagestory.Common;
using Vintagestory.Server;
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
    /// Sends non-teleport position snapshots through the game's authoritative
    /// server handler for the headless player and samples the remote entity on
    /// the rendering client. Pharos 0.6 has no movement driver for its
    /// headless player, so this calls the vanilla packet handler directly; the
    /// handler's resulting UDP replication still travels through the real
    /// embedded-server/client connection and reaches the Harmony target. The
    /// Harmony patch is loaded at startup; changing its saved setting below
    /// does not unload it, so this does not compare against vanilla
    /// interpolation or model the reporter's multi-hour save/modlist.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RemoteClientTracksServerPositionDuringRepeatedMoves()
    {
        await WarmUpAsync();

        ServerTestPlayer other = await CreateTestPlayerAsync("OtTrack");
        Assert.True(await StepUntilAsync(
                () => Client!.Client.World.GetEntityById(other.Entity!.EntityId) is not null,
                maxFrames: 600),
            "The second player's entity never reached the client.");
        long remoteEntityId = other.Entity!.EntityId;

        Vec3d origin = Player!.Position;
        await other.TeleportTo(origin.X + 5, origin.Y, origin.Z);
        await Session!.StepFramesAsync(30);
        await WithOptimizationStateRestoredAsync("entityinterp", async () =>
        {
            var samples = new List<string>();
            double maxDistance = 0;
            await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag on");
            await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag reset");

            long droppedBefore = HarnessPatches.EnqueueShimDropped;
            long deliveredBefore = HarnessPatches.EnqueueShimDelivered;
            HarnessPatches.TrackEntityPositions(remoteEntityId);
            Assert.True(HarnessPatches.IsEntityPositionReceivePatchInstalled,
                "The test observer did not patch EntityBehaviorInterpolatePosition.OnReceivedServerPos.");
            Client!.PacketRecorder.Clear();
            Client.PacketRecorder.Start();
            try
            {
                foreach ((bool settingEnabled, double direction) in new[] { (false, 1.0), (true, -1.0) })
                {
                    await SetOptimizationAsync("entityinterp", settingEnabled);

                    for (int i = 0; i < 12; i++)
                    {
                        int observedBefore = HarnessPatches.ReceivedPositionsFor(remoteEntityId).Count;
                        int shimPositionCountBefore =
                            HarnessPatches.PositionObservationsFor(remoteEntityId).Count;
                        int tcpPositionCountBefore = PharosLoopbackTests.ReadTcpPositionsFor(
                            Client.PacketRecorder.GetRecordedPackets(), remoteEntityId).Count;
                        int clientTickBefore = Client.RunOnClientThread(() =>
                        {
                            Entity entity = Client.Client.World.GetEntityById(remoteEntityId)
                                ?? throw new InvalidOperationException("The remote player disappeared from the client world.");
                            return entity.Attributes.GetInt("tick");
                        });
                        var move = ServerHost!.RunOnGameThread(() =>
                        {
                            Entity entity = other.Entity
                                ?? throw new InvalidOperationException("The remote test player has no server entity.");
                            Assert.False(entity.IsTeleport,
                                "A previous remote-player move left the game in teleport mode.");

                            int serverTickBefore = entity.Attributes.GetInt("tick");
                            EntityPos requestedPosition = entity.Pos.Copy();
                            requestedPosition.X += direction * 0.5;
                            Packet_EntityPosition packet = ServerPackets.getEntityPositionPacket(
                                requestedPosition,
                                entity,
                                serverTickBefore + 1);
                            ServerHost.Server.ServerUdpNetwork.HandlePlayerPosition(
                                packet,
                                (ServerPlayer)(other.Player
                                    ?? throw new InvalidOperationException("The remote test player has no server player.")));

                            Packet_EntityPosition expectedSnapshot = ServerPackets.getEntityPositionPacket(
                                entity.Pos,
                                entity,
                                entity.Attributes.GetInt("tick"));
                            return (
                                Requested: ToVec(requestedPosition),
                                Accepted: ToVec(entity.Pos),
                                ExpectedX: expectedSnapshot.X,
                                ExpectedY: expectedSnapshot.Y,
                                ExpectedZ: expectedSnapshot.Z,
                                ExpectedTick: expectedSnapshot.Tick,
                                ServerTickBefore: serverTickBefore);
                        });
                        Assert.True((move.Accepted - move.Requested).Length() < 0.001,
                            $"The vanilla server rejected the requested move at settingEnabled={settingEnabled}, step={i}: " +
                            $"requested={move.Requested}, accepted={move.Accepted}.");
                        bool matchingSnapshotArrived = await StepUntilAsync(() =>
                        {
                            IReadOnlyList<HarnessPatches.ReceivedEntityPositionObservation> observations =
                                HarnessPatches.ReceivedPositionsFor(remoteEntityId);
                            return observations.Skip(observedBefore).Any(observation =>
                                observation.X == move.ExpectedX &&
                                observation.Y == move.ExpectedY &&
                                observation.Z == move.ExpectedZ);
                        }, maxFrames: 60);
                        if (!matchingSnapshotArrived)
                        {
                            IReadOnlyList<HarnessPatches.EntityPositionObservation> shimPositions =
                                HarnessPatches.PositionObservationsFor(remoteEntityId);
                            IReadOnlyList<HarnessPatches.ReceivedEntityPositionObservation> receivedPositions =
                                HarnessPatches.ReceivedPositionsFor(remoteEntityId);
                            List<HarnessPatches.EntityPositionObservation> tcpPositions =
                                PharosLoopbackTests.ReadTcpPositionsFor(
                                    Client.PacketRecorder.GetRecordedPackets(), remoteEntityId);
                            int clientTickAfter = Client.RunOnClientThread(() =>
                            {
                                Entity entity = Client.Client.World.GetEntityById(remoteEntityId)
                                    ?? throw new InvalidOperationException("The remote player disappeared from the client world.");
                                return entity.Attributes.GetInt("tick");
                            });
                            Vec3d clientPositionAfter = Client.RunOnClientThread(() =>
                            {
                                Entity entity = Client.Client.World.GetEntityById(remoteEntityId)
                                    ?? throw new InvalidOperationException("The remote player disappeared from the client world.");
                                return ToVec(entity.Pos);
                            });
                            int currentServerTick = ServerHost.RunOnGameThread(() =>
                                other.Entity?.Attributes.GetInt("tick") ?? -1);
                            string Observed<T>(IReadOnlyList<T> values, System.Func<T, string> format) =>
                                values.Count == 0 ? "none" : string.Join("; ", values.Select(format).TakeLast(4));
                            string failureDetails =
                                $"The accepted move did not reach the client's interpolation handler at " +
                                $"settingEnabled={settingEnabled}, step={i}; " +
                                $"requested={move.Requested}, accepted={move.Accepted}, " +
                                $"expectedPacket=({move.ExpectedX},{move.ExpectedY},{move.ExpectedZ},tick={move.ExpectedTick}), " +
                                $"serverTickBefore={move.ServerTickBefore}, serverTickAfter={currentServerTick}, " +
                                $"clientTickBefore={clientTickBefore}, clientTickAfter={clientTickAfter}, " +
                                $"clientPos={clientPositionAfter}, shimDelivered={HarnessPatches.EnqueueShimDelivered - deliveredBefore}, " +
                                $"shimDropped={HarnessPatches.EnqueueShimDropped - droppedBefore}, " +
                                $"shimPositions=[{Observed(shimPositions, p => $"({p.X},{p.Y},{p.Z},tick={p.Tick})") }], " +
                                $"tcpPositions=[{Observed(tcpPositions, p => $"({p.X},{p.Y},{p.Z},tick={p.Tick})") }], " +
                                $"callbacks=[{Observed(receivedPositions, p => $"({p.X},{p.Y},{p.Z},tick={p.Tick})") }].";
                            Assert.True(matchingSnapshotArrived, failureDetails);
                        }

                        IReadOnlyList<HarnessPatches.EntityPositionObservation> shimPositionsForMove =
                            HarnessPatches.PositionObservationsFor(remoteEntityId).Skip(shimPositionCountBefore).ToArray();
                        List<HarnessPatches.EntityPositionObservation> tcpPositionsForMove =
                            PharosLoopbackTests.ReadTcpPositionsFor(
                                Client.PacketRecorder.GetRecordedPackets(), remoteEntityId)
                                .Skip(tcpPositionCountBefore)
                                .ToList();
                        bool matchingWirePacketArrived = shimPositionsForMove.Concat(tcpPositionsForMove).Any(packet =>
                            packet.X == move.ExpectedX && packet.Y == move.ExpectedY &&
                            packet.Z == move.ExpectedZ && packet.Tick == move.ExpectedTick);
                        Assert.True(matchingWirePacketArrived,
                            $"The expected position packet did not reach the client network path at " +
                            $"settingEnabled={settingEnabled}, step={i}; " +
                            $"expected=({move.ExpectedX},{move.ExpectedY},{move.ExpectedZ},tick={move.ExpectedTick}), " +
                            $"shim=[{string.Join("; ", shimPositionsForMove.Select(p => $"({p.X},{p.Y},{p.Z},tick={p.Tick})"))}], " +
                            $"tcp=[{string.Join("; ", tcpPositionsForMove.Select(p => $"({p.X},{p.Y},{p.Z},tick={p.Tick})"))}].");
                        await Session.StepFramesAsync(15);

                        Vec3d server = ServerHost.RunOnGameThread(() => ToVec(other.Entity?.Pos));
                        Vec3d client = Client!.RunOnClientThread(() =>
                        {
                            Entity? entity = Client.Client.World.GetEntityById(remoteEntityId);
                            return ToVec(entity?.Pos);
                        });
                        double distance = (client - server).Length();
                        maxDistance = Math.Max(maxDistance, distance);
                        samples.Add($"settingEnabled={settingEnabled} step={i} target={move.Requested} " +
                            $"server={server} client={client} error={distance:F3} " +
                            $"packetTick={move.ExpectedTick} serverTickBefore={move.ServerTickBefore} " +
                            $"clientTickBefore={clientTickBefore}");
                    }
                }

                long interpolationSamples = PharosLoopbackTests.EntityInterpolationSamples(remoteEntityId);
                int receivedSnapshots = HarnessPatches.ReceivedPositionsFor(remoteEntityId).Count;
                List<HarnessPatches.EntityPositionObservation> tcpPositionSnapshots =
                    PharosLoopbackTests.ReadTcpPositionsFor(
                        Client.PacketRecorder.GetRecordedPackets(), remoteEntityId);
                IReadOnlyList<HarnessPatches.EntityPositionObservation> shimPositionSnapshots =
                    HarnessPatches.PositionObservationsFor(remoteEntityId);
                int uniquePositionSnapshots = tcpPositionSnapshots.Concat(shimPositionSnapshots).Distinct().Count();
                long droppedAfter = HarnessPatches.EnqueueShimDropped;
                long deliveredAfter = HarnessPatches.EnqueueShimDelivered;
                await WriteArtifactAsync(nameof(MultiplayerOptimizationTests), "remote-client-tracking.txt",
                    string.Join('\n', samples) + $"\nmaxError={maxDistance:F3}\n" +
                    $"receivedInterpolationSnapshots={receivedSnapshots}\n" +
                    $"entityInterpolationSamples={interpolationSamples}\n" +
                    $"enqueueShimDelivered={deliveredAfter - deliveredBefore}\n" +
                    $"enqueueShimDrops={droppedAfter - droppedBefore}\n" +
                    $"shimPositionSnapshots={shimPositionSnapshots.Count}\n" +
                    $"tcpPositionSnapshots={tcpPositionSnapshots.Count}\n" +
                    $"uniquePositionSnapshots={uniquePositionSnapshots}\n");

                Assert.True(receivedSnapshots >= 24,
                    $"Expected a received position snapshot for each of the 24 accepted moves; got {receivedSnapshots}.");
                Assert.True(uniquePositionSnapshots >= 24,
                    $"Expected at least 24 unique TCP/UDP position snapshots; got {uniquePositionSnapshots}.");
                Assert.True(maxDistance < 0.75,
                    $"The remote client's position diverged by as much as {maxDistance:F3} blocks " +
                    "from the server after non-teleport position updates.");
                Assert.True(interpolationSamples >= 23,
                    $"The remote player {remoteEntityId} reached the interpolation handler only " +
                    $"{interpolationSamples} time(s), below 23 expected updates after the initial baseline.");
                Assert.Equal(droppedBefore, droppedAfter);
            }
            finally
            {
                Client?.PacketRecorder.Stop();
                HarnessPatches.StopTrackingEntityPositions(remoteEntityId);
                PharosLoopbackTests.DisableEntityInterpolationDiagnostics();
            }
        });
    }

    /// <summary>
    /// Confirms that the repulse-agent target remains live for a nearby remote
    /// player and that OptiTime reports its ticks. The game controls whether
    /// player-to-player collision is enabled, so this does not claim that two
    /// players must move apart.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RepulseAgentModuleCountsNearbyRemotePlayerTicks()
    {
        await WarmUpAsync();

        ServerTestPlayer other = await CreateTestPlayerAsync("OtCrowd");
        await Session!.StepFramesAsync(120);

        // Drop the second player on top of the first, so the crowd case is
        // forced rather than hoped for.
        Vec3d origin = Player!.Position;
        await other.TeleportTo(origin.X + 0.25, origin.Y, origin.Z);
        await Session!.StepFramesAsync(180);

        await WithOptimizationStateRestoredAsync("repulseagents", async () =>
        {
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
        });
    }

    /// <summary>
/// Entity shadow culling, at the settings a default install actually has.
///
/// <para>At the vanilla default the mod declines to apply the patch because the
/// far cascade is shorter than its cull distance. The test accepts this safe
/// decline only when startup logs the reason and the status reports the option
/// as disabled. If a game preset raises shadow quality enough for the patch to
/// apply, the test instead requires entity diagnostics.</para>
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
