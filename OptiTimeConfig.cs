using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OptiTime
{
    public class OptiTimeConfig
    {
        public bool DynamicLightOptimizations { get; set; } = true;
        public bool EntityAnimationOptimizations { get; set; } = true;
        public bool ParticleOptimizations { get; set; } = true;
        public bool ParticleViewDistanceScalingEnabled { get; set; } = true;
        public bool OcclusionCullingOptimizations { get; set; } = true;
        public bool ChunkTesselationOptimizations { get; set; } = true;
        public bool AmbientSoundOptimizations { get; set; } = true;
        public bool FlySoundOptimizations { get; set; } = true;
        public bool BackgroundFpsLimiterEnabled { get; set; } = true;
        public int BackgroundMaxFps { get; set; } = 20;
        public bool PreciseFramePacingEnabled { get; set; } = true;
        public bool GuiManagerOptimizations { get; set; } = true;
        public bool GuiManagerNoLinqEnabled { get; set; } = true;
        public bool GuiManagerInputNoLinqEnabled { get; set; } = false;
        public bool GuiManagerMouseMoveCoalescingEnabled { get; set; } = true;
        public int GuiManagerMouseMoveCoalesceIntervalMs { get; set; } = 8;
        public bool HandbookOptimizations { get; set; } = true;
        public bool RecipeLookupOptimizations { get; set; } = true;
        public bool EnableProfiling { get; set; } = false;
        public bool ShaderOptimizations { get; set; } = true;
        public bool WeatherWindOptimizations { get; set; } = true;
        public bool TickingBlocksOptimizations { get; set; } = true;
        public bool ShadowFarVegetationCullEnabled { get; set; } = true;

        private Dictionary<string, Action<bool>> optimizationSetters;
        private Dictionary<string, Func<bool>> optimizationGetters;
        private HashSet<string> conflictDisabledOptimizations = new HashSet<string>();
        private Dictionary<string, string> conflictDisableReasons = new Dictionary<string, string>();

        public OptiTimeConfig()
        {
            InitializeAccessors();
        }

        private void InitializeAccessors()
        {
            optimizationSetters = new Dictionary<string, Action<bool>>
            {
                [nameof(DynamicLightOptimizations)] = v => DynamicLightOptimizations = v,
                [nameof(EntityAnimationOptimizations)] = v => EntityAnimationOptimizations = v,
                [nameof(ParticleOptimizations)] = v => ParticleOptimizations = v,
                [nameof(OcclusionCullingOptimizations)] = v => OcclusionCullingOptimizations = v,
                [nameof(ChunkTesselationOptimizations)] = v => ChunkTesselationOptimizations = v,
                [nameof(AmbientSoundOptimizations)] = v => AmbientSoundOptimizations = v,
                [nameof(FlySoundOptimizations)] = v => FlySoundOptimizations = v,
                [nameof(BackgroundFpsLimiterEnabled)] = v => BackgroundFpsLimiterEnabled = v,
                [nameof(PreciseFramePacingEnabled)] = v => PreciseFramePacingEnabled = v,
                [nameof(GuiManagerOptimizations)] = v => GuiManagerOptimizations = v,
                [nameof(HandbookOptimizations)] = v => HandbookOptimizations = v,
                [nameof(RecipeLookupOptimizations)] = v => RecipeLookupOptimizations = v,
                [nameof(ShaderOptimizations)] = v => ShaderOptimizations = v,
                [nameof(WeatherWindOptimizations)] = v => WeatherWindOptimizations = v,
                [nameof(TickingBlocksOptimizations)] = v => TickingBlocksOptimizations = v,
                [nameof(ShadowFarVegetationCullEnabled)] = v => ShadowFarVegetationCullEnabled = v,
                [nameof(EnableProfiling)] = v => EnableProfiling = v
            };

            optimizationGetters = new Dictionary<string, Func<bool>>
            {
                [nameof(DynamicLightOptimizations)] = () => DynamicLightOptimizations,
                [nameof(EntityAnimationOptimizations)] = () => EntityAnimationOptimizations,
                [nameof(ParticleOptimizations)] = () => ParticleOptimizations,
                [nameof(OcclusionCullingOptimizations)] = () => OcclusionCullingOptimizations,
                [nameof(ChunkTesselationOptimizations)] = () => ChunkTesselationOptimizations,
                [nameof(AmbientSoundOptimizations)] = () => AmbientSoundOptimizations,
                [nameof(FlySoundOptimizations)] = () => FlySoundOptimizations,
                [nameof(BackgroundFpsLimiterEnabled)] = () => BackgroundFpsLimiterEnabled,
                [nameof(PreciseFramePacingEnabled)] = () => PreciseFramePacingEnabled,
                [nameof(GuiManagerOptimizations)] = () => GuiManagerOptimizations,
                [nameof(HandbookOptimizations)] = () => HandbookOptimizations,
                [nameof(RecipeLookupOptimizations)] = () => RecipeLookupOptimizations,
                [nameof(ShaderOptimizations)] = () => ShaderOptimizations,
                [nameof(WeatherWindOptimizations)] = () => WeatherWindOptimizations,
                [nameof(TickingBlocksOptimizations)] = () => TickingBlocksOptimizations,
                [nameof(ShadowFarVegetationCullEnabled)] = () => ShadowFarVegetationCullEnabled,
                [nameof(EnableProfiling)] = () => EnableProfiling
            };
        }

        public static OptiTimeConfig Load(ICoreClientAPI api)
        {
            try
            {
                string configPath = Path.Combine(api.DataBasePath, "ModConfig", "OptiTime.json");
                if (File.Exists(configPath))
                {
                    var config = api.LoadModConfig<OptiTimeConfig>("OptiTime.json");
                    if (config != null)
                    {
                        config.InitializeAccessors();
                        api.Logger.Notification("OptiTime: Configuration loaded successfully");
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                api.Logger.Error($"OptiTime: Error loading configuration: {ex.Message}");
            }

            var defaultConfig = new OptiTimeConfig();
            defaultConfig.Save(api);
            api.Logger.Notification("OptiTime: Created default configuration file");
            return defaultConfig;
        }

        public void Save(ICoreClientAPI api)
        {
            try
            {
                api.StoreModConfig(this, "OptiTime.json");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"OptiTime: Error saving configuration: {ex.Message}");
            }
        }

        public string GetOptimizationDescription(string optimizationName)
        {
            return optimizationName switch
            {
                nameof(DynamicLightOptimizations) => "Adaptive light culling by view distance (shorter radius at low VD)",
                nameof(EntityAnimationOptimizations) => "Distance-based animation LOD (skip frames for far entities)",
                nameof(ParticleOptimizations) => "Particle pool scaling at high view distances (optional)",
                nameof(OcclusionCullingOptimizations) => "Dynamic occlusion-culling gate (view-distance scaled, 70-200)",
                nameof(ChunkTesselationOptimizations) => "Adaptive chunk tesselation multiplier based on queue size",
                nameof(AmbientSoundOptimizations) => "Movement-based ambient sound position updates (200ms fallback)",
                nameof(FlySoundOptimizations) => "Fly sound volume updates only on >1% change when playing",
                nameof(BackgroundFpsLimiterEnabled) => $"Lower FPS when unfocused (BackgroundMaxFps={BackgroundMaxFps})",
                nameof(PreciseFramePacingEnabled) => "Hybrid sleep/yield/spin frame pacing when VSync is off",
                nameof(GuiManagerOptimizations) => "GUI render/input LINQ removal (input patches optional)",
                nameof(HandbookOptimizations) => "Cached handbook relationships after async indexing",
                nameof(RecipeLookupOptimizations) => "Early dimension rejection for impossible crafting matches",
                nameof(ShaderOptimizations) => "OptiTime shader replacements for blur, clouds, godrays, and SSAO",
                nameof(WeatherWindOptimizations) => "Throttle wind speed lookups from every frame to every 4th frame",
                nameof(TickingBlocksOptimizations) => "Reuse BlockPos in particle tick loop to reduce GC pressure",
                nameof(ShadowFarVegetationCullEnabled) => "Skip vegetation in far shadow cascade (removes leaf/grass shadows at distance)",
                _ => "Unknown optimization"
            };
        }

        public void SetOptimization(string optimizationName, bool value)
        {
            if (optimizationSetters == null)
                InitializeAccessors();
            
            if (optimizationSetters.TryGetValue(optimizationName, out var setter))
            {
                setter(value);
            }
        }

        public bool GetOptimizationValue(string optimizationName)
        {
            if (optimizationGetters == null)
                InitializeAccessors();
            
            if (optimizationGetters.TryGetValue(optimizationName, out var getter))
            {
                return getter();
            }
            return false;
        }

        public void MarkAsConflictDisabled(string optimizationName, string reason = null)
        {
            conflictDisabledOptimizations.Add(optimizationName);
            conflictDisableReasons[optimizationName] = string.IsNullOrWhiteSpace(reason) ? "Conflict detected" : reason;
        }

        public bool IsConflictDisabled(string optimizationName)
        {
            return conflictDisabledOptimizations.Contains(optimizationName);
        }

        public string GetConflictReason(string optimizationName)
        {
            if (conflictDisableReasons.TryGetValue(optimizationName, out string reason))
            {
                return reason;
            }

            return null;
        }

        public void PrintStatus(ICoreClientAPI api)
        {
            api.ShowChatMessage("=== OptiTime Status ===");
            PrintOptStatus(api, "shaders", ShaderOptimizations, nameof(ShaderOptimizations));
            PrintOptStatus(api, "dynlights", DynamicLightOptimizations, nameof(DynamicLightOptimizations));
            PrintOptStatus(api, "entityanim", EntityAnimationOptimizations, nameof(EntityAnimationOptimizations));
            PrintOptStatus(api, "particles", ParticleOptimizations, nameof(ParticleOptimizations));
            PrintOptStatus(api, "occlusion", OcclusionCullingOptimizations, nameof(OcclusionCullingOptimizations));
            PrintOptStatus(api, "chunktess", ChunkTesselationOptimizations, nameof(ChunkTesselationOptimizations));
            PrintOptStatus(api, "ambientsound", AmbientSoundOptimizations, nameof(AmbientSoundOptimizations));
            PrintOptStatus(api, "flysound", FlySoundOptimizations, nameof(FlySoundOptimizations));
            PrintOptStatus(api, "bgfps", BackgroundFpsLimiterEnabled, nameof(BackgroundFpsLimiterEnabled));
            PrintOptStatus(api, "framepace", PreciseFramePacingEnabled, nameof(PreciseFramePacingEnabled));
            PrintOptStatus(api, "guimgr", GuiManagerOptimizations, nameof(GuiManagerOptimizations));
            PrintOptStatus(api, "handbook", HandbookOptimizations, nameof(HandbookOptimizations));
            PrintOptStatus(api, "recipe", RecipeLookupOptimizations, nameof(RecipeLookupOptimizations));
            PrintOptStatus(api, "weatherwind", WeatherWindOptimizations, nameof(WeatherWindOptimizations));
            PrintOptStatus(api, "tickingblocks", TickingBlocksOptimizations, nameof(TickingBlocksOptimizations));
            PrintOptStatus(api, "shadowveg", ShadowFarVegetationCullEnabled, nameof(ShadowFarVegetationCullEnabled));
            api.ShowChatMessage($"profiling: {(EnableProfiling ? "ON" : "OFF")}");
        }
        
        private void PrintOptStatus(ICoreClientAPI api, string name, bool enabled, string descKey)
        {
            string status = GetOptimizationStatusText(enabled, descKey);
            string desc = GetOptimizationDescription(descKey);
            api.ShowChatMessage($"  {name}: {status} - {desc}");
        }

        private string GetOptimizationStatusText(bool enabled, string optimizationName)
        {
            string status = enabled ? "ON" : "OFF";

            if (IsConflictDisabled(optimizationName))
            {
                string reason = GetConflictReason(optimizationName);
                status = string.IsNullOrWhiteSpace(reason) ? "OFF (Conflict)" : $"OFF ({reason})";
            }

            return status;
        }

        public void PrintCategoryInfo(ICoreClientAPI api, string category)
        {
            string catLower = category.ToLower();

            var mapping = new Dictionary<string, (bool enabled, string name, string descKey)>
            {
                ["shaders"] = (ShaderOptimizations, "Shaders", nameof(ShaderOptimizations)),
                ["dynlights"] = (DynamicLightOptimizations, "Dynamic Lights", nameof(DynamicLightOptimizations)),
                ["entityanim"] = (EntityAnimationOptimizations, "Entity Animations", nameof(EntityAnimationOptimizations)),
                ["particles"] = (ParticleOptimizations, "Particles", nameof(ParticleOptimizations)),
                ["occlusion"] = (OcclusionCullingOptimizations, "Occlusion Culling", nameof(OcclusionCullingOptimizations)),
                ["chunktess"] = (ChunkTesselationOptimizations, "Chunk Tesselation", nameof(ChunkTesselationOptimizations)),
                ["ambientsound"] = (AmbientSoundOptimizations, "Ambient Sounds", nameof(AmbientSoundOptimizations)),
                ["flysound"] = (FlySoundOptimizations, "Fly Sound", nameof(FlySoundOptimizations)),
                ["bgfps"] = (BackgroundFpsLimiterEnabled, "Background FPS Limiter", nameof(BackgroundFpsLimiterEnabled)),
                ["framepace"] = (PreciseFramePacingEnabled, "Precise Frame Pacing", nameof(PreciseFramePacingEnabled)),
                ["guimgr"] = (GuiManagerOptimizations, "GUI Manager", nameof(GuiManagerOptimizations)),
                ["handbook"] = (HandbookOptimizations, "Handbook", nameof(HandbookOptimizations)),
                ["recipe"] = (RecipeLookupOptimizations, "Recipe Lookup", nameof(RecipeLookupOptimizations)),
                ["weatherwind"] = (WeatherWindOptimizations, "Weather Wind", nameof(WeatherWindOptimizations)),
                ["tickingblocks"] = (TickingBlocksOptimizations, "Ticking Blocks", nameof(TickingBlocksOptimizations)),
                ["shadowveg"] = (ShadowFarVegetationCullEnabled, "Shadow Far Vegetation Cull", nameof(ShadowFarVegetationCullEnabled))
            };

            if (mapping.TryGetValue(catLower, out var info))
            {
                string status = GetOptimizationStatusText(info.enabled, info.descKey);
                string description = GetOptimizationDescription(info.descKey);

                api.ShowChatMessage($"=== {info.name} ===");
                api.ShowChatMessage($"Status: {status}");
                api.ShowChatMessage($"Description: {description}");
            }
            else
            {
                api.ShowChatMessage($"Unknown: {category}");
                api.ShowChatMessage("Available: shaders, dynlights, entityanim, particles, occlusion, chunktess, ambientsound, flysound, bgfps, framepace, guimgr, handbook, recipe");
            }
        }
    }
}
