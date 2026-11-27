using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace OptiTime
{
    /// <summary>
    /// Frustum-culls off-screen non-shadow AnimatableRenderer draws using a conservative
    /// 2.5 * max(ScaleX, ScaleY, ScaleZ) bounding sphere, and parks idle AnimationUtil
    /// instances off EnumRenderStage.Opaque until StartAnimation wakes them again.
    /// </summary>
    public static class AnimatableOptimization
    {
        private static readonly AccessTools.FieldRef<AnimatableRenderer, Vec3d> rendererPosRef;
        private static readonly AccessTools.FieldRef<AnimatableRenderer, ICoreClientAPI> rendererCapiRef;
        private static readonly AccessTools.FieldRef<AnimationUtil, ICoreClientAPI> utilCapiRef;
        private static readonly AccessTools.FieldRef<AnimationUtil, bool> utilStopTriggeredRef;
        private static readonly bool accessorsReady;

        private static volatile bool clientActive;
        private static ConditionalWeakTable<AnimationUtil, ParkState> parkStates =
            new ConditionalWeakTable<AnimationUtil, ParkState>();

        static AnimatableOptimization()
        {
            try
            {
                rendererPosRef = AccessTools.FieldRefAccess<AnimatableRenderer, Vec3d>("pos");
                rendererCapiRef = AccessTools.FieldRefAccess<AnimatableRenderer, ICoreClientAPI>("capi");
                utilCapiRef = AccessTools.FieldRefAccess<AnimationUtil, ICoreClientAPI>("capi");
                utilStopTriggeredRef = AccessTools.FieldRefAccess<AnimationUtil, bool>("stopRenderTriggered");
                accessorsReady = true;
            }
            catch
            {
                accessorsReady = false;
            }
        }

        public static void SetClientActive(bool active)
        {
            clientActive = active;
        }

        public static void Cleanup()
        {
            clientActive = false;
            parkStates = new ConditionalWeakTable<AnimationUtil, ParkState>();
        }

        internal static bool IsParked(AnimationUtil util)
        {
            return util != null && parkStates.TryGetValue(util, out ParkState state) && state.isParked;
        }

        public static bool AnimatableRenderer_OnRenderFrame_Prefix(AnimatableRenderer __instance, EnumRenderStage stage)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return true;
            }

            if (!__instance.ShouldRender)
            {
                return false;
            }

            // Never frustum-cull shadow passes so off-camera blocks still cast shadows into view.
            if (stage == EnumRenderStage.ShadowFar || stage == EnumRenderStage.ShadowNear)
            {
                return true;
            }

            ICoreClientAPI capi = rendererCapiRef(__instance);
            Vec3d pos = rendererPosRef(__instance);
            if (capi == null || pos == null)
            {
                return true;
            }

            FrustumCulling culler = capi.Render?.DefaultFrustumCuller;
            if (culler == null)
            {
                return true;
            }

            double maxScale = Math.Max(
                Math.Abs(__instance.ScaleX),
                Math.Max(Math.Abs(__instance.ScaleY), Math.Abs(__instance.ScaleZ)));
            double radius = (maxScale > 0.0 ? maxScale : 1.0) * 2.5;

            return culler.SphereInFrustum(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, radius);
        }

        public static void AnimationUtil_OnRenderFrame_Postfix(AnimationUtil __instance)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return;
            }

            ICoreClientAPI capi = utilCapiRef(__instance);
            if (capi == null || capi.Side != EnumAppSide.Client || capi.IsGamePaused)
            {
                return;
            }

            if (__instance.animator == null || __instance.renderer == null)
            {
                return;
            }

            if (__instance.activeAnimationsByAnimCode.Count != 0 || __instance.animator.ActiveAnimationCount != 0)
            {
                return;
            }

            if (__instance.renderer.ShouldRender && !utilStopTriggeredRef(__instance))
            {
                return;
            }

            ParkState state = parkStates.GetOrCreateValue(__instance);
            if (state.isDisposed || state.isParked || state.parkQueued)
            {
                return;
            }

            // Defer unregistration until after TriggerRenderStage completes so the active
            // renderer list is never mutated mid-iteration (including under F3 extendedDebugInfo).
            state.parkQueued = true;
            capi.Event.EnqueueMainThreadTask(() =>
            {
                state.parkQueued = false;
                if (state.isDisposed || state.isParked || !clientActive)
                {
                    return;
                }

                if (__instance.activeAnimationsByAnimCode.Count == 0 &&
                    (__instance.animator == null || __instance.animator.ActiveAnimationCount == 0))
                {
                    capi.Event.UnregisterRenderer(__instance, EnumRenderStage.Opaque);
                    state.isParked = true;
                }
            }, "optitimeParkAnimUtil");
        }

        public static bool AnimationUtil_StartAnimation_Prefix(AnimationUtil __instance, AnimationMetaData meta)
        {
            if (!clientActive || !accessorsReady || __instance == null || meta == null)
            {
                return true;
            }

            ICoreClientAPI capi = utilCapiRef(__instance);
            if (capi == null || capi.Side != EnumAppSide.Client)
            {
                return true;
            }

            if (parkStates.TryGetValue(__instance, out ParkState state) && !state.isDisposed)
            {
                state.parkQueued = false;
                if (state.isParked)
                {
                    state.isParked = false;
                    if (RuntimeEnv.MainThreadId == Environment.CurrentManagedThreadId)
                    {
                        capi.Event.RegisterRenderer(__instance, EnumRenderStage.Opaque, "beanimutil");
                    }
                    else
                    {
                        capi.Event.EnqueueMainThreadTask(() =>
                        {
                            if (!state.isDisposed && !state.isParked && clientActive)
                            {
                                capi.Event.RegisterRenderer(__instance, EnumRenderStage.Opaque, "beanimutil");
                            }
                        }, "optitimeWakeAnimUtil");
                    }
                }
            }

            return true;
        }

        public static void AnimationUtil_Dispose_Postfix(AnimationUtil __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (parkStates.TryGetValue(__instance, out ParkState state))
            {
                state.isDisposed = true;
                state.isParked = false;
                state.parkQueued = false;
                parkStates.Remove(__instance);
            }
        }

        private sealed class ParkState
        {
            internal bool isParked;
            internal bool parkQueued;
            internal bool isDisposed;

            public ParkState()
            {
            }
        }
    }
}
