using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using OptiTime.Diagnostics;

namespace OptiTime
{
    /// <summary>
    /// Replaces <c>SystemRenderEntities.OnBeforeRender</c>. Visibility and the per-frame animation
    /// tick stay as in vanilla, so animation time, frame callbacks, sounds and head control advance
    /// every frame. Distant creatures recompute their pose matrices (the joint hierarchy walk in
    /// <c>ClientAnimator.calculateMatrices</c>) every second or third frame and draw the last pose in
    /// between. Every consumer of a pose, held items and seats included, reads the same snapshot the
    /// body is drawn with, so the body and what hangs off it stay together.
    ///
    /// The method also records the renderers it marks visible, so the later entity passes and the
    /// name tag HUD walk only those instead of every loaded entity.
    /// </summary>
    public static class EntityAnimationOptimization
    {
        /// <summary>Squared factor for the LOD hysteresis band: 1.21 in squared distance is 1.1x in blocks.</summary>
        internal const double HysteresisFactorSq = 1.21;

        // Close and medium ranges scale with the view distance and never drop below these.
        // At VD 256 they are 64 and 128 blocks.
        private const double MinCloseDistance = 48;
        private const double MinMediumDistance = 64;

        private static double closeDistSq = 64.0 * 64.0;
        private static double mediumDistSq = 128.0 * 128.0;
        private static uint frameCounter;

        // LOD tier of every entity above tier 0 last frame, and the map being filled this frame.
        // Swapped once per frame, so entities that unload drop out on their own.
        private static Dictionary<long, byte> lodTiers = new Dictionary<long, byte>();
        private static Dictionary<long, byte> nextLodTiers = new Dictionary<long, byte>();

        // Renderers marked visible this frame. Only valid while visibleRenderersGame is the running
        // game: it is cleared at the start of every OnBeforeRender and set when the pass completes.
        private static readonly Dictionary<long, EntityRenderer> visibleRenderers = new Dictionary<long, EntityRenderer>();
        private static ClientMain visibleRenderersGame;

        private static ClientMain clothManagerGame;
        private static ClothManager clothManager;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
        private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> entityRenderersRef =
            AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");

        public static void UpdateViewDistance(int viewDistance)
        {
            double close = Math.Max(MinCloseDistance, viewDistance / 4.0);
            double medium = Math.Max(MinMediumDistance, viewDistance / 2.0);
            closeDistSq = close * close;
            mediumDistSq = medium * medium;
        }

        internal static double CloseDistanceSq => closeDistSq;
        internal static double MediumDistanceSq => mediumDistSq;

        /// <summary>
        /// LOD tier for a squared horizontal distance: 0 posed every frame, 1 every second frame,
        /// 2 every third frame. A tier is entered only past 1.1x its threshold and left only below the
        /// threshold, so an entity walking along a boundary does not flip rates every frame.
        /// </summary>
        internal static int ResolveLodTier(double distSq, int previousTier, double closeSq, double mediumSq)
        {
            double mediumEnterSq = previousTier >= 1 ? closeSq : closeSq * HysteresisFactorSq;
            if (distSq < mediumEnterSq) return 0;

            double farEnterSq = previousTier >= 2 ? mediumSq : mediumSq * HysteresisFactorSq;
            return distSq < farEnterSq ? 1 : 2;
        }

        /// <summary>
        /// Whether the pose is recomputed this frame. The entity id staggers the phase so the
        /// throttled entities split across frames instead of all landing on the same one.
        /// </summary>
        internal static bool ShouldComputePose(int tier, uint frame, long entityId)
        {
            uint phase = frame + (uint)entityId;
            return tier switch
            {
                0 => true,
                1 => (phase & 1) == 0,
                _ => phase % 3 == 0
            };
        }

        /// <summary>
        /// Entities whose pose drives something outside their own body at full frame rate: players
        /// (camera, held tools, fishing line), projectiles (fishing bobbers, arrows), entities the
        /// game keeps outside the loaded range, and riders and their mounts.
        /// </summary>
        internal static bool IsPoseThrottleExempt(Entity entity)
        {
            return entity is EntityPlayer
                || entity is IProjectile
                || entity.AllowOutsideLoadedRange
                || (entity is EntityAgent agent && agent.MountedOn != null)
                || entity.GetInterface<IMountable>()?.AnyMounted() == true;
        }

        /// <summary>
        /// Runs one entity's animation tick. When <paramref name="computePose"/> is false the pose
        /// matrices keep last frame's values for this call only; a value set by someone else is left
        /// untouched, and the flag is restored even if the animation code throws.
        /// </summary>
        internal static void TickAnimation(IAnimationManager animManager, bool computePose, float dt)
        {
            IAnimator animator = animManager?.Animator;
            bool skipPose = !computePose && animator != null && animator.CalculateMatrices;
            if (skipPose) animator.CalculateMatrices = false;
            try
            {
                animManager?.OnClientFrame(dt);
            }
            finally
            {
                if (skipPose) animator.CalculateMatrices = true;
            }
        }

        public static bool OptimizeEntityAnimations(SystemRenderEntities __instance, float dt)
        {
            visibleRenderersGame = null;
            if (OptiTimeMod.Config?.EntityAnimationOptimizations != true) return true;

            ClientMain game;
            Dictionary<long, EntityRenderer> renderers;
            try
            {
                game = gameRef(__instance);
                renderers = entityRenderersRef(game);
            }
            catch
            {
                return true;
            }

            EntityPlayer player = game?.EntityPlayer;
            if (player == null || renderers == null) return true;

            frameCounter++;
            int viewDistance = ClientSettings.ViewDistance;
            double viewDistanceSq = (double)viewDistance * viewDistance;
            double playerX = player.Pos.X;
            double playerZ = player.Pos.Z;
            int dimension = player.Pos.Dimension;
            FrustumCulling frustumCuller = game.frustumCuller;
            ClientWorldMap worldMap = game.WorldMap;
            FrameProfilerUtil profiler = game.api.World.FrameProfiler;
            ClothManager ropes = GetClothManager(game);

            visibleRenderers.Clear();
            nextLodTiers.Clear();

            foreach (KeyValuePair<long, EntityRenderer> pair in renderers)
            {
                EntityRenderer renderer = pair.Value;
                Entity entity = renderer.entity;
                EntityPos pos = entity.Pos;
                bool isPlayer = entity == player;
                double dx = pos.X - playerX;
                double dz = pos.Z - playerZ;
                double distSq = dx * dx + dz * dz;

                // Projectiles stay drawn wherever they are; the fishing line follows the bobber.
                bool rendered = (!isPlayer && entity is IProjectile) || (
                    pos.Dimension == dimension
                    && (entity.AllowOutsideLoadedRange || (distSq < viewDistanceSq && (isPlayer || worldMap.IsChunkRendered(pos))))
                    && frustumCuller.SphereInFrustum((float)pos.X, (float)pos.InternalY, (float)pos.Z, entity.FrustumSphereRadius));

                entity.IsRendered = rendered;
                if (rendered)
                {
                    renderer.BeforeRender(dt);
                    visibleRenderers[pair.Key] = renderer;
                }

                int tier = 0;
                if (distSq >= closeDistSq
                    && !IsPoseThrottleExempt(entity)
                    && ropes?.GetClothSystemAttachedToEntity(pair.Key) == null)
                {
                    lodTiers.TryGetValue(pair.Key, out byte previousTier);
                    tier = ResolveLodTier(distSq, previousTier, closeDistSq, mediumDistSq);
                    if (tier > 0) nextLodTiers[pair.Key] = (byte)tier;
                }

                bool computePose = ShouldComputePose(tier, frameCounter, pair.Key);
                if (rendered) ModuleEntityAnim.OnEntity(tier, computePose);

                profiler.Mark("esr-beforeanim");
                try
                {
                    TickAnimation(entity.AnimManager, computePose, dt);
                }
                catch (Exception)
                {
                    game.Logger.Error("Animations error for entity " + entity.Code.ToShortString() + " at " + pos.AsBlockPos);
                    throw;
                }
                profiler.Mark("esr-afteranim");
            }

            (lodTiers, nextLodTiers) = (nextLodTiers, lodTiers);
            visibleRenderersGame = game;
            return false;
        }

        /// <summary>
        /// Stands in for <c>ClientMain.EntityRenderers</c> in the entity render passes: the renderers
        /// marked visible this frame, or the full map whenever this frame's list was not built.
        /// </summary>
        public static Dictionary<long, EntityRenderer> RenderersForPass(ClientMain game)
        {
            return game == visibleRenderersGame ? visibleRenderers : entityRenderersRef(game);
        }

        /// <summary>
        /// Points the <c>EntityRenderers</c> loads of a render pass at <see cref="RenderersForPass"/>.
        /// The loop bodies keep their own <c>IsRendered</c> checks, so the only change is which
        /// renderers they look at.
        /// </summary>
        public static IEnumerable<CodeInstruction> TranspileRenderPass(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo renderersField = AccessTools.Field(typeof(ClientMain), "EntityRenderers");
            MethodInfo replacement = AccessTools.Method(typeof(EntityAnimationOptimization), nameof(RenderersForPass));
            var codes = new List<CodeInstruction>(instructions);
            int replaced = 0;

            foreach (CodeInstruction code in codes)
            {
                if (code.LoadsField(renderersField))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = replacement;
                    replaced++;
                }
            }

            if (replaced == 0)
                throw new InvalidOperationException("No EntityRenderers load found in the render pass");

            return codes;
        }

        /// <summary>Keeps the visible list free of renderers disposed after it was built.</summary>
        public static void OnEntityRendererRemoved(Entity forEntity)
        {
            if (forEntity != null) visibleRenderers.Remove(forEntity.EntityId);
        }

        /// <summary>
        /// Replaces <c>HudEntityNameTags.OnRenderGUI</c>. Vanilla tests every loaded entity against the
        /// frustum at eye height; this applies the same test to the renderers marked visible this frame.
        /// Entities that are not drawn (beyond the view distance or in a chunk that is not rendered) no
        /// longer get a chat bubble or debug tag floating where no body is drawn.
        /// </summary>
        public static bool RenderVisibleNameTags(ClientMain ___game, float deltaTime)
        {
            if (___game == null || ___game != visibleRenderersGame) return true;

            FrustumCulling frustumCuller = ___game.frustumCuller;
            int dimension = ___game.EntityPlayer.Pos.Dimension;
            foreach (EntityRenderer renderer in visibleRenderers.Values)
            {
                Entity entity = renderer.entity;
                EntityPos pos = entity.Pos;
                if (entity.IsRendered
                    && frustumCuller.SphereInFrustum((float)pos.X, (float)(pos.Y + entity.LocalEyePos.Y), (float)pos.Z, 0.5)
                    && pos.Dimension == dimension)
                {
                    renderer.DoRender2D(deltaTime);
                }
            }

            return false;
        }

        private static ClothManager GetClothManager(ClientMain game)
        {
            if (clothManagerGame != game)
            {
                clothManager = game.api.ModLoader.GetModSystem<ClothManager>();
                clothManagerGame = game;
            }

            return clothManager;
        }

        public static void Cleanup()
        {
            visibleRenderers.Clear();
            visibleRenderersGame = null;
            lodTiers.Clear();
            nextLodTiers.Clear();
            clothManager = null;
            clothManagerGame = null;
        }
    }
}
