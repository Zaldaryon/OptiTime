using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
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
        private const string SolidShadowNoWarpProgramName = "optitime_chunkshadowmap_solid_nowarp";

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

        private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> cullerPlayerPosRef;
        private static readonly MethodInfo handleIncludesMethod;

        private static Action<string> logger;
        private static bool farVegetationCullEnabled;
        private static bool solidDepthPassEnabled;
        private static volatile bool shadowLodDedupeEnabled;
        private static ShaderProgram solidShadowProgram;
        private static ShaderProgram solidShadowNoWarpProgram;

        static ShadowOptimization()
        {
            try
            {
                cullerPlayerPosRef = AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");
            }
            catch
            {
            }

            try
            {
                handleIncludesMethod = AccessTools.Method(
                    typeof(ShaderRegistry),
                    "HandleIncludes",
                    new[] { typeof(ShaderProgram), typeof(string), typeof(HashSet<string>) });
            }
            catch
            {
            }
        }

        public static void SetLogger(Action<string> log) => logger = log;

        public static void Configure(bool farVegetationCull, bool solidDepthPass, bool lodDedupe = true)
        {
            farVegetationCullEnabled = farVegetationCull;
            solidDepthPassEnabled = solidDepthPass;
            shadowLodDedupeEnabled = lodDedupe;
        }

        public static bool IsSolidShadowShaderReady =>
            solidDepthPassEnabled &&
            IsProgramUsable(solidShadowProgram);

        public static bool IsSolidShadowNoWarpReady =>
            solidDepthPassEnabled &&
            IsProgramUsable(solidShadowNoWarpProgram);

        /// <summary>
        /// Compiles or recompiles stripped depth-only shadow shaders for solid terrain passes:
        /// - Pass 0 (Opaque): strips uv unpacking and uv interpolator output while preserving vertex/global warping.
        /// - Pass 5 (TopSoil): additionally strips applyVertexWarping (topsoil never vertex-warps in chunktopsoil.vsh).
        /// Falls back cleanly to cloning Chunkshadowmap's vertex shader if include expansion is unavailable.
        /// </summary>
        public static bool RebuildSolidShadowShader(ICoreClientAPI api)
        {
            DisposeSolidShadowPrograms(api);
            if (!solidDepthPassEnabled || api?.Shader == null)
            {
                return false;
            }

            try
            {
                ShaderProgramChunkshadowmap baseProgram = ShaderPrograms.Chunkshadowmap;
                if (baseProgram == null || baseProgram.Disposed || baseProgram.LoadError)
                {
                    return false;
                }

                solidShadowProgram = BuildSolidShadowVariant(api, baseProgram, SolidShadowProgramName, includeVertexWarp: true);
                solidShadowNoWarpProgram = BuildSolidShadowVariant(api, baseProgram, SolidShadowNoWarpProgramName, includeVertexWarp: false);

                if (!IsProgramUsable(solidShadowProgram))
                {
                    // Fallback to cloning baseProgram.VertexShader.Code verbatim.
                    string fallbackCode = baseProgram.VertexShader?.Code;
                    if (!string.IsNullOrEmpty(fallbackCode))
                    {
                        var fallback = new ShaderProgram
                        {
                            LoadFromFile = false,
                            Oit = false,
                            VertexShader = new Shader(EnumShaderType.VertexShader, fallbackCode, "chunkshadowmapsolid.vsh"),
                            FragmentShader = new Shader(EnumShaderType.FragmentShader, SolidShadowFragmentSource, "chunkshadowmapsolid.fsh")
                        };

                        if (baseProgram.includes != null)
                        {
                            foreach (string inc in baseProgram.includes)
                            {
                                fallback.includes.Add(inc);
                            }
                        }

                        api.Shader.RegisterMemoryShaderProgram(SolidShadowProgramName, fallback);
                        if (fallback.Compile() && !fallback.LoadError && fallback.ProgramId > 0)
                        {
                            solidShadowProgram = fallback;
                        }
                        else
                        {
                            TryDisposeProgram(fallback);
                        }
                    }
                }

                return IsSolidShadowShaderReady;
            }
            catch (Exception ex)
            {
                DisposeSolidShadowPrograms(api);
                logger?.Invoke("[OptiTime] Solid shadow depth shader setup failed: " + ex.Message);
                return false;
            }
        }

        internal static string BuildSolidShadowVertexSource(bool includeVertexWarp)
        {
            string warpLine = includeVertexWarp
                ? "#if USESSBO > 0\n    worldPos = applyVertexWarping(vdata.flags[vIndex], worldPos);\n#else\n    worldPos = applyVertexWarping(renderFlagsIn, worldPos);\n#endif\n"
                : string.Empty;

            return
                "#version 330 core\n" +
                "#extension GL_ARB_explicit_attrib_location: enable\n" +
                "#if USESSBO > 0\n" +
                "layout(location = 0) in vec4 rgbaLightIn;\n" +
                "#else\n" +
                "layout(location = 0) in vec3 xyz;\n" +
                "layout(location = 1) in vec2 uvIn;\n" +
                "layout(location = 2) in vec4 rgbaLightIn;\n" +
                "layout(location = 3) in int renderFlagsIn;\n" +
                "#endif\n" +
                "uniform vec3 origin;\n" +
                "uniform mat4 mvpMatrix;\n" +
                "#include vertexflagbits.ash\n" +
                "#include vertexwarp.vsh\n" +
                "#if USESSBO > 0\n" +
                "layout(binding = 3, std430) readonly buffer faceDataBuf { FaceData faces[]; };\n" +
                "#endif\n" +
                "void main(void)\n" +
                "{\n" +
                "#if USESSBO > 0\n" +
                "    FaceData vdata = faces[gl_VertexID / 4];\n" +
                "    int vIndex = gl_VertexID & 0x03;\n" +
                "    vec3 xyz = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;\n" +
                "#endif\n" +
                "    vec4 worldPos = vec4(xyz + origin, 1.0);\n" +
                warpLine +
                "    worldPos = applyGlobalWarping(worldPos);\n" +
                "    gl_Position = mvpMatrix * worldPos;\n" +
                "}\n";
        }

        internal static bool PassesShadowLodExclusivity(int lodLevel, double horDistSq, double lod2BiasSq)
        {
            if (lodLevel == 2)
            {
                return horDistSq <= lod2BiasSq;
            }

            if (lodLevel == 3)
            {
                return horDistSq > lod2BiasSq;
            }

            return true;
        }

        /// <summary>
        /// Enforces LodLevel 2 / LodLevel 3 mutual exclusion during shadow passes as a non-blocking
        /// Postfix, matching FrustumCulling.InFrustumAndRange so blocks with Lod2Mesh are not drawn
        /// twice into ShadowmapNear and ShadowmapFar without overriding other mods' prefixes.
        /// </summary>
        public static void IsVisible_Postfix(ModelDataPoolLocation __instance, EnumFrustumCullMode mode, FrustumCulling culler, ref bool __result)
        {
            if (!__result || !shadowLodDedupeEnabled || cullerPlayerPosRef == null || __instance == null || culler == null)
            {
                return;
            }

            if (mode != EnumFrustumCullMode.CullInstantShadowPassNear &&
                mode != EnumFrustumCullMode.CullInstantShadowPassFar)
            {
                return;
            }

            int lod = __instance.LodLevel;
            if (lod != 2 && lod != 3)
            {
                return;
            }

            double lod2BiasSq = culler.lod2BiasSq;
            BlockPos playerPos = cullerPlayerPosRef(culler);
            if (playerPos == null || lod2BiasSq <= 1.0)
            {
                return;
            }

            Sphere sphere = __instance.FrustumCullSphere;
            double horDistSq = playerPos.HorDistanceSqTo(sphere.x, sphere.z);
            __result = PassesShadowLodExclusivity(lod, horDistSq, lod2BiasSq);
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
            ShaderProgram noWarpProgram = solidShadowNoWarpProgram;
            bool useSolidDepth = solidDepthPassEnabled &&
                shadowMvpMatrix != null &&
                IsProgramUsable(solidProgram);

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
                    if (IsProgramUsable(noWarpProgram))
                    {
                        solidProgram.Stop();
                        noWarpProgram.Use();
                        noWarpProgram.UniformMatrix("mvpMatrix", shadowMvpMatrix);
                        for (int j = 0; j < textureIds.Length; j++)
                        {
                            topSoilPools[j].Render(cameraPos, "origin", frustumCullMode);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < textureIds.Length; j++)
                        {
                            topSoilPools[j].Render(cameraPos, "origin", frustumCullMode);
                        }
                    }
                    ScreenManager.FrameProfiler.Mark("rend3D-rets-tpp");
                }
                finally
                {
                    if (ShaderProgramBase.CurrentShaderProgram == solidProgram)
                    {
                        solidProgram.Stop();
                    }
                    else if (noWarpProgram != null && ShaderProgramBase.CurrentShaderProgram == noWarpProgram)
                    {
                        noWarpProgram.Stop();
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

        private static ShaderProgram BuildSolidShadowVariant(
            ICoreClientAPI api,
            ShaderProgramChunkshadowmap baseProgram,
            string programName,
            bool includeVertexWarp)
        {
            if (handleIncludesMethod == null)
            {
                return null;
            }

            var program = new ShaderProgram
            {
                LoadFromFile = false,
                Oit = false
            };

            string expandedVert = (string)handleIncludesMethod.Invoke(
                null,
                new object[] { program, BuildSolidShadowVertexSource(includeVertexWarp), null });
            if (string.IsNullOrEmpty(expandedVert))
            {
                return null;
            }

            // Vertex filename MUST start with "chunk" so Shader.UsesSSBOs() promotes #version 430.
            string vshName = includeVertexWarp ? "chunkshadowmapsolid.vsh" : "chunkshadowmapsolidnowarp.vsh";
            string fshName = includeVertexWarp ? "chunkshadowmapsolid.fsh" : "chunkshadowmapsolidnowarp.fsh";

            program.VertexShader = new Shader(EnumShaderType.VertexShader, expandedVert, vshName);
            program.FragmentShader = new Shader(EnumShaderType.FragmentShader, SolidShadowFragmentSource, fshName);

            api.Shader.RegisterMemoryShaderProgram(programName, program);
            if (!program.Compile() || program.LoadError || program.ProgramId <= 0)
            {
                TryDisposeProgram(program);
                return null;
            }

            return program;
        }

        private static bool IsProgramUsable(ShaderProgram program)
        {
            return program != null && !program.Disposed && !program.LoadError && program.ProgramId > 0;
        }

        private static void DisposeSolidShadowPrograms(ICoreClientAPI api)
        {
            DisposeOne(api, ref solidShadowProgram, SolidShadowProgramName);
            DisposeOne(api, ref solidShadowNoWarpProgram, SolidShadowNoWarpProgramName);
        }

        private static void DisposeOne(ICoreClientAPI api, ref ShaderProgram field, string name)
        {
            ShaderProgram existing = field;
            field = null;
            TryDisposeProgram(existing);

            if (api?.Shader != null)
            {
                try
                {
                    if (api.Shader.GetProgramByName(name) is ShaderProgram registered &&
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
            DisposeSolidShadowPrograms(null);
            farVegetationCullEnabled = false;
            solidDepthPassEnabled = false;
            shadowLodDedupeEnabled = false;
            logger = null;
        }
    }
}
