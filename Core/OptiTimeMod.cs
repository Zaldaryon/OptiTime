using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using HarmonyLib;
using System;
using System.Collections.Generic;
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
        private bool runtimeUnsupported = false;
        private Action<int> viewDistanceChangedDelegate = null;
        
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
            ParticleOptimization.ConfigureScaling(Config.ParticleViewDistanceScalingEnabled);
            GuiManagerOptimization.Configure(Config);
            ProfilingHelper.SetEnabled(Config.EnableProfiling);
        }
        
        private static readonly string[] OptiTimeShaderPaths = new[]
        {
            "shaders/blur.fsh",
            "shaders/blur.vsh",
            "shaders/chunkliquid.fsh",
            "shaders/chunkshadowmap.fsh"
        };
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

            if (!config.ShaderOptimizations || ancestralBlissDetected)
            {
                string reason = ancestralBlissDetected ? "Ancestral Bliss" : "config";
                TryDisableOptiTimeShaders(clientApi, reason, OptiTimeShaderPaths);
            }
            else
            {
                // Granular shader disabling
                var shadersToDisable = new List<string>();

                // Coriaender patches chunkliquid at runtime via regex; our replacement would break its patterns
                if (coriaenderDetected)
                {
                    shadersToDisable.Add("shaders/chunkliquid.fsh");
                    shadersToDisable.Add("shaders/chunkshadowmap.fsh");
                }

                if (!config.BlurOptimizationEnabled)
                {
                    shadersToDisable.Add("shaders/blur.fsh");
                    shadersToDisable.Add("shaders/blur.vsh");
                }
                if (shadersToDisable.Count > 0)
                    TryDisableOptiTimeShaders(clientApi, coriaenderDetected ? "CoriaenderShaders" : "config", shadersToDisable.ToArray());
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
                // (sched_yield latency 1-4ms per call). Auto-disable on non-Windows.
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    config.PreciseFramePacingEnabled = false;
                    config.Save(api);
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
                            transpiler: new HarmonyMethod(typeof(OcclusionCullingOptimization), "TranspileThreshold"));
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
                        config.GuiManagerNoLinqEnabled = false;
                        config.Save(api);
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
                            config.GuiManagerInputNoLinqEnabled = false;
                            config.Save(api);
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

            if (config.ShadowFarVegetationCullEnabled)
            {
                try
                {
                    ShadowOptimization.SetLogger(msg => api.Logger.Warning(msg));
                    var renderShadow = AccessTools.Method("Vintagestory.Client.NoObf.ChunkRenderer:RenderShadow", new Type[] { typeof(float) });
                    if (renderShadow == null)
                        throw new InvalidOperationException("ChunkRenderer.RenderShadow not found");

                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.ShadowFarVegetationCullEnabled), "Shadow far vegetation cull", renderShadow))
                    {
                        harmony.Patch(renderShadow,
                            transpiler: new HarmonyMethod(typeof(ShadowOptimization), nameof(ShadowOptimization.TranspileRenderShadow)));
                        api.Logger.Notification("[OptiTime] Shadow far vegetation cull loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load shadow far vegetation cull: " + ex.Message);
                    config.ShadowFarVegetationCullEnabled = false;
                }
            }

            if (config.EntityShadowDistanceCullEnabled)
            {
                // At shadowQuality=1 the far cascade range is 60 blocks, which is smaller
                // than our 80-block cull distance — the transpiler would never cull anything.
                int shadowQ = 0;
                try
                {
                    var csType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
                    var prop = csType?.GetProperty("ShadowMapQuality", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null) shadowQ = (int)prop.GetValue(null);
                }
                catch { }

                if (shadowQ <= 1)
                {
                    api.Logger.Notification("[OptiTime] Entity shadow distance cull skipped (shadowQuality=1, cascade range 60 < cull distance 80)");
                }
                else try
                {
                    EntityShadowCullingOptimization.SetLogger(msg => api.Logger.Warning(msg));
                    var onRenderShadows = AccessTools.Method("Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrameShadows", new Type[] { typeof(float) });
                    if (onRenderShadows == null)
                        throw new InvalidOperationException("SystemRenderEntities.OnRenderFrameShadows not found");

                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.EntityShadowDistanceCullEnabled), "Entity shadow distance cull", onRenderShadows))
                    {
                        harmony.Patch(onRenderShadows,
                            transpiler: new HarmonyMethod(typeof(EntityShadowCullingOptimization), nameof(EntityShadowCullingOptimization.TranspileOnRenderFrameShadows)));
                        api.Logger.Notification("[OptiTime] Entity shadow distance cull loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[OptiTime] Failed to load entity shadow distance cull: " + ex.Message);
                    config.EntityShadowDistanceCullEnabled = false;
                }
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

            api.Event.LevelFinalize += OnLevelFinalize;
        }

        private void OnLevelFinalize()
        {
            capi?.Event.RegisterCallback((dt) =>
            {
                RecipeLookupCacheOptimization.Cleanup();
                InitializeOptimizations(capi);
                RegisterViewDistanceWatcher(capi);
                InitializeHandbookOptimization(capi);
                RegisterDiagModules();
            }, 100);
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

                return HasOtherOwner(patchInfo.Prefixes) ||
                       HasOtherOwner(patchInfo.Transpilers);
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
            config.SetOptimization(optimizationName, false);
            config.MarkAsConflictDisabled(optimizationName, reason);
            config.Save(api);
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
                if (origins == null || origins.Count == 0)
                    return;

                bool disableAll = pathsToDisable.Length == OptiTimeShaderPaths.Length;

                if (disableAll)
                {
                    // Remove entire OptiTime shader origin
                    List<IAssetOrigin> toRemove = new List<IAssetOrigin>();
                    foreach (var origin in origins)
                    {
                        if (!IsOptiTimeOrigin(origin))
                            continue;
                        if (OriginContainsOptiTimeShaders(origin))
                            toRemove.Add(origin);
                    }

                    if (toRemove.Count == 0)
                    {
                        api.Logger.Warning("[OptiTime] ShaderOptimizations disabled, but OptiTime shader origin was not found.");
                        return;
                    }

                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        origins.Remove(toRemove[i]);
                    }

                    api.Assets.Reload(AssetCategory.shaders);
                    api.Assets.Reload(AssetCategory.shaderincludes);
                    api.Logger.Notification($"[OptiTime] All shader assets disabled ({reason}).");
                }
                else
                {
                    // Selectively disable specific shaders by removing from AllAssets
                    // then reloading so vanilla versions are restored from game origin
                    var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < pathsToDisable.Length; i++)
                        pathSet.Add(pathsToDisable[i]);

                    var allAssets = api.Assets.AllAssets;
                    int removed = 0;
                    var keysToRemove = new List<AssetLocation>();
                    foreach (var kvp in allAssets)
                    {
                        if (!string.Equals(kvp.Key.Domain, "game", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (pathSet.Contains(kvp.Key.Path))
                            keysToRemove.Add(kvp.Key);
                    }
                    for (int i = 0; i < keysToRemove.Count; i++)
                    {
                        allAssets.Remove(keysToRemove[i]);
                        removed++;
                    }

                    if (removed > 0)
                    {
                        // Reload shaders so vanilla versions are picked up from game origin
                        api.Assets.Reload(AssetCategory.shaders);
                        api.Logger.Notification($"[OptiTime] {removed} shader(s) reverted to vanilla ({reason}).");
                    }
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error("[OptiTime] Failed to disable shader assets: " + ex.Message);
            }
        }

        private bool IsOptiTimeOrigin(IAssetOrigin origin)
        {
            if (origin == null)
                return false;

            string originPath = origin.OriginPath ?? string.Empty;
            if (!string.IsNullOrEmpty(Mod?.FileName) &&
                originPath.IndexOf(Mod.FileName, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (!string.IsNullOrEmpty(Mod?.Info?.ModID) &&
                originPath.IndexOf(Mod.Info.ModID, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private bool OriginContainsOptiTimeShaders(IAssetOrigin origin)
        {
            try
            {
                foreach (var category in new[] { AssetCategory.shaders, AssetCategory.shaderincludes })
                {
                    var assets = origin.GetAssets(category, false);
                    foreach (var asset in assets)
                    {
                        if (asset?.Location == null)
                            continue;
                        if (!string.Equals(asset.Location.Domain, "game", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (IsOptiTimeShaderPath(asset.Location.Path))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private bool IsOptiTimeShaderPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            for (int i = 0; i < OptiTimeShaderPaths.Length; i++)
            {
                if (path.Equals(OptiTimeShaderPaths[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

                harmony?.UnpatchAll("com.zaldaryon.optitime");

                // Clear static references to prevent memory leaks
                if (config != null)
                {
                    if (config.ParticleOptimizations)
                        ParticleOptimization.Cleanup();
                    if (config.AmbientSoundOptimizations)
                        AmbientSoundOptimization.Cleanup();
                    if (config.GuiManagerOptimizations)
                        GuiManagerOptimization.Cleanup();
                    if (config.FlySoundOptimizations)
                        FlySoundOptimization.Cleanup();
                    FrameRateOptimization.Cleanup();
                    if (config.HandbookOptimizations)
                        HandbookOptimization.Cleanup();
                    EntityInterpolationOptimization.Cleanup();
                    RepulseAgentsOptimization.Cleanup();
                }
                else
                {
                    // If config is null, clean up everything to be safe
                    ParticleOptimization.Cleanup();
                    AmbientSoundOptimization.Cleanup();
                    GuiManagerOptimization.Cleanup();
                    FlySoundOptimization.Cleanup();
                    FrameRateOptimization.Cleanup();
                    HandbookOptimization.Cleanup();
                    EntityInterpolationOptimization.Cleanup();
                    RepulseAgentsOptimization.Cleanup();
                }

                // Cleanup recipe lookup optimization (no resources currently)
                RecipeLookupCacheOptimization.Cleanup();
                WeatherWindOptimization.Cleanup();
                ShadowOptimization.Cleanup();
                EntityShadowCullingOptimization.Cleanup();
                OcclusionCullingOptimization.Cleanup();
                ConfigLibIntegration.Cleanup();
                TranslationServicePatch.Cleanup();

                ProfilingHelper.Cleanup();
            }
            catch (Exception ex)
            {
                capi?.Logger?.Warning($"[OptiTime] Error during dispose: {ex.Message}");
            }
            finally
            {
                harmony = null;
                config = null;
                Config = null;
                staticCapi = null;
                capi = null;
            }
        }

        private void DetectAndHandleConflicts(ICoreClientAPI api)
        {
            // Check for Ancestral Bliss Shaders
            if (ancestralBlissDetected)
            {
                config.MarkAsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations), "Ancestral Bliss compatibility mode");
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
                
                config.EntityAnimationOptimizations = false;
                config.MarkAsConflictDisabled(nameof(OptiTimeConfig.EntityAnimationOptimizations), combatOverhaulDetected ? "Combat Overhaul compatibility mode" : "Overhaullib compatibility mode");
                config.Save(api);
                
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

                config.ParticleViewDistanceScalingEnabled = false;
                config.MarkAsConflictDisabled(nameof(OptiTimeConfig.ParticleViewDistanceScalingEnabled),
                    modName + " compatibility mode");
                config.Save(api);
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

                config.HandbookOptimizations = false;
                config.MarkAsConflictDisabled(nameof(OptiTimeConfig.HandbookOptimizations), "Electrical Progressive compatibility mode");
                config.Save(api);

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
                api.Logger.Warning("[OptiTime] Disabling shader file replacements (chunkliquid, chunkshadowmap)");
                api.Logger.Warning("[OptiTime] VBAO and blur optimizations remain independent");
                api.Logger.Warning("[OptiTime] All code optimizations remain active");
                api.Logger.Warning("═══════════════════════════════════════════════════════");

                config.ShaderOptimizations = false;
                config.MarkAsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations), "CoriaenderShaders compatibility mode");
                config.Save(api);

                if (!config.SuppressCompatibilityMessages)
                {
                    api.ShowChatMessage(Lang.Get("optitime:compat-coriaender-title"));
                    api.ShowChatMessage(Lang.Get("optitime:compat-coriaender-desc"));
                }
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
                    if (!module.Enabled)
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
