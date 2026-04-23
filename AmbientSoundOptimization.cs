using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace OptiTime
{
    public class AmbientSoundOptimization
    {
        private static Vec3d lastPlayerPos = null;
        private static int fallbackCounter = 0;

        public static void Cleanup()
        {
            lastPlayerPos = null;
            fallbackCounter = 0;
        }

        public static bool ThrottleAmbientSoundUpdates(object __instance)
        {
            try
            {
                var instance = __instance as dynamic;
                var player = instance.game.EntityPlayer;
                var currentPos = player.Pos.XYZ;

                // Fallback: force update every 200ms (10 ticks) regardless
                if (++fallbackCounter >= 10)
                {
                    fallbackCounter = 0;
                }
                else if (lastPlayerPos != null)
                {
                    // Skip if player moved less than 0.3 blocks
                    if (currentPos.SquareDistanceTo(lastPlayerPos) < 0.09) // 0.3^2
                        return false;
                }

                // Reuse Vec3d instance instead of Clone() to avoid allocation
                if (lastPlayerPos == null)
                    lastPlayerPos = new Vec3d();
                lastPlayerPos.Set(currentPos.X, currentPos.Y, currentPos.Z);

                var ambientSounds = instance.ambientSounds;
                foreach (var kvp in (IEnumerable<dynamic>)ambientSounds)
                    kvp.Value.updatePosition(player.Pos);

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
