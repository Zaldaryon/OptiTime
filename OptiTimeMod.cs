using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using HarmonyLib;
using System;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;

namespace OptiTime
{
    public class OptiTimeMod : ModSystem
    {
        private ICoreClientAPI capi;
        private OptiTimeConfig config;
        private Harmony harmony;
        private bool ancestralBlissDetected = false;
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
            ParticleOptimization.ConfigureScaling(Config.ParticleViewDistanceScalingEnabled);
            GuiManagerOptimization.Configure(Config);
            ProfilingHelper.SetEnabled(Config.EnableProfiling);
        }
        
        private static readonly string[] OptiTimeShaderPaths = new[]
        {
            "shaders/blur.fsh",
            "shaders/cloudvolumetric.fsh",
            "shaders/godrays.fsh",
            "shaders/godrays.vsh",
            "shaders/ssao.fsh"
        };
        private static readonly Dictionary<string, string> OptimizationMap = new Dictionary<string, string>
        {
            ["shaders"] = "ShaderOptimizations",
            ["dynlights"] = "DynamicLightOptimizations",
            ["entityanim"] = "EntityAnimationOptimizations",
            ["particles"] = "ParticleOptimizations",
            ["occlusion"] = "OcclusionCullingOptimizations",
            ["chunktess"] = "ChunkTesselationOptimizations",
            ["ambientsound"] = "AmbientSoundOptimizations",
            ["flysound"] = "FlySoundOptimizations",
            ["bgfps"] = "BackgroundFpsLimiterEnabled",
            ["framepace"] = "PreciseFramePacingEnabled",
            ["guimgr"] = "GuiManagerOptimizations",
            ["handbook"] = "HandbookOptimizations",
            ["recipe"] = "RecipeLookupOptimizations"
        };

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            
            ancestralBlissDetected = api.ModLoader.IsModEnabled("ancestralblissshaders") || 
                                     api.ModLoader.IsModEnabled("volumetricshadingrefreshed");
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);
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
                TryDisableOptiTimeShaders(clientApi, reason);
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
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
                        api.Logger.Notification("[OptiTime] Precise frame pacing optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load precise frame pacing optimization: " + ex.Message);
                    config.PreciseFramePacingEnabled = false;
                    ApplyRuntimeConfig();
                }
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
                    var constructor = AccessTools.Constructor(
                        AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemRenderParticles"),
                        new[] { AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain") });
                    harmony.Patch(constructor,
                        postfix: new HarmonyMethod(typeof(ParticleOptimization), "AdjustParticlePools"));
                    api.Logger.Notification("[OptiTime] Particle optimization loaded");
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
            

            if (config.ChunkTesselationOptimizations)
            {
                try
                {
                    // Set logger for diagnostics
                    ChunkTesselationOptimization.SetLogger((msg) => api.Logger.Notification(msg));

                    var onBeforeFrame = AccessTools.Method(
                        "Vintagestory.Client.NoObf.ChunkTesselatorManager:OnBeforeFrame",
                        new Type[] { typeof(float) }
                    );
                    if (onBeforeFrame == null)
                    {
                        throw new InvalidOperationException("ChunkTesselatorManager.OnBeforeFrame not found");
                    }

                    var target = onBeforeFrame;
                    if (!DisableIfConflictingPatches(api, nameof(OptiTimeConfig.ChunkTesselationOptimizations), "Chunk tesselation optimization", target))
                    {
                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(typeof(ChunkTesselationOptimization), "OnBeforeFrame_Prefix"));

                        api.Logger.Notification("[OptiTime] Chunk tesselation optimization loaded");
                    }
                }
                catch (Exception ex)
                {
                    api.Logger.Error("[OptiTime] Failed to load chunk tesselation optimization: " + ex.Message);
                    config.ChunkTesselationOptimizations = false;
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

                var instProp = AccessTools.Property(clientSettings, "Inst");
                if (instProp == null) return;

                var inst = instProp.GetValue(null);
                if (inst == null) return;

                var viewDistProp = AccessTools.Property(clientSettings, "ViewDistance");
                if (viewDistProp != null)
                {
                    int initialViewDistance = (int)viewDistProp.GetValue(null);
                    if (config.DynamicLightOptimizations)
                        DynamicLightOptimization.UpdateViewDistance(initialViewDistance);
                    if (config.ParticleOptimizations)
                        ParticleOptimization.UpdateViewDistance(initialViewDistance);
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
                       HasOtherOwner(patchInfo.Postfixes) ||
                       HasOtherOwner(patchInfo.Transpilers) ||
                       HasOtherOwner(patchInfo.Finalizers);
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
            if (config.ChunkTesselationOptimizations) count++;
            if (config.AmbientSoundOptimizations) count++;
            if (config.FlySoundOptimizations) count++;
            if (config.BackgroundFpsLimiterEnabled) count++;
            if (config.PreciseFramePacingEnabled) count++;
            if (config.GuiManagerOptimizations) count++;
            if (config.HandbookOptimizations) count++;
            if (config.RecipeLookupOptimizations) count++;
            if (config.ShaderOptimizations && !config.IsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations))) count++;

            if (count > 0)
                api.Logger.Notification($"[OptiTime] {count} optimization(s) enabled");
            else
                api.Logger.Notification("[OptiTime] No optimizations enabled. Use .optitime");
        }

        private void TryDisableOptiTimeShaders(ICoreClientAPI api, string reason)
        {
            try
            {
                var origins = api.Assets?.Origins;
                if (origins == null || origins.Count == 0)
                    return;

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
                api.Logger.Notification($"[OptiTime] Shader assets disabled ({reason}).");
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
                var assets = origin.GetAssets(AssetCategory.shaders, false);
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
            api.RegisterCommand("optitime", "OptiTime optimization controls", ".optitime [status|<opt> on/off]", OnOptiTimeCommand);
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
                    capi.ShowChatMessage(Lang.Get("optitime:cmd-restart-required"));
                }
                else if (action.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    config.SetOptimization(optName, false);
                    config.Save(capi);
                    ApplyRuntimeConfig();
                    capi.ShowChatMessage(Lang.Get("optitime:cmd-disabled", command));
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
                            var instProp = AccessTools.Property(clientSettings, "Inst");
                            var inst = instProp?.GetValue(null);
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
                    if (config.ChunkTesselationOptimizations)
                        ChunkTesselationOptimization.Cleanup();
                    if (config.AmbientSoundOptimizations)
                        AmbientSoundOptimization.Cleanup();
                    if (config.GuiManagerOptimizations)
                        GuiManagerOptimization.Cleanup();
                    if (config.FlySoundOptimizations)
                        FlySoundOptimization.Cleanup();
                    FrameRateOptimization.Cleanup();
                    if (config.HandbookOptimizations)
                        HandbookOptimization.Cleanup();
                }
                else
                {
                    // If config is null, clean up everything to be safe
                    ParticleOptimization.Cleanup();
                    ChunkTesselationOptimization.Cleanup();
                    AmbientSoundOptimization.Cleanup();
                    GuiManagerOptimization.Cleanup();
                    FlySoundOptimization.Cleanup();
                    FrameRateOptimization.Cleanup();
                    HandbookOptimization.Cleanup();
                }

                // Cleanup recipe lookup optimization (no resources currently)
                RecipeLookupCacheOptimization.Cleanup();

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
                
                api.ShowChatMessage(Lang.Get("optitime:compat-ancestral-title"));
                api.ShowChatMessage(Lang.Get("optitime:compat-ancestral-desc"));
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

                api.ShowChatMessage(Lang.Get("optitime:compat-electricalprogressive-title"));
                api.ShowChatMessage(Lang.Get("optitime:compat-electricalprogressive-desc"));
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
    }
}
