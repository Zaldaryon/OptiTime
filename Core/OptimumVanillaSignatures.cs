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
            ["Vintagestory.API.Client.FrustumCulling:CalcFrustumEquations(System.Double[])"] = "247:12899cf8d914e5c1d4979b54",
            ["Vintagestory.API.Client.FrustumCulling:CalcFrustumEquations(Vintagestory.API.MathTools.BlockPos,System.Double[],System.Double[])"] = "14:c8cefa42c7c8e577edb2ce74",
            ["Vintagestory.API.Client.FrustumCulling:InFrustumAndRange(Vintagestory.API.MathTools.Sphere,System.Boolean,System.Int32)"] = "100:57fc811bce769ca02486db53",
            ["Vintagestory.API.Client.GuiComposer:PostRender(System.Single)"] = "72:0e1c3e07b09c16e5782dfc3a",
            ["Vintagestory.API.Client.GuiElementItemSlotGridBase:ComposeInteractiveElements()"] = "480:26de8fb8f5322bf9303c2f0e",
            ["Vintagestory.API.Client.ModelDataPoolLocation:IsVisible(Vintagestory.API.Client.EnumFrustumCullMode,Vintagestory.API.Client.FrustumCulling)"] = "83:0ab2293d35b2ee7d83159915",
            ["Vintagestory.API.Common.AnimatableRenderer:Dispose()"] = "15:a63a58b077bea27d44ce465f",
            ["Vintagestory.API.Common.AnimatableRenderer:OnRenderFrame(System.Single,Vintagestory.API.Client.EnumRenderStage)"] = "367:5832f3928103354a76a65bfc",
            ["Vintagestory.API.Common.AnimatableRenderer:mainThreadInit(Vintagestory.API.Client.ICoreClientAPI,Vintagestory.API.Client.MeshData,Vintagestory.API.Client.EnumRenderStage)"] = "86:41dbccfd8eb7dbfe7525b0e8",
            ["Vintagestory.API.Common.AnimationManager:OnClientFrame(System.Single)"] = "91:8b8fc40983dc8f5372183a9d",
            ["Vintagestory.API.Common.AnimationUtil:Dispose()"] = "19:4d1d509d9a30ce4e70bc3e02",
            ["Vintagestory.API.Common.AnimationUtil:OnRenderFrame(System.Single,Vintagestory.API.Client.EnumRenderStage)"] = "55:06da1b883ba2393f90089b1b",
            ["Vintagestory.API.Common.AnimationUtil:StartAnimation(Vintagestory.API.Common.AnimationMetaData)"] = "22:87a99571d578007ee54c59af",
            ["Vintagestory.API.Common.FrameProfilerUtil:Leave()"] = "47:79fe32e9df5a603e8b225905",
            ["Vintagestory.API.Common.GridRecipe:Matches(Vintagestory.API.Common.IPlayer,Vintagestory.API.Common.IWorldAccessor,Vintagestory.API.Common.ItemSlot[],System.Int32)"] = "99:300b0603a47c9043712cc202",
            ["Vintagestory.API.Config.TranslationService:HasTranslation(System.String,System.Boolean)"] = "6:2b4820e9e52c831410ef0cdd",
            ["Vintagestory.API.Config.TranslationService:HasTranslation(System.String,System.Boolean,System.Boolean)"] = "85:ce229ad8d31eacad1b2020d7",
            ["Vintagestory.Client.NoObf.ChunkCuller:CullInvisibleChunks()"] = "327:89825b238cf6992fec119738",
            ["Vintagestory.Client.NoObf.ChunkRenderer:OnRenderBefore(System.Single)"] = "115:94d642519957f4c2cd49e2cc",
            ["Vintagestory.Client.NoObf.ChunkRenderer:RenderOIT(System.Single)"] = "341:3671a8d9c085d04760584eab",
            ["Vintagestory.Client.NoObf.ChunkRenderer:RenderOpaque(System.Single)"] = "480:e27b8a9293c4d81f1d916216",
            ["Vintagestory.Client.NoObf.ChunkRenderer:RenderShadow(System.Single)"] = "175:d29303c68fc30412be231953",
            ["Vintagestory.Client.NoObf.ChunkTesselatorManager:OnBeforeFrame(System.Single)"] = "260:b28c83a617b89aa9b0eadca1",
            ["Vintagestory.Client.NoObf.ClientMain:GlTranslate(System.Double,System.Double,System.Double)"] = "31:a9a2daf2c52d9e8475ca4a1c",
            ["Vintagestory.Client.NoObf.ClientMain:GlTranslate(Vintagestory.API.MathTools.Vec3d)"] = "15:2378e40bd817d0f06f47939c",
            ["Vintagestory.Client.NoObf.ClientMain:RemoveEntityRenderer(Vintagestory.API.Common.Entities.Entity)"] = "23:400816ef24be67a5c06e900b",
            ["Vintagestory.Client.NoObf.ClientMain:TriggerRenderStage(Vintagestory.API.Client.EnumRenderStage,System.Single)"] = "26:9d2d7655c6ea7199b23657a7",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:ClearFrameBuffer(Vintagestory.API.Client.EnumFrameBuffer)"] = "109:c4fc273647aa66bcd2e3f6cb",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:ClearFrameBuffer(Vintagestory.API.Client.FrameBufferRef,System.Boolean)"] = "8:64f5a0ecc1920361054661bc",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:ClearFrameBuffer(Vintagestory.API.Client.FrameBufferRef,System.Single[],System.Boolean,System.Boolean)"] = "31:f542ab875ad0ac33d4e3e40b",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:LoadFrameBuffer(Vintagestory.API.Client.EnumFrameBuffer)"] = "294:08026870732230b4e4cad0c2",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:LoadFrameBuffer(Vintagestory.API.Client.FrameBufferRef,System.Int32)"] = "10:5fccbc735135d264dc8786d0",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:RenderFinalComposition()"] = "253:1299be18a2f10d8376b18a8b",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:RenderPostprocessingEffects(System.Single[])"] = "594:11b45e6b6d27d180709c148b",
            ["Vintagestory.Client.NoObf.ClientPlatformWindows:SetupDefaultFrameBuffers()"] = "1606:690fa0a959fc1a6cabd9a244",
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
            ["Vintagestory.Client.NoObf.HudEntityNameTags:OnRenderGUI(System.Single)"] = "68:cfd5d656199be7e8ee92f1ec",
            ["Vintagestory.Client.NoObf.InventoryItemRenderer:GetItemStackRenderInfo(Vintagestory.Client.NoObf.ClientMain,Vintagestory.API.Common.ItemSlot,Vintagestory.API.Client.EnumItemRenderTarget,System.Single)"] = "312:7f0c7eb3102a146477943e80",
            ["Vintagestory.Client.NoObf.InventoryItemRenderer:RenderItemstackToGui(Vintagestory.API.Common.ItemSlot,System.Double,System.Double,System.Double,System.Single,System.Int32,System.Boolean,System.Boolean,System.Boolean)"] = "13:17524bbcfa9d032320add68d",
            ["Vintagestory.Client.NoObf.InventoryItemRenderer:RenderItemstackToGui(Vintagestory.API.Common.ItemSlot,System.Double,System.Double,System.Double,System.Single,System.Int32,System.Single,System.Boolean,System.Boolean,System.Boolean)"] = "582:d1107ccb98974f937fe82eba",
            ["Vintagestory.Client.NoObf.ParticlePoolQuads:SpawnParticles(Vintagestory.API.Common.IParticlePropertiesProvider)"] = "223:a88966a2ed52981e6c5558bb",
            ["Vintagestory.Client.NoObf.SystemClientTickingBlocks:onOffThreadParticleTick(System.Single,Vintagestory.API.Client.IAsyncParticleManager)"] = "178:084f6fdfddebe853008b9ef6",
            ["Vintagestory.Client.NoObf.SystemPlayerSounds:OnGameTick(System.Single)"] = "24:c02f8a2a80cfea2d3945103c",
            ["Vintagestory.Client.NoObf.SystemPlayerSounds:updateFlySound(System.Single)"] = "82:aa30ced64be4881389b16d1d",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnBeforeRender(System.Single)"] = "146:0e216bc4f4bb09ef05a228c1",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderAfterOIT(System.Single)"] = "45:589707493c903b2f3afcd3fe",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrame2D(System.Single)"] = "32:5ab9d743ede113cd69381819",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderFrameShadows(System.Single)"] = "185:6e4b5bc3c26b0d1f7b30088b",
            ["Vintagestory.Client.NoObf.SystemRenderEntities:OnRenderOpaque3D(System.Single)"] = "173:12fe4c03dfb2148a02fff2c0",
            ["Vintagestory.Client.NoObf.SystemRenderParticles:OnRenderFrame3D(System.Single)"] = "17:dafc549b42711282c2dbf4d4",
            ["Vintagestory.Client.NoObf.SystemRenderPlayerEffects:onBeforeRender(System.Single)"] = "101:65a64dc8acba526a7bc29269",
            ["Vintagestory.Client.NoObf.SystemRenderShadowMap:OnRenderShadowFarDone(System.Single)"] = "23:e8fbba3444ac3ceced1542b3",
            ["Vintagestory.Client.NoObf.SystemRenderSunMoon:Dispose(Vintagestory.Client.NoObf.ClientMain)"] = "34:16f6d5f345a1e51288d0a790",
            ["Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3D(System.Single)"] = "857:a9442ab9981c811929aaf832",
            ["Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3DPost(System.Single)"] = "227:fc4d5e35eb883efe2e5cb398",
            ["Vintagestory.Client.ScreenManager:OnNewFrame(System.Single)"] = "99:eb760b72002b05a977a608a3",
            ["Vintagestory.Common.CreativeTab:CreateSearchCache(Vintagestory.API.Common.IWorldAccessor)"] = "77:e461e42e1fd09764e897caf9",
            ["Vintagestory.Common.InventoryCraftingGrid:FindMatchingRecipe()"] = "165:b2ab2626b1844078d3a6dc84",
            ["Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo:GetHandbookInfo(Vintagestory.API.Common.ItemSlot,Vintagestory.API.Client.ICoreClientAPI,Vintagestory.API.Common.ItemStack[],Vintagestory.API.Common.ActionConsumable<System.String>)"] = "331:abddee5fb85066d15c85700b",
            ["Vintagestory.GameContent.EntityBehaviorRepulseAgents:OnGameTick(System.Single)"] = "170:0569a0ac05cab57e48884fca",
            ["Vintagestory.GameContent.EntityShapeRenderer:TesselateShape()"] = "17:8c0ed5faea4a5dca3d0464f7",
            ["Vintagestory.GameContent.EntityShapeRenderer:TesselateShape(System.Action<Vintagestory.API.Client.MeshData>,System.String[])"] = "69:eabcd8b2238f7c81e021b5f6",
            ["Vintagestory.GameContent.EntityShapeRenderer:loadModelMatrix(Vintagestory.API.Common.Entities.Entity,System.Single,System.Boolean)"] = "449:a198590fd16090b66b61319e",
            ["Vintagestory.GameContent.ModSystemSurvivalHandbook:onCreatePagesAsync()"] = "125:0d506e4b68283e85c8967fa2",
            ["Vintagestory.GameContent.WeatherSystemClient:OnRenderFrame(System.Single,Vintagestory.API.Client.EnumRenderStage)"] = "428:bc335716eb5a35acb7963d0d",
        };

        private static readonly Dictionary<string, string> Shaders1227 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shaderincludes/fogandlight.fsh"] = "b212b9094f4934cf42aa3da72bdb9973196bb618014d8633ebd522a807af7130",
            ["shaderincludes/underwatereffects.fsh"] = "639494d529a53d9971f96f714da19f0d1d468ba797c942f638ae86c4c449b2b4",
            ["shaders/bilateralblur.fsh"] = "7ac33c2370fe02f4e629ae9594e2cc6bf8c673b23e1f39c11c4d5c2f53ef7f41",
            ["shaders/blur.fsh"] = "43a89306bb418f4fdd044a1f63f78c3f5f1c08256f81231ed4357d80cc08ce21",
            ["shaders/blur.vsh"] = "27fbfd27a799f59919a93b84ecfc9900efd228af000f1eac9d5ba08d52529f17",
            ["shaders/chunkliquid.fsh"] = "ce805570dd44781a8dcf306efe2f5ae618667055779f7592c78c8c328c6ce694",
            ["shaders/chunkliquid.vsh"] = "399fc721e72ce271c4bd56810bcacc82be974ff229f65a49273988afd0ec0510",
            ["shaders/chunkopaque.fsh"] = "546a8bce43b0e4032399cea9e18e6b1659df521dc4daf77176b7c13949605175",
            ["shaders/chunkopaque.vsh"] = "12997dede5eddb3376a943a588a50fcbc44f921c7045c2e6412f29a42f66431e",
            ["shaders/chunkshadowmap.fsh"] = "a96252453f35c30a7df5f2f0d4e174f4f01fb02a0037aaa1b1fb75d09028ef8a",
            ["shaders/chunkshadowmap.vsh"] = "1cf6eb9b879fe868f404ac46df9b27531abade3c697b6eb0f5acfe2a15117c99",
            ["shaders/chunktopsoil.fsh"] = "c1ba47dff1caab8533bfdc25ed37be4315b77cb953f72e6b473bf0596cf6369f",
            ["shaders/chunktopsoil.vsh"] = "43aca2a3f6ec64c7c0b97c5d6bb03f03ced53cdae6b000684d9bcb25b8f98cdf",
            ["shaders/cloudvolumetric.fsh"] = "661dba616027089f620b7f760b90ee79b730d61aedd286986494f81da4fc0a82",
            ["shaders/final.fsh"] = "b29951b8db8b5485896d3e6b6dff565c335d56225fae4073506159c0139f5b13",
            ["shaders/final.vsh"] = "af5573b194df2ce49cd4631441595d908c1252e94f3cfa8557e926218500d934",
            ["shaders/godrays.fsh"] = "bc3396f4450cebc03138e711811c6e47f777626ea804cf1acad8cdd548b13064",
            ["shaders/ssao.fsh"] = "9f4b5a1a3c0fb5b76202c948c9d1369aaf2d532bdf967a3204d1dc96a2310a4d",
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
