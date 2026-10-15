using System.Reflection;
using System.Collections.Concurrent;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Common;
using Vintagestory.Common.Network.Packets;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Harness fixes for gaps in Pharos 0.6.0 that a populated world runs into.
///
/// These are not workarounds around a flaky test. Each one is a concrete defect
/// in the harness that stops the suite from running at all, and each patch is
/// deliberately narrow: if the shape of the game or harness changes, it is
/// left untouched rather than replaced with a partial patch.
///
/// <para><b>UDP over TCP.</b> Vintage Story 1.22 delivers datagrams that carry
/// entity positions and block updates inlined in TCP packets
/// (<c>Packet_ServerIdEnum.UdpPacket</c>, id 79). On the client,
/// <c>SystemNetworkProcess.EnqueueUdpPacket</c> hands the payload to
/// <c>DummyUdpNetClient.EnqueuePacket</c> - which in the installed game binary
/// throws <c>NotImplementedException</c>. The pinned Pharos 0.6.0 NuGet
/// package's linked socket overrides <c>Send</c> and <c>ReadMessage</c> but
/// not <c>EnqueuePacket</c>, so an entity update in a populated world can take
/// down the scenario from inside the client render loop. Current upstream adds
/// this override in commit <c>3691b70</c>.</para>
///
/// <para>For the pinned package, the prefix puts the payload where the socket's
/// own read path will find it - the shared <see cref="DummyNetwork"/>'s client
/// receive buffer - so the packet then travels the same loopback route,
/// including any configured network degradation, exactly as a datagram read
/// off a real socket would. If the receive lock is unavailable, the patch
/// records a drop instead of mutating the shared buffer without synchronization.</para>
/// </summary>
internal static class HarnessPatches
{
    private static readonly object Gate = new();
    private static readonly object PositionGate = new();
    private static readonly object PositionReceivePatchGate = new();
    private static bool _installed;
    private static bool s_udpEnqueuePatchInstalled;
    private static bool s_entityPositionReceivePatchInstalled;
    private static int s_positionReceiveObserverErrorLogged;
    private static long s_delivered;
    private static long s_dropped;
    private static long s_trackedEntityId = -1;
    private static readonly ConcurrentQueue<EntityPositionObservation> s_positionObservations = new();
    private static readonly ConcurrentQueue<ReceivedEntityPositionObservation> s_receivedPositions = new();

    private static FieldInfo? s_networkField;
    private static FieldInfo? s_clientReceiveBufferField;
    private static FieldInfo? s_clientReceiveLockField;
    private static MethodInfo? s_setNetwork;
    private static MethodInfo? s_enqueuePacket;

    /// <summary>
    /// The loopback's shared UDP dummy network, handed over by the scenario
    /// before the client joins.
    /// </summary>
    private static DummyNetwork? s_sharedUdp;

    /// <summary>How many datagrams the test enqueue shim delivered.</summary>
    public static long EnqueueShimDelivered => Interlocked.Read(ref s_delivered);

    /// <summary>How many datagrams the test enqueue shim dropped.</summary>
    public static long EnqueueShimDropped => Interlocked.Read(ref s_dropped);

    public static bool IsUdpEnqueuePatchInstalled => Volatile.Read(ref s_udpEnqueuePatchInstalled);

    public static int PendingPositionObservationCount
    {
        get
        {
            lock (PositionGate) return s_positionObservations.Count;
        }
    }

    internal readonly record struct EntityPositionObservation(long X, long Y, long Z, int Tick);
    internal readonly record struct ReceivedEntityPositionObservation(long X, long Y, long Z, int Tick);

    public static bool IsEntityPositionReceivePatchInstalled =>
        Volatile.Read(ref s_entityPositionReceivePatchInstalled);

    /// <summary>Tracks snapshots at the client's interpolation receive handler.</summary>
    public static void TrackEntityPositions(long entityId)
    {
        EnsureEntityPositionReceivePatch();
        lock (PositionGate)
        {
            while (s_positionObservations.TryDequeue(out _)) { }
            while (s_receivedPositions.TryDequeue(out _)) { }
            Volatile.Write(ref s_trackedEntityId, entityId);
        }
    }

    /// <summary>Stops retaining UDP position snapshots for the current probe.</summary>
    public static void StopTrackingEntityPositions(long entityId)
    {
        lock (PositionGate)
        {
            if (Volatile.Read(ref s_trackedEntityId) != entityId) return;
            Volatile.Write(ref s_trackedEntityId, -1);
            while (s_positionObservations.TryDequeue(out _)) { }
            while (s_receivedPositions.TryDequeue(out _)) { }
        }
    }

    public static IReadOnlyList<EntityPositionObservation> PositionObservationsFor(long entityId)
    {
        lock (PositionGate)
        {
            return Volatile.Read(ref s_trackedEntityId) == entityId
                ? s_positionObservations.ToArray()
                : Array.Empty<EntityPositionObservation>();
        }
    }

    public static IReadOnlyList<ReceivedEntityPositionObservation> ReceivedPositionsFor(long entityId)
    {
        lock (PositionGate)
        {
            return Volatile.Read(ref s_trackedEntityId) == entityId
                ? s_receivedPositions.ToArray()
                : Array.Empty<ReceivedEntityPositionObservation>();
        }
    }

    private static void EnsureEntityPositionReceivePatch()
    {
        if (IsEntityPositionReceivePatchInstalled) return;

        lock (PositionReceivePatchGate)
        {
            if (s_entityPositionReceivePatchInstalled) return;

            try
            {
                MethodInfo? target = AccessTools.Method(
                    "EntityBehaviorInterpolatePosition:OnReceivedServerPos",
                    [typeof(bool), typeof(EnumHandling).MakeByRefType()]);
                if (target is null)
                {
                    Console.WriteLine(
                        "[OptiTime.Pharos] Interpolation receive observer not installed: " +
                        "the game target EntityBehaviorInterpolatePosition.OnReceivedServerPos was not found.");
                    return;
                }

                new Harmony("optitime.pharos.harness").Patch(
                    target,
                    postfix: new HarmonyMethod(
                        typeof(EntityPositionReceivePatch),
                        nameof(EntityPositionReceivePatch.Postfix)));
                Volatile.Write(ref s_entityPositionReceivePatchInstalled, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[OptiTime.Pharos] Interpolation receive observer not installed ({ex.Message}).");
            }
        }
    }

    internal static class EntityPositionReceivePatch
    {
        public static void Postfix(object __instance)
        {
            long trackedEntityId = Volatile.Read(ref s_trackedEntityId);
            if (trackedEntityId < 0) return;

            try
            {
                object? entity = AccessTools.Field(__instance.GetType(), "entity")?.GetValue(__instance);
                if (entity is null) return;

                long entityId = Convert.ToInt64(ReadMember(entity, "EntityId"));
                if (entityId != trackedEntityId) return;

                object? position = ReadMember(entity, "Pos");
                object? attributes = ReadMember(entity, "Attributes");
                if (position is null || attributes is null) return;

                Type positionType = position.GetType();
                double x = Convert.ToDouble(ReadMember(position, "X"));
                double y = Convert.ToDouble(ReadMember(position, "Y"));
                double z = Convert.ToDouble(ReadMember(position, "Z"));
                int tick = Convert.ToInt32(AccessTools.Method(attributes.GetType(), "GetInt", [typeof(string)])
                    ?.Invoke(attributes, ["tick"]));

                lock (PositionGate)
                {
                    if (Volatile.Read(ref s_trackedEntityId) != entityId) return;
                    s_receivedPositions.Enqueue(new(
                        CollectibleNet.SerializeDoublePrecise(x),
                        CollectibleNet.SerializeDoublePrecise(y),
                        CollectibleNet.SerializeDoublePrecise(z),
                        tick));
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref s_positionReceiveObserverErrorLogged, 1) == 0)
                {
                    Console.WriteLine(
                        $"[OptiTime.Pharos] Interpolation receive observer could not record a position ({ex.Message}).");
                }
            }
        }

        private static object? ReadMember(object instance, string name)
        {
            PropertyInfo? property = AccessTools.Property(instance.GetType(), name);
            if (property is not null) return property.GetValue(instance);
            return AccessTools.Field(instance.GetType(), name)?.GetValue(instance);
        }
    }

    /// <summary>
    /// Installs the patches. Called from a module initializer so they are in
    /// place before the first client boots, which is before the first packet
    /// can arrive.
    /// </summary>
    public static void Ensure()
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;

            try
            {
                var harmony = new Harmony("optitime.pharos.harness");

                Type? socketType = AccessTools.TypeByName(
                    "Vintagestory.Client.Network.DummyUdpNetClient");

                MethodInfo? target = socketType is null
                    ? null
                    : AccessTools.Method(socketType, "EnqueuePacket", [typeof(Packet_UdpPacket)]);

                FieldInfo? networkField = socketType is null
                    ? null
                    : AccessTools.Field(socketType, "network");
                MethodInfo? setNetwork = socketType is null
                    ? null
                    : AccessTools.Method(socketType, "SetNetwork", [typeof(DummyNetwork)]);
                FieldInfo? receiveBufferField = AccessTools.Field(
                    typeof(DummyNetwork), "ClientReceiveBuffer");
                FieldInfo? receiveLockField = AccessTools.Field(
                    typeof(DummyNetwork), "ClientReceiveBufferLock");
                MethodInfo? enqueuePacket = receiveBufferField?.FieldType.GetMethod(
                    "Enqueue", [typeof(object)]);

                if (socketType is null || target is null || networkField is null ||
                    setNetwork is null || receiveBufferField is null || receiveLockField is null ||
                    enqueuePacket is null)
                {
                    Console.WriteLine(
                        "[OptiTime.Pharos] UDP harness patch not installed: " +
                        "the game or Pharos networking API shape has changed.");
                    return;
                }

                s_networkField = networkField;
                s_setNetwork = setNetwork;
                s_clientReceiveBufferField = receiveBufferField;
                s_clientReceiveLockField = receiveLockField;
                s_enqueuePacket = enqueuePacket;

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(UdpEnqueuePatch), nameof(UdpEnqueuePatch.Prefix)));
                Volatile.Write(ref s_udpEnqueuePatchInstalled, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[OptiTime.Pharos] harness patch not installed ({ex.Message}). " +
                    "Scenarios that receive entity updates over UDP will throw.");
            }
        }
    }

    /// <summary>
    /// Called by the scenario with the embedded server's UDP dummy network,
    /// so the client socket can be pointed at the same buffers the server
    /// writes to.
    /// </summary>
    public static void Attach(DummyNetwork shared)
    {
        Ensure();
        lock (Gate)
        {
            if (ReferenceEquals(s_sharedUdp, shared)) return;
            s_sharedUdp = shared;
            ResetCountersCore();
        }
    }

    /// <summary>
    /// Counts how many UDP payloads the harness patch delivered into the client's
    /// receive queue, including payloads unwrapped from TCP fallback packets.
    /// </summary>
    public static void ResetCounters() => ResetCountersCore();

    private static void ResetCountersCore()
    {
        Interlocked.Exchange(ref s_delivered, 0);
        Interlocked.Exchange(ref s_dropped, 0);
        lock (PositionGate)
        {
            Volatile.Write(ref s_trackedEntityId, -1);
            while (s_positionObservations.TryDequeue(out _)) { }
            while (s_receivedPositions.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// The replacement for the throwing method.
    ///
    /// Returns false so the original body - the <c>NotImplementedException</c>
    /// - never runs. Every path out of here returns rather than throws: an
    /// exception on this path lands inside the client's render loop, where it
    /// would take down the whole scenario instead of the one assertion.
    /// </summary>
    internal static class UdpEnqueuePatch
    {
        public static bool Prefix(object __instance, Packet_UdpPacket udpPacket)
        {
            try
            {
                Deliver(__instance, udpPacket);
            }
            catch
            {
                Interlocked.Increment(ref s_dropped);
            }

            return false;
        }

        private static void Deliver(object socket, Packet_UdpPacket packet)
        {
            if (packet is null) return;

            DummyNetwork? shared = s_sharedUdp;
            if (shared is null)
            {
                Interlocked.Increment(ref s_dropped);
                return;
            }

            // Pharos's linked socket never calls SetNetwork, so its own base
            // field is null and every method that touches it would fault. Point
            // it at the same dummy the server writes to, once.
            if (s_networkField?.GetValue(socket) is not DummyNetwork current)
            {
                s_setNetwork?.Invoke(socket, [shared]);
                current = shared;
            }

            object? buffer = s_clientReceiveBufferField?.GetValue(current);
            object? gate = s_clientReceiveLockField?.GetValue(current);

            if (buffer is null)
            {
                Interlocked.Increment(ref s_dropped);
                return;
            }

            if (gate is null)
            {
                // The buffer is shared with the reader. Without its lock, even
                // Enqueue would race ReadMessage, so fail closed and expose the
                // loss through EnqueueShimDropped for the engine probe to catch.
                Interlocked.Increment(ref s_dropped);
                return;
            }

            Monitor.Enter(gate);
            try
            {
                s_enqueuePacket!.Invoke(buffer, [packet]);
            }
            finally
            {
                Monitor.Exit(gate);
            }

            Interlocked.Increment(ref s_delivered);
            RecordPositionObservations(packet);
        }

        private static void RecordPositionObservations(Packet_UdpPacket packet)
        {
            if (Volatile.Read(ref s_trackedEntityId) < 0) return;

            lock (PositionGate)
            {
                long trackedEntityId = Volatile.Read(ref s_trackedEntityId);
                if (trackedEntityId < 0) return;

                if (packet.EntityPosition is { } position && position.EntityId == trackedEntityId)
                {
                    s_positionObservations.Enqueue(new(position.X, position.Y, position.Z, position.Tick));
                }

                if (packet.BulkPositions is not { } bulk || bulk.EntityPositions is not { } positions)
                {
                    return;
                }

                int count = Math.Min(bulk.GetEntityPositionsCount(), positions.Length);
                for (int i = 0; i < count; i++)
                {
                    position = positions[i];
                    if (position.EntityId == trackedEntityId)
                    {
                        s_positionObservations.Enqueue(
                            new(position.X, position.Y, position.Z, position.Tick));
                    }
                }
            }
        }
    }
}

/// <summary>
/// Anchor type kept so the patch installer's assembly reference lives in one
/// place rather than being guessed at four call sites.
/// </summary>
internal static class DummyUdpNetClientShim
{
    public static Assembly Assembly { get; } = typeof(Packet_Server).Assembly;
}

internal static class HarnessPatchesModule
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => HarnessPatches.Ensure();
}
