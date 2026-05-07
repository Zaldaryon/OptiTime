using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace OptiTime
{
    public class ConfigLibIntegration
    {
        private const string ConfigLibSettingChangedEvent = "configlib:{0}:setting-changed";
        private static ICoreClientAPI api;
        private static bool initialized = false;

        public static void TryInitialize(ICoreClientAPI capi)
        {
            if (initialized) return;
            initialized = true;

            api = capi;
            
            capi.Logger.Notification("[OptiTime] ConfigLibIntegration.TryInitialize called");

            // Check if ConfigLib is loaded
            if (!capi.ModLoader.IsModEnabled("configlib"))
            {
                capi.Logger.Debug("[OptiTime] ConfigLib not found - GUI disabled (this is normal)");
                return;
            }
            
            capi.Logger.Notification("[OptiTime] ConfigLib detected, registering event listener");

            // Register event listener for ConfigLib changes
            var eventName = string.Format(ConfigLibSettingChangedEvent, "optitime");
            capi.Event.RegisterEventBusListener(OnConfigLibSettingChanged, 0.5, eventName);

            capi.Logger.Notification("[OptiTime] ConfigLib integration enabled - use .configlib command to open GUI");
        }

        public static void Cleanup()
        {
            if (api != null && initialized)
            {
                try
                {
                    api.Event.UnregisterEventBusListener(OnConfigLibSettingChanged);
                }
                catch { }
            }

            api = null;
            initialized = false;
        }

        private static void OnConfigLibSettingChanged(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (data is not ITreeAttribute tree)
            {
                return;
            }

            var code = tree.GetString("setting");
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            // Get the new value
            var value = tree.GetBool("value");

            // Update OptiTime config
            var config = OptiTimeMod.Config;
            if (config == null) return;

            bool changed = false;

            switch (code)
            {
                case "ShaderOptimizations":
                    config.ShaderOptimizations = value;
                    changed = true;
                    break;
                case "ParticleViewDistanceScalingEnabled":
                    config.ParticleViewDistanceScalingEnabled = value;
                    changed = true;
                    break;
                case "GuiManagerNoLinqEnabled":
                    config.GuiManagerNoLinqEnabled = value;
                    changed = true;
                    break;
                case "GuiManagerInputNoLinqEnabled":
                    config.GuiManagerInputNoLinqEnabled = value;
                    changed = true;
                    break;
                case "GuiManagerMouseMoveCoalescingEnabled":
                    config.GuiManagerMouseMoveCoalescingEnabled = value;
                    changed = true;
                    break;
                case "AmbientSoundOptimizations":
                    config.AmbientSoundOptimizations = value;
                    changed = true;
                    break;
                case "DynamicLightOptimizations":
                    config.DynamicLightOptimizations = value;
                    changed = true;
                    break;
                case "EntityAnimationOptimizations":
                    config.EntityAnimationOptimizations = value;
                    changed = true;
                    break;
                case "FlySoundOptimizations":
                    config.FlySoundOptimizations = value;
                    changed = true;
                    break;
                case "BackgroundFpsLimiterEnabled":
                    config.BackgroundFpsLimiterEnabled = value;
                    changed = true;
                    break;
                case "PreciseFramePacingEnabled":
                    config.PreciseFramePacingEnabled = value;
                    changed = true;
                    break;
                case "GuiManagerOptimizations":
                    config.GuiManagerOptimizations = value;
                    changed = true;
                    break;
                case "HandbookOptimizations":
                    config.HandbookOptimizations = value;
                    changed = true;
                    break;
                case "RecipeLookupOptimizations":
                    config.RecipeLookupOptimizations = value;
                    changed = true;
                    break;
                case "OcclusionCullingOptimizations":
                    config.OcclusionCullingOptimizations = value;
                    changed = true;
                    break;
                case "ParticleOptimizations":
                    config.ParticleOptimizations = value;
                    changed = true;
                    break;
                case "WeatherWindOptimizations":
                    config.WeatherWindOptimizations = value;
                    changed = true;
                    break;
                case "TickingBlocksOptimizations":
                    config.TickingBlocksOptimizations = value;
                    changed = true;
                    break;
                case "ShadowFarVegetationCullEnabled":
                    config.ShadowFarVegetationCullEnabled = value;
                    changed = true;
                    break;
                case "EntityInterpolationOptimizations":
                    config.EntityInterpolationOptimizations = value;
                    changed = true;
                    break;
                case "RepulseAgentsOptimizations":
                    config.RepulseAgentsOptimizations = value;
                    changed = true;
                    break;
                case "BackgroundMaxFps":
                    config.BackgroundMaxFps = tree.GetInt("value", 20);
                    changed = true;
                    break;
            }

            if (changed)
            {
                OptiTimeMod.SaveConfig();
                string status = value ? Lang.Get("optitime:status-on") : Lang.Get("optitime:status-off");
                api?.ShowChatMessage(Lang.Get("optitime:configlib-setting-changed", code, status));
            }
        }
    }
}
