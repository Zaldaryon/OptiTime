using System;
using System.IO;
using Vintagestory.API.Client;
using Xunit;

namespace OptiTime.Tests
{
    public sealed class GpuShaderAndShadowOptimizationTests : IDisposable
    {
        public void Dispose()
        {
            ShadowOptimization.Cleanup();
        }

        [Fact]
        public void ShadowOptimizationFailsOpenWhenDisabledOrNullInstance()
        {
            ShadowOptimization.Configure(false, false);
            Assert.False(ShadowOptimization.IsSolidShadowShaderReady);
            Assert.True(ShadowOptimization.RenderShadow_Prefix(null, 0.016f));

            ShadowOptimization.Configure(true, true);
            Assert.False(ShadowOptimization.IsSolidShadowShaderReady);
            Assert.True(ShadowOptimization.RenderShadow_Prefix(null, 0.016f));
        }

        [Fact]
        public void ShadowOptimizationVegetationCullOnlyTargetsShadowFar()
        {
            Assert.True(ShadowOptimization.ShouldCullVegetation((int)EnumRenderStage.ShadowFar));
            Assert.False(ShadowOptimization.ShouldCullVegetation((int)EnumRenderStage.ShadowNear));
            Assert.False(ShadowOptimization.ShouldCullVegetation((int)EnumRenderStage.Opaque));
        }

        [Fact]
        public void ShadowOptimizationRebuildReturnsFalseWithoutClientShaderApi()
        {
            ShadowOptimization.Configure(true, true);
            Assert.False(ShadowOptimization.RebuildSolidShadowShader(null));
            Assert.False(ShadowOptimization.IsSolidShadowShaderReady);
        }

        [Fact]
        public void PostProcessShadersAvoidRedundantWorkAndUseLfLineEndings()
        {
            string root = FindRepositoryRoot();
            string godrays = File.ReadAllText(Path.Combine(root, "assets", "game", "shaders", "godrays.fsh"));
            string ssao = File.ReadAllText(Path.Combine(root, "assets", "game", "shaders", "ssao.fsh"));
            string bilateral = File.ReadAllText(Path.Combine(root, "assets", "game", "shaders", "bilateralblur.fsh"));

            Assert.DoesNotContain("\r\n", godrays);
            Assert.DoesNotContain("\r\n", ssao);
            Assert.DoesNotContain("\r\n", bilateral);

            Assert.Contains("if (g > 0.0)", godrays);
            Assert.Contains("invOneMinusDecay", godrays);
            Assert.Contains("atCorner || zeroDelta", godrays);

            int gPosIdx = ssao.IndexOf("texture(gPosition, texcoord)", StringComparison.Ordinal);
            int skyReturnIdx = ssao.IndexOf("fragPos.x == 0.0 && fragPos.z == 0.0", StringComparison.Ordinal);
            int gNormIdx = ssao.IndexOf("texture(gNormal, texcoord)", StringComparison.Ordinal);
            int revealIdx = ssao.IndexOf("texture(revealage, texcoord)", StringComparison.Ordinal);
            Assert.True(gPosIdx > 0 && skyReturnIdx > gPosIdx && gNormIdx > skyReturnIdx && revealIdx > skyReturnIdx);
            Assert.Contains("mat3 TBNr =", ssao);
            Assert.DoesNotContain("projection * vec4(samplePos", ssao);

            Assert.Contains("const float w5 = 0.231613;", bilateral);
            Assert.Contains("float outOcc = texture(inputTexture", bilateral);
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OptiTime.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not find the OptiTime repository root.");
        }
    }
}
