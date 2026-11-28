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
    /// Compiles lean memory variants of chunkopaque that strip unused compile-time paths
    /// (skycolor/haxyFade, psychedelic, rust glitch, fog spheres, and optionally underwater
    /// murkiness when no liquid geometry rendered into LiquidDepth) for Opaque, BlendNoCull,
    /// and Decor passes during normal gameplay. Falls back transparently to full vanilla
    /// chunkopaque whenever temporal storms, psychedelic effects, fog spheres, normal view,
    /// or HaxyFade passes are active.
    /// </summary>
    public static class OpaqueShaderOptimization
    {
        private const string LeanWetProgramName = "optitime_chunkopaque_lean_wet";
        private const string LeanDryProgramName = "optitime_chunkopaque_lean_dry";

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
        private static volatile bool hasLiquidDepthInView = true;
        private static ShaderProgram leanWetProgram;
        private static ShaderProgram leanDryProgram;
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

        public static void Configure(bool isEnabled, ILogger modLogger = null)
        {
            enabled = isEnabled;
            if (modLogger != null)
            {
                logger = modLogger;
            }
        }

        public static bool RebuildShaders(ICoreClientAPI api)
        {
            DisposePrograms(api);
            if (!enabled || api?.Shader == null)
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

                leanWetProgram = BuildLeanVariant(api, baseProgram, vertexCode, LeanWetProgramName, includeUnderwater: true);
                leanDryProgram = BuildLeanVariant(api, baseProgram, vertexCode, LeanDryProgramName, includeUnderwater: false);

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

            bool useDry = CanUseDryVariant(sh.CameraUnderwater, hasLiquidDepthInView) && IsLeanDryReady;
            ShaderProgram lean = useDry ? leanDryProgram : leanWetProgram;
            if (!IsProgramUsable(lean))
            {
                return true;
            }

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

            // Pass 5: TopSoil (vanilla Chunktopsoil)
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

            // Pass 1: OpaqueNoCull (HaxyFade == 1, foliage sky-fade) uses full chunkopaque
            platform.GlToggleBlend(on: false);
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
            {
                lean.Uniform("rgbaFogIn", game.AmbientManager.BlendedFogColor);
            }

            if (lean.HasUniform("rgbaAmbientIn"))
            {
                lean.Uniform("rgbaAmbientIn", game.AmbientManager.BlendedAmbientColor);
            }

            if (lean.HasUniform("fogDensityIn"))
            {
                lean.Uniform("fogDensityIn", game.AmbientManager.BlendedFogDensity);
            }

            if (lean.HasUniform("fogMinIn"))
            {
                lean.Uniform("fogMinIn", game.AmbientManager.BlendedFogMin);
            }

            lean.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
            lean.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
            lean.Uniform("alphaTest", alphaTest);

            if (lean.HasUniform("subpixelPaddingX"))
            {
                lean.Uniform("subpixelPaddingX", subPixelPaddingX);
            }

            if (lean.HasUniform("subpixelPaddingY"))
            {
                lean.Uniform("subpixelPaddingY", subPixelPaddingY);
            }
        }

        private static ShaderProgram BuildLeanVariant(
            ICoreClientAPI api,
            ShaderProgramChunkopaque baseProgram,
            string vertexCode,
            string programName,
            bool includeUnderwater)
        {
            if (handleIncludesMethod == null)
            {
                return null;
            }

            string fragSource = BuildLeanFragmentSource(includeUnderwater);
            // Vertex filename MUST start with "chunk" so Shader.UsesSSBOs() promotes #version 430.
            string vshName = includeUnderwater ? "chunkopaque_optitime_wet.vsh" : "chunkopaque_optitime_dry.vsh";
            string fshName = includeUnderwater ? "chunkopaque_optitime_wet.fsh" : "chunkopaque_optitime_dry.fsh";

            var program = new ShaderProgram
            {
                LoadFromFile = false,
                Oit = false
            };

            // Memory shaders bypass ShaderRegistry.LoadShader, so expand #include directives explicitly
            // using the game's active ShaderRegistry include table (which preserves mod include overrides).
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
                    if (string.Equals(inc, "skycolor.fsh", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!includeUnderwater && string.Equals(inc, "underwatereffects.fsh", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

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

        public static void Cleanup(ICoreClientAPI api = null)
        {
            enabled = false;
            hasLiquidDepthInView = true;
            DisposePrograms(api);
            logger = null;
        }

        private static void DisposePrograms(ICoreClientAPI api)
        {
            DisposeOne(api, ref leanWetProgram, LeanWetProgramName);
            DisposeOne(api, ref leanDryProgram, LeanDryProgramName);
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
