using HarmonyLib;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public static class ShadowOptimization
    {
        private const string SolidShadowProgramName = "optitime_chunkshadowmap_solid";

        private const string SolidShadowFragmentSource =
            "#version 330 core\n" +
            "void main()\n" +
            "{\n" +
            "}\n";

        private static readonly AccessTools.FieldRef<ChunkRenderer, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ChunkRenderer, ClientMain>("game");

        private static readonly AccessTools.FieldRef<ChunkRenderer, ClientPlatformAbstract> platformRef =
            AccessTools.FieldRefAccess<ChunkRenderer, ClientPlatformAbstract>("platform");

        private static readonly AccessTools.FieldRef<ChunkRenderer, float> subPixelPaddingXRef =
            AccessTools.FieldRefAccess<ChunkRenderer, float>("subPixelPaddingX");

        private static readonly AccessTools.FieldRef<ChunkRenderer, float> subPixelPaddingYRef =
            AccessTools.FieldRefAccess<ChunkRenderer, float>("subPixelPaddingY");

        private static readonly AccessTools.FieldRef<ClientMain, EnumRenderStage> currentRenderStageRef =
            AccessTools.FieldRefAccess<ClientMain, EnumRenderStage>("currentRenderStage");

        private static readonly AccessTools.FieldRef<ClientMain, float[]> shadowMvpMatrixRef =
            AccessTools.FieldRefAccess<ClientMain, float[]>("shadowMvpMatrix");

        private static Action<string> logger;
        private static bool farVegetationCullEnabled;
        private static bool solidDepthPassEnabled;
        private static ShaderProgram solidShadowProgram;

        public static void SetLogger(Action<string> log) => logger = log;

        public static void Configure(bool farVegetationCull, bool solidDepthPass)
        {
            farVegetationCullEnabled = farVegetationCull;
            solidDepthPassEnabled = solidDepthPass;
        }

        public static bool IsSolidShadowShaderReady =>
            solidDepthPassEnabled &&
            solidShadowProgram != null &&
            !solidShadowProgram.Disposed &&
            !solidShadowProgram.LoadError &&
            solidShadowProgram.ProgramId > 0;

        /// <summary>
        /// Compiles or recompiles the depth-only shadow shader used for solid terrain passes
        /// (Opaque and TopSoil), cloning Chunkshadowmap's expanded vertex shader so SSBO and
        /// vertex-warping behavior stay identical to vanilla without texture fetches or discard.
        /// </summary>
        public static bool RebuildSolidShadowShader(ICoreClientAPI api)
        {
            DisposeSolidShadowProgram(api);
            if (!solidDepthPassEnabled || api?.Shader == null)
            {
                return false;
            }

            ShaderProgram program = null;
            try
            {
                ShaderProgramChunkshadowmap baseProgram = ShaderPrograms.Chunkshadowmap;
                if (baseProgram == null || baseProgram.Disposed || baseProgram.LoadError)
                {
                    return false;
                }

                string vertexCode = baseProgram.VertexShader?.Code;
                if (string.IsNullOrEmpty(vertexCode))
                {
                    return false;
                }

                program = new ShaderProgram
                {
                    LoadFromFile = false,
                    Oit = false,
                    VertexShader = new Shader(EnumShaderType.VertexShader, vertexCode, "chunkshadowmapsolid.vsh"),
                    FragmentShader = new Shader(EnumShaderType.FragmentShader, SolidShadowFragmentSource, "chunkshadowmapsolid.fsh")
                };

                if (baseProgram.includes != null)
                {
                    foreach (string inc in baseProgram.includes)
                    {
                        program.includes.Add(inc);
                    }
                }

                api.Shader.RegisterMemoryShaderProgram(SolidShadowProgramName, program);
                if (!program.Compile() || program.LoadError || program.ProgramId <= 0)
                {
                    TryDisposeProgram(program);
                    logger?.Invoke("[OptiTime] Solid shadow depth shader failed to compile; falling back to vanilla shadow shader");
                    return false;
                }

                solidShadowProgram = program;
                return true;
            }
            catch (Exception ex)
            {
                TryDisposeProgram(program);
                solidShadowProgram = null;
                logger?.Invoke("[OptiTime] Solid shadow depth shader setup failed: " + ex.Message);
                return false;
            }
        }

        public static bool ShouldCullVegetation(int currentRenderStage)
        {
            bool cull = currentRenderStage == (int)EnumRenderStage.ShadowFar;
            ModuleShadowVeg.OnShadowFrame(cull);
            return cull;
        }

        public static bool RenderShadow_Prefix(ChunkRenderer __instance, float dt)
        {
            if (!farVegetationCullEnabled && !solidDepthPassEnabled)
            {
                return true;
            }

            if (__instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            ClientPlatformAbstract platform = platformRef(__instance);
            ShaderProgramChunkshadowmap chunkshadowmap = ShaderPrograms.Chunkshadowmap;
            int[] textureIds = __instance.textureIds;
            MeshDataPoolManager[][] poolsByRenderPass = __instance.poolsByRenderPass;

            if (game?.EntityPlayer == null || platform == null || chunkshadowmap == null || textureIds == null || poolsByRenderPass == null)
            {
                return true;
            }

            if (ShaderProgramBase.CurrentShaderProgram != chunkshadowmap)
            {
                return true;
            }

            float subPixelPaddingX = subPixelPaddingXRef(__instance);
            float subPixelPaddingY = subPixelPaddingYRef(__instance);
            EnumRenderStage currentRenderStage = currentRenderStageRef(game);
            float[] shadowMvpMatrix = shadowMvpMatrixRef(game);
            Vec3d cameraPos = game.EntityPlayer.CameraPos;

            ScreenManager.FrameProfiler.Mark("rend3D-rets-begin");
            platform.GlDepthMask(flag: true);
            platform.GlToggleBlend(on: false);
            platform.GlEnableDepthTest();
            platform.GlDisableCullFace();

            EnumFrustumCullMode frustumCullMode = currentRenderStage == EnumRenderStage.ShadowFar
                ? EnumFrustumCullMode.CullInstantShadowPassFar
                : EnumFrustumCullMode.CullInstantShadowPassNear;

            ShaderProgram solidProgram = solidShadowProgram;
            bool useSolidDepth = solidDepthPassEnabled &&
                shadowMvpMatrix != null &&
                solidProgram != null &&
                !solidProgram.Disposed &&
                !solidProgram.LoadError &&
                solidProgram.ProgramId > 0;

            if (useSolidDepth)
            {
                chunkshadowmap.Stop();
                try
                {
                    solidProgram.Use();
                    solidProgram.UniformMatrix("mvpMatrix", shadowMvpMatrix);

                    MeshDataPoolManager[] opaquePools = poolsByRenderPass[0];
                    for (int i = 0; i < textureIds.Length; i++)
                    {
                        opaquePools[i].Render(cameraPos, "origin", frustumCullMode);
                    }
                    ScreenManager.FrameProfiler.Mark("rend3D-rets-op");

                    MeshDataPoolManager[] topSoilPools = poolsByRenderPass[5];
                    for (int j = 0; j < textureIds.Length; j++)
                    {
                        topSoilPools[j].Render(cameraPos, "origin", frustumCullMode);
                    }
                    ScreenManager.FrameProfiler.Mark("rend3D-rets-tpp");
                }
                finally
                {
                    if (ShaderProgramBase.CurrentShaderProgram == solidProgram)
                    {
                        solidProgram.Stop();
                    }

                    if (ShaderProgramBase.CurrentShaderProgram != chunkshadowmap && !chunkshadowmap.Disposed)
                    {
                        chunkshadowmap.Use();
                    }

                    if (!chunkshadowmap.Disposed)
                    {
                        chunkshadowmap.MvpMatrix = shadowMvpMatrix;
                        chunkshadowmap.Uniform("subpixelPaddingX", subPixelPaddingX);
                        chunkshadowmap.Uniform("subpixelPaddingY", subPixelPaddingY);
                    }
                }
            }
            else
            {
                chunkshadowmap.Uniform("subpixelPaddingX", subPixelPaddingX);
                chunkshadowmap.Uniform("subpixelPaddingY", subPixelPaddingY);

                MeshDataPoolManager[] opaquePools = poolsByRenderPass[0];
                for (int i = 0; i < textureIds.Length; i++)
                {
                    chunkshadowmap.Tex2d2D = textureIds[i];
                    opaquePools[i].Render(cameraPos, "origin", frustumCullMode);
                }
                ScreenManager.FrameProfiler.Mark("rend3D-rets-op");

                MeshDataPoolManager[] topSoilPools = poolsByRenderPass[5];
                for (int j = 0; j < textureIds.Length; j++)
                {
                    chunkshadowmap.Tex2d2D = textureIds[j];
                    topSoilPools[j].Render(cameraPos, "origin", frustumCullMode);
                }
                ScreenManager.FrameProfiler.Mark("rend3D-rets-tpp");
            }

            platform.GlDisableCullFace();

            bool cullVegetation = farVegetationCullEnabled && ShouldCullVegetation((int)currentRenderStage);
            if (!cullVegetation)
            {
                MeshDataPoolManager[] blendNoCullPools = poolsByRenderPass[2];
                for (int k = 0; k < textureIds.Length; k++)
                {
                    chunkshadowmap.Tex2d2D = textureIds[k];
                    blendNoCullPools[k].Render(cameraPos, "origin", frustumCullMode);
                }
            }

            MeshDataPoolManager[] opaqueNoCullPools = poolsByRenderPass[1];
            for (int l = 0; l < textureIds.Length; l++)
            {
                chunkshadowmap.Tex2d2D = textureIds[l];
                opaqueNoCullPools[l].Render(cameraPos, "origin", frustumCullMode);
            }

            platform.GlToggleBlend(on: true);
            return false;
        }

        private static void DisposeSolidShadowProgram(ICoreClientAPI api)
        {
            ShaderProgram existing = solidShadowProgram;
            solidShadowProgram = null;
            TryDisposeProgram(existing);

            if (api?.Shader != null)
            {
                try
                {
                    if (api.Shader.GetProgramByName(SolidShadowProgramName) is ShaderProgram registered &&
                        !ReferenceEquals(registered, existing))
                    {
                        TryDisposeProgram(registered);
                    }
                }
                catch
                {
                }
            }
        }

        private static void TryDisposeProgram(ShaderProgram program)
        {
            if (program == null || program.Disposed)
            {
                return;
            }

            try
            {
                program.Dispose();
            }
            catch
            {
            }
        }

        public static void Cleanup()
        {
            DisposeSolidShadowProgram(null);
            farVegetationCullEnabled = false;
            solidDepthPassEnabled = false;
            logger = null;
        }
    }
}
