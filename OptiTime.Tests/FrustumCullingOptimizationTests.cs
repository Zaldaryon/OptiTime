using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class FrustumCullingOptimizationTests : IDisposable
    {
        private const string TestHarmonyId = "com.zaldaryon.optitime.test.frustum";

        private static int aabbPlaneChecks;
        private readonly Harmony harmony;

        static FrustumCullingOptimizationTests()
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

        public FrustumCullingOptimizationTests()
        {
            harmony = new Harmony(TestHarmonyId);
            aabbPlaneChecks = 0;

            MethodInfo aabbMethod = AccessTools.Method(typeof(Plane), nameof(Plane.AABBisOutside), new[] { typeof(Sphere) });
            harmony.Patch(aabbMethod, prefix: new HarmonyMethod(typeof(FrustumCullingOptimizationTests), nameof(AabbIsOutside_Prefix)));
        }

        public void Dispose()
        {
            InvokeCleanupIfPresent();
            harmony.UnpatchAll(TestHarmonyId);
            aabbPlaneChecks = 0;
        }

        [Fact]
        public void InFrustumAndRange_EvaluatesRangeAndLodBeforePlaneTests()
        {
            InstallOptimizationIfPresent();

            FrustumCulling culler = CreateConfiguredCuller(new BlockPos(0, 64, 0), viewDistance: 256, lod0BiasSq: 64f * 64f, yawRad: 0.0, pitchRad: 0.0);

            // All spheres sit straight ahead along -Z (inside all 5 frustum planes), so vanilla
            // runs all 5 Plane.AABBisOutside checks before rejecting on range/LOD.
            var cases = new (Sphere Sphere, int LodLevel, float Lod0Bias)[]
            {
                (new Sphere(0f, 64f, -40f, 16f, 16f, 16f), 0, 0f),           // lod0BiasSq == 0
                (new Sphere(0f, 64f, -150f, 16f, 16f, 16f), 0, 64f * 64f),   // lod0 beyond lod0BiasSq + 1024
                (new Sphere(0f, 64f, -320f, 16f, 16f, 16f), 1, 64f * 64f),   // lod1 beyond ViewDistanceSq
                (new Sphere(0f, 64f, -220f, 16f, 16f, 16f), 2, 64f * 64f),   // lod2 beyond lod2BiasSq
                (new Sphere(0f, 64f, -60f, 16f, 16f, 16f), 3, 64f * 64f),    // lod3 too close (<= lod2BiasSq)
                (new Sphere(0f, 64f, -320f, 16f, 16f, 16f), 3, 64f * 64f),   // lod3 beyond ViewDistanceSq
                (new Sphere(0f, 64f, -40f, 16f, 16f, 16f), 4, 64f * 64f),    // default invalid lodLevel
                (new Sphere(0f, 64f, -40f, 16f, 16f, 16f), -1, 64f * 64f),   // negative invalid lodLevel
            };

            foreach (var (sphere, lodLevel, lod0Bias) in cases)
            {
                culler.lod0BiasSq = lod0Bias;
                aabbPlaneChecks = 0;

                bool visible = culler.InFrustumAndRange(sphere, nowVisible: false, lodLevel: lodLevel);

                Assert.False(visible);
                Assert.Equal(0, aabbPlaneChecks);
            }

            // A sphere in range and inside the frustum still runs plane checks and returns true.
            culler.lod0BiasSq = 64f * 64f;
            aabbPlaneChecks = 0;
            Assert.True(culler.InFrustumAndRange(new Sphere(0f, 64f, -40f, 16f, 16f, 16f), nowVisible: false, lodLevel: 0));
            Assert.Equal(5, aabbPlaneChecks);
        }

        [Fact]
        public void InFrustumAndRange_MatchesVanillaBitExactAcrossAllLodLevelsAndSpherePositions()
        {
            Type optType = AccessTools.TypeByName("OptiTime.FrustumCullingOptimization");
            Assert.NotNull(optType);
            MethodInfo prefix = AccessTools.Method(optType, "InFrustumAndRange_Prefix");
            Assert.NotNull(prefix);
            AccessTools.Method(optType, "SetClientActive", new[] { typeof(bool) })?.Invoke(null, new object[] { true });

            var rng = new Random(122740);
            var playerPos = new BlockPos(512, 110, 512);
            double[] yaws = { 0.0, 0.75, 2.35, -1.6 };
            double[] pitches = { 0.0, 0.45, -0.55, 0.15 };
            float[] lod0Biases = { 0f, 64f * 64f, 128f * 128f };
            int[] lodLevels = { -1, 0, 1, 2, 3, 4 };

            for (int c = 0; c < yaws.Length; c++)
            {
                FrustumCulling culler = CreateConfiguredCuller(playerPos, viewDistance: 256, lod0BiasSq: 64f * 64f, yawRad: yaws[c], pitchRad: pitches[c]);
                double lod2Boundary = Math.Sqrt(culler.lod2BiasSq);
                double vdBoundary = Math.Sqrt(culler.ViewDistanceSq);

                // Exact boundary spheres + 500 random 3D spheres per camera orientation
                var spheres = new Sphere[506];
                spheres[0] = new Sphere(playerPos.X, playerPos.Y, (float)(playerPos.Z - lod2Boundary), 16f, 16f, 16f);
                spheres[1] = new Sphere(playerPos.X, playerPos.Y, (float)(playerPos.Z - vdBoundary), 16f, 16f, 16f);
                spheres[2] = new Sphere(playerPos.X, playerPos.Y, (float)(playerPos.Z - Math.Sqrt(64f * 64f + 1024f)), 16f, 16f, 16f);
                spheres[3] = new Sphere(playerPos.X, playerPos.Y, playerPos.Z, 16f, 16f, 16f);
                spheres[4] = new Sphere(playerPos.X, playerPos.Y + 200f, playerPos.Z - 40f, 16f, 16f, 16f);
                spheres[5] = new Sphere(playerPos.X, playerPos.Y, playerPos.Z + 80f, 16f, 16f, 16f);

                for (int i = 6; i < spheres.Length; i++)
                {
                    float sx = (float)(playerPos.X + (rng.NextDouble() * 700.0 - 350.0));
                    float sy = (float)(playerPos.Y + (rng.NextDouble() * 240.0 - 120.0));
                    float sz = (float)(playerPos.Z + (rng.NextDouble() * 700.0 - 350.0));
                    float rx = (float)(rng.NextDouble() * 24.0 + 1.0);
                    float ry = (float)(rng.NextDouble() * 24.0 + 1.0);
                    float rz = (float)(rng.NextDouble() * 24.0 + 1.0);
                    spheres[i] = new Sphere(sx, sy, sz, rx, ry, rz);
                }

                foreach (float lod0Bias in lod0Biases)
                {
                    culler.lod0BiasSq = lod0Bias;
                    foreach (Sphere sphere in spheres)
                    {
                        foreach (int lodLevel in lodLevels)
                        {
                            bool vanilla = culler.InFrustumAndRange(sphere, nowVisible: false, lodLevel: lodLevel);
                            object[] args = new object[] { culler, sphere, lodLevel, false };
                            bool runOriginal = (bool)prefix.Invoke(null, args);
                            bool optimized = runOriginal
                                ? culler.InFrustumAndRange(sphere, nowVisible: false, lodLevel: lodLevel)
                                : (bool)args[3];

                            Assert.Equal(vanilla, optimized);
                        }
                    }
                }
            }
        }

        [Fact]
        public void InFrustumAndRange_AllocatesZeroBytesAndFallsBackWhenInactiveOrUninitialized()
        {
            Type optType = AccessTools.TypeByName("OptiTime.FrustumCullingOptimization");
            Assert.NotNull(optType);
            InstallOptimizationIfPresent();

            FrustumCulling culler = CreateConfiguredCuller(new BlockPos(100, 64, 100), viewDistance: 256, lod0BiasSq: 64f * 64f, yawRad: 0.0, pitchRad: 0.0);
            var nearSphere = new Sphere(100f, 64f, 60f, 16f, 16f, 16f);
            var farSphere = new Sphere(100f, 64f, -200f, 16f, 16f, 16f);

            // Warm up JIT
            culler.InFrustumAndRange(nearSphere, false, 0);
            culler.InFrustumAndRange(farSphere, false, 0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            int visibleCount = 0;
            for (int i = 0; i < 5000; i++)
            {
                if (culler.InFrustumAndRange(nearSphere, false, i & 3)) visibleCount++;
                if (culler.InFrustumAndRange(farSphere, false, i & 3)) visibleCount++;
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
            Assert.True(visibleCount > 0);

            // Inactive client flag falls back cleanly to vanilla
            InvokeCleanupIfPresent();
            aabbPlaneChecks = 0;
            Assert.False(culler.InFrustumAndRange(farSphere, false, 0));
            Assert.True(aabbPlaneChecks > 0);
        }

        private static FrustumCulling CreateConfiguredCuller(BlockPos playerPos, int viewDistance, float lod0BiasSq, double yawRad, double pitchRad)
        {
            var culler = new FrustumCulling();
            culler.UpdateViewDistance(viewDistance);
            culler.lod0BiasSq = lod0BiasSq;
            culler.lod2BiasSq = culler.ViewDistanceSq * 0.45;

            double[] proj = Mat4d.Perspective(Mat4d.Create(), 70.0 * Math.PI / 180.0, 16.0 / 9.0, 0.1, 3000.0);
            double[] cam = Mat4d.Create();
            Mat4d.RotateX(cam, cam, pitchRad);
            Mat4d.RotateY(cam, cam, yawRad);
            Mat4d.Translate(cam, cam, -playerPos.X, -playerPos.Y, -playerPos.Z);
            culler.CalcFrustumEquations(playerPos, proj, cam);

            return culler;
        }

        private void InstallOptimizationIfPresent()
        {
            Type optType = AccessTools.TypeByName("OptiTime.FrustumCullingOptimization");
            if (optType == null)
            {
                return;
            }

            AccessTools.Method(optType, "SetClientActive", new[] { typeof(bool) })?.Invoke(null, new object[] { true });
            MethodInfo target = AccessTools.Method(typeof(FrustumCulling), nameof(FrustumCulling.InFrustumAndRange),
                new[] { typeof(Sphere), typeof(bool), typeof(int) });
            MethodInfo prefix = AccessTools.Method(optType, "InFrustumAndRange_Prefix");
            if (target != null && prefix != null)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            }
        }

        private static void InvokeCleanupIfPresent()
        {
            Type optType = AccessTools.TypeByName("OptiTime.FrustumCullingOptimization");
            AccessTools.Method(optType, "Cleanup", Type.EmptyTypes)?.Invoke(null, null);
        }

        private static bool AabbIsOutside_Prefix()
        {
            aabbPlaneChecks++;
            return true;
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
