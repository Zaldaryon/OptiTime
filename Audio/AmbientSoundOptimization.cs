using HarmonyLib;
using Vintagestory.Client.NoObf;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public class AmbientSoundOptimization
    {
        private const double PositionThresholdSquared = 0.09;
        private const int FallbackTickInterval = 10;

        private static double lastPlayerX = double.NaN;
        private static double lastPlayerY = double.NaN;
        private static double lastPlayerZ = double.NaN;
        private static int fallbackCounter;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");

        public static void Cleanup()
        {
            lastPlayerX = double.NaN;
            lastPlayerY = double.NaN;
            lastPlayerZ = double.NaN;
            fallbackCounter = 0;
        }

        public static bool ThrottleAmbientSoundUpdates(SystemPlayerSounds __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }

                var pos = gameRef(__instance)?.EntityPlayer?.Pos;
                if (pos == null)
                {
                    return true;
                }

                double x = pos.X;
                double y = pos.InternalY;
                double z = pos.Z;

                if (++fallbackCounter < FallbackTickInterval)
                {
                    double dx = x - lastPlayerX;
                    double dy = y - lastPlayerY;
                    double dz = z - lastPlayerZ;
                    if (dx * dx + dy * dy + dz * dz < PositionThresholdSquared)
                    {
                        ModuleAmbientSound.OnCall(true);
                        return false;
                    }
                }

                fallbackCounter = 0;
                lastPlayerX = x;
                lastPlayerY = y;
                lastPlayerZ = z;
                ModuleAmbientSound.OnCall(false);
                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
