using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public class ChunkTesselationOptimization
    {
        private const float BaseFrameMs = 1000f / 60f;
        private const int FRAME_HISTORY_CAPACITY = 24;
        private const int MIN_FRAMETIME_FOR_PRESSURE = 1;
        private const int QUEUE_THRESHOLD_LOW = 150;
        private const int QUEUE_THRESHOLD_HIGH = 300;

        // Only the fields needed by GetAdaptiveMultiplier (called from transpiled code)
        private static volatile FieldInfo tessChunksQueueField;
        private static volatile FieldInfo tessChunksQueuePriorityField;
        private static volatile PropertyInfo tessChunksQueueCountProperty;
        private static volatile FieldInfo tessChunksQueueCountField;

        private static readonly float[] frametimeHistoryMs = new float[FRAME_HISTORY_CAPACITY];
        private static int frametimeHistoryCount;
        private static int frametimeHistoryIndex;
        private static float frametimeHistorySumMs;

        private static Action<string> logger;

        public static void SetLogger(Action<string> log) => logger = log;

        public static void Cleanup()
        {
            tessChunksQueueField = null;
            tessChunksQueuePriorityField = null;
            tessChunksQueueCountProperty = null;
            tessChunksQueueCountField = null;
            frametimeHistoryCount = 0;
            frametimeHistoryIndex = 0;
            frametimeHistorySumMs = 0f;
            logger = null;
        }

        /// <summary>
        /// Called from transpiled IL in place of the hardcoded constant 3.
        /// Receives the ChunkTesselatorManager instance via ldarg.0.
        /// </summary>
        public static int GetAdaptiveMultiplier(object managerInstance)
        {
            try
            {
                if (managerInstance == null) return 3;

                EnsureReflection(managerInstance.GetType());
                if (tessChunksQueueField == null) return 3;

                var queue = tessChunksQueueField.GetValue(managerInstance);
                if (queue == null) return 3;

                int queueCount = GetQueueCount(queue);
                int prioCount = 0;
                if (tessChunksQueuePriorityField != null)
                {
                    var prioQueue = tessChunksQueuePriorityField.GetValue(managerInstance);
                    if (prioQueue is Queue<TesselatedChunk> pq)
                        prioCount = pq.Count;
                }

                int queueSize = queueCount + prioCount;

                int baseMultiplier;
                if (queueSize >= QUEUE_THRESHOLD_HIGH)
                    baseMultiplier = 1;
                else if (queueSize >= QUEUE_THRESHOLD_LOW)
                    baseMultiplier = 2;
                else if (queueSize >= 50)
                    baseMultiplier = 4;
                else
                    baseMultiplier = 3;

                int adaptiveMultiplier = baseMultiplier;
                float avgFrameMs = frametimeHistoryCount > 0
                    ? frametimeHistorySumMs / frametimeHistoryCount
                    : 0f;

                if (avgFrameMs >= MIN_FRAMETIME_FOR_PRESSURE)
                {
                    int maxFps = ClientSettings.MaxFPS;
                    float targetFrameMs = maxFps > 0 ? 1000f / maxFps : BaseFrameMs;
                    float pressureRatio = avgFrameMs / targetFrameMs;

                    if (pressureRatio > 1.7f)
                        adaptiveMultiplier = Math.Max(1, (int)MathF.Round(baseMultiplier * 0.55f));
                    else if (pressureRatio > 1.3f)
                        adaptiveMultiplier = Math.Max(1, (int)MathF.Round(baseMultiplier * 0.7f));
                    else if (pressureRatio > 1.15f)
                        adaptiveMultiplier = Math.Max(1, (int)MathF.Round(baseMultiplier * 0.85f));
                    else if (pressureRatio > 1.05f)
                        adaptiveMultiplier = Math.Max(1, (int)MathF.Round(baseMultiplier * 0.95f));
                }

                adaptiveMultiplier = Math.Min(baseMultiplier, Math.Max(1, adaptiveMultiplier));

                if (ProfilingHelper.Enabled)
                    ProfilingHelper.Mark("opt-chunktess", $"queue={queueSize},mult={adaptiveMultiplier},avgFrame={avgFrameMs:0.00}");

                return adaptiveMultiplier;
            }
            catch
            {
                return 3;
            }
        }

        /// <summary>
        /// Transpiler: replaces the hardcoded `3` in the upload budget formula
        /// with a call to GetAdaptiveMultiplier(this).
        /// Also injects frame time recording at method entry.
        /// </summary>
        public static IEnumerable<CodeInstruction> TranspileTesselationThrottle(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getMultiplierMethod = AccessTools.Method(
                typeof(ChunkTesselationOptimization),
                nameof(GetAdaptiveMultiplier),
                new Type[] { typeof(object) });
            var recordFrameMethod = AccessTools.Method(
                typeof(ChunkTesselationOptimization),
                nameof(RecordFrameTime));

            if (getMultiplierMethod == null)
            {
                logger?.Invoke("[ot] ChunkTess: GetAdaptiveMultiplier not found");
                return codes;
            }

            // Inject RecordFrameTime(dt) at method start — only if anchor is found
            var uploadRateLimiterField = AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.ChunkVerticesUploadRateLimiter));
            if (uploadRateLimiterField == null)
            {
                logger?.Invoke("[ot] ChunkTess: ChunkVerticesUploadRateLimiter getter not found");
                return instructions;
            }

            int multiplierIndex = FindUploadMultiplierIndex(codes, uploadRateLimiterField);
            if (multiplierIndex < 0)
            {
                // Diagnostic: find any ldc.i4.3 and log surrounding instructions
                for (int d = 0; d < codes.Count; d++)
                {
                    if (LoadsInt32(codes[d], 3))
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"[ot] ChunkTess: found ldc.i4.3 at {d}, next 8: ");
                        for (int k = d + 1; k < Math.Min(codes.Count, d + 9); k++)
                            sb.Append($"[{codes[k].opcode} {codes[k].operand}] ");
                        logger?.Invoke(sb.ToString());
                    }
                }
                logger?.Invoke("[ot] ChunkTess: upload multiplier anchor not found");
                return instructions;
            }

            if (recordFrameMethod != null)
            {
                codes.Insert(0, new CodeInstruction(OpCodes.Ldarg_1)); // dt
                codes.Insert(1, new CodeInstruction(OpCodes.Call, recordFrameMethod));
                multiplierIndex += 2; // adjust for inserted instructions
            }

            var replacement = new CodeInstruction(OpCodes.Ldarg_0);
            replacement.labels.AddRange(codes[multiplierIndex].labels);
            replacement.blocks.AddRange(codes[multiplierIndex].blocks);
            codes[multiplierIndex] = replacement;
            codes.Insert(multiplierIndex + 1, new CodeInstruction(OpCodes.Call, getMultiplierMethod));

            logger?.Invoke($"[ot] ChunkTess: adaptive multiplier injected at IL index {multiplierIndex}");
            return codes;
        }

        public static void RecordFrameTime(float dt)
        {
            float frameMs = dt * 1000f;
            if (dt <= 0f || float.IsNaN(frameMs) || float.IsInfinity(frameMs))
                return;

            if (frametimeHistoryCount < FRAME_HISTORY_CAPACITY)
            {
                frametimeHistoryMs[frametimeHistoryCount++] = frameMs;
                frametimeHistorySumMs += frameMs;
            }
            else
            {
                frametimeHistorySumMs -= frametimeHistoryMs[frametimeHistoryIndex];
                frametimeHistoryMs[frametimeHistoryIndex] = frameMs;
                frametimeHistorySumMs += frameMs;
                frametimeHistoryIndex = (frametimeHistoryIndex + 1) % FRAME_HISTORY_CAPACITY;
            }
        }

        private static void EnsureReflection(Type managerType)
        {
            if (managerType == null || tessChunksQueueField != null) return;

            tessChunksQueueField = AccessTools.Field(managerType, "tessChunksQueue");
            tessChunksQueuePriorityField = AccessTools.Field(managerType, "tessChunksQueuePriority");

            if (tessChunksQueueField?.FieldType != null)
            {
                tessChunksQueueCountProperty = tessChunksQueueField.FieldType.GetProperty(
                    "Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tessChunksQueueCountProperty == null)
                    tessChunksQueueCountField = AccessTools.Field(tessChunksQueueField.FieldType, "Count");
            }
        }

        private static int GetQueueCount(object queue)
        {
            if (queue == null) return 0;
            try
            {
                if (tessChunksQueueCountProperty != null)
                    return (int)tessChunksQueueCountProperty.GetValue(queue);
                if (tessChunksQueueCountField != null)
                    return (int)tessChunksQueueCountField.GetValue(queue);
            }
            catch { }
            return 0;
        }

        private static int FindUploadMultiplierIndex(List<CodeInstruction> codes, System.Reflection.MethodInfo rateLimiterGetter)
        {
            if (codes == null || rateLimiterGetter == null) return -1;

            // Actual IL pattern in 1.22:
            // ldc.i4.3 → ldloc → ldc.i4.1 → call get_ChunkVerticesUploadRateLimiter → ldc.i4.s 31 → and → shl → div → add → mul
            // We look for: ldc.i4.3 ... call getter ... div ... add ... mul (within 12 instructions)
            for (int i = 0; i < codes.Count; i++)
            {
                if (!LoadsInt32(codes[i], 3)) continue;

                int end = Math.Min(codes.Count, i + 12);
                bool foundGetter = false, foundDiv = false, foundAdd = false, foundMul = false;

                for (int j = i + 1; j < end; j++)
                {
                    if (!foundGetter && codes[j].Calls(rateLimiterGetter))
                        foundGetter = true;
                    else if (foundGetter && !foundDiv && (codes[j].opcode == OpCodes.Div || codes[j].opcode == OpCodes.Div_Un))
                        foundDiv = true;
                    else if (foundDiv && !foundAdd && codes[j].opcode == OpCodes.Add)
                        foundAdd = true;
                    else if (foundAdd && !foundMul && codes[j].opcode == OpCodes.Mul)
                        foundMul = true;
                }

                if (foundGetter && foundDiv && foundAdd && foundMul)
                    return i;
            }
            return -1;
        }

        private static bool LoadsInt32(CodeInstruction instruction, int value)
        {
            if (instruction == null) return false;
            if (value == 3 && instruction.opcode == OpCodes.Ldc_I4_3) return true;
            if (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte sb) return sb == value;
            if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int iv) return iv == value;
            return false;
        }
    }
}
