using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Vintagestory.API.Client;

namespace OptiTime
{
    public static class MeshPoolOptimization
    {
        private static Action<string> logger;

        public static void SetLogger(Action<string> loggerCallback)
        {
            logger = loggerCallback;
        }

        public static void Cleanup()
        {
            logger = null;
        }

        /// <summary>
        /// Transpiler: defers per-iteration /3 divisions to a single division after the loop.
        /// Vanilla: RenderedTriangles += num / 3; AllocatedTris += num / 3; (each iteration)
        /// Patched: accumulates raw indices, divides once after the loop.
        /// Falls through unchanged if the expected IL pattern is not found.
        /// </summary>
        public static IEnumerable<CodeInstruction> TranspileDeferDivision(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var renderedField = AccessTools.Field(typeof(MeshDataPool), nameof(MeshDataPool.RenderedTriangles));
            var allocatedField = AccessTools.Field(typeof(MeshDataPool), nameof(MeshDataPool.AllocatedTris));

            if (renderedField == null || allocatedField == null)
                return codes;

            int removed = 0;
            for (int i = 0; i < codes.Count - 1; i++)
            {
                if (IsLdcI4_3(codes[i]) && codes[i + 1].opcode == OpCodes.Div)
                {
                    codes[i].opcode = OpCodes.Nop;
                    codes[i].operand = null;
                    codes[i + 1].opcode = OpCodes.Nop;
                    codes[i + 1].operand = null;
                    removed++;
                }
            }

            if (removed == 0)
            {
                logger?.Invoke("[OptiTime] Mesh pool transpiler: no div-by-3 found, patch skipped");
                return codes;
            }

            // Insert this.RenderedTriangles /= 3; this.AllocatedTris /= 3; before ret
            for (int i = codes.Count - 1; i >= 0; i--)
            {
                if (codes[i].opcode == OpCodes.Ret)
                {
                    codes.InsertRange(i, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, renderedField),
                        new CodeInstruction(OpCodes.Ldc_I4_3),
                        new CodeInstruction(OpCodes.Div),
                        new CodeInstruction(OpCodes.Stfld, renderedField),

                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, allocatedField),
                        new CodeInstruction(OpCodes.Ldc_I4_3),
                        new CodeInstruction(OpCodes.Div),
                        new CodeInstruction(OpCodes.Stfld, allocatedField),
                    });
                    break;
                }
            }

            return codes;
        }

        private static bool IsLdcI4_3(CodeInstruction instr)
        {
            if (instr.opcode == OpCodes.Ldc_I4_3) return true;
            if (instr.opcode == OpCodes.Ldc_I4_S && instr.operand is sbyte sb && sb == 3) return true;
            if (instr.opcode == OpCodes.Ldc_I4 && instr.operand is int v && v == 3) return true;
            return false;
        }
    }
}
