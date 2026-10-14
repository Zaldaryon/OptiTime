using System.Collections;
using System.Reflection;
using Vintagestory.Client.Network;
using Vintagestory.Common;
using Vintagestory.Common.Network.Packets;

namespace OptiTime.Pharos.Tests;

[CollectionDefinition("Harness patch isolation", DisableParallelization = true)]
public sealed class HarnessPatchTestCollection
{
}

[Collection("Harness patch isolation")]
public class HarnessPatchTests
{
    [Fact]
    public void PrefixDeliversPacketWhenTheSharedReceiveLockExists()
    {
        DummyNetwork network = new();
        network.Start();
        HarnessPatches.Attach(network);
        HarnessPatches.ResetCounters();

        DummyUdpNetClient client = new();
        Packet_UdpPacket packet = new();

        Assert.False(HarnessPatches.UdpEnqueuePatch.Prefix(client, packet));

        Assert.Equal(1, HarnessPatches.Delivered);
        Assert.Equal(0, HarnessPatches.Dropped);
        Assert.Same(packet, Assert.Single(client.ReadMessage()));
    }

    [Fact]
    public void StopTrackingDrainsPositionObservations()
    {
        DummyNetwork network = new();
        network.Start();
        HarnessPatches.Attach(network);
        HarnessPatches.ResetCounters();
        HarnessPatches.TrackEntityPositions(42);

        Packet_UdpPacket packet = new()
        {
            EntityPosition = new Packet_EntityPosition { EntityId = 42, X = 3, Tick = 1 },
        };
        HarnessPatches.UdpEnqueuePatch.Prefix(new DummyUdpNetClient(), packet);

        Assert.Equal(1, HarnessPatches.PendingPositionObservationCount);
        HarnessPatches.StopTrackingEntityPositions(42);
        Assert.Equal(0, HarnessPatches.PendingPositionObservationCount);
    }

    [Fact]
    public void PrefixRecordsOnlyTheTrackedEntityPositionFromBulkUdpPackets()
    {
        DummyNetwork network = new();
        network.Start();
        HarnessPatches.Attach(network);
        HarnessPatches.ResetCounters();
        HarnessPatches.TrackEntityPositions(42);

        Packet_BulkEntityPosition bulk = new();
        bulk.SetEntityPositions(
        [
            new Packet_EntityPosition { EntityId = 41, X = 1, Tick = 1 },
            new Packet_EntityPosition { EntityId = 42, X = 2, Tick = 2 },
        ]);
        Packet_UdpPacket packet = new() { BulkPositions = bulk };

        Assert.False(HarnessPatches.UdpEnqueuePatch.Prefix(new DummyUdpNetClient(), packet));

        HarnessPatches.EntityPositionObservation observation =
            Assert.Single(HarnessPatches.PositionObservationsFor(42));
        Assert.Equal(new HarnessPatches.EntityPositionObservation(2, 0, 0, 2), observation);
        Assert.Empty(HarnessPatches.PositionObservationsFor(41));

        HarnessPatches.StopTrackingEntityPositions(42);
    }

    [Fact]
    public void PrefixCountsPacketAsDroppedWhenTheSharedReceiveLockIsMissing()
    {
        DummyNetwork network = new();
        network.Start();
        HarnessPatches.Attach(network);
        HarnessPatches.ResetCounters();

        FieldInfo receiveLock = typeof(DummyNetwork).GetField(
            "ClientReceiveBufferLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        receiveLock.SetValue(network, null);

        DummyUdpNetClient client = new();
        Packet_UdpPacket packet = new();

        Assert.False(HarnessPatches.UdpEnqueuePatch.Prefix(client, packet));

        Assert.Equal(0, HarnessPatches.Delivered);
        Assert.Equal(1, HarnessPatches.Dropped);

        FieldInfo receiveBuffer = typeof(DummyNetwork).GetField(
            "ClientReceiveBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IEnumerable packets = (IEnumerable)receiveBuffer.GetValue(network)!;
        Assert.Empty(packets.Cast<object>());
    }

    [Fact]
    public void PrefixCountsConcurrentPacketDeliveriesWithoutLoss()
    {
        DummyNetwork network = new();
        network.Start();
        HarnessPatches.Attach(network);
        HarnessPatches.ResetCounters();

        DummyUdpNetClient client = new();
        const int packetCount = 64;

        Parallel.For(0, packetCount, _ =>
            HarnessPatches.UdpEnqueuePatch.Prefix(client, new Packet_UdpPacket()));

        Assert.Equal(packetCount, HarnessPatches.Delivered);
        Assert.Equal(0, HarnessPatches.Dropped);
        Assert.Equal(packetCount, client.ReadMessage().Count());
    }
}
