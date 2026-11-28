using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public static class EntityShadowCullingOptimization
    {
        // Vanilla SystemRenderEntities.OnRenderFrameShadows runs on both ShadowFar and ShadowNear,
        // testing every entity with a flat 3.0 frustum sphere and WorldMap.IsChunkRendered, then
        // unconditionally switching shaders and re-scanning all EntityRenderers a second time for
        // batched entity shadows. This optimization:
        // 1. Caches player position and dimension once per pass and short-circuits ShadowNear
        //    (cascade range <= 58 blocks, well under the 80-block cull floor).
        // 2. Rejects distant entities by horizontal squared distance BEFORE frustum and chunk-map
        //    lookups, scaling the cutoff by (max(3.0, FrustumSphereRadius) / 3.0)^2 with a 1.21x
        //    squared hysteresis band preserved across ShadowNear resets.
        // 3. Collects shadow-rendered renderers into a dense scratch list so the batched pass only
        //    iterates active shadow casters and skips the Shadowmapentityanimated shader switch
        //    entirely when zero entities cast shadows in the cascade.
        private const double BaseCullDistance = 80.0;
        private const double BaseCullDistanceSq = BaseCullDistance * BaseCullDistance;
        private const double DefaultFrustumRadius = 3.0;
        private const double ScaledBaseCoeffSq = (BaseCullDistance / DefaultFrustumRadius) * (BaseCullDistance / DefaultFrustumRadius);
        private const double HysteresisMultiplierSq = 1.21;
        private const double HysteresisTrackMinDistSq = 50.0 * 50.0;
        private const int MinActiveShadowQuality = 2;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> entityRenderersRef;
        private static readonly AccessTools.FieldRef<ClientMain, float[]> shadowMvpMatrixRef;
        private static readonly bool accessorsReady;

        private static Action<string> logger;
        private static volatile bool patchActive;
        private static volatile bool enabled = true;
        private static volatile int currentShadowQuality = MinActiveShadowQuality;

        // Reusable render-thread collections (cleared after every pass and on world leave/cleanup).
        private static readonly List<EntityRenderer> shadowRenderedList = new List<EntityRenderer>(64);
        private static HashSet<long> prevFarRendered = new HashSet<long>();
        private static HashSet<long> currFarRendered = new HashSet<long>();

        static EntityShadowCullingOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
                entityRenderersRef = AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");
                shadowMvpMatrixRef = AccessTools.FieldRefAccess<ClientMain, float[]>("shadowMvpMatrix");
                accessorsReady = true;
            }
            catch
            {
                accessorsReady = false;
            }
        }

        public static void SetLogger(Action<string> log) => logger = log;

        public static bool TranspilerActive => patchActive;

        internal static void SetPatchActive(bool active) => patchActive = active;

        public static void SetEnabled(bool isEnabled)
        {
            enabled = isEnabled;
            if (!isEnabled)
            {
                prevFarRendered.Clear();
                currFarRendered.Clear();
            }
        }

        public static void UpdateShadowMapQuality(int shadowQuality)
        {
            currentShadowQuality = shadowQuality;
            if (shadowQuality < MinActiveShadowQuality)
            {
                prevFarRendered.Clear();
                currFarRendered.Clear();
            }
        }

        public static void OnLeaveWorld()
        {
            shadowRenderedList.Clear();
            prevFarRendered.Clear();
            currFarRendered.Clear();
        }

        /// <summary>
        /// Evaluates whether an entity exceeds its radius-scaled shadow cull distance with 1.21x
        /// squared hysteresis on <see cref="Entity.IsShadowRendered"/>. Used both directly and
        /// by live/unit verification probes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBeyondCullDistance(Entity entity)
        {
            if (entity == null || entity.AllowOutsideLoadedRange ||
                !enabled || currentShadowQuality < MinActiveShadowQuality)
            {
                return false;
            }

            var clientWorld = entity.World as IClientWorldAccessor;
            EntityPlayer player = clientWorld?.Player?.Entity;
            if (player == null || ReferenceEquals(entity, player))
            {
                return false;
            }

            var playerPos = player.Pos;
            bool culled = IsBeyondCullDistanceCore(entity, entity.Pos, playerPos.X, playerPos.Z, hasPrevFar: false, out _);
            ModuleShadowEntity.OnEntity(culled);
            return culled;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBeyondCullDistanceCore(
            Entity entity,
            EntityPos entityPos,
            double playerX,
            double playerZ,
            bool hasPrevFar,
            out double distSq)
        {
            double dx = entityPos.X - playerX;
            double dz = entityPos.Z - playerZ;
            distSq = dx * dx + dz * dz;

            // Since scale >= 1.0, any entity within BaseCullDistance is guaranteed not culled.
            if (distSq <= BaseCullDistanceSq)
            {
                return false;
            }

            double radius = entity.FrustumSphereRadius;
            double thresholdSq = radius > DefaultFrustumRadius
                ? ScaledBaseCoeffSq * (radius * radius)
                : BaseCullDistanceSq;

            bool wasRendered = entity.IsShadowRendered || (hasPrevFar && prevFarRendered.Contains(entity.EntityId));
            if (wasRendered)
            {
                thresholdSq *= HysteresisMultiplierSq;
            }

            return distSq > thresholdSq;
        }

        public static bool OnRenderFrameShadows_Prefix(SystemRenderEntities __instance, float dt)
        {
            if (!enabled || currentShadowQuality < MinActiveShadowQuality || !accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            EntityPlayer player = game?.EntityPlayer;
            Dictionary<long, EntityRenderer> entityRenderers = game != null ? entityRenderersRef(game) : null;
            if (player == null || entityRenderers == null || game.frustumCuller == null || game.WorldMap == null)
            {
                return true;
            }

            var chunkShadow = ShaderProgramBase.CurrentShaderProgram as ShaderProgramChunkshadowmap;
            if (chunkShadow == null)
            {
                return true;
            }

            var playerPos = player.Pos;
            int dimension = playerPos.Dimension;
            double playerX = playerPos.X;
            double playerZ = playerPos.Z;

            FrustumCulling frustum = game.frustumCuller;
            ClientWorldMap worldMap = game.WorldMap;

            // Near cascade range is 30 + 3 * (quality - 1) + 16 (<= 58 blocks), well below the
            // 80-block cull floor; skip distance culling and preserve the ShadowFar hysteresis set.
            bool isFarPass = frustum.shadowRangeZ > BaseCullDistance;
            bool hasPrevFar = false;
            if (isFarPass)
            {
                (prevFarRendered, currFarRendered) = (currFarRendered, prevFarRendered);
                currFarRendered.Clear();
                hasPrevFar = prevFarRendered.Count > 0;
            }

            if (game.api?.Render?.UseSSBOs == true)
            {
                chunkShadow.Stop();
                chunkShadow = (ShaderProgramChunkshadowmap)game.api.Shader.GetProgram(42);
                chunkShadow.Use();
            }

            shadowRenderedList.Clear();
            try
            {
                foreach (KeyValuePair<long, EntityRenderer> pair in entityRenderers)
                {
                    EntityRenderer renderer = pair.Value;
                    Entity entity = renderer.entity;
                    EntityPos entityPos = entity.Pos;

                    if (entityPos.Dimension != dimension)
                    {
                        entity.IsShadowRendered = false;
                        continue;
                    }

                    bool isPlayer = ReferenceEquals(entity, player);
                    double distSq = 0.0;
                    if (isFarPass && !isPlayer && !entity.AllowOutsideLoadedRange)
                    {
                        if (IsBeyondCullDistanceCore(entity, entityPos, playerX, playerZ, hasPrevFar, out distSq))
                        {
                            ModuleShadowEntity.OnEntity(true);
                            entity.IsShadowRendered = false;
                            continue;
                        }
                    }

                    double frustumRadius = entity.FrustumSphereRadius;
                    if (!(frustumRadius > DefaultFrustumRadius))
                    {
                        frustumRadius = DefaultFrustumRadius;
                    }

                    if (frustum.SphereInFrustum((float)entityPos.X, (float)entityPos.InternalY, (float)entityPos.Z, frustumRadius)
                        && (isPlayer || (worldMap.IsValidPos((int)entityPos.X, (int)entityPos.InternalY, (int)entityPos.Z) && worldMap.IsChunkRendered(entityPos))))
                    {
                        entity.IsShadowRendered = true;
                        if (isFarPass && !isPlayer)
                        {
                            ModuleShadowEntity.OnEntity(false);
                            if (distSq > HysteresisTrackMinDistSq)
                            {
                                currFarRendered.Add(entity.EntityId);
                            }
                        }

                        renderer.DoRender3DOpaque(dt, isShadowPass: true);
                        shadowRenderedList.Add(renderer);
                    }
                    else
                    {
                        entity.IsShadowRendered = false;
                    }
                }

                // Skip the Shadowmapentityanimated shader switch and second pass entirely when
                // zero entities rendered shadows in this cascade.
                int count = shadowRenderedList.Count;
                if (count > 0)
                {
                    chunkShadow.Stop();
                    ShaderProgramShadowmapentityanimated animShader = ShaderPrograms.Shadowmapentityanimated;
                    animShader.Use();
                    try
                    {
                        animShader.ProjectionMatrix = game.CurrentProjectionMatrix;
                        animShader.EntityTex2D = game.EntityAtlasManager.AtlasTextures[0].TextureId;
                        for (int i = 0; i < count; i++)
                        {
                            shadowRenderedList[i].DoRender3DOpaqueBatched(dt, isShadowPass: true);
                        }
                    }
                    finally
                    {
                        animShader.Stop();
                        chunkShadow.Use();
                        chunkShadow.MvpMatrix = shadowMvpMatrixRef(game);
                    }
                }
            }
            finally
            {
                shadowRenderedList.Clear();
            }

            return false;
        }

        internal static bool EvaluateFarShadowStepForTest(
            Entity entity,
            double playerX,
            double playerZ,
            bool isFarPass,
            bool beginNewPass)
        {
            if (beginNewPass && isFarPass)
            {
                (prevFarRendered, currFarRendered) = (currFarRendered, prevFarRendered);
                currFarRendered.Clear();
            }

            if (!enabled || currentShadowQuality < MinActiveShadowQuality || !isFarPass ||
                entity == null || entity.AllowOutsideLoadedRange)
            {
                return false;
            }

            bool culled = IsBeyondCullDistanceCore(entity, entity.Pos, playerX, playerZ, prevFarRendered.Count > 0, out double distSq);
            if (!culled && distSq > HysteresisTrackMinDistSq)
            {
                currFarRendered.Add(entity.EntityId);
            }

            return culled;
        }

        public static void Cleanup()
        {
            logger = null;
            patchActive = false;
            enabled = true;
            currentShadowQuality = MinActiveShadowQuality;
            shadowRenderedList.Clear();
            prevFarRendered.Clear();
            currFarRendered.Clear();
        }
    }
}
