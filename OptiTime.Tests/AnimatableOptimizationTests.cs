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
using Vintagestory.GameContent;
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
        private static readonly FieldInfo BlockEntityUtilBeField = AccessTools.Field(typeof(BlockEntityAnimationUtil), "be");

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
        public void AnimatableRenderer_SkipsOffCameraNonShadowDrawsAndRespectsMeshBoundsCustomTransformAndNonFiniteValues()
        {
            Type optType = AccessTools.TypeByName("OptiTime.AnimatableOptimization");
            Assert.NotNull(optType);
            MethodInfo prefix = AccessTools.Method(optType, "AnimatableRenderer_OnRenderFrame_Prefix",
                new[] { typeof(AnimatableRenderer), typeof(EnumRenderStage) });
            MethodInfo initPostfix = AccessTools.Method(optType, "AnimatableRenderer_MainThreadInit_Postfix",
                new[] { typeof(AnimatableRenderer), typeof(MeshData), typeof(EnumRenderStage) });
            Assert.NotNull(prefix);
            Assert.NotNull(initPostfix);
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

            // 3. Conservative 2.5 * max(ScaleX, ScaleY, ScaleZ) floor:
            // At z = -20 with 70 deg vertical FOV (16:9), right frustum plane lies at x ~ 24.9.
            AnimatableRenderer smallOffEdge = CreateRenderer(fakeClientApi, new Vec3d(30.0, 64, -20), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            AnimatableRenderer largeWindmill = CreateRenderer(fakeClientApi, new Vec3d(30.0, 64, -20), scaleX: 3f, scaleY: 1f, scaleZ: 1f);
            Assert.False((bool)prefix.Invoke(null, new object[] { smallOffEdge, EnumRenderStage.Opaque })!);
            Assert.True((bool)prefix.Invoke(null, new object[] { largeWindmill, EnumRenderStage.Opaque })!);

            // 4. Tall elevated mesh (Thunderlord pattern: Y in [4.7, 10.0], Scale = 30) placed far below camera
            // so base pos.Y is below bottom frustum plane, but upper body reaches directly into view.
            var tallMesh = CreateBoxMesh(0f, 4.7f, 0f, 1f, 10.0f, 1f);
            AnimatableRenderer thunderlord = CreateRenderer(fakeClientApi, new Vec3d(0, -140, -100), scaleX: 30f, scaleY: 30f, scaleZ: 30f);
            initPostfix.Invoke(null, new object[] { thunderlord, tallMesh, EnumRenderStage.Opaque });
            Assert.True((bool)prefix.Invoke(null, new object[] { thunderlord, EnumRenderStage.Opaque })!);

            // 5. Tall 2x3 gate (Y in [0, 3], X in [0, 2]) near top frustum edge stays visible via mesh-derived bounds.
            var gateMesh = CreateBoxMesh(0f, 0f, 0f, 2f, 3f, 1f);
            AnimatableRenderer tallGate = CreateRenderer(fakeClientApi, new Vec3d(0, 64 - 16.5, -20), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            initPostfix.Invoke(null, new object[] { tallGate, gateMesh, EnumRenderStage.Opaque });
            Assert.True((bool)prefix.Invoke(null, new object[] { tallGate, EnumRenderStage.Opaque })!);

            // 6. CustomTransform != null and NaN/Infinity Scale or pos fail open (never falsely culled).
            AnimatableRenderer customTransformed = CreateRenderer(fakeClientApi, new Vec3d(0, 64, 30), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            customTransformed.CustomTransform = Mat4f.Create();
            Assert.True((bool)prefix.Invoke(null, new object[] { customTransformed, EnumRenderStage.Opaque })!);

            AnimatableRenderer nanScale = CreateRenderer(fakeClientApi, new Vec3d(0, 64, 30), scaleX: float.NaN, scaleY: 1f, scaleZ: 1f);
            Assert.True((bool)prefix.Invoke(null, new object[] { nanScale, EnumRenderStage.Opaque })!);

            AnimatableRenderer infPos = CreateRenderer(fakeClientApi, new Vec3d(double.PositiveInfinity, 64, 30), scaleX: 1f, scaleY: 1f, scaleZ: 1f);
            Assert.True((bool)prefix.Invoke(null, new object[] { infPos, EnumRenderStage.Opaque })!);

            // 7. Zero allocations across 5,000 off-camera frustum cull checks on real ClientCoreAPI/RenderAPIGame.
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

            PatchUtilHooks(optType);
            MethodInfo isParkedMethod = AccessTools.Method(optType, "IsParked", new[] { typeof(AnimationUtil) });

            FrustumCulling culler = CreateConfiguredCuller(new BlockPos(0, 64, 0));
            ICoreClientAPI fakeClientApi = CreateFakeClientApi(culler, out FakeClientEventState events);
            int prevMainThread = RuntimeEnv.MainThreadId;
            RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
            try
            {
                AnimationUtil util = CreateAnimationUtil(typeof(AnimationUtil), fakeClientApi, events, new Vec3d(0, 64, -10), shouldRender: true, registerRenderer: false);

                // First render frame after animations stop: does NOT mutate renderer list synchronously during OnRenderFrame,
                // instead enqueues a single batched main-thread drain task.
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.Equal(1, events.CountRegistered(util, EnumRenderStage.Opaque));
                Assert.Single(events.PendingTasks);

                events.RunPendingTasks();
                Assert.Equal(0, events.CountRegistered(util, EnumRenderStage.Opaque));
                Assert.True((bool)isParkedMethod!.Invoke(null, new object[] { util })!);

                // StartAnimation wakes the parked animator and re-registers exactly once on EnumRenderStage.Opaque.
                var meta = new AnimationMetaData { Code = "spin", Animation = "spin" };
                Assert.True(util.StartAnimation(meta));
                Assert.Equal(1, events.CountRegistered(util, EnumRenderStage.Opaque));
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                // While animation is active, OnRenderFrame does NOT park it.
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.Equal(1, events.CountRegistered(util, EnumRenderStage.Opaque));
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                // Queued park cancelled by StartAnimation -> StopAnimation before drain does NOT park while ShouldRender && !stopRenderTriggered.
                util.StopAnimation("spin");
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.Single(events.PendingTasks);
                Assert.True(util.StartAnimation(new AnimationMetaData { Code = "spin2", Animation = "spin2" }));
                util.StopAnimation("spin2");
                events.RunPendingTasks();
                Assert.Equal(1, events.CountRegistered(util, EnumRenderStage.Opaque));
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                // Stopping animation cleanly parks it again after drain; Dispose keeps tombstone and prevents wake.
                util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.Equal(0, events.CountRegistered(util, EnumRenderStage.Opaque));
                Assert.True((bool)isParkedMethod.Invoke(null, new object[] { util })!);

                util.Dispose();
                Assert.True(util.StartAnimation(new AnimationMetaData { Code = "afterdispose", Animation = "afterdispose" }));
                Assert.Equal(0, events.CountRegistered(util, EnumRenderStage.Opaque));

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

        [Fact]
        public void IdleBlockEntityRenderer_ParksAcrossExactStagesInBatchedQueueAndProtectsModAndConcurrencyInvariants()
        {
            Type optType = AccessTools.TypeByName("OptiTime.AnimatableOptimization");
            Assert.NotNull(optType);
            AccessTools.Method(optType, "SetClientActive", new[] { typeof(bool) })!.Invoke(null, new object[] { true });
            AccessTools.Method(optType, "SetRendererParkingEnabled", new[] { typeof(bool) })!.Invoke(null, new object[] { true });

            PatchUtilHooks(optType);
            MethodInfo rendererPrefix = AccessTools.Method(optType, "AnimatableRenderer_OnRenderFrame_Prefix",
                new[] { typeof(AnimatableRenderer), typeof(EnumRenderStage) });
            MethodInfo rendererInitPostfix = AccessTools.Method(optType, "AnimatableRenderer_MainThreadInit_Postfix",
                new[] { typeof(AnimatableRenderer), typeof(MeshData), typeof(EnumRenderStage) });
            MethodInfo rendererDisposePostfix = AccessTools.Method(optType, "AnimatableRenderer_Dispose_Postfix",
                new[] { typeof(AnimatableRenderer) });
            MethodInfo isParkedMethod = AccessTools.Method(optType, "IsParked", new[] { typeof(AnimationUtil) });
            MethodInfo isRendererParkedMethod = AccessTools.Method(optType, "IsRendererParked", new[] { typeof(AnimatableRenderer) });

            FrustumCulling culler = CreateConfiguredCuller(new BlockPos(0, 64, 0));
            ICoreClientAPI fakeClientApi = CreateFakeClientApi(culler, out FakeClientEventState events);
            int prevMainThread = RuntimeEnv.MainThreadId;
            RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
            try
            {
                // 1. Batch of 20 idle BlockEntityAnimationUtil instances (half transparent OIT, one custom AfterOIT stage)
                // enqueues ONE batched main-thread task and unregisters exact stages with zero duplicates.
                var utils = new BlockEntityAnimationUtil[20];
                for (int i = 0; i < utils.Length; i++)
                {
                    EnumRenderStage stage = i == 3 ? EnumRenderStage.AfterOIT : EnumRenderStage.Opaque;
                    bool hasOit = i % 2 == 0;
                    var util = (BlockEntityAnimationUtil)CreateAnimationUtil(
                        typeof(BlockEntityAnimationUtil), fakeClientApi, events, new Vec3d(i, 64, -10),
                        shouldRender: false, registerRenderer: true, renderStage: stage, hasOit: hasOit);
                    rendererInitPostfix!.Invoke(null, new object[] { util.renderer, CreateBoxMesh(0, 0, 0, 1, 1, 1), stage });
                    utils[i] = util;
                    util.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                }

                Assert.Single(events.PendingTasks);
                Action firstUtilDrainDelegate = events.PendingTasks.Peek();
                Assert.Equal(1, events.CountRegistered(utils[0].renderer, EnumRenderStage.Opaque));

                events.RunPendingTasks();
                for (int i = 0; i < utils.Length; i++)
                {
                    EnumRenderStage stage = i == 3 ? EnumRenderStage.AfterOIT : EnumRenderStage.Opaque;
                    Assert.True((bool)isParkedMethod!.Invoke(null, new object[] { utils[i] })!);
                    Assert.True((bool)isRendererParkedMethod!.Invoke(null, new object[] { utils[i].renderer })!);
                    Assert.Equal(0, events.CountRegistered(utils[i], EnumRenderStage.Opaque));
                    Assert.Equal(0, events.CountRegistered(utils[i].renderer, stage));
                    Assert.Equal(0, events.CountRegistered(utils[i].renderer, EnumRenderStage.OIT));
                    Assert.Equal(0, events.CountRegistered(utils[i].renderer, EnumRenderStage.ShadowFar));
                    Assert.Equal(0, events.CountRegistered(utils[i].renderer, EnumRenderStage.ShadowNear));
                }

                // 2. Waking custom-stage (AfterOIT) BlockEntityAnimationUtil restores exact stage (not Opaque).
                Assert.True(utils[3].StartAnimation(new AnimationMetaData { Code = "open", Animation = "open" }));
                Assert.Equal(1, events.CountRegistered(utils[3], EnumRenderStage.Opaque));
                Assert.Equal(1, events.CountRegistered(utils[3].renderer, EnumRenderStage.AfterOIT));
                Assert.Equal(0, events.CountRegistered(utils[3].renderer, EnumRenderStage.Opaque));
                Assert.Equal(1, events.CountRegistered(utils[3].renderer, EnumRenderStage.ShadowFar));
                Assert.Equal(1, events.CountRegistered(utils[3].renderer, EnumRenderStage.ShadowNear));

                // 3. Delayed MarkBlockDirty transition: stop animation while ShouldRender == true (zero flicker),
                // util parks reusing exact cached delegate, renderer stays registered until ShouldRender flips to false.
                utils[3].renderer.ShouldRender = true;
                utils[3].StopAnimation("open");
                utils[3].OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                Assert.Single(events.PendingTasks);
                Assert.Same(firstUtilDrainDelegate, events.PendingTasks.Peek());
                events.RunPendingTasks();
                Assert.True((bool)isParkedMethod!.Invoke(null, new object[] { utils[3] })!);
                Assert.False((bool)isRendererParkedMethod!.Invoke(null, new object[] { utils[3].renderer })!);
                Assert.Equal(1, events.CountRegistered(utils[3].renderer, EnumRenderStage.AfterOIT));

                utils[3].renderer.ShouldRender = false;
                Assert.False((bool)rendererPrefix!.Invoke(null, new object[] { utils[3].renderer, EnumRenderStage.AfterOIT })!);
                Assert.Single(events.PendingTasks);
                events.RunPendingTasks();
                Assert.True((bool)isRendererParkedMethod.Invoke(null, new object[] { utils[3].renderer })!);
                Assert.Equal(0, events.CountRegistered(utils[3].renderer, EnumRenderStage.AfterOIT));

                // 4. Uninitialized off-thread AnimatableRenderer (mainThreadInit NOT called yet -> stageRecorded == false)
                // is NEVER parked and NEVER double-registered when StartAnimation fires before mainThreadInit.
                var uninitUtil = (BlockEntityAnimationUtil)CreateAnimationUtil(
                    typeof(BlockEntityAnimationUtil), fakeClientApi, events, new Vec3d(50, 64, -10),
                    shouldRender: false, registerRenderer: false);
                uninitUtil.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.True((bool)isParkedMethod.Invoke(null, new object[] { uninitUtil })!);
                Assert.False((bool)isRendererParkedMethod.Invoke(null, new object[] { uninitUtil.renderer })!);
                Assert.True(uninitUtil.StartAnimation(new AnimationMetaData { Code = "lateinit", Animation = "lateinit" }));
                Assert.Equal(0, events.CountRegistered(uninitUtil.renderer, EnumRenderStage.Opaque));

                // 5. Plain AnimationUtil (GearRenderer / direct ShouldRender pattern) does NOT park its AnimatableRenderer.
                AnimationUtil plainUtil = CreateAnimationUtil(
                    typeof(AnimationUtil), fakeClientApi, events, new Vec3d(60, 64, -10),
                    shouldRender: false, registerRenderer: true);
                rendererInitPostfix.Invoke(null, new object[] { plainUtil.renderer, CreateBoxMesh(0, 0, 0, 1, 1, 1), EnumRenderStage.Opaque });
                plainUtil.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.True((bool)isParkedMethod.Invoke(null, new object[] { plainUtil })!);
                Assert.False((bool)isRendererParkedMethod.Invoke(null, new object[] { plainUtil.renderer })!);
                Assert.Equal(1, events.CountRegistered(plainUtil.renderer, EnumRenderStage.Opaque));

                // 6. Modded AnimationUtil subclass overriding StartAnimation is NEVER parked.
                var moddedUtil = (ModdedStartAnimationUtil)CreateAnimationUtil(
                    typeof(ModdedStartAnimationUtil), fakeClientApi, events, new Vec3d(70, 64, -10),
                    shouldRender: false, registerRenderer: true);
                rendererInitPostfix.Invoke(null, new object[] { moddedUtil.renderer, CreateBoxMesh(0, 0, 0, 1, 1, 1), EnumRenderStage.Opaque });
                moddedUtil.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.False((bool)isParkedMethod.Invoke(null, new object[] { moddedUtil })!);
                Assert.False((bool)isRendererParkedMethod.Invoke(null, new object[] { moddedUtil.renderer })!);

                // 7. Stage colliding with ShadowFar / OIT+hasOit refuses renderer parking.
                var collidingUtil = (BlockEntityAnimationUtil)CreateAnimationUtil(
                    typeof(BlockEntityAnimationUtil), fakeClientApi, events, new Vec3d(80, 64, -10),
                    shouldRender: false, registerRenderer: true, renderStage: EnumRenderStage.OIT, hasOit: true);
                rendererInitPostfix.Invoke(null, new object[] { collidingUtil.renderer, CreateBoxMesh(0, 0, 0, 1, 1, 1), EnumRenderStage.OIT });
                collidingUtil.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                events.RunPendingTasks();
                Assert.False((bool)isRendererParkedMethod.Invoke(null, new object[] { collidingUtil.renderer })!);

                // 8. Concurrent/duplicate off-thread StartAnimation wakes register each stage EXACTLY ONCE (no duplicates).
                RuntimeEnv.MainThreadId = prevMainThread - 1;
                Assert.True(utils[1].StartAnimation(new AnimationMetaData { Code = "a", Animation = "a" }));
                Assert.True(utils[1].StartAnimation(new AnimationMetaData { Code = "b", Animation = "b" }));
                Assert.Single(events.PendingTasks);
                RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
                events.RunPendingTasks();
                Assert.Equal(1, events.CountRegistered(utils[1], EnumRenderStage.Opaque));
                Assert.Equal(1, events.CountRegistered(utils[1].renderer, EnumRenderStage.Opaque));
                Assert.Equal(1, events.CountRegistered(utils[1].renderer, EnumRenderStage.ShadowFar));
                Assert.Equal(1, events.CountRegistered(utils[1].renderer, EnumRenderStage.ShadowNear));

                // 9. Off-thread AnimatableRenderer.Dispose() sticky tombstone blocks late mainThreadInit resurrection.
                AnimatableRenderer disposedEarly = CreateRenderer(fakeClientApi, new Vec3d(90, 64, -10), 1f, 1f, 1f);
                rendererDisposePostfix!.Invoke(null, new object[] { disposedEarly });
                rendererInitPostfix.Invoke(null, new object[] { disposedEarly, CreateBoxMesh(0, 0, 0, 1, 1, 1), EnumRenderStage.Opaque });
                Assert.False((bool)isRendererParkedMethod.Invoke(null, new object[] { disposedEarly })!);

                // 10. DrainWakes exception recovery: a throwing RegisterRenderer does NOT wedge future off-thread wakes.
                events.ThrowOnNextRegister = true;
                RuntimeEnv.MainThreadId = prevMainThread - 1;
                Assert.True(utils[2].StartAnimation(new AnimationMetaData { Code = "boom", Animation = "boom" }));
                RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
                Assert.Throws<InvalidOperationException>(() => events.RunPendingTasks());

                RuntimeEnv.MainThreadId = prevMainThread - 1;
                Assert.True(utils[4].StartAnimation(new AnimationMetaData { Code = "recover", Animation = "recover" }));
                Assert.Single(events.PendingTasks);
                RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
                events.RunPendingTasks();
                Assert.Equal(1, events.CountRegistered(utils[4], EnumRenderStage.Opaque));
                Assert.Equal(1, events.CountRegistered(utils[4].renderer, EnumRenderStage.Opaque));
            }
            finally
            {
                RuntimeEnv.MainThreadId = prevMainThread;
            }
        }

        private void PatchUtilHooks(Type optType)
        {
            MethodInfo onRenderTarget = AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.OnRenderFrame),
                new[] { typeof(float), typeof(EnumRenderStage) });
            MethodInfo startTarget = AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.StartAnimation),
                new[] { typeof(AnimationMetaData) });
            MethodInfo disposeTarget = AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.Dispose), Type.EmptyTypes);

            harmony.Patch(onRenderTarget, postfix: new HarmonyMethod(AccessTools.Method(optType, "AnimationUtil_OnRenderFrame_Postfix")));
            harmony.Patch(startTarget, prefix: new HarmonyMethod(AccessTools.Method(optType, "AnimationUtil_StartAnimation_Prefix")));
            harmony.Patch(disposeTarget, postfix: new HarmonyMethod(AccessTools.Method(optType, "AnimationUtil_Dispose_Postfix")));
        }

        private static AnimationUtil CreateAnimationUtil(
            Type utilType,
            ICoreClientAPI fakeClientApi,
            FakeClientEventState events,
            Vec3d pos,
            bool shouldRender,
            bool registerRenderer,
            EnumRenderStage renderStage = EnumRenderStage.Opaque,
            bool hasOit = false)
        {
            var util = (AnimationUtil)RuntimeHelpers.GetUninitializedObject(utilType);
            util.activeAnimationsByAnimCode = new Dictionary<string, AnimationMetaData>();
            util.animator = new FakeAnimator();
            util.renderer = CreateRenderer(fakeClientApi, pos, 1f, 1f, 1f);
            util.renderer.ShouldRender = shouldRender;
            if (hasOit)
            {
                util.renderer.mtmeshrefTransparent = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            }
            UtilCapiField.SetValue(util, fakeClientApi);
            UtilApiField.SetValue(util, fakeClientApi);
            UtilStopTriggeredField.SetValue(util, false);
            if (util is BlockEntityAnimationUtil)
            {
                var dummyBe = (BlockEntity)RuntimeHelpers.GetUninitializedObject(typeof(DummyBlockEntity));
                dummyBe.Pos = new BlockPos((int)pos.X, (int)pos.Y, (int)pos.Z);
                BlockEntityUtilBeField.SetValue(util, dummyBe);
            }

            events.Register(util, EnumRenderStage.Opaque);
            if (registerRenderer)
            {
                if (hasOit)
                {
                    events.Register(util.renderer, EnumRenderStage.OIT);
                }
                events.Register(util.renderer, renderStage);
                events.Register(util.renderer, EnumRenderStage.ShadowFar);
                events.Register(util.renderer, EnumRenderStage.ShadowNear);
            }

            return util;
        }

        private static MeshData CreateBoxMesh(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            var mesh = new MeshData(8, 12);
            mesh.xyz = new[]
            {
                minX, minY, minZ,
                maxX, minY, minZ,
                minX, maxY, minZ,
                maxX, maxY, minZ,
                minX, minY, maxZ,
                maxX, minY, maxZ,
                minX, maxY, maxZ,
                maxX, maxY, maxZ
            };
            mesh.VerticesCount = 8;
            return mesh;
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

            IBlockAccessor blockAccessorProxy = DispatchProxy.Create<IBlockAccessor, FakeNoOpProxy>();
            IClientWorldAccessor worldProxy = DispatchProxy.Create<IClientWorldAccessor, FakeWorldProxy>();
            ((FakeWorldProxy)(object)worldProxy).BlockAccessor = blockAccessorProxy;

            ICoreClientAPI capiProxy = DispatchProxy.Create<ICoreClientAPI, FakeClientApiProxy>();
            var impl = (FakeClientApiProxy)(object)capiProxy;
            impl.RenderApi = renderProxy;
            impl.EventApi = eventProxy;
            impl.WorldAccessor = worldProxy;
            return capiProxy;
        }

        private static void InvokeCleanupIfPresent()
        {
            Type optType = AccessTools.TypeByName("OptiTime.AnimatableOptimization");
            AccessTools.Method(optType, "Cleanup", Type.EmptyTypes)?.Invoke(null, null);
        }

        private sealed class DummyBlockEntity : BlockEntity
        {
        }

        private sealed class ModdedStartAnimationUtil : BlockEntityAnimationUtil
        {
            public ModdedStartAnimationUtil(ICoreAPI api, BlockEntity be) : base(api, be)
            {
            }

            public override bool StartAnimation(AnimationMetaData meta)
            {
                activeAnimationsByAnimCode[meta.Code] = meta;
                return true;
            }
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
            private readonly Dictionary<EnumRenderStage, List<IRenderer>> byStage = new Dictionary<EnumRenderStage, List<IRenderer>>();
            public readonly Queue<Action> PendingTasks = new Queue<Action>();
            public bool ThrowOnNextRegister;

            public void Register(IRenderer renderer, EnumRenderStage stage)
            {
                if (ThrowOnNextRegister)
                {
                    ThrowOnNextRegister = false;
                    throw new InvalidOperationException("Simulated RegisterRenderer conflict");
                }

                if (!byStage.TryGetValue(stage, out List<IRenderer> list))
                {
                    list = new List<IRenderer>();
                    byStage[stage] = list;
                }

                // Mirror vanilla ClientEventManager.RegisterRenderer: does NOT deduplicate.
                list.Add(renderer);
            }

            public void Unregister(IRenderer renderer, EnumRenderStage stage)
            {
                if (byStage.TryGetValue(stage, out List<IRenderer> list))
                {
                    // Mirror vanilla ClientEventManager.UnregisterRenderer: removes first match only.
                    list.Remove(renderer);
                }
            }

            public int CountRegistered(IRenderer renderer, EnumRenderStage stage)
            {
                if (!byStage.TryGetValue(stage, out List<IRenderer> list))
                {
                    return 0;
                }

                int count = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (ReferenceEquals(list[i], renderer))
                    {
                        count++;
                    }
                }

                return count;
            }

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
            public IClientWorldAccessor WorldAccessor;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    "get_Render" => RenderApi,
                    "get_Event" => EventApi,
                    "get_World" => WorldAccessor,
                    "get_Side" => EnumAppSide.Client,
                    "get_IsGamePaused" => false,
                    _ => targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null
                };
            }
        }

        private class FakeWorldProxy : DispatchProxy
        {
            public IBlockAccessor BlockAccessor;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_BlockAccessor")
                {
                    return BlockAccessor;
                }

                return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        private class FakeNoOpProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
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
                        if (args.Length >= 2 && args[0] is IRenderer reg && args[1] is EnumRenderStage regStage)
                        {
                            State.Register(reg, regStage);
                        }
                        return null;
                    case "UnregisterRenderer":
                        if (args.Length >= 2 && args[0] is IRenderer unreg && args[1] is EnumRenderStage unregStage)
                        {
                            State.Unregister(unreg, unregStage);
                        }
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
