using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;

namespace OptiTime
{
    public class EntityAnimationOptimization
    {
        private static byte frameCounter = 0;
        private const double CLOSE_DIST_SQ = 2304.0;   // 48 blocks (was 24) - more conservative to reduce visual artifacts
        private const double MEDIUM_DIST_SQ = 6400.0;  // 80 blocks (was 40) - more conservative to reduce visual artifacts

        public static bool OptimizeEntityAnimations(object __instance, float dt)
        {
            try
            {
                var instance = __instance as dynamic;
                var game = instance.game;
                var frustumCuller = game.frustumCuller;

                frameCounter++;
                bool mediumFrame = (frameCounter & 1) == 0;
                bool farFrame = (frameCounter % 3) == 0;

                int viewDistanceSq = frustumCuller.ViewDistanceSq;
                var plrPos = game.EntityPlayer.Pos.XYZ;
                int dimension = game.EntityPlayer.Pos.Dimension;
                double maxAnimationDistSq = viewDistanceSq * 1.2;

                foreach (var kvp in (IEnumerable<dynamic>)game.EntityRenderers)
                {
                    var entityRenderer = kvp.Value;
                    Entity entity = entityRenderer.entity;

                    bool isPlayer = entity == game.EntityPlayer;
                    bool allowOutside = entity.AllowOutsideLoadedRange;

                    // 1. Dimension check (cheapest)
                    if (!isPlayer && !allowOutside && entity.Pos.Dimension != dimension)
                    {
                        entity.IsRendered = false;
                        continue;
                    }

                    // 2. Distance check (cheap) - MOVED UP for early rejection
                    double distSq = plrPos.HorizontalSquareDistanceTo(entity.Pos.X, entity.Pos.Z);

                    if (!isPlayer && !allowOutside && distSq > maxAnimationDistSq)
                    {
                        entity.IsRendered = false;
                        continue;
                    }

                    // 3. Frustum check (expensive) - MOVED DOWN after distance check
                    bool inFrustum = frustumCuller.SphereInFrustum(
                        entity.Pos.X,
                        entity.Pos.InternalY,
                        entity.Pos.Z,
                        entity.FrustumSphereRadius);

                    if (!inFrustum && !isPlayer && !allowOutside)
                    {
                        entity.IsRendered = false;
                        continue;
                    }

                    bool inRange = allowOutside ||
                        (distSq < viewDistanceSq &&
                        (isPlayer ||
                         game.WorldMap.IsChunkRendered((int)entity.Pos.X / 32, (int)entity.Pos.InternalY / 32, (int)entity.Pos.Z / 32)));

                    if (inFrustum && inRange)
                    {
                        entity.IsRendered = true;
                        entityRenderer.BeforeRender(dt);

                        bool shouldUpdate = isPlayer || allowOutside || distSq < CLOSE_DIST_SQ ||
                            (distSq < MEDIUM_DIST_SQ ? mediumFrame : farFrame);

                        if (shouldUpdate)
                        {
                            game.api.World.FrameProfiler.Mark("esr-beforeanim");
                            try
                            {
                                entity.AnimManager?.OnClientFrame(dt);
                            }
                            catch (Exception)
                            {
                                game.Logger.Error($"Animations error for entity {entity.Code.ToShortString()} at {entity.Pos.AsBlockPos?.ToString()}");
                                throw;
                            }
                            game.api.World.FrameProfiler.Mark("esr-afteranim");
                        }
                    }
                    else
                    {
                        entity.IsRendered = false;

                        if (isPlayer || allowOutside)
                        {
                            game.api.World.FrameProfiler.Mark("esr-beforeanim");
                            try
                            {
                                entity.AnimManager?.OnClientFrame(dt);
                            }
                            catch (Exception)
                            {
                                game.Logger.Error($"Animations error for entity {entity.Code.ToShortString()} at {entity.Pos.AsBlockPos?.ToString()}");
                                throw;
                            }
                            game.api.World.FrameProfiler.Mark("esr-afteranim");
                        }
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
