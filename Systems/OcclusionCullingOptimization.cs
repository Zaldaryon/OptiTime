using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace OptiTime
{
    public class OcclusionCullingOptimization
    {
        private const int MIN_CHUNK_THRESHOLD = 70;
        private const int MAX_CHUNK_THRESHOLD = 200;

        private static int GetChunkThreshold()
        {
            try
            {
                var clientSettingsType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
                if (clientSettingsType == null) return MIN_CHUNK_THRESHOLD;

                var viewDistanceProp = AccessTools.Property(clientSettingsType, "ViewDistance");
                if (viewDistanceProp == null) return MIN_CHUNK_THRESHOLD;

                int viewDistance = (int)viewDistanceProp.GetValue(null);
                int vdChunks = Math.Max(1, viewDistance / 32);
                int threshold = vdChunks * vdChunks;
                return Math.Clamp(threshold, MIN_CHUNK_THRESHOLD, MAX_CHUNK_THRESHOLD);
            }
            catch
            {
                return MIN_CHUNK_THRESHOLD;
            }
        }

        public static IEnumerable<CodeInstruction> TranspileThreshold(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            bool foundThreshold = false;

            var getThresholdMethod = AccessTools.Method(
                typeof(OcclusionCullingOptimization),
                nameof(GetChunkThreshold),
                Type.EmptyTypes);

            if (getThresholdMethod == null)
            {
                throw new Exception("Failed to find GetChunkThreshold method");
            }

            for (int i = 0; i < codes.Count; i++)
            {
                var instruction = codes[i];

                bool isLdc100 =
                    (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte sVal && sVal == 100) ||
                    (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int iVal && iVal == 100);

                if (!foundThreshold && isLdc100)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getThresholdMethod);
                    foundThreshold = true;
                }

                yield return codes[i];
            }

            if (!foundThreshold)
            {
                throw new Exception("Failed to find chunk-count threshold (100) in CullInvisibleChunks");
            }
        }
    }
}
