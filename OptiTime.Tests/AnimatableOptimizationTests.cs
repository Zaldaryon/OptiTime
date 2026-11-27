using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class AnimatableOptimizationTests : IDisposable
    {
        private const string TestHarmonyId = "com.zaldaryon.optitime.test.animatable";

        private static readonly FieldInfo RendererPosField = AccessTools.Field(typeof(AnimatableRenderer), "pos");
        private static readonly FieldInfo RendererCapiField = AccessTools.Field(typeof(AnimatableRenderer), "capi");
        private static readonly FieldInfo UtilCapiField = AccessTools.Field(typeof(AnimationUtil), "capi");
        private static readonly FieldInfo UtilApiField = AccessTools.Field(typeof(AnimationUtil), "api");
        private static readonly FieldInfo UtilStopTriggeredField = AccessTools.Field(typeof(AnimationUtil), "stopRenderTriggered");

        private readonly Harmony harmony;

        static AnimatableOptimizationTests()
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

        public AnimatableOptimizationTests()
        {
            harmony = new Harmony(TestHarmonyId);
            InvokeCleanupIfPresent();
        }

        public void Dispose()
        {
            InvokeCleanupIfPresent();
            harmony.UnpatchAll(TestHarmonyId);
        }

        [Fact]
        public void AnimatableRenderer_SkipsOffCameraNonShadowDrawsWithConservativeScaleRadius()
        {
            Type optType = AccessTools.TypeByName("OptiTime.AnimatableOptimization");
            Assert.NotNull(optType);
            MethodInfo prefix = AccessTools.Method(optType, "AnimatableRenderer_OnRenderFrame_Prefix",
                new[] { typeof(AnimatableRenderer), typeof(EnumRenderStage) });
            Assert.NotNull(prefix);
            AccessTools.Method(optType, "SetClientActive", new[] { typeof(bool) })!.Invoke(null, new object[] { true });

            FrustumCulling culler = CreateConfiguredCuller(new BlockPos(0, 64, 0));
            var fakeClientApi = CreateFakeClientApi(culler, out _);

            // 1. Behind camera (+Z while camera looks along -Z): Opaque & OIT culled, ShadowNear & ShadowFar NOT culled.
            AnimatableRenderer behind = CreateRenderer(fakeClientApi, new Vec3d(0, 64, 30), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            Assert.False((bool)prefix.Invoke(null, new object[] { behind, EnumRenderStage.Opaque })!);
            Assert.False((bool)prefix.Invoke(null, new object[] { behind, EnumRenderStage.OIT })!);
            Assert.True((bool)prefix.Invoke(null, new object[] { behind, EnumRenderStage.ShadowNear })!);
            Assert.True((bool)prefix.Invoke(null, new object[] { behind, EnumRenderStage.ShadowFar })!);

            // 2. Directly in front of camera (-Z): Opaque & OIT proceed to draw.
            AnimatableRenderer inFront = CreateRenderer(fakeClientApi, new Vec3d(0, 64, -20), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            Assert.True((bool)prefix.Invoke(null, new object[] { inFront, EnumRenderStage.Opaque })!);
            Assert.True((bool)prefix.Invoke(null, new object[] { inFront, EnumRenderStage.OIT })!);

            // 3. Conservative 2.5 * max(ScaleX, ScaleY, ScaleZ) radius:
            // At z = -20 with 70 deg vertical FOV (16:9), right frustum plane lies at x ~ 24.9.
            // At x = 30.0 (5.1 blocks past the plane): scale=1 (radius=2.5) is culled, while scale=3 (radius=7.5) stays visible.
            AnimatableRenderer smallOffEdge = CreateRenderer(fakeClientApi, new Vec3d(30.0, 64, -20), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            AnimatableRenderer largeWindmill = CreateRenderer(fakeClientApi, new Vec3d(30.0, 64, -20), scaleX: 3f, scaleY: 1f, scaleZ: 1f);
            Assert.False((bool)prefix.Invoke(null, new object[] { smallOffEdge, EnumRenderStage.Opaque })!);
            Assert.True((bool)prefix.Invoke(null, new object[] { largeWindmill, EnumRenderStage.Opaque })!);

            // 4. Zero allocations across 5,000 off-camera frustum cull checks on real ClientCoreAPI/RenderAPIGame.
            ICoreClientAPI realApi = CreateRealRenderClientApi(culler);
            AnimatableRenderer realBehind = CreateRenderer(realApi, new Vec3d(0, 64, 30), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            var fastPrefix = (System.Func<AnimatableRenderer, EnumRenderStage, bool>)Delegate.CreateDelegate(
                typeof(System.Func<AnimatableRenderer, EnumRenderStage, bool>), prefix);
            fastPrefix(realBehind, EnumRenderStage.Opaque);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                Assert.False(fastPrefix(realBehind, EnumRenderStage.Opaque));
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        [Fact]
        public void AnimationUtil_ParksStoppedInstancesAndWakesOnStartAnimationWithoutMidLoopMutation()
        {
            Type optType = AccessTools.TypeByName("OptiTime.AnimatableOptimization");
            Assert.NotNull(optType);
            AccessTools.Method(optType, "SetClientActive", new[] { typeof(bool) })!.Invoke(null, new object[] { true });

            MethodInfo onRenderTarget = AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.OnRenderFrame),
                new[] { typeof(float), typeof(EnumRenderStage) });
            MethodInfo startTarget = AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.StartAnimation),
                new[] { typeof(AnimationMetaData) });
            MethodInfo disposeTarget = AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.Dispose), Type.EmptyTypes);

            MethodInfo onRenderPostfix = AccessTools.Method(optType, "AnimationUtil_OnRenderFrame_Postfix");
            MethodInfo startPrefix = AccessTools.Method(optType, "AnimationUtil_StartAnimation_Prefix");
            MethodInfo disposePostfix = AccessTools.Method(optType, "AnimationUtil_Dispose_Postfix");
            MethodInfo isParkedMethod = AccessTools.Method(optType, "IsParked", new[] { typeof(AnimationUtil) });
            Assert.NotNull(onRenderPostfix);
            Assert.NotNull(startPrefix);
            Assert.NotNull(disposePostfix);
            Assert.NotNull(isParkedMethod);

            harmony.Patch(onRenderTarget, postfix: new HarmonyMethod(onRenderPostfix));
            harmony.Patch(startTarget, prefix: new HarmonyMethod(startPrefix));
            harmony.Patch(disposeTarget, postfix: new HarmonyMethod(disposePostfix));

            FrustumCulling culler = CreateConfiguredCuller(new BlockPos(0, 64, 0));
            ICoreClientAPI fakeClientApi = CreateFakeClientApi(culler, out FakeClientEventState events);
            int prevMainThread = RuntimeEnv.MainThreadId;
            RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
            try
            {
                var util = (AnimationUtil)RuntimeHelpers.GetUninitializedObject(typeof(AnimationUtil));
                util.activeAnimationsByAnimCode = new Dictionary<string, AnimationMetaData>();
                util.animator = new FakeAnimator();
                util.renderer = CreateRenderer(fakeClientApi, new Vec3d(0, 64, -10), 1f, 1f, 1f);
                UtilCapiField.SetValue(util, fakeClientApi);
                UtilApiField.SetValue(util, fakeClientApi);
                UtilStopTriggeredField.SetValue(util, false);

                // First render frame after animations stop: does NOT mutate renderer list synchronously during OnRenderFrame,
                // instead enqueues a main-thread park task that unregisters from EnumRenderStage.Opaque.
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.Equal(0, events.UnregisterCalls);
                Assert.Single(events.PendingTasks);

                events.RunPendingTasks();
                Assert.Equal(1, events.UnregisterCalls);
                Assert.True((bool)isParkedMethod!.Invoke(null, new object[] { util })!);

                // StartAnimation wakes the parked animator and re-registers on EnumRenderStage.Opaque.
                var meta = new AnimationMetaData { Code = "spin", Animation = "spin" };
                Assert.True(util.StartAnimation(meta));
                Assert.Equal(1, events.RegisterCalls);
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                // While animation is active, OnRenderFrame does NOT park it.
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.Equal(1, events.UnregisterCalls);
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                // Stopping animation parks it again after pending main-thread flush; Dispose clears parked tracking.
                util.StopAnimation("spin");
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.Equal(2, events.UnregisterCalls);
                Assert.True((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                util.Dispose();
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                // Server-side AnimationUtil (capi == null) is untouched by StartAnimation prefix.
                var serverUtil = (AnimationUtil)RuntimeHelpers.GetUninitializedObject(typeof(AnimationUtil));
                serverUtil.activeAnimationsByAnimCode = new Dictionary<string, AnimationMetaData>();
                UtilCapiField.SetValue(serverUtil, null);
                Assert.True(serverUtil.StartAnimation(new AnimationMetaData { Code = "open", Animation = "open" }));
            }
            finally
            {
                RuntimeEnv.MainThreadId = prevMainThread;
            }
        }

        private static AnimatableRenderer CreateRenderer(ICoreClientAPI capi, Vec3d pos, float scaleX, float scaleY, float scaleZ)
        {
            var renderer = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
            RendererPosField.SetValue(renderer, pos);
            RendererCapiField.SetValue(renderer, capi);
            renderer.ScaleX = scaleX;
            renderer.ScaleY = scaleY;
            renderer.ScaleZ = scaleZ;
            renderer.ShouldRender = true;
            return renderer;
        }

        private static FrustumCulling CreateConfiguredCuller(BlockPos playerPos)
        {
            var culler = new FrustumCulling();
            culler.UpdateViewDistance(256);
            double[] proj = Mat4d.Perspective(Mat4d.Create(), 70.0 * Math.PI / 180.0, 16.0 / 9.0, 0.1, 3000.0);
            double[] cam = Mat4d.Create();
            Mat4d.Translate(cam, cam, -playerPos.X, -playerPos.Y, -playerPos.Z);
            culler.CalcFrustumEquations(playerPos, proj, cam);
            return culler;
        }

        private static ICoreClientAPI CreateRealRenderClientApi(FrustumCulling culler)
        {
            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            game.frustumCuller = culler;
            var renderApi = (RenderAPIGame)RuntimeHelpers.GetUninitializedObject(typeof(RenderAPIGame));
            AccessTools.Field(typeof(RenderAPIGame), "game").SetValue(renderApi, game);
            var clientApi = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
            AccessTools.Field(typeof(ClientCoreAPI), "renderapi").SetValue(clientApi, renderApi);
            return clientApi;
        }

        private static ICoreClientAPI CreateFakeClientApi(FrustumCulling culler, out FakeClientEventState eventState)
        {
            eventState = new FakeClientEventState();
            IRenderAPI renderProxy = DispatchProxy.Create<IRenderAPI, FakeRenderApiProxy>();
            ((FakeRenderApiProxy)(object)renderProxy).Culler = culler;

            IClientEventAPI eventProxy = DispatchProxy.Create<IClientEventAPI, FakeEventApiProxy>();
            ((FakeEventApiProxy)(object)eventProxy).State = eventState;

            ICoreClientAPI capiProxy = DispatchProxy.Create<ICoreClientAPI, FakeClientApiProxy>();
            var impl = (FakeClientApiProxy)(object)capiProxy;
            impl.RenderApi = renderProxy;
            impl.EventApi = eventProxy;
            return capiProxy;
        }

        private static void InvokeCleanupIfPresent()
        {
            Type optType = AccessTools.TypeByName("OptiTime.AnimatableOptimization");
            AccessTools.Method(optType, "Cleanup", Type.EmptyTypes)?.Invoke(null, null);
        }

        private sealed class FakeAnimator : AnimatorBase
        {
            public FakeAnimator() : base(() => 1.0, Array.Empty<Animation>(), null)
            {
            }

            public override int MaxJointId => 0;

            public override void OnFrame(Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode, float dt)
            {
            }

            protected override void calculateMatrices(float dt)
            {
            }
        }

        private sealed class FakeClientEventState
        {
            public int RegisterCalls;
            public int UnregisterCalls;
            public readonly Queue<Action> PendingTasks = new Queue<Action>();

            public void RunPendingTasks()
            {
                while (PendingTasks.Count > 0)
                {
                    PendingTasks.Dequeue().Invoke();
                }
            }
        }

        private class FakeClientApiProxy : DispatchProxy
        {
            public IRenderAPI RenderApi;
            public IClientEventAPI EventApi;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    "get_Render" => RenderApi,
                    "get_Event" => EventApi,
                    "get_Side" => EnumAppSide.Client,
                    "get_IsGamePaused" => false,
                    _ => targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null
                };
            }
        }

        private class FakeRenderApiProxy : DispatchProxy
        {
            public FrustumCulling Culler;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_DefaultFrustumCuller")
                {
                    return Culler;
                }

                return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        private class FakeEventApiProxy : DispatchProxy
        {
            public FakeClientEventState State;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "RegisterRenderer":
                        State.RegisterCalls++;
                        return null;
                    case "UnregisterRenderer":
                        State.UnregisterCalls++;
                        return null;
                    case "EnqueueMainThreadTask":
                        if (args.Length > 0 && args[0] is Action action)
                        {
                            State.PendingTasks.Enqueue(action);
                        }
                        return null;
                    default:
                        return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                            ? Activator.CreateInstance(targetMethod.ReturnType)
                            : null;
                }
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
