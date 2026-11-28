using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Compiles lean memory variants of chunkopaque (Passes 0, 1, 2, 8) and chunktopsoil (Pass 5)
    /// that strip unused compile-time paths (psychedelic, rust glitch, fog spheres, skycolor where
    /// unused, and underwater murkiness when no liquid geometry rendered into LiquidDepth) while
    /// adding normal-brightness shadow early-outs. Falls back transparently to full vanilla shaders
    /// whenever temporal storms, psychedelic effects, fog spheres, or normal view are active.
    /// </summary>
    public static class OpaqueShaderOptimization
    {
        private const string LeanWetProgramName = "optitime_chunkopaque_lean_wet";
        private const string LeanDryProgramName = "optitime_chunkopaque_lean_dry";
        private const string HaxyWetProgramName = "optitime_chunkopaque_haxy_wet";
        private const string HaxyDryProgramName = "optitime_chunkopaque_haxy_dry";
        private const string TopSoilWetProgramName = "optitime_chunktopsoil_lean_wet";
        private const string TopSoilDryProgramName = "optitime_chunktopsoil_lean_dry";

        private static readonly AccessTools.FieldRef<ChunkRenderer, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, ClientPlatformAbstract> platformRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, float> subPixelPaddingXRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, float> subPixelPaddingYRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, Vec2f> blockTextureSizeRef;
        private static readonly AccessTools.FieldRef<DefaultShaderUniforms, float> skyDaylightRef;
        private static readonly AccessTools.FieldRef<ClientMain, bool> unbindSamplersRef;
        private static readonly MethodInfo handleIncludesMethod;
        private static readonly bool accessorsReady;

        private static volatile bool enabled;
        private static volatile bool topSoilEnabled;
        private static volatile bool hasLiquidDepthInView = true;
        private static ShaderProgram leanWetProgram;
        private static ShaderProgram leanDryProgram;
        private static ShaderProgram haxyWetProgram;
        private static ShaderProgram haxyDryProgram;
        private static ShaderProgram topSoilWetProgram;
        private static ShaderProgram topSoilDryProgram;
        private static ILogger logger;

        static OpaqueShaderOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ChunkRenderer, ClientMain>("game");
                platformRef = AccessTools.FieldRefAccess<ChunkRenderer, ClientPlatformAbstract>("platform");
                subPixelPaddingXRef = AccessTools.FieldRefAccess<ChunkRenderer, float>("subPixelPaddingX");
                subPixelPaddingYRef = AccessTools.FieldRefAccess<ChunkRenderer, float>("subPixelPaddingY");
                blockTextureSizeRef = AccessTools.FieldRefAccess<ChunkRenderer, Vec2f>("blockTextureSize");
                skyDaylightRef = AccessTools.FieldRefAccess<DefaultShaderUniforms, float>("SkyDaylight");
                unbindSamplersRef = AccessTools.FieldRefAccess<ClientMain, bool>("unbindSamplers");
                handleIncludesMethod = AccessTools.Method(
                    typeof(ShaderRegistry),
                    "HandleIncludes",
                    new[] { typeof(ShaderProgram), typeof(string), typeof(HashSet<string>) });
                accessorsReady = handleIncludesMethod != null;
            }
            catch
            {
                accessorsReady = false;
            }
        }

        public static bool IsLeanWetReady => IsProgramUsable(leanWetProgram);
        public static bool IsLeanDryReady => IsProgramUsable(leanDryProgram);
        public static bool IsHaxyWetReady => IsProgramUsable(haxyWetProgram);
        public static bool IsHaxyDryReady => IsProgramUsable(haxyDryProgram);
        public static bool IsTopSoilWetReady => IsProgramUsable(topSoilWetProgram);
        public static bool IsTopSoilDryReady => IsProgramUsable(topSoilDryProgram);

        public static void Configure(bool isEnabled, ILogger modLogger = null, bool enableTopSoil = true)
        {
            enabled = isEnabled;
            topSoilEnabled = isEnabled && enableTopSoil;
            if (modLogger != null)
            {
                logger = modLogger;
            }
        }

        public static bool RebuildShaders(ICoreClientAPI api)
        {
            DisposePrograms(api);
            if (!enabled || !accessorsReady || api?.Shader == null)
            {
                return false;
            }

            try
            {
                ShaderProgramChunkopaque baseProgram = ShaderPrograms.Chunkopaque;
                if (baseProgram == null || baseProgram.Disposed || baseProgram.LoadError)
                {
                    return false;
                }

                string vertexCode = baseProgram.VertexShader?.Code;
                if (string.IsNullOrEmpty(vertexCode))
                {
                    return false;
                }

                leanWetProgram = BuildOpaqueVariant(api, baseProgram, vertexCode, LeanWetProgramName, includeUnderwater: true, includeHaxyFade: false);
                leanDryProgram = BuildOpaqueVariant(api, baseProgram, vertexCode, LeanDryProgramName, includeUnderwater: false, includeHaxyFade: false);
                haxyWetProgram = BuildOpaqueVariant(api, baseProgram, vertexCode, HaxyWetProgramName, includeUnderwater: true, includeHaxyFade: true);
                haxyDryProgram = BuildOpaqueVariant(api, baseProgram, vertexCode, HaxyDryProgramName, includeUnderwater: false, includeHaxyFade: true);

                if (topSoilEnabled)
                {
                    ShaderProgramChunktopsoil baseTopSoil = ShaderPrograms.Chunktopsoil;
                    if (baseTopSoil != null && !baseTopSoil.Disposed && !baseTopSoil.LoadError)
                    {
                        topSoilWetProgram = BuildTopSoilVariant(api, TopSoilWetProgramName, includeUnderwater: true);
                        topSoilDryProgram = BuildTopSoilVariant(api, TopSoilDryProgramName, includeUnderwater: false);
                    }
                }

                return IsLeanWetReady;
            }
            catch (Exception ex)
            {
                DisposePrograms(api);
                logger?.Warning("[OptiTime] Lean opaque shader compilation failed; using vanilla chunkopaque: " + ex.Message);
                return false;
            }
        }

        internal static bool CanUseLeanShaders(
            float psychedelicStrength,
            float glitchStrength,
            int fogSphereQuantity,
            bool normalView)
        {
            return !normalView &&
                psychedelicStrength <= 0.001f &&
                glitchStrength <= 0.001f &&
                fogSphereQuantity == 0;
        }

        internal static bool CanUseDryVariant(float cameraUnderwater, bool liquidInView)
        {
            return cameraUnderwater <= 0.001f && !liquidInView;
        }

        public static void OnRenderBefore_Postfix(ChunkRenderer __instance)
        {
            if (!enabled || __instance == null || __instance.poolsByRenderPass == null || __instance.textureIds == null)
            {
                hasLiquidDepthInView = true;
                return;
            }

            MeshDataPoolManager[] liquidPools = __instance.poolsByRenderPass[4];
            if (liquidPools == null)
            {
                hasLiquidDepthInView = true;
                return;
            }

            long usedVram = 0;
            long renderedTris = 0;
            long allocatedTris = 0;
            int count = __instance.textureIds.Length;
            for (int i = 0; i < count; i++)
            {
                MeshDataPoolManager mgr = liquidPools[i];
                if (mgr != null)
                {
                    mgr.GetStats(ref usedVram, ref renderedTris, ref allocatedTris);
                    if (renderedTris > 0)
                    {
                        hasLiquidDepthInView = true;
                        return;
                    }
                }
            }

            hasLiquidDepthInView = false;
        }

        public static bool RenderOpaque_Prefix(ChunkRenderer __instance, float dt)
        {
            if (!enabled || !accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            ClientPlatformAbstract platform = platformRef(__instance);
            if (game == null || platform == null || game.EntityPlayer == null ||
                game.MainCamera == null || game.AmbientManager == null || game.shUniforms == null ||
                __instance.poolsByRenderPass == null || __instance.textureIds == null)
            {
                return true;
            }

            ShaderProgramChunkopaque chunkopaque = ShaderPrograms.Chunkopaque;
            ShaderProgramChunktopsoil chunktopsoil = ShaderPrograms.Chunktopsoil;
            if (chunkopaque == null || chunkopaque.Disposed || chunktopsoil == null || chunktopsoil.Disposed)
            {
                return true;
            }

            DefaultShaderUniforms sh = game.shUniforms;
            if (!CanUseLeanShaders(sh.PsychedelicStrength, sh.GlitchStrength, sh.FogSphereQuantity, ShaderRegistry.NormalView))
            {
                return true;
            }

            bool isDry = CanUseDryVariant(sh.CameraUnderwater, hasLiquidDepthInView);
            ShaderProgram lean = (isDry && IsLeanDryReady) ? leanDryProgram : leanWetProgram;
            if (!IsProgramUsable(lean))
            {
                return true;
            }

            ShaderProgram haxy = (isDry && IsHaxyDryReady)
                ? haxyDryProgram
                : (IsHaxyWetReady ? haxyWetProgram : null);
            ShaderProgram topSoil = topSoilEnabled
                ? ((isDry && IsTopSoilDryReady) ? topSoilDryProgram : (IsTopSoilWetReady ? topSoilWetProgram : null))
                : null;

            float subPixelPaddingX = subPixelPaddingXRef(__instance);
            float subPixelPaddingY = subPixelPaddingYRef(__instance);
            int[] textureIds = __instance.textureIds;
            MeshDataPoolManager[][] poolsByRenderPass = __instance.poolsByRenderPass;

            Vec3d cameraPos = game.EntityPlayer.CameraPos;
            ScreenManager.FrameProfiler.Mark("rend3D-ret-begin");
            platform.GlDepthMask(flag: true);
            platform.GlEnableDepthTest();
            platform.GlToggleBlend(on: true);
            platform.GlEnableCullFace();
            game.GlMatrixModeModelView();
            game.GlPushMatrix();
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);

            // Pass 0: Opaque terrain with lean compile-time shader
            lean.Use();
            try
            {
                BindLeanUniforms(lean, game, alphaTest: 0.001f, subPixelPaddingX, subPixelPaddingY);
                MeshDataPoolManager[] opaquePools = poolsByRenderPass[0];
                for (int i = 0; i < textureIds.Length; i++)
                {
                    lean.BindTexture2D("terrainTex", textureIds[i], 0);
                    lean.BindTexture2D("terrainTexLinear", textureIds[i], 1);
                    opaquePools[i].Render(cameraPos, "origin");
                }
                ScreenManager.FrameProfiler.Mark("rend3D-ret-op");
            }
            finally
            {
                if (ShaderProgramBase.CurrentShaderProgram == lean)
                {
                    lean.Stop();
                }
            }

            // Pass 5: TopSoil (lean variant when ready, else vanilla Chunktopsoil)
            if (topSoil != null)
            {
                topSoil.Use();
                try
                {
                    if (topSoil.HasUniform("rgbaFogIn"))
                        topSoil.Uniform("rgbaFogIn", game.AmbientManager.BlendedFogColor);
                    if (topSoil.HasUniform("rgbaAmbientIn"))
                        topSoil.Uniform("rgbaAmbientIn", game.AmbientManager.BlendedAmbientColor);
                    if (topSoil.HasUniform("fogDensityIn"))
                        topSoil.Uniform("fogDensityIn", game.AmbientManager.BlendedFogDensity);
                    if (topSoil.HasUniform("fogMinIn"))
                        topSoil.Uniform("fogMinIn", game.AmbientManager.BlendedFogMin);
                    topSoil.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
                    topSoil.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
                    if (topSoil.HasUniform("blockTextureSize"))
                        topSoil.Uniform("blockTextureSize", blockTextureSizeRef(__instance));
                    if (topSoil.HasUniform("subpixelPaddingX"))
                        topSoil.Uniform("subpixelPaddingX", subPixelPaddingX);
                    if (topSoil.HasUniform("subpixelPaddingY"))
                        topSoil.Uniform("subpixelPaddingY", subPixelPaddingY);

                    MeshDataPoolManager[] topSoilPools = poolsByRenderPass[5];
                    for (int j = 0; j < textureIds.Length; j++)
                    {
                        topSoil.BindTexture2D("terrainTex", textureIds[j], 0);
                        topSoil.BindTexture2D("terrainTexLinear", textureIds[j], 1);
                        topSoilPools[j].Render(cameraPos, "origin");
                    }
                    ScreenManager.FrameProfiler.Mark("rend3D-ret-tpp");
                }
                finally
                {
                    if (ShaderProgramBase.CurrentShaderProgram == topSoil)
                    {
                        topSoil.Stop();
                    }
                }
            }
            else
            {
                chunktopsoil.Use();
                try
                {
                    chunktopsoil.RgbaFogIn = game.AmbientManager.BlendedFogColor;
                    chunktopsoil.RgbaAmbientIn = game.AmbientManager.BlendedAmbientColor;
                    chunktopsoil.FogDensityIn = game.AmbientManager.BlendedFogDensity;
                    chunktopsoil.FogMinIn = game.AmbientManager.BlendedFogMin;
                    chunktopsoil.ProjectionMatrix = game.CurrentProjectionMatrix;
                    chunktopsoil.ModelViewMatrix = game.CurrentModelViewMatrix;
                    chunktopsoil.BlockTextureSize = blockTextureSizeRef(__instance);
                    chunktopsoil.Uniform("subpixelPaddingX", subPixelPaddingX);
                    chunktopsoil.Uniform("subpixelPaddingY", subPixelPaddingY);
                    MeshDataPoolManager[] topSoilPools = poolsByRenderPass[5];
                    for (int j = 0; j < textureIds.Length; j++)
                    {
                        chunktopsoil.TerrainTex2D = textureIds[j];
                        chunktopsoil.TerrainTexLinear2D = textureIds[j];
                        topSoilPools[j].Render(cameraPos, "origin");
                    }
                    ScreenManager.FrameProfiler.Mark("rend3D-ret-tpp");
                }
                finally
                {
                    if (ShaderProgramBase.CurrentShaderProgram == chunktopsoil)
                    {
                        chunktopsoil.Stop();
                    }
                }
            }

            // Pass 2: BlendNoCull (HaxyFade == 0) with lean compile-time shader
            platform.GlDisableCullFace();
            platform.GlToggleBlend(on: true);
            lean.Use();
            try
            {
                BindLeanUniforms(lean, game, alphaTest: 0.25f, subPixelPaddingX, subPixelPaddingY);
                MeshDataPoolManager[] blendNoCullPools = poolsByRenderPass[2];
                for (int k = 0; k < textureIds.Length; k++)
                {
                    lean.BindTexture2D("terrainTex", textureIds[k], 0);
                    lean.BindTexture2D("terrainTexLinear", textureIds[k], 1);
                    blendNoCullPools[k].Render(cameraPos, "origin");
                }
            }
            finally
            {
                if (ShaderProgramBase.CurrentShaderProgram == lean)
                {
                    lean.Stop();
                }
            }

            // Pass 1: OpaqueNoCull (HaxyFade == 1, foliage sky-fade) with lean haxy variant or vanilla chunkopaque
            platform.GlToggleBlend(on: false);
            if (haxy != null)
            {
                haxy.Use();
                try
                {
                    BindLeanUniforms(haxy, game, alphaTest: 0.42f, subPixelPaddingX, subPixelPaddingY);
                    if (haxy.HasUniform("sunPosition"))
                        haxy.Uniform("sunPosition", game.Calendar.SunPositionNormalized);
                    if (haxy.HasUniform("dayLight"))
                        haxy.Uniform("dayLight", skyDaylightRef(sh));
                    if (haxy.HasUniform("horizonFog"))
                        haxy.Uniform("horizonFog", game.AmbientManager.BlendedCloudDensity);

                    MeshDataPoolManager[] opaqueNoCullPools = poolsByRenderPass[1];
                    for (int l = 0; l < textureIds.Length; l++)
                    {
                        haxy.BindTexture2D("terrainTex", textureIds[l], 0);
                        haxy.BindTexture2D("terrainTexLinear", textureIds[l], 1);
                        opaqueNoCullPools[l].Render(cameraPos, "origin");
                    }
                }
                finally
                {
                    if (ShaderProgramBase.CurrentShaderProgram == haxy)
                    {
                        haxy.Stop();
                    }
                }
            }
            else
            {
                chunkopaque.Use();
                try
                {
                    chunkopaque.CameraUnderwater = sh.CameraUnderwater;
                    chunkopaque.RgbaFogIn = game.AmbientManager.BlendedFogColor;
                    chunkopaque.RgbaAmbientIn = game.AmbientManager.BlendedAmbientColor;
                    chunkopaque.FogDensityIn = game.AmbientManager.BlendedFogDensity;
                    chunkopaque.FogMinIn = game.AmbientManager.BlendedFogMin;
                    chunkopaque.ProjectionMatrix = game.CurrentProjectionMatrix;
                    chunkopaque.ModelViewMatrix = game.CurrentModelViewMatrix;
                    chunkopaque.LiquidDepth2D = platform.FrameBuffers[5].DepthTextureId;
                    chunkopaque.Uniform("subpixelPaddingX", subPixelPaddingX);
                    chunkopaque.Uniform("subpixelPaddingY", subPixelPaddingY);
                    chunkopaque.SunPosition = game.Calendar.SunPositionNormalized;
                    chunkopaque.DayLight = skyDaylightRef(sh);
                    chunkopaque.HorizonFog = game.AmbientManager.BlendedCloudDensity;
                    chunkopaque.AlphaTest = 0.42f;
                    chunkopaque.HaxyFade = 1;

                    MeshDataPoolManager[] opaqueNoCullPools = poolsByRenderPass[1];
                    for (int l = 0; l < textureIds.Length; l++)
                    {
                        chunkopaque.TerrainTex2D = textureIds[l];
                        chunkopaque.TerrainTexLinear2D = textureIds[l];
                        opaqueNoCullPools[l].Render(cameraPos, "origin");
                    }
                }
                finally
                {
                    if (ShaderProgramBase.CurrentShaderProgram == chunkopaque)
                    {
                        chunkopaque.Stop();
                    }
                }
            }

            // Pass 8: Decor (HaxyFade == 0) with lean compile-time shader
            platform.GlEnableCullFace();
            platform.GlToggleBlend(on: true);
            lean.Use();
            try
            {
                BindLeanUniforms(lean, game, alphaTest: 0.2f, subPixelPaddingX, subPixelPaddingY);
                MeshDataPoolManager[] decorPools = poolsByRenderPass[8];
                for (int m = 0; m < textureIds.Length; m++)
                {
                    lean.BindTexture2D("terrainTex", textureIds[m], 0);
                    lean.BindTexture2D("terrainTexLinear", textureIds[m], 1);
                    decorPools[m].Render(cameraPos, "origin");
                }
            }
            finally
            {
                if (ShaderProgramBase.CurrentShaderProgram == lean)
                {
                    lean.Stop();
                }
            }

            platform.GlToggleBlend(on: false);
            ScreenManager.FrameProfiler.Mark("rend3D-ret-opnc");
            game.GlPopMatrix();

            if (unbindSamplersRef(game))
            {
                GL.BindSampler(0, 0);
                GL.BindSampler(1, 0);
                GL.BindSampler(2, 0);
                GL.BindSampler(3, 0);
                GL.BindSampler(4, 0);
                GL.BindSampler(5, 0);
                GL.BindSampler(6, 0);
                GL.BindSampler(7, 0);
                GL.BindSampler(8, 0);
            }

            return false;
        }

        private static void BindLeanUniforms(
            ShaderProgram lean,
            ClientMain game,
            float alphaTest,
            float subPixelPaddingX,
            float subPixelPaddingY)
        {
            if (lean.HasUniform("rgbaFogIn"))
                lean.Uniform("rgbaFogIn", game.AmbientManager.BlendedFogColor);
            if (lean.HasUniform("rgbaAmbientIn"))
                lean.Uniform("rgbaAmbientIn", game.AmbientManager.BlendedAmbientColor);
            if (lean.HasUniform("fogDensityIn"))
                lean.Uniform("fogDensityIn", game.AmbientManager.BlendedFogDensity);
            if (lean.HasUniform("fogMinIn"))
                lean.Uniform("fogMinIn", game.AmbientManager.BlendedFogMin);

            lean.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
            lean.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
            lean.Uniform("alphaTest", alphaTest);

            if (lean.HasUniform("subpixelPaddingX"))
                lean.Uniform("subpixelPaddingX", subPixelPaddingX);
            if (lean.HasUniform("subpixelPaddingY"))
                lean.Uniform("subpixelPaddingY", subPixelPaddingY);
        }

        private static ShaderProgram BuildOpaqueVariant(
            ICoreClientAPI api,
            ShaderProgramChunkopaque baseProgram,
            string vertexCode,
            string programName,
            bool includeUnderwater,
            bool includeHaxyFade)
        {
            if (handleIncludesMethod == null)
            {
                return null;
            }

            string fragSource = includeHaxyFade
                ? BuildLeanHaxyFragmentSource(includeUnderwater)
                : BuildLeanFragmentSource(includeUnderwater);

            string suffix = (includeHaxyFade ? "haxy_" : "") + (includeUnderwater ? "wet" : "dry");
            // Vertex filename MUST start with "chunk" so Shader.UsesSSBOs() promotes #version 430.
            string vshName = "chunkopaque_optitime_" + suffix + ".vsh";
            string fshName = "chunkopaque_optitime_" + suffix + ".fsh";

            var program = new ShaderProgram
            {
                LoadFromFile = false,
                Oit = false
            };

            string expandedFrag = (string)handleIncludesMethod.Invoke(null, new object[] { program, fragSource, null });
            if (string.IsNullOrEmpty(expandedFrag))
            {
                return null;
            }

            program.VertexShader = new Shader(EnumShaderType.VertexShader, vertexCode, vshName);
            program.FragmentShader = new Shader(EnumShaderType.FragmentShader, expandedFrag, fshName);

            if (baseProgram.includes != null)
            {
                foreach (string inc in baseProgram.includes)
                {
                    if (!includeHaxyFade && string.Equals(inc, "skycolor.fsh", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!includeUnderwater && string.Equals(inc, "underwatereffects.fsh", StringComparison.OrdinalIgnoreCase))
                        continue;

                    program.includes.Add(inc);
                }
            }

            api.Shader.RegisterMemoryShaderProgram(programName, program);
            if (!program.Compile() || program.LoadError || program.ProgramId <= 0)
            {
                TryDisposeProgram(program);
                logger?.Warning("[OptiTime] Lean opaque shader '{0}' failed to compile", programName);
                return null;
            }

            program.SetCustomSampler("terrainTex", isLinear: false);
            program.SetCustomSampler("terrainTexLinear", isLinear: true);
            return program;
        }

        private static ShaderProgram BuildTopSoilVariant(
            ICoreClientAPI api,
            string programName,
            bool includeUnderwater)
        {
            if (handleIncludesMethod == null)
            {
                return null;
            }

            string suffix = includeUnderwater ? "wet" : "dry";
            // Vertex filename MUST start with "chunk" so Shader.UsesSSBOs() promotes #version 430.
            string vshName = "chunktopsoil_optitime_" + suffix + ".vsh";
            string fshName = "chunktopsoil_optitime_" + suffix + ".fsh";

            var program = new ShaderProgram
            {
                LoadFromFile = false,
                Oit = false
            };

            string expandedVert = (string)handleIncludesMethod.Invoke(null, new object[] { program, BuildLeanTopSoilVertexSource(), null });
            string expandedFrag = (string)handleIncludesMethod.Invoke(null, new object[] { program, BuildLeanTopSoilFragmentSource(includeUnderwater), null });
            if (string.IsNullOrEmpty(expandedVert) || string.IsNullOrEmpty(expandedFrag))
            {
                return null;
            }

            program.VertexShader = new Shader(EnumShaderType.VertexShader, expandedVert, vshName);
            program.FragmentShader = new Shader(EnumShaderType.FragmentShader, expandedFrag, fshName);

            api.Shader.RegisterMemoryShaderProgram(programName, program);
            if (!program.Compile() || program.LoadError || program.ProgramId <= 0)
            {
                TryDisposeProgram(program);
                logger?.Warning("[OptiTime] Lean topsoil shader '{0}' failed to compile", programName);
                return null;
            }

            program.SetCustomSampler("terrainTex", isLinear: false);
            program.SetCustomSampler("terrainTexLinear", isLinear: true);
            return program;
        }

        internal static string BuildLeanFragmentSource(bool includeUnderwater)
        {
            string underwaterInclude = includeUnderwater ? "#include underwatereffects.fsh\n" : string.Empty;
            string murkDecl = includeUnderwater
                ? "float murkiness = getUnderwaterMurkiness();\n"
                : "const float murkiness = 0.0;\n";
            string underwaterApply = includeUnderwater
                ? "outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);\n"
                : string.Empty;
            string fogWeightExpr = includeUnderwater
                ? "clamp(fogAmount - 50.0 * murkiness, 0.0, 1.0)"
                : "fogAmount";

            return
                "#version 330 core\n" +
                "#extension GL_ARB_explicit_attrib_location: enable\n" +
                "uniform sampler2D terrainTex;\n" +
                "uniform sampler2D terrainTexLinear;\n" +
                "in vec4 rgba;\n" +
                "in vec4 rgbaFog;\n" +
                "in float fogAmount;\n" +
                "in vec2 uv;\n" +
                "in float glowLevel;\n" +
                "flat in int renderFlags;\n" +
                "in vec3 normal;\n" +
                "in vec4 worldPos;\n" +
                "in vec3 vertexPosition;\n" +
                "in vec3 blockLight;\n" +
                "in vec4 gnormal;\n" +
                "in vec4 camPos;\n" +
                "in float lod0Fade;\n" +
                "in float nb;\n" +
                "uniform float alphaTest;\n" +
                "layout(location = 0) out vec4 outColor;\n" +
                "layout(location = 1) out vec4 outGlow;\n" +
                "#if SSAOLEVEL > 0\n" +
                "layout(location = 2) out vec4 outGNormal;\n" +
                "layout(location = 3) out vec4 outGPosition;\n" +
                "#endif\n" +
                "#include vertexflagbits.ash\n" +
                "#include fogandlight.fsh\n" +
                "#include colormap.fsh\n" +
                underwaterInclude +
                "void main()\n" +
                "{\n" +
                "    vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv)) * rgba;\n" +
                "    float aTest = texColor.a + max(0.0, 1.0 - rgba.a) * min(1.0, texColor.a * 10.0) - lod0Fade;\n" +
                "    if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4.0;\n" +
                "    if (aTest < alphaTest || rgba.a < 0.005) discard;\n" +
                "    float b = 1.0;\n" +
                "#if SHADOWQUALITY > 0\n" +
                "    if (shadowIntensity > 0.0001 && blockBrightness < 1.0) {\n" +
                "#if SHADOWQUALITY == 1\n" +
                "        if (shadowCoordsFar.w > 0.0) {\n" +
                "            float minShadowB = clamp(1.0 - shadowIntensity * shadowCoordsFar.w * 0.5 + blockBrightness, 0.0, 1.0);\n" +
                "            bool needShadow = nb > minShadowB;\n" +
                "#if SHINYEFFECT > 0\n" +
                "            needShadow = needShadow || ((renderFlags & ReflectiveBitMask) != 0);\n" +
                "#endif\n" +
                "            if (needShadow) b = getBrightnessFromShadowMap();\n" +
                "        }\n" +
                "#else\n" +
                "        if (shadowCoordsFar.w > 0.0 || shadowCoordsNear.w > 0.0) {\n" +
                "            float minShadowB = clamp(1.0 - shadowIntensity * (shadowCoordsFar.w + shadowCoordsNear.w) * 0.5 + blockBrightness, 0.0, 1.0);\n" +
                "            bool needShadow = nb > minShadowB;\n" +
                "#if SHINYEFFECT > 0\n" +
                "            needShadow = needShadow || ((renderFlags & ReflectiveBitMask) != 0);\n" +
                "#endif\n" +
                "            if (needShadow) b = getBrightnessFromShadowMap();\n" +
                "        }\n" +
                "#endif\n" +
                "    }\n" +
                "#endif\n" +
                "    " + murkDecl +
                "    float finalB = min(b, nb) * (1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5);\n" +
                "    texColor.rgb *= finalB;\n" +
                "    outColor = applyFog(texColor, " + fogWeightExpr + ");\n" +
                "    " + underwaterApply +
                "    float glow = 0.0;\n" +
                "#if SHINYEFFECT > 0\n" +
                "    if ((renderFlags & ReflectiveBitMask) != 0) {\n" +
                "        outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2.0 * fogAmount + 2.0 * (1.0 - b), 0.0, 1.0));\n" +
                "    }\n" +
                "    glow += pow(max(0.0, dot(normal, lightPosition)), 6.0) * 0.125 * shadowIntensity * (1.0 - fogAmount - murkiness);\n" +
                "#endif\n" +
                "#if SSAOLEVEL > 0\n" +
                "    outGPosition = vec4(camPos.xyz, fogAmount * 2.0 + glowLevel + murkiness);\n" +
                "    outGNormal = gnormal;\n" +
                "#endif\n" +
                "    outGlow = vec4(glowLevel + glow, 0.0, 0.0, min(1.0, fogAmount + outColor.a));\n" +
                "}\n";
        }

        internal static string BuildLeanHaxyFragmentSource(bool includeUnderwater)
        {
            string underwaterInclude = includeUnderwater ? "#include underwatereffects.fsh\n" : string.Empty;
            string murkDecl = includeUnderwater
                ? "float murkiness = getUnderwaterMurkiness();\n"
                : "const float murkiness = 0.0;\n";
            string underwaterApply = includeUnderwater
                ? "outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);\n"
                : string.Empty;
            string fogWeightExpr = includeUnderwater
                ? "clamp(fogAmount - 50.0 * murkiness, 0.0, 1.0)"
                : "fogAmount";

            return
                "#version 330 core\n" +
                "#extension GL_ARB_explicit_attrib_location: enable\n" +
                "uniform sampler2D terrainTex;\n" +
                "uniform sampler2D terrainTexLinear;\n" +
                "in vec4 rgba;\n" +
                "in vec4 rgbaFog;\n" +
                "in float fogAmount;\n" +
                "in vec2 uv;\n" +
                "in float glowLevel;\n" +
                "flat in int renderFlags;\n" +
                "in vec3 normal;\n" +
                "in vec4 worldPos;\n" +
                "in vec3 vertexPosition;\n" +
                "in vec3 blockLight;\n" +
                "in vec4 gnormal;\n" +
                "in vec4 camPos;\n" +
                "in float lod0Fade;\n" +
                "in float nb;\n" +
                "uniform float alphaTest;\n" +
                "uniform float fogDensityIn;\n" +
                "uniform float fogMinIn;\n" +
                "uniform float horizonFog;\n" +
                "uniform vec3 sunPosition;\n" +
                "uniform float dayLight;\n" +
                "layout(location = 0) out vec4 outColor;\n" +
                "layout(location = 1) out vec4 outGlow;\n" +
                "#if SSAOLEVEL > 0\n" +
                "layout(location = 2) out vec4 outGNormal;\n" +
                "layout(location = 3) out vec4 outGPosition;\n" +
                "#endif\n" +
                "#include vertexflagbits.ash\n" +
                "#include fogandlight.fsh\n" +
                "#include dither.fsh\n" +
                "#include skycolor.fsh\n" +
                "#include colormap.fsh\n" +
                underwaterInclude +
                "void main()\n" +
                "{\n" +
                "    vec4 texColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv)) * rgba;\n" +
                "    float aTest = texColor.a + max(0.0, 1.0 - rgba.a) * min(1.0, texColor.a * 10.0) - lod0Fade;\n" +
                "    if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4.0;\n" +
                "    if (aTest < alphaTest || rgba.a < 0.005) discard;\n" +
                "    float b = 1.0;\n" +
                "#if SHADOWQUALITY > 0\n" +
                "    if (shadowIntensity > 0.0001 && blockBrightness < 1.0) {\n" +
                "#if SHADOWQUALITY == 1\n" +
                "        if (shadowCoordsFar.w > 0.0) {\n" +
                "            float minShadowB = clamp(1.0 - shadowIntensity * shadowCoordsFar.w * 0.5 + blockBrightness, 0.0, 1.0);\n" +
                "            bool needShadow = nb > minShadowB;\n" +
                "#if SHINYEFFECT > 0\n" +
                "            needShadow = needShadow || ((renderFlags & ReflectiveBitMask) != 0);\n" +
                "#endif\n" +
                "            if (needShadow) b = getBrightnessFromShadowMap();\n" +
                "        }\n" +
                "#else\n" +
                "        if (shadowCoordsFar.w > 0.0 || shadowCoordsNear.w > 0.0) {\n" +
                "            float minShadowB = clamp(1.0 - shadowIntensity * (shadowCoordsFar.w + shadowCoordsNear.w) * 0.5 + blockBrightness, 0.0, 1.0);\n" +
                "            bool needShadow = nb > minShadowB;\n" +
                "#if SHINYEFFECT > 0\n" +
                "            needShadow = needShadow || ((renderFlags & ReflectiveBitMask) != 0);\n" +
                "#endif\n" +
                "            if (needShadow) b = getBrightnessFromShadowMap();\n" +
                "        }\n" +
                "#endif\n" +
                "    }\n" +
                "#endif\n" +
                "    " + murkDecl +
                "    float finalB = min(b, nb) * (1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5);\n" +
                "    texColor.rgb *= finalB;\n" +
                "    outColor = applyFog(texColor, " + fogWeightExpr + ");\n" +
                "    float glow = 0.0;\n" +
                "    float godrayLevel = 0.0;\n" +
                "    if (rgba.a < 0.999) {\n" +
                "        vec4 skyColor = vec4(1.0);\n" +
                "        vec4 skyGlow = vec4(1.0);\n" +
                "        getSkyColorAt(worldPos.xyz, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);\n" +
                "        godrayLevel = skyGlow.g;\n" +
                "        outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1.0 - dayLight, max(0.0, rgba.a)));\n" +
                "    }\n" +
                "    " + underwaterApply +
                "#if SHINYEFFECT > 0\n" +
                "    if ((renderFlags & ReflectiveBitMask) != 0) {\n" +
                "        outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2.0 * fogAmount + 2.0 * (1.0 - b), 0.0, 1.0));\n" +
                "    }\n" +
                "    glow += pow(max(0.0, dot(normal, lightPosition)), 6.0) * 0.125 * shadowIntensity * (1.0 - fogAmount - murkiness);\n" +
                "#endif\n" +
                "#if SSAOLEVEL > 0\n" +
                "    outGPosition = vec4(camPos.xyz, fogAmount * 2.0 + glowLevel + murkiness);\n" +
                "    outGNormal = gnormal;\n" +
                "#endif\n" +
                "    outGlow = vec4(glowLevel + glow, godrayLevel, 0.0, min(1.0, fogAmount + outColor.a));\n" +
                "}\n";
        }

        internal static string BuildLeanTopSoilVertexSource()
        {
            return
                "#version 330 core\n" +
                "#extension GL_ARB_explicit_attrib_location: enable\n" +
                "#if USESSBO > 0\n" +
                "layout(location = 0) in vec4 rgbaLightIn;\n" +
                "layout(location = 1) in vec2 uv2In;\n" +
                "#else\n" +
                "layout(location = 0) in vec3 xyz;\n" +
                "layout(location = 1) in vec2 uvIn;\n" +
                "layout(location = 2) in vec4 rgbaLightIn;\n" +
                "layout(location = 3) in int renderFlagsIn;\n" +
                "layout(location = 4) in vec2 uv2In;\n" +
                "layout(location = 5) in int colormapData;\n" +
                "#endif\n" +
                "uniform vec4 rgbaFogIn;\n" +
                "uniform vec3 rgbaAmbientIn;\n" +
                "uniform float fogDensityIn;\n" +
                "uniform float fogMinIn;\n" +
                "uniform vec3 origin;\n" +
                "uniform mat4 projectionMatrix;\n" +
                "uniform mat4 modelViewMatrix;\n" +
                "uniform float subpixelPaddingX;\n" +
                "uniform float subpixelPaddingY;\n" +
                "uniform vec3 lightPosition;\n" +
                "uniform float shadowIntensity = 1.0;\n" +
                "out vec4 rgba;\n" +
                "out vec4 rgbaFog;\n" +
                "out float fogAmount;\n" +
                "out vec2 uv;\n" +
                "out vec2 uv2;\n" +
                "out vec3 normal;\n" +
                "out float nb;\n" +
                "#if SSAOLEVEL > 0\n" +
                "out vec4 fragPosition;\n" +
                "out vec4 gnormal;\n" +
                "#endif\n" +
                "out vec4 worldPos;\n" +
                "flat out int renderFlags;\n" +
                "#include vertexflagbits.ash\n" +
                "#include shadowcoords.vsh\n" +
                "#include fogandlight.vsh\n" +
                "#include vertexwarp.vsh\n" +
                "#include colormap.vsh\n" +
                "#if USESSBO > 0\n" +
                "layout(binding = 3, std430) readonly buffer faceDataBuf { FaceData faces[]; };\n" +
                "#endif\n" +
                "const float uvEpsilon = 1.0 / 32768.0;\n" +
                "void main(void)\n" +
                "{\n" +
                "#if USESSBO > 0\n" +
                "    FaceData vdata = faces[gl_VertexID / 4];\n" +
                "    int vIndex = gl_VertexID & 0x03;\n" +
                "    renderFlags = vdata.flags[vIndex];\n" +
                "    vec3 vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;\n" +
                "#else\n" +
                "    renderFlags = renderFlagsIn;\n" +
                "    vec3 vertexPosition = xyz;\n" +
                "#endif\n" +
                "    vec4 truePos = vec4(vertexPosition + origin, 1.0);\n" +
                "    worldPos = applyGlobalWarping(truePos);\n" +
                "    vec4 cameraPos = modelViewMatrix * worldPos;\n" +
                "    gl_Position = projectionMatrix * cameraPos;\n" +
                "    calcShadowMapCoords(modelViewMatrix, worldPos);\n" +
                "#if USESSBO > 0\n" +
                "    calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, false);\n" +
                "    uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);\n" +
                "#else\n" +
                "    calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, false);\n" +
                "    uv = uvIn;\n" +
                "#endif\n" +
                "    uv2 = uv2In * 2.0 - vec2((int(uv2In.x * 0x10000) & 1) * (uvEpsilon + subpixelPaddingX * 2.0), (int(uv2In.y * 0x10000) & 1) * (uvEpsilon + subpixelPaddingY * 2.0));\n" +
                "    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);\n" +
                "    rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);\n" +
                "    rgbaFog = rgbaFogIn;\n" +
                "    rgbaFog.a = clamp(20.0 * (1.10 - length(worldPos.xz) / viewDistance) - 5.0 + max(0.0, worldPos.y * 0.02), 0.0, 1.0);\n" +
                "    normal = unpackNormal(renderFlags);\n" +
                "#if SHADOWQUALITY > 0\n" +
                "    float minNormalShade = 0.34 + (1.0 - shadowIntensity) / 8.0;\n" +
                "#else\n" +
                "    float minNormalShade = 0.45;\n" +
                "#endif\n" +
                "    nb = max(max(minNormalShade, 0.5 + 0.5 * dot(normal, lightPosition)), normal.y * 0.95);\n" +
                "#if SSAOLEVEL > 0\n" +
                "    fragPosition = cameraPos;\n" +
                "    gnormal = modelViewMatrix * vec4(normal, 0.0);\n" +
                "    gnormal.w = 0.0;\n" +
                "#endif\n" +
                "}\n";
        }

        internal static string BuildLeanTopSoilFragmentSource(bool includeUnderwater)
        {
            string underwaterInclude = includeUnderwater ? "#include underwatereffects.fsh\n" : string.Empty;
            string murkDecl = includeUnderwater
                ? "float murkiness = getUnderwaterMurkiness();\n"
                : "const float murkiness = 0.0;\n";
            string underwaterApply = includeUnderwater
                ? "outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);\n"
                : string.Empty;
            string fogWeightExpr = includeUnderwater
                ? "clamp(fogAmount - 50.0 * murkiness, 0.0, 1.0)"
                : "fogAmount";

            return
                "#version 330 core\n" +
                "uniform sampler2D terrainTex;\n" +
                "uniform sampler2D terrainTexLinear;\n" +
                "uniform float alphaTest = 0.01;\n" +
                "uniform vec2 blockTextureSize;\n" +
                "in vec4 rgba;\n" +
                "in vec4 rgbaFog;\n" +
                "in float fogAmount;\n" +
                "in vec2 uv;\n" +
                "in vec2 uv2;\n" +
                "in float glowLevel;\n" +
                "in vec3 blockLight;\n" +
                "in vec4 worldPos;\n" +
                "flat in int renderFlags;\n" +
                "in vec3 normal;\n" +
                "in float nb;\n" +
                "layout(location = 0) out vec4 outColor;\n" +
                "layout(location = 1) out vec4 outGlow;\n" +
                "#if SSAOLEVEL > 0\n" +
                "in vec4 fragPosition;\n" +
                "in vec4 gnormal;\n" +
                "layout(location = 2) out vec4 outGNormal;\n" +
                "layout(location = 3) out vec4 outGPosition;\n" +
                "#endif\n" +
                "#include vertexflagbits.ash\n" +
                "#include fogandlight.fsh\n" +
                "#include colormap.fsh\n" +
                "#include noise3d.ash\n" +
                underwaterInclude +
                "void main()\n" +
                "{\n" +
                "    float aTest = rgbaFog.a + max(0.0, 1.0 - rgba.a) * min(1.0, rgbaFog.a * 10.0);\n" +
                "    if (aTest < alphaTest || rgbaFog.a < 0.005) discard;\n" +
                "    vec4 brownSoilColor = texture(terrainTex, uv) * rgba;\n" +
                "    if (normal.y >= 0.0) {\n" +
                "        vec4 grassColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv2 + vec2(blockTextureSize.x * normal.y, 0.0))) * rgba;\n" +
                "        outColor = brownSoilColor * (1.0 - grassColor.a) + grassColor * grassColor.a;\n" +
                "    } else {\n" +
                "        outColor = applyFog(brownSoilColor, fogAmount);\n" +
                "    }\n" +
                "    float b = 1.0;\n" +
                "#if SHADOWQUALITY > 0\n" +
                "    if (shadowIntensity > 0.0001 && blockBrightness < 1.0) {\n" +
                "#if SHADOWQUALITY == 1\n" +
                "        if (shadowCoordsFar.w > 0.0) {\n" +
                "            float minShadowB = clamp(1.0 - shadowIntensity * shadowCoordsFar.w * 0.5 + blockBrightness, 0.0, 1.0);\n" +
                "            if (nb > minShadowB) b = getBrightnessFromShadowMap();\n" +
                "        }\n" +
                "#else\n" +
                "        if (shadowCoordsFar.w > 0.0 || shadowCoordsNear.w > 0.0) {\n" +
                "            float minShadowB = clamp(1.0 - shadowIntensity * (shadowCoordsFar.w + shadowCoordsNear.w) * 0.5 + blockBrightness, 0.0, 1.0);\n" +
                "            if (nb > minShadowB) b = getBrightnessFromShadowMap();\n" +
                "        }\n" +
                "#endif\n" +
                "    }\n" +
                "#endif\n" +
                "    " + murkDecl +
                "    float finalB = min(b, nb) * (1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5);\n" +
                "    outColor.rgb *= finalB;\n" +
                "    outColor = applyFog(outColor, " + fogWeightExpr + ");\n" +
                "    " + underwaterApply +
                "    outColor.a = rgbaFog.a;\n" +
                "    float glow = 0.0;\n" +
                "#if SHINYEFFECT > 0\n" +
                "    if ((renderFlags & ReflectiveBitMask) > 0) {\n" +
                "        vec3 worldVec = normalize(worldPos.xyz);\n" +
                "        float angle = 2.0 * dot(normalize(normal), worldVec);\n" +
                "        angle += gnoise(vec3(uv.x * 500.0, uv.y * 500.0, worldVec.z / 10.0)) / 7.5;\n" +
                "        outColor.rgb *= max(vec3(1.0), vec3(1.0) + 3.0 * blockLight * gnoise(vec3(worldVec.x / 10.0 + angle, worldVec.y / 10.0 + angle, worldVec.z / 10.0 + angle)));\n" +
                "    }\n" +
                "    glow = pow(max(0.0, dot(normal, lightPosition)), 6.0) * 0.1 * shadowIntensity * (1.0 - fogAmount);\n" +
                "#endif\n" +
                "#if SSAOLEVEL > 0\n" +
                "    outGPosition = vec4(fragPosition.xyz, fogAmount * 2.0 + glowLevel);\n" +
                "    outGNormal = gnormal;\n" +
                "#endif\n" +
                "    outGlow = vec4(glowLevel + glow, 0.0, 0.0, outColor.a);\n" +
                "}\n";
        }

        public static void Cleanup(ICoreClientAPI api = null)
        {
            enabled = false;
            topSoilEnabled = false;
            hasLiquidDepthInView = true;
            DisposePrograms(api);
            logger = null;
        }

        private static void DisposePrograms(ICoreClientAPI api)
        {
            DisposeOne(api, ref leanWetProgram, LeanWetProgramName);
            DisposeOne(api, ref leanDryProgram, LeanDryProgramName);
            DisposeOne(api, ref haxyWetProgram, HaxyWetProgramName);
            DisposeOne(api, ref haxyDryProgram, HaxyDryProgramName);
            DisposeOne(api, ref topSoilWetProgram, TopSoilWetProgramName);
            DisposeOne(api, ref topSoilDryProgram, TopSoilDryProgramName);
        }

        private static void DisposeOne(ICoreClientAPI api, ref ShaderProgram field, string name)
        {
            ShaderProgram prev = field;
            field = null;
            TryDisposeProgram(prev);

            if (api?.Shader != null)
            {
                try
                {
                    IShaderProgram registered = api.Shader.GetProgramByName(name);
                    if (registered != null && !ReferenceEquals(registered, prev))
                    {
                        registered.Dispose();
                    }
                }
                catch
                {
                }
            }
        }

        private static bool IsProgramUsable(ShaderProgram program)
        {
            return program != null && !program.Disposed && !program.LoadError && program.ProgramId > 0;
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
    }
}
