using HarmonyLib;
using System;
using System.Reflection;

namespace OptiTime
{
    /// <summary>
    /// Scales particle pool MaxParticles at high view distances (384+: 75%, 512+: 50%).
    /// Targets SystemRenderParticles.mainthreadpools/offthreadpools (internal IParticlePool[]).
    ///
    /// Migrated from dynamic dispatch to cached FieldInfo to eliminate DLR overhead.
    /// IParticlePool is internal to VintagestoryLib — FieldInfo.GetValue is the correct
    /// access pattern for external assemblies (same as FieldRefAccess but for array types
    /// where the element type is inaccessible at compile time).
    ///
    /// Ref: SystemRenderParticles.cs — internal IParticlePool[] mainthreadpools, offthreadpools
    /// Ref: IParticlePool.MaxParticles — int property on each pool
    /// </summary>
    public class ParticleOptimization
    {
        private static object particleSystemInstance = null;
        private static int[] originalMainThreadMaxParticles = null;
        private static int[] originalOffThreadMaxParticles = null;
        private static int cachedViewDistance = 256;
        private static bool viewDistanceScalingEnabled = false;
        private static Action<string> logger;
        private static bool loggedAccessFailure;

        // Cached reflection — resolved once in AdjustParticlePools, zero per-call overhead
        private static FieldInfo mainThreadPoolsField;
        private static FieldInfo offThreadPoolsField;
        private static PropertyInfo maxParticlesProp;

        public static void SetLogger(Action<string> log) => logger = log;

        public static void Cleanup()
        {
            particleSystemInstance = null;
            originalMainThreadMaxParticles = null;
            originalOffThreadMaxParticles = null;
            cachedViewDistance = 256;
            viewDistanceScalingEnabled = false;
            logger = null;
            loggedAccessFailure = false;
            mainThreadPoolsField = null;
            offThreadPoolsField = null;
            maxParticlesProp = null;
        }

        private static float GetParticleScale(int viewDistance)
        {
            if (!viewDistanceScalingEnabled) return 1.0f;

            // Downscale only at very high view distances where scene load is heavier.
            if (viewDistance >= 512) return 0.5f;   // ultra-high distance: halve particles
            if (viewDistance >= 384) return 0.75f;  // high distance: 75% of vanilla
            return 1.0f;                            // normal distances: keep vanilla
        }

        public static void AdjustParticlePools(object __instance)
        {
            try
            {
                particleSystemInstance = __instance;
                var instanceType = __instance.GetType();

                // Resolve fields once — SystemRenderParticles.mainthreadpools/offthreadpools are internal
                mainThreadPoolsField = AccessTools.Field(instanceType, "mainthreadpools");
                offThreadPoolsField = AccessTools.Field(instanceType, "offthreadpools");

                if (mainThreadPoolsField == null || offThreadPoolsField == null)
                    throw new InvalidOperationException("Pool fields not found on " + instanceType.Name);

                var mainPools = mainThreadPoolsField.GetValue(__instance) as Array;
                var offPools = offThreadPoolsField.GetValue(__instance) as Array;

                if (mainPools == null || offPools == null)
                    throw new InvalidOperationException("Pool arrays are null");

                // Resolve MaxParticles property from the first pool element
                if (mainPools.Length > 0)
                {
                    var poolElement = mainPools.GetValue(0);
                    if (poolElement != null)
                        maxParticlesProp = poolElement.GetType().GetProperty("MaxParticles");
                }

                if (maxParticlesProp == null)
                    throw new InvalidOperationException("MaxParticles property not found on pool type");

                originalMainThreadMaxParticles = new int[mainPools.Length];
                originalOffThreadMaxParticles = new int[offPools.Length];

                for (int i = 0; i < mainPools.Length; i++)
                    originalMainThreadMaxParticles[i] = (int)maxParticlesProp.GetValue(mainPools.GetValue(i));

                for (int i = 0; i < offPools.Length; i++)
                    originalOffThreadMaxParticles[i] = (int)maxParticlesProp.GetValue(offPools.GetValue(i));

                ApplyParticleScale(cachedViewDistance);
            }
            catch
            {
                if (!loggedAccessFailure)
                {
                    loggedAccessFailure = true;
                    logger?.Invoke("[OptiTime] Particle pool access failed — MaxParticles not available, scaling disabled");
                }
            }
        }

        public static void UpdateViewDistance(int newViewDistance)
        {
            if (!viewDistanceScalingEnabled) return;
            cachedViewDistance = newViewDistance;
            ApplyParticleScale(newViewDistance);
        }

        private static void ApplyParticleScale(int viewDistance)
        {
            if (!viewDistanceScalingEnabled) return;
            if (particleSystemInstance == null || originalMainThreadMaxParticles == null || maxParticlesProp == null) return;

            try
            {
                float scale = GetParticleScale(viewDistance);

                var mainPools = mainThreadPoolsField.GetValue(particleSystemInstance) as Array;
                var offPools = offThreadPoolsField.GetValue(particleSystemInstance) as Array;

                if (mainPools != null)
                {
                    for (int i = 0; i < mainPools.Length; i++)
                        maxParticlesProp.SetValue(mainPools.GetValue(i), (int)(originalMainThreadMaxParticles[i] * scale));
                }

                if (offPools != null)
                {
                    for (int i = 0; i < offPools.Length; i++)
                        maxParticlesProp.SetValue(offPools.GetValue(i), (int)(originalOffThreadMaxParticles[i] * scale));
                }

                if (ProfilingHelper.Enabled)
                    ProfilingHelper.Mark("opt-particlescale", $"vd={viewDistance},scale={scale:0.00}", countOnly: true);
            }
            catch
            {
                if (!loggedAccessFailure)
                {
                    loggedAccessFailure = true;
                    logger?.Invoke("[OptiTime] Particle pool scaling failed — MaxParticles not available, scaling disabled");
                }
            }
        }

        public static void ConfigureScaling(bool enabled)
        {
            viewDistanceScalingEnabled = enabled;

            if (!enabled && particleSystemInstance != null &&
                originalMainThreadMaxParticles != null &&
                originalOffThreadMaxParticles != null &&
                maxParticlesProp != null)
            {
                try
                {
                    var mainPools = mainThreadPoolsField.GetValue(particleSystemInstance) as Array;
                    var offPools = offThreadPoolsField.GetValue(particleSystemInstance) as Array;

                    if (mainPools != null)
                    {
                        for (int i = 0; i < mainPools.Length; i++)
                            maxParticlesProp.SetValue(mainPools.GetValue(i), originalMainThreadMaxParticles[i]);
                    }

                    if (offPools != null)
                    {
                        for (int i = 0; i < offPools.Length; i++)
                            maxParticlesProp.SetValue(offPools.GetValue(i), originalOffThreadMaxParticles[i]);
                    }
                }
                catch
                {
                    if (!loggedAccessFailure)
                    {
                        loggedAccessFailure = true;
                        logger?.Invoke("[OptiTime] Particle pool restore failed — MaxParticles not available");
                    }
                }
            }
        }
    }
}
