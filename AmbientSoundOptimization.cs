using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public class AmbientSoundOptimization
    {
        private static Vec3d lastPlayerPos;
        private static int fallbackCounter;

        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
        private static readonly AccessTools.FieldRef<SystemPlayerSounds, Dictionary<AmbientSound, AmbientSound>> ambientSoundsRef =
            AccessTools.FieldRefAccess<SystemPlayerSounds, Dictionary<AmbientSound, AmbientSound>>("ambientSounds");
        private static readonly MethodInfo updatePositionMethod =
            AccessTools.Method(typeof(AmbientSound), "updatePosition", new[] { typeof(EntityPos) });

        public static void Cleanup()
        {
            lastPlayerPos = null;
            fallbackCounter = 0;
        }

        public static bool ThrottleAmbientSoundUpdates(SystemPlayerSounds __instance)
        {
            try
            {
                var game = gameRef(__instance);
                var player = game.EntityPlayer;
                var currentPos = player.Pos.XYZ;

                if (++fallbackCounter >= 10)
                {
                    fallbackCounter = 0;
                }
                else if (lastPlayerPos != null)
                {
                    if (currentPos.SquareDistanceTo(lastPlayerPos) < 0.09)
                        return false;
                }

                if (lastPlayerPos == null)
                    lastPlayerPos = new Vec3d();
                lastPlayerPos.Set(currentPos.X, currentPos.Y, currentPos.Z);

                var ambientSounds = ambientSoundsRef(__instance);
                var posArg = new object[] { player.Pos };
                foreach (var kvp in ambientSounds)
                    updatePositionMethod.Invoke(kvp.Value, posArg);

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
