using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace OptiTime
{
    /// <summary>
    /// Reuses a render-thread scratch ItemRenderInfo inside
    /// InventoryItemRenderer.RenderItemstackToGui while leaving the public
    /// GetItemStackRenderInfo API returning fresh instances for external callers.
    /// </summary>
    public static class GuiItemRenderInfoOptimization
    {
        private const string RenderVariantAttribute = "renderVariant";
        private static readonly AssetLocation rotOverlayLocation = new AssetLocation("textures/gui/rot.png");

        private static volatile bool clientActive;
        private static volatile bool transpilerApplied;
        private static int sessionEpoch;
        private static Action<string> logger;

        private sealed class ThreadScratch
        {
            public readonly ItemRenderInfo Info;
            public readonly Size2i TextureSize;
            public LoadedTexture RotOverlay;
            public ICoreClientAPI RotOverlayApi;
            public int RotOverlayTextureId;
            public int SessionEpoch;
            public int RenderCallDepth;
            public bool ScratchInUse;
            public int ScratchOwnerDepth;

            public ThreadScratch(int epoch)
            {
                Info = new ItemRenderInfo();
                TextureSize = Info.TextureSize ?? new Size2i();
                Info.TextureSize = TextureSize;
                SessionEpoch = epoch;
            }
        }

        [ThreadStatic]
        private static ThreadScratch threadScratch;

        internal static bool TranspilerApplied => transpilerApplied;

        internal static void ResetTranspilerApplied()
        {
            transpilerApplied = false;
        }

        internal static void SetLogger(Action<string> log)
        {
            logger = log;
        }

        internal static void SetClientActive(bool active)
        {
            clientActive = active;
        }

        internal static void ResetSessionState()
        {
            Interlocked.Increment(ref sessionEpoch);
            ClearThreadLocalState();
        }

        internal static void Cleanup()
        {
            clientActive = false;
            transpilerApplied = false;
            logger = null;
            Interlocked.Increment(ref sessionEpoch);
            ClearThreadLocalState();
        }

        private static void ClearThreadLocalState()
        {
            ThreadScratch state = threadScratch;
            if (state != null)
            {
                ClearScratchReferences(state);
                state.RotOverlay = null;
                state.RotOverlayApi = null;
                state.RotOverlayTextureId = 0;
                state.ScratchInUse = false;
                state.ScratchOwnerDepth = 0;
                state.RenderCallDepth = 0;
                threadScratch = null;
            }
        }

        public static void RenderItemstackToGui_Prefix()
        {
            ThreadScratch state = threadScratch;
            if (state == null)
            {
                state = new ThreadScratch(Volatile.Read(ref sessionEpoch));
                threadScratch = state;
            }

            state.RenderCallDepth++;
        }

        public static void RenderItemstackToGui_Finalizer()
        {
            ThreadScratch state = threadScratch;
            if (state == null)
            {
                return;
            }

            if (state.ScratchInUse && state.ScratchOwnerDepth == state.RenderCallDepth)
            {
                ClearScratchReferences(state);
                state.ScratchInUse = false;
                state.ScratchOwnerDepth = 0;
            }

            if (state.RenderCallDepth > 0)
            {
                state.RenderCallDepth--;
            }
        }

        public static IEnumerable<CodeInstruction> TranspileRenderItemstackToGui(IEnumerable<CodeInstruction> instructions)
        {
            transpilerApplied = false;

            MethodInfo originalMethod = AccessTools.Method(
                typeof(InventoryItemRenderer),
                nameof(InventoryItemRenderer.GetItemStackRenderInfo),
                new[] { typeof(ClientMain), typeof(ItemSlot), typeof(EnumItemRenderTarget), typeof(float) });

            MethodInfo replacementMethod = AccessTools.Method(
                typeof(GuiItemRenderInfoOptimization),
                nameof(GetItemStackRenderInfoForGui),
                new[] { typeof(ClientMain), typeof(ItemSlot), typeof(EnumItemRenderTarget), typeof(float) });

            var codes = new List<CodeInstruction>(instructions);
            if (originalMethod == null || replacementMethod == null)
            {
                logger?.Invoke("[OptiTime] GUI ItemRenderInfo scratch skipped: method targets not found.");
                return codes;
            }

            int matchIndex = -1;
            int matchCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(originalMethod))
                {
                    matchIndex = i;
                    matchCount++;
                }
            }

            if (matchCount != 1)
            {
                logger?.Invoke($"[OptiTime] GUI ItemRenderInfo scratch skipped: expected 1 GetItemStackRenderInfo call, found {matchCount}.");
                return codes;
            }

            var call = new CodeInstruction(OpCodes.Call, replacementMethod);
            call.labels.AddRange(codes[matchIndex].labels);
            call.blocks.AddRange(codes[matchIndex].blocks);
            codes[matchIndex] = call;
            transpilerApplied = true;
            return codes;
        }

        public static ItemRenderInfo GetItemStackRenderInfoForGui(
            ClientMain game,
            ItemSlot inSlot,
            EnumItemRenderTarget target,
            float dt)
        {
            ThreadScratch state = threadScratch;
            if (!clientActive ||
                game == null ||
                game.Side != EnumAppSide.Client ||
                inSlot == null ||
                state == null ||
                state.RenderCallDepth <= 0 ||
                state.ScratchInUse)
            {
                return InventoryItemRenderer.GetItemStackRenderInfo(game, inSlot, target, dt);
            }

            int epoch = Volatile.Read(ref sessionEpoch);
            if (state.SessionEpoch != epoch)
            {
                state.RotOverlay = null;
                state.RotOverlayApi = null;
                state.RotOverlayTextureId = 0;
                state.SessionEpoch = epoch;
            }

            ItemRenderInfo currentScratch = state.Info;
            ResetToDefaults(currentScratch, state.TextureSize);
            state.ScratchInUse = true;
            state.ScratchOwnerDepth = state.RenderCallDepth;

            try
            {
                ItemStack itemstack = inSlot.Itemstack;
                CollectibleObject collectible = itemstack?.Collectible;
                if (itemstack == null || collectible == null || collectible.Code == null)
                {
                    return currentScratch;
                }

                currentScratch.dt = dt;

                switch (target)
                {
                    case EnumItemRenderTarget.Ground:
                        currentScratch.Transform = collectible.GroundTransform;
                        break;
                    case EnumItemRenderTarget.Gui:
                        currentScratch.Transform = collectible.GuiTransform;
                        break;
                    case EnumItemRenderTarget.HandTp:
                        currentScratch.Transform = collectible.TpHandTransform;
                        break;
                    case EnumItemRenderTarget.HandTpOff:
                        currentScratch.Transform = collectible.TpOffHandTransform ?? collectible.TpHandTransform;
                        break;
                }

                if (itemstack.Class == EnumItemClass.Block)
                {
                    currentScratch.ModelRef = game.TesselatorManager.blockModelRefsInventory[itemstack.Id];
                }
                else
                {
                    int renderVariant = itemstack.TempAttributes.HasAttribute(RenderVariantAttribute)
                        ? itemstack.TempAttributes.GetInt(RenderVariantAttribute)
                        : itemstack.Attributes.GetInt(RenderVariantAttribute);

                    if (renderVariant != 0)
                    {
                        MultiTextureMeshRef[] altModels = game.TesselatorManager.altItemModelRefsInventory[itemstack.Id];
                        if (renderVariant < 0 || altModels == null || altModels.Length < renderVariant)
                        {
                            game.Logger?.Warning(
                                "Itemstack {0} has an invalid renderVariant {1}. No such model variant exists. Will reset to 0",
                                itemstack.GetName(),
                                renderVariant);
                            itemstack.TempAttributes.SetInt(RenderVariantAttribute, 0);
                            renderVariant = 0;
                        }
                    }

                    currentScratch.ModelRef = renderVariant == 0
                        ? game.TesselatorManager.itemModelRefsInventory[itemstack.Id]
                        : game.TesselatorManager.altItemModelRefsInventory[itemstack.Id][renderVariant - 1];
                }

                CompositeShape shape = itemstack.Class == EnumItemClass.Block
                    ? itemstack.Block.Shape
                    : itemstack.Item.Shape;
                currentScratch.NormalShaded = shape != null && !shape.VoxelizeTexture;

                Size2i atlasSize = itemstack.Class == EnumItemClass.Block
                    ? game.BlockAtlasManager.Size
                    : game.ItemAtlasManager.Size;
                currentScratch.TextureSize.Width = atlasSize.Width;
                currentScratch.TextureSize.Height = atlasSize.Height;

                Block block = itemstack.Block;
                currentScratch.HalfTransparent = block != null &&
                    (block.RenderPass == EnumChunkRenderPass.Meta || block.RenderPass == EnumChunkRenderPass.Transparent);
                currentScratch.AlphaTest = collectible.RenderAlphaTest;
                currentScratch.CullFaces = block != null &&
                    (block.RenderPass == EnumChunkRenderPass.Opaque || block.RenderPass == EnumChunkRenderPass.TopSoil);
                currentScratch.ApplyColor = currentScratch.NormalShaded;

                TransitionState transitionState = collectible.UpdateAndGetTransitionState(game, inSlot, EnumTransitionType.Perish);
                if (transitionState != null && transitionState.TransitionLevel > 0f)
                {
                    ApplyRotOverlay(state, game.api, currentScratch, transitionState.TransitionLevel);
                }

                currentScratch.InSlot = inSlot;
                inSlot.OnBeforeRender(currentScratch);

                ItemRenderInfo renderinfo = currentScratch;
                collectible.OnBeforeRender(game.api, itemstack, target, ref renderinfo);

                if (!ReferenceEquals(renderinfo, currentScratch))
                {
                    ClearScratchReferences(state);
                    state.ScratchInUse = false;
                    state.ScratchOwnerDepth = 0;
                }

                return renderinfo;
            }
            catch
            {
                ClearScratchReferences(state);
                state.ScratchInUse = false;
                state.ScratchOwnerDepth = 0;
                throw;
            }
        }

        private static void ResetToDefaults(ItemRenderInfo info, Size2i textureSize)
        {
            info.ModelRef = null;
            info.Transform = null;
            info.CullFaces = false;
            info.TextureId = 0;
            textureSize.Width = 0;
            textureSize.Height = 0;
            info.TextureSize = textureSize;
            info.AlphaTest = 0f;
            info.HalfTransparent = false;
            info.NormalShaded = false;
            info.ApplyColor = false;
            info.OverlayTexture = null;
            info.OverlayOpacity = 0f;
            info.DamageEffect = 0f;
            info.InSlot = null;
            info.dt = 0f;
        }

        private static void ClearScratchReferences(ThreadScratch state)
        {
            ItemRenderInfo info = state.Info;
            info.ModelRef = null;
            info.Transform = null;
            info.OverlayTexture = null;
            info.InSlot = null;
            info.TextureSize = state.TextureSize;
        }

        private static void ApplyRotOverlay(ThreadScratch state, ICoreClientAPI capi, ItemRenderInfo info, float opacity)
        {
            if (state.RotOverlay == null ||
                !ReferenceEquals(state.RotOverlayApi, capi) ||
                state.RotOverlay.Disposed ||
                state.RotOverlay.TextureId != state.RotOverlayTextureId)
            {
                state.RotOverlay = new LoadedTexture(capi)
                {
                    IgnoreUndisposed = true
                };
                state.RotOverlayApi = capi;
                state.RotOverlayTextureId = 0;
            }

            LoadedTexture texture = state.RotOverlay;
            capi.Render.GetOrLoadTexture(rotOverlayLocation, ref texture);
            state.RotOverlay = texture;
            state.RotOverlayTextureId = texture?.TextureId ?? 0;
            info.OverlayTexture = texture;
            info.OverlayOpacity = opacity;
        }

        internal static ItemRenderInfo GetThreadScratchForTesting()
        {
            return threadScratch?.Info;
        }

        internal static LoadedTexture GetThreadRotOverlayForTesting()
        {
            return threadScratch?.RotOverlay;
        }

        internal static bool IsScratchInUseForTesting()
        {
            return threadScratch?.ScratchInUse ?? false;
        }
    }
}
