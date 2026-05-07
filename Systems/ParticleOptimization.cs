using System;
using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Particle spawn optimization combining 4 independent techniques in a single prefix:
    ///
    /// 1. VIEW DISTANCE REJECTION — Probabilistic reject at high VD (384+: 25%, 512+: 50%)
    /// 2. FRUSTUM CULLING — Reject spawns whose base position is behind the camera
    /// 3. POOL OCCUPANCY PRESSURE — Gradual rejection as pool fills (70%+ occupancy)
    /// 4. FRAME-TIME THROTTLE — Reduce spawns when frame time exceeds target
    ///
    /// All techniques respect IgnoreUserConfig (critical particles always spawn).
    /// Combined effect: 65-75% fewer particles in worst-case high-VD scenarios.
    /// </summary>
    public static class ParticleOptimization
    {
        // --- Configuration ---
        private static int cachedViewDistance = 256;
        private static bool scalingEnabled;
        private static Action<string> logger;

        // --- Frustum culling state ---
        private static FrustumCulling frustumCuller;
        private static bool frustumAvailable;

        // --- Pool occupancy state (cached per-pool via reflection) ---
        private static readonly AccessTools.FieldRef<ParticlePoolQuads, int> poolSizeRef =
            AccessTools.FieldRefAccess<ParticlePoolQuads, int>("poolSize");

        // --- Frame-time throttle state ---
        private static float spawnMultiplier = 1.0f;
        private static long lastFrameTimestamp;
        private static float cachedFrameMs;
        private static float targetFrameMs = 16.67f; // Updated from ClientSettings.MaxFPS
        private const float ThrottleFloor = 0.3f;

        // --- RNG ---
        [ThreadStatic]
        private static Random tRandom;
        private static Random Rng => tRandom ??= new Random();

        public static void SetLogger(Action<string> log) => logger = log;

        public static void ConfigureScaling(bool enabled)
        {
            scalingEnabled = enabled;
        }

        public static void UpdateViewDistance(int newViewDistance)
        {
            cachedViewDistance = newViewDistance;
        }

        /// <summary>
        /// Called once after game loads to cache the frustum culler reference and FPS target.
        /// </summary>
        public static void InitializeFrustum(ICoreClientAPI capi)
        {
            try
            {
                var game = (ClientMain)capi.World;
                frustumCuller = game.frustumCuller;
                frustumAvailable = frustumCuller != null;

                int maxFps = ClientSettings.MaxFPS;
                targetFrameMs = maxFps > 0 ? 1000f / maxFps : 16.67f;
            }
            catch
            {
                frustumAvailable = false;
            }
        }

        /// <summary>
        /// Called from a per-frame hook to update frame-time state.
        /// </summary>
        public static void UpdateFrameTime()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastFrameTimestamp != 0)
            {
                cachedFrameMs = (now - lastFrameTimestamp) * 1000f / Stopwatch.Frequency;

                // Ignore spikes from loading/pause (>200ms = <5fps)
                if (cachedFrameMs > 200f)
                {
                    lastFrameTimestamp = now;
                    return;
                }

                // Adaptive multiplier: ramp down fast under pressure, recover slowly
                if (cachedFrameMs > targetFrameMs * 1.3f)
                    spawnMultiplier = Math.Max(ThrottleFloor, spawnMultiplier - 0.03f);
                else if (cachedFrameMs < targetFrameMs * 1.1f)
                    spawnMultiplier = Math.Min(1.0f, spawnMultiplier + 0.005f);
            }
            lastFrameTimestamp = now;
        }

        public static void Cleanup()
        {
            cachedViewDistance = 256;
            scalingEnabled = false;
            logger = null;
            frustumCuller = null;
            frustumAvailable = false;
            spawnMultiplier = 1.0f;
            lastFrameTimestamp = 0;
            cachedFrameMs = 0;
        }

        /// <summary>
        /// Harmony prefix on ParticlePoolQuads.SpawnParticles (inherited by ParticlePoolCubes).
        /// Combines all 4 rejection techniques in order of cheapness.
        /// </summary>
        public static bool SpawnParticlesPrefix(
            ParticlePoolQuads __instance,
            IParticlePropertiesProvider particleProperties,
            ref int __result)
        {
            if (!scalingEnabled)
                return true;

            // Critical particles always pass
            if (particleProperties == null || particleProperties.IgnoreUserConfig)
                return true;

            var rng = Rng;

            // --- TECHNIQUE 1: View distance rejection (cheapest check first) ---
            if (cachedViewDistance >= 384)
            {
                float vdReject = cachedViewDistance >= 512 ? 0.50f : 0.25f;
                if (rng.NextDouble() < vdReject)
                {
                    __result = 0;
                    return false;
                }
            }

            // --- TECHNIQUE 2: Frustum culling on spawn position ---
            if (frustumAvailable)
            {
                Vec3d pos = particleProperties.Pos;
                if (pos != null && !frustumCuller.SphereInFrustum(pos.X, pos.Y, pos.Z, 16.0))
                {
                    __result = 0;
                    return false;
                }
            }

            // --- TECHNIQUE 3: Pool occupancy pressure ---
            int poolSize = poolSizeRef(__instance);
            if (poolSize > 0)
            {
                float occupancy = (float)__instance.QuantityAlive / poolSize;
                if (occupancy > 0.7f)
                {
                    float pressure = (occupancy - 0.7f) / 0.3f; // 0.0 at 70%, 1.0 at 100%
                    float rejectChance = pressure * 0.5f; // max 50% extra rejection
                    if (rng.NextDouble() < rejectChance)
                    {
                        __result = 0;
                        return false;
                    }
                }
            }

            // --- TECHNIQUE 4: Frame-time throttle ---
            if (spawnMultiplier < 0.99f)
            {
                if (rng.NextDouble() > spawnMultiplier)
                {
                    __result = 0;
                    return false;
                }
            }

            return true;
        }
    }
}
