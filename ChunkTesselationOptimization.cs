using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public class ChunkTesselationOptimization
    {
        private const float BaseFrameMs = 1000f / 60f;
        private const int FRAME_HISTORY_CAPACITY = 24;
        private const int MIN_FRAMETIME_FOR_PRESSURE = 1;
        private const int QUEUE_THRESHOLD_LOW = 150;   // Increased from 50 - berry bushes won't trigger throttle
        private const int QUEUE_THRESHOLD_HIGH = 300;  // Emergency throttle for extreme loads

        private static volatile FieldInfo tessChunksQueueField = null;
        private static volatile FieldInfo tessChunksQueuePriorityField = null;
        private static volatile PropertyInfo tessChunksQueueCountProperty = null;
        private static volatile PropertyInfo tessChunksQueuePriorityCountProperty = null;
        private static volatile FieldInfo tessChunksQueueCountField = null;
        private static volatile FieldInfo tessChunksQueuePriorityCountField = null;
        private static volatile FieldInfo gameField = null;
        private static volatile FieldInfo tessChunksQueueLockField = null;
        private static volatile FieldInfo tessChunksQueuePriorityLockField = null;
        private static volatile FieldInfo tmpPosField = null;
        private static volatile FieldInfo singleUploadDelayCounterField = null;
        private static volatile FieldInfo processPrioQueueField = null;
        private static volatile FieldInfo dirtyChunksPriorityField = null;
        private static volatile FieldInfo dirtyChunksField = null;
        private static volatile FieldInfo dirtyChunksLastField = null;
        private static volatile FieldInfo chunkRendererField = null;
        private static volatile FieldInfo runtimeStatsChunksAwaitingTesselationField = null;
        private static volatile FieldInfo runtimeStatsChunksAwaitingPoolingField = null;
        private static volatile FieldInfo tesselatedChunkPositionXField = null;
        private static volatile FieldInfo tesselatedChunkPositionYAndDimensionField = null;
        private static volatile FieldInfo tesselatedChunkPositionZField = null;
        private static volatile FieldInfo tesselatedChunkVerticesCountField = null;
        private static volatile FieldInfo tesselatedChunkChunkField = null;
        private static volatile FieldInfo clientChunkQueuedForUploadField = null;
        private static volatile MethodInfo tesselatedChunkRecalcPriorityMethod = null;
        private static volatile MethodInfo tesselatedChunkUnusedDisposeMethod = null;
        private static volatile MethodInfo worldMapGetChunkAtBlockPosMethod = null;
        private static volatile MethodInfo chunkRendererAddTesselatedChunkMethod = null;
        private static volatile MethodInfo triggerChunkRetesselatedMethod = null;

        private static readonly float[] frametimeHistoryMs = new float[FRAME_HISTORY_CAPACITY];
        private static int frametimeHistoryCount = 0;
        private static int frametimeHistoryIndex = 0;
        private static float frametimeHistorySumMs = 0f;
        private static int frameCounter;

        private static System.Action<string> logger = null;
        public static void SetLogger(System.Action<string> log)
        {
            logger = log;
        }

        public static void Cleanup()
        {
            tessChunksQueueField = null;
            tessChunksQueuePriorityField = null;
            tessChunksQueueCountProperty = null;
            tessChunksQueuePriorityCountProperty = null;
            tessChunksQueueCountField = null;
            tessChunksQueuePriorityCountField = null;
            gameField = null;
            tessChunksQueueLockField = null;
            tessChunksQueuePriorityLockField = null;
            tmpPosField = null;
            singleUploadDelayCounterField = null;
            processPrioQueueField = null;
            dirtyChunksPriorityField = null;
            dirtyChunksField = null;
            dirtyChunksLastField = null;
            chunkRendererField = null;
            runtimeStatsChunksAwaitingTesselationField = null;
            runtimeStatsChunksAwaitingPoolingField = null;
            tesselatedChunkPositionXField = null;
            tesselatedChunkPositionYAndDimensionField = null;
            tesselatedChunkPositionZField = null;
            tesselatedChunkVerticesCountField = null;
            tesselatedChunkChunkField = null;
            clientChunkQueuedForUploadField = null;
            tesselatedChunkRecalcPriorityMethod = null;
            tesselatedChunkUnusedDisposeMethod = null;
            worldMapGetChunkAtBlockPosMethod = null;
            chunkRendererAddTesselatedChunkMethod = null;
            triggerChunkRetesselatedMethod = null;
            frametimeHistoryCount = 0;
            frametimeHistoryIndex = 0;
            frametimeHistorySumMs = 0f;
            frameCounter = 0;
            logger = null;
        }

        public static bool OnBeforeFrame_Prefix(object __instance, float dt)
        {
            try
            {
                EnsureQueueReflection(__instance?.GetType());
                RecordFrameTime(dt);

                if (__instance == null ||
                    gameField == null ||
                    tessChunksQueueField == null ||
                    tessChunksQueuePriorityField == null ||
                    tessChunksQueueLockField == null ||
                    tessChunksQueuePriorityLockField == null ||
                    tmpPosField == null ||
                    singleUploadDelayCounterField == null ||
                    processPrioQueueField == null)
                {
                    return true;
                }

                var game = gameField.GetValue(__instance) as ClientMain;
                EnsureRuntimeReflection(game);

                var tessChunksQueue = tessChunksQueueField.GetValue(__instance) as SortableQueue<TesselatedChunk>;
                var tessChunksQueuePriority = tessChunksQueuePriorityField.GetValue(__instance) as Queue<TesselatedChunk>;
                var tessChunksQueueLock = tessChunksQueueLockField.GetValue(__instance);
                var tessChunksQueuePriorityLock = tessChunksQueuePriorityLockField.GetValue(__instance);
                var tmpPos = tmpPosField.GetValue(__instance) as Vec3i;

                if (game == null || tessChunksQueue == null || tessChunksQueuePriority == null || tessChunksQueueLock == null || tessChunksQueuePriorityLock == null || tmpPos == null)
                {
                    return true;
                }

                if (dirtyChunksPriorityField == null || dirtyChunksField == null || dirtyChunksLastField == null ||
                    runtimeStatsChunksAwaitingTesselationField == null || runtimeStatsChunksAwaitingPoolingField == null ||
                    tesselatedChunkPositionXField == null || tesselatedChunkPositionYAndDimensionField == null ||
                    tesselatedChunkPositionZField == null || tesselatedChunkVerticesCountField == null ||
                    tesselatedChunkChunkField == null || clientChunkQueuedForUploadField == null ||
                    tesselatedChunkRecalcPriorityMethod == null || tesselatedChunkUnusedDisposeMethod == null ||
                    worldMapGetChunkAtBlockPosMethod == null || chunkRendererAddTesselatedChunkMethod == null || chunkRendererField == null)
                {
                    return true;
                }

                int dirtyChunksPriorityCount = GetQueueCount(dirtyChunksPriorityField.GetValue(game), null, null);
                int dirtyChunksCount = GetQueueCount(dirtyChunksField.GetValue(game), null, null);
                int dirtyChunksLastCount = GetQueueCount(dirtyChunksLastField.GetValue(game), null, null);
                runtimeStatsChunksAwaitingTesselationField.SetValue(null, dirtyChunksPriorityCount + dirtyChunksCount + dirtyChunksLastCount);
                runtimeStatsChunksAwaitingPoolingField.SetValue(null, tessChunksQueuePriority.Count + GetQueueCount(tessChunksQueue, tessChunksQueueCountProperty, tessChunksQueueCountField));

                int num1 = game.frustumCuller.ViewDistanceSq / 48 + 350;
                int num2 = 0;
                bool processPrioQueue = (bool)processPrioQueueField.GetValue(__instance);

                if (processPrioQueue)
                {
                    lock (tessChunksQueuePriorityLock)
                    {
                        while (tessChunksQueuePriority.Count > 0)
                        {
                            TesselatedChunk tesschunk = tessChunksQueuePriority.Dequeue();
                            SetQueuedForUpload(tesschunk, false);
                            int positionX = GetIntFieldValue(tesselatedChunkPositionXField, tesschunk);
                            int positionYAndDimension = GetIntFieldValue(tesselatedChunkPositionYAndDimensionField, tesschunk);
                            int positionZ = GetIntFieldValue(tesselatedChunkPositionZField, tesschunk);
                            object chunkAtBlockPos = worldMapGetChunkAtBlockPosMethod.Invoke(game.WorldMap, new object[] { positionX, positionYAndDimension, positionZ });
                            if (chunkAtBlockPos != null)
                            {
                                object chunkRenderer = chunkRendererField.GetValue(game);
                                chunkRendererAddTesselatedChunkMethod.Invoke(chunkRenderer, new object[] { tesschunk, chunkAtBlockPos });
                                singleUploadDelayCounterField.SetValue(__instance, 10);
                                num2 += GetIntFieldValue(tesselatedChunkVerticesCountField, tesschunk);
                                tmpPos.Set(positionX / 32, positionYAndDimension / 32, positionZ / 32);
                                triggerChunkRetesselatedMethod?.Invoke(game.eventManager, new object[] { tmpPos, chunkAtBlockPos });
                            }
                            else
                            {
                                tesselatedChunkUnusedDisposeMethod.Invoke(tesschunk, null);
                            }
                        }
                    }

                    processPrioQueueField.SetValue(__instance, false);
                }

                int count = GetQueueCount(tessChunksQueue, tessChunksQueueCountProperty, tessChunksQueueCountField);
                int adaptiveMultiplier = GetAdaptiveMultiplier(__instance);
                int num3 = num1 * (adaptiveMultiplier + count / (1 << ClientSettings.ChunkVerticesUploadRateLimiter));
                int singleUploadDelayCounter = (int)singleUploadDelayCounterField.GetValue(__instance);

                if (num2 >= num3 || count < 2 && (count == 0 || singleUploadDelayCounter++ < 10))
                {
                    singleUploadDelayCounterField.SetValue(__instance, singleUploadDelayCounter);
                    return false;
                }

                singleUploadDelayCounterField.SetValue(__instance, 0);
                lock (tessChunksQueueLock)
                {
                    tessChunksQueue.RunForEach(eachTC => tesselatedChunkRecalcPriorityMethod.Invoke(eachTC, new object[] { game.player }));
                    tessChunksQueue.Sort();

                    while (GetQueueCount(tessChunksQueue, tessChunksQueueCountProperty, tessChunksQueueCountField) > 0 && num2 < num3)
                    {
                        TesselatedChunk tesschunk = tessChunksQueue.Dequeue();
                        SetQueuedForUpload(tesschunk, false);
                        int positionX = GetIntFieldValue(tesselatedChunkPositionXField, tesschunk);
                        int positionYAndDimension = GetIntFieldValue(tesselatedChunkPositionYAndDimensionField, tesschunk);
                        int positionZ = GetIntFieldValue(tesselatedChunkPositionZField, tesschunk);
                        object chunkAtBlockPos = worldMapGetChunkAtBlockPosMethod.Invoke(game.WorldMap, new object[] { positionX, positionYAndDimension, positionZ });
                        if (chunkAtBlockPos != null)
                        {
                            object chunkRenderer = chunkRendererField.GetValue(game);
                            chunkRendererAddTesselatedChunkMethod.Invoke(chunkRenderer, new object[] { tesschunk, chunkAtBlockPos });
                            num2 += GetIntFieldValue(tesselatedChunkVerticesCountField, tesschunk);
                            tmpPos.Set(positionX / 32, positionYAndDimension / 32, positionZ / 32);
                            triggerChunkRetesselatedMethod?.Invoke(game.eventManager, new object[] { tmpPos, chunkAtBlockPos });
                        }
                        else
                        {
                            tesselatedChunkUnusedDisposeMethod.Invoke(tesschunk, null);
                        }
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void RecordFrameTime(float dt)
        {
            try
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
            catch
            {
            }
        }

        private static void EnsureQueueReflection(Type managerType)
        {
            if (managerType == null)
                return;

            if (tessChunksQueueField == null)
            {
                tessChunksQueueField = AccessTools.Field(managerType, "tessChunksQueue");
                ResolveCountAccess(tessChunksQueueField?.FieldType, out var queueCountProperty, out var queueCountField);
                tessChunksQueueCountProperty = queueCountProperty;
                tessChunksQueueCountField = queueCountField;
            }

            if (tessChunksQueuePriorityField == null)
            {
                tessChunksQueuePriorityField = AccessTools.Field(managerType, "tessChunksQueuePriority");
                ResolveCountAccess(tessChunksQueuePriorityField?.FieldType, out var priorityCountProperty, out var priorityCountField);
                tessChunksQueuePriorityCountProperty = priorityCountProperty;
                tessChunksQueuePriorityCountField = priorityCountField;
            }

            gameField ??= AccessTools.Field(managerType, "game");
            tessChunksQueueLockField ??= AccessTools.Field(managerType, "tessChunksQueueLock");
            tessChunksQueuePriorityLockField ??= AccessTools.Field(managerType, "tessChunksQueuePriorityLock");
            tmpPosField ??= AccessTools.Field(managerType, "tmpPos");
            singleUploadDelayCounterField ??= AccessTools.Field(managerType, "singleUploadDelayCounter");
            processPrioQueueField ??= AccessTools.Field(managerType, "processPrioQueue");
        }

        private static void EnsureRuntimeReflection(ClientMain game)
        {
            if (game == null)
                return;

            var gameType = game.GetType();
            dirtyChunksPriorityField ??= AccessTools.Field(gameType, "dirtyChunksPriority");
            dirtyChunksField ??= AccessTools.Field(gameType, "dirtyChunks");
            dirtyChunksLastField ??= AccessTools.Field(gameType, "dirtyChunksLast");
            chunkRendererField ??= AccessTools.Field(gameType, "chunkRenderer");

            var runtimeStatsType = gameType.Assembly.GetType("Vintagestory.Client.RuntimeStats");
            runtimeStatsChunksAwaitingTesselationField ??= runtimeStatsType?.GetField("chunksAwaitingTesselation", BindingFlags.Public | BindingFlags.Static);
            runtimeStatsChunksAwaitingPoolingField ??= runtimeStatsType?.GetField("chunksAwaitingPooling", BindingFlags.Public | BindingFlags.Static);

            tesselatedChunkPositionXField ??= AccessTools.Field(typeof(TesselatedChunk), "positionX");
            tesselatedChunkPositionYAndDimensionField ??= AccessTools.Field(typeof(TesselatedChunk), "positionYAndDimension");
            tesselatedChunkPositionZField ??= AccessTools.Field(typeof(TesselatedChunk), "positionZ");
            tesselatedChunkVerticesCountField ??= AccessTools.Field(typeof(TesselatedChunk), "VerticesCount");
            tesselatedChunkChunkField ??= AccessTools.Field(typeof(TesselatedChunk), "chunk");
            tesselatedChunkRecalcPriorityMethod ??= AccessTools.Method(typeof(TesselatedChunk), "RecalcPriority");
            tesselatedChunkUnusedDisposeMethod ??= AccessTools.Method(typeof(TesselatedChunk), "UnusedDispose");

            if (tesselatedChunkChunkField != null)
            {
                clientChunkQueuedForUploadField ??= AccessTools.Field(tesselatedChunkChunkField.FieldType, "queuedForUpload");
            }

            if (game.WorldMap != null)
            {
                worldMapGetChunkAtBlockPosMethod ??= AccessTools.Method(game.WorldMap.GetType(), "GetChunkAtBlockPos", new[] { typeof(int), typeof(int), typeof(int) });
            }

            object chunkRenderer = chunkRendererField?.GetValue(game);
            if (chunkRenderer != null)
            {
                chunkRendererAddTesselatedChunkMethod ??= AccessTools.Method(chunkRenderer.GetType(), "AddTesselatedChunk");
            }

            if (game.eventManager != null)
            {
                triggerChunkRetesselatedMethod ??= AccessTools.Method(game.eventManager.GetType(), "TriggerChunkRetesselated");
            }
        }

        private static void SetQueuedForUpload(TesselatedChunk tesschunk, bool value)
        {
            if (tesschunk == null || tesselatedChunkChunkField == null || clientChunkQueuedForUploadField == null)
                return;

            object chunk = tesselatedChunkChunkField.GetValue(tesschunk);
            if (chunk != null)
            {
                clientChunkQueuedForUploadField.SetValue(chunk, value);
            }
        }

        private static int GetIntFieldValue(FieldInfo field, object instance)
        {
            if (field == null || instance == null)
                return 0;

            return (int)field.GetValue(instance);
        }

        private static void ResolveCountAccess(Type queueType, out PropertyInfo countProperty, out FieldInfo countField)
        {
            countProperty = null;
            countField = null;

            if (queueType == null)
                return;

            countProperty = queueType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (countProperty == null)
            {
                countField = AccessTools.Field(queueType, "Count");
            }
        }

        private static int GetAdaptiveMultiplier(object managerInstance)
        {
            try
            {
                if (managerInstance == null)
                    return 3;

                EnsureQueueReflection(managerInstance.GetType());
                if (tessChunksQueueField == null)
                    return 3;

                int queueSize;
                int baseMultiplier;
                var queue = tessChunksQueueField.GetValue(managerInstance);
                var prioQueue = tessChunksQueuePriorityField?.GetValue(managerInstance);

                if (queue == null)
                    return 3;

                int queueCount = GetQueueCount(queue, tessChunksQueueCountProperty, tessChunksQueueCountField);
                int prioQueueCount = prioQueue == null ? 0 : GetQueueCount(prioQueue, tessChunksQueuePriorityCountProperty, tessChunksQueuePriorityCountField);
                queueSize = queueCount + prioQueueCount;

                if (queueSize >= QUEUE_THRESHOLD_HIGH)
                {
                    // Extreme upload backlog: throttle to avoid saturating render thread
                    baseMultiplier = 1;
                }
                else if (queueSize >= QUEUE_THRESHOLD_LOW)
                {
                    // Heavy backlog: modest throttle to stabilize frame pacing
                    baseMultiplier = 2;
                }
                else if (queueSize >= 50)
                {
                    // Moderate backlog: boost uploads to drain queue faster
                    baseMultiplier = 4;
                }
                else
                {
                    // Light backlog: vanilla behavior
                    baseMultiplier = 3;
                }

                int adaptiveMultiplier = baseMultiplier;
                float avgFrameMs = GetAverageFrametimeMs();
                if (avgFrameMs >= MIN_FRAMETIME_FOR_PRESSURE)
                {
                    float targetFrameMs = GetTargetFrameMs();
                    if (targetFrameMs > 0f)
                    {
                        float pressureRatio = avgFrameMs / targetFrameMs;
                        if (pressureRatio > 1.7f)
                            adaptiveMultiplier = Math.Max(1, Math.Max(1, (int)MathF.Round(baseMultiplier * 0.55f)));
                        else if (pressureRatio > 1.3f)
                            adaptiveMultiplier = Math.Max(1, Math.Max(1, (int)MathF.Round(baseMultiplier * 0.7f)));
                        else if (pressureRatio > 1.15f)
                            adaptiveMultiplier = Math.Max(1, Math.Max(1, (int)MathF.Round(baseMultiplier * 0.85f)));
                        else if (pressureRatio > 1.05f)
                            adaptiveMultiplier = Math.Max(1, Math.Max(1, (int)MathF.Round(baseMultiplier * 0.95f)));
                    }
                }

                adaptiveMultiplier = Math.Min(baseMultiplier, Math.Max(1, adaptiveMultiplier));

                if (logger != null && queueSize > 0 && avgFrameMs > 0f && frameCounter++ % 250 == 0)
                {
                    string reason = queueSize >= QUEUE_THRESHOLD_HIGH ? "EXTREME backlog" :
                        queueSize >= QUEUE_THRESHOLD_LOW ? "HEAVY backlog" :
                        queueSize >= 50 ? "MODERATE backlog (BOOST)" : "light backlog";
                    logger($"[OptiTime] ChunkTess: Queue={queueSize}, Base={baseMultiplier}, Adaptive={adaptiveMultiplier}, AvgFrame={avgFrameMs:0.00}ms ({reason})");
                }

                if (ProfilingHelper.Enabled)
                {
                    ProfilingHelper.Mark("opt-chunktess", $"queue={queueSize},base={baseMultiplier},mult={adaptiveMultiplier},avgFrame={avgFrameMs:0.00}");
                }

                return adaptiveMultiplier;
            }
            catch
            {
                return 3;
            }
        }

        private static int GetQueueCount(object queue, PropertyInfo countProperty, FieldInfo countField)
        {
            if (queue == null)
                return 0;

            try
            {
                if (countProperty != null)
                    return (int)countProperty.GetValue(queue);

                if (countField != null)
                    return (int)countField.GetValue(queue);

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static float GetAverageFrametimeMs()
        {
            if (frametimeHistoryCount <= 0)
                return 0f;

            return frametimeHistorySumMs / frametimeHistoryCount;
        }

        private static float GetTargetFrameMs()
        {
            try
            {
                int maxFps = ClientSettings.MaxFPS;
                if (maxFps <= 0)
                    return BaseFrameMs;

                return 1000f / maxFps;
            }
            catch
            {
                return BaseFrameMs;
            }
        }

        public static IEnumerable<CodeInstruction> TranspileTesselationThrottle(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getMultiplierMethod = AccessTools.Method(
                typeof(ChunkTesselationOptimization),
                nameof(GetAdaptiveMultiplier),
                new System.Type[] { typeof(object) });

            if (getMultiplierMethod == null)
            {
                logger?.Invoke("[OptiTime] ChunkTesselation: GetAdaptiveMultiplier not found. Leaving vanilla upload formula unchanged.");
                return codes;
            }

            var uploadRateLimiterField = AccessTools.Field(typeof(ClientSettings), nameof(ClientSettings.ChunkVerticesUploadRateLimiter));
            int multiplierIndex = FindUploadMultiplierIndex(codes, uploadRateLimiterField);
            if (multiplierIndex < 0)
            {
                logger?.Invoke("[OptiTime] ChunkTesselation: Adaptive upload multiplier anchor not found in OnBeforeFrame. Leaving vanilla upload formula unchanged.");
                return codes;
            }

            var replacement = new CodeInstruction(OpCodes.Ldarg_0);
            replacement.labels.AddRange(codes[multiplierIndex].labels);
            replacement.blocks.AddRange(codes[multiplierIndex].blocks);
            codes[multiplierIndex] = replacement;
            codes.Insert(multiplierIndex + 1, new CodeInstruction(OpCodes.Call, getMultiplierMethod));

            logger?.Invoke($"[OptiTime] ChunkTesselation: Adaptive upload multiplier injected at IL index {multiplierIndex}.");
            return codes;
        }

        private static int FindUploadMultiplierIndex(List<CodeInstruction> codes, FieldInfo uploadRateLimiterField)
        {
            if (codes == null || uploadRateLimiterField == null)
                return -1;

            for (int i = 0; i < codes.Count; i++)
            {
                if (!LoadsInt32(codes[i], 3))
                    continue;

                int rateLimiterIndex = FindInstructionIndex(codes, i + 1, 8,
                    code => code.opcode == OpCodes.Ldsfld && Equals(code.operand, uploadRateLimiterField));
                if (rateLimiterIndex < 0)
                    continue;

                int shlIndex = FindInstructionIndex(codes, rateLimiterIndex + 1, 4, code => code.opcode == OpCodes.Shl);
                int divIndex = FindInstructionIndex(codes, shlIndex + 1, 4, code => code.opcode == OpCodes.Div || code.opcode == OpCodes.Div_Un);
                int addIndex = FindInstructionIndex(codes, divIndex + 1, 4, code => code.opcode == OpCodes.Add);
                int mulIndex = FindInstructionIndex(codes, addIndex + 1, 4, code => code.opcode == OpCodes.Mul);

                if (shlIndex >= 0 && divIndex >= 0 && addIndex >= 0 && mulIndex >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindInstructionIndex(List<CodeInstruction> codes, int startIndex, int maxDistance, Func<CodeInstruction, bool> predicate)
        {
            if (codes == null || predicate == null || startIndex < 0)
                return -1;

            int endExclusive = Math.Min(codes.Count, startIndex + Math.Max(0, maxDistance));
            for (int i = startIndex; i < endExclusive; i++)
            {
                if (predicate(codes[i]))
                    return i;
            }

            return -1;
        }

        private static bool LoadsInt32(CodeInstruction instruction, int value)
        {
            if (instruction == null)
                return false;

            if (value == 3 && instruction.opcode == OpCodes.Ldc_I4_3)
                return true;

            if (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte sbyteValue)
                return sbyteValue == value;

            if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int intValue)
                return intValue == value;

            return false;
        }
    }
}
