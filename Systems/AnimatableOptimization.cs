using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace OptiTime
{
    /// <summary>
    /// Frustum-culls off-screen non-shadow AnimatableRenderer draws using a conservative
    /// mesh-derived bounding sphere (minimum radius 2.5 * max(ScaleX, ScaleY, ScaleZ)),
    /// and parks idle AnimationUtil instances along with eligible BlockEntityAnimationUtil
    /// AnimatableRenderer registrations via zero-allocation batched main-thread queues.
    /// </summary>
    public static class AnimatableOptimization
    {
        private const double MinLocalCullRadius = 2.5;
        private const string BlockEntityAnimationUtilTypeName = "Vintagestory.GameContent.BlockEntityAnimationUtil";

        private static readonly AccessTools.FieldRef<AnimatableRenderer, Vec3d> rendererPosRef;
        private static readonly AccessTools.FieldRef<AnimatableRenderer, ICoreClientAPI> rendererCapiRef;
        private static readonly AccessTools.FieldRef<AnimationUtil, ICoreClientAPI> utilCapiRef;
        private static readonly AccessTools.FieldRef<AnimationUtil, bool> utilStopTriggeredRef;
        private static readonly bool accessorsReady;

        private static readonly Type blockEntityAnimationUtilType =
            AccessTools.TypeByName(BlockEntityAnimationUtilTypeName);
        private static readonly ConcurrentDictionary<Type, bool> utilParkableTypeCache =
            new ConcurrentDictionary<Type, bool>();
        private static readonly ConcurrentDictionary<Type, bool> rendererOwnerParkableTypeCache =
            new ConcurrentDictionary<Type, bool>();

        private static readonly ConditionalWeakTable<AnimationUtil, UtilParkState> utilStates =
            new ConditionalWeakTable<AnimationUtil, UtilParkState>();
        private static readonly ConditionalWeakTable<AnimatableRenderer, RendererParkState> rendererStates =
            new ConditionalWeakTable<AnimatableRenderer, RendererParkState>();

        private static readonly Queue<AnimationUtil> pendingUtilParks = new Queue<AnimationUtil>(64);
        private static readonly Queue<AnimatableRenderer> pendingRendererParks = new Queue<AnimatableRenderer>(64);
        private static readonly Queue<AnimationUtil> pendingWakes = new Queue<AnimationUtil>(32);
        private static readonly object wakeLock = new object();

        private static volatile bool clientActive;
        private static volatile bool rendererParkingEnabled;
        private static int sessionEpoch = 1;

        private static bool utilParkScheduled;
        private static bool rendererParkScheduled;
        private static bool wakeScheduled;

        private static readonly Action drainUtilParksAction = DrainUtilParks;
        private static readonly Action drainRendererParksAction = DrainRendererParks;
        private static readonly Action drainWakesAction = DrainWakes;

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

        public static void SetRendererParkingEnabled(bool enabled)
        {
            rendererParkingEnabled = enabled;
        }

        public static void Cleanup()
        {
            clientActive = false;
            rendererParkingEnabled = false;
            Interlocked.Increment(ref sessionEpoch);

            while (pendingUtilParks.Count > 0)
            {
                AnimationUtil util = pendingUtilParks.Dequeue();
                if (utilStates.TryGetValue(util, out UtilParkState state))
                {
                    state.parkQueued = false;
                }
            }

            while (pendingRendererParks.Count > 0)
            {
                AnimatableRenderer renderer = pendingRendererParks.Dequeue();
                if (rendererStates.TryGetValue(renderer, out RendererParkState rendererState))
                {
                    rendererState.parkQueued = false;
                }
            }

            utilParkScheduled = false;
            rendererParkScheduled = false;

            lock (wakeLock)
            {
                pendingWakes.Clear();
                wakeScheduled = false;
            }
        }

        internal static bool IsParked(AnimationUtil util)
        {
            return util != null &&
                utilStates.TryGetValue(util, out UtilParkState state) &&
                Volatile.Read(ref state.epoch) == Volatile.Read(ref sessionEpoch) &&
                state.isParked;
        }

        internal static bool IsRendererParked(AnimatableRenderer renderer)
        {
            return renderer != null &&
                rendererStates.TryGetValue(renderer, out RendererParkState rendererState) &&
                Volatile.Read(ref rendererState.epoch) == Volatile.Read(ref sessionEpoch) &&
                rendererState.isParked;
        }

        public static void AnimatableRenderer_MainThreadInit_Postfix(
            AnimatableRenderer __instance,
            MeshData meshdata,
            EnumRenderStage renderStage)
        {
            if (!clientActive || __instance == null)
            {
                return;
            }

            RendererParkState rendererState = GetOrResetRendererState(__instance);
            if (Volatile.Read(ref rendererState.isDisposed))
            {
                return;
            }

            ComputeLocalBounds(meshdata, out double localCenterY, out double localRadius);
            rendererState.localCenterY = localCenterY;
            rendererState.localRadius = localRadius;
            rendererState.boundsComputed = true;

            bool hasOit = __instance.mtmeshrefTransparent != null;
            rendererState.renderStage = renderStage;
            rendererState.hasOit = hasOit;
            rendererState.stageRecorded = true;
            rendererState.canParkStages =
                __instance.GetType() == typeof(AnimatableRenderer) &&
                renderStage != EnumRenderStage.ShadowFar &&
                renderStage != EnumRenderStage.ShadowNear &&
                !(hasOit && renderStage == EnumRenderStage.OIT);
            rendererState.isParked = false;
            rendererState.parkQueued = false;
        }

        public static bool AnimatableRenderer_OnRenderFrame_Prefix(AnimatableRenderer __instance, EnumRenderStage stage)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return true;
            }

            int epoch = Volatile.Read(ref sessionEpoch);

            if (!__instance.ShouldRender)
            {
                if (rendererParkingEnabled &&
                    rendererStates.TryGetValue(__instance, out RendererParkState parkState) &&
                    Volatile.Read(ref parkState.epoch) == epoch &&
                    parkState.stageRecorded &&
                    parkState.canParkStages &&
                    !Volatile.Read(ref parkState.isDisposed) &&
                    !parkState.isParked &&
                    !parkState.parkQueued &&
                    parkState.ownerUtil != null &&
                    parkState.ownerUtil.TryGetTarget(out AnimationUtil owner))
                {
                    if (utilStates.TryGetValue(owner, out UtilParkState ownerState) &&
                        Volatile.Read(ref ownerState.epoch) == epoch &&
                        ownerState.isParked &&
                        !Volatile.Read(ref ownerState.isDisposed) &&
                        Volatile.Read(ref ownerState.wakeGeneration) == ownerState.parkedWakeGen &&
                        owner.renderer == __instance &&
                        owner.activeAnimationsByAnimCode.Count == 0 &&
                        (owner.animator == null || owner.animator.ActiveAnimationCount == 0))
                    {
                        ICoreClientAPI ownerCapi = rendererCapiRef(__instance);
                        if (ownerCapi != null && ownerCapi.Side == EnumAppSide.Client && !ownerCapi.IsGamePaused)
                        {
                            EnqueueRendererPark(__instance, parkState, ownerCapi);
                        }
                    }
                }

                return false;
            }

            // Never frustum-cull shadow passes so off-camera blocks still cast shadows into view.
            if (stage == EnumRenderStage.ShadowFar || stage == EnumRenderStage.ShadowNear)
            {
                return true;
            }

            // CustomTransform bypasses ScaleX/Y/Z and may translate or scale arbitrarily.
            if (__instance.CustomTransform != null)
            {
                return true;
            }

            ICoreClientAPI capi = rendererCapiRef(__instance);
            Vec3d pos = rendererPosRef(__instance);
            if (capi == null || pos == null ||
                !double.IsFinite(pos.X) || !double.IsFinite(pos.Y) || !double.IsFinite(pos.Z))
            {
                return true;
            }

            FrustumCulling culler = capi.Render?.DefaultFrustumCuller;
            if (culler == null)
            {
                return true;
            }

            double scaleX = Math.Abs(__instance.ScaleX);
            double scaleY = Math.Abs(__instance.ScaleY);
            double scaleZ = Math.Abs(__instance.ScaleZ);
            double maxScale = Math.Max(scaleX, Math.Max(scaleY, scaleZ));
            if (!double.IsFinite(maxScale))
            {
                return true;
            }

            double localCenterY = 0.5;
            double localRadius = MinLocalCullRadius;
            if (rendererStates.TryGetValue(__instance, out RendererParkState boundsState) &&
                Volatile.Read(ref boundsState.epoch) == epoch &&
                boundsState.boundsComputed)
            {
                localCenterY = boundsState.localCenterY;
                localRadius = boundsState.localRadius;
            }

            double centerYOffset = __instance.ScaleY != 0f
                ? localCenterY * __instance.ScaleY
                : localCenterY;
            double radius = (maxScale > 0.0 ? maxScale : 1.0) * localRadius;
            if (!double.IsFinite(centerYOffset) || !double.IsFinite(radius))
            {
                return true;
            }

            return culler.SphereInFrustum(pos.X + 0.5, pos.Y + centerYOffset, pos.Z + 0.5, radius);
        }

        public static void AnimatableRenderer_Dispose_Postfix(AnimatableRenderer __instance)
        {
            if (__instance == null)
            {
                return;
            }

            // Keep a sticky tombstone in the ConditionalWeakTable so an off-thread Dispose()
            // cannot be resurrected by a queued mainThreadInit or GetOrCreateValue lookup.
            RendererParkState rendererState = GetOrResetRendererState(__instance);
            Volatile.Write(ref rendererState.isDisposed, true);
            rendererState.ownerUtil = null;
        }

        public static void AnimationUtil_OnRenderFrame_Postfix(AnimationUtil __instance)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return;
            }

            AnimatableRenderer renderer = __instance.renderer;
            if (__instance.activeAnimationsByAnimCode.Count != 0 ||
                __instance.animator == null ||
                renderer == null ||
                __instance.animator.ActiveAnimationCount != 0)
            {
                return;
            }

            if (renderer.ShouldRender && !utilStopTriggeredRef(__instance))
            {
                return;
            }

            if (!IsUtilParkableType(__instance.GetType()))
            {
                return;
            }

            ICoreClientAPI capi = utilCapiRef(__instance);
            if (capi == null || capi.Side != EnumAppSide.Client || capi.IsGamePaused)
            {
                return;
            }

            UtilParkState state = GetOrResetUtilState(__instance);
            if (Volatile.Read(ref state.isDisposed) || state.isParked || state.parkQueued)
            {
                return;
            }

            if (rendererParkingEnabled &&
                renderer.GetType() == typeof(AnimatableRenderer) &&
                IsRendererOwnerParkableType(__instance.GetType()))
            {
                RendererParkState rendererState = GetOrResetRendererState(renderer);
                if (!Volatile.Read(ref rendererState.isDisposed))
                {
                    if (rendererState.ownerUtil == null ||
                        !rendererState.ownerUtil.TryGetTarget(out AnimationUtil existingOwner) ||
                        !ReferenceEquals(existingOwner, __instance))
                    {
                        rendererState.ownerUtil = new WeakReference<AnimationUtil>(__instance);
                    }
                }
            }

            // Defer unregistration until after TriggerRenderStage completes so the active
            // renderer list is never mutated mid-iteration (including under F3 extendedDebugInfo).
            state.queuedWakeGen = Volatile.Read(ref state.wakeGeneration);
            state.parkQueued = true;
            pendingUtilParks.Enqueue(__instance);
            if (!utilParkScheduled)
            {
                utilParkScheduled = true;
                try
                {
                    capi.Event.EnqueueMainThreadTask(drainUtilParksAction, "optitimeParkAnimUtilBatch");
                }
                catch
                {
                    utilParkScheduled = false;
                    throw;
                }
            }
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

            UtilParkState state = GetOrResetUtilState(__instance);
            if (Volatile.Read(ref state.isDisposed))
            {
                return true;
            }

            // Monotonic wake generation invalidates any queued park even before vanilla
            // adds meta.Code to activeAnimationsByAnimCode (eliminates TOCTOU races).
            Interlocked.Increment(ref state.wakeGeneration);

            if (RuntimeEnv.MainThreadId == Environment.CurrentManagedThreadId)
            {
                WakeOnMainThread(__instance, state, capi);
            }
            else
            {
                // Keep all isParked/parkQueued mutations strictly main-thread-only:
                // enqueue the util and let DrainWakes inspect live state and live renderer.
                bool shouldSchedule;
                lock (wakeLock)
                {
                    pendingWakes.Enqueue(__instance);
                    shouldSchedule = !wakeScheduled;
                    if (shouldSchedule)
                    {
                        wakeScheduled = true;
                    }
                }

                if (shouldSchedule)
                {
                    try
                    {
                        capi.Event.EnqueueMainThreadTask(drainWakesAction, "optitimeWakeAnimUtilBatch");
                    }
                    catch
                    {
                        lock (wakeLock)
                        {
                            wakeScheduled = false;
                        }
                        throw;
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

            UtilParkState state = GetOrResetUtilState(__instance);
            Volatile.Write(ref state.isDisposed, true);

            AnimatableRenderer renderer = __instance.renderer;
            if (renderer != null && rendererStates.TryGetValue(renderer, out RendererParkState rendererState))
            {
                rendererState.ownerUtil = null;
            }
        }

        private static void WakeOnMainThread(AnimationUtil util, UtilParkState state, ICoreClientAPI capi)
        {
            if (Volatile.Read(ref state.isDisposed))
            {
                return;
            }

            state.parkQueued = false;
            if (state.isParked)
            {
                state.isParked = false;
                capi.Event.RegisterRenderer(util, EnumRenderStage.Opaque, "beanimutil");
            }

            AnimatableRenderer renderer = util.renderer;
            if (rendererParkingEnabled &&
                renderer != null &&
                rendererStates.TryGetValue(renderer, out RendererParkState rendererState) &&
                Volatile.Read(ref rendererState.epoch) == Volatile.Read(ref sessionEpoch) &&
                !Volatile.Read(ref rendererState.isDisposed))
            {
                rendererState.parkQueued = false;
                if (rendererState.isParked)
                {
                    rendererState.isParked = false;
                    RegisterAnimatableRenderer(capi, renderer, rendererState);
                }
            }
        }

        private static void EnqueueRendererPark(
            AnimatableRenderer renderer,
            RendererParkState rendererState,
            ICoreClientAPI capi)
        {
            rendererState.parkQueued = true;
            pendingRendererParks.Enqueue(renderer);
            if (!rendererParkScheduled)
            {
                rendererParkScheduled = true;
                try
                {
                    capi.Event.EnqueueMainThreadTask(drainRendererParksAction, "optitimeParkAnimRendererBatch");
                }
                catch
                {
                    rendererParkScheduled = false;
                    throw;
                }
            }
        }

        private static void DrainUtilParks()
        {
            utilParkScheduled = false;
            int epoch = Volatile.Read(ref sessionEpoch);
            try
            {
                int count = pendingUtilParks.Count;
                for (int i = 0; i < count; i++)
                {
                    AnimationUtil util = pendingUtilParks.Dequeue();
                    if (!clientActive ||
                        !utilStates.TryGetValue(util, out UtilParkState state) ||
                        Volatile.Read(ref state.epoch) != epoch)
                    {
                        continue;
                    }

                    if (!state.parkQueued)
                    {
                        continue;
                    }

                    state.parkQueued = false;
                    if (Volatile.Read(ref state.isDisposed) || state.isParked)
                    {
                        continue;
                    }

                    int wakeGen = Volatile.Read(ref state.wakeGeneration);
                    if (wakeGen != state.queuedWakeGen)
                    {
                        continue;
                    }

                    AnimatableRenderer renderer = util.renderer;
                    if (util.activeAnimationsByAnimCode.Count != 0 ||
                        (util.animator != null && util.animator.ActiveAnimationCount != 0) ||
                        (renderer != null && renderer.ShouldRender && !utilStopTriggeredRef(util)))
                    {
                        continue;
                    }

                    ICoreClientAPI capi = utilCapiRef(util);
                    if (capi == null || capi.Side != EnumAppSide.Client || capi.IsGamePaused)
                    {
                        continue;
                    }

                    capi.Event.UnregisterRenderer(util, EnumRenderStage.Opaque);
                    state.isParked = true;
                    state.parkedWakeGen = wakeGen;

                    if (rendererParkingEnabled &&
                        renderer != null &&
                        !renderer.ShouldRender &&
                        rendererStates.TryGetValue(renderer, out RendererParkState rendererState) &&
                        Volatile.Read(ref rendererState.epoch) == epoch &&
                        rendererState.stageRecorded &&
                        rendererState.canParkStages &&
                        !Volatile.Read(ref rendererState.isDisposed) &&
                        !rendererState.isParked &&
                        rendererState.ownerUtil != null &&
                        rendererState.ownerUtil.TryGetTarget(out AnimationUtil owner) &&
                        ReferenceEquals(owner, util))
                    {
                        rendererState.parkQueued = false;
                        UnregisterAnimatableRenderer(capi, renderer, rendererState);
                        rendererState.isParked = true;
                    }
                }
            }
            catch
            {
                while (pendingUtilParks.Count > 0)
                {
                    AnimationUtil dropped = pendingUtilParks.Dequeue();
                    if (utilStates.TryGetValue(dropped, out UtilParkState droppedState))
                    {
                        droppedState.parkQueued = false;
                    }
                }
                throw;
            }
        }

        private static void DrainRendererParks()
        {
            rendererParkScheduled = false;
            int epoch = Volatile.Read(ref sessionEpoch);
            try
            {
                int count = pendingRendererParks.Count;
                for (int i = 0; i < count; i++)
                {
                    AnimatableRenderer renderer = pendingRendererParks.Dequeue();
                    if (!clientActive || !rendererParkingEnabled ||
                        !rendererStates.TryGetValue(renderer, out RendererParkState rendererState) ||
                        Volatile.Read(ref rendererState.epoch) != epoch)
                    {
                        continue;
                    }

                    if (!rendererState.parkQueued)
                    {
                        continue;
                    }

                    rendererState.parkQueued = false;
                    if (!rendererState.stageRecorded ||
                        !rendererState.canParkStages ||
                        Volatile.Read(ref rendererState.isDisposed) ||
                        rendererState.isParked ||
                        renderer.ShouldRender)
                    {
                        continue;
                    }

                    if (rendererState.ownerUtil == null ||
                        !rendererState.ownerUtil.TryGetTarget(out AnimationUtil owner) ||
                        owner.renderer != renderer ||
                        !utilStates.TryGetValue(owner, out UtilParkState ownerState) ||
                        Volatile.Read(ref ownerState.epoch) != epoch ||
                        !ownerState.isParked ||
                        Volatile.Read(ref ownerState.isDisposed) ||
                        Volatile.Read(ref ownerState.wakeGeneration) != ownerState.parkedWakeGen ||
                        owner.activeAnimationsByAnimCode.Count != 0 ||
                        (owner.animator != null && owner.animator.ActiveAnimationCount != 0))
                    {
                        continue;
                    }

                    ICoreClientAPI capi = rendererCapiRef(renderer);
                    if (capi == null || capi.Side != EnumAppSide.Client || capi.IsGamePaused)
                    {
                        continue;
                    }

                    UnregisterAnimatableRenderer(capi, renderer, rendererState);
                    rendererState.isParked = true;
                }
            }
            catch
            {
                while (pendingRendererParks.Count > 0)
                {
                    AnimatableRenderer dropped = pendingRendererParks.Dequeue();
                    if (rendererStates.TryGetValue(dropped, out RendererParkState droppedState))
                    {
                        droppedState.parkQueued = false;
                    }
                }
                throw;
            }
        }

        private static void DrainWakes()
        {
            int epoch = Volatile.Read(ref sessionEpoch);
            try
            {
                while (true)
                {
                    AnimationUtil util;
                    lock (wakeLock)
                    {
                        if (pendingWakes.Count == 0)
                        {
                            wakeScheduled = false;
                            return;
                        }

                        util = pendingWakes.Dequeue();
                    }

                    if (!clientActive ||
                        !utilStates.TryGetValue(util, out UtilParkState state) ||
                        Volatile.Read(ref state.epoch) != epoch ||
                        Volatile.Read(ref state.isDisposed))
                    {
                        continue;
                    }

                    ICoreClientAPI capi = utilCapiRef(util);
                    if (capi == null || capi.Side != EnumAppSide.Client)
                    {
                        continue;
                    }

                    WakeOnMainThread(util, state, capi);
                }
            }
            catch
            {
                lock (wakeLock)
                {
                    wakeScheduled = false;
                    if (pendingWakes.Count > 0)
                    {
                        pendingWakes.Clear();
                    }
                }
                throw;
            }
        }

        private static void UnregisterAnimatableRenderer(
            ICoreClientAPI capi,
            AnimatableRenderer renderer,
            RendererParkState rendererState)
        {
            EnumRenderStage primaryStage = rendererState.renderStage;
            capi.Event.UnregisterRenderer(renderer, primaryStage);
            if (rendererState.hasOit)
            {
                capi.Event.UnregisterRenderer(renderer, EnumRenderStage.OIT);
            }

            capi.Event.UnregisterRenderer(renderer, EnumRenderStage.ShadowFar);
            capi.Event.UnregisterRenderer(renderer, EnumRenderStage.ShadowNear);
        }

        private static void RegisterAnimatableRenderer(
            ICoreClientAPI capi,
            AnimatableRenderer renderer,
            RendererParkState rendererState)
        {
            EnumRenderStage primaryStage = rendererState.renderStage;
            if (rendererState.hasOit)
            {
                capi.Event.RegisterRenderer(renderer, EnumRenderStage.OIT, "animatable");
            }

            capi.Event.RegisterRenderer(renderer, primaryStage, "animatable");
            capi.Event.RegisterRenderer(renderer, EnumRenderStage.ShadowFar, "animatable");
            capi.Event.RegisterRenderer(renderer, EnumRenderStage.ShadowNear, "animatable");
        }

        private static void ComputeLocalBounds(MeshData meshdata, out double localCenterY, out double localRadius)
        {
            localCenterY = 0.5;
            localRadius = MinLocalCullRadius;

            if (meshdata == null || meshdata.xyz == null || meshdata.VerticesCount <= 0)
            {
                return;
            }

            float[] xyz = meshdata.xyz;
            int coordCount = Math.Min(meshdata.VerticesCount * 3, xyz.Length);
            if (coordCount < 3)
            {
                return;
            }

            float minY = float.MaxValue;
            float maxY = float.MinValue;
            bool anyFinite = false;

            for (int i = 0; i + 2 < coordCount; i += 3)
            {
                float x = xyz[i];
                float y = xyz[i + 1];
                float z = xyz[i + 2];
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                {
                    continue;
                }

                anyFinite = true;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            if (!anyFinite)
            {
                return;
            }

            double centerY = 0.5 * (minY + maxY);
            double maxDistSq = 0.0;
            for (int i = 0; i + 2 < coordCount; i += 3)
            {
                float x = xyz[i];
                float y = xyz[i + 1];
                float z = xyz[i + 2];
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                {
                    continue;
                }

                double dx = x - 0.5;
                double dy = y - centerY;
                double dz = z - 0.5;
                double distSq = dx * dx + dy * dy + dz * dz;
                if (distSq > maxDistSq)
                {
                    maxDistSq = distSq;
                }
            }

            double radius = Math.Sqrt(maxDistSq) * 1.35 + 0.5;
            if (double.IsFinite(centerY) && double.IsFinite(radius))
            {
                localCenterY = centerY;
                localRadius = Math.Max(MinLocalCullRadius, radius);
            }
        }

        private static bool IsUtilParkableType(Type type)
        {
            return utilParkableTypeCache.GetOrAdd(type, static candidate =>
            {
                MethodInfo startAnim = candidate.GetMethod(
                    nameof(AnimationUtil.StartAnimation),
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(AnimationMetaData) },
                    null);
                return startAnim != null && startAnim.DeclaringType == typeof(AnimationUtil);
            });
        }

        private static bool IsRendererOwnerParkableType(Type type)
        {
            return rendererOwnerParkableTypeCache.GetOrAdd(type, static candidate =>
            {
                if (blockEntityAnimationUtilType == null ||
                    !blockEntityAnimationUtilType.IsAssignableFrom(candidate))
                {
                    return false;
                }

                if (!IsUtilParkableType(candidate))
                {
                    return false;
                }

                MethodInfo onStateChange = candidate.GetMethod(
                    "OnAnimationsStateChange",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(bool) },
                    null);
                return onStateChange != null && onStateChange.DeclaringType == blockEntityAnimationUtilType;
            });
        }

        private static UtilParkState GetOrResetUtilState(AnimationUtil util)
        {
            UtilParkState state = utilStates.GetOrCreateValue(util);
            int epoch = Volatile.Read(ref sessionEpoch);
            if (Volatile.Read(ref state.epoch) != epoch)
            {
                lock (state)
                {
                    if (Volatile.Read(ref state.epoch) != epoch)
                    {
                        state.ResetForEpoch(epoch);
                    }
                }
            }

            return state;
        }

        private static RendererParkState GetOrResetRendererState(AnimatableRenderer renderer)
        {
            RendererParkState rendererState = rendererStates.GetOrCreateValue(renderer);
            int epoch = Volatile.Read(ref sessionEpoch);
            if (Volatile.Read(ref rendererState.epoch) != epoch)
            {
                lock (rendererState)
                {
                    if (Volatile.Read(ref rendererState.epoch) != epoch)
                    {
                        rendererState.ResetForEpoch(epoch);
                    }
                }
            }

            return rendererState;
        }

        private sealed class UtilParkState
        {
            internal int epoch;
            internal bool isParked;
            internal bool parkQueued;
            internal bool isDisposed;
            internal int wakeGeneration;
            internal int queuedWakeGen;
            internal int parkedWakeGen;

            public UtilParkState()
            {
                epoch = Volatile.Read(ref sessionEpoch);
            }

            internal void ResetForEpoch(int newEpoch)
            {
                isParked = false;
                parkQueued = false;
                Volatile.Write(ref isDisposed, false);
                Volatile.Write(ref wakeGeneration, 0);
                queuedWakeGen = 0;
                parkedWakeGen = 0;
                Volatile.Write(ref epoch, newEpoch);
            }
        }

        private sealed class RendererParkState
        {
            internal int epoch;
            internal WeakReference<AnimationUtil> ownerUtil;
            internal EnumRenderStage renderStage;
            internal bool stageRecorded;
            internal bool canParkStages;
            internal bool hasOit;
            internal bool boundsComputed;
            internal double localCenterY;
            internal double localRadius;
            internal bool isParked;
            internal bool parkQueued;
            internal bool isDisposed;

            public RendererParkState()
            {
                renderStage = EnumRenderStage.Opaque;
                localCenterY = 0.5;
                localRadius = MinLocalCullRadius;
                epoch = Volatile.Read(ref sessionEpoch);
            }

            internal void ResetForEpoch(int newEpoch)
            {
                ownerUtil = null;
                renderStage = EnumRenderStage.Opaque;
                stageRecorded = false;
                canParkStages = false;
                hasOit = false;
                boundsComputed = false;
                localCenterY = 0.5;
                localRadius = MinLocalCullRadius;
                isParked = false;
                parkQueued = false;
                Volatile.Write(ref isDisposed, false);
                Volatile.Write(ref epoch, newEpoch);
            }
        }
    }
}
