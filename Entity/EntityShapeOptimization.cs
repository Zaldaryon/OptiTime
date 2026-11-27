using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OptiTime.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace OptiTime
{
    /// <summary>
    /// Spreads reshapes of entities that already have a mesh over several frames.
    ///
    /// <para>Vanilla: when an entity's shape is marked modified (outfit, gear, attachments), the next
    /// <c>EntityShapeRenderer.BeforeRender</c> calls <c>TesselateShape()</c>. The mesh itself is built on a
    /// worker thread, but <c>Entity.OnTesselation</c> (shape clone, gear step-parenting, texture lookups)
    /// runs on the render thread first and costs roughly 10 to 25 ms for a dressed villager, so a group
    /// changing together stalls a single frame for over 100 ms.</para>
    ///
    /// <para>Change: at most <see cref="ReshapesPerFrame"/> such reshape starts per frame. A deferred call
    /// returns before <c>OnTesselation</c>, so <c>ShapeFresh</c> stays false and <c>BeforeRender</c> asks
    /// again next frame while the previous mesh and animator keep drawing. First meshes, players and calls
    /// on an entity whose shape is not marked modified always run, and a reshape that has waited
    /// <see cref="MaxWaitFrames"/> frames runs whatever the budget.</para>
    /// </summary>
    public static class EntityShapeOptimization
    {
        internal const int ReshapesPerFrame = 1;
        internal const uint MaxWaitFrames = 30;
        private const uint PruneIntervalFrames = 600;

        private static readonly AccessTools.FieldRef<EntityShapeRenderer, MultiTextureMeshRef> meshRefOpaqueRef =
            AccessTools.FieldRefAccess<EntityShapeRenderer, MultiTextureMeshRef>("meshRefOpaque");

        // Frame each deferred entity first waited in. Entries are removed when the reshape runs and pruned
        // when the entity stopped asking (unloaded, or no longer drawn).
        private static readonly Dictionary<long, uint> waitingSince = new Dictionary<long, uint>();
        private static uint frame;
        private static int reshapesThisFrame;
        private static ICoreClientAPI capi;
        private static FrameStart frameStart;

        public static void Initialize(ICoreClientAPI api, Harmony harmony, MethodBase target)
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(EntityShapeOptimization), nameof(LimitReshapes)));
            capi = api;
            frameStart = new FrameStart();
            api.Event.RegisterRenderer(frameStart, EnumRenderStage.Before, "optitime-entityshape");
            api.Logger.Notification("[OptiTime] Entity reshape budget loaded");
        }

        public static void Cleanup()
        {
            if (capi != null && frameStart != null)
                capi.Event.UnregisterRenderer(frameStart, EnumRenderStage.Before);
            capi = null;
            frameStart = null;
            waitingSince.Clear();
            frame = 0;
            reshapesThisFrame = 0;
        }

        /// <summary>
        /// Whether a <c>TesselateShape()</c> call counts against the budget: a reshape of a non-player entity
        /// that already has a mesh and whose shape is marked modified, so a skipped call is asked again.
        /// </summary>
        internal static bool IsBudgeted(bool shapeFresh, bool isPlayer, bool hasMesh)
        {
            return hasMesh && !shapeFresh && !isPlayer;
        }

        /// <summary>Whether a budgeted reshape may start now.</summary>
        internal static bool MayStartNow(int startedThisFrame, bool waiting, uint waitedFrames)
        {
            return startedThisFrame < ReshapesPerFrame || (waiting && waitedFrames >= MaxWaitFrames);
        }

        public static bool LimitReshapes(EntityShapeRenderer __instance)
        {
            if (OptiTimeMod.Config?.EntityShapeOptimizations != true) return true;

            Entity entity = __instance.entity;
            if (entity == null || !IsBudgeted(entity.ShapeFresh, entity is EntityPlayer, meshRefOpaqueRef(__instance) != null))
                return true;

            return TryStartReshape(entity.EntityId);
        }

        /// <summary>
        /// Charges a budgeted reshape of <paramref name="entityId"/> to this frame, or records that it waits.
        /// </summary>
        internal static bool TryStartReshape(long entityId)
        {
            bool waiting = waitingSince.TryGetValue(entityId, out uint since);
            if (!MayStartNow(reshapesThisFrame, waiting, frame - since))
            {
                if (!waiting) waitingSince[entityId] = frame;
                ModuleEntityShape.OnDeferred();
                return false;
            }

            reshapesThisFrame++;
            if (waiting) waitingSince.Remove(entityId);
            ModuleEntityShape.OnReshape(waiting);
            return true;
        }

        internal static void BeginFrame()
        {
            frame++;
            reshapesThisFrame = 0;
            if (frame % PruneIntervalFrames != 0 || waitingSince.Count == 0) return;

            foreach (KeyValuePair<long, uint> pair in waitingSince)
            {
                if (frame - pair.Value > PruneIntervalFrames) waitingSince.Remove(pair.Key);
            }
        }

        internal static int WaitingCount => waitingSince.Count;

        /// <summary>Starts the frame's budget before SystemRenderEntities.OnBeforeRender (order 0.4).</summary>
        private sealed class FrameStart : IRenderer
        {
            public double RenderOrder => 0;
            public int RenderRange => 0;
            public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => BeginFrame();
            public void Dispose() { }
        }
    }
}
