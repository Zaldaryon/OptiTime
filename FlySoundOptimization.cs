using HarmonyLib;
using System;

namespace OptiTime
{
    public class FlySoundOptimization
    {
        private static float lastVolume = 0f;

        public static bool OptimizeFlySound(object __instance, float dt)
        {
            try
            {
                var instance = __instance as dynamic;
                var flySound = instance.FlySound;

                double flySpeed = instance.flySpeed;
                float curVolume = instance.curVolume;

                bool flag = Math.Abs(flySpeed) - 0.05 > 0.2;
                if (flag && !instance.fallActive && !flySound.IsPlaying)
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

                    instance.curVolume = newVolume;
                    instance.targetVolume = targetVolume;
                }
                else
                {
                    instance.targetVolume = 0.0f;
                }
                instance.fallActive = flag;

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
