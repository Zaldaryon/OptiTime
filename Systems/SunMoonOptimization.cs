using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Replaces SystemRenderSunMoon's blocking single-query occlusion poll with a non-blocking
    /// 4-slot GL query ring, skips OnRenderFrame3DPost when the sun is below the horizon, outside
    /// the camera clip frustum, or when all ring slots are still in flight, culls off-frustum
    /// sun and moon draws in OnRenderFrame3D, disposes ring queries on cleanup, and eliminates
    /// per-frame matrix and vector allocations.
    /// </summary>
    public static class SunMoonOptimization
    {
        private const int QueryRingSize = 4;
        private const int QueryRingMask = QueryRingSize - 1;
        private const float SunHorizonCutoff = -0.05f;
        private const float SunSpecularSampleDivisor = 1500f;
        private const float MaxMoonProximityFactor = 0.2f;
        private const double ClipMargin = 1.002;

        private const int OutLeft = 1 << 0;
        private const int OutRight = 1 << 1;
        private const int OutBottom = 1 << 2;
        private const int OutTop = 1 << 3;
        private const int OutNear = 1 << 4;
        private const int OutFar = 1 << 5;
        private const int OutAll = OutLeft | OutRight | OutBottom | OutTop | OutNear | OutFar;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, MeshRef> quadModelRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, int> sunTextureIdRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, int[]> moonTextureIdsRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, int> imageSizeRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, Matrix4> sunMatRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, Matrix4> moonMatRef;
        private static readonly AccessTools.FieldRef<SystemRenderSunMoon, float> targetSunSpecRef;
        private static readonly AccessTools.FieldRef<ClientMain, int> skyTextureIdRef;
        private static readonly AccessTools.FieldRef<ClientMain, int> skyGlowTextureIdRef;
        private static readonly AccessTools.FieldRef<ClientMain, int> frameSeedRef;
        private static readonly AccessTools.FieldRef<DefaultShaderUniforms, float> skyDaylightRef;
        private static readonly bool accessorsReady;

        private static readonly object stateLock = new object();
        private static readonly List<WeakReference<SunMoonState>> activeStates = new List<WeakReference<SunMoonState>>();
        private static ConditionalWeakTable<SystemRenderSunMoon, SunMoonState> states =
            new ConditionalWeakTable<SystemRenderSunMoon, SunMoonState>();

        private static volatile bool clientActive;

        private static System.Action<int[]> testGenQueries;
        private static System.Action<int[]> testDeleteQueries;
        private static System.Func<int, bool> testIsQueryAvailable;
        private static System.Func<int, int> testGetQueryResult;
        private static System.Action<int> testBeginQuery;
        private static System.Action testEndQuery;
        private static System.Action<bool> testSetColorMask;
        private static System.Action<SystemRenderSunMoon, ClientMain, Matrix4> testRenderOcclusionQuad;
        private static System.Action<ClientMain> testRestoreDepthMask;

        static SunMoonOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
                quadModelRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, MeshRef>("quadModel");
                sunTextureIdRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, int>("suntextureId");
                moonTextureIdsRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, int[]>("moontextureIds");
                imageSizeRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, int>("ImageSize");
                sunMatRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, Matrix4>("sunmat");
                moonMatRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, Matrix4>("moonmat");
                targetSunSpecRef = AccessTools.FieldRefAccess<SystemRenderSunMoon, float>("targetSunSpec");
                skyTextureIdRef = AccessTools.FieldRefAccess<ClientMain, int>("skyTextureId");
                skyGlowTextureIdRef = AccessTools.FieldRefAccess<ClientMain, int>("skyGlowTextureId");
                frameSeedRef = AccessTools.FieldRefAccess<ClientMain, int>("frameSeed");
                skyDaylightRef = AccessTools.FieldRefAccess<DefaultShaderUniforms, float>("SkyDaylight");
                accessorsReady = true;
            }
            catch
            {
                accessorsReady = false;
            }
        }

        public static void SetClientActive(bool active)
        {
            clientActive = active;
        }

        internal static void SetTestHooks(
            System.Action<int[]> genQueries,
            System.Action<int[]> deleteQueries,
            System.Func<int, bool> isQueryAvailable,
            System.Func<int, int> getQueryResult,
            System.Action<int> beginQuery,
            System.Action endQuery,
            System.Action<bool> setColorMask,
            System.Action<SystemRenderSunMoon, ClientMain, Matrix4> renderOcclusionQuad,
            System.Action<ClientMain> restoreDepthMask)
        {
            testGenQueries = genQueries;
            testDeleteQueries = deleteQueries;
            testIsQueryAvailable = isQueryAvailable;
            testGetQueryResult = getQueryResult;
            testBeginQuery = beginQuery;
            testEndQuery = endQuery;
            testSetColorMask = setColorMask;
            testRenderOcclusionQuad = renderOcclusionQuad;
            testRestoreDepthMask = restoreDepthMask;
        }

        public static void Cleanup()
        {
            clientActive = false;

            List<SunMoonState> toDispose = null;
            lock (stateLock)
            {
                for (int i = 0; i < activeStates.Count; i++)
                {
                    if (activeStates[i].TryGetTarget(out SunMoonState state) && !state.disposed)
                    {
                        toDispose ??= new List<SunMoonState>();
                        toDispose.Add(state);
                    }
                }

                activeStates.Clear();
                states = new ConditionalWeakTable<SystemRenderSunMoon, SunMoonState>();
            }

            if (toDispose != null)
            {
                for (int i = 0; i < toDispose.Count; i++)
                {
                    try
                    {
                        DisposeState(toDispose[i]);
                    }
                    catch
                    {
                        // Ignore GL context teardown races during final mod unload.
                    }
                }
            }

            testGenQueries = null;
            testDeleteQueries = null;
            testIsQueryAvailable = null;
            testGetQueryResult = null;
            testBeginQuery = null;
            testEndQuery = null;
            testSetColorMask = null;
            testRenderOcclusionQuad = null;
            testRestoreDepthMask = null;
        }

        public static bool OnRenderFrame3DPost_Prefix(SystemRenderSunMoon __instance)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            if (game?.Calendar == null)
            {
                return true;
            }

            SunMoonState state = GetOrCreateState(__instance);
            if (state.disposed)
            {
                return false;
            }

            bool harvested = false;
            int latestSamples = 0;
            if (state.queriesAllocated)
            {
                while (state.inFlightCount > 0)
                {
                    int queryId = state.queryIds[state.head];
                    if (!IsQueryAvailable(queryId))
                    {
                        break;
                    }

                    latestSamples = GetQueryResult(queryId);
                    harvested = true;
                    state.head = (state.head + 1) & QueryRingMask;
                    state.inFlightCount--;
                }
            }

            Vec3f sunPositionNormalized = game.Calendar.SunPositionNormalized;
            if (sunPositionNormalized != null && sunPositionNormalized.Y < SunHorizonCutoff)
            {
                targetSunSpecRef(__instance) = 0f;
                return false;
            }

            if (harvested)
            {
                targetSunSpecRef(__instance) = GameMath.Clamp(latestSamples / SunSpecularSampleDivisor, 0f, 1f);
            }

            Vec3f sunPosition = game.Calendar.SunPosition;
            int imageSize = imageSizeRef(__instance);
            float sunScale = __instance.sunScale;
            var sunPosVec = new Vector3(sunPosition.X, sunPosition.Y, sunPosition.Z);
            Quaternion lookRotation = SystemRenderSunMoon.CreateLookRotation(sunPosVec);
            Matrix4 queryMat = Matrix4.CreateTranslation(-imageSize / 2f, -imageSize, -imageSize / 2f)
                * Matrix4.CreateScale(sunScale, sunScale * 7f, sunScale)
                * Matrix4.CreateFromQuaternion(lookRotation)
                * Matrix4.CreateTranslation(sunPosVec);
            sunMatRef(__instance) = queryMat;

            // Exact 4D homogeneous clip-space cull on the stretched 7x query quad: when all 4 corners
            // fall outside the same clip plane (and global vertex warp is inactive), the GPU clipper
            // discards 100% of fragments so SamplesPassed is guaranteed to be 0.
            IRenderAPI renderApi = game.api?.Render;
            if (renderApi != null && game.PMatrix != null && !IsGlobalWarpActive(game.shUniforms))
            {
                double[] projection = game.PMatrix.Top;
                double[] cameraOrigin = renderApi.CameraMatrixOrigin;
                if (projection != null && cameraOrigin != null)
                {
                    CopyMatrix4ToDouble(ref queryMat, state.modelDoubleScratch);
                    Mat4d.Mul(state.mvScratch, cameraOrigin, state.modelDoubleScratch);
                    Mat4d.Mul(state.mvpScratch, projection, state.mvScratch);
                    if (IsQuadOutsideClipFrustum(state.mvpScratch, imageSize))
                    {
                        targetSunSpecRef(__instance) = 0f;
                        return false;
                    }
                }
            }

            if (!state.queriesAllocated)
            {
                GenQueries(state.queryIds);
                state.queriesAllocated = true;
            }

            if (state.inFlightCount >= QueryRingSize)
            {
                return false;
            }

            int slotQueryId = state.queryIds[state.tail];
            bool queryBegun = false;
            try
            {
                SetColorMask(false);
                if (testRenderOcclusionQuad != null)
                {
                    BeginQuery(slotQueryId);
                    queryBegun = true;
                    testRenderOcclusionQuad(__instance, game, queryMat);
                }
                else
                {
                    RenderOcclusionQuadWithQuery(__instance, game, ref queryMat, slotQueryId, ref queryBegun);
                }
            }
            finally
            {
                if (queryBegun)
                {
                    EndQuery();
                    state.tail = (state.tail + 1) & QueryRingMask;
                    state.inFlightCount++;
                }

                RestoreDepthMask(game);
                SetColorMask(true);
            }

            return false;
        }

        public static bool OnRenderFrame3D_Prefix(SystemRenderSunMoon __instance, float dt)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            IRenderAPI renderApi = game?.api?.Render;
            if (game?.Platform == null || game.Calendar == null || renderApi == null || game.EntityPlayer == null)
            {
                return true;
            }

            SunMoonState state = GetOrCreateState(__instance);
            ClientPlatformAbstract platform = game.Platform;

            ref int sunTextureId = ref sunTextureIdRef(__instance);
            int[] moonTextureIds = moonTextureIdsRef(__instance);
            if (sunTextureId == -1)
            {
                sunTextureId = game.GetOrLoadCachedTexture(new AssetLocation("environment/sun.png"));
                for (int i = 0; i < 8; i++)
                {
                    moonTextureIds[i] = game.GetOrLoadCachedTexture(new AssetLocation("environment/moon/" + i + ".png"));
                }
            }

            int imageSize = imageSizeRef(__instance);
            Vec3f moonPosition = game.Calendar.MoonPosition;
            Vec3f sunPositionNormalized = game.Calendar.SunPositionNormalized;
            game.shUniforms.SunPosition3D = sunPositionNormalized;

            Vec3f moonDir = state.moonDirScratch;
            moonDir.Set(moonPosition.X, moonPosition.Y, moonPosition.Z).Normalize();

            float moonLightStrength = game.Calendar.MoonLightStrength;
            float sunLightStrength = game.Calendar.SunLightStrength;
            float lightLerp = GameMath.Clamp(50f * (moonLightStrength - sunLightStrength), 0f, 1f);
            game.shUniforms.LightPosition3D.Set(
                GameMath.Lerp(sunPositionNormalized.X, moonDir.X, lightLerp),
                GameMath.Lerp(sunPositionNormalized.Y, moonDir.Y, lightLerp),
                GameMath.Lerp(sunPositionNormalized.Z, moonDir.Z, lightLerp));

            double[] projection = game.PMatrix.Top;
            double[] cameraOrigin = renderApi.CameraMatrixOrigin;
            DefaultShaderUniforms shaderUniforms = renderApi.ShaderUniforms;
            bool allowStandardFrustumCull = !IsGlobalWarpActive(shaderUniforms);

            if (sunPositionNormalized.Y < SunHorizonCutoff)
            {
                CopyMatrixfToDouble(__instance.ModelMat.Values, state.modelDoubleScratch);
                Mat4d.Mul(state.mvScratch, cameraOrigin, state.modelDoubleScratch);
                Mat4d.Mul(state.mvpScratch, projection, state.mvScratch);
                Vec3f sunScreen = game.shUniforms.SunPositionScreen ?? (game.shUniforms.SunPositionScreen = new Vec3f());
                ProjectMvpToScreenNdc(
                    (float)imageSize / 4f,
                    (float)(-imageSize) / 4f,
                    0.0,
                    state.mvpScratch,
                    game.Width,
                    game.Height,
                    sunScreen);
            }

            __instance.prepareSunMat();
            ref Matrix4 sunMat = ref sunMatRef(__instance);

            CopyMatrix4ToDouble(ref sunMat, state.modelDoubleScratch);
            Mat4d.Mul(state.mvScratch, cameraOrigin, state.modelDoubleScratch);
            Mat4d.Mul(state.mvpScratch, projection, state.mvScratch);

            if (sunPositionNormalized.Y >= SunHorizonCutoff)
            {
                Vec3f sunScreen = game.shUniforms.SunPositionScreen ?? (game.shUniforms.SunPositionScreen = new Vec3f());
                ProjectMvpToScreenNdc(
                    (float)imageSize / 2f,
                    (float)imageSize / 2f,
                    0.0,
                    state.mvpScratch,
                    game.Width,
                    game.Height,
                    sunScreen);
            }

            bool drawSun = !allowStandardFrustumCull || !IsQuadOutsideClipFrustum(state.mvpScratch, imageSize);

            PrepareMoonMatNoAlloc(__instance, game, imageSize, moonPosition, moonDir, sunPositionNormalized);
            ref Matrix4 moonMat = ref moonMatRef(__instance);

            CopyMatrix4ToDouble(ref moonMat, state.modelDoubleScratch);
            Mat4d.Mul(state.mvScratch, cameraOrigin, state.modelDoubleScratch);
            Mat4d.Mul(state.mvpScratch, projection, state.mvScratch);
            bool drawMoon = !IsQuadOutsideClipFrustum(state.mvpScratch, imageSize);

            float targetSunSpec = targetSunSpecRef(__instance);
            game.shUniforms.SunSpecularIntensity = GameMath.Clamp(
                game.shUniforms.SunSpecularIntensity + (targetSunSpec - game.shUniforms.SunSpecularIntensity) * dt * 20f,
                0f,
                1f);

            if (!drawSun && !drawMoon)
            {
                return false;
            }

            MeshRef quadModel = quadModelRef(__instance);
            platform.GlToggleBlend(on: true);
            platform.GlDisableCullFace();
            platform.GlDisableDepthTest();

            if (drawSun)
            {
                Vec3f sourceSunColor = game.Calendar.SunColor;
                Vec3f sunColor = state.sunColorScratch;
                sunColor.Set(sourceSunColor.X, sourceSunColor.Y, sourceSunColor.Z);
                float avgSunColor = (sunColor.R + sunColor.G + sunColor.B) / 3f;
                float cloudDensity = game.AmbientManager.BlendedCloudDensity;
                float desatFactor = GameMath.Clamp(
                    GameMath.Max(game.AmbientManager.BlendedFlatFogDensity * 40f, cloudDensity * cloudDensity),
                    0f,
                    1f);
                sunColor.R = desatFactor * avgSunColor + (1f - desatFactor) * sunColor.R;
                sunColor.G = desatFactor * avgSunColor + (1f - desatFactor) * sunColor.G;
                sunColor.B = desatFactor * avgSunColor + (1f - desatFactor) * sunColor.B;

                Vec4f sunTint = state.sunTintScratch;
                sunTint.Set(1f, 1f, 1f, 1f);
                if (shaderUniforms.FogSphereQuantity > 0)
                {
                    float[] fogSpheres = shaderUniforms.FogSpheres;
                    for (int i = 0; i < shaderUniforms.FogSphereQuantity; i++)
                    {
                        int offset = i * 8;
                        float dx = fogSpheres[offset];
                        float dy = fogSpheres[offset + 1];
                        float dz = fogSpheres[offset + 2];
                        float radius = fogSpheres[offset + 3];
                        float density = fogSpheres[offset + 4];
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        double attenuation = (1.0 - dist / (double)radius) * (double)radius * (double)density;
                        sunTint.A = (float)GameMath.Clamp((double)sunTint.A - attenuation, 0.0, 1.0);
                    }
                }

                state.sunLightScratch.Set(sunColor.R, sunColor.G, sunColor.B, 1f);

                ShaderProgramStandard standard = ShaderPrograms.Standard;
                standard.Use();
                standard.Uniform("skyShaded", 1);
                standard.FadeFromSpheresFog = 1;
                standard.RgbaTint = sunTint;
                standard.RgbaAmbientIn = ColorUtil.WhiteRgbVec;
                standard.RgbaLightIn = state.sunLightScratch;
                standard.RgbaFogIn = game.AmbientManager.BlendedFogColor;
                standard.ExtraGlow = 0;
                standard.FogMinIn = game.AmbientManager.BlendedFogMin;
                standard.FogDensityIn = game.AmbientManager.BlendedFogDensity;
                standard.DontWarpVertices = 0;
                standard.AddRenderFlags = 0;
                standard.ExtraZOffset = 0f;
                standard.NormalShaded = 0;
                standard.OverlayOpacity = 0f;
                standard.ExtraGodray = 1f / 3f;
                standard.ShadowIntensity = 0f;
                standard.ApplySsao = 0;
                standard.AlphaTest = 0.01f;
                standard.UniformMatrix("modelMatrix", ref sunMat);
                standard.ViewMatrix = renderApi.CameraMatrixOriginf;
                standard.ProjectionMatrix = renderApi.CurrentProjectionMatrix;
                standard.Tex2D = sunTextureId;
                platform.RenderMesh(quadModel);
                standard.Uniform("skyShaded", 0);
                standard.ExtraGodray = 0f;
                standard.ApplySsao = 1;
                standard.FadeFromSpheresFog = 0;
                standard.Stop();
            }

            if (drawMoon)
            {
                float angleSunFromMoon = ComputeAngleSunFromMoon(sunPositionNormalized, moonDir);
                ShaderProgramCelestialobject celestial = ShaderPrograms.Celestialobject;
                celestial.Use();
                celestial.Sky2D = skyTextureIdRef(game);
                celestial.Glow2D = skyGlowTextureIdRef(game);
                celestial.SunPosition = sunPositionNormalized;
                celestial.Uniform("moonPosition", moonDir);
                celestial.Uniform("moonSunAngle", angleSunFromMoon);
                celestial.DayLight = skyDaylightRef(game.shUniforms);
                celestial.WeirdMathToMakeMoonLookNicer = 1;
                celestial.DitherSeed = (frameSeedRef(game) + 1) % Math.Max(1, game.Width * game.Height);
                celestial.HorizontalResolution = game.Width;
                celestial.PlayerToSealevelOffset = (float)game.EntityPlayer.Pos.Y - (float)game.SeaLevel;
                celestial.RgbaFogIn = game.AmbientManager.BlendedFogColor;
                celestial.FogMinIn = game.AmbientManager.BlendedFogMin;
                celestial.FogDensityIn = game.AmbientManager.BlendedFogDensity;
                celestial.HorizonFog = game.AmbientManager.BlendedCloudDensity;
                celestial.ExtraGlow = 0;
                celestial.ExtraGodray = 0.5f;
                celestial.UniformMatrix("modelMatrix", ref moonMat);
                celestial.ViewMatrix = renderApi.CameraMatrixOriginf;
                celestial.ProjectionMatrix = renderApi.CurrentProjectionMatrix;
                celestial.Tex2D = moonTextureIds[4];
                platform.RenderMesh(quadModel);
                celestial.WeirdMathToMakeMoonLookNicer = 0;
                celestial.Stop();
            }

            platform.GlToggleBlend(on: false);
            platform.GlEnableDepthTest();
            return false;
        }

        public static void Dispose_Postfix(SystemRenderSunMoon __instance)
        {
            if (__instance == null)
            {
                return;
            }

            SunMoonState state;
            lock (stateLock)
            {
                if (!states.TryGetValue(__instance, out state))
                {
                    return;
                }

                states.Remove(__instance);
            }

            DisposeState(state);
        }

        internal static void ProjectToScreenNdc(
            double x,
            double y,
            double z,
            double[] projection,
            double[] modelView,
            double[] mvpScratch,
            int viewportWidth,
            int viewportHeight,
            Vec3f target)
        {
            Mat4d.Mul(mvpScratch, projection, modelView);
            ProjectMvpToScreenNdc(x, y, z, mvpScratch, viewportWidth, viewportHeight, target);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ProjectMvpToScreenNdc(
            double x,
            double y,
            double z,
            double[] mvp,
            int viewportWidth,
            int viewportHeight,
            Vec3f target)
        {
            double clipX = mvp[0] * x + mvp[4] * y + mvp[8] * z + mvp[12];
            double clipY = mvp[1] * x + mvp[5] * y + mvp[9] * z + mvp[13];
            double clipZ = mvp[2] * x + mvp[6] * y + mvp[10] * z + mvp[14];
            double clipW = mvp[3] * x + mvp[7] * y + mvp[11] * z + mvp[15];

            double screenX = (clipX / clipW + 1.0) * (double)(viewportWidth / 2);
            double screenY = (clipY / clipW + 1.0) * (double)(viewportHeight / 2);
            target.Set(
                (float)screenX / (float)viewportWidth * 2f - 1f,
                (float)screenY / (float)viewportHeight * 2f - 1f,
                (float)clipZ);
        }

        /// <summary>
        /// Exact 4D homogeneous clip-space test for the XY quad [0..quadSize] x [0..quadSize] at z=0.
        /// Returns true only when all four corners lie outside the same clip plane half-space,
        /// which guarantees zero rasterized fragments even for quads straddling w = 0.
        /// </summary>
        internal static bool IsQuadOutsideClipFrustum(double[] mvp, double quadSize)
        {
            double m0 = mvp[0] * quadSize;
            double m1 = mvp[1] * quadSize;
            double m2 = mvp[2] * quadSize;
            double m3 = mvp[3] * quadSize;

            double m4 = mvp[4] * quadSize;
            double m5 = mvp[5] * quadSize;
            double m6 = mvp[6] * quadSize;
            double m7 = mvp[7] * quadSize;

            double t0 = mvp[12];
            double t1 = mvp[13];
            double t2 = mvp[14];
            double t3 = mvp[15];

            int allOut = OutAll;
            allOut &= ClipOutcode(t0, t1, t2, t3);
            if (allOut == 0) return false;

            allOut &= ClipOutcode(t0 + m0, t1 + m1, t2 + m2, t3 + m3);
            if (allOut == 0) return false;

            allOut &= ClipOutcode(t0 + m4, t1 + m5, t2 + m6, t3 + m7);
            if (allOut == 0) return false;

            allOut &= ClipOutcode(t0 + m0 + m4, t1 + m1 + m5, t2 + m2 + m6, t3 + m3 + m7);
            return allOut != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ClipOutcode(double x, double y, double z, double w)
        {
            double wMargin = w * ClipMargin;
            int code = 0;
            if (x < -wMargin) code |= OutLeft;
            if (x > wMargin) code |= OutRight;
            if (y < -wMargin) code |= OutBottom;
            if (y > wMargin) code |= OutTop;
            if (z < -wMargin) code |= OutNear;
            if (z > wMargin) code |= OutFar;
            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsGlobalWarpActive(DefaultShaderUniforms uniforms)
        {
            if (uniforms == null)
            {
                return false;
            }

            return uniforms.GlobalWorldWarp > 0f
                || uniforms.GlitchWaviness > 0.1f
                || (uniforms.PerceptionEffectId == 2 && uniforms.PerceptionEffectIntensity > 0f);
        }

        internal static float ComputeAngleSunFromMoon(Vec3f sunPosNormalized, Vec3f moonPosNormalized)
        {
            float relX = sunPosNormalized.X - moonPosNormalized.X;
            float relY = sunPosNormalized.Y - moonPosNormalized.Y;
            float relZ = sunPosNormalized.Z - moonPosNormalized.Z;

            float proj = moonPosNormalized.X * relX + moonPosNormalized.Y * relY + moonPosNormalized.Z * relZ;
            relX -= moonPosNormalized.X * proj;
            relY -= moonPosNormalized.Y * proj;
            relZ -= moonPosNormalized.Z * proj;

            // moonPosNormalized.Cross((0, 1, 0)) = (-moonZ, 0, moonX)
            float crossX = -moonPosNormalized.Z;
            float crossZ = moonPosNormalized.X;

            float relLen = MathF.Sqrt(relX * relX + relY * relY + relZ * relZ);
            float crossLen = MathF.Sqrt(crossX * crossX + crossZ * crossZ);
            float denom = relLen * crossLen;
            if (denom == 0f)
            {
                return 0f;
            }

            float cosAngle = GameMath.Clamp((relX * crossX + relZ * crossZ) / denom, -1f, 1f);
            return (float)Math.Acos(cosAngle);
        }

        private static void PrepareMoonMatNoAlloc(
            SystemRenderSunMoon instance,
            ClientMain game,
            int imageSize,
            Vec3f moonPos,
            Vec3f moonDir,
            Vec3f sunPosNormalized)
        {
            float proximity = GameMath.Clamp((sunPosNormalized.Dot(moonDir) - 0.99f) * 40f, 0f, MaxMoonProximityFactor);
            float moonY = moonPos.Y + (float)game.EntityPlayer.LocalEyePos.Y - ((float)game.EntityPlayer.Pos.Y - (float)game.SeaLevel) / 10000f;
            var moonPosVec = new Vector3(moonPos.X, moonY, moonPos.Z);
            Quaternion lookRot = SystemRenderSunMoon.CreateLookRotation(moonPosVec);
            moonMatRef(instance) = Matrix4.CreateTranslation(-imageSize / 2f, -imageSize / 2f, -imageSize / 2f)
                * Matrix4.CreateScale(instance.moonScale * (1.1f + proximity))
                * Matrix4.CreateFromQuaternion(lookRot)
                * Matrix4.CreateTranslation(moonPosVec);
        }

        private static void RenderOcclusionQuadWithQuery(
            SystemRenderSunMoon instance,
            ClientMain game,
            ref Matrix4 queryMat,
            int slotQueryId,
            ref bool queryBegun)
        {
            ClientPlatformAbstract platform = game.Platform;
            platform.GlEnableDepthTest();
            platform.GlToggleBlend(on: true);
            platform.GlDisableCullFace();
            platform.GlDepthMask(flag: false);

            ShaderProgramStandard standard = ShaderPrograms.Standard;
            standard.Use();
            try
            {
                // Color writes are masked off; upload only geometry and alpha-test inputs that affect
                // gl_Position and discard, skipping dead color/fog/glow/sin() uniform uploads.
                standard.RgbaTint = ColorUtil.WhiteArgbVec;
                standard.DontWarpVertices = 0;
                standard.AddRenderFlags = 0;
                standard.ExtraZOffset = 0f;
                standard.OverlayOpacity = 0f;
                standard.UniformMatrix("modelMatrix", ref queryMat);
                standard.ViewMatrix = game.api.Render.CameraMatrixOriginf;
                standard.ProjectionMatrix = game.api.Render.CurrentProjectionMatrix;
                standard.Tex2D = sunTextureIdRef(instance);

                BeginQuery(slotQueryId);
                queryBegun = true;
                platform.RenderMesh(quadModelRef(instance));
            }
            finally
            {
                standard.Stop();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyMatrixfToDouble(float[] source, double[] destination)
        {
            for (int i = 0; i < 16; i++)
            {
                destination[i] = source[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyMatrix4ToDouble(ref Matrix4 source, double[] destination)
        {
            destination[0] = source.M11;
            destination[1] = source.M12;
            destination[2] = source.M13;
            destination[3] = source.M14;
            destination[4] = source.M21;
            destination[5] = source.M22;
            destination[6] = source.M23;
            destination[7] = source.M24;
            destination[8] = source.M31;
            destination[9] = source.M32;
            destination[10] = source.M33;
            destination[11] = source.M34;
            destination[12] = source.M41;
            destination[13] = source.M42;
            destination[14] = source.M43;
            destination[15] = source.M44;
        }

        private static SunMoonState GetOrCreateState(SystemRenderSunMoon instance)
        {
            if (states.TryGetValue(instance, out SunMoonState existing))
            {
                return existing;
            }

            lock (stateLock)
            {
                if (states.TryGetValue(instance, out existing))
                {
                    return existing;
                }

                var created = new SunMoonState();
                states.Add(instance, created);
                activeStates.Add(new WeakReference<SunMoonState>(created));
                return created;
            }
        }

        private static void DisposeState(SunMoonState state)
        {
            if (state == null || state.disposed)
            {
                return;
            }

            state.disposed = true;
            state.head = 0;
            state.tail = 0;
            state.inFlightCount = 0;

            if (state.queriesAllocated)
            {
                state.queriesAllocated = false;
                DeleteQueries(state.queryIds);
            }
        }

        private static void GenQueries(int[] queryIds)
        {
            if (testGenQueries != null)
            {
                testGenQueries(queryIds);
                return;
            }

            GL.GenQueries(queryIds.Length, queryIds);
        }

        private static void DeleteQueries(int[] queryIds)
        {
            if (testDeleteQueries != null)
            {
                testDeleteQueries(queryIds);
                return;
            }

            GL.DeleteQueries(queryIds.Length, queryIds);
        }

        private static bool IsQueryAvailable(int queryId)
        {
            if (testIsQueryAvailable != null)
            {
                return testIsQueryAvailable(queryId);
            }

            GL.GetQueryObject(queryId, GetQueryObjectParam.QueryResultAvailable, out int available);
            return available != 0;
        }

        private static int GetQueryResult(int queryId)
        {
            if (testGetQueryResult != null)
            {
                return testGetQueryResult(queryId);
            }

            GL.GetQueryObject(queryId, GetQueryObjectParam.QueryResult, out int samples);
            return samples;
        }

        private static void BeginQuery(int queryId)
        {
            if (testBeginQuery != null)
            {
                testBeginQuery(queryId);
                return;
            }

            GL.BeginQuery(QueryTarget.SamplesPassed, queryId);
        }

        private static void EndQuery()
        {
            if (testEndQuery != null)
            {
                testEndQuery();
                return;
            }

            GL.EndQuery(QueryTarget.SamplesPassed);
        }

        private static void SetColorMask(bool enabled)
        {
            if (testSetColorMask != null)
            {
                testSetColorMask(enabled);
                return;
            }

            GL.ColorMask(enabled, enabled, enabled, enabled);
        }

        private static void RestoreDepthMask(ClientMain game)
        {
            if (testRestoreDepthMask != null)
            {
                testRestoreDepthMask(game);
                return;
            }

            game.Platform?.GlDepthMask(flag: true);
        }

        private sealed class SunMoonState
        {
            internal readonly int[] queryIds = new int[QueryRingSize];
            internal bool queriesAllocated;
            internal bool disposed;
            internal int head;
            internal int tail;
            internal int inFlightCount;

            internal readonly double[] modelDoubleScratch = new double[16];
            internal readonly double[] mvScratch = new double[16];
            internal readonly double[] mvpScratch = new double[16];
            internal readonly Vec3f moonDirScratch = new Vec3f();
            internal readonly Vec3f sunColorScratch = new Vec3f();
            internal readonly Vec4f sunTintScratch = new Vec4f();
            internal readonly Vec4f sunLightScratch = new Vec4f();
        }
    }
}
