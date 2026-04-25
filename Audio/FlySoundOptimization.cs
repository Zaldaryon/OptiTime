using HarmonyLib;
using System;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    public class FlySoundOptimization
    {
        private static float lastVolume;

        private static readonly AccessTools.FieldRef<SystemPlayerSounds, ILoadedSound> flySoundRef =
            AccessTools.FieldRefAccess<SystemPlayerSounds, ILoadedSound>("FlySound");
        private static readonly AccessTools.FieldRef<SystemPlayerSounds, double> flySpeedRef =
            AccessTools.FieldRefAccess<SystemPlayerSounds, double>("flySpeed");
        private static readonly AccessTools.FieldRef<SystemPlayerSounds, float> curVolumeRef =
            AccessTools.FieldRefAccess<SystemPlayerSounds, float>("curVolume");
        private static readonly AccessTools.FieldRef<SystemPlayerSounds, float> targetVolumeRef =
            AccessTools.FieldRefAccess<SystemPlayerSounds, float>("targetVolume");
        private static readonly AccessTools.FieldRef<SystemPlayerSounds, bool> fallActiveRef =
            AccessTools.FieldRefAccess<SystemPlayerSounds, bool>("fallActive");

        public static bool OptimizeFlySound(SystemPlayerSounds __instance, float dt)
        {
            try
            {
                var flySound = flySoundRef(__instance);
                double flySpeed = flySpeedRef(__instance);
                float curVolume = curVolumeRef(__instance);

                bool flag = Math.Abs(flySpeed) - 0.05 > 0.2;
                if (flag && !fallActiveRef(__instance) && !flySound.IsPlaying)
                    flySound.Start();
                if (!flag && curVolume < 0.08f && flySound.IsPlaying)
                    flySound.Stop();

                if (flySound.IsPlaying)
                {
                    float targetVolume = flag ? Math.Min(1f, Math.Abs((float)flySpeed)) : 0.0f;
                    float newVolume = Math.Max(0.0f, Math.Min(1f,
                        curVolume + (targetVolume - curVolume) * dt * (flag ? 1.0f : 5.0f)));

                    if (Math.Abs(newVolume - lastVolume) > 0.01f)
                    {
                        flySound.SetVolume(newVolume);
                        lastVolume = newVolume;
                    }

                    curVolumeRef(__instance) = newVolume;
                    targetVolumeRef(__instance) = targetVolume;
                }
                else
                {
                    targetVolumeRef(__instance) = 0.0f;
                }
                fallActiveRef(__instance) = flag;

                return false;
            }
            catch
            {
                return true;
            }
        }

        public static void Cleanup()
        {
            lastVolume = 0f;
        }
    }
}
