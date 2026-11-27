using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OptiTime.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Replaces per-frame GetEntitiesAround allocations in SystemRenderPlayerEffects.onBeforeRender
    /// with a direct ClientWorldMap.chunks scan into pre-allocated top-K scratch arrays and a
    /// 15-frame stationary cache, re-projecting view-space light positions every frame.
    /// </summary>
    public class DynamicLightOptimization
    {
        private const int MaxCacheFrames = 15;
        private const double MoveThresholdSq = 0.25 * 0.25;
        private const int ChunksPerDimension = 1024;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<ClientMain, List<IPointLight>> pointlightsRef;
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, int> maxDynLightsRef;
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, Vec4d> invalRef;
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, Vec4d> outvalRef;
        private static readonly AccessTools.FieldRef<SystemRenderPlayerEffects, Vec3f> outval3Ref;
        private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> chunksRef;
        private static readonly AccessTools.FieldRef<ClientWorldMap, object> chunksLockRef;

        private static readonly bool accessorsReady;

        private static volatile int cachedViewDistance = 256;
        private static volatile float cachedLightRadius = 45f; // matches GetOptimalLightRadius(256)
        private static volatile bool enabled = true;

        private static readonly object statesLock = new object();
        private static ConditionalWeakTable<SystemRenderPlayerEffects, LightCacheState> states =
            new ConditionalWeakTable<SystemRenderPlayerEffects, LightCacheState>();

        static DynamicLightOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
                pointlightsRef = AccessTools.FieldRefAccess<ClientMain, List<IPointLight>>("pointlights");
                maxDynLightsRef = AccessTools.FieldRefAccess<SystemRenderPlayerEffects, int>("maxDynLights");
                invalRef = AccessTools.FieldRefAccess<SystemRenderPlayerEffects, Vec4d>("inval");
                outvalRef = AccessTools.FieldRefAccess<SystemRenderPlayerEffects, Vec4d>("outval");
                outval3Ref = AccessTools.FieldRefAccess<SystemRenderPlayerEffects, Vec3f>("outval3");
                chunksRef = AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
                chunksLockRef = AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");
                accessorsReady = true;
            }
            catch
            {
                accessorsReady = false;
            }
        }

        private static float GetOptimalLightRadius(int viewDistance) => viewDistance switch
        {
            <= 128 => 35f,
            <= 256 => 45f,
            <= 384 => 52f,
            _ => 60f
        };

        public static void Configure(bool isEnabled)
        {
            bool wasEnabled = enabled;
            enabled = isEnabled;
            if (wasEnabled && !isEnabled)
            {
                Cleanup();
            }
        }

        public static void UpdateViewDistance(int newViewDistance)
        {
            cachedViewDistance = newViewDistance;
            cachedLightRadius = GetOptimalLightRadius(newViewDistance);
        }

        public static void Cleanup()
        {
            lock (statesLock)
            {
                foreach (KeyValuePair<SystemRenderPlayerEffects, LightCacheState> entry in states)
                {
                    entry.Value.Clear();
                }

                states = new ConditionalWeakTable<SystemRenderPlayerEffects, LightCacheState>();
            }
        }

        public static bool OptimizeLightCulling(SystemRenderPlayerEffects __instance, float dt)
        {
            if (!enabled || !accessorsReady || __instance == null)
            {
                return true;
            }

            try
            {
                ClientMain game = gameRef(__instance);
                EntityPlayer entityPlayer = game?.EntityPlayer;
                EntityPos plrPos = entityPlayer?.Pos;
                ClientWorldMap worldMap = game?.WorldMap;
                if (game == null || plrPos == null || worldMap == null || game.shUniforms == null)
                {
                    return true;
                }

                int maxDynLights = maxDynLightsRef(__instance);
                Vec4d inval = invalRef(__instance);
                Vec4d outval = outvalRef(__instance);
                Vec3f outval3 = outval3Ref(__instance);

                game.shUniforms.PointLightsCount = 0;

                if (maxDynLights > 0)
                {
                    double px = plrPos.X;
                    double py = plrPos.InternalY;
                    double pz = plrPos.Z;
                    int dim = Math.Max(0, plrPos.Dimension);
                    float lightRadius = cachedLightRadius;
                    long plrEntityId = entityPlayer.EntityId;

                    LightCacheState state = states.GetOrCreateValue(__instance);
                    bool canReuse = state.cacheValid
                        && state.framesSinceScan < MaxCacheFrames
                        && state.lastScanDim == dim
                        && state.lastMaxDynLights == maxDynLights
                        && state.lastScanRadius == lightRadius
                        && state.lastPlayerEntityId == plrEntityId;

                    if (canReuse)
                    {
                        double dx = px - state.lastScanX;
                        double dy = py - state.lastScanY;
                        double dz = pz - state.lastScanZ;
                        canReuse = (dx * dx + dy * dy + dz * dz) < MoveThresholdSq;
                    }

                    if (canReuse)
                    {
                        state.framesSinceScan++;
                    }
                    else
                    {
                        ScanChunksForNearestLights(
                            state,
                            worldMap,
                            px,
                            py,
                            pz,
                            dim,
                            lightRadius,
                            maxDynLights,
                            plrEntityId);
                    }

                    double[] mvMatrix = game.CurrentModelViewMatrixd;
                    int rendered = 0;
                    Entity[] cached = state.cachedEntities;
                    int cachedCount = state.cachedCount;

                    for (int i = 0; i < cachedCount; i++)
                    {
                        Entity ent = cached[i];
                        if (ent == null || ent.State == EnumEntityState.Despawned || ent.Pos == null)
                        {
                            continue;
                        }

                        byte[] hsv = ent.LightHsv;
                        if (hsv == null || hsv.Length < 3 || hsv[2] <= 0)
                        {
                            continue;
                        }

                        AddPointLight(game, mvMatrix, maxDynLights, inval, outval, outval3, hsv, ent.Pos);
                        rendered++;
                    }

                    ModuleDynLights.OnFrame(rendered, Math.Max(rendered, state.cachedFoundCount));

                    List<IPointLight> pointlights = pointlightsRef(game);
                    if (pointlights != null)
                    {
                        for (int i = 0; i < pointlights.Count; i++)
                        {
                            IPointLight pl = pointlights[i];
                            if (pl != null)
                            {
                                AddPointLightVec3f(game, mvMatrix, maxDynLights, inval, outval, pl.Color, pl.Pos);
                            }
                        }
                    }
                }
                else
                {
                    ModuleDynLights.OnFrame(0, 0);
                }

                game.api?.Render?.PerceptionEffects?.OnBeforeGameRender(dt);
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void ScanChunksForNearestLights(
            LightCacheState state,
            ClientWorldMap worldMap,
            double px,
            double py,
            double pz,
            int dim,
            float lightRadius,
            int maxDynLights,
            long plrEntityId)
        {
            state.EnsureCapacity(maxDynLights);
            if (state.cachedCount > 0)
            {
                Array.Clear(state.cachedEntities, 0, state.cachedCount);
                state.cachedCount = 0;
            }

            Dictionary<long, ClientChunk> chunks = chunksRef(worldMap);
            object chunksLock = chunksLockRef(worldMap);
            int scratchCount = 0;
            int totalFound = 0;

            if (chunks != null && chunksLock != null)
            {
                double lightRadiusSq = (double)lightRadius * lightRadius;
                int dimBaseY = dim * ChunksPerDimension;
                int maxWorldCx = Math.Max(0, worldMap.ChunkMapSizeX - 1);
                int maxWorldCy = dimBaseY + Math.Max(0, worldMap.ChunkMapSizeY - 1);
                int maxWorldCz = Math.Max(0, worldMap.ChunkMapSizeZ - 1);

                int minCx = Math.Max(0, (int)Math.Floor((px - lightRadius) / 32.0));
                int maxCx = Math.Min(maxWorldCx, (int)Math.Floor((px + lightRadius) / 32.0));
                int minCy = Math.Max(dimBaseY, (int)Math.Floor((py - lightRadius) / 32.0));
                int maxCy = Math.Min(maxWorldCy, (int)Math.Floor((py + lightRadius) / 32.0));
                int minCz = Math.Max(0, (int)Math.Floor((pz - lightRadius) / 32.0));
                int maxCz = Math.Min(maxWorldCz, (int)Math.Floor((pz + lightRadius) / 32.0));

                int maxChunkCount = Math.Max(0, (maxCx - minCx + 1) * (maxCy - minCy + 1) * (maxCz - minCz + 1));
                state.EnsureChunkCapacity(maxChunkCount);

                int mulX = worldMap.index3dMulX;
                int mulZ = worldMap.index3dMulZ;
                ClientChunk[] scratchChunks = state.scratchChunks;
                int chunkCount = 0;

                // Snapshot only populated chunks under chunksLock; evaluate LightHsv and distances outside lock.
                lock (chunksLock)
                {
                    for (int cx = minCx; cx <= maxCx; cx++)
                    {
                        for (int cy = minCy; cy <= maxCy; cy++)
                        {
                            for (int cz = minCz; cz <= maxCz; cz++)
                            {
                                long key = MapUtil.Index3dL(cx, cy, cz, mulX, mulZ);
                                if (chunks.TryGetValue(key, out ClientChunk chunk) && chunk != null && chunk.EntitiesCount > 0)
                                {
                                    scratchChunks[chunkCount++] = chunk;
                                }
                            }
                        }
                    }
                }

                Entity[] scratch = state.scratchEntities;
                double[] scratchDistSq = state.scratchDistSq;
                int farthestIdx = 0;
                double farthestDistSq = -1.0;

                for (int c = 0; c < chunkCount; c++)
                {
                    ClientChunk chunk = scratchChunks[c];
                    Entity[] chunkEntities = chunk.Entities;
                    if (chunkEntities == null)
                    {
                        continue;
                    }

                    int entitiesCount = chunk.EntitiesCount;
                    for (int i = 0; i < chunkEntities.Length; i++)
                    {
                        Entity ent = chunkEntities[i];
                        if (ent == null)
                        {
                            if (i >= entitiesCount)
                            {
                                break;
                            }

                            continue;
                        }

                        if (ent.State == EnumEntityState.Despawned || ent.Pos == null)
                        {
                            continue;
                        }

                        byte[] hsv = ent.LightHsv;
                        if (hsv == null || hsv.Length < 3 || hsv[2] <= 0)
                        {
                            continue;
                        }

                        double dx = ent.Pos.X - px;
                        double dy = ent.Pos.InternalY - py;
                        double dz = ent.Pos.Z - pz;
                        double horizSq = dx * dx + dz * dz;
                        if (horizSq > lightRadiusSq || Math.Abs(dy) > lightRadius)
                        {
                            continue;
                        }

                        totalFound++;
                        double distSq = horizSq + dy * dy;

                        if (scratchCount < maxDynLights)
                        {
                            scratch[scratchCount] = ent;
                            scratchDistSq[scratchCount] = distSq;
                            if (distSq > farthestDistSq)
                            {
                                farthestDistSq = distSq;
                                farthestIdx = scratchCount;
                            }

                            scratchCount++;
                        }
                        else if (distSq < farthestDistSq)
                        {
                            scratch[farthestIdx] = ent;
                            scratchDistSq[farthestIdx] = distSq;

                            farthestIdx = 0;
                            farthestDistSq = scratchDistSq[0];
                            for (int k = 1; k < scratchCount; k++)
                            {
                                if (scratchDistSq[k] > farthestDistSq)
                                {
                                    farthestDistSq = scratchDistSq[k];
                                    farthestIdx = k;
                                }
                            }
                        }
                    }
                }

                if (chunkCount > 0)
                {
                    Array.Clear(scratchChunks, 0, chunkCount);
                }

                if (scratchCount > 0)
                {
                    Array.Copy(scratch, 0, state.cachedEntities, 0, scratchCount);
                    Array.Clear(scratch, 0, scratchCount);
                }
            }

            state.cachedCount = scratchCount;
            state.cachedFoundCount = totalFound;
            state.lastScanX = px;
            state.lastScanY = py;
            state.lastScanZ = pz;
            state.lastScanDim = dim;
            state.lastScanRadius = lightRadius;
            state.lastMaxDynLights = maxDynLights;
            state.lastPlayerEntityId = plrEntityId;
            state.framesSinceScan = 0;
            state.cacheValid = true;
        }

        private static void AddPointLight(
            ClientMain game,
            double[] mvMatrix,
            int maxDynLights,
            Vec4d inval,
            Vec4d outval,
            Vec3f outval3,
            byte[] lighthsv,
            EntityPos pos)
        {
            int count = game.shUniforms.PointLightsCount;
            if (count >= maxDynLights)
            {
                return;
            }

            inval.Set(pos.X, pos.InternalY, pos.Z, 1.0);
            Mat4d.MulWithVec4(mvMatrix, inval, outval);

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

        private static void AddPointLightVec3f(
            ClientMain game,
            double[] mvMatrix,
            int maxDynLights,
            Vec4d inval,
            Vec4d outval,
            Vec3f color,
            Vec3d pos)
        {
            int count = game.shUniforms.PointLightsCount;
            if (count >= maxDynLights)
            {
                return;
            }

            inval.Set(pos.X, pos.Y, pos.Z, 1.0);
            Mat4d.MulWithVec4(mvMatrix, inval, outval);

            game.shUniforms.PointLights3[3 * count] = (float)outval.X;
            game.shUniforms.PointLights3[3 * count + 1] = (float)outval.Y;
            game.shUniforms.PointLights3[3 * count + 2] = (float)outval.Z;

            game.shUniforms.PointLightColors3[3 * count] = color.Z;
            game.shUniforms.PointLightColors3[3 * count + 1] = color.Y;
            game.shUniforms.PointLightColors3[3 * count + 2] = color.X;
            game.shUniforms.PointLightsCount++;
        }

        private sealed class LightCacheState
        {
            internal ClientChunk[] scratchChunks = Array.Empty<ClientChunk>();
            internal Entity[] scratchEntities = Array.Empty<Entity>();
            internal double[] scratchDistSq = Array.Empty<double>();
            internal Entity[] cachedEntities = Array.Empty<Entity>();
            internal int cachedCount;
            internal int cachedFoundCount;
            internal int framesSinceScan;
            internal double lastScanX;
            internal double lastScanY;
            internal double lastScanZ;
            internal int lastScanDim;
            internal float lastScanRadius;
            internal int lastMaxDynLights;
            internal long lastPlayerEntityId;
            internal bool cacheValid;

            public LightCacheState()
            {
            }

            internal void EnsureCapacity(int capacity)
            {
                if (scratchEntities.Length < capacity)
                {
                    scratchEntities = new Entity[capacity];
                    scratchDistSq = new double[capacity];
                }

                if (cachedEntities.Length < capacity)
                {
                    cachedEntities = new Entity[capacity];
                }
            }

            internal void EnsureChunkCapacity(int capacity)
            {
                if (scratchChunks.Length < capacity)
                {
                    scratchChunks = new ClientChunk[capacity];
                }
            }

            internal void Clear()
            {
                if (scratchChunks.Length > 0)
                {
                    Array.Clear(scratchChunks, 0, scratchChunks.Length);
                }

                if (scratchEntities.Length > 0)
                {
                    Array.Clear(scratchEntities, 0, scratchEntities.Length);
                }

                if (cachedEntities.Length > 0)
                {
                    Array.Clear(cachedEntities, 0, cachedEntities.Length);
                }

                cachedCount = 0;
                cachedFoundCount = 0;
                framesSinceScan = 0;
                cacheValid = false;
            }
        }
    }
}
