using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common.Entities;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public class OptiTimeMod : ModSystem
    {
        private ICoreClientAPI capi;
        private OptiTimeConfig config;
        private Harmony harmony;
        private bool ancestralBlissDetected = false;
        private bool coriaenderDetected = false;
        private bool optimumHostDetected = false;
        private bool externalWorldShaderOverrideDetected = false;
        private bool externalBlurOverrideDetected = false;
        private bool chunkShadowMapOverrideActive = false;
        private bool runtimeUnsupported = false;
        private Action<int> viewDistanceChangedDelegate = null;
        private OnSettingsChanged<int> shadowMapQualityChangedDelegate = null;
        private ActionBoolReturn reloadShaderDelegate = null;
        private bool entityShadowPatched = false;
        private System.Reflection.MethodBase guiItemRenderTarget;
        private System.Reflection.MethodBase guiItemRenderInfoTarget;
        private System.Reflection.MethodBase renderShadowTarget;
        
        // Public accessors for ConfigLib integration
        public static OptiTimeConfig Config { get; private set; }
        private static ICoreClientAPI staticCapi;
        
        public static void SaveConfig()
        {
            if (Config != null && staticCapi != null)
            {
                Config.Save(staticCapi);
                ApplyRuntimeConfig();
            }
        }

        public static void ApplyRuntimeConfig()
        {
            if (Config == null)
                return;

            FrameRateOptimization.Configure(Config);
            ParticleOptimization.SetLogger(msg => staticCapi?.Logger?.Warning(msg));
            ParticleOptimization.Configure(Config.ParticleOptimizations, Config.ParticleViewDistanceScalingEnabled);
            DynamicLightOptimization.Configure(Config.DynamicLightOptimizations);
            OcclusionCullingOptimization.Configure(Config.OcclusionCullingOptimizations);
            EntityShadowCullingOptimization.SetEnabled(Config.EntityShadowDistanceCullEnabled);
            GuiManagerOptimization.Configure(Config);
            ProfilingHelper.SetEnabled(Config.EnableProfiling);
        }
        
        private static readonly string[] OptiTimeShaderPaths = new[]
        {
            "shaders/bilateralblur.fsh",
            "shaders/blur.fsh",
            "shaders/blur.vsh",
            "shaders/chunkliquid.fsh",
            "shaders/chunkopaque.fsh",
            "shaders/chunkshadowmap.fsh",
            "shaders/godrays.fsh",
            "shaders/ssao.fsh"
        };

        private static bool IsWorldShaderPath(string path)
        {
            return !path.StartsWith("shaders/blur.", StringComparison.OrdinalIgnoreCase);
        }
        private static readonly Dictionary<string, string> OptimizationMap = new Dictionary<string, string>
        {
            ["shaders"] = "ShaderOptimizations",
            ["blur"] = "BlurOptimizationEnabled",
            ["dynlights"] = "DynamicLightOptimizations",
            ["entityanim"] = "EntityAnimationOptimizations",
            ["particles"] = "ParticleOptimizations",
            ["occlusion"] = "OcclusionCullingOptimizations",
            ["ambientsound"] = "AmbientSoundOptimizations",
            ["flysound"] = "FlySoundOptimizations",
            ["bgfps"] = "BackgroundFpsLimiterEnabled",
            ["framepace"] = "PreciseFramePacingEnabled",
            ["guimgr"] = "GuiManagerOptimizations",
            ["handbook"] = "HandbookOptimizations",
            ["recipe"] = "RecipeLookupOptimizations",
            ["weatherwind"] = "WeatherWindOptimizations",
            ["tickingblocks"] = "TickingBlocksOptimizations",
            ["shadowveg"] = "ShadowFarVegetationCullEnabled",
            ["shadowentity"] = "EntityShadowDistanceCullEnabled",
            ["entityinterp"] = "EntityInterpolationOptimizations",
            ["repulseagents"] = "RepulseAgentsOptimizations",
            ["entityshape"] = "EntityShapeOptimizations",
            ["suppress"] = "SuppressCompatibilityMessages"
        };

        private static readonly HashSet<string> NoRestartRequired = new HashSet<string>
        {
            nameof(OptiTimeConfig.SuppressCompatibilityMessages)
        };

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            if (Environment.Version.Major < 10)
            {
                api.Logger.Error("[OptiTime] Requires .NET 10+ (Vintage Story 1.22.0+). Current runtime: .NET {0}. Mod will not load.", Environment.Version.Major);
                runtimeUnsupported = true;
                return;
            }

            optimumHostDetected = OptimumCompatibility.Detect();

            ancestralBlissDetected = api.ModLoader.IsModEnabled("ancestralblissshaders") || 
                                     api.ModLoader.IsModEnabled("volumetricshadingrefreshed");
            coriaenderDetected = api.ModLoader.IsModEnabled("coriaendershaders");
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
            if (runtimeUnsupported) return;
            if (api.Side != EnumAppSide.Client)
                return;

            var clientApi = api as ICoreClientAPI;
            if (clientApi == null)
                return;

            if (config == null)
                config = OptiTimeConfig.Load(clientApi);

            if (optimumHostDetected)
            {
                ApplyOptimumShaderPolicy(clientApi);
                return;
            }

            if (!config.ShaderOptimizations || ancestralBlissDetected)
            {
                string reason = ancestralBlissDetected ? "Ancestral Bliss" : "config";
                chunkShadowMapOverrideActive = false;
                TryDisableOptiTimeShaders(clientApi, reason, OptiTimeShaderPaths);
            }
            else
            {
                var shadersToDisable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var supersededPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Coriaender patches world and post-process shaders at runtime; keep only blur independent.
                if (coriaenderDetected)
                {
                    foreach (string path in OptiTimeShaderPaths)
                    {
                        if (IsWorldShaderPath(path))
                            shadersToDisable.Add(path);
                    }
                }

                if (!config.BlurOptimizationEnabled)
                {
                    shadersToDisable.Add("shaders/blur.fsh");
                    shadersToDisable.Add("shaders/blur.vsh");
                }

                IReadOnlyDictionary<string, string> vanillaHashes = ResolveVanillaShaderHashes();
                bool nonVanillaUnderneath = false;

                foreach (string path in OptiTimeShaderPaths)
                {
                    if (shadersToDisable.Contains(path))
                        continue;

                    if (clientApi.Assets?.AllAssets == null ||
                        !clientApi.Assets.AllAssets.TryGetValue(new AssetLocation("game", path), out IAsset current) ||
                        current == null)
                    {
                        continue;
                    }

                    if (!IsOptiTimeOrigin(current.Origin))
                    {
                        supersededPaths.Add(path);
                        continue;
                    }

                    if (!IsUnderlyingShaderVanilla(clientApi, path, vanillaHashes))
                    {
                        shadersToDisable.Add(path);
                        nonVanillaUnderneath = true;
                    }
                }

                // Keep blur vertex and fragment shaders atomic so texCoords[9] stays matched.
                if (config.BlurOptimizationEnabled &&
                    (shadersToDisable.Contains("shaders/blur.fsh") ||
                     shadersToDisable.Contains("shaders/blur.vsh") ||
                     supersededPaths.Contains("shaders/blur.fsh") ||
                     supersededPaths.Contains("shaders/blur.vsh")))
                {
                    externalBlurOverrideDetected = true;
                    AddIfOptiTimeOwnsShader(clientApi, "shaders/blur.fsh", shadersToDisable);
                    AddIfOptiTimeOwnsShader(clientApi, "shaders/blur.vsh", shadersToDisable);
                }

                if (!coriaenderDetected &&
                    OptiTimeShaderPaths.Where(IsWorldShaderPath).All(p => shadersToDisable.Contains(p) || supersededPaths.Contains(p)))
                {
                    externalWorldShaderOverrideDetected = true;
                }

                chunkShadowMapOverrideActive =
                    !coriaenderDetected &&
                    !shadersToDisable.Contains("shaders/chunkshadowmap.fsh") &&
                    !supersededPaths.Contains("shaders/chunkshadowmap.fsh") &&
                    IsUnderlyingShaderVanilla(clientApi, "shaders/chunkshadowmap.vsh", vanillaHashes);

                if (shadersToDisable.Count > 0)
                {
                    string reason = coriaenderDetected && !nonVanillaUnderneath
                        ? "CoriaenderShaders"
                        : (nonVanillaUnderneath ? "external shader override" : "config");
                    TryDisableOptiTimeShaders(clientApi, reason, shadersToDisable.ToArray());
                }
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (runtimeUnsupported) return;

            capi = api;
            staticCapi = api;
            if (config == null)
                config = OptiTimeConfig.Load(api);
            Config = config;

            if (optimumHostDetected)
                ApplyOptimumPolicy(api);

            config.Save(api);
            ProfilingHelper.Initialize(api, config.EnableProfiling);

            // Try to initialize ConfigLib integration (optional)
            ConfigLibIntegration.TryInitialize(api);

            // Detect conflicting mods
            DetectAndHandleConflicts(api);

            harmony = new Harmony("com.zaldaryon.optitime");
            ApplyRuntimeConfig();

            // Always-on: fix vanilla TranslationService thread-safety bug (notFound HashSet)
            try
            {
                var hasTranslation = AccessTools.Method(typeof(TranslationService), nameof(TranslationService.HasTranslation),
                    new[] { typeof(string), typeof(bool), typeof(bool) });
                if (hasTranslation != null)
                {
                    harmony.Patch(hasTranslation,
                        transpiler: new HarmonyMethod(typeof(TranslationServicePatch), nameof(TranslationServicePatch.Transpile)));
                    api.Logger.Notification("[OptiTime] TranslationService thread-safety fix loaded");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] TranslationService thread-safety fix failed: " + ex.Message);
            }

            SafeCatalogIsolation.SetLogger(msg => api.Logger.Warning(msg));

            try
            {
                var creativeSearchTarget = AccessTools.Method("Vintagestory.Common.CreativeTab:CreateSearchCache",
                    new Type[] { typeof(IWorldAccessor) });
                if (creativeSearchTarget != null &&
                    !HasAnyConflictingPatches(creativeSearchTarget) &&
                    (!optimumHostDetected || OptimumOwnership.IsVanillaBody(creativeSearchTarget, GameVersion.ShortGameVersion)))
                {
                    harmony.Patch(creativeSearchTarget,
                        prefix: new HarmonyMethod(typeof(SafeCatalogIsolation), nameof(SafeCatalogIsolation.CreateSearchCache_Prefix)));
                    api.Logger.Notification("[OptiTime] Creative search exception isolation loaded");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] Creative search exception isolation failed: " + ex.Message);
            }

            try
            {
                var handbookPagesTarget = AccessTools.Method("Vintagestory.GameContent.ModSystemSurvivalHandbook:onCreatePagesAsync");
                if (handbookPagesTarget != null &&
                    !HasAnyConflictingPatches(handbookPagesTarget) &&
                    (!optimumHostDetected || OptimumOwnership.IsVanillaBody(handbookPagesTarget, GameVersion.ShortGameVersion)))
                {
                    harmony.Patch(handbookPagesTarget,
                        transpiler: new HarmonyMethod(typeof(SafeCatalogIsolation), nameof(SafeCatalogIsolation.TranspileCreatePagesAsync)));
                    api.Logger.Notification("[OptiTime] Handbook page exception isolation loaded");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] Handbook page exception isolation failed: " + ex.Message);
            }

            try
            {
                var chunkUploadTarget = AccessTools.Method("Vintagestory.Client.NoObf.ChunkTesselatorManager:OnBeforeFrame",
                    new Type[] { typeof(float) });
                if (chunkUploadTarget != null &&
                    !HasAnyConflictingPatches(chunkUploadTarget) &&
                    (!optimumHostDetected || OptimumOwnership.IsVanillaBody(chunkUploadTarget, GameVersion.ShortGameVersion)))
                {
                    harmony.Patch(chunkUploadTarget,
                        prefix: new HarmonyMethod(typeof(ChunkUploadOptimization), nameof(ChunkUploadOptimization.OnBeforeFrame_Prefix)));
                    api.Logger.Notification("[OptiTime] Chunk upload budget and queue sort optimization loaded");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] Chunk upload optimization failed: " + ex.Message);
            }

            if (api.Side == EnumAppSide.Client)
            {
                try
                {
                    var frustumRangeTarget = AccessTools.Method(
                        "Vintagestory.API.Client.FrustumCulling:InFrustumAndRange",
                        new Type[] { typeof(Vintagestory.API.MathTools.Sphere), typeof(bool), typeof(int) });
                    if (frustumRangeTarget != null &&
                        !HasAnyConflictingPatches(frustumRangeTarget) &&
                        (!optimumHostDetected || OptimumOwnership.IsVanillaBody(frustumRangeTarget, GameVersion.ShortGameVersion)))
                    {
                        FrustumCullingOptimization.SetClientActive(true);
                        harmony.Patch(frustumRangeTarget,
                            prefix: new HarmonyMethod(typeof(FrustumCullingOptimization), nameof(FrustumCullingOptimization.InFrustumAndRange_Prefix)));
                        api.Logger.Notification("[OptiTime] Frustum range-first culling optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    FrustumCullingOptimization.Cleanup();
                    api.Logger.Warning("[OptiTime] Frustum range-first culling optimization failed: " + ex.Message);
                }

                try
                {
                    var animRendererTarget = AccessTools.Method(
                        "Vintagestory.API.Common.AnimatableRenderer:OnRenderFrame",
                        new Type[] { typeof(float), typeof(EnumRenderStage) });
                    bool animRendererReady = animRendererTarget != null &&
                        !HasAnyConflictingPatches(animRendererTarget) &&
                        (!optimumHostDetected || OptimumOwnership.IsVanillaBody(animRendererTarget, GameVersion.ShortGameVersion));

                    var animRendererInitTarget = AccessTools.Method(
                        "Vintagestory.API.Common.AnimatableRenderer:mainThreadInit",
                        new Type[] { typeof(ICoreClientAPI), typeof(MeshData), typeof(EnumRenderStage) });
                    var animRendererDisposeTarget = AccessTools.Method(
                        "Vintagestory.API.Common.AnimatableRenderer:Dispose",
                        Type.EmptyTypes);
                    bool animRendererLifecycleReady = animRendererReady &&
                        animRendererInitTarget != null && animRendererDisposeTarget != null &&
                        !HasAnyConflictingPatches(animRendererInitTarget) &&
                        !HasAnyConflictingPatches(animRendererDisposeTarget) &&
                        (!optimumHostDetected ||
                            (OptimumOwnership.IsVanillaBody(animRendererInitTarget, GameVersion.ShortGameVersion) &&
                             OptimumOwnership.IsVanillaBody(animRendererDisposeTarget, GameVersion.ShortGameVersion)));

                    var animUtilRenderTarget = AccessTools.Method(
                        "Vintagestory.API.Common.AnimationUtil:OnRenderFrame",
                        new Type[] { typeof(float), typeof(EnumRenderStage) });
                    var animUtilStartTarget = AccessTools.Method(
                        "Vintagestory.API.Common.AnimationUtil:StartAnimation",
                        new Type[] { typeof(AnimationMetaData) });
                    var animUtilDisposeTarget = AccessTools.Method(
                        "Vintagestory.API.Common.AnimationUtil:Dispose",
                        Type.EmptyTypes);
                    bool animUtilReady = animUtilRenderTarget != null && animUtilStartTarget != null && animUtilDisposeTarget != null &&
                        !HasAnyConflictingPatches(animUtilRenderTarget) &&
                        !HasAnyConflictingPatches(animUtilStartTarget) &&
                        !HasAnyConflictingPatches(animUtilDisposeTarget) &&
                        (!optimumHostDetected ||
                            (OptimumOwnership.IsVanillaBody(animUtilRenderTarget, GameVersion.ShortGameVersion) &&
                             OptimumOwnership.IsVanillaBody(animUtilStartTarget, GameVersion.ShortGameVersion) &&
                             OptimumOwnership.IsVanillaBody(animUtilDisposeTarget, GameVersion.ShortGameVersion)));

                    if (animRendererReady || animUtilReady)
                    {
                        AnimatableOptimization.SetClientActive(true);
                    }

                    if (animRendererReady)
                    {
                        harmony.Patch(animRendererTarget,
                            prefix: new HarmonyMethod(typeof(AnimatableOptimization), nameof(AnimatableOptimization.AnimatableRenderer_OnRenderFrame_Prefix)));
                        if (animRendererLifecycleReady)
                        {
                            harmony.Patch(animRendererInitTarget,
                                postfix: new HarmonyMethod(typeof(AnimatableOptimization), nameof(AnimatableOptimization.AnimatableRenderer_MainThreadInit_Postfix)));
                            harmony.Patch(animRendererDisposeTarget,
                                postfix: new HarmonyMethod(typeof(AnimatableOptimization), nameof(AnimatableOptimization.AnimatableRenderer_Dispose_Postfix)));
                        }
                        api.Logger.Notification("[OptiTime] AnimatableRenderer frustum culling loaded");
                    }

                    if (animUtilReady)
                    {
                        harmony.Patch(animUtilRenderTarget,
                            postfix: new HarmonyMethod(typeof(AnimatableOptimization), nameof(AnimatableOptimization.AnimationUtil_OnRenderFrame_Postfix)));
                        harmony.Patch(animUtilStartTarget,
                            prefix: new HarmonyMethod(typeof(AnimatableOptimization), nameof(AnimatableOptimization.AnimationUtil_StartAnimation_Prefix)));
                        harmony.Patch(animUtilDisposeTarget,
                            postfix: new HarmonyMethod(typeof(AnimatableOptimization), nameof(AnimatableOptimization.AnimationUtil_Dispose_Postfix)));

                        if (animRendererLifecycleReady)
                        {
                            AnimatableOptimization.SetRendererParkingEnabled(true);
                            api.Logger.Notification("[OptiTime] Idle AnimationUtil and AnimatableRenderer parking loaded");
                        }
                        else
                        {
                            api.Logger.Notification("[OptiTime] Idle AnimationUtil parking loaded");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnimatableOptimization.Cleanup();
                    api.Logger.Warning("[OptiTime] Animatable optimization failed: " + ex.Message);
                }

                try
                {
                    var sunMoonRenderTarget = AccessTools.Method(
                        "Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3D",
                        new Type[] { typeof(float) });
                    var sunMoonPostTarget = AccessTools.Method(
                        "Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3DPost",
                        new Type[] { typeof(float) });
                    var sunMoonDisposeTarget = AccessTools.Method(
                        "Vintagestory.Client.NoObf.SystemRenderSunMoon:Dispose",
                        new Type[] { typeof(Vintagestory.Client.NoObf.ClientMain) });

                    bool sunMoonRenderReady = sunMoonRenderTarget != null &&
                        !HasAnyConflictingPatches(sunMoonRenderTarget) &&
                        (!optimumHostDetected || OptimumOwnership.IsVanillaBody(sunMoonRenderTarget, GameVersion.ShortGameVersion));

                    bool sunMoonPostReady = sunMoonPostTarget != null && sunMoonDisposeTarget != null &&
                        !HasAnyConflictingPatches(sunMoonPostTarget) &&
                        !HasAnyConflictingPatches(sunMoonDisposeTarget) &&
                        (!optimumHostDetected ||
                            (OptimumOwnership.IsVanillaBody(sunMoonPostTarget, GameVersion.ShortGameVersion) &&
                             OptimumOwnership.IsVanillaBody(sunMoonDisposeTarget, GameVersion.ShortGameVersion)));

                    if (sunMoonRenderReady || sunMoonPostReady)
                    {
                        SunMoonOptimization.SetClientActive(true);
                    }

                    if (sunMoonRenderReady)
                    {
                        harmony.Patch(sunMoonRenderTarget,
                            prefix: new HarmonyMethod(typeof(SunMoonOptimization), nameof(SunMoonOptimization.OnRenderFrame3D_Prefix)));
                    }

                    if (sunMoonPostReady)
                    {
                        harmony.Patch(sunMoonPostTarget,
                            prefix: new HarmonyMethod(typeof(SunMoonOptimization), nameof(SunMoonOptimization.OnRenderFrame3DPost_Prefix)));
                        harmony.Patch(sunMoonDisposeTarget,
                            postfix: new HarmonyMethod(typeof(SunMoonOptimization), nameof(SunMoonOptimization.Dispose_Postfix)));
                    }

                    if (sunMoonRenderReady || sunMoonPostReady)
                    {
                        api.Logger.Notification("[OptiTime] Sun/moon occlusion query ring and scratch optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    SunMoonOptimization.Cleanup();
                    api.Logger.Warning("[OptiTime] Sun/moon optimization failed: " + ex.Message);
                }

                try
                {
                    GuiItemRenderInfoOptimization.SetLogger(msg => api.Logger.Warning(msg));
                    var renderItemstackTarget = AccessTools.Method(
                        "Vintagestory.Client.NoObf.InventoryItemRenderer:RenderItemstackToGui",
                        new Type[]
                        {
                            typeof(ItemSlot), typeof(double), typeof(double), typeof(double),
                            typeof(float), typeof(int), typeof(float),
                            typeof(bool), typeof(bool), typeof(bool)
                        });
                    var getItemRenderInfoTarget = AccessTools.Method(
                        typeof(Vintagestory.Client.NoObf.InventoryItemRenderer),
                        nameof(Vintagestory.Client.NoObf.InventoryItemRenderer.GetItemStackRenderInfo),
                        new Type[]
                        {
                            typeof(Vintagestory.Client.NoObf.ClientMain),
                            typeof(ItemSlot),
                            typeof(EnumItemRenderTarget),
                            typeof(float)
                        });

                    if (renderItemstackTarget != null &&
                        getItemRenderInfoTarget != null &&
                        !HasAnyConflictingPatches(renderItemstackTarget) &&
                        !HasAnyForeignPatches(getItemRenderInfoTarget) &&
                        (!optimumHostDetected ||
                            (OptimumOwnership.IsVanillaBody(renderItemstackTarget, GameVersion.ShortGameVersion) &&
                             OptimumOwnership.IsVanillaBody(getItemRenderInfoTarget, GameVersion.ShortGameVersion))))
                    {
                        GuiItemRenderInfoOptimization.ResetTranspilerApplied();
                        harmony.Patch(
                            renderItemstackTarget,
                            prefix: new HarmonyMethod(typeof(GuiItemRenderInfoOptimization), nameof(GuiItemRenderInfoOptimization.RenderItemstackToGui_Prefix)),
                            transpiler: new HarmonyMethod(typeof(GuiItemRenderInfoOptimization), nameof(GuiItemRenderInfoOptimization.TranspileRenderItemstackToGui)),
                            finalizer: new HarmonyMethod(typeof(GuiItemRenderInfoOptimization), nameof(GuiItemRenderInfoOptimization.RenderItemstackToGui_Finalizer)));

                        if (GuiItemRenderInfoOptimization.TranspilerApplied)
                        {
                            GuiItemRenderInfoOptimization.SetClientActive(true);
                            guiItemRenderTarget = renderItemstackTarget;
                            guiItemRenderInfoTarget = getItemRenderInfoTarget;
                            api.Logger.Notification("[OptiTime] GUI ItemRenderInfo scratch reuse loaded");
                        }
                        else
                        {
                            harmony.Unpatch(renderItemstackTarget, HarmonyPatchType.All, harmony.Id);
                            GuiItemRenderInfoOptimization.Cleanup();
                            api.Logger.Warning("[OptiTime] GUI ItemRenderInfo scratch reuse skipped: transpiler did not match expected call site.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    GuiItemRenderInfoOptimization.Cleanup();
                    api.Logger.Warning("[OptiTime] GUI ItemRenderInfo scratch reuse failed: " + ex.Message);
                }
            }

            try
            {
                var onNewFrame = AccessTools.Method("Vintagestory.Client.ScreenManager:OnNewFrame", new Type[] { typeof(float) });
                if (onNewFrame != null)
                {
                    if (config.BackgroundFpsLimiterEnabled && HasAnyConflictingPatches(onNewFrame))
                    {
                        DisableOptimizationForConflict(api, nameof(OptiTimeConfig.BackgroundFpsLimiterEnabled), BuildPatchConflictReason("Background FPS limiter", onNewFrame));
                    }

                    harmony.Patch(
                        onNewFrame,
                        postfix: new HarmonyMethod(typeof(FrameRateOptimization), nameof(FrameRateOptimization.OnNewFrame_Postfix))
                    );
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] Failed to load frame telemetry/background FPS hook: " + ex.Message);
                config.BackgroundFpsLimiterEnabled = false;
                ApplyRuntimeConfig();
            }

            if (config.PreciseFramePacingEnabled)
            {
                // Thread.Yield() loop causes lag spikes on Linux PREEMPT kernels
                // (sched_yield latency 1-4ms per call). Auto-disable on non-Windows for this session.
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    config.ForceOffForSession(nameof(OptiTimeConfig.PreciseFramePacingEnabled), "Auto-disabled on non-Windows");
                    ApplyRuntimeConfig();
                    api.Logger.Notification("[OptiTime] Precise frame pacing auto-disabled on non-Windows (sched_yield latency)");
                }
                else
                {
                try
                {
                    var renderFrame = AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:window_RenderFrame");
                    if (renderFrame == null)
                    {
                        throw new InvalidOperationException("ClientPlatformWindows.window_RenderFrame not found");
                    }

                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.PreciseFramePacingEnabled), "Precise frame pacing", renderFrame))
                    {
                        harmony.Patch(
                            renderFrame,
                            transpiler: new HarmonyMethod(typeof(FrameRateOptimization), nameof(FrameRateOptimization.TranspileRenderFrameSleep))
                        );

                        var leaveMethod = AccessTools.Method(typeof(Vintagestory.API.Common.FrameProfilerUtil), nameof(Vintagestory.API.Common.FrameProfilerUtil.Leave));
                        if (leaveMethod != null)
                        {
                            harmony.Patch(
                                leaveMethod,
                                prefix: new HarmonyMethod(typeof(FrameRateOptimization), nameof(FrameRateOptimization.Leave_Prefix))
                            );
                        }

                        api.Logger.Notification("[OptiTime] Precise frame pacing optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load precise frame pacing optimization: " + ex.Message);
                    config.PreciseFramePacingEnabled = false;
                    ApplyRuntimeConfig();
                }
                } // else (Windows)
            }

            if (config.DynamicLightOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.Client.NoObf.SystemRenderPlayerEffects:onBeforeRender", new Type[] { typeof(float) });
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.DynamicLightOptimizations), "Dynamic light optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(DynamicLightOptimization), "OptimizeLightCulling"));
                        api.Logger.Notification("[OptiTime] Dynamic light optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load dynamic light optimization: " + ex.Message);
                    config.DynamicLightOptimizations = false;
                }
            }
            
            if (config.EntityAnimationOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.Client.NoObf.SystemRenderEntities:OnBeforeRender", new Type[] { typeof(float) });
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.EntityAnimationOptimizations), "Entity animation optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(EntityAnimationOptimization), "OptimizeEntityAnimations"));
                        api.Logger.Notification("[OptiTime] Entity animation optimization loaded");
                        PatchVisibleEntityPasses(api);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load entity animation optimization: " + ex.Message);
                    config.EntityAnimationOptimizations = false;
                }
            }
            
            if (config.ParticleOptimizations)
            {
                try
                {
                    var spawnMethod = AccessTools.Method(
                        "Vintagestory.Client.NoObf.ParticlePoolQuads:SpawnParticles",
                        new[] { typeof(Vintagestory.API.Common.IParticlePropertiesProvider) });
                    if (spawnMethod == null)
                        throw new InvalidOperationException("ParticlePoolQuads.SpawnParticles not found");

                    var prefix = new HarmonyMethod(typeof(ParticleOptimization), nameof(ParticleOptimization.SpawnParticlesPrefix));
                    harmony.Patch(spawnMethod, prefix: prefix);

                    // Frame-time tracking: postfix on the particle render method (called once/frame)
                    var renderMethod = AccessTools.Method(
                        "Vintagestory.Client.NoObf.SystemRenderParticles:OnRenderFrame3D",
                        new[] { typeof(float) });
                    if (renderMethod != null)
                    {
                        harmony.Patch(renderMethod,
                            postfix: new HarmonyMethod(typeof(ParticleOptimization), nameof(ParticleOptimization.UpdateFrameTime)));
                    }

                    api.Logger.Notification("[OptiTime] Particle optimization loaded (VD + frustum + occupancy + frame-time)");
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load particle optimization: " + ex.Message);
                    config.ParticleOptimizations = false;
                }
            }
            
            if (config.OcclusionCullingOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.Client.NoObf.ChunkCuller:CullInvisibleChunks", new Type[] { });
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.OcclusionCullingOptimizations), "Occlusion culling optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(OcclusionCullingOptimization), nameof(OcclusionCullingOptimization.CullInvisibleChunks_Prefix)));
                        api.Logger.Notification("[OptiTime] Occlusion culling optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load occlusion culling optimization: " + ex.Message);
                    config.OcclusionCullingOptimizations = false;
                }
            }
            

            if (config.AmbientSoundOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.Client.NoObf.SystemPlayerSounds:OnGameTick", new Type[] { typeof(float) });
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.AmbientSoundOptimizations), "Ambient sound optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(AmbientSoundOptimization), "ThrottleAmbientSoundUpdates"));
                        api.Logger.Notification("[OptiTime] Ambient sound optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load ambient sound optimization: " + ex.Message);
                    config.AmbientSoundOptimizations = false;
                }
            }

            if (config.FlySoundOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.Client.NoObf.SystemPlayerSounds:updateFlySound", new Type[] { typeof(float) });
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.FlySoundOptimizations), "Fly sound optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(FlySoundOptimization), "OptimizeFlySound"));
                        api.Logger.Notification("[OptiTime] Fly sound optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load fly sound optimization: " + ex.Message);
                    config.FlySoundOptimizations = false;
                }
            }

            if (config.GuiManagerOptimizations && config.GuiManagerNoLinqEnabled)
            {
                try
                {
                    // Check for conflicting prefixes on render methods
                    var beforeRender = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnBeforeRenderFrame3D");
                    var renderGui = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnRenderFrameGUI");

                    bool conflict =
                        HasConflictingPrefix(beforeRender) ||
                        HasConflictingPrefix(renderGui);

                    if (!conflict)
                    {
                        harmony.Patch(
                            beforeRender,
                            prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnBeforeRenderFrame3D_Prefix"));
                        harmony.Patch(
                            renderGui,
                            prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnRenderFrameGUI_Prefix"));
                        api.Logger.Notification("[OptiTime] GUI Manager render optimization loaded");

                        var guiComposerPostRender = AccessTools.Method(
                            "Vintagestory.API.Client.GuiComposer:PostRender",
                            new Type[] { typeof(float) });
                        if (guiComposerPostRender != null && !HasAnyConflictingPatches(guiComposerPostRender))
                        {
                            harmony.Patch(
                                guiComposerPostRender,
                                finalizer: new HarmonyMethod(typeof(GuiManagerOptimization), nameof(GuiManagerOptimization.GuiComposer_PostRender_Finalizer)));
                        }
                        else
                        {
                            api.Logger.Warning("[OptiTime] GUI composer finalization safety patch disabled due existing GuiComposer.PostRender patches.");
                        }
                    }
                    else
                    {
                        config.ForceOffForSession(nameof(OptiTimeConfig.GuiManagerNoLinqEnabled), "Existing GUI render patches");
                        ApplyRuntimeConfig();
                        api.Logger.Warning("[OptiTime] GUI Manager render optimization disabled due to existing GUI patches (potential mod conflict).");
                    }

                    if (config.GuiManagerInputNoLinqEnabled)
                    {
                        var finalizeFrame = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnFinalizeFrame", new Type[] { typeof(float) });
                        var keyDown = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnKeyDown", new Type[] { typeof(Vintagestory.API.Client.KeyEvent) });
                        var keyUp = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnKeyUp", new Type[] { typeof(Vintagestory.API.Client.KeyEvent) });
                        var keyPress = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnKeyPress", new Type[] { typeof(Vintagestory.API.Client.KeyEvent) });
                        var mouseDown = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnMouseDown", new Type[] { typeof(Vintagestory.API.Client.MouseEvent) });
                        var mouseUp = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnMouseUp", new Type[] { typeof(Vintagestory.API.Client.MouseEvent) });
                        var mouseMove = AccessTools.Method("Vintagestory.Client.NoObf.GuiManager:OnMouseMove", new Type[] { typeof(Vintagestory.API.Client.MouseEvent) });
                        var composeInteractive = AccessTools.Method("Vintagestory.API.Client.GuiElementItemSlotGridBase:ComposeInteractiveElements");

                        bool inputConflict =
                            HasAnyConflictingPatches(finalizeFrame) ||
                            HasAnyConflictingPatches(keyDown) ||
                            HasAnyConflictingPatches(keyUp) ||
                            HasAnyConflictingPatches(keyPress) ||
                            HasAnyConflictingPatches(mouseDown) ||
                            HasAnyConflictingPatches(mouseUp) ||
                            HasAnyConflictingPatches(mouseMove) ||
                            HasAnyConflictingPatches(composeInteractive);

                        if (!inputConflict)
                        {
                            harmony.Patch(finalizeFrame,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnFinalizeFrame_Prefix"));
                            harmony.Patch(keyDown,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnKeyDown_Prefix"));
                            harmony.Patch(keyUp,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnKeyUp_Prefix"));
                            harmony.Patch(keyPress,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnKeyPress_Prefix"));
                            harmony.Patch(mouseDown,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnMouseDown_Prefix"));
                            harmony.Patch(mouseUp,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnMouseUp_Prefix"));
                            harmony.Patch(mouseMove,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "OnMouseMove_Prefix"));
                            harmony.Patch(composeInteractive,
                                prefix: new HarmonyMethod(typeof(GuiManagerOptimization), "ComposeInteractiveElements_Prefix"));
                            api.Logger.Notification("[OptiTime] GUI Manager input optimization loaded");
                        }
                        else
                        {
                            config.ForceOffForSession(nameof(OptiTimeConfig.GuiManagerInputNoLinqEnabled), "Existing GUI input patches");
                            ApplyRuntimeConfig();
                            api.Logger.Warning("[OptiTime] GUI Manager input optimization disabled due to existing GUI patches (potential mod conflict).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load GUI Manager optimization: " + ex.Message);
                    config.GuiManagerOptimizations = false;
                }
            }

            if (config.HandbookOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo:GetHandbookInfo", new Type[] { typeof(Vintagestory.API.Common.ItemSlot), typeof(Vintagestory.API.Client.ICoreClientAPI), typeof(Vintagestory.API.Common.ItemStack[]), typeof(Vintagestory.API.Common.ActionConsumable<string>) });
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.HandbookOptimizations), "Handbook optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(HandbookOptimization), "GetHandbookInfo_Prefix"));
                        api.Logger.Notification("[OptiTime] Handbook optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load handbook optimization: " + ex.Message);
                    config.HandbookOptimizations = false;
                }
            }

            if (config.RecipeLookupOptimizations)
            {
                try
                {
                    var matchesTarget =
                        AccessTools.Method(typeof(GridRecipe), "Matches", new Type[] { typeof(IPlayer), typeof(ItemSlot[]), typeof(int) }) ??
                        AccessTools.Method(typeof(GridRecipe), "Matches", new Type[] { typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int) });
                    var findMatchingRecipeTarget = AccessTools.Method("Vintagestory.Common.InventoryCraftingGrid:FindMatchingRecipe");

                    if (matchesTarget == null || findMatchingRecipeTarget == null)
                    {
                        throw new InvalidOperationException("Recipe lookup patch targets not found");
                    }

                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.RecipeLookupOptimizations), "Recipe lookup optimization", matchesTarget) &&
                        !DisableIfConflictingPatches(api, nameof(OptiTimeConfig.RecipeLookupOptimizations), "Recipe lookup optimization", findMatchingRecipeTarget))
                    {
                        var matchesPrefix = new HarmonyMethod(typeof(RecipeLookupCacheOptimization), nameof(RecipeLookupCacheOptimization.MatchesWithWorld_Prefix));

                        harmony.Patch(
                            matchesTarget,
                            prefix: matchesPrefix);
                        harmony.Patch(
                            findMatchingRecipeTarget,
                            prefix: new HarmonyMethod(typeof(RecipeLookupCacheOptimization), nameof(RecipeLookupCacheOptimization.FindMatchingRecipe_Prefix)));
                        api.Logger.Notification("[OptiTime] Recipe lookup optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load recipe lookup optimization: " + ex.Message);
                    config.RecipeLookupOptimizations = false;
                }
            }

            RegisterCommands(api);

            if (config.WeatherWindOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.GameContent.WeatherSystemClient:OnRenderFrame", new Type[] { typeof(float), typeof(Vintagestory.API.Client.EnumRenderStage) });
                    if (target == null)
                        throw new InvalidOperationException("WeatherSystemClient.OnRenderFrame not found");

                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.WeatherWindOptimizations), "Weather wind optimization", target))
                    {
                        harmony.Patch(target,
                            transpiler: new HarmonyMethod(typeof(WeatherWindOptimization), nameof(WeatherWindOptimization.Transpile)));
                        api.Logger.Notification("[OptiTime] Weather wind speed throttle loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load weather wind optimization: " + ex.Message);
                    config.WeatherWindOptimizations = false;
                }
            }

            if (config.TickingBlocksOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.Client.NoObf.SystemClientTickingBlocks:onOffThreadParticleTick", new Type[] { typeof(float), typeof(Vintagestory.API.Client.IAsyncParticleManager) });
                    if (target == null)
                        throw new InvalidOperationException("SystemClientTickingBlocks.onOffThreadParticleTick not found");

                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.TickingBlocksOptimizations), "Ticking blocks optimization", target))
                    {
                        harmony.Patch(target,
                            transpiler: new HarmonyMethod(typeof(TickingBlocksOptimization), nameof(TickingBlocksOptimization.Transpile)));
                        api.Logger.Notification("[OptiTime] Ticking blocks GC optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load ticking blocks optimization: " + ex.Message);
                    config.TickingBlocksOptimizations = false;
                }
            }

            bool wantSolidShadowDepthPass =
                chunkShadowMapOverrideActive &&
                config.ShaderOptimizations &&
                !ancestralBlissDetected &&
                !coriaenderDetected &&
                !optimumHostDetected;

            if (config.ShadowFarVegetationCullEnabled || wantSolidShadowDepthPass)
            {
                try
                {
                    ShadowOptimization.SetLogger(msg => api.Logger.Warning(msg));
                    var renderShadow = AccessTools.Method("Vintagestory.Client.NoObf.ChunkRenderer:RenderShadow", new Type[] { typeof(float) });
                    if (renderShadow == null)
                        throw new InvalidOperationException("ChunkRenderer.RenderShadow not found");

                    bool canPatchShadow;
                    if (config.ShadowFarVegetationCullEnabled)
                    {
                        canPatchShadow = !DisableIfConflictingPatches(api, nameof(OptiTimeConfig.ShadowFarVegetationCullEnabled), "Shadow far vegetation cull", renderShadow);
                    }
                    else
                    {
                        canPatchShadow = !HasAnyConflictingPatches(renderShadow);
                        if (!canPatchShadow)
                            api.Logger.Warning("[OptiTime] Solid shadow depth pass skipped: conflicting patch detected on ChunkRenderer.RenderShadow");
                    }

                    if (canPatchShadow)
                    {
                        ShadowOptimization.Configure(config.ShadowFarVegetationCullEnabled, wantSolidShadowDepthPass);
                        harmony.Patch(renderShadow,
                            prefix: new HarmonyMethod(typeof(ShadowOptimization), nameof(ShadowOptimization.RenderShadow_Prefix)));
                        renderShadowTarget = renderShadow;

                        if (wantSolidShadowDepthPass)
                        {
                            ShadowOptimization.RebuildSolidShadowShader(api);
                            reloadShaderDelegate = () =>
                            {
                                ShadowOptimization.RebuildSolidShadowShader(api);
                                return true;
                            };
                            api.Event.ReloadShader += reloadShaderDelegate;
                        }

                        api.Logger.Notification(
                            "[OptiTime] Shadow optimization loaded (farVegCull={0}, solidDepthPass={1})",
                            config.ShadowFarVegetationCullEnabled,
                            ShadowOptimization.IsSolidShadowShaderReady);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load shadow optimization: " + ex.Message);
                    ShadowOptimization.Configure(false, false);
                    if (config.ShadowFarVegetationCullEnabled)
                        config.ForceOffForSession(nameof(OptiTimeConfig.ShadowFarVegetationCullEnabled), "Failed to patch ChunkRenderer.RenderShadow");
                }
            }

            if (config.EntityShadowDistanceCullEnabled)
            {
                int shadowQ = api.Settings?.Int?.Get("shadowMapQuality", 0) ?? 0;
                ApplyEntityShadowQuality(api, shadowQ, isStartup: true);
                RegisterShadowMapQualityWatcher(api);
            }

            if (config.EntityInterpolationOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("EntityBehaviorInterpolatePosition:OnReceivedServerPos",
                        new Type[] { typeof(bool), typeof(EnumHandling).MakeByRefType() });
                    if (target != null && !DisableIfConflictingPatches(api, nameof(OptiTimeConfig.EntityInterpolationOptimizations), "Entity interpolation optimization", target))
                    {
                        EntityInterpolationOptimization.Initialize(api, harmony, false);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load entity interpolation optimization: " + ex.Message);
                    config.EntityInterpolationOptimizations = false;
                }
            }

            if (config.RepulseAgentsOptimizations)
            {
                try
                {
                    var target = AccessTools.Method(
                        AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorRepulseAgents"),
                        "OnGameTick", new Type[] { typeof(float) });
                    if (target != null && !DisableIfConflictingPatches(api, nameof(OptiTimeConfig.RepulseAgentsOptimizations), "RepulseAgents optimization", target))
                    {
                        RepulseAgentsOptimization.Initialize(api, harmony);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load RepulseAgents optimization: " + ex.Message);
                    config.RepulseAgentsOptimizations = false;
                }
            }

            if (config.EntityShapeOptimizations)
            {
                try
                {
                    var target = AccessTools.Method("Vintagestory.GameContent.EntityShapeRenderer:TesselateShape", Type.EmptyTypes);
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.EntityShapeOptimizations), "Entity reshape budget", target))
                    {
                        EntityShapeOptimization.Initialize(api, harmony, target);
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load entity reshape budget: " + ex.Message);
                    config.EntityShapeOptimizations = false;
                }
            }

            if (optimumHostDetected)
                AuditOptimumPatches(api);

            api.Event.LevelFinalize += OnLevelFinalize;
            api.Event.LeaveWorld += OnLeaveWorld;
        }

        private void OnLeaveWorld()
        {
            AmbientSoundOptimization.Cleanup();
            DynamicLightOptimization.Cleanup();
            EntityShadowCullingOptimization.OnLeaveWorld();
            GuiItemRenderInfoOptimization.ResetSessionState();
        }

        private void OnLevelFinalize()
        {
            VerifyGuiItemRenderInfoPatches(capi);
            VerifyShadowPatches(capi);
            capi?.Event.RegisterCallback((dt) =>
            {
                RecipeLookupCacheOptimization.Cleanup();
                InitializeOptimizations(capi);
                RegisterViewDistanceWatcher(capi);
                InitializeHandbookOptimization(capi);
                RegisterDiagModules();
            }, 100);
        }

        private void VerifyGuiItemRenderInfoPatches(ICoreClientAPI api)
        {
            if (harmony == null || guiItemRenderTarget == null || guiItemRenderInfoTarget == null)
            {
                return;
            }

            if (HasAnyConflictingPatches(guiItemRenderTarget) || HasAnyForeignPatches(guiItemRenderInfoTarget))
            {
                harmony.Unpatch(guiItemRenderTarget, HarmonyPatchType.All, harmony.Id);
                GuiItemRenderInfoOptimization.Cleanup();
                guiItemRenderTarget = null;
                guiItemRenderInfoTarget = null;
                api?.Logger?.Notification("[OptiTime] GUI ItemRenderInfo scratch reuse yielded to a late-loaded mod patch.");
            }
        }

        private void VerifyShadowPatches(ICoreClientAPI api)
        {
            if (harmony == null || renderShadowTarget == null)
            {
                return;
            }

            if (HasAnyConflictingPatches(renderShadowTarget))
            {
                harmony.Unpatch(renderShadowTarget, HarmonyPatchType.All, harmony.Id);
                if (reloadShaderDelegate != null && api?.Event != null)
                {
                    try { api.Event.ReloadShader -= reloadShaderDelegate; } catch { }
                    reloadShaderDelegate = null;
                }
                ShadowOptimization.Cleanup();
                renderShadowTarget = null;
                if (config != null && config.ShadowFarVegetationCullEnabled)
                {
                    config.ForceOffForSession(nameof(OptiTimeConfig.ShadowFarVegetationCullEnabled), "Yielded to late-loaded mod patch on ChunkRenderer.RenderShadow");
                }
                api?.Logger?.Notification("[OptiTime] Shadow optimization yielded to a late-loaded mod patch.");
            }
        }

        private void InitializeHandbookOptimization(ICoreClientAPI api)
        {
            if (!config.HandbookOptimizations) return;

            try
            {
                // Get all item stacks for handbook indexing
                var allstacks = new System.Collections.Generic.List<ItemStack>();

                foreach (CollectibleObject obj in api.World.Collectibles)
                {
                    var stacks = obj.GetHandBookStacks(api);
                    if (stacks != null)
                    {
                        foreach (ItemStack stack in stacks)
                        {
                            allstacks.Add(stack);
                        }
                    }
                }

                // Start async indexing
                if (allstacks.Count > 0)
                {
                    HandbookOptimization.InitializeIndexAsync(api, allstacks.ToArray());
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[OptiTime] Failed to initialize handbook optimization: {ex.Message}");
            }
        }

        private void ApplyEntityShadowQuality(ICoreClientAPI api, int shadowQ, bool isStartup)
        {
            EntityShadowCullingOptimization.UpdateShadowMapQuality(shadowQ);

            if (config == null || !config.EntityShadowDistanceCullEnabled ||
                config.IsConflictDisabled(nameof(OptiTimeConfig.EntityShadowDistanceCullEnabled)))
            {
                return;
            }

            if (shadowQ <= 1)
            {
                config.MarkEntityShadowCullSkipped(shadowQ);
                if (isStartup)
                {
                    api.Logger.Notification($"[OptiTime] Entity shadow distance cull skipped (shadowQuality={shadowQ}, cascade range below cull distance 80)");
                }
                else
                {
                    api.Logger.Notification($"[OptiTime] Entity shadow distance cull paused (shadowQuality={shadowQ}, cascade range below cull distance 80)");
                }
                return;
            }

            config.ClearEntityShadowCullSkipped();
            if (entityShadowPatched || harmony == null)
            {
                return;
            }

            try
            {
                EntityShadowCullingOptimization.SetLogger(msg => api.Logger.Warning(msg));
                var onRenderShadows = AccessTools.Method("Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrameShadows", new Type[] { typeof(float) });
                if (onRenderShadows == null)
                    throw new InvalidOperationException("SystemRenderEntities.OnRenderFrameShadows not found");

                if (optimumHostDetected && !OptimumOwnership.IsVanillaBody(onRenderShadows, GameVersion.ShortGameVersion))
                {
                    api.Logger.Notification("[OptiTime] Entity shadow distance cull skipped: target is not a vanilla body.");
                    return;
                }

                if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.EntityShadowDistanceCullEnabled), "Entity shadow distance cull", onRenderShadows))
                {
                    harmony.Patch(
                        onRenderShadows,
                        prefix: new HarmonyMethod(typeof(EntityShadowCullingOptimization), nameof(EntityShadowCullingOptimization.OnRenderFrameShadows_Prefix)));
                    EntityShadowCullingOptimization.SetPatchActive(true);
                    entityShadowPatched = true;
                    api.Logger.Notification("[OptiTime] Entity shadow distance cull loaded");
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] Failed to load entity shadow distance cull: " + ex.Message);
                config.EntityShadowDistanceCullEnabled = false;
            }
        }

        private void RegisterShadowMapQualityWatcher(ICoreClientAPI api)
        {
            if (shadowMapQualityChangedDelegate != null || api?.Settings?.Int == null)
            {
                return;
            }

            shadowMapQualityChangedDelegate = (newShadowQuality) =>
            {
                if (capi != null)
                {
                    ApplyEntityShadowQuality(capi, newShadowQuality, isStartup: false);
                }
            };

            api.Settings.Int.AddWatcher("shadowMapQuality", shadowMapQualityChangedDelegate);
        }

        private void RegisterViewDistanceWatcher(ICoreClientAPI api)
        {
            try
            {
                var clientSettings = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
                if (clientSettings == null) return;

                var instField = AccessTools.Field(clientSettings, "Inst");
                if (instField == null) return;

                var inst = instField.GetValue(null);
                if (inst == null) return;

                var viewDistProp = AccessTools.Property(clientSettings, "ViewDistance");
                if (viewDistProp != null)
                {
                    int initialViewDistance = (int)viewDistProp.GetValue(null);
                    if (config.DynamicLightOptimizations)
                        DynamicLightOptimization.UpdateViewDistance(initialViewDistance);
                    if (config.ParticleOptimizations)
                        ParticleOptimization.UpdateViewDistance(initialViewDistance);
                    if (config.OcclusionCullingOptimizations)
                        OcclusionCullingOptimization.UpdateViewDistance(initialViewDistance);
                    if (config.EntityAnimationOptimizations)
                        EntityAnimationOptimization.UpdateViewDistance(initialViewDistance);
                }

                var addWatcherMethod = AccessTools.Method(clientSettings, "AddWatcher");
                if (addWatcherMethod == null) return;

                var genericMethod = addWatcherMethod.MakeGenericMethod(typeof(int));
                viewDistanceChangedDelegate = (newViewDistance) =>
                {
                    if (config.DynamicLightOptimizations)
                        DynamicLightOptimization.UpdateViewDistance(newViewDistance);
                    if (config.ParticleOptimizations)
                        ParticleOptimization.UpdateViewDistance(newViewDistance);
                    if (config.OcclusionCullingOptimizations)
                        OcclusionCullingOptimization.UpdateViewDistance(newViewDistance);
                    if (config.EntityAnimationOptimizations)
                        EntityAnimationOptimization.UpdateViewDistance(newViewDistance);
                };

                genericMethod.Invoke(inst, new object[] { "viewDistance", viewDistanceChangedDelegate });
            }
            catch { }
        }

        /// <summary>
        /// Points the entity render passes and the name tag HUD at the renderers the entity animation
        /// pass marked visible. Each pass is independent: one that another mod already patches keeps
        /// walking every renderer, and none is patched unless removed renderers can be tracked.
        /// </summary>
        private void PatchVisibleEntityPasses(ICoreClientAPI api)
        {
            try
            {
                var removeRenderer = AccessTools.Method("Vintagestory.Client.NoObf.ClientMain:RemoveEntityRenderer", new Type[] { typeof(Entity) })
                    ?? throw new InvalidOperationException("ClientMain.RemoveEntityRenderer not found");
                harmony.Patch(removeRenderer,
                    postfix: new HarmonyMethod(typeof(EntityAnimationOptimization), nameof(EntityAnimationOptimization.OnEntityRendererRemoved)));
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] Visible entity list disabled: " + ex.Message);
                return;
            }

            var renderPass = new HarmonyMethod(typeof(EntityAnimationOptimization), nameof(EntityAnimationOptimization.TranspileRenderPass));
            var nameTags = new HarmonyMethod(typeof(EntityAnimationOptimization), nameof(EntityAnimationOptimization.RenderVisibleNameTags));
            var passes = new (string Target, HarmonyMethod Prefix, HarmonyMethod Transpiler)[]
            {
                ("Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderOpaque3D", null, renderPass),
                ("Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderAfterOIT", null, renderPass),
                ("Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrame2D", null, renderPass),
                ("Vintagestory.Client.NoObf.HudEntityNameTags:OnRenderGUI", nameTags, null)
            };

            foreach (var pass in passes)
            {
                try
                {
                    var method = AccessTools.Method(pass.Target, new Type[] { typeof(float) });
                    if (method == null || HasAnyConflictingPatches(method))
                    {
                        api.Logger.Warning($"[OptiTime] Visible entity list not applied to {pass.Target} (missing or patched by another mod)");
                        continue;
                    }

                    harmony.Patch(method, prefix: pass.Prefix, transpiler: pass.Transpiler);
                }
                catch (Exception ex)
                {
                    api.Logger.Warning($"[OptiTime] Visible entity list not applied to {pass.Target}: {ex.Message}");
                }
            }
        }

        private bool HasConflictingPrefix(System.Reflection.MethodBase method)
        {
            try
            {
                var patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo == null || patchInfo.Prefixes == null)
                    return false;

                foreach (var patch in patchInfo.Prefixes)
                {
                    // If another Harmony owner has a prefix that could skip vanilla, consider it conflicting
                    if (patch.owner != harmony.Id)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private bool HasAnyConflictingPatches(System.Reflection.MethodBase method)
        {
            // Conflict policy (audit B4):
            //   - Foreign transpilers: CONFLICT. Two transpilers on the same method risk
            //     IL-level cascade failures that are very hard to diagnose.
            //   - Foreign prefixes: CONFLICT. A prefix returning false can skip vanilla in
            //     ways that interact poorly with our patches. We cannot inspect IL of the
            //     foreign prefix to know if it returns false, so we treat all as conflicting.
            //   - Foreign postfixes: SAFE. Multiple postfixes compose; all are invoked.
            //   - Foreign finalizers: SAFE. Finalizers do not change control flow.
            //
            // Net effect: a mod that adds telemetry/HUD via postfix on a method we also
            // patch with a postfix no longer forces our optimization off. Transpilers and
            // prefix-skip patterns remain treated as conflicts.
            return HasForeignPatch(method, includePostfixesAndFinalizers: false);
        }

        // Rejects all foreign patch kinds (including postfixes and finalizers) on methods
        // whose call site is bypassed by a transpiler, so third-party hooks are never skipped.
        private bool HasAnyForeignPatches(System.Reflection.MethodBase method)
        {
            return HasForeignPatch(method, includePostfixesAndFinalizers: true);
        }

        private bool HasForeignPatch(System.Reflection.MethodBase method, bool includePostfixesAndFinalizers)
        {
            try
            {
                if (method == null)
                    return false;

                var patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo == null)
                    return false;

                bool HasOtherOwner(ICollection<Patch> patches)
                {
                    if (patches == null) return false;
                    foreach (var patch in patches)
                    {
                        if (patch.owner != harmony.Id)
                            return true;
                    }
                    return false;
                }

                if (HasOtherOwner(patchInfo.Prefixes) || HasOtherOwner(patchInfo.Transpilers))
                    return true;

                return includePostfixesAndFinalizers &&
                       (HasOtherOwner(patchInfo.Postfixes) || HasOtherOwner(patchInfo.Finalizers));
            }
            catch { }
            return false;
        }

        private bool DisableIfConflictingPatches(ICoreClientAPI api, string optimizationName, string label, System.Reflection.MethodBase method)
        {
            if (method == null)
            {
                throw new InvalidOperationException($"Patch target missing for {label}");
            }

            if (!HasAnyConflictingPatches(method))
                return false;

            DisableOptimizationForConflict(api, optimizationName, BuildPatchConflictReason(label, method));
            return true;
        }

        private string BuildPatchConflictReason(string label, System.Reflection.MethodBase method)
        {
            string typeName = method?.DeclaringType?.Name ?? "UnknownType";
            string methodName = method?.Name ?? "UnknownMethod";
            return $"{label} disabled due to existing patches on {typeName}.{methodName}";
        }

        private void DisableOptimizationForConflict(ICoreClientAPI api, string optimizationName, string reason)
        {
            config.ForceOffForSession(optimizationName, reason);
            ApplyRuntimeConfig();
            api.Logger.Warning("[OptiTime] " + reason);
        }

        private void InitializeOptimizations(ICoreClientAPI api)
        {
            int count = 0;
            if (config.DynamicLightOptimizations) count++;
            if (config.EntityAnimationOptimizations) count++;
            if (config.ParticleOptimizations) count++;
            if (config.OcclusionCullingOptimizations) count++;
            if (config.AmbientSoundOptimizations) count++;
            if (config.FlySoundOptimizations) count++;
            if (config.BackgroundFpsLimiterEnabled) count++;
            if (config.PreciseFramePacingEnabled) count++;
            if (config.GuiManagerOptimizations) count++;
            if (config.HandbookOptimizations) count++;
            if (config.RecipeLookupOptimizations) count++;
            if (config.WeatherWindOptimizations) count++;
            if (config.TickingBlocksOptimizations) count++;
            if (config.ShadowFarVegetationCullEnabled) count++;
            if (config.EntityShadowDistanceCullEnabled) count++;
            if (config.EntityInterpolationOptimizations) count++;
            if (config.RepulseAgentsOptimizations) count++;
            if (config.EntityShapeOptimizations) count++;
            if (config.ShaderOptimizations && !config.IsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations))) count++;

            if (count > 0)
                api.Logger.Notification($"[OptiTime] {count} optimization(s) enabled");
            else
                api.Logger.Notification("[OptiTime] No optimizations enabled. Use .optitime");

            if (config.ParticleOptimizations)
                ParticleOptimization.InitializeFrustum(api);
        }

        private void TryDisableOptiTimeShaders(ICoreClientAPI api, string reason, string[] pathsToDisable)
        {
            try
            {
                var origins = api.Assets?.Origins;
                if (origins == null || origins.Count == 0 || pathsToDisable == null || pathsToDisable.Length == 0)
                    return;

                // Hide only shader files inside the asset origin wrapper so OptiTime's own lang and config assets stay loaded.
                HideOptiTimeShaders(api, new List<string>(pathsToDisable), reason);
            }
            catch (Exception ex)
            {
                api.Logger.Error("[OptiTime] Failed to disable shader assets: " + ex.Message);
            }
        }

        private bool IsOptiTimeOrigin(IAssetOrigin origin)
        {
            return origin != null && IsModOriginPath(origin.OriginPath, Mod?.FileName, Mod?.Info?.ModID);
        }

        /// <summary>
        /// True when an asset origin path belongs to the mod. Only the folder the origin itself points at is
        /// compared: the last path segment, or the one before a trailing "assets" for a mod that is unpacked or
        /// installed as a folder. Looking at any other segment also matches a game or data folder that merely
        /// has the mod id somewhere in its path, which would remove the base game assets when the mod
        /// disables its shader overrides.
        /// </summary>
        internal static bool IsModOriginPath(string originPath, string modFileName, string modId)
        {
            if (string.IsNullOrEmpty(originPath))
                return false;

            string[] segments = originPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return false;

            string folder = segments[segments.Length - 1];
            if (string.Equals(folder, "assets", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
                folder = segments[segments.Length - 2];

            // "OptiTime-1.5.17.zip" for the archive and "OptiTime-1.5.17.zip_<hash>" for the folder it is unpacked to.
            if (!string.IsNullOrEmpty(modFileName) &&
                (string.Equals(folder, modFileName, StringComparison.OrdinalIgnoreCase) ||
                 folder.StartsWith(modFileName + "_", StringComparison.OrdinalIgnoreCase)))
                return true;

            return !string.IsNullOrEmpty(modId) && string.Equals(folder, modId, StringComparison.OrdinalIgnoreCase);
        }

        public void RegisterCommands(ICoreClientAPI api)
        {
#pragma warning disable CS0618
            api.RegisterCommand("optitime", "OptiTime optimization controls", ".optitime [status|&lt;opt&gt; on/off]", OnOptiTimeCommand);
#pragma warning restore CS0618
        }

        private void OnOptiTimeCommand(int groupId, CmdArgs args)
        {
            if (args.Length == 0)
            {
                config.PrintStatus(capi);
                return;
            }

            string command = args[0];

            if (command.Equals("profile", StringComparison.OrdinalIgnoreCase))
            {
                HandleProfileCommand(args);
                return;
            }

            if (command.Equals("interpdiag", StringComparison.OrdinalIgnoreCase))
            {
                HandleInterpDiagCommand(args);
                return;
            }

            if (command.Equals("diag", StringComparison.OrdinalIgnoreCase))
            {
                HandleDiagCommand(args);
                return;
            }

            if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                config.PrintStatus(capi);
                return;
            }

            string cmdLower = command.ToLower();
            if (OptimizationMap.TryGetValue(cmdLower, out string optName))
            {
                // Optimization name provided
                if (args.Length < 2)
                {
                    // No second argument - show info
                    config.PrintCategoryInfo(capi, command);
                    return;
                }

                string action = args[1];
                if (action.Equals("on", StringComparison.OrdinalIgnoreCase))
                {

                    if (config.IsConflictDisabled(optName))
                    {
                        if (config.IsForcedOffForSession(optName))
                        {
                            config.SetOptimization(optName, true);
                            config.Save(capi);
                        }

                        string reason = config.GetConflictReason(optName) ?? "conflict detected";
                        capi.ShowChatMessage($"[OptiTime] {command}: cannot enable");
                        capi.ShowChatMessage($"[OptiTime] {reason}");
                        return;
                    }
                    config.SetOptimization(optName, true);
                    config.Save(capi);
                    ApplyRuntimeConfig();
                    capi.ShowChatMessage(Lang.Get("optitime:cmd-enabled", command));
                    if (!NoRestartRequired.Contains(optName))
                        capi.ShowChatMessage(Lang.Get("optitime:cmd-restart-required"));
                }
                else if (action.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    config.SetOptimization(optName, false);
                    config.Save(capi);
                    ApplyRuntimeConfig();
                    capi.ShowChatMessage(Lang.Get("optitime:cmd-disabled", command));
                    if (!NoRestartRequired.Contains(optName))
                        capi.ShowChatMessage(Lang.Get("optitime:cmd-restart-required"));
                }
                else
                {
                    capi.ShowChatMessage(Lang.Get("optitime:cmd-usage-toggle"));
                }
            }
            else
            {
                capi.ShowChatMessage(Lang.Get("optitime:cmd-unknown", command));
                capi.ShowChatMessage(Lang.Get("optitime:cmd-available", string.Join(", ", OptimizationMap.Keys)));
            }
        }

        public override void Dispose()
        {
            if (runtimeUnsupported) return;

            try
            {
                // Unregister event handlers to prevent memory leaks
                if (capi != null)
                {
                    capi.Event.LevelFinalize -= OnLevelFinalize;
                    capi.Event.LeaveWorld -= OnLeaveWorld;
                }

                if (shadowMapQualityChangedDelegate != null)
                {
                    try
                    {
                        capi?.Settings?.Int?.RemoveWatcher("shadowMapQuality", shadowMapQualityChangedDelegate);
                    }
                    catch { }
                    shadowMapQualityChangedDelegate = null;
                }

                // Unregister view distance watcher
                if (viewDistanceChangedDelegate != null)
                {
                    try
                    {
                        var clientSettings = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
                        var removeWatcherMethod = AccessTools.Method(clientSettings, "RemoveWatcher");
                        if (removeWatcherMethod != null && clientSettings != null)
                        {
                            var genericMethod = removeWatcherMethod.MakeGenericMethod(typeof(int));
                            var instField = AccessTools.Field(clientSettings, "Inst");
                            var inst = instField?.GetValue(null);
                            if (inst != null)
                            {
                                genericMethod.Invoke(inst, new object[] { "viewDistance", viewDistanceChangedDelegate });
                            }
                        }
                    }
                    catch { }
                    viewDistanceChangedDelegate = null;
                }
                if (reloadShaderDelegate != null)
                {
                    try
                    {
                        if (capi?.Event != null)
                            capi.Event.ReloadShader -= reloadShaderDelegate;
                    }
                    catch { }
                    reloadShaderDelegate = null;
                }
                entityShadowPatched = false;

                harmony?.UnpatchAll("com.zaldaryon.optitime");

                // Clear static references unconditionally so runtime or session-scoped toggles never leak state
                ParticleOptimization.Cleanup();
                DynamicLightOptimization.Cleanup();
                AmbientSoundOptimization.Cleanup();
                GuiManagerOptimization.Cleanup();
                FlySoundOptimization.Cleanup();
                FrameRateOptimization.Cleanup();
                HandbookOptimization.Cleanup();
                EntityInterpolationOptimization.Cleanup();
                RepulseAgentsOptimization.Cleanup();
                EntityShapeOptimization.Cleanup();
                EntityAnimationOptimization.Cleanup();

                // Cleanup recipe lookup optimization (no resources currently)
                RecipeLookupCacheOptimization.Cleanup();
                WeatherWindOptimization.Cleanup();
                ShadowOptimization.Cleanup();
                EntityShadowCullingOptimization.Cleanup();
                OcclusionCullingOptimization.Cleanup();
                FrustumCullingOptimization.Cleanup();
                AnimatableOptimization.Cleanup();
                SunMoonOptimization.Cleanup();
                GuiItemRenderInfoOptimization.Cleanup();
                ConfigLibIntegration.Cleanup();
                TranslationServicePatch.Cleanup();
                SafeCatalogIsolation.Cleanup();
                ChunkUploadOptimization.Cleanup();

                ProfilingHelper.Cleanup();
            }
            catch (Exception ex)
            {
                capi?.Logger?.Warning($"[OptiTime] Error during dispose: {ex.Message}");
            }
            finally
            {
                ancestralBlissDetected = false;
                coriaenderDetected = false;
                optimumHostDetected = false;
                externalWorldShaderOverrideDetected = false;
                externalBlurOverrideDetected = false;
                chunkShadowMapOverrideActive = false;
                runtimeUnsupported = false;
                harmony = null;
                guiItemRenderTarget = null;
                guiItemRenderInfoTarget = null;
                renderShadowTarget = null;
                config = null;
                Config = null;
                staticCapi = null;
                capi = null;
            }
        }

        /// <summary>
        /// Optimum rewrites the game assemblies, so OptiTime only keeps the optimizations whose target
        /// methods are still the vanilla bodies and that Optimum does not ship as its own setting.
        /// Everything else is switched off for this session only: the player's OptiTime.json keeps every
        /// value, so the optimizations come back as soon as Optimum is removed.
        /// </summary>
        private void ApplyOptimumPolicy(ICoreClientAPI api)
        {
            string gameVersion = GameVersion.ShortGameVersion;
            string reason = null;
            int yielded = 0;
            var kept = new List<string>();

            try
            {
                reason = Lang.Get("optitime:compat-optimum-reason");

                foreach (OptimumOwnership.Feature feature in OptimumOwnership.Features)
                {
                    if (!config.GetOptimizationValue(feature.OptimizationName))
                        continue;

                    string cause = OptimumOwnership.YieldReason(feature, gameVersion, OptimumOwnership.ClientSettingsHasMember);
                    if (cause == null)
                    {
                        kept.Add(feature.OptimizationName);
                        continue;
                    }

                    config.ForceOffForSession(feature.OptimizationName, reason);
                    yielded++;
                    api.Logger.Notification("[OptiTime] {0} yields to Optimum: {1}.", feature.OptimizationName, cause);
                }
            }
            catch (Exception ex)
            {
                // Fail closed: when the checks cannot finish, nothing that could patch next to Optimum stays on.
                api.Logger.Warning("[OptiTime] The Optimum compatibility check failed ({0}). Every optimization is switched off for this session.", ex.Message);
                reason ??= "Provided by Optimum";
                foreach (OptimumOwnership.Feature feature in OptimumOwnership.Features)
                {
                    if (config.GetOptimizationValue(feature.OptimizationName))
                    {
                        config.ForceOffForSession(feature.OptimizationName, reason);
                        yielded++;
                    }
                }

                kept.Clear();
            }

            api.Logger.Notification("[OptiTime] Optimum detected (game {0}). {1} optimization(s) yield to Optimum for this session, {2} stay active. OptiTime.json is left unchanged.",
                gameVersion, yielded, kept.Count);

            if (yielded > 0 && !config.SuppressCompatibilityMessages)
            {
                try
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-optimum-title"));
                    api.ShowChatMessage(Lang.Get("optitime:compat-optimum-desc"));
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Could not show the Optimum compatibility message: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Keeps an OptiTime shader override only when the shader file underneath it is still the vanilla one.
        /// Optimum changes shaders such as chunkliquid and chunkshadowmap and checks them for its own
        /// contracts, so an override would also switch Optimum features off.
        /// </summary>
        private void ApplyOptimumShaderPolicy(ICoreClientAPI api)
        {
            try
            {
                ApplyOptimumShaderPolicyCore(api);
            }
            catch (Exception ex)
            {
                // Fail closed: keep none of the overrides when their files cannot be compared.
                api.Logger.Warning("[OptiTime] The Optimum shader check failed ({0}). The OptiTime shader overrides are hidden.", ex.Message);
                HideOptiTimeShaders(api, new List<string>(OptiTimeShaderPaths), "Optimum");
            }
        }

        private void ApplyOptimumShaderPolicyCore(ICoreClientAPI api)
        {
            var revert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, string> vanillaHashes = OptimumVanillaSignatures.ShadersForGameVersion(GameVersion.ShortGameVersion);

            foreach (string path in OptiTimeShaderPaths)
            {
                if (!api.Assets.AllAssets.TryGetValue(new AssetLocation("game", path), out IAsset current) || !IsOptiTimeOrigin(current.Origin))
                    continue;

                bool wanted = config.ShaderOptimizations && !ancestralBlissDetected;
                if (coriaenderDetected && IsWorldShaderPath(path))
                    wanted = false;
                if (!config.BlurOptimizationEnabled && path.StartsWith("shaders/blur.", StringComparison.OrdinalIgnoreCase))
                    wanted = false;

                bool underneathIsVanilla = IsUnderlyingShaderVanilla(api, path, vanillaHashes);

                if (!wanted || !underneathIsVanilla)
                    revert.Add(path);
            }

            if (revert.Contains("shaders/blur.fsh") || revert.Contains("shaders/blur.vsh"))
            {
                AddIfOptiTimeOwnsShader(api, "shaders/blur.fsh", revert);
                AddIfOptiTimeOwnsShader(api, "shaders/blur.vsh", revert);
            }

            if (revert.Count > 0)
                HideOptiTimeShaders(api, revert.ToList(), "Optimum");
        }

        private static IReadOnlyDictionary<string, string> ResolveVanillaShaderHashes()
        {
            return OptimumVanillaSignatures.ShadersForGameVersion(GameVersion.ShortGameVersion)
                ?? OptimumVanillaSignatures.ShadersForGameVersion("1.22.7");
        }

        private bool IsUnderlyingShaderVanilla(ICoreClientAPI api, string path, IReadOnlyDictionary<string, string> vanillaHashes)
        {
            try
            {
                if (vanillaHashes == null || !vanillaHashes.TryGetValue(path, out string expected))
                    return false;

                IAsset underneath = FindShaderUnderneath(api, path);
                if (underneath == null)
                    return false;

                if (underneath.Data == null)
                    underneath.Origin?.TryLoadAsset(underneath);

                return underneath.Data != null &&
                    string.Equals(expected, Sha256Hex(underneath.Data), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void AddIfOptiTimeOwnsShader(ICoreClientAPI api, string path, HashSet<string> targetPaths)
        {
            if (api.Assets?.AllAssets != null &&
                api.Assets.AllAssets.TryGetValue(new AssetLocation("game", path), out IAsset current) &&
                current != null &&
                IsOptiTimeOrigin(current.Origin))
            {
                targetPaths.Add(path);
            }
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The shader the game would use without OptiTime: the last other origin that provides it.</summary>
        private IAsset FindShaderUnderneath(ICoreClientAPI api, string path)
        {
            IAsset found = null;
            foreach (IAssetOrigin origin in api.Assets.Origins)
            {
                if (IsOptiTimeOrigin(origin))
                    continue;

                foreach (IAsset asset in origin.GetAssets(AssetCategory.shaders, true))
                {
                    if (asset?.Location != null &&
                        string.Equals(asset.Location.Domain, "game", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(asset.Location.Path, path, StringComparison.OrdinalIgnoreCase))
                        found = asset;
                }
            }

            if (found != null && found.Data == null)
                found.Origin?.TryLoadAsset(found);

            return found;
        }

        /// <summary>
        /// Hides the given OptiTime shader files and reloads the shader category, so the shaders underneath
        /// are used again. Only the named files are hidden, the other overrides stay.
        /// </summary>
        private void HideOptiTimeShaders(ICoreClientAPI api, List<string> paths, string reason)
        {
            try
            {
                List<IAssetOrigin> origins = api.Assets.Origins;
                for (int i = 0; i < origins.Count; i++)
                {
                    if (!IsOptiTimeOrigin(origins[i]))
                        continue;

                    var filtered = origins[i] as FilteredAssetOrigin ?? new FilteredAssetOrigin(origins[i]);
                    int hidden = 0;
                    foreach (string path in paths)
                    {
                        if (filtered.Hide(path))
                            hidden++;
                    }

                    origins[i] = filtered;
                    api.Assets.Reload(AssetCategory.shaders);
                    api.Logger.Notification("[OptiTime] {0} shader override(s) hidden so the shaders underneath are used ({1}).", hidden, reason);
                    return;
                }

                api.Logger.Warning("[OptiTime] The OptiTime asset origin was not found; the shader overrides stay.");
            }
            catch (Exception ex)
            {
                api.Logger.Error("[OptiTime] Failed to hide shader overrides: " + ex.Message);
            }
        }

        /// <summary>
        /// Safety net for the patches that were applied. Every method patched with the OptiTime Harmony id
        /// has to be a vanilla body when Optimum is the host. Anything else is unpatched and logged, which
        /// also catches a patch site the feature table does not know about.
        /// </summary>
        private void AuditOptimumPatches(ICoreClientAPI api)
        {
            string gameVersion = GameVersion.ShortGameVersion;
            try
            {
                foreach (System.Reflection.MethodBase method in harmony.GetPatchedMethods().ToList())
                {
                    try
                    {
                        if (OptimumOwnership.IsVanillaBody(method, gameVersion))
                            continue;

                        harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                        api.Logger.Warning("[OptiTime] Removed the patch on {0}.{1}: the method is not a verified vanilla body next to Optimum.", method.DeclaringType?.FullName, method.Name);
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Warning("[OptiTime] Could not audit the patch on {0}.{1}: {2}", method.DeclaringType?.FullName, method.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[OptiTime] The Optimum patch audit failed: " + ex.Message);
            }
        }

        private void DetectAndHandleConflicts(ICoreClientAPI api)
        {
            // Check for Ancestral Bliss Shaders
            if (ancestralBlissDetected)
            {
                config.ForceOffForSession(nameof(OptiTimeConfig.ShaderOptimizations), "Ancestral Bliss compatibility mode");
                api.Logger.Notification("═══════════════════════════════════════════════════════");
                api.Logger.Notification("[OptiTime] Ancestral Bliss Shaders detected");
                api.Logger.Notification("[OptiTime] Running in compatibility mode:");
                api.Logger.Notification("[OptiTime]   - Shaders: Using Ancestral Bliss (OptiTime shaders disabled)");
                api.Logger.Notification("[OptiTime]   - All code optimizations: Active");
                api.Logger.Notification("═══════════════════════════════════════════════════════");
                
                if (!config.SuppressCompatibilityMessages)
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-ancestral-title"));
                    api.ShowChatMessage(Lang.Get("optitime:compat-ancestral-desc"));
                }
            }

            // Check for Combat Overhaul / Overhaullib
            bool combatOverhaulDetected = api.ModLoader.IsModEnabled("combatoverhaul");
            bool overhaullibDetected = api.ModLoader.IsModEnabled("overhaullib");
            if (combatOverhaulDetected || overhaullibDetected)
            {
                api.Logger.Warning("═══════════════════════════════════════════════════════");
                api.Logger.Warning("[OptiTime] " + (combatOverhaulDetected ? "Combat Overhaul" : "Overhaullib") + " detected");
                api.Logger.Warning("[OptiTime] Disabling Entity Animation optimization to prevent conflicts");
                api.Logger.Warning("[OptiTime] All other optimizations remain active");
                api.Logger.Warning("═══════════════════════════════════════════════════════");
                
                config.ForceOffForSession(nameof(OptiTimeConfig.EntityAnimationOptimizations), combatOverhaulDetected ? "Combat Overhaul compatibility mode" : "Overhaullib compatibility mode");
                
                if (!config.SuppressCompatibilityMessages)
                {
                    if (combatOverhaulDetected)
                    {
                        api.ShowChatMessage(Lang.Get("optitime:compat-overhaul-title"));
                        api.ShowChatMessage(Lang.Get("optitime:compat-overhaul-desc"));
                    }
                    else
                    {
                        api.ShowChatMessage(Lang.Get("optitime:compat-overhaullib-title"));
                        api.ShowChatMessage(Lang.Get("optitime:compat-overhaullib-desc"));
                    }
                }
            }

            // Check for blood FX mods (Brutal Story, XBlood)
            bool brutalStoryDetected = api.ModLoader.IsModEnabled("brutalstory");
            bool xbloodDetected = api.ModLoader.IsModEnabled("xblood");
            if (brutalStoryDetected || xbloodDetected)
            {
                string modName = brutalStoryDetected ? "Brutal Story" : "XBlood";
                api.Logger.Warning("═══════════════════════════════════════════════════════");
                api.Logger.Warning("[OptiTime] " + modName + " detected");
                api.Logger.Warning("[OptiTime] Disabling Particle view distance scaling to preserve blood effects");
                api.Logger.Warning("[OptiTime] All other optimizations remain active");
                api.Logger.Warning("═══════════════════════════════════════════════════════");

                config.ForceOffForSession(nameof(OptiTimeConfig.ParticleViewDistanceScalingEnabled), modName + " compatibility mode");
                ApplyRuntimeConfig();

                if (!config.SuppressCompatibilityMessages)
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-bloodfx-title", modName));
                    api.ShowChatMessage(Lang.Get("optitime:compat-bloodfx-desc"));
                }
            }

            // Check for Electrical Progressive (Industry)
            if (api.ModLoader.IsModEnabled("electricalprogressiveindustry"))
            {
                api.Logger.Warning("═══════════════════════════════════════════════════════");
                api.Logger.Warning("[OptiTime] Electrical Progressive (Industry) detected");
                api.Logger.Warning("[OptiTime] Disabling Handbook optimization to prevent conflicts");
                api.Logger.Warning("[OptiTime] All other optimizations remain active");
                api.Logger.Warning("═══════════════════════════════════════════════════════");

                config.ForceOffForSession(nameof(OptiTimeConfig.HandbookOptimizations), "Electrical Progressive compatibility mode");

                if (!config.SuppressCompatibilityMessages)
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-electricalprogressive-title"));
                    api.ShowChatMessage(Lang.Get("optitime:compat-electricalprogressive-desc"));
                }
            }

            // Check for CoriaenderShaders (runtime shader patching via regex on chunkliquid)
            if (api.ModLoader.IsModEnabled("coriaendershaders"))
            {
                api.Logger.Warning("═══════════════════════════════════════════════════════");
                api.Logger.Warning("[OptiTime] CoriaenderShaders detected");
                api.Logger.Warning("[OptiTime] Disabling world shader file replacements for CoriaenderShaders compatibility");
                api.Logger.Warning("[OptiTime] VBAO and blur optimizations remain independent");
                api.Logger.Warning("[OptiTime] All code optimizations remain active");
                api.Logger.Warning("═══════════════════════════════════════════════════════");

                config.ForceOffForSession(nameof(OptiTimeConfig.ShaderOptimizations), "CoriaenderShaders compatibility mode");

                if (!config.SuppressCompatibilityMessages)
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-coriaender-title"));
                    api.ShowChatMessage(Lang.Get("optitime:compat-coriaender-desc"));
                }
            }

            if (externalWorldShaderOverrideDetected && !config.IsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations)))
            {
                config.ForceOffForSession(nameof(OptiTimeConfig.ShaderOptimizations), "External shader override");
            }

            if (externalBlurOverrideDetected && !config.IsConflictDisabled(nameof(OptiTimeConfig.BlurOptimizationEnabled)))
            {
                config.ForceOffForSession(nameof(OptiTimeConfig.BlurOptimizationEnabled), "External blur shader override");
            }

            // Check for smoke/firearms mods whose particles spawn via off-thread pools
            bool realSmokeDetected = api.ModLoader.IsModEnabled("realsmoke");
            bool firearmsDetected = api.ModLoader.IsModEnabled("maltiezfirearms") ||
                                    api.ModLoader.IsModEnabled("extrafirearms");
            if (realSmokeDetected || firearmsDetected)
            {
                string modName = realSmokeDetected ? "Real Smoke" : "Firearms";
                api.Logger.Notification("═══════════════════════════════════════════════════════");
                api.Logger.Notification("[OptiTime] " + modName + " detected");
                api.Logger.Notification("[OptiTime] Particle rejection disabled on off-thread pools");
                api.Logger.Notification("[OptiTime] Main-thread particle optimizations remain active");
                api.Logger.Notification("═══════════════════════════════════════════════════════");

                ParticleOptimization.SetSkipOffthreadRejection(true);

                if (!config.SuppressCompatibilityMessages)
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-smoke-title", modName));
                    api.ShowChatMessage(Lang.Get("optitime:compat-smoke-desc"));
                }
            }
        }

        private void HandleProfileCommand(CmdArgs args)
        {
            if (args.Length < 2)
            {
                string status = config.EnableProfiling ? Lang.Get("optitime:status-on") : Lang.Get("optitime:status-off");
                capi.ShowChatMessage(Lang.Get("optitime:profiling-status", status));
                capi.ShowChatMessage(Lang.Get("optitime:profiling-usage"));
                return;
            }

            string action = args[1].ToLower();
            switch (action)
            {
                case "on":
                    config.EnableProfiling = true;
                    config.Save(capi);
                    ProfilingHelper.SetEnabled(true);
                    capi.ShowChatMessage(Lang.Get("optitime:profiling-on"));
                    capi.ShowChatMessage(Lang.Get("optitime:profiling-on-hint"));
                    break;
                case "off":
                    config.EnableProfiling = false;
                    config.Save(capi);
                    ProfilingHelper.SetEnabled(false);
                    capi.ShowChatMessage(Lang.Get("optitime:profiling-off"));
                    break;
                case "dump":
                    ProfilingHelper.Dump(capi);
                    break;
                case "reset":
                    ProfilingHelper.ResetCounters();
                    capi.ShowChatMessage(Lang.Get("optitime:profiling-reset"));
                    break;
                default:
                    capi.ShowChatMessage(Lang.Get("optitime:profiling-usage"));
                    break;
            }
        }

        private void RegisterDiagModules()
        {
            DiagRegistry.Clear();
            if (config.EntityInterpolationOptimizations) DiagRegistry.Register(new ModuleEntityInterp());
            if (config.RepulseAgentsOptimizations) DiagRegistry.Register(new ModuleRepulse());
            if (config.EntityShapeOptimizations) DiagRegistry.Register(new ModuleEntityShape());
            if (config.EntityAnimationOptimizations) DiagRegistry.Register(new ModuleEntityAnim());
            if (config.DynamicLightOptimizations) DiagRegistry.Register(new ModuleDynLights());
            if (config.FlySoundOptimizations) DiagRegistry.Register(new ModuleFlySound());
            if (config.BackgroundFpsLimiterEnabled) DiagRegistry.Register(new ModuleBgFps());
            if (config.WeatherWindOptimizations) DiagRegistry.Register(new ModuleWeatherWind());
            if (config.ShadowFarVegetationCullEnabled) DiagRegistry.Register(new ModuleShadowVeg());
            if (config.EntityShadowDistanceCullEnabled) DiagRegistry.Register(new ModuleShadowEntity());
            if (config.TickingBlocksOptimizations) DiagRegistry.Register(new ModuleTickingBlocks());
            if (config.ParticleOptimizations) DiagRegistry.Register(new ModuleParticles());
            if (config.OcclusionCullingOptimizations) DiagRegistry.Register(new ModuleOcclusion());
            if (config.AmbientSoundOptimizations) DiagRegistry.Register(new ModuleAmbientSound());
            if (config.GuiManagerOptimizations) DiagRegistry.Register(new ModuleGuiMgr());
            if (config.HandbookOptimizations) DiagRegistry.Register(new ModuleHandbook());
            if (config.RecipeLookupOptimizations) DiagRegistry.Register(new ModuleRecipe());
            if (config.PreciseFramePacingEnabled) DiagRegistry.Register(new ModuleFramePace(true));
            else DiagRegistry.Register(new ModuleFramePace(false));
            if (config.ShaderOptimizations) DiagRegistry.Register(new ModuleShaders());
        }

        private void HandleDiagCommand(CmdArgs args)
        {
            if (args.Length < 2)
            {
                DiagRegistry.ListModules(capi);
                return;
            }

            string target = args[1].ToLower();
            string action = args.Length >= 3 ? args[2].ToLower() : "dump";

            if (target == "all")
            {
                switch (action)
                {
                    case "on": DiagRegistry.EnableAll(); capi.ShowChatMessage("[OptiTime] All diag modules enabled"); break;
                    case "off": DiagRegistry.DisableAll(); capi.ShowChatMessage("[OptiTime] All diag modules disabled"); break;
                    case "dump": DiagRegistry.DumpAll(capi); break;
                    case "reset": DiagRegistry.ResetAll(); capi.ShowChatMessage("[OptiTime] All diag modules reset"); break;
                    default: capi.ShowChatMessage("[OptiTime] usage: .optitime diag all on|off|dump|reset"); break;
                }
                return;
            }

            var module = DiagRegistry.Get(target);
            if (module == null)
            {
                capi.ShowChatMessage($"[OptiTime] Unknown diag module: {target}");
                DiagRegistry.ListModules(capi);
                return;
            }

            switch (action)
            {
                case "on": module.Enable(); capi.ShowChatMessage($"[OptiTime] diag {module.ShortName}: ON"); break;
                case "off": module.Disable(); capi.ShowChatMessage($"[OptiTime] diag {module.ShortName}: OFF"); break;
                case "dump":
                    if (module.IsAvailable && !module.Enabled)
                    {
                        capi.ShowChatMessage($"[OptiTime] diag {module.ShortName} is not enabled, run `.optitime diag {module.ShortName} on` first, then play ~30s before dumping");
                        break;
                    }
                    module.Dump(capi);
                    break;
                case "reset": module.Reset(); capi.ShowChatMessage($"[OptiTime] diag {module.ShortName}: reset"); break;
                default: capi.ShowChatMessage("[OptiTime] usage: .optitime diag &lt;module&gt; on|off|dump|reset"); break;
            }
        }

        private void HandleInterpDiagCommand(CmdArgs args)
        {
            if (args.Length < 2)
            {
                capi.ShowChatMessage("[OptiTime] .optitime interpdiag on/off/dump/reset — measures observed entity-position packet cadence");
                return;
            }
            switch (args[1].ToLower())
            {
                case "on":
                    EntityInterpolationOptimization.SetDiagnosticEnabled(true);
                    capi.ShowChatMessage("[OptiTime] EntityInterp diag: ON — run `.optitime interpdiag dump` after ~30s of nearby entity activity");
                    break;
                case "off":
                    EntityInterpolationOptimization.SetDiagnosticEnabled(false);
                    capi.ShowChatMessage("[OptiTime] EntityInterp diag: OFF");
                    break;
                case "dump":
                    EntityInterpolationOptimization.DumpDiagnostic(capi);
                    break;
                case "reset":
                    EntityInterpolationOptimization.ResetDiagnostic();
                    capi.ShowChatMessage("[OptiTime] EntityInterp diag counters reset");
                    break;
                default:
                    capi.ShowChatMessage("[OptiTime] usage: .optitime interpdiag on|off|dump|reset");
                    break;
            }
        }
    }
}
