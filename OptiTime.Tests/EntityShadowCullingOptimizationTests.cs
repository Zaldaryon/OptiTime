using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class EntityShadowCullingOptimizationTests : IDisposable
    {
        private static readonly FieldInfo EntityPosBackingField = AccessTools.Field(typeof(Entity), "<Pos>k__BackingField");
        private static readonly FieldInfo ClientMainPlayerField = AccessTools.Field(typeof(ClientMain), "player");
        private static readonly FieldInfo ClientPlayerEntityField = AccessTools.Field(typeof(ClientPlayer), "worlddata");

        static EntityShadowCullingOptimizationTests()
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

        public EntityShadowCullingOptimizationTests()
        {
            EntityShadowCullingOptimization.Cleanup();
        }

        public void Dispose()
        {
            EntityShadowCullingOptimization.Cleanup();
        }

        [Fact]
        public void LargeEntityRadiusScalesCullDistanceAndAppliesHysteresis()
        {
            var player = CreatePlayerEntity(0.0, 0.0);
            var world = CreateClientWorld(player);

            // Standard entity (radius 3.0 -> 80m cull, 88m with 1.21x squared hysteresis).
            var standard = CreateEntity(id: 2, x: 84.0, z: 0.0, radius: 3.0, world: world);
            standard.IsShadowRendered = false;
            Assert.True(EntityShadowCullingOptimization.IsBeyondCullDistance(standard));

            // With IsShadowRendered = true, 84m stays rendered (84^2 = 7056 <= 6400 * 1.21 = 7744).
            standard.IsShadowRendered = true;
            Assert.False(EntityShadowCullingOptimization.IsBeyondCullDistance(standard));

            // Past 88m (89^2 = 7921 > 7744), even an already-rendered standard entity is culled.
            standard.Pos.X = 89.0;
            Assert.True(EntityShadowCullingOptimization.IsBeyondCullDistance(standard));

            // Large entity (radius 6.0 -> scale 2.0 -> 160m cull, 176m with hysteresis).
            var giant = CreateEntity(id: 3, x: 120.0, z: 0.0, radius: 6.0, world: world);
            giant.IsShadowRendered = false;
            Assert.False(EntityShadowCullingOptimization.IsBeyondCullDistance(giant));

            giant.Pos.X = 165.0;
            giant.IsShadowRendered = false;
            Assert.True(EntityShadowCullingOptimization.IsBeyondCullDistance(giant));

            giant.IsShadowRendered = true;
            Assert.False(EntityShadowCullingOptimization.IsBeyondCullDistance(giant));

            giant.Pos.X = 177.0;
            Assert.True(EntityShadowCullingOptimization.IsBeyondCullDistance(giant));

            // NaN / negative / small FrustumSphereRadius falls back safely to 3.0 (80m).
            var badRadius = CreateEntity(id: 4, x: 82.0, z: 0.0, radius: double.NaN, world: world);
            badRadius.IsShadowRendered = false;
            Assert.True(EntityShadowCullingOptimization.IsBeyondCullDistance(badRadius));
        }

        [Fact]
        public void FarHysteresisSurvivesNearCascadeResetRespectsQualityAndAllocatesZeroBytes()
        {
            EntityShadowCullingOptimization.UpdateShadowMapQuality(3);

            // Entity with null World proves the pass path uses cached player X/Z without resolving entity.World.
            var mob = CreateEntity(id: 200, x: 10.0 + 79.0, z: 20.0, radius: 3.0, world: null);

            // Frame 1 ShadowFar: mob at 79m <= 80m -> rendered.
            Assert.False(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));
            mob.IsShadowRendered = true;

            // Frame 1 ShadowNear: culling skipped and ShadowNear resets IsShadowRendered = false.
            Assert.False(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: false, beginNewPass: true));
            mob.IsShadowRendered = false;

            // Frame 2 ShadowFar: mob moves to 84m (in 80..88m deadband) with IsShadowRendered == false from ShadowNear.
            // Far hysteresis remembers it rendered on previous ShadowFar and keeps it rendered!
            mob.Pos.X = 10.0 + 84.0;
            Assert.False(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));

            // Frame 3 ShadowFar: mob moves to 89m > 88m -> culled.
            mob.Pos.X = 10.0 + 89.0;
            Assert.True(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));

            // Frame 4 ShadowFar: mob steps back to 84m -> stays culled until entering <= 80m!
            mob.Pos.X = 10.0 + 84.0;
            Assert.True(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));

            // Quality lowered to 1 at runtime -> culling becomes inactive immediately.
            EntityShadowCullingOptimization.UpdateShadowMapQuality(1);
            mob.Pos.X = 10.0 + 150.0;
            Assert.False(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));

            // OnLeaveWorld clears hysteresis state so a newly loaded world never collides on EntityId.
            EntityShadowCullingOptimization.UpdateShadowMapQuality(3);
            mob.Pos.X = 10.0 + 79.0;
            Assert.False(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));
            EntityShadowCullingOptimization.OnLeaveWorld();
            mob.Pos.X = 10.0 + 84.0;
            mob.IsShadowRendered = false;
            Assert.True(EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: true));

            // Zero allocations over 5,000 far-pass evaluations in steady state.
            _ = EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: false);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                _ = EntityShadowCullingOptimization.EvaluateFarShadowStepForTest(mob, 10.0, 20.0, isFarPass: true, beginNewPass: false);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        [Fact]
        public void ConfigTracksRuntimeShadowQualitySkipAndClear()
        {
            var config = new OptiTimeConfig { EntityShadowDistanceCullEnabled = true };
            config.MarkEntityShadowCullSkipped(1);

            MethodInfo clearMethod = AccessTools.Method(typeof(OptiTimeConfig), "ClearEntityShadowCullSkipped");
            MethodInfo statusMethod = AccessTools.Method(typeof(OptiTimeConfig), "GetOptimizationStatusText",
                new[] { typeof(bool), typeof(string) });
            Assert.NotNull(clearMethod);
            Assert.NotNull(statusMethod);

            Assert.Equal(1, config.EntityShadowSkippedQuality);

            clearMethod.Invoke(config, null);
            Assert.Null(config.EntityShadowSkippedQuality);
            string activeStatus = (string)statusMethod.Invoke(config, new object[] { true, nameof(OptiTimeConfig.EntityShadowDistanceCullEnabled) })!;
            Assert.Equal("ON", activeStatus);
        }

        private static TestRadiusEntity CreateEntity(long id, double x, double z, double radius, IWorldAccessor world = null)
        {
            var entity = (TestRadiusEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestRadiusEntity));
            entity.EntityId = id;
            EntityPosBackingField.SetValue(entity, new EntityPos(x, 64.0, z));
            entity.World = world;
            entity.RadiusOverride = radius;
            return entity;
        }

        private static IClientWorldAccessor CreateClientWorld(EntityPlayer playerEntity)
        {
            var clientPlayer = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            var worldData = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            worldData.EntityPlayer = playerEntity;
            ClientPlayerEntityField.SetValue(clientPlayer, worldData);

            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            ClientMainPlayerField.SetValue(game, clientPlayer);
            return game;
        }

        private static EntityPlayer CreatePlayerEntity(double x, double z)
        {
            var player = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            EntityPosBackingField.SetValue(player, new EntityPos(x, 64.0, z));
            player.EntityId = 1;
            return player;
        }

        private sealed class TestRadiusEntity : Entity
        {
            public double RadiusOverride;
            public override double FrustumSphereRadius => RadiusOverride;
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
