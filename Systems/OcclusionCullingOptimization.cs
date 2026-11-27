using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using OptiTime.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common.Database;

namespace OptiTime
{
    /// <summary>
    /// Replaces ChunkCuller's 3-rays-per-shell raycast walk with a conservative flat-grid
    /// BFS visibility flood outside chunksLock, paired with dynamic view-distance threshold
    /// scaling and a 150 ms same-chunk cooldown.
    /// </summary>
    public class OcclusionCullingOptimization
    {
        private const int MinChunkThreshold = 70;
        private const int MaxChunkThreshold = 200;
        private const int DefaultThreshold = 100;
        private const long ExtraDimensionsStart = 4503599627370496L;

        private static readonly AccessTools.FieldRef<ChunkCuller, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<ChunkCuller, Vec3i> centerPosRef;
        private static readonly AccessTools.FieldRef<ChunkCuller, bool> isAboveHeightLimitRef;
        private static readonly AccessTools.FieldRef<ChunkCuller, int> qCountRef;
        private static readonly AccessTools.FieldRef<ChunkCuller, bool> nowOffRef;

        private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> chunkRendererRef;
        private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> chunksRef;
        private static readonly AccessTools.FieldRef<ClientWorldMap, object> chunksLockRef;

        private static readonly Action<ClientChunk, bool> setVisible;
        private static readonly Action<ChunkRenderer> swapVisibleBuffers;

        private static readonly bool accessorsReady;

        private static volatile int cachedThreshold = DefaultThreshold;
        private static volatile bool enabled = true;
        private static volatile int configGeneration;

        // Swappable millisecond clock for deterministic unit tests.
        private static System.Func<long> tickCountMsProvider = () => Environment.TickCount64;

        private static ConditionalWeakTable<ChunkCuller, CullerState> states =
            new ConditionalWeakTable<ChunkCuller, CullerState>();

        static OcclusionCullingOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ChunkCuller, ClientMain>("game");
                centerPosRef = AccessTools.FieldRefAccess<ChunkCuller, Vec3i>("centerpos");
                isAboveHeightLimitRef = AccessTools.FieldRefAccess<ChunkCuller, bool>("isAboveHeightLimit");
                qCountRef = AccessTools.FieldRefAccess<ChunkCuller, int>("qCount");
                nowOffRef = AccessTools.FieldRefAccess<ChunkCuller, bool>("nowOff");

                chunkRendererRef = AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
                chunksRef = AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
                chunksLockRef = AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");

                MethodInfo setVisibleMethod = AccessTools.Method(typeof(ClientChunk), "SetVisible", new[] { typeof(bool) });
                MethodInfo swapBuffersMethod = AccessTools.Method(typeof(ChunkRenderer), "SwapVisibleBuffers", Type.EmptyTypes);

                if (setVisibleMethod != null && swapBuffersMethod != null)
                {
                    setVisible = (Action<ClientChunk, bool>)Delegate.CreateDelegate(typeof(Action<ClientChunk, bool>), setVisibleMethod);
                    swapVisibleBuffers = (Action<ChunkRenderer>)Delegate.CreateDelegate(typeof(Action<ChunkRenderer>), swapBuffersMethod);
                    accessorsReady = true;
                }
            }
            catch
            {
                accessorsReady = false;
            }
        }

        public static void Configure(bool isEnabled)
        {
            bool wasEnabled = enabled;
            enabled = isEnabled;
            if (!wasEnabled && isEnabled)
            {
                Interlocked.Increment(ref configGeneration);
            }
        }

        public static void UpdateViewDistance(int viewDistance)
        {
            int vdChunks = Math.Max(1, viewDistance / 32);
            int threshold = vdChunks * vdChunks;
            cachedThreshold = Math.Clamp(threshold, MinChunkThreshold, MaxChunkThreshold);
        }

        public static void Cleanup()
        {
            cachedThreshold = DefaultThreshold;
            enabled = true;
            states = new ConditionalWeakTable<ChunkCuller, CullerState>();
        }

        public static bool CullInvisibleChunks_Prefix(ChunkCuller __instance)
        {
            if (!enabled || !accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            if (game == null || game.WorldMap == null)
            {
                return true;
            }

            ClientWorldMap worldMap = game.WorldMap;
            Dictionary<long, ClientChunk> chunks = chunksRef(worldMap);
            object chunksLock = chunksLockRef(worldMap);
            ChunkRenderer chunkRenderer = chunkRendererRef(game);
            if (chunks == null || chunksLock == null || chunkRenderer == null)
            {
                return true;
            }

            ModuleOcclusion.OnFrame(true);
            int threshold = cachedThreshold;
            ref bool nowOff = ref nowOffRef(__instance);

            if (!ClientSettings.Occlusionculling || chunks.Count < threshold)
            {
                if (nowOff)
                {
                    return false;
                }

                ClientChunk.bufIndex = 1;
                lock (chunksLock)
                {
                    foreach (KeyValuePair<long, ClientChunk> chunk in chunks)
                    {
                        if (chunk.Key / ExtraDimensionsStart != 1)
                        {
                            setVisible(chunk.Value, true);
                        }
                    }
                }

                ClientChunk.bufIndex = 0;
                lock (chunksLock)
                {
                    foreach (KeyValuePair<long, ClientChunk> chunk in chunks)
                    {
                        if (chunk.Key / ExtraDimensionsStart != 1)
                        {
                            setVisible(chunk.Value, true);
                        }
                    }
                }

                nowOff = true;
                return false;
            }

            nowOff = false;

            EntityPlayer entityPlayer = game.player?.Entity;
            if (entityPlayer == null)
            {
                return true;
            }

            Vec3d cameraPos = entityPlayer.CameraPos;
            int currentDim = Math.Max(0, entityPlayer.Pos.Dimension);
            int dimOffset = currentDim * ChunkVisibilityGrid.ChunksPerDimension;
            int camChunkX = (int)(cameraPos.X / 32.0);
            int camChunkY = ChunkVisibilityGrid.ResolveCameraChunkY(cameraPos.Y, currentDim);
            int camChunkZ = (int)(cameraPos.Z / 32.0);

            Vec3i centerPos = centerPosRef(__instance);
            CullerState state = states.GetOrCreateValue(__instance);
            int currentGen = configGeneration;
            bool sameChunk = state.lastConfigGeneration == currentGen
                && centerPos.Equals(camChunkX, camChunkY, camChunkZ);

            int regenCount = game.chunkPositionsForRegenTrav.Count;
            ref int qCount = ref qCountRef(__instance);
            long nowMs = tickCountMsProvider();

            if (!ChunkVisibilityGrid.ShouldRecalculate(
                    sameChunk,
                    regenCount,
                    qCount,
                    ref state.recalcPending,
                    nowMs,
                    state.lastRecalcMs))
            {
                return false;
            }

            state.lastRecalcMs = nowMs;
            state.lastConfigGeneration = currentGen;
            qCount = regenCount;
            centerPos.Set(camChunkX, camChunkY, camChunkZ);
            isAboveHeightLimitRef(__instance) = (camChunkY - dimOffset) >= worldMap.ChunkMapSizeY;

            // Size the flat grid once on the culling thread; later steps read grid's own dimensions.
            ChunkVisibilityGrid grid = state.grid;
            grid.Prepare(ClientSettings.ViewDistance / 32 + 4, worldMap.ChunkMapSizeY + 2);
            if (state.chunkGrid.Length < grid.CellCount)
            {
                state.chunkGrid = new ClientChunk[grid.CellCount];
            }

            ClientChunk[] chunkGrid = state.chunkGrid;
            int w = grid.Width;
            int h = grid.Height;
            int r = grid.RadiusXZ;
            int ry = grid.RadiusY;
            long mulX = worldMap.index3dMulX;
            long mulXZ = mulX * worldMap.index3dMulZ;
            long cx = camChunkX;
            long cy = camChunkY;
            long cz = camChunkZ;

            lock (chunksLock)
            {
                foreach (KeyValuePair<long, ClientChunk> item in chunks)
                {
                    ClientChunk chunk = item.Value;
                    long key = item.Key;
                    long y = key / mulXZ;
                    if (y / ChunkVisibilityGrid.ChunksPerDimension != currentDim)
                    {
                        setVisible(chunk, true);
                        continue;
                    }

                    setVisible(chunk, false);

                    long ly = y - cy + ry;
                    if ((ulong)ly >= (ulong)h)
                    {
                        continue;
                    }

                    long rem = key - y * mulXZ;
                    long z = rem / mulX;
                    long lz = z - cz + r;
                    long lx = rem - z * mulX - cx + r;
                    if ((ulong)lx >= (ulong)w || (ulong)lz >= (ulong)w)
                    {
                        continue;
                    }

                    int idx = (int)((ly * w + lz) * w + lx);
                    chunkGrid[idx] = chunk;
                    bool fresh = Volatile.Read(ref chunk.traversabilityFresh);
                    grid.SetCell(idx, ChunkVisibilityGrid.EncodeCell(chunk.traversability, fresh));
                }

                // Keep the immediate neighborhood around the camera visible regardless of traversability.
                for (int i = -1; i <= 1; i++)
                {
                    for (int j = -1; j <= 2; j++)
                    {
                        for (int k = -1; k <= 1; k++)
                        {
                            long key = worldMap.ChunkIndex3D(new ChunkPos(i + camChunkX, j + camChunkY, k + camChunkZ));
                            if (chunks.TryGetValue(key, out ClientChunk neighbor))
                            {
                                setVisible(neighbor, true);
                            }
                        }
                    }
                }
            }

            // Flood visibility outside chunksLock using dimension-local camera Y.
            grid.Run(
                camChunkX,
                camChunkY - dimOffset,
                camChunkZ,
                worldMap.ChunkMapSizeX,
                worldMap.ChunkMapSizeY,
                worldMap.ChunkMapSizeZ,
                useOcclusion: true);

            int reachedCount = grid.QueueLength;
            for (int i = 0; i < reachedCount; i++)
            {
                ClientChunk chunk = chunkGrid[grid.ReachedCell(i)];
                if (chunk != null)
                {
                    setVisible(chunk, true);
                }
            }

            // Release chunk references immediately so unloaded chunks are never retained across passes.
            Array.Clear(chunkGrid, 0, grid.CellCount);

            swapVisibleBuffers(chunkRenderer);
            return false;
        }

        private sealed class CullerState
        {
            internal readonly ChunkVisibilityGrid grid = new ChunkVisibilityGrid();
            internal ClientChunk[] chunkGrid = Array.Empty<ClientChunk>();
            internal long lastRecalcMs;
            internal bool recalcPending;
            internal int lastConfigGeneration = -1;

            public CullerState()
            {
            }
        }
    }
}
