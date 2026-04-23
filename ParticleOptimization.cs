using HarmonyLib;
using System;

namespace OptiTime
{
    public class ParticleOptimization
    {
        private static object particleSystemInstance = null;
        private static int[] originalMainThreadMaxParticles = null;
        private static int[] originalOffThreadMaxParticles = null;
        private static int cachedViewDistance = 256;
        private static bool viewDistanceScalingEnabled = false;

        public static void Cleanup()
        {
            particleSystemInstance = null;
            originalMainThreadMaxParticles = null;
            originalOffThreadMaxParticles = null;
            cachedViewDistance = 256;
            viewDistanceScalingEnabled = false;
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
                var instance = __instance as dynamic;
                particleSystemInstance = __instance;

                var mainthreadpools = instance.mainthreadpools;
                var offthreadpools = instance.offthreadpools;

                originalMainThreadMaxParticles = new int[mainthreadpools.Length];
                originalOffThreadMaxParticles = new int[offthreadpools.Length];

                for (int i = 0; i < mainthreadpools.Length; i++)
                {
                    originalMainThreadMaxParticles[i] = mainthreadpools[i].MaxParticles;
                }

                for (int i = 0; i < offthreadpools.Length; i++)
                {
                    originalOffThreadMaxParticles[i] = offthreadpools[i].MaxParticles;
                }

                ApplyParticleScale(cachedViewDistance);
            }
            catch { }
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
            if (particleSystemInstance == null || originalMainThreadMaxParticles == null) return;

            try
            {
                var instance = particleSystemInstance as dynamic;
                float scale = GetParticleScale(viewDistance);

                var mainthreadpools = instance.mainthreadpools;
                var offthreadpools = instance.offthreadpools;

                for (int i = 0; i < mainthreadpools.Length; i++)
                {
                    mainthreadpools[i].MaxParticles = (int)(originalMainThreadMaxParticles[i] * scale);
                }

                for (int i = 0; i < offthreadpools.Length; i++)
                {
                    offthreadpools[i].MaxParticles = (int)(originalOffThreadMaxParticles[i] * scale);
                }

                if (ProfilingHelper.Enabled)
                {
                    ProfilingHelper.Mark("opt-particlescale", $"vd={viewDistance},scale={scale:0.00}", countOnly: true);
                }
            }
            catch { }
        }

        public static void ConfigureScaling(bool enabled)
        {
            viewDistanceScalingEnabled = enabled;

            // If scaling was turned off after being on, restore original caps
            if (!enabled && particleSystemInstance != null &&
                originalMainThreadMaxParticles != null &&
                originalOffThreadMaxParticles != null)
            {
                try
                {
                    var instance = particleSystemInstance as dynamic;
                    var mainthreadpools = instance.mainthreadpools;
                    var offthreadpools = instance.offthreadpools;

                    for (int i = 0; i < mainthreadpools.Length; i++)
                    {
                        mainthreadpools[i].MaxParticles = originalMainThreadMaxParticles[i];
                    }

                    for (int i = 0; i < offthreadpools.Length; i++)
                    {
                        offthreadpools[i].MaxParticles = originalOffThreadMaxParticles[i];
                    }
                }
                catch { }
            }
        }
    }
}
