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
/// F5 — Flood protection: accelerated playback instead of recursive queue drain
/// F1 — Wall-clock-gated extrapolation: fires only when packet gap exceeds 110ms
///       (empirically grounded threshold from production MP server data).
///       Cap at 200ms matches Source Engine's cl_extrapolate_amount range.
/// F6 — Hermite spline: cubic Hermite with 3-point history (opt-in)
///
/// Note: VS server emits position packets every other 30Hz physics tick = 15Hz
/// (PhysicsManager.cs: `num6 = (ticksToDo - num5 + 1) % 2`). The Tick field on
/// Packet_EntityPosition is a per-entity packet sequence counter (GetIntAndIncrement),
/// so tickDiff=1 ≈ 66.7ms of real time. Vanilla's `1/15f` interval is intentional
/// and correct.
///
/// Performance: zero reflection on hot paths. All fields are public — direct cast access.
/// Thread safety: ConcurrentDictionary for cross-thread state (network → render).
///
/// References:
///   Glenn Fiedler, "Snapshot Interpolation" — https://gafferongames.com/post/snapshot_interpolation
///   Valve, "Source Multiplayer Networking" — https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking
///   Gabriel Gambetta, "Entity Interpolation" — https://gabrielgambetta.com/entity-interpolation.html
///   Tim Ford, "Overwatch Gameplay Architecture and Netcode", GDC 2017
/// </summary>
public static class EntityInterpolationOptimization
{
    private static ICoreClientAPI capi;
    private static bool hermiteEnabled;

    // F1 — Wall-clock-gated extrapolation constants (§5.3 of redesign doc)
    // Gate threshold: 110ms — empirically grounded from production MP server data.
    // 80-86% of inter-packet intervals are <=50ms (healthy), 3% are 50-110ms (borderline),
    // ~11-14% are >=110ms (real gaps requiring intervention).
    private const long ExtrapolationGateMs = 110;
    // Cap: 200ms — conservative end of Source Engine's 250ms range.
    private const long MaxExtrapolationMs = 200;
    // Correction half-life: 75ms — industry mid-range (Unreal CMC exponential decay).
    private const double CorrectionHalfLifeMs = 75.0;
    // Hard snap threshold — if error exceeds 5m, entity was repositioned, not just delayed.
    private const double MaxCorrectionDistanceSq = 5.0 * 5.0;

    // F5 — Flood protection constants
    private const int AccelerateThreshold = 15;
    private const int HardTeleportThreshold = 50;
    private const float MaxPlaybackSpeed = 4.0f;

    // Matches vanilla's `interval = 1/15f` const
    private const float SnapshotInterval = 1f / 15f;

    // Per-entity state — ConcurrentDictionary for thread safety (network thread writes, render thread reads).
    private static readonly ConcurrentDictionary<long, ExtrapolationState> extrapolationStates = new();

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
    // F1 trigger-rate counter
    private static long diagF1Triggered;
    private static long diagF1Frames;

    private sealed class ExtrapolationState
    {
        // F1 — error correction offset (decays exponentially)
        public double errorOffsetX, errorOffsetY, errorOffsetZ;
        // F1 — last known velocity for extrapolation (computed at packet receive)
        public double lastVelX, lastVelY, lastVelZ;
        public bool isExtrapolating;
        // F1 — wall-clock baseline (captured at packet receive, NOT at first extrapolation frame)
        public long lastPacketWallMs;
        public double lastKnownX, lastKnownY, lastKnownZ;
        public bool hasBaseline;
        // F6 — previous-previous snapshot for Hermite tangent at pL
        public PositionSnapshot pLL;
        public bool hasPLL;
    }

    public static void Initialize(ICoreClientAPI api, Harmony harmony, bool enableHermite)
    {
        capi = api;
        hermiteEnabled = enableHermite;

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

        // F1+F6: Postfix on OnRenderFrame
        var onRenderFrame = AccessTools.Method(
            "EntityBehaviorInterpolatePosition:OnRenderFrame",
            new Type[] { typeof(float), typeof(EnumRenderStage) }
        );

        if (onRenderFrame != null)
        {
            var renderPatchInfo = Harmony.GetPatchInfo(onRenderFrame);
            if (renderPatchInfo?.Postfixes != null)
            {
                foreach (var p in renderPatchInfo.Postfixes)
                {
                    if (p.owner != harmony.Id)
                    {
                        api.Logger.Warning($"[OptiTime] EntityInterpolation: foreign postfix on OnRenderFrame from {p.owner} — extrapolation may interact");
                        break;
                    }
                }
            }
            harmony.Patch(onRenderFrame,
                postfix: new HarmonyMethod(typeof(EntityInterpolationOptimization), nameof(Postfix_OnRenderFrame)));
        }
        else
        {
            api.Logger.Warning("[OptiTime] EntityInterpolation: Could not find OnRenderFrame");
        }

        api.Event.OnEntityDespawn += OnEntityDespawn;

        string features = "F5 (flood protection), F1 (wall-clock-gated extrapolation 110-200ms)";
        if (hermiteEnabled) features += ", F6 (Hermite spline)";
        api.Logger.Notification($"[OptiTime] Entity interpolation smoothing enabled: {features}");
    }

    public static void Cleanup()
    {
        if (capi != null)
        {
            capi.Event.OnEntityDespawn -= OnEntityDespawn;
        }
        capi = null;
        extrapolationStates.Clear();
        diagEnabled = false;
        diagLastPacketMs.Clear();
    }

    private static void OnEntityDespawn(Entity entity, EntityDespawnData reason)
    {
        extrapolationStates.TryRemove(entity.EntityId, out _);
        diagLastPacketMs.TryRemove(entity.EntityId, out _);
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
        Interlocked.Exchange(ref diagF1Triggered, 0);
        Interlocked.Exchange(ref diagF1Frames, 0);
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

        // F1 trigger-rate
        long f1t = Interlocked.Read(ref diagF1Triggered);
        long f1f = Interlocked.Read(ref diagF1Frames);
        if (f1f > 0)
        {
            double triggerPct = 100.0 * f1t / f1f;
            DiagLog.Line(api, "entityinterp", $"f1 triggered={triggerPct:F1}% ({f1t}/{f1f} frames)");
        }
    }

    #endregion

    #region F5 — Prefix: replace flood drain with accelerated playback

    /// <summary>
    /// Replaces vanilla OnReceivedServerPos entirely. Identical behavior except:
    /// - Flood protection: accelerated playback at 15+, hard teleport at 50+
    /// - Captures wall-clock baseline for F1 gating
    /// - Resets extrapolation state on teleport and new data arrival
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

            // Reset extrapolation state on teleport — seed baseline with zero velocity
            var state = extrapolationStates.GetOrAdd(entity.EntityId, static _ => new ExtrapolationState());
            state.lastVelX = state.lastVelY = state.lastVelZ = 0;
            state.errorOffsetX = state.errorOffsetY = state.errorOffsetZ = 0;
            state.isExtrapolating = false;
            state.lastKnownX = entity.Pos.X;
            state.lastKnownY = entity.Pos.Y;
            state.lastKnownZ = entity.Pos.Z;
            state.lastPacketWallMs = Environment.TickCount64;
            state.hasBaseline = true;
        }
        else
        {
            // Non-teleport: capture baseline for wall-clock-gated extrapolation (§5.5)
            long nowMs = Environment.TickCount64;
            var state = extrapolationStates.GetOrAdd(entity.EntityId, static _ => new ExtrapolationState());

            if (state.hasBaseline)
            {
                double dtSec = Math.Max(0.001, (nowMs - state.lastPacketWallMs) / 1000.0);
                state.lastVelX = (entity.Pos.X - state.lastKnownX) / dtSec;
                state.lastVelY = (entity.Pos.Y - state.lastKnownY) / dtSec;
                state.lastVelZ = (entity.Pos.Z - state.lastKnownZ) / dtSec;
            }

            // F1 — When new data arrives during extrapolation, compute error offset for smooth correction
            if (state.isExtrapolating)
            {
                state.errorOffsetX = entity.Pos.X - __instance.pN.x;
                state.errorOffsetY = entity.Pos.Y - __instance.pN.y;
                state.errorOffsetZ = entity.Pos.Z - __instance.pN.z;

                double errDistSq = state.errorOffsetX * state.errorOffsetX +
                                   state.errorOffsetY * state.errorOffsetY +
                                   state.errorOffsetZ * state.errorOffsetZ;
                if (errDistSq > MaxCorrectionDistanceSq)
                {
                    state.errorOffsetX = state.errorOffsetY = state.errorOffsetZ = 0;
                }
                state.isExtrapolating = false;
            }

            state.lastKnownX = entity.Pos.X;
            state.lastKnownY = entity.Pos.Y;
            state.lastKnownZ = entity.Pos.Z;
            state.lastPacketWallMs = nowMs;
            state.hasBaseline = true;
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
            var st = extrapolationStates.GetOrAdd(entity.EntityId, static _ => new ExtrapolationState());
            st.lastVelX = st.lastVelY = st.lastVelZ = 0;
            st.errorOffsetX = st.errorOffsetY = st.errorOffsetZ = 0;
            st.isExtrapolating = false;
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

    #region F1+F6 — Postfix: wall-clock-gated extrapolation + Hermite spline + error correction

    /// <summary>
    /// Runs after vanilla OnRenderFrame. Applies:
    /// - F1: Wall-clock-gated extrapolation when packet gap exceeds 110ms
    /// - F1: Exponential decay error correction after extrapolation ends
    /// - F6: Hermite spline interpolation with 3-point history (opt-in)
    /// </summary>
    internal static void Postfix_OnRenderFrame(
        EntityBehaviorInterpolatePosition __instance,
        float dt)
    {
        var entity = __instance.entity;
        var agent = __instance.agent;

        // Skip if mounted — position controlled by mount seat system
        if (agent?.MountedOn != null) return;

        // Projectiles use BehaviorPassivePhysics — skip all interpolation enhancements
        if (entity is IProjectile) return;

        long entityId = entity.EntityId;
        var state = extrapolationStates.GetOrAdd(entityId, static _ => new ExtrapolationState());

        var pL = __instance.pL;
        var pN = __instance.pN;

        if (!entity.Alive)
        {
            ResetF1State(state);
            return;
        }

        // F6 — Track pLL history for Hermite tangents
        if (hermiteEnabled && !pN.isTeleport)
        {
            if (!state.hasPLL)
            {
                state.pLL = pL;
                state.hasPLL = true;
            }
        }

        // F1 — Wall-clock-gated extrapolation (§5.6)
        if (state.hasBaseline && !pN.isTeleport)
        {
            long nowMs = Environment.TickCount64;
            long gapMs = nowMs - state.lastPacketWallMs;

            if (diagEnabled) Interlocked.Increment(ref diagF1Frames);

            if (gapMs > ExtrapolationGateMs && entity.Alive)
            {
                // Velocity gate: don't extrapolate stationary entities (DIS dead reckoning pattern).
                // Server only sends position packets when entity moves, so idle mobs have
                // stale lastPacketWallMs — without this gate, F1 fires uselessly every frame.
                double velMagSq = state.lastVelX * state.lastVelX + state.lastVelY * state.lastVelY + state.lastVelZ * state.lastVelZ;
                if (velMagSq < 0.0001) return; // < 0.01 m/s — entity is stationary

                long extrapMs = Math.Min(gapMs - ExtrapolationGateMs, MaxExtrapolationMs - ExtrapolationGateMs);
                double extrapSec = extrapMs / 1000.0;
                entity.Pos.X = state.lastKnownX + state.lastVelX * extrapSec;
                entity.Pos.Y = state.lastKnownY + state.lastVelY * extrapSec;
                entity.Pos.Z = state.lastKnownZ + state.lastVelZ * extrapSec;
                state.isExtrapolating = true;

                if (diagEnabled) Interlocked.Increment(ref diagF1Triggered);
                return;
            }
        }

        // F1 — Exponential decay error correction (Unreal CMC ApplyExponentialDecay pattern)
        bool hasErrorOffset = state.errorOffsetX != 0 || state.errorOffsetY != 0 || state.errorOffsetZ != 0;
        if (hasErrorOffset)
        {
            double decay = Math.Pow(0.5, dt * 1000.0 / CorrectionHalfLifeMs);
            state.errorOffsetX *= decay;
            state.errorOffsetY *= decay;
            state.errorOffsetZ *= decay;

            double errSq = state.errorOffsetX * state.errorOffsetX +
                           state.errorOffsetY * state.errorOffsetY +
                           state.errorOffsetZ * state.errorOffsetZ;

            if (errSq < 0.0001)
            {
                state.errorOffsetX = state.errorOffsetY = state.errorOffsetZ = 0;
            }
        }

        // F6 — Hermite spline interpolation with 3-point history
        if (hermiteEnabled && __instance.wait == 0 && pN.interval > 0 && !pN.isTeleport && state.hasPLL)
        {
            float delta = __instance.dtAccum / pN.interval;
            delta = Math.Clamp(delta, 0f, 1f);

            var pLL = state.pLL;
            float totalInterval = pL.interval + pN.interval;

            double v0x, v0y, v0z;
            double v1x, v1y, v1z;

            if (totalInterval > 0.001f)
            {
                v0x = (pN.x - pLL.x) * (pN.interval / totalInterval);
                v0y = (pN.y - pLL.y) * (pN.interval / totalInterval);
                v0z = (pN.z - pLL.z) * (pN.interval / totalInterval);
            }
            else
            {
                v0x = pN.x - pL.x;
                v0y = pN.y - pL.y;
                v0z = pN.z - pL.z;
            }

            v1x = pN.x - pL.x;
            v1y = pN.y - pL.y;
            v1z = pN.z - pL.z;

            double t = delta;
            double t2 = t * t;
            double t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;

            entity.Pos.X = h00 * pL.x + h10 * v0x + h01 * pN.x + h11 * v1x;
            entity.Pos.Y = h00 * pL.y + h10 * v0y + h01 * pN.y + h11 * v1y;
            entity.Pos.Z = h00 * pL.z + h10 * v0z + h01 * pN.z + h11 * v1z;
        }

        // Reset extrapolation flag when we have data
        if (__instance.wait == 0 && state.isExtrapolating)
        {
            state.isExtrapolating = false;
        }

        // F1 — Apply error correction offset AFTER Hermite
        if (hasErrorOffset && (state.errorOffsetX != 0 || state.errorOffsetY != 0 || state.errorOffsetZ != 0))
        {
            entity.Pos.X += state.errorOffsetX;
            entity.Pos.Y += state.errorOffsetY;
            entity.Pos.Z += state.errorOffsetZ;
        }

        // F6 — Store current pL as pLL for next frame when a pop happened
        if (hermiteEnabled && state.hasPLL)
        {
            if (state.pLL.x != pL.x || state.pLL.y != pL.y || state.pLL.z != pL.z)
            {
                state.pLL = pL;
            }
        }
    }

    private static void ResetF1State(ExtrapolationState state)
    {
        state.errorOffsetX = state.errorOffsetY = state.errorOffsetZ = 0;
        state.lastVelX = state.lastVelY = state.lastVelZ = 0;
        state.isExtrapolating = false;
    }

    #endregion
}
