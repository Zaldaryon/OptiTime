using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public static class EntityShadowCullingOptimization
    {
        // Vanilla SystemRenderEntities.OnRenderFrameShadows renders every entity in the shadow
        // frustum (radius 3.0) regardless of distance. At the default shadow map size
        // (Math.Max(4, ShadowMapQuality + 2) * 1024, min 4096^2 — verified at
        // ClientPlatformWindows.cs:1468-1469), an entity beyond ~80 blocks occupies a
        // sub-pixel area and contributes no visible shadow. This optimization injects a
        // horizontal squared-distance check into the loop so distant non-player entities
        // are dropped before IsShadowRendered is set, which also short-circuits the
        // second (batched) shadow pass that gates on the same flag.

        // 80 blocks horizontal squared. Conservative: even slow-moving large entities are
        // sub-pixel in the shadow map past this distance. Player and AllowOutsideLoadedRange
        // entities are always rendered.
        private const double CullDistanceSq = 80.0 * 80.0;

        private static Action<string> logger;
        private static volatile bool transpilerActive;

        public static void SetLogger(Action<string> log) => logger = log;

        public static bool TranspilerActive => transpilerActive;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBeyondCullDistance(Entity entity)
        {
            if (entity == null) return false;
            if (entity.AllowOutsideLoadedRange) return false;

            var clientWorld = entity.World as IClientWorldAccessor;
            var player = clientWorld?.Player?.Entity;
            if (player == null || entity == player) return false;

            var pPos = player.Pos;
            var ePos = entity.Pos;
            double dx = ePos.X - pPos.X;
            double dz = ePos.Z - pPos.Z;
            return dx * dx + dz * dz > CullDistanceSq;
        }

        public static IEnumerable<CodeInstruction> TranspileOnRenderFrameShadows(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);
            var isShadowRenderedField = AccessTools.Field(typeof(Entity), nameof(Entity.IsShadowRendered));
            var cullCheck = AccessTools.Method(typeof(EntityShadowCullingOptimization), nameof(IsBeyondCullDistance));

            if (isShadowRenderedField == null || cullCheck == null)
            {
                logger?.Invoke("[OptiTime] Entity shadow cull: required members not found, optimization inactive");
                return codes;
            }

            // Locate the two stfld IsShadowRendered. Vanilla writes `entity.IsShadowRendered = true;` in
            // the matched branch and `entity.IsShadowRendered = false;` in the else branch. The else
            // branch's leading instruction carries the brfalse target label from the condition chain.
            int trueStfldIdx = -1;
            int elseStfldIdx = -1;

            for (int i = 1; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Stfld &&
                    codes[i].operand is FieldInfo fi && fi == isShadowRenderedField)
                {
                    var prev = codes[i - 1];
                    if (prev.opcode == OpCodes.Ldc_I4_1 && trueStfldIdx < 0) trueStfldIdx = i;
                    else if (prev.opcode == OpCodes.Ldc_I4_0 && elseStfldIdx < 0) elseStfldIdx = i;
                }
            }

            if (trueStfldIdx < 0 || elseStfldIdx < 0)
            {
                logger?.Invoke("[OptiTime] Entity shadow cull: IL anchors not found (stfld IsShadowRendered), optimization inactive");
                return codes;
            }

            // True branch entry: `ldloc entity; ldc.i4.1; stfld IsShadowRendered` → entry is at trueStfldIdx-2.
            // Else branch entry: `ldloc entity; ldc.i4.0; stfld IsShadowRendered` → entry is at elseStfldIdx-2.
            int trueBranchEntry = trueStfldIdx - 2;
            int elseBranchEntry = elseStfldIdx - 2;
            if (trueBranchEntry < 0 || elseBranchEntry < 0)
            {
                logger?.Invoke("[OptiTime] Entity shadow cull: IL pattern shorter than expected, optimization inactive");
                return codes;
            }

            var entityLoad = codes[trueBranchEntry];
            if (!IsLdloc(entityLoad.opcode))
            {
                logger?.Invoke("[OptiTime] Entity shadow cull: expected ldloc at true-branch entry, optimization inactive");
                return codes;
            }

            // Acquire (or create) a label on the else-branch entry instruction.
            Label elseLabel;
            var elseEntry = codes[elseBranchEntry];
            if (elseEntry.labels.Count > 0)
            {
                elseLabel = elseEntry.labels[0];
            }
            else
            {
                elseLabel = il.DefineLabel();
                elseEntry.labels.Add(elseLabel);
            }

            // Inject at the true-branch entry:
            //   ldloc entity
            //   call EntityShadowCullingOptimization.IsBeyondCullDistance
            //   brtrue elseLabel
            // The original `ldloc entity` (which may carry inbound labels from the brfalse chain)
            // must remain at the same position, so we copy its labels onto the first injected
            // instruction and clear them from the original.
            var injected = new List<CodeInstruction>
            {
                new CodeInstruction(entityLoad.opcode, entityLoad.operand),
                new CodeInstruction(OpCodes.Call, cullCheck),
                new CodeInstruction(OpCodes.Brtrue, elseLabel)
            };

            if (entityLoad.labels.Count > 0)
            {
                injected[0].labels.AddRange(entityLoad.labels);
                entityLoad.labels.Clear();
            }

            codes.InsertRange(trueBranchEntry, injected);
            transpilerActive = true;
            return codes;
        }

        private static bool IsLdloc(OpCode op)
        {
            return op == OpCodes.Ldloc || op == OpCodes.Ldloc_S ||
                   op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 ||
                   op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3;
        }

        public static void Cleanup()
        {
            logger = null;
            transpilerActive = false;
        }
    }
}
