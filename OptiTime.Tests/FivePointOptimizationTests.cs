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
            LiquidShaderOptimization.Cleanup();
            LumaAndFinalOptimization.Cleanup();
            ShadowOptimization.Cleanup();
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
            Assert.Contains(finalTranspiled, ci => ci.operand is FieldInfo fi && fi.Name == "FourColorAttachments");
            Assert.Contains(finalTranspiled, ci => ci.operand is FieldInfo fi && fi.Name == "TwoColorAttachments");

            Assert.Contains(modelTranspiled, ci => ci.operand is MethodInfo mi && mi.Name == nameof(RenderLoopAllocationOptimization.ScaleUniform));

            MethodInfo loadFb = AccessTools.Method(typeof(ClientPlatformWindows), "LoadFrameBuffer", new[] { typeof(EnumFrameBuffer) });
            List<CodeInstruction> loadFbTranspiled = RenderLoopAllocationOptimization.LoadFrameBufferTransparent_Transpiler(
                PatchProcessor.GetOriginalInstructions(loadFb)).ToList();
            Assert.Contains(loadFbTranspiled, ci => ci.operand is FieldInfo fi && fi.Name == "ThreeColorAttachments");

            MethodInfo triggerStage = AccessTools.Method(typeof(ClientMain), "TriggerRenderStage", new[] { typeof(EnumRenderStage), typeof(float) });
            List<CodeInstruction> triggerTranspiled = RenderLoopAllocationOptimization.TriggerRenderStage_Transpiler(
                PatchProcessor.GetOriginalInstructions(triggerStage)).ToList();
            Assert.Contains(triggerTranspiled, ci => ci.operand is MethodInfo mi && mi.Name == nameof(RenderLoopAllocationOptimization.GetAfterStageErrorLabel));

            MethodInfo calcFrustum = AccessTools.Method(typeof(FrustumCulling), nameof(FrustumCulling.CalcFrustumEquations),
                new[] { typeof(BlockPos), typeof(double[]), typeof(double[]) });
            List<CodeInstruction> frustumTranspiled = RenderLoopAllocationOptimization.CalcFrustumEquations_Transpiler(
                PatchProcessor.GetOriginalInstructions(calcFrustum)).ToList();
            Assert.Contains(frustumTranspiled, ci => ci.operand is MethodInfo mi && mi.Name == nameof(RenderLoopAllocationOptimization.GetScratchFrustumMatrix));
        }

        [Fact]
        public void NextWaveShadersAndHelpersPreserveInvariantsAndZeroAllocations()
        {
            // TopSoil & Haxy lean shader invariants
            string haxyWet = OpaqueShaderOptimization.BuildLeanHaxyFragmentSource(includeUnderwater: true);
            string haxyDry = OpaqueShaderOptimization.BuildLeanHaxyFragmentSource(includeUnderwater: false);
            Assert.Contains("skycolor.fsh", haxyWet);
            Assert.Contains("getSkyColorAt", haxyWet);
            Assert.DoesNotContain("applyPsychedelicEffect", haxyWet);
            Assert.DoesNotContain("underwatereffects.fsh", haxyDry);

            string topSoilVs = OpaqueShaderOptimization.BuildLeanTopSoilVertexSource();
            string topSoilWet = OpaqueShaderOptimization.BuildLeanTopSoilFragmentSource(includeUnderwater: true);
            string topSoilDry = OpaqueShaderOptimization.BuildLeanTopSoilFragmentSource(includeUnderwater: false);
            Assert.Contains("out float nb;", topSoilVs);
            Assert.Contains("if (nb > minShadowB)", topSoilWet);
            Assert.DoesNotContain("applyPsychedelicEffect", topSoilWet);
            Assert.DoesNotContain("applyRustEffect", topSoilWet);
            Assert.DoesNotContain("underwatereffects.fsh", topSoilDry);

            // Clear-weather liquid shader invariants
            Assert.True(LiquidShaderOptimization.CanUseClearLiquid(0f, 0f, 0));
            Assert.False(LiquidShaderOptimization.CanUseClearLiquid(0.2f, 0f, 0));
            Assert.False(LiquidShaderOptimization.CanUseClearLiquid(0f, 0.1f, 0));
            Assert.False(LiquidShaderOptimization.CanUseClearLiquid(0f, 0f, 1));
            string clearLiquid = LiquidShaderOptimization.BuildClearLiquidFragmentSource();
            Assert.DoesNotContain("dropletnoise", clearLiquid);
            Assert.Contains("getUnderwaterMurkiness", clearLiquid);
            Assert.Contains("texelFetch(depthTex,", clearLiquid);

            // Luma / Final invariants & 8-bit Primary format guard
            Assert.True(LumaAndFinalOptimization.CanUseLeanFinal(0f, 0f, 0f));
            Assert.False(LumaAndFinalOptimization.CanUseLeanFinal(0.1f, 0f, 0f));
            Assert.False(LumaAndFinalOptimization.CanUseLeanFinal(0f, 0.1f, 0f));
            Assert.False(LumaAndFinalOptimization.CanUseLeanFinal(0f, 0f, 0.1f));
            Assert.True(LumaAndFinalOptimization.IsEightBitRgbaFormat((int)OpenTK.Graphics.OpenGL.PixelInternalFormat.Rgba8));
            Assert.True(LumaAndFinalOptimization.IsEightBitRgbaFormat((int)OpenTK.Graphics.OpenGL.PixelInternalFormat.Srgb8Alpha8));
            Assert.False(LumaAndFinalOptimization.IsEightBitRgbaFormat((int)OpenTK.Graphics.OpenGL.PixelInternalFormat.Rgba16f));
            Assert.False(LumaAndFinalOptimization.IsEightBitRgbaFormat((int)OpenTK.Graphics.OpenGL.PixelInternalFormat.Rgba32f));
            string finalGather = LumaAndFinalOptimization.BuildLeanFinalFragmentSource(enableGather4: true);
            Assert.Contains("GL_ARB_gpu_shader5", finalGather);
            Assert.DoesNotContain("gnoise", finalGather);
            Assert.Equal("#define FXAA_GATHER4_ALPHA 1", LumaAndFinalOptimization.ApplyGather4Override("#define FXAA_GATHER4_ALPHA 0", true));

            // Stripped solid shadow VS & Lod 2/3 shadow exclusivity
            string solidVs = ShadowOptimization.BuildSolidShadowVertexSource(includeVertexWarp: true);
            string noWarpVs = ShadowOptimization.BuildSolidShadowVertexSource(includeVertexWarp: false);
            Assert.DoesNotContain("UnpackUv", solidVs);
            Assert.Contains("applyVertexWarping", solidVs);
            Assert.DoesNotContain("applyVertexWarping", noWarpVs);
            Assert.Contains("applyGlobalWarping", noWarpVs);
            Assert.True(ShadowOptimization.PassesShadowLodExclusivity(2, 100.0, 200.0));
            Assert.False(ShadowOptimization.PassesShadowLodExclusivity(2, 300.0, 200.0));
            Assert.False(ShadowOptimization.PassesShadowLodExclusivity(3, 100.0, 200.0));
            Assert.True(ShadowOptimization.PassesShadowLodExclusivity(3, 300.0, 200.0));
            Assert.True(ShadowOptimization.PassesShadowLodExclusivity(0, 999.0, 200.0));

            // ScaleUniform matches Mat4f.Scale and allocates 0 bytes
            float[] expected = Mat4f.Create();
            Mat4f.Translate(expected, expected, 1f, 2f, 3f);
            float[] actual = (float[])expected.Clone();
            Mat4f.Scale(expected, expected, new float[] { 1.5f, 1.5f, 1.5f });
            RenderLoopAllocationOptimization.ScaleUniform(actual, actual, 1.5f);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(expected[i], actual[i], 5);
            }

            // TriggerRenderStage cached label & FrustumCulling zero-alloc check
            string label = RenderLoopAllocationOptimization.GetAfterStageErrorLabel(EnumRenderStage.Opaque);
            Assert.Equal("After render stage Opaque", label);
            long before = GC.GetAllocatedBytesForCurrentThread();
            string label2 = RenderLoopAllocationOptimization.GetAfterStageErrorLabel(EnumRenderStage.Opaque);
            RenderLoopAllocationOptimization.ScaleUniform(actual, actual, 1.0f);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Same(label, label2);
            Assert.Equal(0L, after - before);
        }
    }
}
