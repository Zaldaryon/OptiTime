using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace OptiTime;

/// <summary>
/// Improves remote entity position interpolation smoothness in multiplayer.
/// Targets EntityBehaviorInterpolatePosition (vsessentialsmod).
///
/// F5 — Flood protection: accelerated playback instead of recursive queue drain
/// F3 — Interval correction: 1/15f → 1/30f via transpiler on PopQueue
/// F1 — Extrapolation: constant velocity (200ms cap) + exponential decay correction
/// F6 — Hermite spline: cubic Hermite with 3-point history (opt-in)
///
/// Performance: zero reflection on hot paths. All fields are public — direct cast access.
/// Thread safety: ConcurrentDictionary for cross-thread state (network → render).
///
/// References:
///   Glenn Fiedler, "Snapshot Interpolation" — https://gafferongames.com/post/snapshot_interpolation
///   Valve, "Source Multiplayer Networking" — https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking
///   Gabriel Gambetta, "Entity Interpolation" — https://gabrielgambetta.com/entity-interpolation.html
///   Tim Ford, "Overwatch Gameplay Architecture and Netcode", GDC 2017
///   Stephen Toub, ".NET Performance" — reflection cost analysis
/// </summary>
public static class EntityInterpolationOptimization
{
    private static ICoreClientAPI capi;
    private static bool hermiteEnabled;

    // F1 — Extrapolation constants
    // Source Engine: 250ms cap (cl_extrapolate_amount). Unity Netcode: 333ms (20 ticks at 60Hz).
    // 200ms is conservative and within both ranges.
    private const float MaxExtrapolationTime = 0.2f;
    // Unreal CMC uses exponential decay with configurable half-life. 150ms matches Source Engine feel.
    private const float CorrectionHalfLife = 0.15f;
    // Hard snap threshold — if error exceeds this, entity was repositioned, not just delayed.
    private const double MaxCorrectionDistanceSq = 5.0 * 5.0;

    // F5 — Flood protection constants
    // Overwatch: continuous time dilation. Source Engine: cl_interp buffer management.
    // 15 entries = ~500ms at 30Hz — significant lag, start accelerating.
    private const int AccelerateThreshold = 15;
    // 50 entries = ~1.7s at 30Hz — severe disconnect, hard teleport.
    private const int HardTeleportThreshold = 50;
    private const float MaxPlaybackSpeed = 4.0f;

    // F3 — Corrected interval matching server 30Hz physics tick rate.
    // Valve formula: interp = cl_interp_ratio / cl_updaterate = 1/30 = 33.3ms.
    // Vanilla uses 1/15f = 66.7ms, causing ~200ms unnecessary buffer latency.
    private const float CorrectedInterval = 1f / 30f;

    // Per-entity state — ConcurrentDictionary for thread safety (network thread writes, render thread reads).
    // Source: C# ConcurrentDictionary is lock-free for reads, fine-grained locks for writes.
    private static readonly ConcurrentDictionary<long, ExtrapolationState> extrapolationStates = new();

    private sealed class ExtrapolationState
    {
        // F1 — error correction offset (decays exponentially)
        public double errorOffsetX, errorOffsetY, errorOffsetZ;
        // F1 — last known velocity for extrapolation
        public double lastVelX, lastVelY, lastVelZ;
        public float extrapolationTime;
        public bool isExtrapolating;
        // F6 — previous-previous snapshot for Hermite tangent at pL
        public PositionSnapshot pLL;
        public bool hasPLL;
    }

    public static void Initialize(ICoreClientAPI api, Harmony harmony, bool enableHermite)
    {
        capi = api;
        hermiteEnabled = enableHermite;

        // F5+F3: Prefix on OnReceivedServerPos — replaces vanilla entirely
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

        // F1+F6: Postfix on OnRenderFrame — adds extrapolation and Hermite after vanilla lerp
        var onRenderFrame = AccessTools.Method(
            "EntityBehaviorInterpolatePosition:OnRenderFrame",
            new Type[] { typeof(float), typeof(EnumRenderStage) }
        );

        if (onRenderFrame != null)
        {
            // Check for foreign patches — postfix is cooperative but log a warning
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

        // F3: Transpiler on PopQueue ONLY — the only method where the const 1/15f appears in IL.
        // OnReceivedServerPos is replaced by prefix (dead code). OnRenderFrame uses pN.interval (field, not const).
        var popQueue = AccessTools.Method(
            "EntityBehaviorInterpolatePosition:PopQueue",
            new Type[] { typeof(bool) }
        );

        if (popQueue != null)
        {
            // Transpiler-on-transpiler is the highest-risk conflict type — check before applying
            var popPatchInfo = Harmony.GetPatchInfo(popQueue);
            bool hasConflict = false;
            if (popPatchInfo != null)
            {
                foreach (var p in popPatchInfo.Transpilers ?? [])
                {
                    if (p.owner != harmony.Id) { hasConflict = true; break; }
                }
            }
            if (hasConflict)
            {
                api.Logger.Warning("[OptiTime] EntityInterpolation: skipping PopQueue transpiler — foreign transpiler detected");
            }
            else
            {
                harmony.Patch(popQueue,
                    transpiler: new HarmonyMethod(typeof(EntityInterpolationOptimization), nameof(Transpiler_FixInterval)));
            }
        }

        // F3: Fix initial targetSpeed from 0.6 to 1.0 (calibrated for 1/30f interval)
        var initialize = AccessTools.Method(
            "EntityBehaviorInterpolatePosition:Initialize",
            new Type[] { typeof(EntityProperties), typeof(Vintagestory.API.Datastructures.JsonObject) }
        );

        if (initialize != null)
        {
            harmony.Patch(initialize,
                postfix: new HarmonyMethod(typeof(EntityInterpolationOptimization), nameof(Postfix_Initialize)));
        }

        // Entity despawn cleanup
        api.Event.OnEntityDespawn += OnEntityDespawn;

        string features = "F5 (flood protection), F3 (interval 1/30f), F1 (extrapolation 200ms)";
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
    }

    private static void OnEntityDespawn(Entity entity, EntityDespawnData reason)
    {
        extrapolationStates.TryRemove(entity.EntityId, out _);
    }

    #region F3 — Transpiler: replace 1/15f with 1/30f in PopQueue only

    /// <summary>
    /// Replaces the inlined 1/15f constant (0.06666667f) with 1/30f (0.03333333f) in PopQueue.
    /// The only usage: Math.Max(pN.interval, interval) in the HandleRemotePhysics call.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler_FixInterval(IEnumerable<CodeInstruction> instructions)
    {
        const float vanillaInterval = 1f / 15f;
        int replaced = 0;

        foreach (var instr in instructions)
        {
            if (instr.opcode == OpCodes.Ldc_R4 && instr.operand is float fval &&
                Math.Abs(fval - vanillaInterval) < 0.0001f)
            {
                instr.operand = CorrectedInterval;
                replaced++;
            }
            yield return instr;
        }

        if (replaced > 0)
            capi?.Logger?.Debug($"[OptiTime] PopQueue transpiler: replaced {replaced} occurrence(s) of 1/15f → 1/30f");
    }

    #endregion

    #region F3 — Postfix: fix initial targetSpeed

    private static void Postfix_Initialize(EntityBehaviorInterpolatePosition __instance)
    {
        __instance.targetSpeed = 1.0f;
    }

    #endregion

    #region F5 — Prefix: replace flood drain with accelerated playback

    /// <summary>
    /// Replaces vanilla OnReceivedServerPos entirely. Identical behavior except:
    /// - Uses CorrectedInterval (1/30f) instead of vanilla 1/15f
    /// - Flood protection: accelerated playback at 15+, hard teleport at 50+ (vanilla: recursive drain at 20)
    /// - Resets extrapolation state on teleport and new data arrival
    ///
    /// Zero reflection — all fields are public, accessed via direct cast.
    /// </summary>
    private static bool Prefix_OnReceivedServerPos(
        EntityBehaviorInterpolatePosition __instance,
        bool isTeleport,
        ref EnumHandling handled)
    {
        var entity = __instance.entity;
        float tickInterval = entity.Attributes.GetInt("tickDiff", 1) * CorrectedInterval;

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

            // Reset extrapolation state on teleport
            extrapolationStates[entity.EntityId] = new ExtrapolationState();
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
            // Emergency: hard teleport (vanilla behavior, but at much higher threshold)
            __instance.PopQueue(true);
            extrapolationStates[entity.EntityId] = new ExtrapolationState();
        }
        else if (queueCount > AccelerateThreshold)
        {
            // Accelerate playback proportionally to queue depth (Overwatch time dilation pattern)
            float speed = 1.0f + (queueCount - AccelerateThreshold) * 0.15f;
            __instance.targetSpeed = Math.Min(speed, MaxPlaybackSpeed);
        }

        // F1 — When new data arrives during extrapolation, compute error offset for smooth correction
        if (!isTeleport && extrapolationStates.TryGetValue(entity.EntityId, out var state) && state.isExtrapolating)
        {
            // Error = where entity visually is (extrapolated) - where it should be (new pN)
            state.errorOffsetX = entity.Pos.X - __instance.pN.x;
            state.errorOffsetY = entity.Pos.Y - __instance.pN.y;
            state.errorOffsetZ = entity.Pos.Z - __instance.pN.z;

            // If error is too large, entity was repositioned — snap instead of blending
            double errDistSq = state.errorOffsetX * state.errorOffsetX +
                               state.errorOffsetY * state.errorOffsetY +
                               state.errorOffsetZ * state.errorOffsetZ;
            if (errDistSq > MaxCorrectionDistanceSq)
            {
                state.errorOffsetX = state.errorOffsetY = state.errorOffsetZ = 0;
            }

            state.isExtrapolating = false;
            state.extrapolationTime = 0;
        }

        handled = EnumHandling.PreventSubsequent;
        return false;
    }

    #endregion

    #region F1+F6 — Postfix: extrapolation + Hermite spline + error correction

    /// <summary>
    /// Runs after vanilla OnRenderFrame. Applies:
    /// - F1: Extrapolation when queue is empty (wait==1) — constant velocity, 200ms cap
    /// - F1: Exponential decay error correction after extrapolation ends
    /// - F6: Hermite spline interpolation with 3-point history (opt-in)
    ///
    /// Zero reflection — all fields accessed via direct cast.
    /// </summary>
    private static void Postfix_OnRenderFrame(
        EntityBehaviorInterpolatePosition __instance,
        float dt)
    {
        var entity = __instance.entity;
        var agent = __instance.agent;

        // Skip if mounted — position controlled by mount seat system
        if (agent?.MountedOn != null) return;

        int wait = __instance.wait;
        long entityId = entity.EntityId;

        var state = extrapolationStates.GetOrAdd(entityId, static _ => new ExtrapolationState());

        var pL = __instance.pL;
        var pN = __instance.pN;

        // F6 — Track pLL history for Hermite tangents.
        // PopQueue does: pL = pN; pN = dequeue(). We need the pL BEFORE the pop.
        // Strategy: store current pL each frame. When pL changes (pop happened),
        // the previously stored value is the correct pLL.
        if (hermiteEnabled && !pN.isTeleport)
        {
            if (!state.hasPLL)
            {
                // First frame: seed pLL with current pL (Hermite will degrade to linear)
                state.pLL = pL;
                state.hasPLL = true;
            }
            // pLL is updated at the END of this method (after Hermite uses it)
            // so that Hermite reads the pLL from BEFORE the current pop.
        }

        // F1 — Extrapolation when queue is empty
        if (wait != 0 && pN.interval > 0 && !pN.isTeleport)
        {
            if (!state.isExtrapolating)
            {
                // Compute velocity from last segment (units/sec)
                state.lastVelX = (pN.x - pL.x) / pN.interval;
                state.lastVelY = (pN.y - pL.y) / pN.interval;
                state.lastVelZ = (pN.z - pL.z) / pN.interval;
                state.isExtrapolating = true;
                state.extrapolationTime = 0;
            }

            state.extrapolationTime += dt;

            if (state.extrapolationTime <= MaxExtrapolationTime)
            {
                // Extrapolate at constant velocity from last known position
                entity.Pos.X = pN.x + state.lastVelX * state.extrapolationTime;
                entity.Pos.Y = pN.y + state.lastVelY * state.extrapolationTime;
                entity.Pos.Z = pN.z + state.lastVelZ * state.extrapolationTime;
            }
            // Beyond cap: hold at last extrapolated position (don't snap back to pN)

            return;
        }

        // F1 — Exponential decay error correction (Unreal CMC ApplyExponentialDecay pattern)
        // Applied AFTER Hermite (if active) so the offset is not overwritten.
        bool hasErrorOffset = state.errorOffsetX != 0 || state.errorOffsetY != 0 || state.errorOffsetZ != 0;
        if (hasErrorOffset)
        {
            double decay = Math.Pow(0.5, dt / CorrectionHalfLife);
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
        if (hermiteEnabled && wait == 0 && pN.interval > 0 && !pN.isTeleport && state.hasPLL)
        {
            // Skip for projectiles — they have competing physics simulation (BehaviorPassivePhysics)
            if (entity is IProjectile)
            {
                return;
            }

            float delta = __instance.dtAccum / pN.interval;
            delta = Math.Clamp(delta, 0f, 1f);

            // Derive velocities at endpoints using 3-point history
            // v_L = (pN - pLL) / (pL.interval + pN.interval) * pN.interval — Catmull-Rom tangent at pL
            // v_N = (pN - pL) — displacement over current segment (tangent at pN)
            var pLL = state.pLL;
            float totalInterval = pL.interval + pN.interval;

            double v0x, v0y, v0z; // tangent at pL (scaled by segment duration)
            double v1x, v1y, v1z; // tangent at pN (scaled by segment duration)

            if (totalInterval > 0.001f)
            {
                // Catmull-Rom tangent at pL: direction from pLL to pN, scaled to current segment
                v0x = (pN.x - pLL.x) * (pN.interval / totalInterval);
                v0y = (pN.y - pLL.y) * (pN.interval / totalInterval);
                v0z = (pN.z - pLL.z) * (pN.interval / totalInterval);
            }
            else
            {
                // Fallback: use segment displacement (degrades to linear)
                v0x = pN.x - pL.x;
                v0y = pN.y - pL.y;
                v0z = pN.z - pL.z;
            }

            // Tangent at pN: use segment displacement (we don't have pNN)
            v1x = pN.x - pL.x;
            v1y = pN.y - pL.y;
            v1z = pN.z - pL.z;

            // Cubic Hermite basis functions
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
        if (wait == 0 && state.isExtrapolating)
        {
            state.isExtrapolating = false;
            state.extrapolationTime = 0;
        }

        // F1 — Apply error correction offset AFTER Hermite (so Hermite doesn't overwrite it)
        if (hasErrorOffset && (state.errorOffsetX != 0 || state.errorOffsetY != 0 || state.errorOffsetZ != 0))
        {
            entity.Pos.X += state.errorOffsetX;
            entity.Pos.Y += state.errorOffsetY;
            entity.Pos.Z += state.errorOffsetZ;
        }

        // F6 — Store current pL as pLL for next frame ONLY when a pop happened.
        // Detect pop: pL changed from what we stored last frame.
        if (hermiteEnabled && state.hasPLL)
        {
            if (state.pLL.x != pL.x || state.pLL.y != pL.y || state.pLL.z != pL.z)
            {
                // pL changed (pop happened) — update pLL to current pL for next frame
                state.pLL = pL;
            }
        }
    }

    #endregion
}
