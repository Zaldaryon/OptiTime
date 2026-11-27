using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class DynamicLightOptimizationTests : IDisposable
    {
        private const string TestHarmonyId = "com.zaldaryon.optitime.test.dynlights";

        private static readonly FieldInfo SystemGameField = AccessTools.Field(typeof(ClientSystem), "game");
        private static readonly FieldInfo MaxDynLightsField = AccessTools.Field(typeof(SystemRenderPlayerEffects), "maxDynLights");
        private static readonly FieldInfo InvalField = AccessTools.Field(typeof(SystemRenderPlayerEffects), "inval");
        private static readonly FieldInfo OutvalField = AccessTools.Field(typeof(SystemRenderPlayerEffects), "outval");
        private static readonly FieldInfo Outval3Field = AccessTools.Field(typeof(SystemRenderPlayerEffects), "outval3");

        private static readonly FieldInfo PointLightsField = AccessTools.Field(typeof(ClientMain), "pointlights");
        private static readonly FieldInfo WorldMapChunksField = AccessTools.Field(typeof(ClientWorldMap), "chunks");
        private static readonly FieldInfo WorldMapChunksLockField = AccessTools.Field(typeof(ClientWorldMap), "chunksLock");
        private static readonly FieldInfo WorldMapSizeField = AccessTools.Field(typeof(ClientWorldMap), "mapsize");
        private static readonly FieldInfo WorldMapChunkSizeYField = AccessTools.Field(typeof(WorldMap), "chunkMapSizeY");
        private static readonly FieldInfo WorldMapHueLevelsField = AccessTools.Field(typeof(ClientWorldMap), "hueLevels");
        private static readonly FieldInfo WorldMapSatLevelsField = AccessTools.Field(typeof(ClientWorldMap), "satLevels");
        private static readonly FieldInfo WorldMapBlockLightLevelsField = AccessTools.Field(typeof(WorldMap), "BlockLightLevels");

        private static int getEntitiesAroundCalls;
        private static int perceptionRenderCalls;

        private readonly Harmony harmony;

        static DynamicLightOptimizationTests()
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

        public DynamicLightOptimizationTests()
        {
            harmony = new Harmony(TestHarmonyId);
            getEntitiesAroundCalls = 0;
            perceptionRenderCalls = 0;
            InvokeCleanupIfPresent();
            DynamicLightOptimization.UpdateViewDistance(256);

            MethodInfo getEntitiesAround = AccessTools.Method(typeof(GameMain), "GetEntitiesAround",
                new[] { typeof(Vec3d), typeof(float), typeof(float), typeof(ActionConsumable<Entity>) });
            MethodInfo perceptionOnBefore = AccessTools.Method(typeof(PerceptionEffects), "OnBeforeGameRender",
                new[] { typeof(float) });

            harmony.Patch(getEntitiesAround, prefix: new HarmonyMethod(typeof(DynamicLightOptimizationTests), nameof(GetEntitiesAround_Prefix)));
            if (perceptionOnBefore != null)
            {
                harmony.Patch(perceptionOnBefore, prefix: new HarmonyMethod(typeof(DynamicLightOptimizationTests), nameof(PerceptionOnBefore_Prefix)));
            }
        }

        public void Dispose()
        {
            InvokeCleanupIfPresent();
            harmony.UnpatchAll(TestHarmonyId);
            DynamicLightOptimization.UpdateViewDistance(256);
        }

        [Fact]
        public void OptimizeLightCulling_NeverCallsGetEntitiesAroundAndAllocatesZeroBytesInSteadyState()
        {
            (SystemRenderPlayerEffects system, ClientMain game, Dictionary<long, ClientChunk> chunks) = CreateHarness(maxDynLights: 4);
            AddEntityToChunk(game, chunks, CreateLightEntity(105, 64, 100, lightLevel: 14));
            AddEntityToChunk(game, chunks, CreateLightEntity(112, 64, 100, lightLevel: 12));
            AddEntityToChunk(game, chunks, CreateLightEntity(120, 64, 100, lightLevel: 0));

            // Warm up initial scratch buffers
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(0, getEntitiesAroundCalls);
            Assert.Equal(2, game.shUniforms.PointLightsCount);

            // Stationary steady state: 0 bytes allocated across 10 cached frames
            long beforeStationary = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
            {
                DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            }
            long afterStationary = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, getEntitiesAroundCalls);
            Assert.Equal(0L, afterStationary - beforeStationary);
            Assert.Equal(2, game.shUniforms.PointLightsCount);

            // Moving steady state (triggers chunk scan every frame): 0 bytes allocated
            long beforeMoving = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++)
            {
                game.EntityPlayer.Pos.X += 1.0;
                DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            }
            long afterMoving = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, getEntitiesAroundCalls);
            Assert.Equal(0L, afterMoving - beforeMoving);
        }

        [Fact]
        public void OptimizeLightCulling_SelectsTopKNearestAndReprojectsViewSpaceEveryFrame()
        {
            (SystemRenderPlayerEffects system, ClientMain game, Dictionary<long, ClientChunk> chunks) = CreateHarness(maxDynLights: 3);

            // Place 5 lit entities at distances 5, 10, 15, 25, 35 plus 1 despawned and 1 unlit
            LightTestEntity e1 = CreateLightEntity(105, 64, 100, lightLevel: 15);
            LightTestEntity e2 = CreateLightEntity(110, 64, 100, lightLevel: 14);
            LightTestEntity e3 = CreateLightEntity(115, 64, 100, lightLevel: 13);
            LightTestEntity e4 = CreateLightEntity(125, 64, 100, lightLevel: 12);
            LightTestEntity e5 = CreateLightEntity(135, 64, 100, lightLevel: 11);
            LightTestEntity despawned = CreateLightEntity(101, 64, 100, lightLevel: 15);
            despawned.State = EnumEntityState.Despawned;
            LightTestEntity unlit = CreateLightEntity(102, 64, 100, lightLevel: 0);

            AddEntityToChunk(game, chunks, e5);
            AddEntityToChunk(game, chunks, despawned);
            AddEntityToChunk(game, chunks, e3);
            AddEntityToChunk(game, chunks, unlit);
            AddEntityToChunk(game, chunks, e1);
            AddEntityToChunk(game, chunks, e4);
            AddEntityToChunk(game, chunks, e2);

            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);

            Assert.Equal(3, game.shUniforms.PointLightsCount);
            var uploadedX = new HashSet<float>
            {
                game.shUniforms.PointLights3[0],
                game.shUniforms.PointLights3[3],
                game.shUniforms.PointLights3[6]
            };
            Assert.Equal(new HashSet<float> { 105f, 110f, 115f }, uploadedX);

            // On stationary frame 2 (cache hit): shift model-view matrix X by +50 and move e1 by +2 -> must re-project immediately
            game.MvMatrix.Top[12] = 50.0;
            e1.Pos.X = 107.0;
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);

            Assert.Equal(3, game.shUniforms.PointLightsCount);
            var reprojectedX = new HashSet<float>
            {
                game.shUniforms.PointLights3[0],
                game.shUniforms.PointLights3[3],
                game.shUniforms.PointLights3[6]
            };
            Assert.Equal(new HashSet<float> { 157f, 160f, 165f }, reprojectedX);

            // Extinguish e2 during stationary cache window -> immediately excluded on next frame
            e2.SetLightLevel(0);
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(2, game.shUniforms.PointLightsCount);
        }

        [Fact]
        public void OptimizeLightCulling_ReusesStationaryCacheFor15FramesAndInvalidatesOnMovement()
        {
            (SystemRenderPlayerEffects system, ClientMain game, Dictionary<long, ClientChunk> chunks) = CreateHarness(maxDynLights: 8);
            AddEntityToChunk(game, chunks, CreateLightEntity(105, 64, 100, lightLevel: 14));

            // Frame 0: initial scan finds 1 lit entity
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(1, game.shUniforms.PointLightsCount);

            // Spawn a second lit entity while player stays within 0.25 blocks
            AddEntityToChunk(game, chunks, CreateLightEntity(108, 64, 100, lightLevel: 14));
            game.EntityPlayer.Pos.X += 0.1;

            // Frames 1..15 reuse stationary cache (up to 15 frames while movement < 0.25 blocks)
            for (int f = 1; f <= 15; f++)
            {
                DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
                Assert.Equal(1, game.shUniforms.PointLightsCount);
            }

            // Frame 16 expires the 15-frame stationary cache and rescans -> discovers 2nd entity
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(2, game.shUniforms.PointLightsCount);

            // Spawn a third entity and move player >= 0.25 blocks -> invalidates immediately on next frame
            AddEntityToChunk(game, chunks, CreateLightEntity(112, 64, 100, lightLevel: 14));
            game.EntityPlayer.Pos.X += 0.3;
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(3, game.shUniforms.PointLightsCount);
        }

        [Fact]
        public void Cleanup_ClearsCachedStateAndIsWiredToLeaveWorldAndDispose()
        {
            MethodInfo cleanup = AccessTools.Method(typeof(DynamicLightOptimization), "Cleanup", Type.EmptyTypes);
            Assert.NotNull(cleanup);

            (SystemRenderPlayerEffects system, ClientMain game, Dictionary<long, ClientChunk> chunks) = CreateHarness(maxDynLights: 8);
            AddEntityToChunk(game, chunks, CreateLightEntity(105, 64, 100, lightLevel: 14));
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(1, game.shUniforms.PointLightsCount);

            // Add entity while stationary, call Cleanup() -> next frame must rescan immediately
            AddEntityToChunk(game, chunks, CreateLightEntity(109, 64, 100, lightLevel: 14));
            cleanup.Invoke(null, null);
            DynamicLightOptimization.OptimizeLightCulling(system, 0.016f);
            Assert.Equal(2, game.shUniforms.PointLightsCount);

            string modSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));
            Assert.Contains("LeaveWorld", modSource, StringComparison.Ordinal);
            Assert.Contains("DynamicLightOptimization.Cleanup()", modSource, StringComparison.Ordinal);
        }

        private (SystemRenderPlayerEffects system, ClientMain game, Dictionary<long, ClientChunk> chunks) CreateHarness(int maxDynLights)
        {
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            var worldMap = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
            var chunks = new Dictionary<long, ClientChunk>();
            WorldMapChunksField.SetValue(worldMap, chunks);
            WorldMapChunksLockField.SetValue(worldMap, new object());
            WorldMapSizeField.SetValue(worldMap, new Vec3i(320, 256, 320));
            WorldMapChunkSizeYField.SetValue(worldMap, 8);
            worldMap.index3dMulX = 2097152;
            worldMap.index3dMulZ = 2097152;

            byte[] hue = new byte[64];
            byte[] sat = new byte[8];
            float[] blockLight = new float[32];
            for (int i = 0; i < 32; i++)
            {
                blockLight[i] = i / 31f;
            }
            WorldMapHueLevelsField?.SetValue(worldMap, hue);
            WorldMapSatLevelsField?.SetValue(worldMap, sat);
            WorldMapBlockLightLevelsField?.SetValue(worldMap, blockLight);
            game.WorldMap = worldMap;

            var uniforms = new DefaultShaderUniforms
            {
                PointLights3 = new float[Math.Max(1, maxDynLights) * 3],
                PointLightColors3 = new float[Math.Max(1, maxDynLights) * 3]
            };
            game.shUniforms = uniforms;
            game.MvMatrix = new StackMatrix4();
            game.MvMatrix.Push(Mat4d.Create());
            AccessTools.Field(typeof(ClientMain), "tmpMatrixd")?.SetValue(game, new double[16]);
            PointLightsField.SetValue(game, new List<IPointLight>());

            var entityPlayer = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            AccessTools.Property(typeof(Entity), "Pos").SetValue(entityPlayer, new EntityPos(100.0, 64.0, 100.0, 0f, 0f, 0f));

            var worldData = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            AccessTools.Property(typeof(ClientWorldPlayerData), "EntityPlayer").SetValue(worldData, entityPlayer);

            var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            AccessTools.Field(typeof(ClientPlayer), "game").SetValue(player, game);
            AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, worldData);
            game.player = player;

            var perception = (PerceptionEffects)RuntimeHelpers.GetUninitializedObject(typeof(PerceptionEffects));
            var renderApi = (RenderAPIGame)RuntimeHelpers.GetUninitializedObject(typeof(RenderAPIGame));
            AccessTools.Field(typeof(RenderAPIGame), "perceptionEffects")?.SetValue(renderApi, perception);

            var clientApi = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
            AccessTools.Field(typeof(ClientCoreAPI), "renderapi")?.SetValue(clientApi, renderApi);
            AccessTools.Field(typeof(ClientMain), "api")?.SetValue(game, clientApi);

            var system = (SystemRenderPlayerEffects)RuntimeHelpers.GetUninitializedObject(typeof(SystemRenderPlayerEffects));
            SystemGameField.SetValue(system, game);
            MaxDynLightsField.SetValue(system, maxDynLights);
            InvalField.SetValue(system, new Vec4d());
            OutvalField.SetValue(system, new Vec4d());
            Outval3Field.SetValue(system, new Vec3f());

            return (system, game, chunks);
        }

        private static LightTestEntity CreateLightEntity(double x, double y, double z, byte lightLevel)
        {
            var ent = new LightTestEntity(new byte[] { 0, 0, lightLevel });
            AccessTools.Property(typeof(Entity), "Pos").SetValue(ent, new EntityPos(x, y, z, 0f, 0f, 0f));
            ent.State = EnumEntityState.Active;
            return ent;
        }

        private static void AddEntityToChunk(ClientMain game, Dictionary<long, ClientChunk> chunks, Entity entity)
        {
            int cx = (int)Math.Floor(entity.Pos.X / 32.0);
            int cy = (int)Math.Floor(entity.Pos.InternalY / 32.0);
            int cz = (int)Math.Floor(entity.Pos.Z / 32.0);
            long key = MapUtil.Index3dL(cx, cy, cz, game.WorldMap.index3dMulX, game.WorldMap.index3dMulZ);

            if (!chunks.TryGetValue(key, out ClientChunk chunk))
            {
                chunk = (ClientChunk)RuntimeHelpers.GetUninitializedObject(typeof(ClientChunk));
                chunk.Entities = new Entity[16];
                chunk.EntitiesCount = 0;
                chunks[key] = chunk;
            }

            chunk.Entities[chunk.EntitiesCount++] = entity;
        }

        private static void InvokeCleanupIfPresent()
        {
            AccessTools.Method(typeof(DynamicLightOptimization), "Cleanup", Type.EmptyTypes)?.Invoke(null, null);
        }

        private static bool GetEntitiesAround_Prefix(ref Entity[] __result)
        {
            getEntitiesAroundCalls++;
            __result = Array.Empty<Entity>();
            return false;
        }

        private static bool PerceptionOnBefore_Prefix()
        {
            perceptionRenderCalls++;
            return false;
        }

        private sealed class LightTestEntity : Entity
        {
            private byte[] lightHsv;

            public LightTestEntity(byte[] lightHsv)
            {
                this.lightHsv = lightHsv;
            }

            public override byte[] LightHsv => lightHsv;

            public void SetLightLevel(byte level)
            {
                lightHsv[2] = level;
            }
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
