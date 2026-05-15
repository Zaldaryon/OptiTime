using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public static class ShadowOptimization
    {
        // --- Skip BlendNoCull (vegetation) in far cascade shadow ---

        private static Action<string> logger;

        public static void SetLogger(Action<string> log) => logger = log;

        /// <summary>
        /// Called from transpiled IL. Returns true if vegetation should be culled (far shadow pass).
        /// </summary>
        public static bool ShouldCullVegetation(int currentRenderStage)
        {
            bool cull = currentRenderStage == (int)EnumRenderStage.ShadowFar;
            ModuleShadowVeg.OnShadowFrame(cull);
            return cull;
        }

        public static IEnumerable<CodeInstruction> TranspileRenderShadow(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);
            var renderMethod = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
            var currentRenderStageField = AccessTools.Field(typeof(ClientMain), "currentRenderStage");

            // Find the loop that renders poolsByRenderPass[2] (BlendNoCull).
            // Pattern: ldc.i4.2 → ldelem → loop with Render calls.
            // We wrap the entire BlendNoCull loop in a check:
            //   if (game.currentRenderStage == ShadowFar) skip;
            //
            // The method has 4 render loops for passes [0], [5], [2], [1].
            // We need to find the third loop (pass index 2).

            int passIndex2Loc = -1;
            int loopEndLoc = -1;

            for (int i = 0; i < codes.Count - 4; i++)
            {
                // Look for: ldc.i4.2 followed by ldelem.ref (loading poolsByRenderPass[2])
                // Require preceding ldfld to avoid false matches on other ldc.i4.2 uses
                if (codes[i].opcode == OpCodes.Ldc_I4_2 &&
                    i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Ldelem_Ref)
                {
                    // Verify this is in the shadow render context by checking nearby Render calls
                    bool hasRenderCall = false;
                    for (int j = i; j < Math.Min(i + 30, codes.Count); j++)
                    {
                        if (codes[j].Calls(renderMethod))
                        {
                            hasRenderCall = true;
                            break;
                        }
                    }
                    if (!hasRenderCall) continue;

                    // Walk backwards to find the start of this loop block.
                    // The loop starts with a local variable init (ldc.i4.0 + stloc for the loop counter).
                    // We look for the preceding GlDisableCullFace call as anchor (not any callvirt inside the loop).
                    var disableCullFace = AccessTools.Method(typeof(ClientPlatformAbstract), "GlDisableCullFace");
                    for (int j = i - 1; j >= Math.Max(0, i - 30); j--)
                    {
                        if ((codes[j].opcode == OpCodes.Callvirt || codes[j].opcode == OpCodes.Call) &&
                            codes[j].operand is System.Reflection.MethodInfo mi && mi == disableCullFace)
                        {
                            for (int k = j + 1; k <= i; k++)
                            {
                                if (codes[k].opcode == OpCodes.Ldc_I4_0)
                                {
                                    passIndex2Loc = k;
                                    break;
                                }
                            }
                            break;
                        }
                    }
                    if (passIndex2Loc < 0) continue;

                    // Find the end of this loop: look for the next ldc.i4.1 + ldelem (pass [1]) or
                    // the next ldc.i4.0 + stloc that starts the pass [1] loop
                    for (int j = i + 1; j < codes.Count - 2; j++)
                    {
                        if (codes[j].opcode == OpCodes.Ldc_I4_1 &&
                            codes[j + 1].opcode == OpCodes.Ldelem_Ref)
                        {
                            // Walk back to find the ldc.i4.0 that starts the [1] loop
                            for (int k = j - 1; k > i; k--)
                            {
                                if (codes[k].opcode == OpCodes.Ldc_I4_0)
                                {
                                    loopEndLoc = k;
                                    break;
                                }
                            }
                            break;
                        }
                    }
                    break;
                }
            }

            if (passIndex2Loc >= 0 && loopEndLoc > passIndex2Loc)
            {
                // Insert a branch: if (game.currentRenderStage == ShadowFar) goto skipLabel
                var skipLabel = il.DefineLabel();
                var skipLabelInstr = new CodeInstruction(OpCodes.Nop);
                skipLabelInstr.labels.Add(skipLabel);

                var checkInstructions = new List<CodeInstruction>
                {
                    new CodeInstruction(OpCodes.Ldarg_0), // this (ChunkRenderer)
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ChunkRenderer), "game")),
                    new CodeInstruction(OpCodes.Ldfld, currentRenderStageField),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShadowOptimization), nameof(ShouldCullVegetation))),
                    new CodeInstruction(OpCodes.Brtrue, skipLabel)
                };

                codes.InsertRange(passIndex2Loc, checkInstructions);

                // Adjust loopEndLoc for the inserted instructions
                loopEndLoc += checkInstructions.Count;

                // Insert the skip label nop at the loop end
                codes.Insert(loopEndLoc, skipLabelInstr);
            }
            else
            {
                logger?.Invoke("[OptiTime] Shadow far vegetation cull: IL anchors not found, optimization inactive");
            }

            return codes;
        }

        public static void Cleanup()
        {
            logger = null;
        }
    }
}
