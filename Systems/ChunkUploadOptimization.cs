using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Bounds per-frame chunk upload time in ChunkTesselatorManager.OnBeforeFrame,
    /// skips redundant normal-queue priority recalculation and sorting when camera
    /// state and queue membership are unchanged, avoids per-frame delegate allocation,
    /// and disposes queued meshes targeting already-disposed chunks.
    /// </summary>
    public static class ChunkUploadOptimization
    {
        private const double MoveThresholdSq = 0.25;
        private const float RotationThresholdRad = 0.05f;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, object> queueLockRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, SortableQueue<TesselatedChunk>> queueRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, object> prioLockRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, Queue<TesselatedChunk>> prioQueueRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, Vec3i> tmpPosRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, int> singleUploadDelayRef;
        private static readonly AccessTools.FieldRef<ChunkTesselatorManager, bool> processPrioRef;

        private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> chunkRendererRef;
        private static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> dirtyPrioRef;
        private static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> dirtyRef;
        private static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> dirtyLastRef;

        private static readonly AccessTools.FieldRef<TesselatedChunk, int> posXRef;
        private static readonly AccessTools.FieldRef<TesselatedChunk, int> posYRef;
        private static readonly AccessTools.FieldRef<TesselatedChunk, int> posZRef;
        private static readonly AccessTools.FieldRef<TesselatedChunk, int> vertsRef;
        private static readonly AccessTools.FieldRef<TesselatedChunk, ClientChunk> tessChunkRef;
        private static readonly AccessTools.FieldRef<ClientChunk, bool> queuedForUploadRef;

        private static readonly System.Func<ClientWorldMap, int, int, int, ClientChunk> getChunkAtBlockPos;
        private static readonly Action<TesselatedChunk, ClientPlayer> recalcPriority;
        private static readonly Action<TesselatedChunk> unusedDispose;

        private static readonly bool accessorsReady;

        // Swappable timestamp source for deterministic unit tests.
        private static System.Func<long> timestampProvider = Stopwatch.GetTimestamp;

        private static ConditionalWeakTable<ChunkTesselatorManager, UploadState> states =
            new ConditionalWeakTable<ChunkTesselatorManager, UploadState>();

        static ChunkUploadOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
                queueLockRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, object>("tessChunksQueueLock");
                queueRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, SortableQueue<TesselatedChunk>>("tessChunksQueue");
                prioLockRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, object>("tessChunksQueuePriorityLock");
                prioQueueRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, Queue<TesselatedChunk>>("tessChunksQueuePriority");
                tmpPosRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, Vec3i>("tmpPos");
                singleUploadDelayRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, int>("singleUploadDelayCounter");
                processPrioRef = AccessTools.FieldRefAccess<ChunkTesselatorManager, bool>("processPrioQueue");

                chunkRendererRef = AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
                dirtyPrioRef = AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunksPriority");
                dirtyRef = AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunks");
                dirtyLastRef = AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunksLast");

                posXRef = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionX");
                posYRef = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionYAndDimension");
                posZRef = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionZ");
                vertsRef = AccessTools.FieldRefAccess<TesselatedChunk, int>("VerticesCount");
                tessChunkRef = AccessTools.FieldRefAccess<TesselatedChunk, ClientChunk>("chunk");
                queuedForUploadRef = AccessTools.FieldRefAccess<ClientChunk, bool>("queuedForUpload");

                MethodInfo getChunkMethod = AccessTools.Method(
                    typeof(ClientWorldMap),
                    "GetChunkAtBlockPos",
                    new[] { typeof(int), typeof(int), typeof(int) });
                MethodInfo recalcMethod = AccessTools.Method(
                    typeof(TesselatedChunk),
                    "RecalcPriority",
                    new[] { typeof(ClientPlayer) });
                MethodInfo unusedDisposeMethod = AccessTools.Method(
                    typeof(TesselatedChunk),
                    "UnusedDispose",
                    Type.EmptyTypes);

                if (getChunkMethod != null && recalcMethod != null && unusedDisposeMethod != null)
                {
                    getChunkAtBlockPos = (System.Func<ClientWorldMap, int, int, int, ClientChunk>)
                        Delegate.CreateDelegate(typeof(System.Func<ClientWorldMap, int, int, int, ClientChunk>), getChunkMethod);
                    recalcPriority = (Action<TesselatedChunk, ClientPlayer>)
                        Delegate.CreateDelegate(typeof(Action<TesselatedChunk, ClientPlayer>), recalcMethod);
                    unusedDispose = (Action<TesselatedChunk>)
                        Delegate.CreateDelegate(typeof(Action<TesselatedChunk>), unusedDisposeMethod);
                    accessorsReady = true;
                }
            }
            catch
            {
                accessorsReady = false;
            }
        }

        internal static long ComputeBudgetMs(int queueBacklog)
        {
            if (queueBacklog > 16)
            {
                return 8L;
            }

            if (queueBacklog > 4)
            {
                return 5L;
            }

            return 3L;
        }

        public static bool OnBeforeFrame_Prefix(ChunkTesselatorManager __instance, float dt)
        {
            if (!accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            if (game == null || game.WorldMap == null || game.frustumCuller == null)
            {
                return true;
            }

            ChunkRenderer chunkRenderer = chunkRendererRef(game);
            if (chunkRenderer == null)
            {
                return true;
            }

            Queue<TesselatedChunk> prioQueue = prioQueueRef(__instance);
            SortableQueue<TesselatedChunk> normalQueue = queueRef(__instance);
            if (prioQueue == null || normalQueue == null)
            {
                return false;
            }

            RuntimeStats.chunksAwaitingTesselation =
                (dirtyPrioRef(game)?.Count ?? 0) +
                (dirtyRef(game)?.Count ?? 0) +
                (dirtyLastRef(game)?.Count ?? 0);

            int prioCount = prioQueue.Count;
            int normalCountSnapshot = normalQueue.Count;
            RuntimeStats.chunksAwaitingPooling = prioCount + normalCountSnapshot;

            long uploadStartTicks = timestampProvider();
            long maxUploadMs = ComputeBudgetMs(prioCount + normalCountSnapshot);
            long maxUploadTicks = (Stopwatch.Frequency * maxUploadMs) / 1000L;

            int baseVertexBudget = game.frustumCuller.ViewDistanceSq / 48 + 350;
            int uploadedVertices = 0;

            ClientWorldMap worldMap = game.WorldMap;
            ClientEventManager eventManager = game.eventManager;
            Vec3i tmpPos = tmpPosRef(__instance);
            ref bool processPrio = ref processPrioRef(__instance);
            ref int singleUploadDelay = ref singleUploadDelayRef(__instance);

            if (processPrio)
            {
                object prioLock = prioLockRef(__instance);
                bool uploadedAnyPrio = false;

                lock (prioLock)
                {
                    prioQueue = prioQueueRef(__instance);
                    if (prioQueue == null)
                    {
                        processPrio = false;
                        return false;
                    }

                    while (prioQueue.Count > 0)
                    {
                        if (uploadedAnyPrio && (timestampProvider() - uploadStartTicks) >= maxUploadTicks)
                        {
                            break;
                        }

                        TesselatedChunk tc = prioQueue.Dequeue();
                        ClientChunk sourceChunk = tessChunkRef(tc);
                        if (sourceChunk != null)
                        {
                            queuedForUploadRef(sourceChunk) = false;
                        }

                        int posX = posXRef(tc);
                        int posY = posYRef(tc);
                        int posZ = posZRef(tc);
                        ClientChunk chunkAtBlockPos = getChunkAtBlockPos(worldMap, posX, posY, posZ);
                        if (chunkAtBlockPos != null && !chunkAtBlockPos.Disposed)
                        {
                            chunkRenderer.AddTesselatedChunk(tc, chunkAtBlockPos);
                            singleUploadDelay = 10;
                            uploadedVertices += vertsRef(tc);
                            tmpPos.Set(posX / 32, posY / 32, posZ / 32);
                            eventManager?.TriggerChunkRetesselated(tmpPos, chunkAtBlockPos);
                            uploadedAnyPrio = true;
                        }
                        else
                        {
                            unusedDispose(tc);
                        }
                    }

                    processPrio = prioQueue.Count > 0;
                }

                if (processPrio || (timestampProvider() - uploadStartTicks) >= maxUploadTicks)
                {
                    return false;
                }
            }

            normalQueue = queueRef(__instance);
            if (normalQueue == null)
            {
                return false;
            }

            int count = normalQueue.Count;
            int rateShift = ClientSettings.ChunkVerticesUploadRateLimiter;
            int divisor = (rateShift >= 0 && rateShift < 31) ? (1 << rateShift) : 1;
            int vertexLimit = baseVertexBudget * (3 + count / divisor);
            if (uploadedVertices >= vertexLimit || (count < 2 && (count == 0 || singleUploadDelay++ < 10)))
            {
                return false;
            }

            singleUploadDelay = 0;
            object queueLock = queueLockRef(__instance);

            lock (queueLock)
            {
                normalQueue = queueRef(__instance);
                if (normalQueue == null || normalQueue.Count == 0)
                {
                    return false;
                }

                UploadState state = states.GetOrCreateValue(__instance);
                ClientPlayer player = game.player;
                EntityPlayer entityPlayer = player?.Entity;

                if (entityPlayer != null)
                {
                    EntityPos pos = entityPlayer.Pos;
                    float yaw = player.CameraYaw;
                    float pitch = player.CameraPitch;

                    bool shouldResort = true;
                    if (state.hasSorted && normalQueue.Count == state.lastPostDrainCount)
                    {
                        double dx = pos.X - state.lastPlayerX;
                        double dy = pos.Y - state.lastPlayerY;
                        double dz = pos.Z - state.lastPlayerZ;
                        bool moved = (dx * dx + dy * dy + dz * dz) >= MoveThresholdSq;
                        bool rotated = Math.Abs(GameMath.AngleRadDistance(yaw, state.lastYaw)) >= RotationThresholdRad
                            || Math.Abs(pitch - state.lastPitch) >= RotationThresholdRad;
                        shouldResort = moved || rotated;
                    }

                    if (shouldResort)
                    {
                        state.activePlayer = player;
                        try
                        {
                            normalQueue.RunForEach(state.recalcPriorityCallback);
                        }
                        finally
                        {
                            state.activePlayer = null;
                        }

                        normalQueue.Sort();
                        state.hasSorted = true;
                        state.lastPlayerX = pos.X;
                        state.lastPlayerY = pos.Y;
                        state.lastPlayerZ = pos.Z;
                        state.lastYaw = yaw;
                        state.lastPitch = pitch;
                    }
                }

                bool uploadedAnyNormal = uploadedVertices > 0;
                while (normalQueue.Count > 0 && uploadedVertices < vertexLimit)
                {
                    if (uploadedAnyNormal && (timestampProvider() - uploadStartTicks) >= maxUploadTicks)
                    {
                        break;
                    }

                    TesselatedChunk tc = normalQueue.Dequeue();
                    ClientChunk sourceChunk = tessChunkRef(tc);
                    if (sourceChunk != null)
                    {
                        queuedForUploadRef(sourceChunk) = false;
                    }

                    int posX = posXRef(tc);
                    int posY = posYRef(tc);
                    int posZ = posZRef(tc);
                    ClientChunk chunkAtBlockPos = getChunkAtBlockPos(worldMap, posX, posY, posZ);
                    if (chunkAtBlockPos != null && !chunkAtBlockPos.Disposed)
                    {
                        chunkRenderer.AddTesselatedChunk(tc, chunkAtBlockPos);
                        uploadedVertices += vertsRef(tc);
                        tmpPos.Set(posX / 32, posY / 32, posZ / 32);
                        eventManager?.TriggerChunkRetesselated(tmpPos, chunkAtBlockPos);
                        uploadedAnyNormal = true;
                    }
                    else
                    {
                        unusedDispose(tc);
                    }
                }

                state.lastPostDrainCount = normalQueue.Count;
            }

            return false;
        }

        public static void Cleanup()
        {
            states = new ConditionalWeakTable<ChunkTesselatorManager, UploadState>();
        }

        private sealed class UploadState
        {
            internal bool hasSorted;
            internal double lastPlayerX;
            internal double lastPlayerY;
            internal double lastPlayerZ;
            internal float lastYaw;
            internal float lastPitch;
            internal int lastPostDrainCount;
            internal ClientPlayer activePlayer;
            internal readonly Action<TesselatedChunk> recalcPriorityCallback;

            public UploadState()
            {
                recalcPriorityCallback = RecalcSingleChunk;
            }

            private void RecalcSingleChunk(TesselatedChunk chunk)
            {
                recalcPriority(chunk, activePlayer);
            }
        }
    }
}
