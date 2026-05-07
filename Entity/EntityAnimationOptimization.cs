using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public class EntityAnimationOptimization
    {
        private static byte frameCounter;
        // LOD thresholds scaled with the active client view distance (audit B6).
        // Defaults match the historical hardcoded values at VD = 256:
        //   close   = max(32, VD / 4)     → 48 at VD=192, 64 at VD=256, 128 at VD=512
        //   medium  = max(64, VD / 2)     → 80 at VD=160, 128 at VD=256, 256 at VD=512
        // UpdateViewDistance is called from OptiTimeMod.RegisterViewDistanceWatcher.
        private static double closeDistSq = 64.0 * 64.0;       // VD=256 default
        private static double mediumDistSq = 128.0 * 128.0;    // VD=256 default

        public static void UpdateViewDistance(int viewDistance)
        {
            double close = System.Math.Max(32.0, viewDistance / 4.0);
            double medium = System.Math.Max(64.0, viewDistance / 2.0);
            closeDistSq = close * close;
            mediumDistSq = medium * medium;
        }

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
        private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> entityRenderersRef =
            AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");

        public static bool OptimizeEntityAnimations(SystemRenderEntities __instance, float dt)
        {
            try
            {
                var game = gameRef(__instance);
                var frustumCuller = game.frustumCuller;

                frameCounter++;
                bool mediumFrame = (frameCounter & 1) == 0;
                bool farFrame = (frameCounter % 3) == 0;

                int viewDistanceSq = frustumCuller.ViewDistanceSq;
                var plrPos = game.EntityPlayer.Pos.XYZ;
                int dimension = game.EntityPlayer.Pos.Dimension;
                double maxAnimationDistSq = viewDistanceSq * 1.2;

                foreach (var kvp in entityRenderersRef(game))
                {
                    var entityRenderer = kvp.Value;
                    Entity entity = entityRenderer.entity;

                    bool isPlayer = entity == game.EntityPlayer;
                    bool allowOutside = entity.AllowOutsideLoadedRange;

                    if (!isPlayer && entity.Pos.Dimension != dimension)
                    {
                        entity.IsRendered = false;
                        continue;
                    }

                    double distSq = plrPos.HorizontalSquareDistanceTo(entity.Pos.X, entity.Pos.Z);

                    if (!isPlayer && !allowOutside && distSq > maxAnimationDistSq)
                    {
                        entity.IsRendered = false;
                        continue;
                    }

                    bool inFrustum = frustumCuller.SphereInFrustum(
                        entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z,
                        entity.FrustumSphereRadius);

                    if (!inFrustum && !isPlayer && !allowOutside)
                    {
                        entity.IsRendered = false;
                        continue;
                    }

                    bool inRange = allowOutside ||
                        (distSq < viewDistanceSq &&
                        (isPlayer ||
                         game.WorldMap.IsChunkRendered(
                             (int)entity.Pos.X / 32,
                             (int)entity.Pos.InternalY / 32,
                             (int)entity.Pos.Z / 32)));

                    if (inFrustum && inRange)
                    {
                        entity.IsRendered = true;
                        entityRenderer.BeforeRender(dt);

                        bool shouldUpdate = isPlayer || allowOutside || distSq < closeDistSq ||
                            (distSq < mediumDistSq ? mediumFrame : farFrame);

                        if (shouldUpdate)
                        {
                            game.api.World.FrameProfiler.Mark("esr-beforeanim");
                            try { entity.AnimManager?.OnClientFrame(dt); }
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
                            try { entity.AnimManager?.OnClientFrame(dt); }
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
