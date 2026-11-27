using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace OptiTime
{
    /// <summary>
    /// Reorders FrustumCulling.InFrustumAndRange on the client so the inexpensive
    /// horizontal distance and LOD bounds checks short-circuit before running the
    /// five 3D plane-AABB tests, preserving bit-exact results with zero allocations.
    /// </summary>
    public static class FrustumCullingOptimization
    {
        private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> playerPosRef;
        private static readonly AccessTools.FieldRef<FrustumCulling, Plane[]> frustumRef;
        private static readonly bool accessorsReady;

        private static volatile bool clientActive;

        static FrustumCullingOptimization()
        {
            try
            {
                playerPosRef = AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");
                frustumRef = AccessTools.FieldRefAccess<FrustumCulling, Plane[]>("frustum");
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
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InFrustumAndRange_Prefix(FrustumCulling __instance, Sphere sphere, int lodLevel, ref bool __result)
        {
            if (!clientActive || !accessorsReady || __instance == null)
            {
                return true;
            }

            BlockPos playerPos = playerPosRef(__instance);
            if (playerPos == null)
            {
                return true;
            }

            bool inRange;
            switch (lodLevel)
            {
                case 0:
                    inRange = __instance.lod0BiasSq > 0
                        && (double)playerPos.HorDistanceSqTo(sphere.x, sphere.z) < __instance.lod0BiasSq + 32 * 32;
                    break;
                case 1:
                    inRange = (double)playerPos.HorDistanceSqTo(sphere.x, sphere.z) < __instance.ViewDistanceSq;
                    break;
                case 2:
                    inRange = (double)playerPos.HorDistanceSqTo(sphere.x, sphere.z) <= __instance.lod2BiasSq;
                    break;
                case 3:
                {
                    double distance = playerPos.HorDistanceSqTo(sphere.x, sphere.z);
                    inRange = distance > __instance.lod2BiasSq && distance < __instance.ViewDistanceSq;
                    break;
                }
                default:
                    __result = false;
                    return false;
            }

            if (!inRange)
            {
                __result = false;
                return false;
            }

            Plane[] frustum = frustumRef(__instance);
            if (frustum == null || frustum.Length < 5)
            {
                return true;
            }

            __result = !frustum[0].AABBisOutside(sphere)
                && !frustum[1].AABBisOutside(sphere)
                && !frustum[2].AABBisOutside(sphere)
                && !frustum[3].AABBisOutside(sphere)
                && !frustum[4].AABBisOutside(sphere);
            return false;
        }
    }
}
