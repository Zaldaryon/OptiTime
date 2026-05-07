using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace OptiTime;

/// <summary>
/// Distance-based cull for EntityBehaviorRepulseAgents.
///
/// On client, non-player entities only check repulsion against the player (O(1) per entity).
/// The player entity runs a full WalkEntities spatial query (O(N)).
///
/// This cull skips OnGameTick entirely for non-player entities beyond 64 blocks.
/// Each skipped entity saves: state checks + WalkEntity(player) + pushVector math.
/// With 200 creatures, ~60-70% are typically beyond 64 blocks = measurable savings.
///
/// The player entity's WalkEntities call is never skipped — it handles all nearby repulsion.
///
/// Industry standard: Unreal Mass Entity LOD, CA-LOD (Paris et al. 2009).
/// References:
///   https://x157.github.io/UE5/Mass/LOD
///   https://link.springer.com/chapter/10.1007/978-3-642-10347-6_2
/// </summary>
public static class RepulseAgentsOptimization
{
    private static ICoreClientAPI capi;
    private const double CullDistanceSq = 64.0 * 64.0;

    public static void Initialize(ICoreClientAPI api, Harmony harmony)
    {
        capi = api;

        var targetType = AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorRepulseAgents");
        if (targetType == null)
        {
            api.Logger.Warning("[OptiTime] RepulseAgents: Could not find EntityBehaviorRepulseAgents type");
            return;
        }

        var targetMethod = AccessTools.Method(targetType, "OnGameTick", new Type[] { typeof(float) });
        if (targetMethod == null)
        {
            api.Logger.Warning("[OptiTime] RepulseAgents: Could not find OnGameTick method");
            return;
        }

        harmony.Patch(targetMethod, prefix: new HarmonyMethod(typeof(RepulseAgentsOptimization), nameof(Prefix_OnGameTick)));
        api.Logger.Notification("[OptiTime] RepulseAgents distance cull enabled (64 blocks)");
    }

    public static void Cleanup()
    {
        capi = null;
    }

    /// <summary>
    /// Skip OnGameTick for non-player entities beyond 64 blocks from the player.
    /// The __instance is EntityBehaviorRepulseAgents but we receive it as EntityBehavior
    /// to avoid a compile-time dependency on VSEssentials.dll.
    /// </summary>
    private static bool Prefix_OnGameTick(EntityBehavior __instance)
    {
        var api = capi;
        if (api == null) return true;

        var entity = __instance.entity;

        // Only cull on client side
        var clientWorld = entity.World as IClientWorldAccessor;
        if (clientWorld == null) return true;

        // Null safety: Player/Entity may not exist during early loading
        var player = clientWorld.Player;
        if (player?.Entity == null) return true;

        // Never cull the player entity — it runs the expensive WalkEntities query
        if (entity == player.Entity) return true;

        return !IsBeyondCullDistance(entity.Pos.X, entity.Pos.Z, player.Entity.Pos.X, player.Entity.Pos.Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBeyondCullDistance(double ex, double ez, double px, double pz)
    {
        double dx = ex - px;
        double dz = ez - pz;
        return dx * dx + dz * dz > CullDistanceSq;
    }
}
