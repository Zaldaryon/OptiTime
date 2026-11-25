using System;
using System.Collections.Generic;

namespace OptiTime
{
    /// <summary>
    /// Canonical IL signatures of the vanilla methods OptiTime can patch, and content hashes of the vanilla
    /// shader files it can override, for each supported game version. They are the reference
    /// <see cref="OptimumOwnership"/> compares the running game against. The unit test
    /// <c>VanillaSignatureTableMatchesTheGameAssemblies</c> recomputes them from the game install and prints
    /// the replacement table when they drift. A game version without an entry makes OptiTime yield every
    /// optimization that would patch a method when Optimum is the host.
    /// </summary>
    internal static class OptimumVanillaSignatures
    {
        private static readonly Dictionary<string, string> Methods1227 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EntityBehaviorInterpolatePosition:OnReceivedServerPos(System.Boolean,Vintagestory.API.Common.EnumHandling&)"] = "135:ac67b1d0fc6c0c535b0282e6",
            ["Vintagestory.API.Client.GuiComposer:PostRender(System.Single)"] = "72:0e1c3e07b09c16e5782dfc3a",
            ["Vintagestory.API.Client.GuiElementItemSlotGridBase:ComposeInteractiveElements()"] = "480:26de8fb8f5322bf9303c2f0e",
            ["Vintagestory.API.Common.FrameProfilerUtil:Leave()"] = "47:79fe32e9df5a603e8b225905",
            ["Vintagestory.API.Common.GridRecipe:Matches(Vintagestory.API.Common.IPlayer,Vintagestory.API.Common.IWorldAccessor,Vintagestory.API.Common.ItemSlot[],System.Int32)"] = "99:300b0603a47c9043712cc202",
            ["Vintagestory.API.Config.TranslationService:HasTranslation(System.String,System.Boolean)"] = "6:2b4820e9e52c831410ef0cdd",
            ["Vintagestory.API.Config.TranslationService:HasTranslation(System.String,System.Boolean,System.Boolean)"] = "85:ce229ad8d31eacad1b2020d7",
            ["Vintagestory.Client.NoObf.ChunkCuller:CullInvisibleChunks()"] = "327:89825b238cf6992fec119738",
            ["Vintagestory.Client.NoObf.ChunkRenderer:RenderShadow(System.Single)"] = "175:d29303c68fc30412be231953",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:window_RenderFrame(OpenTK.Windowing.Common.FrameEventArgs)"] = "116:70287b1a5f630580f26fd153",
            ["Vintagestory.Client.NoObf.GuiManager:OnBeforeRenderFrame3D(System.Single)"] = "26:b2fad83981bb4fbcdd38be82",
            ["Vintagestory.Client.NoObf.GuiManager:OnFinalizeFrame(System.Single)"] = "27:740c83d4eb11bb379a3fc2bb",
            ["Vintagestory.Client.NoObf.GuiManager:OnKeyDown(Vintagestory.API.Client.KeyEvent)"] = "73:50ded9903b4434c6ecceaf22",
            ["Vintagestory.Client.NoObf.GuiManager:OnKeyPress(Vintagestory.API.Client.KeyEvent)"] = "29:5a5a2a305bb876ad3314cc8f",
            ["Vintagestory.Client.NoObf.GuiManager:OnKeyUp(Vintagestory.API.Client.KeyEvent)"] = "29:dea9ef325ffec2f19171c4b8",
            ["Vintagestory.Client.NoObf.GuiManager:OnMouseDown(Vintagestory.API.Client.MouseEvent)"] = "47:0cdfeb64b95f12951807d590",
            ["Vintagestory.Client.NoObf.GuiManager:OnMouseMove(Vintagestory.API.Client.MouseEvent)"] = "38:7810e859813e1e2d621d6e5f",
            ["Vintagestory.Client.NoObf.GuiManager:OnMouseUp(Vintagestory.API.Client.MouseEvent)"] = "44:18621243697caef8e8b24738",
            ["Vintagestory.Client.NoObf.GuiManager:OnRenderFrameGUI(System.Single)"] = "72:4ceb05aaffa68dad196ad27e",
            ["Vintagestory.Client.NoObf.ParticlePoolQuads:SpawnParticles(Vintagestory.API.Common.IParticlePropertiesProvider)"] = "223:a88966a2ed52981e6c5558bb",
            ["Vintagestory.Client.NoObf.SystemClientTickingBlocks:onOffThreadParticleTick(System.Single,Vintagestory.API.Client.IAsyncParticleManager)"] = "178:084f6fdfddebe853008b9ef6",
            ["Vintagestory.Client.NoObf.SystemPlayerSounds:OnGameTick(System.Single)"] = "24:c02f8a2a80cfea2d3945103c",
            ["Vintagestory.Client.NoObf.SystemPlayerSounds:updateFlySound(System.Single)"] = "82:aa30ced64be4881389b16d1d",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnBeforeRender(System.Single)"] = "146:0e216bc4f4bb09ef05a228c1",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrameShadows(System.Single)"] = "185:6e4b5bc3c26b0d1f7b30088b",
            ["Vintagestory.Client.NoObf.SystemRenderParticles:OnRenderFrame3D(System.Single)"] = "17:dafc549b42711282c2dbf4d4",
            ["Vintagestory.Client.NoObf.SystemRenderPlayerEffects:onBeforeRender(System.Single)"] = "101:65a64dc8acba526a7bc29269",
            ["Vintagestory.Client.ScreenManager:OnNewFrame(System.Single)"] = "99:eb760b72002b05a977a608a3",
            ["Vintagestory.Common.InventoryCraftingGrid:FindMatchingRecipe()"] = "165:b2ab2626b1844078d3a6dc84",
            ["Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo:GetHandbookInfo(Vintagestory.API.Common.ItemSlot,Vintagestory.API.Client.ICoreClientAPI,Vintagestory.API.Common.ItemStack[],Vintagestory.API.Common.ActionConsumable<System.String>)"] = "331:abddee5fb85066d15c85700b",
            ["Vintagestory.GameContent.EntityBehaviorRepulseAgents:OnGameTick(System.Single)"] = "170:0569a0ac05cab57e48884fca",
            ["Vintagestory.GameContent.WeatherSystemClient:OnRenderFrame(System.Single,Vintagestory.API.Client.EnumRenderStage)"] = "428:bc335716eb5a35acb7963d0d",
        };

        private static readonly Dictionary<string, string> Shaders1227 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shaders/blur.fsh"] = "43a89306bb418f4fdd044a1f63f78c3f5f1c08256f81231ed4357d80cc08ce21",
            ["shaders/blur.vsh"] = "27fbfd27a799f59919a93b84ecfc9900efd228af000f1eac9d5ba08d52529f17",
            ["shaders/chunkliquid.fsh"] = "ce805570dd44781a8dcf306efe2f5ae618667055779f7592c78c8c328c6ce694",
            ["shaders/chunkshadowmap.fsh"] = "a96252453f35c30a7df5f2f0d4e174f4f01fb02a0037aaa1b1fb75d09028ef8a",
        };

        internal static IReadOnlyDictionary<string, string> ForGameVersion(string gameVersion)
        {
            return gameVersion == "1.22.7" ? Methods1227 : null;
        }

        internal static IReadOnlyDictionary<string, string> ShadersForGameVersion(string gameVersion)
        {
            return gameVersion == "1.22.7" ? Shaders1227 : null;
        }
    }
}
