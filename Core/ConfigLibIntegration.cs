using System;
using System.Globalization;
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

            var config = OptiTimeMod.Config;
            if (config == null) return;

            if (!TryApplySetting(config, code, tree, out string feedback))
            {
                return;
            }

            OptiTimeMod.SaveConfig();
            feedback ??= tree.GetBool("value") ? Lang.Get("optitime:status-on") : Lang.Get("optitime:status-off");
            api?.ShowChatMessage(Lang.Get("optitime:configlib-setting-changed", code, feedback));
        }

        internal static bool TryApplySetting(OptiTimeConfig config, string code, ITreeAttribute tree, out string feedback)
        {
            feedback = null;
            if (config == null || tree == null || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            bool value = tree.GetBool("value");
            bool changed = true;

            switch (code)
            {
                case "ShaderOptimizations":
                    config.ShaderOptimizations = value;
                    break;
                case "BlurOptimizationEnabled":
                    config.BlurOptimizationEnabled = value;
                    break;
                case "ParticleViewDistanceScalingEnabled":
                    config.ParticleViewDistanceScalingEnabled = value;
                    break;
                case "GuiManagerNoLinqEnabled":
                    config.GuiManagerNoLinqEnabled = value;
                    break;
                case "GuiManagerInputNoLinqEnabled":
                    config.GuiManagerInputNoLinqEnabled = value;
                    break;
                case "GuiManagerMouseMoveCoalescingEnabled":
                    config.GuiManagerMouseMoveCoalescingEnabled = value;
                    break;
                case "AmbientSoundOptimizations":
                    config.AmbientSoundOptimizations = value;
                    break;
                case "DynamicLightOptimizations":
                    config.DynamicLightOptimizations = value;
                    break;
                case "EntityAnimationOptimizations":
                    config.EntityAnimationOptimizations = value;
                    break;
                case "FlySoundOptimizations":
                    config.FlySoundOptimizations = value;
                    break;
                case "BackgroundFpsLimiterEnabled":
                    config.BackgroundFpsLimiterEnabled = value;
                    break;
                case "PreciseFramePacingEnabled":
                    config.PreciseFramePacingEnabled = value;
                    break;
                case "GuiManagerOptimizations":
                    config.GuiManagerOptimizations = value;
                    break;
                case "HandbookOptimizations":
                    config.HandbookOptimizations = value;
                    break;
                case "RecipeLookupOptimizations":
                    config.RecipeLookupOptimizations = value;
                    break;
                case "OcclusionCullingOptimizations":
                    config.OcclusionCullingOptimizations = value;
                    break;
                case "ParticleOptimizations":
                    config.ParticleOptimizations = value;
                    break;
                case "WeatherWindOptimizations":
                    config.WeatherWindOptimizations = value;
                    break;
                case "TickingBlocksOptimizations":
                    config.TickingBlocksOptimizations = value;
                    break;
                case "ShadowFarVegetationCullEnabled":
                    config.ShadowFarVegetationCullEnabled = value;
                    break;
                case "EntityShadowDistanceCullEnabled":
                    config.EntityShadowDistanceCullEnabled = value;
                    break;
                case "EntityInterpolationOptimizations":
                    config.EntityInterpolationOptimizations = value;
                    break;
                case "RepulseAgentsOptimizations":
                    config.RepulseAgentsOptimizations = value;
                    break;
                case "BackgroundMaxFps":
                    config.BackgroundMaxFps = tree.GetInt("value", config.BackgroundMaxFps);
                    feedback = config.BackgroundMaxFps.ToString(CultureInfo.InvariantCulture);
                    break;
                case "PreciseFramePacingUndershootPercent":
                    float undershootPercent = tree.GetFloat("value", (float)config.PreciseFramePacingUndershootPercent);
                    feedback = undershootPercent.ToString(CultureInfo.InvariantCulture);
                    config.PreciseFramePacingUndershootPercent = double.Parse(feedback, CultureInfo.InvariantCulture);
                    break;
                case "PreciseFramePacingYieldThresholdMs":
                    float yieldThresholdMs = tree.GetFloat("value", (float)config.PreciseFramePacingYieldThresholdMs);
                    feedback = yieldThresholdMs.ToString(CultureInfo.InvariantCulture);
                    config.PreciseFramePacingYieldThresholdMs = double.Parse(feedback, CultureInfo.InvariantCulture);
                    break;
                case "PreciseFramePacingSpinIterations":
                    config.PreciseFramePacingSpinIterations = tree.GetInt("value", config.PreciseFramePacingSpinIterations);
                    feedback = config.PreciseFramePacingSpinIterations.ToString(CultureInfo.InvariantCulture);
                    break;
                case "SuppressCompatibilityMessages":
                    config.SuppressCompatibilityMessages = value;
                    break;
                default:
                    changed = false;
                    break;
            }

            if (!changed)
            {
                return false;
            }

            return true;
        }
    }
}
