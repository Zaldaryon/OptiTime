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
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Eliminates verified per-frame managed heap allocations on the client render thread:
    /// - ClientMain.GlTranslate(double, double, double): scalar matrix translation without new double[3].
    /// - AnimationManager.OnClientFrame: zero-allocation Dictionary.Values scan instead of LINQ Any().
    /// - ClientPlatformWindows.ClearFrameBuffer(EnumFrameBuffer): cached static clear-color arrays.
    /// - ClientPlatformWindows.RenderPostprocessingEffects &amp; RenderFinalComposition: static clear/draw-buffer arrays.
    /// - EntityShapeRenderer.loadModelMatrix: reusable render-thread scratch quaternion instead of Quaterniond.Create().
    /// </summary>
    public static class RenderLoopAllocationOptimization
    {
        private static readonly float[] ClearBlackOpaque = { 0f, 0f, 0f, 1f };
        private static readonly float[] ClearAllZero = { 0f, 0f, 0f, 0f };
        private static readonly float[] ClearRedOne = { 1f, 0f, 0f, 0f };
        private static readonly float[] ClearAllOne = { 1f, 1f, 1f, 1f };
        private static readonly DrawBuffersEnum[] SingleColorAttachment0 = { DrawBuffersEnum.ColorAttachment0 };

        [ThreadStatic]
        private static double[] scratchQuaternion;

        private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> renderSsaoRef;
        private static readonly bool platformAccessorsReady;

        private static readonly MethodInfo QuaternionCreateMethod =
            AccessTools.Method(typeof(Quaterniond), nameof(Quaterniond.Create), Type.EmptyTypes);
        private static readonly MethodInfo ScratchQuaternionMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(GetResetScratchQuaternion));
        private static readonly MethodInfo HasAdjustCollisionBoxMethod =
            AccessTools.Method(typeof(RenderLoopAllocationOptimization), nameof(HasAdjustCollisionBox));
        private static readonly FieldInfo ClearAllOneField =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(ClearAllOne));
        private static readonly FieldInfo SingleColorAttachment0Field =
            AccessTools.Field(typeof(RenderLoopAllocationOptimization), nameof(SingleColorAttachment0));

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

            // Look for: call Enumerable.Any<KeyValuePair<string, AnimationMetaData>>(source, predicate)
            // preceded by loading the cached predicate delegate (ldsfld ...; dup; brtrue ...; pop; ldnull; ldftn; newobj; dup; stsfld).
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Call || !(list[i].operand is MethodInfo mi) ||
                    mi.DeclaringType != typeof(Enumerable) || mi.Name != nameof(Enumerable.Any) ||
                    mi.GetParameters().Length != 2)
                {
                    continue;
                }

                // Find the preceding ActiveAnimationsByAnimCode field load within 16 instructions.
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

                // Replace everything between dictLoadIdx+1 and i (inclusive) with a direct call to HasAdjustCollisionBox.
                var replacement = new CodeInstruction(OpCodes.Call, HasAdjustCollisionBoxMethod);
                int removeCount = i - dictLoadIdx;
                list.RemoveRange(dictLoadIdx + 1, removeCount);
                list.Insert(dictLoadIdx + 1, replacement);
                break;
            }

            return list;
        }

        /// <summary>
        /// Replaces Quaterniond.Create() inside EntityShapeRenderer.loadModelMatrix with a
        /// render-thread scratch quaternion, avoiding 56 B/call across Opaque + ShadowFar + ShadowNear.
        /// </summary>
        public static IEnumerable<CodeInstruction> LoadModelMatrix_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (QuaternionCreateMethod == null || ScratchQuaternionMethod == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(QuaternionCreateMethod))
                {
                    list[i].operand = ScratchQuaternionMethod;
                }
            }

            return list;
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
        /// Replaces the per-frame `new DrawBuffersEnum[1] { ColorAttachment0 }` in
        /// ClientPlatformWindows.RenderFinalComposition with a static readonly array.
        /// </summary>
        public static IEnumerable<CodeInstruction> RenderFinalComposition_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            if (SingleColorAttachment0Field == null)
            {
                return list;
            }

            for (int i = 0; i < list.Count - 5; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_1 &&
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
                    break;
                }
            }

            return list;
        }
    }
}
