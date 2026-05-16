using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using Vintagestory.API.MathTools;
using OptiTime.Diagnostics;

namespace OptiTime
{
    /// <summary>
    /// Throttles the two GetWindSpeedAt + GetRainMapHeightAt calls in
    /// WeatherSystemClient.OnRenderFrame from every frame to every 4th frame.
    /// The wind speed is already lerp-smoothed, so skipping lookups on
    /// intermediate frames produces identical visual results.
    /// </summary>
    public static class WeatherWindOptimization
    {
        private static int frameCounter;
        private const int UpdateInterval = 4;
        private static readonly Vec3d cachedWind = new Vec3d();
        private static readonly Vec3d cachedSurfaceWind = new Vec3d();
        private static int cachedRainHeight;
        private static bool initialized;

        // Called by transpiled code instead of the first BlockAccessor.GetWindSpeedAt
        // Also increments the frame counter (only called once per frame in Before stage)
        public static Vec3d GetWindSpeedThrottled(
            Vintagestory.API.Common.IBlockAccessor ba, Vec3d pos)
        {
            frameCounter++;
            bool isSkipped = initialized && frameCounter % UpdateInterval != 0;
            ModuleWeatherWind.OnTick(isSkipped);
            if (!initialized || frameCounter % UpdateInterval == 0)
            {
                var r = ba.GetWindSpeedAt(pos);
                cachedWind.Set(r.X, r.Y, r.Z);

                if (!initialized)
                {
                    // Pre-fill all caches on first frame to avoid returning zeros
                    var sw = ba.GetWindSpeedAt(pos);
                    cachedSurfaceWind.Set(sw.X, sw.Y, sw.Z);
                    cachedRainHeight = ba.GetRainMapHeightAt((int)pos.X, (int)pos.Z);
                }

                initialized = true;
                return r;
            }
            return cachedWind;
        }

        // Second call (surface wind) uses separate cache, no counter increment
        public static Vec3d GetSurfaceWindSpeedThrottled(
            Vintagestory.API.Common.IBlockAccessor ba, Vec3d pos)
        {
            if (frameCounter % UpdateInterval != 0)
                return cachedSurfaceWind;

            var r = ba.GetWindSpeedAt(pos);
            cachedSurfaceWind.Set(r.X, r.Y, r.Z);
            return r;
        }

        public static int GetRainMapHeightThrottled(
            Vintagestory.API.Common.IBlockAccessor ba, int x, int z)
        {
            if (frameCounter % UpdateInterval != 0)
                return cachedRainHeight;

            cachedRainHeight = ba.GetRainMapHeightAt(x, z);
            return cachedRainHeight;
        }

        public static void Cleanup()
        {
            frameCounter = 0;
            initialized = false;
        }

        /// <summary>
        /// Transpiler for WeatherSystemClient.OnRenderFrame.
        /// Replaces the two GetWindSpeedAt calls and one GetRainMapHeightAt call
        /// with throttled cached versions. No TickFrame injection needed —
        /// the first wind call increments the counter itself.
        /// </summary>
        public static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions)
        {
            var getWindSpeed = AccessTools.Method(
                typeof(Vintagestory.API.Common.IBlockAccessor),
                nameof(Vintagestory.API.Common.IBlockAccessor.GetWindSpeedAt),
                new[] { typeof(Vec3d) });

            var getRainHeight = AccessTools.Method(
                typeof(Vintagestory.API.Common.IBlockAccessor),
                nameof(Vintagestory.API.Common.IBlockAccessor.GetRainMapHeightAt),
                new[] { typeof(int), typeof(int) });

            if (getWindSpeed == null || getRainHeight == null)
                return instructions;

            var throttledWind = AccessTools.Method(typeof(WeatherWindOptimization),
                nameof(GetWindSpeedThrottled));
            var throttledSurfaceWind = AccessTools.Method(typeof(WeatherWindOptimization),
                nameof(GetSurfaceWindSpeedThrottled));
            var throttledRain = AccessTools.Method(typeof(WeatherWindOptimization),
                nameof(GetRainMapHeightThrottled));

            var codes = new List<CodeInstruction>(instructions);
            int windCallIndex = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(getWindSpeed))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call,
                        windCallIndex == 0 ? throttledWind : throttledSurfaceWind);
                    windCallIndex++;
                }
                else if (codes[i].Calls(getRainHeight))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, throttledRain);
                }
            }

            return codes;
        }
    }
}
