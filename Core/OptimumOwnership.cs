using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace OptiTime
{
    /// <summary>
    /// Decides which OptiTime optimizations can run next to Optimum without replacing anything Optimum
    /// already changed. Optimum rewrites the game assemblies ahead of time, so a Harmony prefix or
    /// transpiler written against the vanilla body would silently override Optimum's version of it.
    /// A method is only patched when its body is still the vanilla one: its canonical IL signature
    /// has to match the one recorded for the running game version in <see cref="OptimumVanillaSignatures"/>.
    /// Unknown methods and unknown game versions fail closed, so the optimization yields.
    /// </summary>
    internal static class OptimumOwnership
    {
        internal sealed class Feature
        {
            public Feature(string optimizationName, string[] targets, string optimumSetting = null)
            {
                OptimizationName = optimizationName;
                Targets = targets;
                OptimumSetting = optimumSetting;
            }

            /// <summary>Name of the OptiTimeConfig flag that switches the feature on.</summary>
            public string OptimizationName { get; }

            /// <summary>Patched methods as "Namespace.Type:Method". Every overload must still be vanilla.</summary>
            public string[] Targets { get; }

            /// <summary>
            /// ClientSettings member Optimum ships for the same feature. When it exists, Optimum owns the
            /// setting and OptiTime must not run a second implementation next to it.
            /// </summary>
            public string OptimumSetting { get; }
        }

        private const string ClientSettingsTypeName = "Vintagestory.Client.NoObf.ClientSettings";

        /// <summary>Methods OptiTime patches whatever the configuration says.</summary>
        internal static readonly string[] AlwaysOnTargets =
        {
            "Vintagestory.API.Client.FrustumCulling:InFrustumAndRange",
            "Vintagestory.API.Common.AnimatableRenderer:Dispose",
            "Vintagestory.API.Common.AnimatableRenderer:OnRenderFrame",
            "Vintagestory.API.Common.AnimatableRenderer:mainThreadInit",
            "Vintagestory.API.Common.AnimationUtil:Dispose",
            "Vintagestory.API.Common.AnimationUtil:OnRenderFrame",
            "Vintagestory.API.Common.AnimationUtil:StartAnimation",
            "Vintagestory.API.Config.TranslationService:HasTranslation",
            "Vintagestory.Client.ScreenManager:OnNewFrame",
            "Vintagestory.Client.NoObf.ChunkTesselatorManager:OnBeforeFrame",
            "Vintagestory.Client.NoObf.InventoryItemRenderer:GetItemStackRenderInfo",
            "Vintagestory.Client.NoObf.InventoryItemRenderer:RenderItemstackToGui",
            "Vintagestory.Client.NoObf.SystemRenderSunMoon:Dispose",
            "Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3D",
            "Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3DPost",
            "Vintagestory.Common.CreativeTab:CreateSearchCache",
            "Vintagestory.GameContent.ModSystemSurvivalHandbook:onCreatePagesAsync"
        };

        internal static readonly Feature[] Features =
        {
            new Feature(nameof(OptiTimeConfig.BackgroundFpsLimiterEnabled), new[] { "Vintagestory.Client.ScreenManager:OnNewFrame" }, "OptimumBackgroundFpsLimit"),
            new Feature(nameof(OptiTimeConfig.PreciseFramePacingEnabled), new[]
            {
                "Vintagestory.Client.NoObf.ClientPlatformWindows:window_RenderFrame",
                "Vintagestory.API.Common.FrameProfilerUtil:Leave"
            }, "OptimumPreciseFramePacing"),
            new Feature(nameof(OptiTimeConfig.DynamicLightOptimizations), new[] { "Vintagestory.Client.NoObf.SystemRenderPlayerEffects:onBeforeRender" }, "OptimumDynamicLightCache"),
            new Feature(nameof(OptiTimeConfig.EntityAnimationOptimizations), new[]
            {
                "Vintagestory.Client.NoObf.SystemRenderEntities:OnBeforeRender",
                "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderOpaque3D",
                "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderAfterOIT",
                "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrame2D",
                "Vintagestory.Client.NoObf.HudEntityNameTags:OnRenderGUI",
                "Vintagestory.Client.NoObf.ClientMain:RemoveEntityRenderer"
            }),
            new Feature(nameof(OptiTimeConfig.ParticleOptimizations), new[]
            {
                "Vintagestory.Client.NoObf.ParticlePoolQuads:SpawnParticles",
                "Vintagestory.Client.NoObf.SystemRenderParticles:OnRenderFrame3D"
            }, "OptimumParticleDistanceGate"),
            new Feature(nameof(OptiTimeConfig.OcclusionCullingOptimizations), new[] { "Vintagestory.Client.NoObf.ChunkCuller:CullInvisibleChunks" }),
            new Feature(nameof(OptiTimeConfig.AmbientSoundOptimizations), new[] { "Vintagestory.Client.NoObf.SystemPlayerSounds:OnGameTick" }),
            new Feature(nameof(OptiTimeConfig.FlySoundOptimizations), new[] { "Vintagestory.Client.NoObf.SystemPlayerSounds:updateFlySound" }),
            new Feature(nameof(OptiTimeConfig.GuiManagerOptimizations), new[]
            {
                "Vintagestory.Client.NoObf.GuiManager:OnBeforeRenderFrame3D",
                "Vintagestory.Client.NoObf.GuiManager:OnRenderFrameGUI",
                "Vintagestory.API.Client.GuiComposer:PostRender"
            }),
            new Feature(nameof(OptiTimeConfig.GuiManagerInputNoLinqEnabled), new[]
            {
                "Vintagestory.Client.NoObf.GuiManager:OnFinalizeFrame",
                "Vintagestory.Client.NoObf.GuiManager:OnKeyDown",
                "Vintagestory.Client.NoObf.GuiManager:OnKeyUp",
                "Vintagestory.Client.NoObf.GuiManager:OnKeyPress",
                "Vintagestory.Client.NoObf.GuiManager:OnMouseDown",
                "Vintagestory.Client.NoObf.GuiManager:OnMouseUp",
                "Vintagestory.Client.NoObf.GuiManager:OnMouseMove",
                "Vintagestory.API.Client.GuiElementItemSlotGridBase:ComposeInteractiveElements"
            }),
            new Feature(nameof(OptiTimeConfig.HandbookOptimizations), new[] { "Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo:GetHandbookInfo" }),
            new Feature(nameof(OptiTimeConfig.RecipeLookupOptimizations), new[]
            {
                "Vintagestory.API.Common.GridRecipe:Matches",
                "Vintagestory.Common.InventoryCraftingGrid:FindMatchingRecipe"
            }),
            new Feature(nameof(OptiTimeConfig.WeatherWindOptimizations), new[] { "Vintagestory.GameContent.WeatherSystemClient:OnRenderFrame" }, "OptimumWeatherWindThrottle"),
            new Feature(nameof(OptiTimeConfig.TickingBlocksOptimizations), new[] { "Vintagestory.Client.NoObf.SystemClientTickingBlocks:onOffThreadParticleTick" }),
            new Feature(nameof(OptiTimeConfig.ShadowFarVegetationCullEnabled), new[] { "Vintagestory.Client.NoObf.ChunkRenderer:RenderShadow" }, "OptimumShadowFarVegetation"),
            new Feature(nameof(OptiTimeConfig.EntityShadowDistanceCullEnabled), new[] { "Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrameShadows" }, "OptimumEntityShadowCull"),
            new Feature(nameof(OptiTimeConfig.EntityInterpolationOptimizations), new[] { "EntityBehaviorInterpolatePosition:OnReceivedServerPos" }),
            new Feature(nameof(OptiTimeConfig.RepulseAgentsOptimizations), new[] { "Vintagestory.GameContent.EntityBehaviorRepulseAgents:OnGameTick" }, "OptimumRepulsionGate"),
            new Feature(nameof(OptiTimeConfig.EntityShapeOptimizations), new[] { "Vintagestory.GameContent.EntityShapeRenderer:TesselateShape" })
        };

        private static readonly Dictionary<string, string> SignatureCache = new Dictionary<string, string>();

        /// <summary>
        /// Returns why the feature has to yield to Optimum, or null when it can stay active.
        /// </summary>
        internal static string YieldReason(Feature feature, string gameVersion, Func<string, bool> optimumHasSetting)
        {
            try
            {
                if (feature.OptimumSetting != null && optimumHasSetting != null && optimumHasSetting(feature.OptimumSetting))
                    return "Optimum provides its own " + feature.OptimumSetting + " setting";

                foreach (string target in feature.Targets)
                {
                    string reason = TargetYieldReason(target, gameVersion);
                    if (reason != null)
                        return reason;
                }

                return null;
            }
            catch (Exception ex)
            {
                // Fail closed: a check that cannot be completed never lets the feature patch next to Optimum.
                return "the check failed (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>
        /// Returns why a target method cannot be patched next to Optimum, or null when every overload of it
        /// is still the vanilla body recorded for this game version.
        /// </summary>
        internal static string TargetYieldReason(string target, string gameVersion)
        {
            IReadOnlyDictionary<string, string> table = OptimumVanillaSignatures.ForGameVersion(gameVersion);
            if (table == null)
                return "no vanilla signatures are recorded for game version " + gameVersion;

            List<MethodBase> overloads;
            try
            {
                overloads = ResolveOverloads(target);
            }
            catch (Exception ex)
            {
                return target + " could not be inspected (" + ex.GetType().Name + ")";
            }

            if (overloads.Count == 0)
                return target + " was not found";

            foreach (MethodBase method in overloads)
            {
                string key = MethodKey(method);
                if (!table.TryGetValue(key, out string expected))
                    return key + " has no recorded vanilla signature";

                string actual;
                try
                {
                    actual = CachedSignature(key, method);
                }
                catch (Exception ex)
                {
                    return key + " could not be read (" + ex.GetType().Name + ")";
                }

                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    return key + " is not the vanilla body";
            }

            return null;
        }

        /// <summary>True when a method may be patched next to Optimum. Used to audit every applied patch.</summary>
        internal static bool IsVanillaBody(MethodBase method, string gameVersion)
        {
            IReadOnlyDictionary<string, string> table = OptimumVanillaSignatures.ForGameVersion(gameVersion);
            if (table == null || method == null)
                return false;

            string key = MethodKey(method);
            if (!table.TryGetValue(key, out string expected))
                return false;

            try
            {
                return string.Equals(expected, CachedSignature(key, method), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static List<MethodBase> ResolveOverloads(string target)
        {
            var result = new List<MethodBase>();
            int split = target.IndexOf(':');
            if (split <= 0)
                return result;

            Type type = AccessTools.TypeByName(target.Substring(0, split));
            if (type == null)
                return result;

            string name = target.Substring(split + 1);
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in type.GetMethods(all))
            {
                if (method.Name == name && !method.IsAbstract && method.GetMethodBody() != null)
                    result.Add(method);
            }

            return result;
        }

        internal static string MethodKey(MethodBase method)
        {
            string parameters = string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType)));
            return TypeName(method.DeclaringType) + ":" + method.Name + "(" + parameters + ")";
        }

        /// <summary>
        /// Type name without assembly versions, so the signatures do not change with the .NET runtime
        /// or the version of a referenced assembly.
        /// </summary>
        internal static string TypeName(Type type)
        {
            if (type == null)
                return "";
            if (type.IsByRef)
                return TypeName(type.GetElementType()) + "&";
            if (type.IsArray)
                return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                string definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
                int tick = definition.IndexOf('`');
                if (tick >= 0)
                    definition = definition.Substring(0, tick);
                return definition + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            }

            return type.FullName ?? type.Name;
        }

        private static string CachedSignature(string key, MethodBase method)
        {
            lock (SignatureCache)
            {
                if (!SignatureCache.TryGetValue(key, out string signature))
                {
                    signature = Signature(method);
                    SignatureCache[key] = signature;
                }

                return signature;
            }
        }

        /// <summary>
        /// Canonical, token independent description of a method body. Optimum rewrites assemblies, which
        /// renumbers metadata tokens, so the IL bytes cannot be compared. Instructions, resolved operands,
        /// exception blocks and local variable types are compared instead. Numbers are formatted with the
        /// invariant culture so the result does not depend on the player's language.
        /// </summary>
        internal static string Signature(MethodBase method)
        {
            List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(method);
            var labelTargets = new Dictionary<Label, int>();
            for (int i = 0; i < instructions.Count; i++)
            {
                foreach (Label label in instructions[i].labels)
                    labelTargets[label] = i;
            }

            int Target(Label label) => labelTargets.TryGetValue(label, out int index) ? index : -1;

            var text = new StringBuilder();
            MethodBody body = method.GetMethodBody();
            if (body != null)
            {
                text.Append("locals:");
                foreach (LocalVariableInfo local in body.LocalVariables)
                    text.Append(TypeName(local.LocalType)).Append(local.IsPinned ? "!" : "").Append(';');
                text.Append('\n');
            }

            foreach (CodeInstruction instruction in instructions)
            {
                foreach (ExceptionBlock block in instruction.blocks)
                    text.Append('[').Append(block.blockType).Append(' ').Append(TypeName(block.catchType)).Append("]\n");

                text.Append(instruction.opcode.Name);
                if (instruction.operand != null)
                    text.Append(' ').Append(DescribeOperand(instruction.operand, Target));
                text.Append('\n');
            }

            using SHA256 sha = SHA256.Create();
            string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
            return instructions.Count.ToString(CultureInfo.InvariantCulture) + ":" + hash.Substring(0, 24);
        }

        private static string DescribeOperand(object operand, Func<Label, int> target)
        {
            switch (operand)
            {
                case Label label:
                    return "L" + target(label).ToString(CultureInfo.InvariantCulture);
                case Label[] labels:
                    return string.Join(",", labels.Select(l => "L" + target(l).ToString(CultureInfo.InvariantCulture)));
                case LocalBuilder local:
                    return "loc" + local.LocalIndex.ToString(CultureInfo.InvariantCulture) + ":" + TypeName(local.LocalType);
                case MethodBase method:
                    return TypeName(method.DeclaringType) + "::" + method.Name + "<"
                        + string.Join(",", method.IsGenericMethod ? method.GetGenericArguments().Select(TypeName) : Enumerable.Empty<string>()) + ">("
                        + string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";
                case FieldInfo field:
                    return TypeName(field.DeclaringType) + "::" + field.Name;
                case Type type:
                    return TypeName(type);
                case string literal:
                    return "\"" + literal + "\"";
                case IFormattable number:
                    return number.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return operand.ToString();
            }
        }

        /// <summary>
        /// True when the ClientSettings class of the host has a member with this name. Optimum injects its
        /// settings there, which is how OptiTime sees that Optimum ships the same feature.
        /// </summary>
        internal static bool ClientSettingsHasMember(string name)
        {
            try
            {
                Type type = AccessTools.TypeByName(ClientSettingsTypeName);
                if (type == null)
                    return false;

                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                return type.GetMember(name, all).Length > 0;
            }
            catch
            {
                // The method signature check still decides when the settings cannot be read.
                return false;
            }
        }

        /// <summary>
        /// Lists every method this class decides about with its current signature, in the format of
        /// <see cref="OptimumVanillaSignatures"/>. The unit test that guards the table prints it.
        /// </summary>
        internal static SortedDictionary<string, string> DescribeAllTargets()
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            IEnumerable<string> targets = Features.SelectMany(f => f.Targets).Concat(AlwaysOnTargets).Distinct();
            foreach (string target in targets)
            {
                foreach (MethodBase method in ResolveOverloads(target))
                    result[MethodKey(method)] = Signature(method);
            }

            return result;
        }
    }
}
