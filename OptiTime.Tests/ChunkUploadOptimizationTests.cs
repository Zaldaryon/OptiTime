using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class ChunkUploadOptimizationTests : IDisposable
    {
        private const string TestHarmonyId = "com.zaldaryon.optitime.test.chunkupload";

        private static readonly FieldInfo GameField = AccessTools.Field(typeof(ClientSystem), "game");
        private static readonly FieldInfo QueueLockField = AccessTools.Field(typeof(ChunkTesselatorManager), "tessChunksQueueLock");
        private static readonly FieldInfo QueueField = AccessTools.Field(typeof(ChunkTesselatorManager), "tessChunksQueue");
        private static readonly FieldInfo PrioLockField = AccessTools.Field(typeof(ChunkTesselatorManager), "tessChunksQueuePriorityLock");
        private static readonly FieldInfo PrioQueueField = AccessTools.Field(typeof(ChunkTesselatorManager), "tessChunksQueuePriority");
        private static readonly FieldInfo TmpPosField = AccessTools.Field(typeof(ChunkTesselatorManager), "tmpPos");
        private static readonly FieldInfo SingleUploadDelayField = AccessTools.Field(typeof(ChunkTesselatorManager), "singleUploadDelayCounter");
        private static readonly FieldInfo ProcessPrioField = AccessTools.Field(typeof(ChunkTesselatorManager), "processPrioQueue");

        private static readonly FieldInfo TessPosXField = AccessTools.Field(typeof(TesselatedChunk), "positionX");
        private static readonly FieldInfo TessPosYField = AccessTools.Field(typeof(TesselatedChunk), "positionYAndDimension");
        private static readonly FieldInfo TessPosZField = AccessTools.Field(typeof(TesselatedChunk), "positionZ");
        private static readonly FieldInfo TessChunkField = AccessTools.Field(typeof(TesselatedChunk), "chunk");
        private static readonly FieldInfo TessVertsField = AccessTools.Field(typeof(TesselatedChunk), "VerticesCount");
        private static readonly FieldInfo ChunkQueuedField = AccessTools.Field(typeof(ClientChunk), "queuedForUpload");
        private static readonly FieldInfo WorldChunkDisposedField = AccessTools.Field(typeof(WorldChunk), "Disposed");
        private static readonly PropertyInfo WorldChunkDisposedProp = AccessTools.Property(typeof(WorldChunk), "Disposed");

        private static readonly FieldInfo ClientChunkRendererField = AccessTools.Field(typeof(ClientMain), "chunkRenderer");
        private static readonly FieldInfo DirtyPrioField = AccessTools.Field(typeof(ClientMain), "dirtyChunksPriority");
        private static readonly FieldInfo DirtyField = AccessTools.Field(typeof(ClientMain), "dirtyChunks");
        private static readonly FieldInfo DirtyLastField = AccessTools.Field(typeof(ClientMain), "dirtyChunksLast");

        private static Dictionary<(int x, int y, int z), ClientChunk> currentChunkLookup = new Dictionary<(int, int, int), ClientChunk>();
        private static List<TesselatedChunk> uploadedChunks = new List<TesselatedChunk>();
        private static List<TesselatedChunk> unusedDisposedChunks = new List<TesselatedChunk>();
        private static long syntheticTicks;
        private static double uploadDurationMs;
        private static int recalcPriorityCalls;
        private static int sortCalls;

        private readonly Harmony harmony;

        static ChunkUploadOptimizationTests()
        {
            string gameDirectory = FindGameDirectory();
            if (gameDirectory == null)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Lib"), Path.Combine(gameDirectory, "Mods") })
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }

                return null;
            };
        }

        public ChunkUploadOptimizationTests()
        {
            harmony = new Harmony(TestHarmonyId);
            ResetHookState();
            InstallHarnessHooks();
            InstallOptimizationUnderTestIfPresent();
        }

        public void Dispose()
        {
            RestoreOptimizationState();
            harmony.UnpatchAll(TestHarmonyId);
            ResetHookState();
        }

        [Fact]
        public void PriorityQueueUpload_CapsPerFrameTimeToAdaptiveBudgetAndAlwaysUploadsAtLeastOne()
        {
            // Tier 2: 6 priority chunks (backlog > 4, <= 16 -> 5 ms budget), 2.5 ms each -> 2 per frame
            (ChunkTesselatorManager manager, _, Queue<TesselatedChunk> prioQueue, _) = CreateManagerHarness();
            for (int i = 0; i < 6; i++)
            {
                prioQueue.Enqueue(CreateTesselatedChunk(i * 32, 0, 0, vertices: 100, disposed: false));
            }
            ProcessPrioField.SetValue(manager, true);
            uploadDurationMs = 2.5;

            manager.OnBeforeFrame(0.016f);

            Assert.Equal(2, uploadedChunks.Count);
            Assert.Equal(4, prioQueue.Count);
            Assert.True((bool)ProcessPrioField.GetValue(manager));

            // Subsequent frames drain remaining priority chunks without dropping any
            manager.OnBeforeFrame(0.016f);
            Assert.Equal(4, uploadedChunks.Count);
            Assert.Equal(2, prioQueue.Count);
            Assert.True((bool)ProcessPrioField.GetValue(manager));

            manager.OnBeforeFrame(0.016f);
            Assert.Equal(6, uploadedChunks.Count);
            Assert.Empty(prioQueue);
            Assert.False((bool)ProcessPrioField.GetValue(manager));

            // Tier 1: 3 priority chunks (backlog <= 4 -> 3 ms budget), single chunk takes 10 ms -> always uploads >= 1
            uploadedChunks.Clear();
            for (int i = 10; i < 13; i++)
            {
                prioQueue.Enqueue(CreateTesselatedChunk(i * 32, 0, 0, vertices: 100, disposed: false));
            }
            ProcessPrioField.SetValue(manager, true);
            uploadDurationMs = 10.0;

            manager.OnBeforeFrame(0.016f);

            Assert.Single(uploadedChunks);
            Assert.Equal(2, prioQueue.Count);
            Assert.True((bool)ProcessPrioField.GetValue(manager));

            // Tier 3: 20 priority chunks (backlog > 16 -> 8 ms budget), 3.0 ms each -> 3 chunks (9 ms >= 8 ms)
            uploadedChunks.Clear();
            prioQueue.Clear();
            for (int i = 20; i < 40; i++)
            {
                prioQueue.Enqueue(CreateTesselatedChunk(i * 32, 0, 0, vertices: 100, disposed: false));
            }
            ProcessPrioField.SetValue(manager, true);
            uploadDurationMs = 3.0;

            manager.OnBeforeFrame(0.016f);

            Assert.Equal(3, uploadedChunks.Count);
            Assert.Equal(17, prioQueue.Count);
            Assert.True((bool)ProcessPrioField.GetValue(manager));
        }

        [Fact]
        public void NormalQueueSort_SkipsRedundantRecalcAndSortWhenCameraAndQueueUnchanged()
        {
            (ChunkTesselatorManager manager, ClientMain game, _, SortableQueue<TesselatedChunk> normalQueue) = CreateManagerHarness();
            for (int i = 0; i < 6; i++)
            {
                normalQueue.EnqueueOrMerge(CreateTesselatedChunk(i * 32, 0, 0, vertices: 500_000, disposed: false));
            }
            uploadDurationMs = 0.1;

            // Frame 1: initial sort runs
            manager.OnBeforeFrame(0.016f);
            Assert.True(recalcPriorityCalls > 0);
            Assert.Equal(1, sortCalls);
            Assert.Single(uploadedChunks);
            Assert.Equal(5, normalQueue.Count);

            // Frame 2: stationary camera and no new enqueues -> skip both RecalcPriority and Sort
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.Equal(0, recalcPriorityCalls);
            Assert.Equal(0, sortCalls);
            Assert.Equal(2, uploadedChunks.Count);
            Assert.Equal(4, normalQueue.Count);

            // Frame 3: yaw changes beyond rotation threshold -> re-sort
            game.player.CameraYaw += 0.1f;
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.True(recalcPriorityCalls > 0);
            Assert.Equal(1, sortCalls);
            Assert.Equal(3, uploadedChunks.Count);
            Assert.Equal(3, normalQueue.Count);

            // Frame 4: stationary again -> skip again
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.Equal(0, recalcPriorityCalls);
            Assert.Equal(0, sortCalls);
            Assert.Equal(4, uploadedChunks.Count);
            Assert.Equal(2, normalQueue.Count);

            // Frame 5: new chunk enqueued while stationary -> re-sort
            normalQueue.EnqueueOrMerge(CreateTesselatedChunk(99 * 32, 0, 0, vertices: 500_000, disposed: false));
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.True(recalcPriorityCalls > 0);
            Assert.Equal(1, sortCalls);
            Assert.Equal(5, uploadedChunks.Count);

            // Frame 6: player moves >= 0.5 blocks (sqDist >= 0.25) -> re-sort
            game.EntityPlayer.Pos.X += 1.0;
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.True(recalcPriorityCalls > 0);
            Assert.Equal(1, sortCalls);

            // Frame 7: pitch changes >= 0.05 rad -> re-sort
            normalQueue.EnqueueOrMerge(CreateTesselatedChunk(101 * 32, 0, 0, vertices: 500_000, disposed: false));
            normalQueue.EnqueueOrMerge(CreateTesselatedChunk(102 * 32, 0, 0, vertices: 500_000, disposed: false));
            normalQueue.EnqueueOrMerge(CreateTesselatedChunk(103 * 32, 0, 0, vertices: 500_000, disposed: false));
            manager.OnBeforeFrame(0.016f);
            game.player.CameraPitch += 0.08f;
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.True(recalcPriorityCalls > 0);
            Assert.Equal(1, sortCalls);

            // Cleanup resets cached sort state so the next frame re-sorts even while stationary
            ChunkUploadOptimization.Cleanup();
            recalcPriorityCalls = 0;
            sortCalls = 0;
            manager.OnBeforeFrame(0.016f);
            Assert.True(recalcPriorityCalls > 0);
            Assert.Equal(1, sortCalls);
        }

        [Fact]
        public void DisposedChunks_AreUnusedDisposedWithoutUploadingInBothQueues()
        {
            (ChunkTesselatorManager manager, _, Queue<TesselatedChunk> prioQueue, SortableQueue<TesselatedChunk> normalQueue) = CreateManagerHarness();
            uploadDurationMs = 0.1;

            TesselatedChunk prioDisposed = CreateTesselatedChunk(0, 0, 0, vertices: 10, disposed: true);
            TesselatedChunk prioLive = CreateTesselatedChunk(32, 0, 0, vertices: 10, disposed: false);
            TesselatedChunk normalDisposed = CreateTesselatedChunk(64, 0, 0, vertices: 10, disposed: true);
            TesselatedChunk normalLive = CreateTesselatedChunk(96, 0, 0, vertices: 10, disposed: false);

            prioQueue.Enqueue(prioDisposed);
            prioQueue.Enqueue(prioLive);
            ProcessPrioField.SetValue(manager, true);

            normalQueue.EnqueueOrMerge(normalDisposed);
            normalQueue.EnqueueOrMerge(normalLive);

            manager.OnBeforeFrame(0.016f);

            Assert.Equal(2, uploadedChunks.Count);
            Assert.DoesNotContain(uploadedChunks, c => ReferenceEquals(c, prioDisposed));
            Assert.DoesNotContain(uploadedChunks, c => ReferenceEquals(c, normalDisposed));
            Assert.Contains(uploadedChunks, c => ReferenceEquals(c, prioLive));
            Assert.Contains(uploadedChunks, c => ReferenceEquals(c, normalLive));
            Assert.Equal(2, unusedDisposedChunks.Count);
            Assert.Contains(unusedDisposedChunks, c => ReferenceEquals(c, prioDisposed));
            Assert.Contains(unusedDisposedChunks, c => ReferenceEquals(c, normalDisposed));
        }

        [Fact]
        public void InvokeReflectionSmokeScript_ChecksCurrentSignaturesAndChunkUploadPatch()
        {
            string scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "Invoke-ReflectionSmoke.ps1");
            string script = File.ReadAllText(scriptPath);

            Assert.Contains("Vintagestory.API.Common.IWorldAccessor", script, StringComparison.Ordinal);
            Assert.Contains("MatchesWithWorld_Prefix", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Matches_Prefix\"", script, StringComparison.Ordinal);
            Assert.Contains("ChunkTesselatorManager", script, StringComparison.Ordinal);
            Assert.Contains("ChunkUploadOptimization", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Expected OnBeforeFrame IL to contain ldc.i4.3", script, StringComparison.Ordinal);
        }

        private (ChunkTesselatorManager manager, ClientMain game, Queue<TesselatedChunk> prioQueue, SortableQueue<TesselatedChunk> normalQueue) CreateManagerHarness()
        {
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            game.WorldMap = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
            ClientChunkRendererField.SetValue(game, (ChunkRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ChunkRenderer)));
            game.frustumCuller = (FrustumCulling)RuntimeHelpers.GetUninitializedObject(typeof(FrustumCulling));
            game.frustumCuller.ViewDistanceSq = 0;
            DirtyPrioField.SetValue(game, new UniqueQueue<long>());
            DirtyField.SetValue(game, new UniqueQueue<long>());
            DirtyLastField.SetValue(game, new UniqueQueue<long>());

            var entityPlayer = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            AccessTools.Property(typeof(Entity), "Pos").SetValue(entityPlayer, new EntityPos(100.0, 64.0, 100.0, 0f, 0f, 0f));

            var worldData = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            AccessTools.Property(typeof(ClientWorldPlayerData), "EntityPlayer").SetValue(worldData, entityPlayer);

            var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            AccessTools.Field(typeof(ClientPlayer), "game").SetValue(player, game);
            AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, worldData);
            player.CameraYaw = 0.5f;
            player.CameraPitch = 0.25f;
            game.player = player;

            var camera = (PlayerCamera)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCamera));
            camera.CameraEyePos = new Vec3d(100.0, 65.7, 100.0);
            camera.Fov = 1.22f;
            camera.ZNear = 0.1f;
            camera.ZFar = 3000f;
            game.MainCamera = camera;

            var manager = (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
            var prioQueue = new Queue<TesselatedChunk>();
            var normalQueue = new SortableQueue<TesselatedChunk>();
            GameField.SetValue(manager, game);
            QueueLockField.SetValue(manager, new object());
            QueueField.SetValue(manager, normalQueue);
            PrioLockField.SetValue(manager, new object());
            PrioQueueField.SetValue(manager, prioQueue);
            TmpPosField.SetValue(manager, new Vec3i());
            SingleUploadDelayField.SetValue(manager, 0);
            ProcessPrioField.SetValue(manager, false);

            return (manager, game, prioQueue, normalQueue);
        }

        private TesselatedChunk CreateTesselatedChunk(int x, int y, int z, int vertices, bool disposed)
        {
            var chunk = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
            ChunkQueuedField.SetValue(chunk, true);
            if (WorldChunkDisposedField != null)
            {
                WorldChunkDisposedField.SetValue(chunk, disposed);
            }
            else if (WorldChunkDisposedProp?.CanWrite == true)
            {
                WorldChunkDisposedProp.SetValue(chunk, disposed);
            }
            else
            {
                AccessTools.Field(typeof(WorldChunk), "<Disposed>k__BackingField")?.SetValue(chunk, disposed);
            }

            currentChunkLookup[(x, y, z)] = chunk;

            var tc = new TesselatedChunk();
            TessPosXField.SetValue(tc, x);
            TessPosYField.SetValue(tc, y);
            TessPosZField.SetValue(tc, z);
            TessChunkField.SetValue(tc, chunk);
            TessVertsField.SetValue(tc, vertices);
            return tc;
        }

        private void InstallHarnessHooks()
        {
            MethodInfo getChunk = AccessTools.Method(typeof(ClientWorldMap), "GetChunkAtBlockPos", new[] { typeof(int), typeof(int), typeof(int) });
            MethodInfo addTess = AccessTools.Method(typeof(ChunkRenderer), "AddTesselatedChunk", new[] { typeof(TesselatedChunk), typeof(ClientChunk) });
            MethodInfo unusedDispose = AccessTools.Method(typeof(TesselatedChunk), "UnusedDispose");
            MethodInfo recalc = AccessTools.Method(typeof(TesselatedChunk), "RecalcPriority", new[] { typeof(ClientPlayer) });
            MethodInfo sort = AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), "Sort");

            harmony.Patch(getChunk, prefix: new HarmonyMethod(typeof(ChunkUploadOptimizationTests), nameof(GetChunkAtBlockPos_Prefix)));
            harmony.Patch(addTess, prefix: new HarmonyMethod(typeof(ChunkUploadOptimizationTests), nameof(AddTesselatedChunk_Prefix)));
            harmony.Patch(unusedDispose, prefix: new HarmonyMethod(typeof(ChunkUploadOptimizationTests), nameof(UnusedDispose_Prefix)));
            harmony.Patch(recalc, prefix: new HarmonyMethod(typeof(ChunkUploadOptimizationTests), nameof(RecalcPriority_Prefix)));
            harmony.Patch(sort, prefix: new HarmonyMethod(typeof(ChunkUploadOptimizationTests), nameof(Sort_Prefix)));
        }

        private void InstallOptimizationUnderTestIfPresent()
        {
            Type optType = AccessTools.TypeByName("OptiTime.ChunkUploadOptimization");
            if (optType == null)
            {
                return;
            }

            AccessTools.Method(optType, "Cleanup")?.Invoke(null, null);
            FieldInfo tsField = AccessTools.Field(optType, "timestampProvider");
            tsField?.SetValue(null, new Func<long>(() => syntheticTicks));

            MethodInfo onBeforeFrame = AccessTools.Method(typeof(ChunkTesselatorManager), "OnBeforeFrame", new[] { typeof(float) });
            MethodInfo prefix = AccessTools.Method(optType, "OnBeforeFrame_Prefix");
            if (onBeforeFrame != null && prefix != null)
            {
                harmony.Patch(onBeforeFrame, prefix: new HarmonyMethod(prefix));
            }
        }

        private static void RestoreOptimizationState()
        {
            Type optType = AccessTools.TypeByName("OptiTime.ChunkUploadOptimization");
            if (optType == null)
            {
                return;
            }

            AccessTools.Method(optType, "Cleanup")?.Invoke(null, null);
            FieldInfo tsField = AccessTools.Field(optType, "timestampProvider");
            tsField?.SetValue(null, new Func<long>(Stopwatch.GetTimestamp));
        }

        private static void ResetHookState()
        {
            currentChunkLookup = new Dictionary<(int, int, int), ClientChunk>();
            uploadedChunks = new List<TesselatedChunk>();
            unusedDisposedChunks = new List<TesselatedChunk>();
            syntheticTicks = Stopwatch.Frequency * 10L;
            uploadDurationMs = 0.0;
            recalcPriorityCalls = 0;
            sortCalls = 0;
        }

        private static bool GetChunkAtBlockPos_Prefix(int posX, int posY, int posZ, ref ClientChunk __result)
        {
            currentChunkLookup.TryGetValue((posX, posY, posZ), out __result);
            return false;
        }

        private static bool AddTesselatedChunk_Prefix(TesselatedChunk tesschunk)
        {
            uploadedChunks.Add(tesschunk);
            syntheticTicks += (long)Math.Round(uploadDurationMs * Stopwatch.Frequency / 1000.0);
            return false;
        }

        private static bool UnusedDispose_Prefix(TesselatedChunk __instance)
        {
            unusedDisposedChunks.Add(__instance);
            return false;
        }

        private static bool RecalcPriority_Prefix()
        {
            recalcPriorityCalls++;
            return true;
        }

        private static bool Sort_Prefix()
        {
            sortCalls++;
            return true;
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OptiTime.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate OptiTime repository root.");
        }

        private static string FindGameDirectory()
        {
            string env = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "VintagestoryLib.dll")))
            {
                return env;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Path.Combine(home, ".local", "share", "vintagestory");
            return File.Exists(Path.Combine(local, "VintagestoryLib.dll")) ? local : null;
        }
    }
}
