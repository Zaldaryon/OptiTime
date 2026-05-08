using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public class DynamicLightOptimization
    {
        private static int cachedViewDistance = 256;
        private static readonly EntityDistanceComparer comparer = new EntityDistanceComparer();

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
        private static readonly AccessTools.FieldRef<ClientMain, System.Collections.Generic.List<IPointLight>> pointlightsRef =
            AccessTools.FieldRefAccess<ClientMain, System.Collections.Generic.List<IPointLight>>("pointlights");
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, int> maxDynLightsRef =
            AccessTools.FieldRefAccess<SystemRenderPlayerEffects, int>("maxDynLights");
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, Vec4d> invalRef =
            AccessTools.FieldRefAccess<SystemRenderPlayerEffects, Vec4d>("inval");
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, Vec4d> outvalRef =
            AccessTools.FieldRefAccess<SystemRenderPlayerEffects, Vec4d>("outval");
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, Vec3f> outval3Ref =
            AccessTools.FieldRefAccess<SystemRenderPlayerEffects, Vec3f>("outval3");

        private static float GetOptimalLightRadius(int viewDistance) => viewDistance switch
        {
            <= 128 => 35f,
            <= 256 => 45f,
            <= 384 => 52f,
            _ => 60f
        };

        private class EntityDistanceComparer : IComparer<Entity>
        {
            public Vec3d plrPos;
            public int Compare(Entity a, Entity b) =>
                a.Pos.SquareDistanceTo(plrPos).CompareTo(b.Pos.SquareDistanceTo(plrPos));
        }

        private static void PartialSort(Entity[] entities, int k, EntityDistanceComparer cmp)
        {
            for (int i = 0; i < k && i < entities.Length; i++)
            {
                int minIdx = i;
                double minDist = entities[i].Pos.SquareDistanceTo(cmp.plrPos);
                for (int j = i + 1; j < entities.Length; j++)
                {
                    double dist = entities[j].Pos.SquareDistanceTo(cmp.plrPos);
                    if (dist < minDist) { minIdx = j; minDist = dist; }
                }
                if (minIdx != i) (entities[i], entities[minIdx]) = (entities[minIdx], entities[i]);
            }
        }

        public static bool OptimizeLightCulling(SystemRenderPlayerEffects __instance, float dt)
        {
            try
            {
                var game = gameRef(__instance);
                int maxDynLights = maxDynLightsRef(__instance);
                Vec4d inval = invalRef(__instance);
                Vec4d outval = outvalRef(__instance);
                Vec3f outval3 = outval3Ref(__instance);

                game.shUniforms.PointLightsCount = 0;
                Vec3d plrPos = game.EntityPlayer.Pos.XYZ;
                float lightRadius = GetOptimalLightRadius(cachedViewDistance);

                Entity[] entities = game.GetEntitiesAround(plrPos, lightRadius, lightRadius,
                    (ActionConsumable<Entity>)(e => e.LightHsv != null && e.LightHsv[2] > 0));

                int entityCount = Math.Min(entities.Length, maxDynLights);
                if (entities.Length > maxDynLights)
                {
                    comparer.plrPos = plrPos;
                    PartialSort(entities, maxDynLights, comparer);
                }

                for (int i = 0; i < entityCount; i++)
                    AddPointLight(game, maxDynLights, inval, outval, outval3, entities[i].LightHsv, entities[i].Pos);

                var pointlights = pointlightsRef(game);
                for (int i = 0; i < pointlights.Count; i++)
                    AddPointLightVec3f(game, maxDynLights, inval, outval, pointlights[i].Color, pointlights[i].Pos);

                game.api.Render.PerceptionEffects.OnBeforeGameRender(dt);
                return false;
            }
            catch
            {
                return true;
            }
        }

        public static void UpdateViewDistance(int newViewDistance) => cachedViewDistance = newViewDistance;

        private static void AddPointLight(ClientMain game, int maxDynLights, Vec4d inval, Vec4d outval, Vec3f outval3, byte[] lighthsv, EntityPos pos)
        {
            int count = game.shUniforms.PointLightsCount;
            if (count >= maxDynLights) return;

            inval.Set(pos.X, pos.InternalY, pos.Z, 1.0);
            Mat4d.MulWithVec4(game.CurrentModelViewMatrixd, inval, outval);

            game.shUniforms.PointLights3[3 * count] = (float)outval.X;
            game.shUniforms.PointLights3[3 * count + 1] = (float)outval.Y;
            game.shUniforms.PointLights3[3 * count + 2] = (float)outval.Z;

            int num = lighthsv[2];
            int h = game.WorldMap.hueLevels[lighthsv[0]];
            int s = game.WorldMap.satLevels[lighthsv[1]];
            int v = (int)(game.WorldMap.BlockLightLevels[num] * 255);
            ColorUtil.ToRGBVec3f(ColorUtil.HsvToRgba(h, s, v), ref outval3);

            game.shUniforms.PointLightColors3[3 * count] = outval3.Z * num;
            game.shUniforms.PointLightColors3[3 * count + 1] = outval3.Y * num;
            game.shUniforms.PointLightColors3[3 * count + 2] = outval3.X * num;
            game.shUniforms.PointLightsCount++;
        }

        private static void AddPointLightVec3f(ClientMain game, int maxDynLights, Vec4d inval, Vec4d outval, Vec3f color, Vec3d pos)
        {
            int count = game.shUniforms.PointLightsCount;
            if (count >= maxDynLights) return;

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
