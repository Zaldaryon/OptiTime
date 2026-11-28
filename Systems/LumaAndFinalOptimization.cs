using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Optimizes the Luma and FinalComposition passes:
    /// 1. Reallocates EnumFrameBuffer.Luma color attachment from RGBA16F to RGBA8 because its sole input
    ///    (Primary color attachment 0) is already RGBA8, halving 4K VRAM and memory bandwidth (-33.2 MB at 4K).
    /// 2. Compiles a lean FinalComposition memory shader that strips dead vignette/glitch gnoise paths during
    ///    normal gameplay and enables FXAA_GATHER4_ALPHA on OpenGL 4.0+ non-macOS drivers (with automatic
    ///    compile-time fallback when unsupported).
    /// </summary>
    public static class LumaAndFinalOptimization
    {
        private const string LeanFinalProgramName = "optitime_final_lean";

        private static readonly DrawBuffersEnum[] DrawBuffers1 = { DrawBuffersEnum.ColorAttachment0 };
        private static readonly DrawBuffersEnum[] DrawBuffers2 =
        {
            DrawBuffersEnum.ColorAttachment0,
            DrawBuffersEnum.ColorAttachment1
        };
        private static readonly DrawBuffersEnum[] DrawBuffers4 =
        {
            DrawBuffersEnum.ColorAttachment0,
            DrawBuffersEnum.ColorAttachment1,
            DrawBuffersEnum.ColorAttachment2,
            DrawBuffersEnum.ColorAttachment3
        };

        private static readonly AccessTools.FieldRef<ClientPlatformWindows, List<FrameBufferRef>> frameBuffersRef;
        private static readonly AccessTools.FieldRef<ClientPlatformWindows, MeshRef> screenQuadRef;
        private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> offscreenBufferRef;
        private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> renderSsaoRef;
        private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> renderGodRaysRef;
        private static readonly MethodInfo handleIncludesMethod;
        private static readonly bool accessorsReady;

        private static volatile bool lumaEnabled;
        private static volatile bool lumaFailedOnDriver;
        private static volatile bool lumaActive;
        private static volatile int lumaAppliedTexId;

        private static volatile bool leanFinalEnabled;
        private static volatile bool gather4Active;
        private static ShaderProgram leanFinalProgram;
        private static ILogger logger;

        static LumaAndFinalOptimization()
        {
            try
            {
                frameBuffersRef = AccessTools.FieldRefAccess<ClientPlatformWindows, List<FrameBufferRef>>("frameBuffers");
                screenQuadRef = AccessTools.FieldRefAccess<ClientPlatformWindows, MeshRef>("screenQuad");
                offscreenBufferRef = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("OffscreenBuffer");
                renderSsaoRef = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderSSAO");
                renderGodRaysRef = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderGodRays");
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

        public static bool IsLumaRgba8Active => lumaActive && lumaEnabled && !lumaFailedOnDriver;
        public static bool IsLeanFinalReady => IsProgramUsable(leanFinalProgram);
        public static bool IsGather4Active => gather4Active && IsLeanFinalReady;

        public static void Configure(bool enableLumaRgba8, bool enableLeanFinal, ILogger modLogger = null)
        {
            lumaEnabled = enableLumaRgba8;
            leanFinalEnabled = enableLeanFinal;
            if (modLogger != null)
            {
                logger = modLogger;
            }
        }

        public static void Cleanup(ICoreClientAPI api = null)
        {
            lumaEnabled = false;
            lumaFailedOnDriver = false;
            lumaActive = false;
            lumaAppliedTexId = 0;
            leanFinalEnabled = false;
            gather4Active = false;
            DisposeProgram(api);
            logger = null;
        }

        internal static bool CanUseLeanFinal(float glitchStrength, float frostVignetting, float damageVignetting)
        {
            return glitchStrength <= 0.0001f &&
                frostVignetting <= 0.0001f &&
                damageVignetting <= 0.0001f;
        }

        internal static bool IsEightBitRgbaFormat(int glInternalFormat)
        {
            return glInternalFormat == (int)PixelInternalFormat.Rgba8 ||
                glInternalFormat == (int)PixelInternalFormat.Srgb8Alpha8 ||
                glInternalFormat == (int)PixelInternalFormat.Rgba;
        }

        internal static bool TryGetLumaBuffer(List<FrameBufferRef> buffers, out FrameBufferRef lumaFb)
        {
            lumaFb = null;
            if (buffers == null || buffers.Count <= (int)EnumFrameBuffer.Luma)
            {
                return false;
            }

            FrameBufferRef fb = buffers[(int)EnumFrameBuffer.Luma];
            if (fb == null || fb.Disposed || fb.FboId <= 0 || fb.Width < 1 || fb.Height < 1 ||
                fb.ColorTextureIds == null || fb.ColorTextureIds.Length == 0 || fb.ColorTextureIds[0] <= 0)
            {
                return false;
            }

            lumaFb = fb;
            return true;
        }

        public static void SetupDefaultFrameBuffers_Postfix(List<FrameBufferRef> __result)
        {
            ApplyLumaRgba8(__result, logger);
        }

        public static bool ApplyLumaRgba8(List<FrameBufferRef> buffers, ILogger modLogger = null)
        {
            if (modLogger != null)
            {
                logger = modLogger;
            }

            if (!lumaEnabled || lumaFailedOnDriver)
            {
                lumaActive = false;
                lumaAppliedTexId = 0;
                return false;
            }

            if (!TryGetLumaBuffer(buffers, out FrameBufferRef fb))
            {
                lumaActive = false;
                lumaAppliedTexId = 0;
                return false;
            }

            int texId = fb.ColorTextureIds[0];
            if (lumaActive && lumaAppliedTexId == texId)
            {
                return true;
            }

            int prevDrawFbo = 0;
            int prevReadFbo = 0;
            int prevTex = 0;
            bool stateSaved = false;

            try
            {
                GL.GetInteger(GetPName.DrawFramebufferBinding, out prevDrawFbo);
                GL.GetInteger(GetPName.ReadFramebufferBinding, out prevReadFbo);
                GL.GetInteger(GetPName.TextureBinding2D, out prevTex);
                stateSaved = true;

                // Only narrow Luma to RGBA8 when Primary[0] is actually 8-bit; fail open if an HDR mod
                // upgraded Primary[0] to RGBA16F/RGBA32F so highlights above 1.0 are never clamped.
                FrameBufferRef primary = buffers[(int)EnumFrameBuffer.Primary];
                if (primary == null || primary.Disposed || primary.ColorTextureIds == null ||
                    primary.ColorTextureIds.Length == 0 || primary.ColorTextureIds[0] <= 0)
                {
                    lumaActive = false;
                    lumaAppliedTexId = 0;
                    return false;
                }

                GL.BindTexture(TextureTarget.Texture2D, primary.ColorTextureIds[0]);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int primaryFormat);
                if (!IsEightBitRgbaFormat(primaryFormat))
                {
                    lumaActive = false;
                    lumaAppliedTexId = 0;
                    logger?.Notification("[OptiTime] Primary color buffer uses non-8-bit format (0x{0:X}); keeping RGBA16F Luma buffer", primaryFormat);
                    return false;
                }

                if (!ReallocateLuma(fb, PixelInternalFormat.Rgba8, PixelType.UnsignedByte))
                {
                    ReallocateLuma(fb, PixelInternalFormat.Rgba16f, PixelType.UnsignedShort);
                    lumaFailedOnDriver = true;
                    lumaActive = false;
                    lumaAppliedTexId = 0;
                    logger?.Warning("[OptiTime] RGBA8 Luma framebuffer unsupported on this driver; keeping RGBA16F");
                    return false;
                }

                lumaAppliedTexId = texId;
                lumaActive = true;
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    ReallocateLuma(fb, PixelInternalFormat.Rgba16f, PixelType.UnsignedShort);
                }
                catch
                {
                }

                lumaFailedOnDriver = true;
                lumaActive = false;
                lumaAppliedTexId = 0;
                logger?.Warning("[OptiTime] RGBA8 Luma framebuffer failed; keeping RGBA16F: " + ex.Message);
                return false;
            }
            finally
            {
                if (stateSaved)
                {
                    try
                    {
                        GL.BindTexture(TextureTarget.Texture2D, prevTex);
                        if (prevDrawFbo == prevReadFbo)
                        {
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevDrawFbo);
                        }
                        else
                        {
                            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, prevDrawFbo);
                            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, prevReadFbo);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static bool RebuildShader(ICoreClientAPI api)
        {
            DisposeProgram(api);
            gather4Active = false;
            if (!leanFinalEnabled || !accessorsReady || api?.Shader == null)
            {
                return false;
            }

            try
            {
                ShaderProgramFinal baseFinal = ShaderPrograms.Final;
                if (baseFinal == null || baseFinal.Disposed || baseFinal.LoadError)
                {
                    return false;
                }

                string vertexCode = baseFinal.VertexShader?.Code;
                if (string.IsNullOrEmpty(vertexCode))
                {
                    return false;
                }

                bool tryGather4 = SupportsGather4Alpha();
                if (tryGather4)
                {
                    leanFinalProgram = BuildFinalVariant(api, vertexCode, enableGather4: true);
                    if (IsProgramUsable(leanFinalProgram))
                    {
                        gather4Active = true;
                        return true;
                    }
                }

                leanFinalProgram = BuildFinalVariant(api, vertexCode, enableGather4: false);
                return IsProgramUsable(leanFinalProgram);
            }
            catch (Exception ex)
            {
                DisposeProgram(api);
                logger?.Warning("[OptiTime] Lean final composition shader setup failed; using vanilla final: " + ex.Message);
                return false;
            }
        }

        public static bool RenderFinalComposition_Prefix(ClientPlatformWindows __instance)
        {
            if (!leanFinalEnabled || !accessorsReady || __instance == null || !IsProgramUsable(leanFinalProgram))
            {
                return true;
            }

            if (!offscreenBufferRef(__instance))
            {
                return false;
            }

            DefaultShaderUniforms sh = __instance.ShaderUniforms;
            if (sh == null || !CanUseLeanFinal(sh.GlitchStrength, sh.FrostVignetting, sh.DamageVignetting))
            {
                return true;
            }

            List<FrameBufferRef> buffers = frameBuffersRef(__instance);
            MeshRef screenQuad = screenQuadRef(__instance);
            if (buffers == null || buffers.Count <= 14 || screenQuad == null)
            {
                return true;
            }

            FrameBufferRef primaryFb = buffers[0];
            if (primaryFb == null || primaryFb.Width <= 0 || primaryFb.Height <= 0)
            {
                return true;
            }

            bool renderSsao = renderSsaoRef(__instance);
            bool renderGodRays = renderGodRaysRef(__instance);

            int bloomParts2D = buffers[8].ColorTextureIds[0];
            int godrayParts2D = buffers[7].ColorTextureIds[0];
            int primaryScene2D = buffers[10].ColorTextureIds[0];

            GL.DrawBuffers(1, DrawBuffers1);
            GL.Disable(EnableCap.DepthTest);
            __instance.GlToggleBlend(on: true);

            ShaderProgram final = leanFinalProgram;
            final.Use();
            try
            {
                final.BindTexture2D("primaryScene", primaryScene2D, 0);
                if (final.HasUniform("bloomParts"))
                {
                    final.BindTexture2D("bloomParts", bloomParts2D, 1);
                }

                if (final.HasUniform("glowParts"))
                {
                    final.BindTexture2D("glowParts", primaryFb.ColorTextureIds[1], 2);
                }

                if (final.HasUniform("godrayParts"))
                {
                    final.BindTexture2D("godrayParts", godrayParts2D, 3);
                }

                if (final.HasUniform("ambientBloomLevel"))
                {
                    final.Uniform(
                        "ambientBloomLevel",
                        ClientSettings.AmbientBloomLevel / 100f +
                        sh.AmbientBloomLevelAdd[0] +
                        sh.AmbientBloomLevelAdd[1] +
                        sh.AmbientBloomLevelAdd[2] +
                        sh.AmbientBloomLevelAdd[3]);
                }

                if (renderSsao && final.HasUniform("ssaoScene"))
                {
                    final.BindTexture2D("ssaoScene", buffers[14].ColorTextureIds[0], 4);
                }

                final.Uniform(
                    "invFrameSizeIn",
                    1f / (float)primaryFb.Width,
                    1f / (float)primaryFb.Height);
                final.Uniform("gammaLevel", ClientSettings.GammaLevel);
                final.Uniform("extraGamma", ClientSettings.ExtraGammaLevel);
                final.Uniform("contrastLevel", sh.ExtraContrastLevel);
                final.Uniform(
                    "brightnessLevel",
                    ClientSettings.BrightnessLevel + Math.Max(0f, sh.DropShadowIntensity * 2f - 1.66f) / 3f);
                final.Uniform("sepiaLevel", sh.SepiaLevel + sh.ExtraSepia);

                if (renderGodRays)
                {
                    if (final.HasUniform("sunPosScreenIn"))
                    {
                        final.Uniform("sunPosScreenIn", sh.SunPositionScreen);
                    }

                    if (final.HasUniform("sunPos3dIn"))
                    {
                        final.Uniform("sunPos3dIn", sh.SunPosition3D);
                    }

                    if (final.HasUniform("playerViewVector"))
                    {
                        final.Uniform("playerViewVector", sh.PlayerViewVector);
                    }
                }

                __instance.RenderFullscreenTriangle(screenQuad);
            }
            finally
            {
                if (ShaderProgramBase.CurrentShaderProgram == final)
                {
                    final.Stop();
                }

                if (renderSsao)
                {
                    GL.DrawBuffers(4, DrawBuffers4);
                }
                else
                {
                    GL.DrawBuffers(2, DrawBuffers2);
                }
            }

            return false;
        }

        internal static string BuildLeanFinalFragmentSource(bool enableGather4)
        {
            string extHeader = enableGather4
                ? "#extension GL_ARB_gpu_shader5 : enable\n"
                : string.Empty;

            return
                "#version 330 core\n" +
                extHeader +
                "uniform sampler2D primaryScene;\n" +
                "uniform sampler2D glowParts;\n" +
                "uniform sampler2D bloomParts;\n" +
                "uniform sampler2D godrayParts;\n" +
                "uniform sampler2D ssaoScene;\n" +
                "uniform float gammaLevel;\n" +
                "uniform float brightnessLevel;\n" +
                "uniform float contrastLevel;\n" +
                "uniform float sepiaLevel;\n" +
                "uniform float ambientBloomLevel;\n" +
                "uniform float extraGamma = 1.0;\n" +
                "uniform float minlight = 0.0;\n" +
                "uniform float maxlight = 1.0;\n" +
                "uniform float minsat = 0.0;\n" +
                "uniform float maxsat = 1.0;\n" +
                "in vec2 invFrameSize;\n" +
                "in vec2 texCoord;\n" +
                "flat in float godrayIntensity;\n" +
                "layout(location = 0) out vec4 outColor;\n" +
                "#include fxaa.fsh\n" +
                "#include colorutil.ash\n" +
                "vec4 ColorGrade(vec4 color) {\n" +
                "    color.a = dot(color.rgb, vec3(0.299, 0.587, 0.114));\n" +
                "    vec3 hsl = rgb2hsl(color.rgb);\n" +
                "    float lightRange = maxlight - minlight;\n" +
                "    float satRange = maxsat - minsat;\n" +
                "    hsl.z = pow((clamp(hsl.z, minlight, maxlight) - minlight) / lightRange, 1.0 / gammaLevel);\n" +
                "    hsl.y = (clamp(hsl.y, minsat, maxsat) - minsat) / satRange;\n" +
                "    color.rgb = hsl2rgb(hsl);\n" +
                "    color.rgb = pow(color.rgb, vec3(1.0 / extraGamma));\n" +
                "    color.rgb *= brightnessLevel;\n" +
                "    vec3 sepia = vec3(\n" +
                "        (color.r * 0.393) + (color.g * 0.769) + (color.b * 0.189),\n" +
                "        (color.r * 0.349) + (color.g * 0.686) + (color.b * 0.168),\n" +
                "        (color.r * 0.272) + (color.g * 0.534) + (color.b * 0.131)\n" +
                "    ) * 0.85;\n" +
                "    color.rgb = mix(color.rgb, sepia, sepiaLevel);\n" +
                "    color.rgb = color.rgb * (contrastLevel + 1.0) - contrastLevel;\n" +
                "    float b = (color.r + color.b + color.g) / 3.0;\n" +
                "    color.rgb /= max(1.0, b);\n" +
                "    color.rgb = min(vec3(1.0), color.rgb);\n" +
                "    return color;\n" +
                "}\n" +
                "void main(void)\n" +
                "{\n" +
                "#if FXAA == 1\n" +
                "    vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);\n" +
                "#else\n" +
                "    vec4 color = texture(primaryScene, texCoord);\n" +
                "#endif\n" +
                "    color.a = 1.0;\n" +
                "    float bloomSub = 0.0;\n" +
                "#if BLOOM == 1\n" +
                "    vec4 bloomCol = texture(bloomParts, texCoord);\n" +
                "    float glowLevel = texture(glowParts, texCoord).r;\n" +
                "    float ambLevel = ambientBloomLevel / 2.0;\n" +
                "    color.rgb = (color.rgb + bloomCol.rgb * (ambLevel * 1.5)) / (1.0 + ambLevel);\n" +
                "    bloomSub = glowLevel * (bloomCol.r + bloomCol.b + bloomCol.g);\n" +
                "#endif\n" +
                "#if SSAOLEVEL > 0\n" +
                "#if SSAOLEVEL > 1\n" +
                "    float ssao = min(texture(ssaoScene, texCoord).r, texture(ssaoScene, texCoord - vec2(0.0, invFrameSize.y)).r);\n" +
                "#else\n" +
                "    float ssao = texture(ssaoScene, texCoord).r;\n" +
                "#endif\n" +
                "    color.rgb *= min(1.0, ssao + bloomSub);\n" +
                "#endif\n" +
                "#if GODRAYS > 0\n" +
                "    vec4 grc = texture(godrayParts, texCoord);\n" +
                "    color.rgb += grc.rgb;\n" +
                "    color.rgb = min(color.rgb, vec3(1.0));\n" +
                "    color.a = 1.0;\n" +
                "#endif\n" +
                "    vec4 gradedColor = ColorGrade(color);\n" +
                "    outColor = mix(color, gradedColor, gradedColor.a);\n" +
                "    outColor.a = 1.0;\n" +
                "}\n";
        }

        internal static string ApplyGather4Override(string expandedFragSource, bool enableGather4)
        {
            if (!enableGather4 || string.IsNullOrEmpty(expandedFragSource))
            {
                return expandedFragSource;
            }

            return expandedFragSource.Replace("#define FXAA_GATHER4_ALPHA 0", "#define FXAA_GATHER4_ALPHA 1");
        }

        private static ShaderProgram BuildFinalVariant(ICoreClientAPI api, string vertexCode, bool enableGather4)
        {
            var program = new ShaderProgram
            {
                LoadFromFile = false,
                Oit = false
            };

            string rawFrag = BuildLeanFinalFragmentSource(enableGather4);
            string expandedFrag = (string)handleIncludesMethod.Invoke(null, new object[] { program, rawFrag, null });
            if (string.IsNullOrEmpty(expandedFrag))
            {
                return null;
            }

            expandedFrag = ApplyGather4Override(expandedFrag, enableGather4);

            program.VertexShader = new Shader(EnumShaderType.VertexShader, vertexCode, "final_optitime.vsh");
            program.FragmentShader = new Shader(EnumShaderType.FragmentShader, expandedFrag, "final_optitime.fsh");

            api.Shader.RegisterMemoryShaderProgram(LeanFinalProgramName, program);
            if (!program.Compile() || program.LoadError || program.ProgramId <= 0)
            {
                TryDisposeProgram(program);
                return null;
            }

            return program;
        }

        private static bool SupportsGather4Alpha()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return false;
            }

            try
            {
                GL.GetInteger(GetPName.MajorVersion, out int major);
                if (major >= 4)
                {
                    return true;
                }

                GL.GetInteger(GetPName.NumExtensions, out int count);
                for (int i = 0; i < count; i++)
                {
                    string ext = GL.GetString(StringNameIndexed.Extensions, i);
                    if (string.Equals(ext, "GL_ARB_gpu_shader5", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool ReallocateLuma(FrameBufferRef fb, PixelInternalFormat internalFormat, PixelType pixelType)
        {
            int texId = fb.ColorTextureIds[0];

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb.FboId);
            GL.BindTexture(TextureTarget.Texture2D, texId);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                internalFormat,
                fb.Width,
                fb.Height,
                0,
                PixelFormat.Rgba,
                pixelType,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texId,
                0);

            return GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete;
        }

        private static void DisposeProgram(ICoreClientAPI api)
        {
            ShaderProgram prev = leanFinalProgram;
            leanFinalProgram = null;
            TryDisposeProgram(prev);

            if (api?.Shader != null)
            {
                try
                {
                    IShaderProgram registered = api.Shader.GetProgramByName(LeanFinalProgramName);
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
