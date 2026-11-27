using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class SunMoonOptimizationTests : IDisposable
    {
        private static readonly FieldInfo GameField = AccessTools.Field(typeof(ClientSystem), "game");
        private static readonly FieldInfo GameWorldCalendarField = AccessTools.Field(typeof(ClientMain), "GameWorldCalendar");
        private static readonly FieldInfo ImageSizeField = AccessTools.Field(typeof(SystemRenderSunMoon), "ImageSize");
        private static readonly FieldInfo TargetSunSpecField = AccessTools.Field(typeof(SystemRenderSunMoon), "targetSunSpec");
        private static readonly FieldInfo SunMatField = AccessTools.Field(typeof(SystemRenderSunMoon), "sunmat");
        private static readonly FieldInfo QuadModelField = AccessTools.Field(typeof(SystemRenderSunMoon), "quadModel");
        private static readonly FieldInfo SunTextureIdField = AccessTools.Field(typeof(SystemRenderSunMoon), "suntextureId");
        private static readonly FieldInfo ClientCoreApiRenderApiField = AccessTools.Field(typeof(ClientCoreAPI), "renderapi");
        private static readonly FieldInfo RenderApiGameField = AccessTools.Field(typeof(RenderAPIGame), "game");
        private static readonly MethodInfo VanillaGetAngleSunFromMoon = AccessTools.Method(
            typeof(SystemRenderSunMoon),
            "getAngleSunFromMoon",
            new[] { typeof(Vec3f), typeof(Vec3f) });

        static SunMoonOptimizationTests()
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

        public SunMoonOptimizationTests()
        {
            InvokeCleanupIfPresent();
        }

        public void Dispose()
        {
            InvokeCleanupIfPresent();
        }

        [Fact]
        public void OcclusionQueryRing_UsesFourNonBlockingSlotsSkipsWhenFullBelowHorizonOrOffFrustumAndDisposes()
        {
            Type optType = AccessTools.TypeByName("OptiTime.SunMoonOptimization");
            Assert.NotNull(optType);

            MethodInfo setActive = AccessTools.Method(optType, "SetClientActive", new[] { typeof(bool) });
            MethodInfo postPrefix = AccessTools.Method(optType, "OnRenderFrame3DPost_Prefix", new[] { typeof(SystemRenderSunMoon) });
            MethodInfo disposePostfix = AccessTools.Method(optType, "Dispose_Postfix", new[] { typeof(SystemRenderSunMoon) });
            Assert.NotNull(setActive);
            Assert.NotNull(postPrefix);
            Assert.NotNull(disposePostfix);

            var gl = new FakeQueryBackend();
            var drawCalls = 0;
            bool colorMaskRestored = true;
            bool depthMaskRestored = true;
            bool throwOnDraw = false;

            MethodInfo setTestHooks = AccessTools.Method(optType, "SetTestHooks");
            Assert.NotNull(setTestHooks);
            setTestHooks.Invoke(null, new object[]
            {
                (System.Action<int[]>)gl.GenQueries,
                (System.Action<int[]>)gl.DeleteQueries,
                (System.Func<int, bool>)gl.IsQueryAvailable,
                (System.Func<int, int>)gl.GetQueryResult,
                (System.Action<int>)gl.BeginQuery,
                (System.Action)gl.EndQuery,
                (System.Action<bool>)((enabled) => colorMaskRestored = enabled),
                (System.Action<SystemRenderSunMoon, ClientMain, Matrix4>)((sys, game, mat) =>
                {
                    depthMaskRestored = false;
                    drawCalls++;
                    if (throwOnDraw)
                    {
                        throw new InvalidOperationException("simulated render failure");
                    }
                }),
                (System.Action<ClientMain>)((game) => depthMaskRestored = true)
            });

            setActive.Invoke(null, new object[] { true });

            // Camera looks along -Z with 70 deg vertical FOV; sun at (0, 10, -50) is inside the frustum.
            SystemRenderSunMoon sunMoon = CreateSunMoon(
                sunPos: new Vec3f(0f, 10f, -50f),
                sunPosNorm: new Vec3f(0f, 0.196f, -0.98f),
                withCamera: true);
            var game = (ClientMain)GameField.GetValue(sunMoon)!;
            var calendar = (ClientGameCalendar)GameWorldCalendarField.GetValue(game)!;

            // 1. Frames 1..4: GPU hasn't finished any query yet; 4 distinct query IDs are issued without reading QueryResult.
            for (int frame = 0; frame < 4; frame++)
            {
                bool runVanilla = (bool)postPrefix.Invoke(null, new object[] { sunMoon })!;
                Assert.False(runVanilla);
            }

            Assert.Equal(1, gl.GenCalls);
            Assert.Equal(4, gl.GeneratedIds.Length);
            Assert.Equal(4, drawCalls);
            Assert.Equal(0, gl.ResultReads);
            Assert.Equal(new[] { 101, 102, 103, 104 }, gl.BegunIds);
            Assert.Equal(4, gl.EndCalls);
            Assert.True(colorMaskRestored);
            Assert.True(depthMaskRestored);

            // 2. Frame 5 with all 4 slots still in-flight: must skip draw & BeginQuery completely (no stall, no dummy draw).
            Assert.False((bool)postPrefix.Invoke(null, new object[] { sunMoon })!);
            Assert.Equal(4, drawCalls);
            Assert.Equal(0, gl.ResultReads);
            Assert.Equal(4, gl.BegunIds.Count);

            // 3. Oldest slot (101) becomes available with 750 samples -> targetSunSpec = 0.5f, slot 101 reused for next draw.
            gl.MarkReady(101, 750);
            Assert.False((bool)postPrefix.Invoke(null, new object[] { sunMoon })!);
            Assert.Equal(0.5f, (float)TargetSunSpecField.GetValue(sunMoon)!, 4);
            Assert.Equal(1, gl.ResultReads);
            Assert.Equal(5, drawCalls);
            Assert.Equal(101, gl.BegunIds[^1]);

            // 4. Multiple oldest slots (102=300, 103=1800) become ready -> FIFO drains both, keeps freshest clamped 1.0f.
            gl.MarkReady(102, 300);
            gl.MarkReady(103, 1800);
            Assert.False((bool)postPrefix.Invoke(null, new object[] { sunMoon })!);
            Assert.Equal(1.0f, (float)TargetSunSpecField.GetValue(sunMoon)!, 4);
            Assert.Equal(3, gl.ResultReads);
            Assert.Equal(6, drawCalls);
            Assert.Equal(102, gl.BegunIds[^1]);

            // 5. Sun behind camera (+Z while looking along -Z): off-frustum skip sets targetSunSpec=0f with 0 draws/queries!
            calendar.SunPosition.Set(0f, 15f, 50f);
            calendar.SunPositionNormalized.Set(0f, 0.287f, 0.958f);
            Assert.False((bool)postPrefix.Invoke(null, new object[] { sunMoon })!);
            Assert.Equal(0f, (float)TargetSunSpecField.GetValue(sunMoon)!, 4);
            Assert.Equal(6, drawCalls);

            // 6. With global world warp active, off-frustum skip disables itself conservatively and issues a query.
            game.shUniforms.GlobalWorldWarp = 0.5f;
            Assert.False((bool)postPrefix.Invoke(null, new object[] { sunMoon })!);
            Assert.Equal(7, drawCalls);
            game.shUniforms.GlobalWorldWarp = 0f;

            // 7. Sun drops below horizon (Y = -0.1f < -0.05f): drains ready queries, resets targetSunSpec = 0f, skips draw!
            gl.MarkReady(104, 900);
            calendar.SunPositionNormalized.Set(0f, -0.1f, -0.99f);
            calendar.SunPosition.Set(0f, -5f, -50f);
            Assert.False((bool)postPrefix.Invoke(null, new object[] { sunMoon })!);
            Assert.Equal(0f, (float)TargetSunSpecField.GetValue(sunMoon)!, 4);
            Assert.Equal(7, drawCalls);
            Assert.Equal(4, gl.ResultReads); // slot 104 drained cleanly without overwriting targetSunSpec

            // 8. Sun rises above horizon and in front of camera, and draw throws: EndQuery + ColorMask + DepthMask restored!
            calendar.SunPositionNormalized.Set(0f, 0.2f, -0.98f);
            calendar.SunPosition.Set(0f, 10f, -50f);
            throwOnDraw = true;
            var ex = Assert.Throws<TargetInvocationException>(() => postPrefix.Invoke(null, new object[] { sunMoon }));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.True(colorMaskRestored);
            Assert.True(depthMaskRestored);
            Assert.Equal(gl.BegunIds.Count, gl.EndCalls);
            throwOnDraw = false;

            // 9. Dispose deletes all 4 generated queries exactly once (idempotent on second call).
            disposePostfix.Invoke(null, new object[] { sunMoon });
            Assert.Equal(1, gl.DeleteCalls);
            Assert.Equal(new[] { 101, 102, 103, 104 }, gl.DeletedIds);
            disposePostfix.Invoke(null, new object[] { sunMoon });
            Assert.Equal(1, gl.DeleteCalls);
        }

        [Fact]
        public void ClipFrustumTestAndScratchMath_MatchVanillaWithZeroAllocations()
        {
            Type optType = AccessTools.TypeByName("OptiTime.SunMoonOptimization");
            Assert.NotNull(optType);

            MethodInfo projectMethod = AccessTools.Method(optType, "ProjectToScreenNdc");
            MethodInfo angleMethod = AccessTools.Method(optType, "ComputeAngleSunFromMoon");
            MethodInfo frustumMethod = AccessTools.Method(optType, "IsQuadOutsideClipFrustum");
            Assert.NotNull(projectMethod);
            Assert.NotNull(angleMethod);
            Assert.NotNull(frustumMethod);

            double[] proj = Mat4d.Perspective(Mat4d.Create(), 70.0 * Math.PI / 180.0, 16.0 / 9.0, 0.1, 3000.0);
            double[] identityCam = Mat4d.Create();
            double[] scratchMv = new double[16];
            double[] scratchMvp = new double[16];

            var isOutside = (System.Func<double[], double, bool>)Delegate.CreateDelegate(
                typeof(System.Func<double[], double, bool>), frustumMethod);

            // Helper to build MVP for a quad centered at pos with given vertical stretch.
            bool TestSunQuad(Vector3 pos, float yScale, float yOffset)
            {
                Quaternion rot = SystemRenderSunMoon.CreateLookRotation(pos);
                Matrix4 mat = Matrix4.CreateTranslation(-128f, yOffset, -128f)
                    * Matrix4.CreateScale(0.04f, 0.04f * yScale, 0.04f)
                    * Matrix4.CreateFromQuaternion(rot)
                    * Matrix4.CreateTranslation(pos);
                double[] model =
                {
                    mat.M11, mat.M12, mat.M13, mat.M14,
                    mat.M21, mat.M22, mat.M23, mat.M24,
                    mat.M31, mat.M32, mat.M33, mat.M34,
                    mat.M41, mat.M42, mat.M43, mat.M44
                };
                Mat4d.Mul(scratchMv, identityCam, model);
                Mat4d.Mul(scratchMvp, proj, scratchMv);
                return isOutside(scratchMvp, 256.0);
            }

            // Directly in front (-Z): visible.
            Assert.False(TestSunQuad(new Vector3(0f, 10f, -50f), 1f, -128f));
            // Behind camera (+Z): culled.
            Assert.True(TestSunQuad(new Vector3(0f, 10f, 50f), 1f, -128f));
            // Far to the right (+X beyond frustum): culled.
            Assert.True(TestSunQuad(new Vector3(120f, 0f, -50f), 1f, -128f));
            // High above top of 70-deg FOV (top plane at z=-50 is y ~ 35): normal sun at y=48 is outside,
            // while 7x-tall occlusion query quad extending downward (-256 * 0.28 = -71.7) overlaps top of screen -> visible!
            Assert.True(TestSunQuad(new Vector3(0f, 48f, -50f), 1f, -128f));
            Assert.False(TestSunQuad(new Vector3(0f, 48f, -50f), 7f, -256f));

            // 1. Verify ProjectToScreenNdc matches MatrixToolsd.Project + NDC transform bit-for-bit.
            double[] cam = Mat4d.Create();
            Mat4d.RotateX(cam, cam, 0.35);
            Mat4d.RotateY(cam, cam, -0.8);
            Matrix4 sunMat = Matrix4.CreateTranslation(-128f, -128f, -128f)
                * Matrix4.CreateScale(0.04f)
                * Matrix4.CreateFromQuaternion(SystemRenderSunMoon.CreateLookRotation(new Vector3(12f, 25f, -18f)))
                * Matrix4.CreateTranslation(new Vector3(12f, 25f, -18f));

            double[] vanillaModel =
            {
                sunMat.M11, sunMat.M12, sunMat.M13, sunMat.M14,
                sunMat.M21, sunMat.M22, sunMat.M23, sunMat.M24,
                sunMat.M31, sunMat.M32, sunMat.M33, sunMat.M34,
                sunMat.M41, sunMat.M42, sunMat.M43, sunMat.M44
            };
            double[] vanillaMv = new double[16];
            Mat4d.Mul(vanillaMv, cam, vanillaModel);
            Vec3d projected = MatrixToolsd.Project(new Vec3d(128.0, 128.0, 0.0), proj, vanillaMv, 1920, 1080);
            Vec3f expectedScreen = new Vec3f(
                (float)projected.X / 1920f * 2f - 1f,
                (float)projected.Y / 1080f * 2f - 1f,
                (float)projected.Z);

            Vec3f actualScreen = new Vec3f();
            var fastProject = (System.Action<double, double, double, double[], double[], double[], int, int, Vec3f>)
                Delegate.CreateDelegate(typeof(System.Action<double, double, double, double[], double[], double[], int, int, Vec3f>), projectMethod);

            Mat4d.Mul(scratchMv, cam, vanillaModel);
            fastProject(128.0, 128.0, 0.0, proj, scratchMv, scratchMvp, 1920, 1080, actualScreen);

            Assert.Equal(expectedScreen.X, actualScreen.X, 5);
            Assert.Equal(expectedScreen.Y, actualScreen.Y, 5);
            Assert.Equal(expectedScreen.Z, actualScreen.Z, 5);

            // 2. Verify ComputeAngleSunFromMoon matches vanilla and never returns NaN.
            var sunMoon = (SystemRenderSunMoon)RuntimeHelpers.GetUninitializedObject(typeof(SystemRenderSunMoon));
            var fastAngle = (System.Func<Vec3f, Vec3f, float>)Delegate.CreateDelegate(typeof(System.Func<Vec3f, Vec3f, float>), angleMethod);

            Vec3f[] suns =
            {
                new Vec3f(0.5f, 0.7f, 0.51f).Normalize(),
                new Vec3f(-0.3f, 0.9f, -0.31f).Normalize(),
                new Vec3f(0f, 1f, 0f),
                new Vec3f(0.1f, -0.2f, 0.97f).Normalize()
            };
            Vec3f[] moons =
            {
                new Vec3f(-0.4f, 0.6f, 0.69f).Normalize(),
                new Vec3f(0.8f, -0.1f, -0.59f).Normalize(),
                new Vec3f(0f, 1f, 0f),
                new Vec3f(0.1f, -0.2f, 0.97f).Normalize()
            };

            for (int i = 0; i < suns.Length; i++)
            {
                float expectedAngle = (float)VanillaGetAngleSunFromMoon.Invoke(
                    sunMoon,
                    new object[] { suns[i].Clone().Sub(moons[i]), moons[i] })!;
                float actualAngle = fastAngle(suns[i], moons[i]);
                Assert.False(float.IsNaN(actualAngle));
                if (!float.IsNaN(expectedAngle))
                {
                    Assert.Equal(expectedAngle, actualAngle, 5);
                }
            }

            // 3. Zero allocations across 5,000 invocations of all three hot-path helpers.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                fastProject(128.0, 128.0, 0.0, proj, scratchMv, scratchMvp, 1920, 1080, actualScreen);
                _ = isOutside(scratchMvp, 256.0);
                _ = fastAngle(suns[0], moons[0]);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        private static SystemRenderSunMoon CreateSunMoon(Vec3f sunPos, Vec3f sunPosNorm, bool withCamera)
        {
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            var calendar = (ClientGameCalendar)RuntimeHelpers.GetUninitializedObject(typeof(ClientGameCalendar));
            calendar.SunPosition = sunPos;
            calendar.SunPositionNormalized = sunPosNorm;
            GameWorldCalendarField.SetValue(game, calendar);
            game.shUniforms = new DefaultShaderUniforms();

            if (withCamera)
            {
                double[] proj = Mat4d.Perspective(Mat4d.Create(), 70.0 * Math.PI / 180.0, 16.0 / 9.0, 0.1, 3000.0);
                game.PMatrix = new StackMatrix4();
                game.PMatrix.Push(proj);

                var camera = (PlayerCamera)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCamera));
                camera.CameraMatrixOrigin = Mat4d.Create();
                game.MainCamera = camera;

                var renderApi = (RenderAPIGame)RuntimeHelpers.GetUninitializedObject(typeof(RenderAPIGame));
                RenderApiGameField.SetValue(renderApi, game);
                var clientApi = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
                ClientCoreApiRenderApiField.SetValue(clientApi, renderApi);
                game.api = clientApi;
            }

            var sunMoon = (SystemRenderSunMoon)RuntimeHelpers.GetUninitializedObject(typeof(SystemRenderSunMoon));
            GameField.SetValue(sunMoon, game);
            ImageSizeField.SetValue(sunMoon, 256);
            sunMoon.sunScale = 0.04f;
            sunMoon.moonScale = 0.023100002f;
            sunMoon.ModelMat = new Matrixf();
            QuadModelField.SetValue(sunMoon, null);
            SunTextureIdField.SetValue(sunMoon, 7);
            TargetSunSpecField.SetValue(sunMoon, 0f);
            SunMatField.SetValue(sunMoon, Matrix4.Identity);
            return sunMoon;
        }

        private static void InvokeCleanupIfPresent()
        {
            Type optType = AccessTools.TypeByName("OptiTime.SunMoonOptimization");
            AccessTools.Method(optType, "Cleanup", Type.EmptyTypes)?.Invoke(null, null);
        }

        private sealed class FakeQueryBackend
        {
            private readonly Dictionary<int, int> readyResults = new Dictionary<int, int>();
            private int nextId = 101;
            public int GenCalls;
            public int DeleteCalls;
            public int ResultReads;
            public int EndCalls;
            public int[] GeneratedIds = Array.Empty<int>();
            public int[] DeletedIds = Array.Empty<int>();
            public readonly List<int> BegunIds = new List<int>();

            public void GenQueries(int[] ids)
            {
                GenCalls++;
                for (int i = 0; i < ids.Length; i++)
                {
                    ids[i] = nextId++;
                }

                GeneratedIds = (int[])ids.Clone();
            }

            public void DeleteQueries(int[] ids)
            {
                DeleteCalls++;
                DeletedIds = (int[])ids.Clone();
            }

            public void MarkReady(int id, int samples)
            {
                readyResults[id] = samples;
            }

            public bool IsQueryAvailable(int id)
            {
                return readyResults.ContainsKey(id);
            }

            public int GetQueryResult(int id)
            {
                ResultReads++;
                int value = readyResults[id];
                readyResults.Remove(id);
                return value;
            }

            public void BeginQuery(int id)
            {
                BegunIds.Add(id);
            }

            public void EndQuery()
            {
                EndCalls++;
            }
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
