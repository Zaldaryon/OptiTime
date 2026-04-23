using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace OptiTime
{
    public class DynamicLightOptimization
    {
        private static int cachedViewDistance = 256;
        private static readonly EntityDistanceComparer comparer = new EntityDistanceComparer();

        private static float GetOptimalLightRadius(int viewDistance)
        {
            return viewDistance switch
            {
                <= 128 => 35f,
                <= 256 => 45f,
                <= 384 => 52f,
                _ => 60f
            };
        }

        private class EntityDistanceComparer : IComparer<Entity>
        {
            public Vec3d plrPos;

            public int Compare(Entity a, Entity b)
            {
                return a.Pos.SquareDistanceTo(plrPos).CompareTo(b.Pos.SquareDistanceTo(plrPos));
            }
        }

        private static void PartialSort(Entity[] entities, int k, EntityDistanceComparer comparer)
        {
            for (int i = 0; i < k && i < entities.Length; i++)
            {
                int minIndex = i;
                double minDist = entities[i].Pos.SquareDistanceTo(comparer.plrPos);
                
                for (int j = i + 1; j < entities.Length; j++)
                {
                    double dist = entities[j].Pos.SquareDistanceTo(comparer.plrPos);
                    if (dist < minDist)
                    {
                        minIndex = j;
                        minDist = dist;
                    }
                }
                
                if (minIndex != i)
                {
                    Entity temp = entities[i];
                    entities[i] = entities[minIndex];
                    entities[minIndex] = temp;
                }
            }
        }

        public static bool OptimizeLightCulling(object __instance, float dt)
        {
            try
            {
                var instance = __instance as dynamic;
                var game = instance.game;

                game.shUniforms.PointLightsCount = 0;

                Vec3d plrPos = game.EntityPlayer.Pos.XYZ;
                float lightRadius = GetOptimalLightRadius(cachedViewDistance);

                Entity[] entities = game.GetEntitiesAround(plrPos, lightRadius, lightRadius,
                    (ActionConsumable<Entity>)(e => e.LightHsv != null && e.LightHsv[2] > 0));

                int maxDynLights = instance.maxDynLights;
                int entityCount = Math.Min(entities.Length, maxDynLights);

                if (entities.Length > maxDynLights)
                {
                    comparer.plrPos = plrPos;
                    PartialSort(entities, maxDynLights, comparer);
                }

                for (int i = 0; i < entityCount; i++)
                {
                    Entity entity = entities[i];
                    AddPointLight(instance, entity.LightHsv, entity.Pos);
                }

                var pointlights = game.pointlights;
                for (int i = 0; i < pointlights.Count; i++)
                {
                    var pointlight = pointlights[i];
                    AddPointLightVec3f(instance, pointlight.Color, pointlight.Pos);
                }

                game.api.Render.PerceptionEffects.OnBeforeGameRender(dt);

                return false;
            }
            catch
            {
                return true;
            }
        }

        public static void UpdateViewDistance(int newViewDistance)
        {
            cachedViewDistance = newViewDistance;
        }
        
        private static void AddPointLight(dynamic instance, byte[] lighthsv, EntityPos pos)
        {
            var game = instance.game;
            int count = game.shUniforms.PointLightsCount;
            if (count >= instance.maxDynLights) return;

            Vec4d inval = instance.inval;
            Vec4d outval = instance.outval;

            inval.Set(pos.X, pos.InternalY, pos.Z, 1.0);
            Mat4d.MulWithVec4(game.CurrentModelViewMatrixd, inval, outval);

            game.shUniforms.PointLights3[3 * count] = (float)outval.X;
            game.shUniforms.PointLights3[3 * count + 1] = (float)outval.Y;
            game.shUniforms.PointLights3[3 * count + 2] = (float)outval.Z;

            int h = game.WorldMap.hueLevels[lighthsv[0]];
            int s = game.WorldMap.satLevels[lighthsv[1]];
            int v = (int)(game.WorldMap.BlockLightLevels[lighthsv[2]] * 255);

            Vec3f outval3 = instance.outval3;
            ColorUtil.ToRGBVec3f(ColorUtil.HsvToRgba(h, s, v), ref outval3);

            game.shUniforms.PointLightColors3[3 * count] = outval3.Z;
            game.shUniforms.PointLightColors3[3 * count + 1] = outval3.Y;
            game.shUniforms.PointLightColors3[3 * count + 2] = outval3.X;

            game.shUniforms.PointLightsCount++;
        }

        private static void AddPointLightVec3f(dynamic instance, Vec3f color, Vec3d pos)
        {
            var game = instance.game;
            int count = game.shUniforms.PointLightsCount;
            if (count >= instance.maxDynLights) return;

            Vec4d inval = instance.inval;
            Vec4d outval = instance.outval;

            inval.Set(pos.X, pos.Y, pos.Z, 1.0);
            Mat4d.MulWithVec4(game.CurrentModelViewMatrixd, inval, outval);

            game.shUniforms.PointLights3[3 * count] = (float)outval.X;
            game.shUniforms.PointLights3[3 * count + 1] = (float)outval.Y;
            game.shUniforms.PointLights3[3 * count + 2] = (float)outval.Z;

            game.shUniforms.PointLightColors3[3 * count] = color.Z;
            game.shUniforms.PointLightColors3[3 * count + 1] = color.Y;
            game.shUniforms.PointLightColors3[3 * count + 2] = color.X;

            game.shUniforms.PointLightsCount++;
        }
    }
}
