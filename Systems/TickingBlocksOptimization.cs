using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.MathTools;
using OptiTime.Diagnostics;

namespace OptiTime
{
    /// <summary>
    /// Eliminates per-iteration BlockPos allocations in
    /// SystemClientTickingBlocks.onOffThreadParticleTick by replacing
    /// new BlockPos(x, y, z) with a reused BlockPos.Set(x, y, z).
    /// Saves 30K-90K heap allocations/sec in nature-heavy scenes.
    /// </summary>
    public static class TickingBlocksOptimization
    {
        /// <summary>
        /// Transpiler: finds the `new BlockPos(int, int, int)` inside the
        /// foreach loop and replaces it with a local BlockPos + Set call.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var blockPosCtor = AccessTools.Constructor(
                typeof(BlockPos), new[] { typeof(int), typeof(int), typeof(int) });
            var blockPosSet = AccessTools.Method(
                typeof(BlockPos), nameof(BlockPos.Set),
                new[] { typeof(int), typeof(int), typeof(int) });

            if (blockPosCtor == null || blockPosSet == null)
                return instructions;

            // Declare a local to hold the reusable BlockPos
            var reusableLocal = il.DeclareLocal(typeof(BlockPos));

            var codes = new List<CodeInstruction>(instructions);
            bool initialized = false;

            for (int i = 0; i < codes.Count; i++)
            {
                // Find: newobj BlockPos(int, int, int)
                if (codes[i].opcode == OpCodes.Newobj &&
                    codes[i].operand is ConstructorInfo ctor &&
                    ctor == blockPosCtor)
                {
                    if (!initialized)
                    {
                        // Insert at method start: reusable = new BlockPos(0, 0, 0)
                        // Find the first instruction and insert before it
                        var initInstructions = new List<CodeInstruction>
                        {
                            new CodeInstruction(OpCodes.Ldc_I4_0),
                            new CodeInstruction(OpCodes.Ldc_I4_0),
                            new CodeInstruction(OpCodes.Ldc_I4_0),
                            new CodeInstruction(OpCodes.Newobj, blockPosCtor),
                            new CodeInstruction(OpCodes.Stloc, reusableLocal)
                        };
                        codes.InsertRange(0, initInstructions);
                        i += initInstructions.Count;
                        initialized = true;
                    }

                    // Replace: new BlockPos(x, y, z)
                    // Stack has: x, y, z
                    // We need: reusable.Set(x, y, z) which returns BlockPos
                    // But Set's stack is: this, x, y, z
                    // So we need to insert ldloc before the 3 args

                    // Find where the 3 args start (go back to find the first arg push)
                    // The pattern is: [push x] [push y] [push z] [newobj]
                    // We need to insert ldloc_reusable before the first arg

                    // Find the start of the 3 arguments by counting back
                    // This is tricky with complex expressions, so instead:
                    // Store the 3 args in temp locals, then call Set

                    var tempZ = il.DeclareLocal(typeof(int));
                    var tempY = il.DeclareLocal(typeof(int));
                    var tempX = il.DeclareLocal(typeof(int));

                    // Before the newobj, stack is: ..., x, y, z
                    // Replace newobj with: store z, store y, store x, load reusable, load x, load y, load z, call Set
                    var replacement = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Stloc, tempZ),
                        new CodeInstruction(OpCodes.Stloc, tempY),
                        new CodeInstruction(OpCodes.Stloc, tempX),
                        // Diag: count each reused iteration
                        new CodeInstruction(OpCodes.Ldc_I4_1),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ModuleTickingBlocks), nameof(ModuleTickingBlocks.OnTick))),
                        new CodeInstruction(OpCodes.Ldloc, reusableLocal),
                        new CodeInstruction(OpCodes.Ldloc, tempX),
                        new CodeInstruction(OpCodes.Ldloc, tempY),
                        new CodeInstruction(OpCodes.Ldloc, tempZ),
                        new CodeInstruction(OpCodes.Callvirt, blockPosSet)
                    };

                    codes.RemoveAt(i);
                    codes.InsertRange(i, replacement);
                    i += replacement.Count - 1;
                    break; // Only replace the first occurrence (the one in the loop)
                }
            }

            return codes;
        }
    }
}
