using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Compiles a lean clear-weather chunkliquid memory shader that strips rain ripple loops
    /// (dropletnoise), psychedelic distortion, and fog spheres when it is not raining and no
    /// special effects are active, while preserving full underwater murkiness, 9-tap integer
    /// texelFetch foam, lava rendering, specular reflections, and OIT output.
    /// </summary>
    public static class LiquidShaderOptimization
    {
        private const string LeanLiquidProgramName = "optitime_chunkliquid_clear";

        private static readonly AccessTools.FieldRef<ChunkRenderer, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, float> subPixelPaddingXRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, float> subPixelPaddingYRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, Vec2f> blockTextureSizeRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, float> curRainFallRef;
        private static readonly AccessTools.FieldRef<ChunkRenderer, float> lastSetRainFallRef;
        private static readonly AccessTools.FieldRef<ClientCoreAPI, RenderAPIBase> renderApiRef;
        private static readonly AccessTools.FieldRef<RenderAPIBase, bool> useSSBOsRef;
        private static readonly MethodInfo handleIncludesMethod;
        private static readonly bool accessorsReady;

        private static volatile bool enabled;
        private static ShaderProgram clearLiquidProgram;
        private static ILogger logger;

        static LiquidShaderOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ChunkRenderer, ClientMain>("game");
                subPixelPaddingXRef = AccessTools.FieldRefAccess<ChunkRenderer, float>("subPixelPaddingX");
                subPixelPaddingYRef = AccessTools.FieldRefAccess<ChunkRenderer, float>("subPixelPaddingY");
                blockTextureSizeRef = AccessTools.FieldRefAccess<ChunkRenderer, Vec2f>("blockTextureSize");
                curRainFallRef = AccessTools.FieldRefAccess<ChunkRenderer, float>("curRainFall");
                lastSetRainFallRef = AccessTools.FieldRefAccess<ChunkRenderer, float>("lastSetRainFall");
                renderApiRef = AccessTools.FieldRefAccess<ClientCoreAPI, RenderAPIBase>("renderapi");
                useSSBOsRef = AccessTools.FieldRefAccess<RenderAPIBase, bool>("useSSBOs");
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

        public static bool IsClearLiquidReady => IsProgramUsable(clearLiquidProgram);

        public static void Configure(bool isEnabled, ILogger modLogger = null)
        {
            enabled = isEnabled;
            if (modLogger != null)
            {
                logger = modLogger;
            }
        }

        public static void Cleanup(ICoreClientAPI api = null)
        {
            enabled = false;
            DisposeProgram(api);
            logger = null;
        }

        internal static bool CanUseClearLiquid(float curRainFall, float psychedelicStrength, int fogSphereQuantity)
        {
            return curRainFall < 0.001f &&
                psychedelicStrength <= 0.001f &&
                fogSphereQuantity == 0;
        }

        public static bool RebuildShader(ICoreClientAPI api)
        {
            DisposeProgram(api);
            if (!enabled || !accessorsReady || api?.Shader == null)
            {
                return false;
            }

            try
            {
                ShaderProgramChunkliquid baseLiquid = ShaderPrograms.Chunkliquid;
                if (baseLiquid == null || baseLiquid.Disposed || baseLiquid.LoadError)
                {
                    return false;
                }

                string vertexCode = baseLiquid.VertexShader?.Code;
                if (string.IsNullOrEmpty(vertexCode))
                {
                    return false;
                }

                var program = new ShaderProgram
                {
                    LoadFromFile = false,
                    Oit = true
                };

                string expandedFrag = (string)handleIncludesMethod.Invoke(
                    null,
                    new object[] { program, BuildClearLiquidFragmentSource(), null });
                if (string.IsNullOrEmpty(expandedFrag))
                {
                    return false;
                }

                // Chunkliquid runs with useSSBOs=false in RenderOIT, matching vanilla vertex shader.
                program.VertexShader = new Shader(EnumShaderType.VertexShader, vertexCode, "liquidclear_optitime.vsh");
                program.FragmentShader = new Shader(EnumShaderType.FragmentShader, expandedFrag, "liquidclear_optitime.fsh");

                if (baseLiquid.includes != null)
                {
                    foreach (string inc in baseLiquid.includes)
                    {
                        program.includes.Add(inc);
                    }
                }

                api.Shader.RegisterMemoryShaderProgram(LeanLiquidProgramName, program);
                if (!program.Compile() || program.LoadError || program.ProgramId <= 0)
                {
                    TryDisposeProgram(program);
                    logger?.Warning("[OptiTime] Lean clear-weather chunkliquid shader failed to compile; using vanilla chunkliquid");
                    return false;
                }

                clearLiquidProgram = program;
                return true;
            }
            catch (Exception ex)
            {
                DisposeProgram(api);
                logger?.Warning("[OptiTime] Lean clear-weather chunkliquid setup failed: " + ex.Message);
                return false;
            }
        }

        public static bool RenderOIT_Prefix(ChunkRenderer __instance, float deltaTime)
        {
            if (!enabled || !accessorsReady || __instance == null || !IsProgramUsable(clearLiquidProgram))
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            if (game == null || game.EntityPlayer == null || game.MainCamera == null ||
                game.AmbientManager == null || game.shUniforms == null || game.api?.Render?.FrameBuffers == null ||
                __instance.poolsByRenderPass == null || __instance.textureIds == null)
            {
                return true;
            }

            ShaderProgramChunktransparent chunktransparent = ShaderPrograms.Chunktransparent;
            if (chunktransparent == null || chunktransparent.Disposed)
            {
                return true;
            }

            float curRain = curRainFallRef(__instance);
            DefaultShaderUniforms sh = game.shUniforms;
            if (!CanUseClearLiquid(curRain, sh.PsychedelicStrength, sh.FogSphereQuantity))
            {
                return true;
            }

            float subPixelPaddingX = subPixelPaddingXRef(__instance);
            float subPixelPaddingY = subPixelPaddingYRef(__instance);
            int[] textureIds = __instance.textureIds;
            MeshDataPoolManager[][] poolsByRenderPass = __instance.poolsByRenderPass;
            Vec3d cameraPos = game.EntityPlayer.CameraPos;

            game.GlPushMatrix();
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
            game.GlPushMatrix();

            RenderAPIBase renderApi = renderApiRef(game.api);
            if (renderApi == null)
            {
                return true;
            }

            ShaderProgram liquid = clearLiquidProgram;
            liquid.Use();
            bool prevUseSSBOs = useSSBOsRef(renderApi);
            try
            {
                if (liquid.HasUniform("waterStillCounter"))
                    liquid.Uniform("waterStillCounter", sh.WaterStillCounter);
                if (liquid.HasUniform("waterFlowCounter"))
                    liquid.Uniform("waterFlowCounter", sh.WaterFlowCounter);
                if (liquid.HasUniform("windWaveIntensity"))
                    liquid.Uniform("windWaveIntensity", sh.WindWaveIntensity);
                if (liquid.HasUniform("sunPosRel"))
                    liquid.Uniform("sunPosRel", sh.SunPosition3D);
                if (liquid.HasUniform("sunColor"))
                    liquid.Uniform("sunColor", game.Calendar.SunColor);
                if (liquid.HasUniform("reflectColor"))
                    liquid.Uniform("reflectColor", game.Calendar.ReflectColor);
                if (liquid.HasUniform("playerPosForFoam"))
                    liquid.Uniform("playerPosForFoam", sh.PlayerPosForFoam);
                if (liquid.HasUniform("subpixelPaddingX"))
                    liquid.Uniform("subpixelPaddingX", subPixelPaddingX);
                if (liquid.HasUniform("subpixelPaddingY"))
                    liquid.Uniform("subpixelPaddingY", subPixelPaddingY);

                lastSetRainFallRef(__instance) = 0f;

                FrameBufferRef primaryFb = game.api.Render.FrameBuffers[0];
                if (liquid.HasUniform("rgbaFogIn"))
                    liquid.Uniform("rgbaFogIn", game.AmbientManager.BlendedFogColor);
                if (liquid.HasUniform("rgbaAmbientIn"))
                    liquid.Uniform("rgbaAmbientIn", game.AmbientManager.BlendedAmbientColor);
                if (liquid.HasUniform("fogDensityIn"))
                    liquid.Uniform("fogDensityIn", game.AmbientManager.BlendedFogDensity);
                if (liquid.HasUniform("fogMinIn"))
                    liquid.Uniform("fogMinIn", game.AmbientManager.BlendedFogMin);
                if (liquid.HasUniform("blockTextureSize"))
                    liquid.Uniform("blockTextureSize", blockTextureSizeRef(__instance));
                if (liquid.HasUniform("textureAtlasSize"))
                    liquid.Uniform("textureAtlasSize", game.BlockAtlasManager.SizeAsVec2f);
                if (liquid.HasUniform("toShadowMapSpaceMatrixFar"))
                    liquid.UniformMatrix("toShadowMapSpaceMatrixFar", sh.ToShadowMapSpaceMatrixFar);
                if (liquid.HasUniform("toShadowMapSpaceMatrixNear"))
                    liquid.UniformMatrix("toShadowMapSpaceMatrixNear", sh.ToShadowMapSpaceMatrixNear);
                liquid.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
                liquid.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
                if (liquid.HasUniform("playerViewVec"))
                    liquid.Uniform("playerViewVec", sh.PlayerViewVector);
                if (liquid.HasUniform("depthTex"))
                    liquid.BindTexture2D("depthTex", primaryFb.DepthTextureId);
                if (liquid.HasUniform("frameSize"))
                    liquid.Uniform("frameSize", primaryFb.Width, primaryFb.Height);
                if (liquid.HasUniform("sunSpecularIntensity"))
                    liquid.Uniform("sunSpecularIntensity", sh.SunSpecularIntensity);

                useSSBOsRef(renderApi) = false;
                MeshDataPoolManager[] liquidPools = poolsByRenderPass[4];
                for (int i = 0; i < textureIds.Length; i++)
                {
                    liquid.BindTexture2D("terrainTex", textureIds[i], 0);
                    liquidPools[i].Render(cameraPos, "origin");
                }
            }
            finally
            {
                useSSBOsRef(renderApi) = prevUseSSBOs;
                if (ShaderProgramBase.CurrentShaderProgram == liquid)
                {
                    liquid.Stop();
                }
            }

            ScreenManager.FrameProfiler.Mark("rend3D-ret-lp");
            game.GlPopMatrix();

            chunktransparent.Use();
            try
            {
                chunktransparent.RgbaFogIn = game.AmbientManager.BlendedFogColor;
                chunktransparent.RgbaAmbientIn = game.AmbientManager.BlendedAmbientColor;
                chunktransparent.FogDensityIn = game.AmbientManager.BlendedFogDensity;
                chunktransparent.FogMinIn = game.AmbientManager.BlendedFogMin;
                chunktransparent.ProjectionMatrix = game.CurrentProjectionMatrix;
                chunktransparent.ModelViewMatrix = game.CurrentModelViewMatrix;
                chunktransparent.Uniform("subpixelPaddingX", subPixelPaddingX);
                chunktransparent.Uniform("subpixelPaddingY", subPixelPaddingY);

                MeshDataPoolManager[] transparentPools = poolsByRenderPass[3];
                MeshDataPoolManager[] metaPools = poolsByRenderPass[6];
                bool renderMeta = ClientSettings.RenderMetaBlocks;
                for (int j = 0; j < textureIds.Length; j++)
                {
                    chunktransparent.TerrainTex2D = textureIds[j];
                    transparentPools[j].Render(cameraPos, "origin");
                    if (renderMeta)
                    {
                        metaPools[j].Render(cameraPos, "origin");
                    }
                }
            }
            finally
            {
                if (ShaderProgramBase.CurrentShaderProgram == chunktransparent)
                {
                    chunktransparent.Stop();
                }
            }

            game.GlPopMatrix();
            ScreenManager.FrameProfiler.Mark("rend3D-ret-tp");
            return false;
        }

        internal static string BuildClearLiquidFragmentSource()
        {
            return
                "#version 330 core\n" +
                "uniform sampler2D terrainTex;\n" +
                "uniform sampler2D depthTex;\n" +
                "uniform vec2 blockTextureSize;\n" +
                "uniform vec2 textureAtlasSize;\n" +
                "uniform float waterFlowCounter;\n" +
                "uniform vec3 sunPosRel;\n" +
                "uniform vec3 sunColor;\n" +
                "uniform vec3 reflectColor;\n" +
                "uniform float waterWaveCounter;\n" +
                "uniform float sunSpecularIntensity;\n" +
                "uniform float windSpeed;\n" +
                "in vec4 rgba;\n" +
                "in vec4 rgbaFog;\n" +
                "in float fogAmount;\n" +
                "in vec2 uv;\n" +
                "in vec2 uvSize;\n" +
                "in float waterStillCounterOff;\n" +
                "flat in vec2 uvBase;\n" +
                "in vec3 fragWorldPos;\n" +
                "in vec3 fWorldPos;\n" +
                "in vec3 fragNormal;\n" +
                "in float fresnel;\n" +
                "flat in int skyExposed;\n" +
                "in vec2 flowVectorf;\n" +
                "in float glowLevel;\n" +
                "flat in int waterFlags;\n" +
                "flat in int renderFoam;\n" +
                "#include fogandlight.fsh\n" +
                "#include noise3d.ash\n" +
                "#include colormap.fsh\n" +
                "#include underwatereffects.fsh\n" +
                "#include oit.fsh\n" +
                "void main()\n" +
                "{\n" +
                "    if (rgba.a < 0.005) discard;\n" +
                "    float murkiness = max(0.0, getUnderwaterMurkiness() - fogAmount);\n" +
                "    if (murkiness > 0.05) discard;\n" +
                "    vec4 texColor;\n" +
                "    float vn = max(0.0, 0.9 - abs(fragNormal.y));\n" +
                "    bool isLava = (waterFlags & LiquidIsLavaBitMask) > 0;\n" +
                "    bool fullAlpha = (waterFlags & LiquidFullAlphaBitMask) > 0;\n" +
                "    float wfc = isLava ? (waterFlowCounter * 0.1 * (1.0 + 5.0 * vn)) : (waterFlowCounter * (1.0 + 5.0 * vn));\n" +
                "    float flowSpeed = length(flowVectorf);\n" +
                "    if (flowSpeed > 0.001) {\n" +
                "        vec2 flowVec = normalize(flowVectorf) * flowSpeed;\n" +
                "        if (fragNormal.y < 0.0) wfc *= -1.0;\n" +
                "        vec2 uvxOffset = clamp(mod((uv - uvBase) + flowVec * wfc * blockTextureSize, blockTextureSize), vec2(1.0 / textureAtlasSize), blockTextureSize - 1.0 / textureAtlasSize);\n" +
                "        texColor = texture(terrainTex, uvBase + uvxOffset);\n" +
                "    } else {\n" +
                "        vec2 uvxOffset = clamp(blockTextureSize - uvSize, vec2(1.0 / textureAtlasSize), blockTextureSize - 1.0 / textureAtlasSize);\n" +
                "        texColor = texture(terrainTex, uv) * waterStillCounterOff + (1.0 - waterStillCounterOff) * texture(terrainTex, uvBase + uvxOffset);\n" +
                "    }\n" +
                "    texColor = getColorMapped(terrainTex, texColor);\n" +
                "    vec4 rgbaFinal = rgba;\n" +
                "#if FOAMEFFECT > 0\n" +
                "    rgbaFinal.a = rgba.a + max(0.0, texColor.a - 0.4);\n" +
                "#else\n" +
                "    rgbaFinal.a = rgba.a + texColor.a;\n" +
                "#endif\n" +
                "    float bright = (rgba.r + rgba.g + rgba.b) / 3.0;\n" +
                "    float shadowBright = getBrightnessFromShadowMap();\n" +
                "    if (fullAlpha) rgbaFinal.a = 1.0;\n" +
                "    if (isLava) {\n" +
                "        texColor *= vec4(vec3((rgbaFinal.r + rgbaFinal.g + rgbaFinal.b) / 3.0 * shadowBright), rgbaFinal.a);\n" +
                "    } else {\n" +
                "        texColor *= vec4(rgbaFinal.rgb * shadowBright, rgbaFinal.a);\n" +
                "    }\n" +
                "    if (flowSpeed > 0.0) texColor.a *= 1.2 * flowSpeed;\n" +
                "    bool doLightFoam = (waterFlags & LiquidWeakFoamBitMask) != 0;\n" +
                "#if FOAMEFFECT > 0\n" +
                "    if (rgbaFinal.a > 0.0) {\n" +
                "        float ownDepth = linearDepth(gl_FragCoord.z);\n" +
                "        ivec2 fragPx = ivec2(gl_FragCoord.xy);\n" +
                "        ivec2 maxPx = max(ivec2(0), ivec2(frameSize) - ivec2(1));\n" +
                "        float diffTotal = 0.0;\n" +
                "        float d;\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx, ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2(-1, -1), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2( 1, -1), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2(-1,  1), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2( 1,  1), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2(-2,  0), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2( 2,  0), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2( 0, -2), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        d = ownDepth - linearDepth(texelFetch(depthTex, clamp(fragPx + ivec2( 0,  2), ivec2(0), maxPx), 0).x); if (d < 0.001) diffTotal += abs(d);\n" +
                "        diffTotal /= 5.76;\n" +
                "        diffTotal = min(diffTotal, -vn / 10.0 + 0.05);\n" +
                "        if (isLava) {\n" +
                "            float intensity = clamp(dot(fragNormal, vec3(0.0, 1.0, 0.0)), 0.0, 1.0) * 0.5;\n" +
                "            float a = fragWorldPos.x + fragWorldPos.y - 1.5 * flowVectorf.x * wfc;\n" +
                "            float b = fragWorldPos.z - 1.5 * flowVectorf.y * wfc;\n" +
                "            float diff = intensity * clamp(1.0 - diffTotal * 1000.0 - gnoise(vec3(a * 35.0, b * 35.0, wfc)) / 2.0 + gnoise(vec3(a * 2.0, b * 2.0, wfc)) / 2.0, 0.0, 1.0);\n" +
                "            float noise = intensity * (gnoise(vec3(a, b, wfc)) + 0.5) / 2.0;\n" +
                "            float rgbAdd = bright * (diff * 0.3 + noise / 10.0);\n" +
                "            texColor.rgb -= vec3(rgbAdd);\n" +
                "            texColor.a = 1.0;\n" +
                "            float blackSpots = gnoise(fragWorldPos.xyz) + 0.5;\n" +
                "            texColor.rgb -= blackSpots * 0.75;\n" +
                "            texColor.g += 0.1;\n" +
                "        } else {\n" +
                "            vec3 localPos = fragWorldPos.xyz;\n" +
                "            float intensity = clamp(dot(fragNormal, vec3(0.0, 1.0, 0.0)), 0.0, 1.0);\n" +
                "            float a = localPos.x + localPos.y - 1.5 * flowVectorf.x * wfc;\n" +
                "            float b = localPos.z - 1.5 * flowVectorf.y * wfc;\n" +
                "            float noise1 = gnoise(vec3(a * 15.0, -abs(b * 15.0), wfc)) + gnoise(vec3(a * 5.0, b * 5.0, wfc));\n" +
                "            float noise2 = gnoise(vec3(a, b, wfc));\n" +
                "            float diff = intensity * clamp(1.0 - diffTotal * 1500.0 - noise1 / 2.0 + noise2 / 2.0, 0.0, 1.0);\n" +
                "            float noise = intensity * (gnoise(vec3(a * 0.4, b * 0.4, wfc)) / 2.0 + noise2 / 2.0 + 0.5) / 2.0;\n" +
                "            float rgbAdd = max(0.0, bright * (diff * 0.3 + noise / 10.0));\n" +
                "            if (doLightFoam) rgbAdd *= 0.5;\n" +
                "            texColor.rgb += vec3(rgbAdd);\n" +
                "            texColor.a += (max(0.0, diff / 16.0 + noise / (12.0 - 8.0 * min(1.0, windSpeed))) + vn / 4.0) / clamp(fresnel, 0.5, 1.0);\n" +
                "#if SHADOWQUALITY == 0\n" +
                "            if (skyExposed > 0) {\n" +
                "#endif\n" +
                "                vec3 noisepos = vec3(localPos.x, localPos.z, waterWaveCounter / 8.0 + windWaveCounter / 6.0);\n" +
                "                float dy = noise2 / 20.0 + clamp(gnoise(noisepos) / 10.0, 0.0, 0.6);\n" +
                "                vec3 normal = normalize(vec3(dy, 1.0, -dy));\n" +
                "                float upness = max(0.0, dot(fragNormal, vec3(0.0, 1.0, 0.0)));\n" +
                "                vec3 eye = normalize(vec3(fWorldPos.x, fWorldPos.y - 2.0, fWorldPos.z));\n" +
                "                vec3 reflectionVec = reflect(sunPosRel, normal);\n" +
                "                float p = dot(reflectionVec, eye);\n" +
                "                if (p > 0.0) {\n" +
                "                    float sunb = clamp(sunPosRel.y * 10.0, 0.0, 1.0) * clamp(1.5 - sunPosRel.y, 0.0, 1.0) * sunSpecularIntensity;\n" +
                "                    float specular = pow(p, 50.0) * sunb;\n" +
                "#if SHADOWQUALITY > 0\n" +
                "                    float weight = upness * clamp(specular * clamp(pow(shadowBright, 4.0), 0.0, 1.0) * clamp(1.5 * shadowIntensity, 0.0, 1.0), 0.0, 1.0);\n" +
                "#else\n" +
                "                    float weight = upness * clamp(specular * clamp(pow(shadowBright, 4.0), 0.0, 1.0), 0.0, 1.0);\n" +
                "#endif\n" +
                "                    vec3 sunColf = applyFog(vec4(reflectColor, 1.0), fogAmount).rgb;\n" +
                "                    texColor.rgb = mix(texColor.rgb, sunColf + noise1 * 0.2, weight);\n" +
                "                    texColor.a = mix(texColor.a, texColor.a + specular / 2.0, weight);\n" +
                "                }\n" +
                "#if SHADOWQUALITY == 0\n" +
                "            }\n" +
                "#endif\n" +
                "        }\n" +
                "    }\n" +
                "#else\n" +
                "    if (isLava) texColor.a = 1.0;\n" +
                "#endif\n" +
                "    texColor = applyFog(texColor, fogAmount);\n" +
                "    texColor.a = clamp(texColor.a + fogAmount, 0.0, 1.0);\n" +
                "    OIT(texColor, glowLevel);\n" +
                "}\n";
        }

        private static void DisposeProgram(ICoreClientAPI api)
        {
            ShaderProgram prev = clearLiquidProgram;
            clearLiquidProgram = null;
            TryDisposeProgram(prev);

            if (api?.Shader != null)
            {
                try
                {
                    IShaderProgram registered = api.Shader.GetProgramByName(LeanLiquidProgramName);
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
