using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Skips the redundant Primary FBO bind and full-resolution viewport bounce at the end of
    /// ShadowFar when ShadowNear will immediately bind ShadowmapNear without drawing anything
    /// to Primary. Fails open whenever ShadowQuality &lt;= 1 or any other renderer listens on
    /// EnumRenderStage.ShadowFarDone.
    /// </summary>
    public static class ShadowPassTransitionOptimization
    {
        private static readonly AccessTools.FieldRef<SystemRenderShadowMap, ClientMain> gameRef;
        private static readonly bool accessorsReady;

        private static volatile bool enabled;

        static ShadowPassTransitionOptimization()
        {
            try
            {
                gameRef = AccessTools.FieldRefAccess<SystemRenderShadowMap, ClientMain>("game");
                accessorsReady = true;
            }
            catch
            {
                accessorsReady = false;
            }
        }

        public static bool IsEnabled => enabled && accessorsReady;

        public static void Configure(bool isEnabled)
        {
            enabled = isEnabled;
        }

        public static void Cleanup()
        {
            enabled = false;
        }

        internal static bool ShouldSkipPrimaryBounce(
            int shadowQuality,
            float dropShadowIntensity,
            int shadowFarDoneRendererCount,
            int shadowNearRendererCount)
        {
            return shadowQuality > 1 &&
                dropShadowIntensity > 0.01f &&
                shadowFarDoneRendererCount == 1 &&
                shadowNearRendererCount > 0;
        }

        public static bool OnRenderShadowFarDone_Prefix(SystemRenderShadowMap __instance, float dt)
        {
            if (!enabled || !accessorsReady || __instance == null)
            {
                return true;
            }

            ClientMain game = gameRef(__instance);
            if (game == null || game.AmbientManager == null || game.eventManager == null)
            {
                return true;
            }

            List<RenderHandler>[] stages = game.eventManager.renderersByStage;
            if (stages == null ||
                stages.Length <= (int)EnumRenderStage.ShadowNear ||
                stages.Length <= (int)EnumRenderStage.ShadowFarDone)
            {
                return true;
            }

            List<RenderHandler> farDoneHandlers = stages[(int)EnumRenderStage.ShadowFarDone];
            List<RenderHandler> nearHandlers = stages[(int)EnumRenderStage.ShadowNear];
            int farDoneCount = farDoneHandlers != null ? farDoneHandlers.Count : 0;
            int nearCount = nearHandlers != null ? nearHandlers.Count : 0;

            if (!ShouldSkipPrimaryBounce(
                game.AmbientManager.ShadowQuality,
                game.shUniforms != null ? game.shUniforms.DropShadowIntensity : 0f,
                farDoneCount,
                nearCount))
            {
                return true;
            }

            ShaderProgramBase currentShader = ShaderProgramBase.CurrentShaderProgram;
            if (currentShader != null && !currentShader.Disposed)
            {
                currentShader.Stop();
            }

            game.PMatrix.Pop();
            game.MvMatrix.Pop();
            return false;
        }
    }
}
