using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Eliminates verified per-frame managed heap allocations on the client render thread:
    /// - ClientMain.GlTranslate(double, double, double): scalar matrix translation without new double[3].
    /// - ClientMain.TriggerRenderStage(EnumRenderStage, float): cached stage error strings without enum boxing/concat.
    /// - FrustumCulling.CalcFrustumEquations(BlockPos, double[], double[]): scratch double[16] instead of Mat4d.Create().
    /// - AnimationManager.OnClientFrame: zero-allocation Dictionary.Values scan instead of LINQ Any().
    /// - ClientPlatformWindows.ClearFrameBuffer / LoadFrameBuffer(Transparent) / RenderPostprocessingEffects / RenderFinalComposition: static clear/draw-buffer arrays.
    /// - EntityShapeRenderer.loadModelMatrix: reusable scratch quaternion and scalar uniform scale without new float[3].
    /// </summary>
    public static class RenderLoopAllocationOptimization
    {
        private static readonly float[] ClearBlackOpaque = { 0f, 0f, 0f, 1f };
        private static readonly float[] ClearAllZero = { 0f, 0f, 0f, 0f };
        private static readonly float[] ClearRedOne = { 1f, 0f, 0f, 0f };
        private static readonly float[] ClearAllOne = { 1f, 1f, 1f, 1f };
        private static readonly DrawBuffersEnum[] SingleColorAttachment0 = { DrawBuffersEnum.ColorAttachment0 };
        private static readonly DrawBuffersEnum[] TwoColorAttachments =
        {
            DrawBuffersEnum.ColorAttachment0,
            DrawBuffersEnum.ColorAttachment1
        };
        private static readonly DrawBuffersEnum[] ThreeColorAttachments =
        {
            DrawBuffersEnum.ColorAttachment0,
            DrawBuffersEnum.ColorAttachment1,
            DrawBuffersEnum.ColorAttachment2
        };
        private static readonly DrawBuffersEnum[] FourColorAttachments =
        {
            DrawBuffersEnum.ColorAttachment0,
            DrawBuffersEnum.ColorAttachment1,
            DrawBuffersEnum.ColorAttachment2,
            DrawBuffersEnum.ColorAttachment3
        };

        private static readonly string[] AfterStageErrorLabels = BuildAfterStageErrorLabels();

        [ThreadStatic]
        private static double[] scratchQuaternion;

        [ThreadStatic]
        private static double[] scratchFrustumMatrix;

        private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> renderSsaoRef;
        private static readonly bool platformAccessorsReady;

        private static readonly MethodInfo QuaternionCreateMethod =
            AccessTools.Method(typeof(Quaterniond), nameof(Quaterniond.Create), Type.EmptyTypes);
        private static readonly MethodInfo Mat4dCreateMethod =
            AccessTools.Method(typeof(Mat4d), nameof(Mat4d.Create), Type.EmptyTypes);
        private static readonly MethodInfo Mat4fScaleArrayMethod =
            AccessTools.Method(typeof(Mat4f), nameof(Mat4f.Scale), new[] { typeof(float[]), typeof(float[]), typeof(float[]) });
        private static readonly MethodInfo ScratchQuaternionMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(GetResetScratchQuaternion));
        private static readonly MethodInfo ScratchFrustumMatrixMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(GetScratchFrustumMatrix));
        private static readonly MethodInfo AfterStageErrorLabelMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(GetAfterStageErrorLabel));
        private static readonly MethodInfo ScaleUniformMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(ScaleUniform));
        private static readonly MethodInfo HasAdjustCollisionBoxMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(HasAdjustCollisionBox));
        private static readonly FieldInfo ClearAllOneField =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(ClearAllOne));
        private static readonly FieldInfo SingleColorAttachment0Field =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(SingleColorAttachment0));
        private static readonly FieldInfo TwoColorAttachmentsField =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(TwoColorAttachments));
        private static readonly FieldInfo ThreeColorAttachmentsField =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(ThreeColorAttachments));
        private static readonly FieldInfo FourColorAttachmentsField =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(FourColorAttachments));

        private static volatile bool enabled;

        static RenderLoopAllocationOptimization()
        {
            try
            {
                renderSsaoRef = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderSSAO");
                platformAccessorsReady = true;
            }
            catch
            {
                platformAccessorsReady = false;
            }
        }

        public static bool IsEnabled => enabled;

        public static void Configure(bool isEnabled)
        {
            enabled = isEnabled;
        }

        public static void Cleanup()
        {
            enabled = false;
        }

        private static string[] BuildAfterStageErrorLabels()
        {
            Array values = Enum.GetValues(typeof(EnumRenderStage));
            int max = 0;
            foreach (object v in values)
            {
                int iv = (int)v;
                if (iv > max)
                {
                    max = iv;
                }
            }

            string[] labels = new string[max + 1];
            foreach (object v in values)
            {
                int iv = (int)v;
                if (iv >= 0 && iv < labels.Length)
                {
                    labels[iv] = "After render stage " + v.ToString();
                }
            }

            return labels;
        }

        public static string GetAfterStageErrorLabel(EnumRenderStage stage)
        {
            int idx = (int)stage;
            if ((uint)idx < (uint)AfterStageErrorLabels.Length)
            {
                string cached = AfterStageErrorLabels[idx];
                if (cached != null)
                {
                    return cached;
                }
            }

            return "After render stage " + stage;
        }

        public static double[] GetScratchFrustumMatrix()
        {
            double[] m = scratchFrustumMatrix;
            if (m == null)
            {
                m = new double[16];
                scratchFrustumMatrix = m;
            }

            return m;
        }

        /// <summary>
        /// Replaces `"After render stage " + stage` inside ClientMain.TriggerRenderStage with a
        /// cached label lookup, avoiding enum boxing and string allocation without blocking other mods' prefixes.
        /// </summary>
        public static IEnumerable<CodeInstruction> TriggerRenderStage_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (AfterStageErrorLabelMethod == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldstr || !Equals(list[i].operand, "After render stage "))
                {
                    continue;
                }

                int concatIdx = -1;
                for (int j = i + 1; j < Math.Min(list.Count, i + 6); j++)
                {
                    if (list[j].opcode == OpCodes.Call &&
                        list[j].operand is MethodInfo mi &&
                        mi.DeclaringType == typeof(string) &&
                        mi.Name == nameof(string.Concat))
                    {
                        concatIdx = j;
                        break;
                    }
                }

                if (concatIdx < 0)
                {
                    continue;
                }

                var loadStage = new CodeInstruction(OpCodes.Ldarg_1);
                loadStage.labels.AddRange(list[i].labels);
                var callCached = new CodeInstruction(OpCodes.Call, AfterStageErrorLabelMethod);
                list.RemoveRange(i, concatIdx - i + 1);
                list.Insert(i, loadStage);
                list.Insert(i + 1, callCached);
                break;
            }

            return list;
        }

        /// <summary>
        /// Replaces Mat4d.Create() inside FrustumCulling.CalcFrustumEquations(BlockPos, double[], double[])
        /// with a render-thread scratch double[16], preserving full composability with other mods.
        /// </summary>
        public static IEnumerable<CodeInstruction> CalcFrustumEquations_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (Mat4dCreateMethod == null || ScratchFrustumMatrixMethod == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(Mat4dCreateMethod))
                {
                    list[i].operand = ScratchFrustumMatrixMethod;
                }
            }

            return list;
        }

        /// <summary>
        /// Zero-allocation replacement for ClientMain.GlTranslate(double, double, double).
        /// Matches Mat4d.Translate(array, array, new double[] { x, y, z }) algebraically.
        /// </summary>
        public static bool GlTranslate_Prefix(ClientMain __instance, double x, double y, double z)
        {
            if (!enabled || __instance == null)
            {
                return true;
            }

            double[] m = __instance.CurrentMatrixModeProjection ? __instance.PMatrix.Top : __instance.MvMatrix.Top;
            if (m == null || m.Length < 16)
            {
                return true;
            }

            m[12] = m[0] * x + m[4] * y + m[8] * z + m[12];
            m[13] = m[1] * x + m[5] * y + m[9] * z + m[13];
            m[14] = m[2] * x + m[6] * y + m[10] * z + m[14];
            m[15] = m[3] * x + m[7] * y + m[11] * z + m[15];
            return false;
        }

        /// <summary>
        /// Zero-allocation replacement for ClientPlatformWindows.ClearFrameBuffer(EnumFrameBuffer)
        /// for Primary, Transparent, LiquidDepth, ShadowmapFar, and ShadowmapNear.
        /// </summary>
        public static bool ClearFrameBuffer_Prefix(ClientPlatformWindows __instance, EnumFrameBuffer framebuffer)
        {
            if (!enabled || !platformAccessorsReady || __instance == null)
            {
                return true;
            }

            switch (framebuffer)
            {
                case EnumFrameBuffer.Primary:
                {
                    GL.ClearBuffer(ClearBuffer.Color, 0, ClearBlackOpaque);
                    GL.ClearBuffer(ClearBuffer.Color, 1, ClearBlackOpaque);
                    if (renderSsaoRef(__instance))
                    {
                        GL.ClearBuffer(ClearBuffer.Color, 2, ClearBlackOpaque);
                        GL.ClearBuffer(ClearBuffer.Color, 3, ClearBlackOpaque);
                    }

                    float depth = 1f;
                    GL.ClearBuffer(ClearBuffer.Depth, 0, ref depth);
                    return false;
                }
                case EnumFrameBuffer.LiquidDepth:
                case EnumFrameBuffer.ShadowmapFar:
                case EnumFrameBuffer.ShadowmapNear:
                {
                    List<FrameBufferRef> buffers = __instance.FrameBuffers;
                    int idx = (int)framebuffer;
                    if (buffers == null || idx < 0 || idx >= buffers.Count || buffers[idx] == null)
                    {
                        return true;
                    }

                    FrameBufferRef fb = buffers[idx];
                    float depth = 1f;
                    GL.Viewport(0, 0, fb.Width, fb.Height);
                    GL.ClearBuffer(ClearBuffer.Depth, 0, ref depth);
                    return false;
                }
                case EnumFrameBuffer.Transparent:
                {
                    GL.ClearBuffer(ClearBuffer.Color, 0, ClearAllZero);
                    GL.ClearBuffer(ClearBuffer.Color, 1, ClearRedOne);
                    GL.ClearBuffer(ClearBuffer.Color, 2, ClearAllZero);
                    return false;
                }
                default:
                    return true;
            }
        }

        public static bool HasAdjustCollisionBox(Dictionary<string, AnimationMetaData> activeAnimations)
        {
            if (activeAnimations == null || activeAnimations.Count == 0)
            {
                return false;
            }

            foreach (AnimationMetaData meta in activeAnimations.Values)
            {
                if (meta != null && meta.AdjustCollisionBox)
                {
                    return true;
                }
            }

            return false;
        }

        public static double[] GetResetScratchQuaternion()
        {
            double[] q = scratchQuaternion;
            if (q == null)
            {
                q = new double[4];
                scratchQuaternion = q;
            }

            q[0] = 0.0;
            q[1] = 0.0;
            q[2] = 0.0;
            q[3] = 1.0;
            return q;
        }

        public static float[] ScaleUniform(float[] output, float[] a, float s)
        {
            output[0] = a[0] * s;
            output[1] = a[1] * s;
            output[2] = a[2] * s;
            output[3] = a[3] * s;
            output[4] = a[4] * s;
            output[5] = a[5] * s;
            output[6] = a[6] * s;
            output[7] = a[7] * s;
            output[8] = a[8] * s;
            output[9] = a[9] * s;
            output[10] = a[10] * s;
            output[11] = a[11] * s;
            output[12] = a[12];
            output[13] = a[13];
            output[14] = a[14];
            output[15] = a[15];
            return output;
        }

        /// <summary>
        /// Replaces ActiveAnimationsByAnimCode.Any(anim => anim.Value.AdjustCollisionBox) in
        /// AnimationManager.OnClientFrame with a zero-allocation Dictionary.Values loop.
        /// </summary>
        public static IEnumerable<CodeInstruction> AnimationManagerOnClientFrame_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (HasAdjustCollisionBoxMethod == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Call || !(list[i].operand is MethodInfo mi) ||
                    mi.DeclaringType != typeof(Enumerable) || mi.Name != nameof(Enumerable.Any) ||
                    mi.GetParameters().Length != 2)
                {
                    continue;
                }

                int dictLoadIdx = -1;
                for (int j = i - 1; j >= Math.Max(0, i - 18); j--)
                {
                    if (list[j].operand is FieldInfo fi && fi.Name == "ActiveAnimationsByAnimCode")
                    {
                        dictLoadIdx = j;
                        break;
                    }
                }

                if (dictLoadIdx < 0)
                {
                    continue;
                }

                var replacement = new CodeInstruction(OpCodes.Call, HasAdjustCollisionBoxMethod);
                int removeCount = i - dictLoadIdx;
                list.RemoveRange(dictLoadIdx + 1, removeCount);
                list.Insert(dictLoadIdx + 1, replacement);
                break;
            }

            return list;
        }

        /// <summary>
        /// Replaces Quaterniond.Create() and `Mat4f.Scale(ModelMat, ModelMat, new float[3] { size, size, size })`
        /// inside EntityShapeRenderer.loadModelMatrix with zero-allocation helpers.
        /// </summary>
        public static IEnumerable<CodeInstruction> LoadModelMatrix_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);

            if (QuaternionCreateMethod != null && ScratchQuaternionMethod != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Calls(QuaternionCreateMethod))
                    {
                        list[i].operand = ScratchQuaternionMethod;
                    }
                }
            }

            if (Mat4fScaleArrayMethod != null && ScaleUniformMethod != null)
            {
                for (int i = 0; i <= list.Count - 15; i++)
                {
                    if (list[i].opcode == OpCodes.Ldc_I4_3 &&
                        list[i + 1].opcode == OpCodes.Newarr &&
                        Equals(list[i + 1].operand, typeof(float)) &&
                        list[i + 2].opcode == OpCodes.Dup &&
                        list[i + 3].opcode == OpCodes.Ldc_I4_0 &&
                        list[i + 5].opcode == OpCodes.Stelem_R4 &&
                        list[i + 6].opcode == OpCodes.Dup &&
                        list[i + 7].opcode == OpCodes.Ldc_I4_1 &&
                        list[i + 9].opcode == OpCodes.Stelem_R4 &&
                        list[i + 10].opcode == OpCodes.Dup &&
                        list[i + 11].opcode == OpCodes.Ldc_I4_2 &&
                        list[i + 13].opcode == OpCodes.Stelem_R4 &&
                        list[i + 14].Calls(Mat4fScaleArrayMethod) &&
                        SameLocalLoad(list[i + 4], list[i + 8]) &&
                        SameLocalLoad(list[i + 4], list[i + 12]))
                    {
                        var loadScale = new CodeInstruction(list[i + 4].opcode, list[i + 4].operand);
                        loadScale.labels.AddRange(list[i].labels);
                        var callScale = new CodeInstruction(OpCodes.Call, ScaleUniformMethod);
                        list.RemoveRange(i, 15);
                        list.Insert(i, loadScale);
                        list.Insert(i + 1, callScale);
                        break;
                    }
                }
            }

            return list;
        }

        private static bool SameLocalLoad(CodeInstruction a, CodeInstruction b)
        {
            return a.opcode == b.opcode && Equals(a.operand, b.operand);
        }

        /// <summary>
        /// Replaces the per-frame `new float[4] { 1f, 1f, 1f, 1f }` passed to GL.ClearBuffer in
        /// ClientPlatformWindows.RenderPostprocessingEffects with a static readonly array.
        /// </summary>
        public static IEnumerable<CodeInstruction> RenderPostprocessingEffectsClear_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (ClearAllOneField == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count - 4; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_4 &&
                    list[i + 1].opcode == OpCodes.Newarr &&
                    Equals(list[i + 1].operand, typeof(float)) &&
                    list[i + 2].opcode == OpCodes.Dup &&
                    list[i + 3].opcode == OpCodes.Ldtoken &&
                    list[i + 4].opcode == OpCodes.Call &&
                    list[i + 4].operand is MethodInfo initMi &&
                    initMi.Name == "InitializeArray")
                {
                    var loadStatic = new CodeInstruction(OpCodes.Ldsfld, ClearAllOneField);
                    loadStatic.labels.AddRange(list[i].labels);
                    list.RemoveRange(i, 5);
                    list.Insert(i, loadStatic);
                    break;
                }
            }

            return list;
        }

        /// <summary>
        /// Replaces the per-frame `new DrawBuffersEnum[3] { CA0, CA1, CA2 }` in
        /// ClientPlatformWindows.LoadFrameBuffer(EnumFrameBuffer.Transparent) with a static readonly array.
        /// </summary>
        public static IEnumerable<CodeInstruction> LoadFrameBufferTransparent_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (ThreeColorAttachmentsField == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count - 4; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_3 &&
                    list[i + 1].opcode == OpCodes.Newarr &&
                    Equals(list[i + 1].operand, typeof(DrawBuffersEnum)) &&
                    list[i + 2].opcode == OpCodes.Dup &&
                    list[i + 3].opcode == OpCodes.Ldtoken &&
                    list[i + 4].opcode == OpCodes.Call &&
                    list[i + 4].operand is MethodInfo initMi &&
                    initMi.Name == "InitializeArray")
                {
                    var loadStatic = new CodeInstruction(OpCodes.Ldsfld, ThreeColorAttachmentsField);
                    loadStatic.labels.AddRange(list[i].labels);
                    list.RemoveRange(i, 5);
                    list.Insert(i, loadStatic);
                    break;
                }
            }

            return list;
        }

        /// <summary>
        /// Replaces all three per-frame DrawBuffersEnum[] allocations ([1], [4], [2]) in
        /// ClientPlatformWindows.RenderFinalComposition with static readonly arrays.
        /// </summary>
        public static IEnumerable<CodeInstruction> RenderFinalComposition_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (SingleColorAttachment0Field == null || TwoColorAttachmentsField == null || FourColorAttachmentsField == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                // Pattern 1: new DrawBuffersEnum[1] { ColorAttachment0 } (6 instructions)
                if (i <= list.Count - 6 &&
                    list[i].opcode == OpCodes.Ldc_I4_1 &&
                    list[i + 1].opcode == OpCodes.Newarr &&
                    Equals(list[i + 1].operand, typeof(DrawBuffersEnum)) &&
                    list[i + 2].opcode == OpCodes.Dup &&
                    list[i + 3].opcode == OpCodes.Ldc_I4_0 &&
                    list[i + 4].opcode == OpCodes.Ldc_I4 &&
                    Equals(list[i + 4].operand, (int)DrawBuffersEnum.ColorAttachment0) &&
                    list[i + 5].opcode == OpCodes.Stelem_I4)
                {
                    var loadStatic = new CodeInstruction(OpCodes.Ldsfld, SingleColorAttachment0Field);
                    loadStatic.labels.AddRange(list[i].labels);
                    list.RemoveRange(i, 6);
                    list.Insert(i, loadStatic);
                    continue;
                }

                // Pattern 2: new DrawBuffersEnum[4] via InitializeArray (5 instructions)
                if (i <= list.Count - 5 &&
                    list[i].opcode == OpCodes.Ldc_I4_4 &&
                    list[i + 1].opcode == OpCodes.Newarr &&
                    Equals(list[i + 1].operand, typeof(DrawBuffersEnum)) &&
                    list[i + 2].opcode == OpCodes.Dup &&
                    list[i + 3].opcode == OpCodes.Ldtoken &&
                    list[i + 4].opcode == OpCodes.Call &&
                    list[i + 4].operand is MethodInfo initMi &&
                    initMi.Name == "InitializeArray")
                {
                    var loadStatic = new CodeInstruction(OpCodes.Ldsfld, FourColorAttachmentsField);
                    loadStatic.labels.AddRange(list[i].labels);
                    list.RemoveRange(i, 5);
                    list.Insert(i, loadStatic);
                    continue;
                }

                // Pattern 3: new DrawBuffersEnum[2] { ColorAttachment0, ColorAttachment1 } (10 instructions)
                if (i <= list.Count - 10 &&
                    list[i].opcode == OpCodes.Ldc_I4_2 &&
                    list[i + 1].opcode == OpCodes.Newarr &&
                    Equals(list[i + 1].operand, typeof(DrawBuffersEnum)) &&
                    list[i + 2].opcode == OpCodes.Dup &&
                    list[i + 3].opcode == OpCodes.Ldc_I4_0 &&
                    list[i + 4].opcode == OpCodes.Ldc_I4 &&
                    Equals(list[i + 4].operand, (int)DrawBuffersEnum.ColorAttachment0) &&
                    list[i + 5].opcode == OpCodes.Stelem_I4 &&
                    list[i + 6].opcode == OpCodes.Dup &&
                    list[i + 7].opcode == OpCodes.Ldc_I4_1 &&
                    list[i + 8].opcode == OpCodes.Ldc_I4 &&
                    Equals(list[i + 8].operand, (int)DrawBuffersEnum.ColorAttachment1) &&
                    list[i + 9].opcode == OpCodes.Stelem_I4)
                {
                    var loadStatic = new CodeInstruction(OpCodes.Ldsfld, TwoColorAttachmentsField);
                    loadStatic.labels.AddRange(list[i].labels);
                    list.RemoveRange(i, 10);
                    list.Insert(i, loadStatic);
                }
            }

            return list;
        }
    }
}
