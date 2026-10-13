using System.Reflection;
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
/// deliberately narrow and fail-open: if the shape of the game or the harness
/// changes, the patch does nothing rather than throwing in the render loop.
///
/// <para><b>UDP over TCP.</b> Vintage Story 1.22 delivers datagrams that carry
/// entity positions and block updates inlined in TCP packets
/// (<c>Packet_ServerIdEnum.UdpPacket</c>, id 79). On the client,
/// <c>SystemNetworkProcess.EnqueueUdpPacket</c> hands the payload to
/// <c>DummyUdpNetClient.EnqueuePacket</c> - which in the vanilla source is
/// <c>throw new NotImplementedException()</c>. Pharos overrides
/// <c>Send</c> and <c>ReadMessage</c> on its linked socket but not
/// <c>EnqueuePacket</c>, so the first entity update in a world with animals
/// takes the whole scenario down with a <c>NotImplementedException</c> thrown
/// from inside the client render loop.</para>
///
/// <para>The fix puts the payload where the socket's own read path will find
/// it - the shared <see cref="DummyNetwork"/>'s client receive buffer - so the
/// packet then travels the same loopback route, including any configured
/// network degradation, exactly as a datagram read off a real socket would. It
/// is not dropped: the client keeps receiving entity positions, which the
/// interpolation and repulse tests depend on.</para>
/// </summary>
internal static class HarnessPatches
{
    private static readonly object Gate = new();
    private static bool _installed;

    private static FieldInfo? s_networkField;
    private static FieldInfo? s_clientReceiveBufferField;
    private static FieldInfo? s_clientReceiveLockField;
    private static MethodInfo? s_setNetwork;

    /// <summary>
    /// The loopback's shared UDP dummy network, handed over by the scenario
    /// before the client joins.
    /// </summary>
    private static DummyNetwork? s_sharedUdp;

    /// <summary>How many datagrams the patch delivered, and how many it dropped.</summary>
    public static long Delivered { get; private set; }

    public static long Dropped { get; private set; }

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

                if (socketType is null || target is null)
                {
                    // The type moved. Nothing to do: the patch is an
                    // improvement to the harness, not a requirement of the
                    // suite, so a rename must not break the run.
                    return;
                }

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(UdpEnqueuePatch), nameof(UdpEnqueuePatch.Prefix)));

                s_networkField = AccessTools.Field(socketType, "network");
                s_setNetwork = AccessTools.Method(socketType, "SetNetwork", [typeof(DummyNetwork)]);

                s_clientReceiveBufferField = AccessTools.Field(typeof(DummyNetwork), "ClientReceiveBuffer");
                s_clientReceiveLockField = AccessTools.Field(typeof(DummyNetwork), "ClientReceiveBufferLock");
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
        s_sharedUdp = shared;
    }

    /// <summary>
    /// Counts how many datagrams the harness patch delivered, so a run can say
    /// whether the multiplayer tests were actually receiving entity updates or
    /// merely not crashing.
    /// </summary>
    public static void ResetCounters() => ResetCountersCore();

    private static void ResetCountersCore()
    {
        Delivered = 0;
        Dropped = 0;
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
                Dropped++;
            }

            return false;
        }

        private static void Deliver(object socket, Packet_UdpPacket packet)
        {
            if (packet is null) return;

            DummyNetwork? shared = s_sharedUdp;
            if (shared is null)
            {
                Dropped++;
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
                Dropped++;
                return;
            }

            if (gate is null)
            {
                ((System.Collections.IEnumerable)buffer).Cast<object>().Append(packet);
                Dropped++;
                return;
            }

            Monitor.Enter(gate);
            try
            {
                MethodInfo enqueue = buffer.GetType().GetMethod("Enqueue")!;
                enqueue.Invoke(buffer, [packet]);
            }
            finally
            {
                Monitor.Exit(gate);
            }

            Delivered++;
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