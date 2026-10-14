using System.Collections.Concurrent;
using System.Reflection;
using Vintagestory.Common.Network.Packets;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Zaldaryon.Pharos.Network;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Exercises position delivery for a tracked vanilla entity through the Pharos loopback.
/// This scenario uses a superflat world to keep world startup bounded.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: "creativebuilding",
    worldType: "superflat")]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class PharosLoopbackTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Moves a server-owned vanilla entity in range and verifies that changing position
    /// snapshots reach the client's interpolation handler over UDP or TCP fallback.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task TrackedEntityPositionPacketsReachInterpolationOverLoopback()
    {
        Assert.True(HarnessPatches.IsUdpEnqueuePatchInstalled,
            "The Pharos UDP receive patch was not installed in the engine process.");

        await Session!.StepFramesAsync(60);

        Vec3d origin = Player!.Position;
        Entity mover = ServerHost!.RunOnGameThread(() =>
        {
            ICoreServerAPI serverApi = (ICoreServerAPI)ServerHost.Server.Api;
            EntityProperties type = serverApi.World.GetEntityType(
                new AssetLocation("game:chicken-hen"))!;
            Entity entity = serverApi.World.ClassRegistry.CreateEntity(type);
            entity.Pos.SetPos(origin.AddCopy(4, 0, 4));
            serverApi.World.SpawnEntity(entity);
            return entity;
        });

        bool clientSawMover = await StepUntilAsync(
            () => Client!.Client.World.GetEntityById(mover.EntityId) is not null,
            maxFrames: 300);
        Assert.True(clientSawMover,
            $"The client did not receive the spawned chicken (entity {mover.EntityId}) before movement.");

        await Session.StepFramesAsync(60);
        await SetOptimizationAsync("entityinterp", enabled: true);

        HarnessPatches.TrackEntityPositions(mover.EntityId);
        Client!.PacketRecorder.Clear();
        Client.PacketRecorder.Start();

        try
        {
            await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag on");
            await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag reset");

            for (int i = 0; i < 40; i++)
            {
                double x = origin.X + 4 + (i + 1) * 0.5;
                ServerHost!.RunOnGameThread(
                    () => mover.TeleportToDouble(x, origin.Y, origin.Z + 4));
                await Session.StepFramesAsync(15);
            }

            Client.PacketRecorder.Stop();

            IReadOnlyList<RecordedPacket> recorded = Client.PacketRecorder.GetRecordedPackets();
            List<HarnessPatches.EntityPositionObservation> tcpPositions =
                ReadTcpPositionsFor(recorded, mover.EntityId);
            IReadOnlyList<HarnessPatches.EntityPositionObservation> shimPositions =
                HarnessPatches.PositionObservationsFor(mover.EntityId);
            List<HarnessPatches.EntityPositionObservation> positions =
                tcpPositions.Concat(shimPositions).Distinct().ToList();

            Console.WriteLine(
                $"Entity {mover.EntityId}: {positions.Count} unique position snapshot(s), " +
                $"{tcpPositions.Count} in recorded TCP envelopes, " +
                $"{shimPositions.Count} delivered through the UDP receive shim; " +
                $"shim delivered={HarnessPatches.Delivered}, dropped={HarnessPatches.Dropped}.");

            Assert.Equal(0, HarnessPatches.Dropped);
            Assert.True(positions.Count > 0,
                $"No UDP or TCP position snapshot for moving entity {mover.EntityId} reached the client.");

            int distinctPositions = positions
                .Select(position => (position.X, position.Y, position.Z))
                .Distinct()
                .Count();
            Assert.True(distinctPositions > 1,
                $"The client received positions for entity {mover.EntityId}, but they never changed.");

            CommandOutput dump = await RunCommandAsync($"{OptiTimeConfig.Command} interpdiag dump");
            string text = Unescape(dump.Text);
            await WriteArtifactAsync(nameof(PharosLoopbackTests), "interpdiag.txt", text);
            Console.WriteLine(text);

            Assert.False(string.IsNullOrWhiteSpace(text),
                "The interpolation diagnostic command printed nothing.");

            IReadOnlyList<string> diagnosticLines = DiagReader.Lines(
                ClientLogText(), "entityinterp");
            DiagCounters interpolation = DiagReader.Parse(string.Join(' ', diagnosticLines));
            Assert.True(interpolation.Require("samples", "entityinterp") > 0,
                $"The interpolation handler recorded no repeated position samples. " +
                $"Diagnostic output: {interpolation.Raw}");

            long entitySamples = EntityInterpolationSamples(mover.EntityId);
            Console.WriteLine(
                $"Entity {mover.EntityId}: interpolation handler recorded {entitySamples} repeated samples.");
            Assert.True(entitySamples > 0,
                $"The interpolation handler recorded no repeated position samples for moving " +
                $"entity {mover.EntityId}; diagnostic output: {interpolation.Raw}");
        }
        finally
        {
            Client?.PacketRecorder.Stop();
            HarnessPatches.StopTrackingEntityPositions(mover.EntityId);
            DisableEntityInterpolationDiagnostics();
        }
    }

    private static long EntityInterpolationSamples(long entityId)
    {
        Type? interpolationType = EntityInterpolationType();
        FieldInfo? countsField = interpolationType?.GetField(
            "diagSamplesByEntityId", BindingFlags.NonPublic | BindingFlags.Static);
        var counts = countsField?.GetValue(null) as ConcurrentDictionary<long, long>;

        Assert.True(counts is not null,
            "OptiTime's per-entity interpolation diagnostic counter was not loaded.");
        return counts!.TryGetValue(entityId, out long samples) ? samples : 0;
    }

    private static void DisableEntityInterpolationDiagnostics()
    {
        MethodInfo? disable = EntityInterpolationType()?.GetMethod(
            "SetDiagnosticEnabled", BindingFlags.Public | BindingFlags.Static);
        disable?.Invoke(null, [false]);
    }

    private static Type? EntityInterpolationType()
    {
        Assembly? modAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "OptiTime");
        return modAssembly?.GetType("OptiTime.EntityInterpolationOptimization");
    }

    private static List<HarnessPatches.EntityPositionObservation> ReadTcpPositionsFor(
        IReadOnlyList<RecordedPacket> recorded,
        long entityId)
    {
        List<HarnessPatches.EntityPositionObservation> result = [];

        foreach (RecordedPacket recordedPacket in recorded)
        {
            if (recordedPacket.Direction != PacketDirection.Inbound ||
                recordedPacket.PacketId is not (79 or 80))
            {
                continue;
            }

            byte[] payload = recordedPacket.GetPayloadBytes();
            Packet_Server serverPacket = Packet_ServerSerializer.DeserializeBuffer(
                payload, payload.Length, new Packet_Server());

            if (serverPacket.Id == 80)
            {
                AddPosition(serverPacket.EntityPosition, entityId, result);
            }
            else if (serverPacket.Id == 79 && serverPacket.UdpPacket is { } udpPacket)
            {
                AddPosition(udpPacket.EntityPosition, entityId, result);

                if (udpPacket.BulkPositions is { } bulk && bulk.EntityPositions is { } positions)
                {
                    int count = Math.Min(bulk.GetEntityPositionsCount(), positions.Length);
                    for (int i = 0; i < count; i++)
                    {
                        AddPosition(positions[i], entityId, result);
                    }
                }
            }
        }

        return result;
    }

    private static void AddPosition(
        Packet_EntityPosition? position,
        long entityId,
        ICollection<HarnessPatches.EntityPositionObservation> into)
    {
        if (position?.EntityId == entityId)
        {
            into.Add(new(position.X, position.Y, position.Z, position.Tick));
        }
    }

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal);
}
