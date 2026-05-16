using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using OptiTime.Diagnostics;

namespace OptiTime;

/// <summary>
/// Improves remote entity position interpolation smoothness in multiplayer.
/// Targets EntityBehaviorInterpolatePosition (vsessentialsmod).
///
/// F5 — Flood protection: accelerated playback instead of recursive queue drain.
///      Vanilla uses recursive PopQueue(true) at queueCount > 20 which teleports.
///      This mod instead accelerates playback at 15+ and hard-teleports only at 50+.
///
/// Note: VS server emits position packets every other 30Hz physics tick = 15Hz
/// (PhysicsManager.cs: `num6 = (ticksToDo - num5 + 1) % 2`). The Tick field on
/// Packet_EntityPosition is a per-entity packet sequence counter (GetIntAndIncrement),
/// so tickDiff=1 ≈ 66.7ms of real time. Vanilla's `1/15f` interval is intentional
/// and correct.
///
/// Performance: zero reflection on hot paths. All fields are public — direct cast access.
/// </summary>
public static class EntityInterpolationOptimization
{
    private static ICoreClientAPI capi;

    // F5 — Flood protection constants
    private const int AccelerateThreshold = 15;
    private const int HardTeleportThreshold = 50;
    private const float MaxPlaybackSpeed = 4.0f;

    // Matches vanilla's `interval = 1/15f` const
    private const float SnapshotInterval = 1f / 15f;

    // Diagnostic instrumentation — opt-in measurement of observed snapshot cadence.
    private static volatile bool diagEnabled;
    private static long diagStartMs;
    private static long diagSampleCount;
    private static long diagTotalDtMs;
    private static long diagTotalTickDiff;
    // Inter-packet wall-clock delta bins (ms): <25, 25-50, 50-80, 80-110, 110-200, >=200
    private static readonly long[] diagDtBins = new long[6];
    // tickDiff bins: 1, 2, 3, 4, 5, >=6
    private static readonly long[] diagTickDiffBins = new long[6];
    private static readonly ConcurrentDictionary<long, long> diagLastPacketMs = new();

    public static void Initialize(ICoreClientAPI api, Harmony harmony, bool enableHermite)
    {
        capi = api;

        // F5: Prefix on OnReceivedServerPos — replaces vanilla entirely
        var onReceivedServerPos = AccessTools.Method(
            "EntityBehaviorInterpolatePosition:OnReceivedServerPos",
            new Type[] { typeof(bool), typeof(EnumHandling).MakeByRefType() }
        );

        if (onReceivedServerPos != null)
        {
            harmony.Patch(onReceivedServerPos,
                prefix: new HarmonyMethod(typeof(EntityInterpolationOptimization), nameof(Prefix_OnReceivedServerPos)));
        }
        else
        {
            api.Logger.Warning("[OptiTime] EntityInterpolation: Could not find OnReceivedServerPos");
        }

        api.Logger.Notification("[OptiTime] Entity interpolation smoothing enabled: F5 (flood protection)");
    }

    public static void Cleanup()
    {
        capi = null;
        diagEnabled = false;
        diagLastPacketMs.Clear();
    }

    #region Diagnostic instrumentation

    public static void SetDiagnosticEnabled(bool enabled)
    {
        if (enabled && !diagEnabled) ResetDiagnostic();
        diagEnabled = enabled;
    }

    public static void ResetDiagnostic()
    {
        for (int i = 0; i < diagDtBins.Length; i++)
        {
            Interlocked.Exchange(ref diagDtBins[i], 0);
            Interlocked.Exchange(ref diagTickDiffBins[i], 0);
        }
        Interlocked.Exchange(ref diagSampleCount, 0);
        Interlocked.Exchange(ref diagTotalDtMs, 0);
        Interlocked.Exchange(ref diagTotalTickDiff, 0);
        diagLastPacketMs.Clear();
        Interlocked.Exchange(ref diagStartMs, Environment.TickCount64);
    }

    private static void RecordDiagSample(long entityId, int tickDiff)
    {
        long now = Environment.TickCount64;
        int tdBin = Math.Clamp(tickDiff - 1, 0, diagTickDiffBins.Length - 1);
        Interlocked.Increment(ref diagTickDiffBins[tdBin]);

        if (diagLastPacketMs.TryGetValue(entityId, out long last))
        {
            long dt = now - last;
            int dtBin = dt switch
            {
                < 25 => 0,
                < 50 => 1,
                < 80 => 2,
                < 110 => 3,
                < 200 => 4,
                _ => 5
            };
            Interlocked.Increment(ref diagDtBins[dtBin]);
            Interlocked.Increment(ref diagSampleCount);
            Interlocked.Add(ref diagTotalDtMs, dt);
            Interlocked.Add(ref diagTotalTickDiff, tickDiff);
        }
        diagLastPacketMs[entityId] = now;
    }

    public static void DumpDiagnostic(ICoreClientAPI api)
    {
        long total = Interlocked.Read(ref diagSampleCount);
        if (total == 0)
        {
            DiagLog.Line(api, "entityinterp", "no samples yet — enable with `.optitime interpdiag on` and wait.");
            return;
        }
        long elapsedMs = Environment.TickCount64 - Interlocked.Read(ref diagStartMs);
        long sumDt = Interlocked.Read(ref diagTotalDtMs);
        long sumTd = Interlocked.Read(ref diagTotalTickDiff);
        double msPerTick = sumTd > 0 ? (double)sumDt / sumTd : 0;

        DiagLog.Line(api, "entityinterp", $"samples={total} elapsedS={elapsedMs / 1000.0:F1} meanMsPerTick={msPerTick:F1}");

        string[] dtLabels = { "<25ms", "25-50ms", "50-80ms", "80-110ms", "110-200ms", ">=200ms" };
        for (int i = 0; i < diagDtBins.Length; i++)
        {
            long c = Interlocked.Read(ref diagDtBins[i]);
            if (c == 0) continue;
            double pct = 100.0 * c / total;
            DiagLog.Line(api, "entityinterp", $"dtBin {dtLabels[i]}={pct:F1}% ({c})");
        }

        long tdTotal = 0;
        for (int i = 0; i < diagTickDiffBins.Length; i++) tdTotal += Interlocked.Read(ref diagTickDiffBins[i]);
        if (tdTotal > 0)
        {
            for (int i = 0; i < diagTickDiffBins.Length; i++)
            {
                long c = Interlocked.Read(ref diagTickDiffBins[i]);
                if (c == 0) continue;
                double pct = 100.0 * c / tdTotal;
                string label = i == diagTickDiffBins.Length - 1 ? $">={i + 1}" : (i + 1).ToString();
                DiagLog.Line(api, "entityinterp", $"tickDiff={label}: {pct:F1}% ({c})");
            }
        }
    }

    #endregion

    #region F5 — Prefix: replace flood drain with accelerated playback

    /// <summary>
    /// Replaces vanilla OnReceivedServerPos entirely. Identical behavior except:
    /// - Flood protection: accelerated playback at 15+, hard teleport at 50+
    ///   (vanilla uses recursive PopQueue(true) at 20+ which teleports immediately)
    /// </summary>
    internal static bool Prefix_OnReceivedServerPos(
        EntityBehaviorInterpolatePosition __instance,
        bool isTeleport,
        ref EnumHandling handled)
    {
        var entity = __instance.entity;

        // Projectiles use BehaviorPassivePhysics — let vanilla handle them entirely
        if (entity is IProjectile) return true;

        int tickDiff = entity.Attributes.GetInt("tickDiff", 1);
        if (diagEnabled) RecordDiagSample(entity.EntityId, tickDiff);
        float tickInterval = tickDiff * SnapshotInterval;

        __instance.PushQueue(new PositionSnapshot(entity.Pos, tickInterval, isTeleport));

        if (isTeleport)
        {
            __instance.dtAccum = 0;
            __instance.positionQueue.Clear();
            __instance.queueCount = 0;

            __instance.PushQueue(new PositionSnapshot(entity.Pos, tickInterval, false));
            __instance.PushQueue(new PositionSnapshot(entity.Pos, tickInterval, false));

            __instance.PopQueue(false);
            __instance.PopQueue(false);

            __instance.currentYaw = entity.Pos.Yaw;
            __instance.currentPitch = entity.Pos.Pitch;
            __instance.currentRoll = entity.Pos.Roll;

            if (__instance.agent != null)
            {
                __instance.currentHeadYaw = entity.Pos.HeadYaw;
                __instance.currentHeadPitch = entity.Pos.HeadPitch;
                __instance.currentBodyYaw = __instance.agent.BodyYawServer;
            }
        }

        __instance.targetYaw = entity.Pos.Yaw;
        __instance.targetPitch = entity.Pos.Pitch;
        __instance.targetRoll = entity.Pos.Roll;

        if (__instance.agent != null)
        {
            __instance.targetHeadYaw = entity.Pos.HeadYaw;
            __instance.targetHeadPitch = entity.Pos.HeadPitch;
            __instance.targetBodyYaw = __instance.agent.BodyYawServer;
        }

        // F5 — Accelerated playback instead of vanilla recursive drain
        int queueCount = __instance.queueCount;

        if (queueCount > HardTeleportThreshold)
        {
            __instance.PopQueue(true);
        }
        else if (queueCount > AccelerateThreshold)
        {
            float speed = 1.0f + (queueCount - AccelerateThreshold) * 0.15f;
            __instance.targetSpeed = Math.Min(speed, MaxPlaybackSpeed);
        }

        handled = EnumHandling.PreventSubsequent;
        return false;
    }

    #endregion
}
