using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class OcclusionCullingOptimizationTests : IDisposable
    {
        private const string TestHarmonyId = "com.zaldaryon.optitime.test.occlusion";

        private static readonly FieldInfo CullerGameField = AccessTools.Field(typeof(ChunkCuller), "game");
        private static readonly FieldInfo CullerShellField = AccessTools.Field(typeof(ChunkCuller), "cubicShellPositions");
        private static readonly FieldInfo CullerCenterPosField = AccessTools.Field(typeof(ChunkCuller), "centerpos");
        private static readonly FieldInfo CullerCurPosField = AccessTools.Field(typeof(ChunkCuller), "curpos");
        private static readonly FieldInfo CullerToPosField = AccessTools.Field(typeof(ChunkCuller), "toPos");
        private static readonly FieldInfo CullerPlanePosField = AccessTools.Field(typeof(ChunkCuller), "planePosition");
        private static readonly FieldInfo CullerRayField = AccessTools.Field(typeof(ChunkCuller), "ray");
        private static readonly FieldInfo CullerQCountField = AccessTools.Field(typeof(ChunkCuller), "qCount");
        private static readonly FieldInfo CullerNowOffField = AccessTools.Field(typeof(ChunkCuller), "nowOff");

        private static readonly FieldInfo ClientChunkRendererField = AccessTools.Field(typeof(ClientMain), "chunkRenderer");
        private static readonly FieldInfo WorldMapChunksField = AccessTools.Field(typeof(ClientWorldMap), "chunks");
        private static readonly FieldInfo WorldMapChunksLockField = AccessTools.Field(typeof(ClientWorldMap), "chunksLock");
        private static readonly FieldInfo WorldMapSizeField = AccessTools.Field(typeof(ClientWorldMap), "mapsize");
        private static readonly FieldInfo WorldMapChunkSizeYField = AccessTools.Field(typeof(WorldMap), "chunkMapSizeY");

        private static readonly (int A, int B, int Bit)[] VanillaPairs =
        {
            (0, 1, 0), (0, 3, 1), (0, 2, 2), (0, 4, 3), (0, 5, 4),
            (1, 2, 5), (1, 3, 6), (1, 4, 7), (1, 5, 8),
            (2, 3, 9), (2, 4, 10), (2, 5, 11),
            (3, 4, 12), (3, 5, 13), (4, 5, 14),
        };

        private static int raycastCalls;
        private static long syntheticMs;
        private static int swapBufferCalls;

        private readonly Harmony harmony;
        private readonly bool? previousOcclusionSetting;
        private readonly int? previousViewDistance;
        private readonly int previousBufIndex;

        static OcclusionCullingOptimizationTests()
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

        public OcclusionCullingOptimizationTests()
        {
            harmony = new Harmony(TestHarmonyId);
            raycastCalls = 0;
            swapBufferCalls = 0;
            syntheticMs = 10_000;
            previousBufIndex = ClientChunk.bufIndex;

            try
            {
                previousOcclusionSetting = ClientSettings.Occlusionculling;
                previousViewDistance = ClientSettings.ViewDistance;
            }
            catch
            {
                previousOcclusionSetting = null;
                previousViewDistance = null;
            }

            InstallHarnessHooks();
            InstallOptimizationUnderTestIfPresent();
        }

        public void Dispose()
        {
            RestoreOptimizationState();
            harmony.UnpatchAll(TestHarmonyId);
            ClientChunk.bufIndex = previousBufIndex;

            if (previousOcclusionSetting.HasValue)
            {
                try
                {
                    ClientSettings.Inst.Bool["occlusionculling"] = previousOcclusionSetting.Value;
                }
                catch
                {
                }
            }

            if (previousViewDistance.HasValue)
            {
                try
                {
                    ClientSettings.Inst.Int["viewDistance"] = previousViewDistance.Value;
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void CullInvisibleChunks_ReplacesShellRaycastWithFlatGridBfsAndRespects150MsCooldown()
        {
            ClientSettings.Inst.Bool["occlusionculling"] = true;
            ClientSettings.Inst.Int["viewDistance"] = 128;
            OcclusionCullingOptimization.UpdateViewDistance(128);

            (ChunkCuller culler, ClientMain game, Dictionary<long, ClientChunk> chunks) = CreateCullerHarness(mapX: 12, mapY: 4, mapZ: 12);

            // Populate an open 8x4x8 region around (4, 1, 4) with a full-height solid wall at z = 6
            // and a chunk behind the wall at (4, 1, 7), plus an extra-dimension chunk (dim = 2).
            for (int x = 1; x <= 8; x++)
            {
                for (int y = 0; y < 4; y++)
                {
                    for (int z = 1; z <= 8; z++)
                    {
                        bool isWall = z == 6;
                        ClientChunk chunk = CreateClientChunk(isWall ? (ushort)0 : (ushort)0x7FFF, fresh: true);
                        chunks[game.WorldMap.ChunkIndex3D(x, y, z)] = chunk;
                    }
                }
            }

            // Extra dimension (dim = 2) chunk must stay visible when player is in dim 0.
            long extraDimKey = game.WorldMap.ChunkIndex3D(4, 1, 4, dim: 2);
            ClientChunk extraDimChunk = CreateClientChunk(0, fresh: true);
            chunks[extraDimKey] = extraDimChunk;

            game.player.Entity.CameraPos.Set(4 * 32 + 16, 1 * 32 + 16, 4 * 32 + 16);
            game.player.Entity.Pos.SetPos(4 * 32 + 16, 1 * 32 + 16, 4 * 32 + 16);
            game.player.Entity.Pos.Dimension = 0;

            // First pass: camera moved from default (0,0,0) -> (4,1,4), runs BFS immediately
            culler.CullInvisibleChunks();

            Assert.Equal(0, raycastCalls);
            Assert.Equal(1, swapBufferCalls);
            Assert.True(chunks[game.WorldMap.ChunkIndex3D(4, 1, 5)].CullVisible[ClientChunk.bufIndex]);
            Assert.True(chunks[game.WorldMap.ChunkIndex3D(4, 1, 6)].CullVisible[ClientChunk.bufIndex]);
            Assert.False(chunks[game.WorldMap.ChunkIndex3D(4, 1, 7)].CullVisible[ClientChunk.bufIndex]);
            Assert.True(extraDimChunk.CullVisible[ClientChunk.bufIndex]);

            // Same chunk + significant regen queue delta (>= 10), within 150 ms cooldown -> skipped
            for (int i = 0; i < 15; i++)
            {
                game.chunkPositionsForRegenTrav.Add(new ChunkPos(i, 0, 0, 0));
            }
            syntheticMs += 50;
            culler.CullInvisibleChunks();
            Assert.Equal(1, swapBufferCalls);

            // Once 150 ms cooldown elapses -> recalculates once
            syntheticMs += 110;
            culler.CullInvisibleChunks();
            Assert.Equal(2, swapBufferCalls);
            Assert.Equal(0, raycastCalls);

            // Moving camera to a different chunk bypasses cooldown immediately
            game.player.Entity.CameraPos.Set(5 * 32 + 16, 1 * 32 + 16, 4 * 32 + 16);
            syntheticMs += 10;
            culler.CullInvisibleChunks();
            Assert.Equal(3, swapBufferCalls);
            Assert.Equal(0, raycastCalls);
        }

        [Fact]
        public void VisibilityParity_CavesAndRandomWorlds_MarksSupersetOfVanillaShellRays()
        {
            Type gridType = AccessTools.TypeByName("OptiTime.ChunkVisibilityGrid");
            Assert.NotNull(gridType);

            // 1. Sealed cave pocket vs open tunnel: tunnel lets visibility through, sealed cave blocks behind wall.
            var caveWorld = new TestWorld { MapX = 10, MapY = 3, MapZ = 10 };
            for (int x = 0; x < 10; x++)
            {
                for (int y = 0; y < 3; y++)
                {
                    for (int z = 0; z < 10; z++)
                    {
                        bool wall = z == 6 && !(x == 5 && y == 1);
                        caveWorld.Chunks[(x, y, z)] = InvokeEncodeCell(gridType, wall ? (ushort)0 : (ushort)0x7FFF, fresh: true);
                    }
                }
            }

            // Tunnel at (5, 1, 6) only connects N <-> S (bit 2).
            caveWorld.Chunks[(5, 1, 6)] = InvokeEncodeCell(gridType, 1 << 2, fresh: true);
            HashSet<(int, int, int)> caveVisible = RunGridModel(gridType, caveWorld, (5, 1, 4), viewDistance: 128, occlusion: true);
            Assert.Contains((5, 1, 6), caveVisible);
            Assert.Contains((5, 1, 7), caveVisible);

            // Seal the tunnel: (5, 1, 7) must now be occluded.
            caveWorld.Chunks[(5, 1, 6)] = InvokeEncodeCell(gridType, 0, fresh: true);
            HashSet<(int, int, int)> sealedVisible = RunGridModel(gridType, caveWorld, (5, 1, 4), viewDistance: 128, occlusion: true);
            Assert.Contains((5, 1, 6), sealedVisible);
            Assert.DoesNotContain((5, 1, 7), sealedVisible);

            // 2. Random worlds (including cameras above world height): BFS marks a superset of vanilla rays.
            foreach (int seed in new[] { 21, 22, 23 })
            {
                TestWorld w = CreateRandomWorld(gridType, seed, 24, 5, 24, loadedChance: 0.96, passChance: 0.92, staleChance: 0.05);
                var rng = new Random(seed * 31);
                for (int trial = 0; trial < 4; trial++)
                {
                    int camY = trial == 3 ? w.MapY + 2 : rng.Next(0, w.MapY);
                    var cam = (rng.Next(0, w.MapX), camY, rng.Next(0, w.MapZ));
                    HashSet<(int, int, int)> rays = RunVanillaRayModel(w, cam, viewDistance: 160);
                    Assert.True(rays.Count > 100);
                    HashSet<(int, int, int)> bfs = RunGridModel(gridType, w, cam, viewDistance: 160, occlusion: true);

                    foreach (var pos in rays)
                    {
                        Assert.True(bfs.Contains(pos), $"seed {seed} trial {trial} cam {cam}: ray-visible {pos} missing from BFS");
                    }
                }
            }
        }

        [Fact]
        public void VisibilityParity_AboveWorldAndExtraDimensions_PreserveExactBounds()
        {
            Type gridType = AccessTools.TypeByName("OptiTime.ChunkVisibilityGrid");
            Assert.NotNull(gridType);

            // Above-world camera reaches terrain below and stops cleanly when very high.
            TestWorld open = CreateRandomWorld(gridType, 31, 16, 4, 16, loadedChance: 1.0, passChance: 1.0, staleChance: 0.0);
            HashSet<(int, int, int)> aboveVisible = RunGridModel(gridType, open, (8, open.MapY + 3, 8), viewDistance: 128, occlusion: true);
            Assert.Contains((8, open.MapY - 1, 8), aboveVisible);

            HashSet<(int, int, int)> farAboveVisible = RunGridModel(gridType, open, (8, open.MapY + 12, 8), viewDistance: 128, occlusion: true);
            Assert.DoesNotContain((8, open.MapY - 1, 8), farAboveVisible);

            // ResolveCameraChunkY handles normal, below-floor, above-height, and torn reads across dimensions.
            MethodInfo resolveY = AccessTools.Method(gridType, "ResolveCameraChunkY", new[] { typeof(double), typeof(int) });
            Assert.NotNull(resolveY);
            Assert.Equal(4, (int)resolveY.Invoke(null, new object[] { 128.5, 0 }));
            Assert.Equal(-2, (int)resolveY.Invoke(null, new object[] { -64.0, 0 }));
            Assert.Equal(2052, (int)resolveY.Invoke(null, new object[] { 2 * 32768 + 128.5, 2 }));
            Assert.Equal(2052, (int)resolveY.Invoke(null, new object[] { 128.5, 2 }));
            Assert.Equal(4, (int)resolveY.Invoke(null, new object[] { 2 * 32768 + 128.5, 0 }));

            // Inside extra dimension (dim = 2), passing dimension-local camY enforces floor bounds and blocks behind full-height wall.
            var dimWorld = new TestWorld { MapX = 12, MapY = 8, MapZ = 12 };
            for (int x = 0; x < 12; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int z = 0; z < 12; z++)
                    {
                        bool wall = x == 6;
                        dimWorld.Chunks[(x, y, z)] = InvokeEncodeCell(gridType, wall ? (ushort)0 : (ushort)0x7FFF, fresh: true);
                    }
                }
            }

            int camInternalY = (int)resolveY.Invoke(null, new object[] { 2 * 32768 + 4 * 32 + 16, 2 });
            int localCamY = camInternalY - 2 * 1024;
            HashSet<(int, int, int)> dimVisible = RunGridModel(gridType, dimWorld, (4, localCamY, 5), viewDistance: 192, occlusion: true);
            Assert.Contains((4, 4, 5), dimVisible);
            Assert.Contains((6, 4, 5), dimVisible);
            Assert.DoesNotContain((8, 4, 5), dimVisible);
        }

        [Fact]
        public void FlatGridBfs_ZeroSteadyStateAllocationsAndOneEnqueuePerCell()
        {
            Type gridType = AccessTools.TypeByName("OptiTime.ChunkVisibilityGrid");
            Assert.NotNull(gridType);

            object grid = Activator.CreateInstance(gridType);
            MethodInfo prepare = AccessTools.Method(gridType, "Prepare", new[] { typeof(int), typeof(int) });
            MethodInfo setCell = AccessTools.Method(gridType, "SetCell", new[] { typeof(int), typeof(ushort) });
            MethodInfo indexOf = AccessTools.Method(gridType, "IndexOf", new[] { typeof(int), typeof(int), typeof(int) });
            MethodInfo run = AccessTools.Method(gridType, "Run", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool) });
            MethodInfo reachedCell = AccessTools.Method(gridType, "ReachedCell", new[] { typeof(int) });
            PropertyInfo queueLengthProp = AccessTools.Property(gridType, "QueueLength");
            PropertyInfo cellCountProp = AccessTools.Property(gridType, "CellCount");

            // Warm up initial allocation
            prepare.Invoke(grid, new object[] { 8, 6 });
            int centerIdx = (int)indexOf.Invoke(grid, new object[] { 8, 6, 8 });
            setCell.Invoke(grid, new object[] { centerIdx, InvokeEncodeCell(gridType, 0x7FFF, true) });
            run.Invoke(grid, new object[] { 12, 2, 12, 24, 4, 24, true });

            // Steady-state pass with same size must allocate 0 bytes and enqueue every cell at most once
            Action<int, int> prepareFast = (Action<int, int>)Delegate.CreateDelegate(typeof(Action<int, int>), grid, prepare);
            Action<int, int, int, int, int, int, bool> runFast = (Action<int, int, int, int, int, int, bool>)Delegate.CreateDelegate(typeof(Action<int, int, int, int, int, int, bool>), grid, run);

            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            prepareFast(8, 6);
            runFast(12, 2, 12, 24, 4, 24, true);
            long afterBytes = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, afterBytes - beforeBytes);

            int qLen = (int)queueLengthProp.GetValue(grid);
            int cellCount = (int)cellCountProp.GetValue(grid);
            Assert.True(qLen > 0 && qLen <= cellCount);

            var seen = new HashSet<int>();
            for (int i = 0; i < qLen; i++)
            {
                int cell = (int)reachedCell.Invoke(grid, new object[] { i });
                Assert.True(seen.Add(cell));
            }
        }

        private (ChunkCuller culler, ClientMain game, Dictionary<long, ClientChunk> chunks) CreateCullerHarness(int mapX, int mapY, int mapZ)
        {
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            var worldMap = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
            var chunks = new Dictionary<long, ClientChunk>();
            WorldMapChunksField.SetValue(worldMap, chunks);
            WorldMapChunksLockField.SetValue(worldMap, new object());
            WorldMapSizeField.SetValue(worldMap, new Vec3i(mapX * 32, mapY * 32, mapZ * 32));
            WorldMapChunkSizeYField.SetValue(worldMap, mapY);
            worldMap.index3dMulX = 2097152;
            worldMap.index3dMulZ = 2097152;
            game.WorldMap = worldMap;

            var renderer = (ChunkRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ChunkRenderer));
            ClientChunkRendererField.SetValue(game, renderer);
            game.chunkPositionsForRegenTrav = new HashSet<ChunkPos>();

            var entityPlayer = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            AccessTools.Property(typeof(Entity), "Pos").SetValue(entityPlayer, new EntityPos(0, 0, 0, 0f, 0f, 0f));
            entityPlayer.CameraPos = new Vec3d(0, 0, 0);

            var worldData = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            AccessTools.Property(typeof(ClientWorldPlayerData), "EntityPlayer").SetValue(worldData, entityPlayer);

            var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            AccessTools.Field(typeof(ClientPlayer), "game").SetValue(player, game);
            AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, worldData);
            game.player = player;

            var culler = (ChunkCuller)RuntimeHelpers.GetUninitializedObject(typeof(ChunkCuller));
            CullerGameField.SetValue(culler, game);
            CullerShellField.SetValue(culler, new[] { new Vec3i(1, 0, 0), new Vec3i(-1, 0, 0), new Vec3i(0, 0, 1), new Vec3i(0, 0, -1) });
            CullerCenterPosField.SetValue(culler, new Vec3i());
            CullerCurPosField.SetValue(culler, new Vec3i());
            CullerToPosField.SetValue(culler, new Vec3i());
            CullerPlanePosField.SetValue(culler, new Vec3d());
            CullerRayField.SetValue(culler, new Ray());
            CullerQCountField.SetValue(culler, 0);
            CullerNowOffField.SetValue(culler, false);

            return (culler, game, chunks);
        }

        private static ClientChunk CreateClientChunk(ushort traversability, bool fresh)
        {
            var chunk = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
            chunk.traversability = traversability;
            chunk.traversabilityFresh = fresh;
            chunk.CullVisible = new Bools(a: true, b: true);
            return chunk;
        }

        private void InstallHarnessHooks()
        {
            MethodInfo traverseRay = AccessTools.Method(typeof(ChunkCuller), "traverseRayAndMarkVisible");
            MethodInfo swapBuffers = AccessTools.Method(typeof(ChunkRenderer), "SwapVisibleBuffers");
            harmony.Patch(traverseRay, prefix: new HarmonyMethod(typeof(OcclusionCullingOptimizationTests), nameof(TraverseRay_Prefix)));
            harmony.Patch(swapBuffers, prefix: new HarmonyMethod(typeof(OcclusionCullingOptimizationTests), nameof(SwapVisibleBuffers_Prefix)));
        }

        private void InstallOptimizationUnderTestIfPresent()
        {
            MethodInfo prefix = AccessTools.Method(typeof(OcclusionCullingOptimization), "CullInvisibleChunks_Prefix");
            if (prefix == null)
            {
                return;
            }

            OcclusionCullingOptimization.Cleanup();
            AccessTools.Method(typeof(OcclusionCullingOptimization), "Configure", new[] { typeof(bool) })?.Invoke(null, new object[] { true });
            AccessTools.Field(typeof(OcclusionCullingOptimization), "tickCountMsProvider")?.SetValue(null, new Func<long>(() => syntheticMs));

            MethodInfo target = AccessTools.Method(typeof(ChunkCuller), "CullInvisibleChunks", Type.EmptyTypes);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        private static void RestoreOptimizationState()
        {
            OcclusionCullingOptimization.Cleanup();
            AccessTools.Field(typeof(OcclusionCullingOptimization), "tickCountMsProvider")?.SetValue(null, new Func<long>(() => Environment.TickCount64));
            AccessTools.Method(typeof(OcclusionCullingOptimization), "Configure", new[] { typeof(bool) })?.Invoke(null, new object[] { true });
        }

        private static bool TraverseRay_Prefix()
        {
            raycastCalls++;
            return true;
        }

        private static bool SwapVisibleBuffers_Prefix()
        {
            swapBufferCalls++;
            ClientChunk.bufIndex = (ClientChunk.bufIndex + 1) % 2;
            return false;
        }

        private sealed class TestWorld
        {
            public int MapX;
            public int MapY;
            public int MapZ;
            public readonly Dictionary<(int X, int Y, int Z), ushort> Chunks = new Dictionary<(int, int, int), ushort>();
        }

        private static ushort InvokeEncodeCell(Type gridType, ushort traversability, bool fresh)
        {
            MethodInfo encode = AccessTools.Method(gridType, "EncodeCell", new[] { typeof(ushort), typeof(bool) });
            return (ushort)encode.Invoke(null, new object[] { traversability, fresh });
        }

        private static TestWorld CreateRandomWorld(Type gridType, int seed, int mapX, int mapY, int mapZ, double loadedChance, double passChance, double staleChance)
        {
            var rng = new Random(seed);
            var w = new TestWorld { MapX = mapX, MapY = mapY, MapZ = mapZ };
            for (int x = 0; x < mapX; x++)
            {
                for (int y = 0; y < mapY; y++)
                {
                    for (int z = 0; z < mapZ; z++)
                    {
                        if (rng.NextDouble() >= loadedChance)
                        {
                            continue;
                        }

                        ushort mask = 0;
                        for (int bit = 0; bit < 15; bit++)
                        {
                            if (rng.NextDouble() < passChance)
                            {
                                mask |= (ushort)(1 << bit);
                            }
                        }

                        bool fresh = rng.NextDouble() >= staleChance;
                        w.Chunks[(x, y, z)] = InvokeEncodeCell(gridType, mask, fresh);
                    }
                }
            }

            return w;
        }

        private static HashSet<(int, int, int)> RunGridModel(Type gridType, TestWorld w, (int X, int Y, int Z) cam, int viewDistance, bool occlusion)
        {
            int r = viewDistance / 32 + 4;
            int ry = w.MapY + 2;
            object grid = Activator.CreateInstance(gridType);
            AccessTools.Method(gridType, "Prepare", new[] { typeof(int), typeof(int) }).Invoke(grid, new object[] { r, ry });
            int width = (int)AccessTools.Property(gridType, "Width").GetValue(grid);
            int height = (int)AccessTools.Property(gridType, "Height").GetValue(grid);
            MethodInfo indexOf = AccessTools.Method(gridType, "IndexOf", new[] { typeof(int), typeof(int), typeof(int) });
            MethodInfo setCell = AccessTools.Method(gridType, "SetCell", new[] { typeof(int), typeof(ushort) });

            foreach (var kv in w.Chunks)
            {
                int lx = kv.Key.X - cam.X + r;
                int ly = kv.Key.Y - cam.Y + ry;
                int lz = kv.Key.Z - cam.Z + r;
                if ((uint)lx < (uint)width && (uint)lz < (uint)width && (uint)ly < (uint)height)
                {
                    int idx = (int)indexOf.Invoke(grid, new object[] { lx, ly, lz });
                    setCell.Invoke(grid, new object[] { idx, kv.Value });
                }
            }

            AccessTools.Method(gridType, "Run", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool) })
                .Invoke(grid, new object[] { cam.X, cam.Y, cam.Z, w.MapX, w.MapY, w.MapZ, occlusion });

            int qLen = (int)AccessTools.Property(gridType, "QueueLength").GetValue(grid);
            MethodInfo reachedCell = AccessTools.Method(gridType, "ReachedCell", new[] { typeof(int) });
            var visible = new HashSet<(int, int, int)>();
            for (int i = 0; i < qLen; i++)
            {
                int idx = (int)reachedCell.Invoke(grid, new object[] { i });
                int lx = idx % width;
                int rest = idx / width;
                int lz = rest % width;
                int ly = rest / width;
                var pos = (cam.X + lx - r, cam.Y + ly - ry, cam.Z + lz - r);
                if (w.Chunks.ContainsKey(pos))
                {
                    visible.Add(pos);
                }
            }

            return visible;
        }

        private static HashSet<(int, int, int)> RunVanillaRayModel(TestWorld w, (int X, int Y, int Z) cam, int viewDistance)
        {
            int[] pairBit = new int[36];
            Array.Fill(pairBit, -1);
            foreach (var (a, b, bit) in VanillaPairs)
            {
                pairBit[a * 6 + b] = bit;
                pairBit[b * 6 + a] = bit;
            }

            int mapY = w.MapY;
            bool above = cam.Y >= mapY;
            int shellRadius = viewDistance / 32 + 1;

            var shell = new HashSet<(int, int, int)>();
            foreach (Vec2i p in ShapeUtil.GetOctagonPoints(0, 0, shellRadius))
            {
                for (int j = -mapY; j <= mapY; j++)
                {
                    shell.Add((p.X, j, p.Y));
                }
            }

            for (int k = 0; k < shellRadius; k++)
            {
                foreach (Vec2i p in ShapeUtil.GetOctagonPoints(0, 0, k))
                {
                    shell.Add((p.X, -mapY, p.Y));
                    shell.Add((p.X, mapY, p.Y));
                }
            }

            var visible = new HashSet<(int, int, int)>();
            foreach (var rel in shell)
            {
                TraceVanillaRay(w, pairBit, cam, rel, 0.25, 0.5, above, visible);
                TraceVanillaRay(w, pairBit, cam, rel, 0.75, 0.5, above, visible);
                TraceVanillaRay(w, pairBit, cam, rel, 0.75, 0.0, above, visible);
            }

            return visible;
        }

        private static void TraceVanillaRay(TestWorld w, int[] pairBit, (int X, int Y, int Z) from, (int X, int Y, int Z) rel,
            double yoff, double xoff, bool above, HashSet<(int, int, int)> visible)
        {
            double ox = from.X + xoff, oy = from.Y + yoff, oz = from.Z + 0.5;
            double dx = rel.X + xoff, dy = rel.Y + yoff, dz = rel.Z + 0.5;
            var to = (X: from.X + rel.X, Y: from.Y + rel.Y, Z: from.Z + rel.Z);
            int total = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) + Math.Abs(to.Z - from.Z);
            var cur = from;
            int entry = -1;
            int[] fdx = { 0, 1, 0, -1, 0, 0 };
            int[] fdy = { 0, 0, 0, 0, 1, -1 };
            int[] fdz = { -1, 0, 1, 0, 0, 0 };
            int[] opp = { 2, 3, 0, 1, 5, 4 };

            int dist;
            while ((dist = Math.Abs(cur.X - from.X) + Math.Abs(cur.Y - from.Y) + Math.Abs(cur.Z - from.Z)) <= total + 2)
            {
                int exit = FindExitingFace(cur, ox, oy, oz, dx, dy, dz);
                if (exit < 0)
                {
                    break;
                }

                if (w.Chunks.TryGetValue(cur, out ushort cell))
                {
                    visible.Add(cur);
                    if (dist > 1 && entry >= 0 && cell != 0xFFFF && ((cell >> pairBit[entry * 6 + exit]) & 1) == 0)
                    {
                        break;
                    }
                }

                cur = (cur.X + fdx[exit], cur.Y + fdy[exit], cur.Z + fdz[exit]);
                entry = opp[exit];
                bool valid = cur.X >= 0 && cur.Y >= 0 && cur.Z >= 0 && cur.X < w.MapX && cur.Y < w.MapY && cur.Z < w.MapZ;
                if (!valid && (!above || cur.Y <= 0))
                {
                    break;
                }
            }
        }

        private static int FindExitingFace((int X, int Y, int Z) pos, double ox, double oy, double oz, double dx, double dy, double dz)
        {
            int[] nx = { 0, 1, 0, -1, 0, 0 };
            int[] ny = { 0, 0, 0, 0, 1, -1 };
            int[] nz = { -1, 0, 1, 0, 0, 0 };
            for (int i = 0; i < 6; i++)
            {
                double dot = nx[i] * dx + ny[i] * dy + nz[i] * dz;
                if (dot <= 1E-05)
                {
                    continue;
                }

                Vec3f center = BlockFacing.ALLFACES[i].PlaneCenter;
                double px = pos.X + center.X, py = pos.Y + center.Y, pz = pos.Z + center.Z;
                double t = ((px - ox) * nx[i] + (py - oy) * ny[i] + (pz - oz) * nz[i]) / dot;
                if (t >= 0.0 && Math.Abs(ox + dx * t - px) <= 0.5 && Math.Abs(oy + dy * t - py) <= 0.5 && Math.Abs(oz + dz * t - pz) <= 0.5)
                {
                    return i;
                }
            }

            return -1;
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
