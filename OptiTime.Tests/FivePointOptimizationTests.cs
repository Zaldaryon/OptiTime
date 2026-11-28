using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class FivePointOptimizationTests : IDisposable
    {
        static FivePointOptimizationTests()
        {
            string gameDir = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                gameDir = Path.Combine(home, ".local", "share", "vintagestory");
            }

            if (!Directory.Exists(gameDir))
            {
                return;
            }

            string capturedDir = gameDir;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string dll = new AssemblyName(args.Name).Name + ".dll";
                foreach (string sub in new[] { "", "Lib", "Mods" })
                {
                    string path = string.IsNullOrEmpty(sub) ? Path.Combine(capturedDir, dll) : Path.Combine(capturedDir, sub, dll);
                    if (File.Exists(path))
                    {
                        return Assembly.LoadFrom(path);
                    }
                }

                return null;
            };
        }

        public void Dispose()
        {
            ShadowPassTransitionOptimization.Cleanup();
            OpaqueShaderOptimization.Cleanup();
            RenderLoopAllocationOptimization.Cleanup();
        }

        [Fact]
        public void ShadowPassTransitionOnlySkipsBounceWhenShadowNearFollowsAndNoOtherListener()
        {
            Assert.True(ShadowPassTransitionOptimization.ShouldSkipPrimaryBounce(2, 0.5f, 1, 1));
            Assert.False(ShadowPassTransitionOptimization.ShouldSkipPrimaryBounce(1, 0.5f, 1, 1));
            Assert.False(ShadowPassTransitionOptimization.ShouldSkipPrimaryBounce(2, 0.005f, 1, 1));
            Assert.False(ShadowPassTransitionOptimization.ShouldSkipPrimaryBounce(2, 0.5f, 2, 1));
            Assert.False(ShadowPassTransitionOptimization.ShouldSkipPrimaryBounce(2, 0.5f, 1, 0));

            ShadowPassTransitionOptimization.Configure(false);
            Assert.True(ShadowPassTransitionOptimization.OnRenderShadowFarDone_Prefix(null, 0.016f));
        }

        [Fact]
        public void OpaqueShaderOptimizationGuardsEffectsAndStripsDeadIncludes()
        {
            Assert.True(OpaqueShaderOptimization.CanUseLeanShaders(0f, 0f, 0, false));
            Assert.False(OpaqueShaderOptimization.CanUseLeanShaders(0.1f, 0f, 0, false));
            Assert.False(OpaqueShaderOptimization.CanUseLeanShaders(0f, 0.1f, 0, false));
            Assert.False(OpaqueShaderOptimization.CanUseLeanShaders(0f, 0f, 1, false));
            Assert.False(OpaqueShaderOptimization.CanUseLeanShaders(0f, 0f, 0, true));

            Assert.True(OpaqueShaderOptimization.CanUseDryVariant(0f, false));
            Assert.False(OpaqueShaderOptimization.CanUseDryVariant(0.5f, false));
            Assert.False(OpaqueShaderOptimization.CanUseDryVariant(0f, true));

            string wetSrc = OpaqueShaderOptimization.BuildLeanFragmentSource(includeUnderwater: true);
            string drySrc = OpaqueShaderOptimization.BuildLeanFragmentSource(includeUnderwater: false);

            Assert.DoesNotContain("skycolor.fsh", wetSrc);
            Assert.DoesNotContain("applyPsychedelicEffect", wetSrc);
            Assert.DoesNotContain("applyRustEffect", wetSrc);
            Assert.DoesNotContain("applySpheresFog", wetSrc);
            Assert.Contains("underwatereffects.fsh", wetSrc);
            Assert.Contains("ReflectiveBitMask", wetSrc);

            Assert.DoesNotContain("underwatereffects.fsh", drySrc);
            Assert.DoesNotContain("getUnderwaterMurkiness", drySrc);
        }

        [Fact]
        public void RenderLoopAllocationOptimizationGlTranslateMatchesMat4dWithZeroAllocations()
        {
            RenderLoopAllocationOptimization.Configure(true);

            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            game.MvMatrix = new(4);
            game.PMatrix = new(4);
            game.MvMatrix.PushIdentity();
            game.PMatrix.PushIdentity();
            game.CurrentMatrixModeProjection = false;

            double[] expected = Mat4d.Create();
            Mat4d.Scale(expected, expected, new double[] { 2.0, 3.0, 4.0 });
            Array.Copy(expected, game.MvMatrix.Top, 16);

            Mat4d.Translate(expected, expected, new double[] { 5.5, -2.25, 10.75 });

            // Warm up JIT.
            Assert.False(RenderLoopAllocationOptimization.GlTranslate_Prefix(game, 5.5, -2.25, 10.75));
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(expected[i], game.MvMatrix.Top[i], 10);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.False(RenderLoopAllocationOptimization.GlTranslate_Prefix(game, 1.0, 2.0, 3.0));
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        [Fact]
        public void RenderLoopAllocationOptimizationHelpersAndTranspilersWorkWithZeroAllocations()
        {
            var dict = new Dictionary<string, AnimationMetaData>
            {
                ["walk"] = new AnimationMetaData { AdjustCollisionBox = false },
                ["sit"] = new AnimationMetaData { AdjustCollisionBox = true }
            };
            Assert.True(RenderLoopAllocationOptimization.HasAdjustCollisionBox(dict));
            dict["sit"].AdjustCollisionBox = false;
            Assert.False(RenderLoopAllocationOptimization.HasAdjustCollisionBox(dict));

            // Warm up one-time ThreadStatic scratch allocation before measuring steady-state zero allocations.
            _ = RenderLoopAllocationOptimization.GetResetScratchQuaternion();

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool hasAdjust = RenderLoopAllocationOptimization.HasAdjustCollisionBox(dict);
            double[] q = RenderLoopAllocationOptimization.GetResetScratchQuaternion();
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.False(hasAdjust);
            Assert.Equal(1.0, q[3]);
            Assert.Equal(0L, after - before);

            // Verify transpilers modify their exact targets cleanly.
            MethodInfo onClientFrame = AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame), new[] { typeof(float) });
            List<CodeInstruction> animTranspiled = RenderLoopAllocationOptimization.AnimationManagerOnClientFrame_Transpiler(
                PatchProcessor.GetOriginalInstructions(onClientFrame)).ToList();
            Assert.Contains(animTranspiled, ci => ci.operand is MethodInfo mi && mi.Name == nameof(RenderLoopAllocationOptimization.HasAdjustCollisionBox));

            MethodInfo loadModel = AccessTools.Method("Vintagestory.GameContent.EntityShapeRenderer:loadModelMatrix",
                new[] { typeof(Vintagestory.API.Common.Entities.Entity), typeof(float), typeof(bool) });
            List<CodeInstruction> modelTranspiled = RenderLoopAllocationOptimization.LoadModelMatrix_Transpiler(
                PatchProcessor.GetOriginalInstructions(loadModel)).ToList();
            Assert.Contains(modelTranspiled, ci => ci.operand is MethodInfo mi && mi.Name == nameof(RenderLoopAllocationOptimization.GetResetScratchQuaternion));

            MethodInfo renderPost = AccessTools.Method(typeof(ClientPlatformWindows), "RenderPostprocessingEffects", new[] { typeof(float[]) });
            List<CodeInstruction> postTranspiled = RenderLoopAllocationOptimization.RenderPostprocessingEffectsClear_Transpiler(
                PatchProcessor.GetOriginalInstructions(renderPost)).ToList();
            Assert.Contains(postTranspiled, ci => ci.operand is FieldInfo fi && fi.Name == "ClearAllOne");

            MethodInfo renderFinal = AccessTools.Method(typeof(ClientPlatformWindows), "RenderFinalComposition", Type.EmptyTypes);
            List<CodeInstruction> finalTranspiled = RenderLoopAllocationOptimization.RenderFinalComposition_Transpiler(
                PatchProcessor.GetOriginalInstructions(renderFinal)).ToList();
            Assert.Contains(finalTranspiled, ci => ci.operand is FieldInfo fi && fi.Name == "SingleColorAttachment0");
        }
    }
}
