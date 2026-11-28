using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Xunit;

namespace OptiTime.Tests
{
    public sealed class GuiItemRenderInfoOptimizationTests : IDisposable
    {
        private readonly Harmony harmony;

        public GuiItemRenderInfoOptimizationTests()
        {
            GuiItemRenderInfoOptimization.Cleanup();
            harmony = new Harmony("com.zaldaryon.optitime.tests.guiitemrenderinfo." + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id);
            GuiItemRenderInfoOptimization.Cleanup();
        }

        private sealed class FakeRenderApi : RenderAPIGame
        {
            public FakeRenderApi() : base(null, null)
            {
            }

            public int GetOrLoadTextureCalls;
            public int NextTextureId = 42;
            public int NextWidth = 64;
            public int NextHeight = 64;
            public AssetLocation LastRequestedLocation;

            public override void GetOrLoadTexture(AssetLocation name, ref LoadedTexture intoTexture)
            {
                GetOrLoadTextureCalls++;
                LastRequestedLocation = name;
                intoTexture.IgnoreUndisposed = true;
                intoTexture.TextureId = NextTextureId;
                intoTexture.Width = NextWidth;
                intoTexture.Height = NextHeight;
            }
        }

        private sealed class FakeSlot : DummySlot
        {
            public Action<ItemRenderInfo> BeforeRenderHook { get; set; }

            public FakeSlot(ItemStack stack) : base(stack)
            {
            }

            public override void OnBeforeRender(ItemRenderInfo renderInfo)
            {
                BeforeRenderHook?.Invoke(renderInfo);
            }
        }

        private sealed class ProbeBehavior : CollectibleBehavior
        {
            private readonly Action<ItemRenderInfo> hook;

            public ProbeBehavior(CollectibleObject collObj, Action<ItemRenderInfo> hook) : base(collObj)
            {
                this.hook = hook;
            }

            public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
            {
                hook?.Invoke(renderinfo);
            }
        }

        private delegate void CollectibleBeforeRenderHook(
            ICoreClientAPI capi,
            ItemStack itemstack,
            EnumItemRenderTarget target,
            ref ItemRenderInfo renderinfo);

        private sealed class ProbeItem : Item
        {
            public TransitionState PerishState;
            public CollectibleBeforeRenderHook BeforeRenderHook;
            public Action<IWorldAccessor, ItemStack> InGuiIdleHook;
            public int InGuiIdleCalls { get; private set; }

            public override string GetHeldItemName(ItemStack itemStack)
            {
                return Code?.ToString() ?? "probe";
            }

            public override TransitionState UpdateAndGetTransitionState(IWorldAccessor world, ItemSlot inslot, EnumTransitionType type)
            {
                return type == EnumTransitionType.Perish ? PerishState : null;
            }

            public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
            {
                BeforeRenderHook?.Invoke(capi, itemstack, target, ref renderinfo);
                base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
            }

            public override void InGuiIdle(IWorldAccessor world, ItemStack stack)
            {
                InGuiIdleCalls++;
                InGuiIdleHook?.Invoke(world, stack);
            }
        }

        private sealed class ProbeBlock : Block
        {
            public CollectibleBeforeRenderHook BeforeRenderHook;

            public override string GetHeldItemName(ItemStack itemStack)
            {
                return Code?.ToString() ?? "probe";
            }

            public override TransitionState UpdateAndGetTransitionState(IWorldAccessor world, ItemSlot inslot, EnumTransitionType type)
            {
                return null;
            }

            public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
            {
                BeforeRenderHook?.Invoke(capi, itemstack, target, ref renderinfo);
                base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
            }
        }

        private static MethodInfo GetRenderItemstackToGuiTarget()
        {
            return AccessTools.Method(
                typeof(InventoryItemRenderer),
                nameof(InventoryItemRenderer.RenderItemstackToGui),
                new[]
                {
                    typeof(ItemSlot), typeof(double), typeof(double), typeof(double),
                    typeof(float), typeof(int), typeof(float),
                    typeof(bool), typeof(bool), typeof(bool)
                });
        }

        private void PatchRenderItemstackToGui()
        {
            MethodInfo target = GetRenderItemstackToGuiTarget();
            Assert.NotNull(target);
            GuiItemRenderInfoOptimization.ResetTranspilerApplied();
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(GuiItemRenderInfoOptimization), nameof(GuiItemRenderInfoOptimization.RenderItemstackToGui_Prefix)),
                transpiler: new HarmonyMethod(typeof(GuiItemRenderInfoOptimization), nameof(GuiItemRenderInfoOptimization.TranspileRenderItemstackToGui)),
                finalizer: new HarmonyMethod(typeof(GuiItemRenderInfoOptimization), nameof(GuiItemRenderInfoOptimization.RenderItemstackToGui_Finalizer)));
            Assert.True(GuiItemRenderInfoOptimization.TranspilerApplied);
            GuiItemRenderInfoOptimization.SetClientActive(true);
        }

        private static (ClientMain Game, InventoryItemRenderer Renderer, FakeRenderApi RenderApi, MultiTextureMeshRef ItemMesh, MultiTextureMeshRef BlockMesh) CreateHarness()
        {
            var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
            AccessTools.Field(typeof(ClientPlatformWindows), "logger").SetValue(platform, new NullLogger());

            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            game.Platform = platform;
            var tesselator = (ShapeTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ShapeTesselatorManager));
            var itemMesh = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            var blockMesh = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));

            tesselator.blockModelRefsInventory = new MultiTextureMeshRef[8];
            tesselator.blockModelRefsInventory[2] = blockMesh;
            tesselator.itemModelRefsInventory = new MultiTextureMeshRef[8];
            tesselator.itemModelRefsInventory[1] = itemMesh;
            tesselator.altItemModelRefsInventory = new MultiTextureMeshRef[8][];
            game.TesselatorManager = tesselator;

            var blockAtlas = (BlockTextureAtlasManager)RuntimeHelpers.GetUninitializedObject(typeof(BlockTextureAtlasManager));
            blockAtlas.Size = new Size2i(1024, 512);
            game.BlockAtlasManager = blockAtlas;

            var itemAtlas = (ItemTextureAtlasManager)RuntimeHelpers.GetUninitializedObject(typeof(ItemTextureAtlasManager));
            itemAtlas.Size = new Size2i(512, 256);
            game.ItemAtlasManager = itemAtlas;

            var fakeRenderApi = (FakeRenderApi)RuntimeHelpers.GetUninitializedObject(typeof(FakeRenderApi));
            fakeRenderApi.NextTextureId = 42;
            fakeRenderApi.NextWidth = 64;
            fakeRenderApi.NextHeight = 64;

            var api = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
            AccessTools.Field(typeof(ClientCoreAPI), "renderapi").SetValue(api, fakeRenderApi);
            game.api = api;

            var renderer = (InventoryItemRenderer)RuntimeHelpers.GetUninitializedObject(typeof(InventoryItemRenderer));
            AccessTools.Field(typeof(InventoryItemRenderer), "game").SetValue(renderer, game);

            return (game, renderer, fakeRenderApi, itemMesh, blockMesh);
        }

        private static ProbeItem CreateItem(int id = 1, bool voxelized = false, float alphaTest = 0.05f)
        {
            return new ProbeItem
            {
                ItemId = id,
                Code = new AssetLocation("game:probeitem-" + id),
                GuiTransform = null,
                RenderAlphaTest = alphaTest,
                Shape = new CompositeShape { VoxelizeTexture = voxelized },
                CollectibleBehaviors = Array.Empty<CollectibleBehavior>()
            };
        }

        [Fact]
        public void RenderItemstackToGui_ReusesScratchWithZeroPerSlotAllocation()
        {
            PatchRenderItemstackToGui();
            var (_, renderer, _, _, _) = CreateHarness();
            var item = CreateItem();
            var slot = new FakeSlot(new ItemStack(item));

            for (int i = 0; i < 8; i++)
            {
                renderer.RenderItemstackToGui(slot, 10, 20, 50, 32f, -1, 0.016f);
            }

            const int iterations = 500;
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                renderer.RenderItemstackToGui(slot, 10, 20, 50, 32f, -1, 0.016f);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            Assert.Equal(8 + iterations, item.InGuiIdleCalls);
            Assert.True(
                allocated < 512,
                $"Expected near-zero allocation across {iterations} patched RenderItemstackToGui calls (GetItemStackRenderInfo path), got {allocated} B ({allocated / (double)iterations:F2} B/call).");

            ItemRenderInfo scratch = GuiItemRenderInfoOptimization.GetThreadScratchForTesting();
            Assert.NotNull(scratch);
            Assert.Null(scratch.InSlot);
            Assert.Null(scratch.ModelRef);
            Assert.Null(scratch.Transform);
            Assert.Null(scratch.OverlayTexture);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
        }

        [Fact]
        public void PublicGetItemStackRenderInfo_StillReturnsFreshInstancesForExternalCallers()
        {
            PatchRenderItemstackToGui();
            var (game, renderer, _, _, _) = CreateHarness();
            var item = CreateItem();
            var slot = new FakeSlot(new ItemStack(item));

            renderer.RenderItemstackToGui(slot, 10, 20, 50, 32f, -1, 0.016f);
            ItemRenderInfo scratch = GuiItemRenderInfoOptimization.GetThreadScratchForTesting();

            ItemRenderInfo first = InventoryItemRenderer.GetItemStackRenderInfo(game, slot, EnumItemRenderTarget.Gui, 0.016f);
            ItemRenderInfo second = InventoryItemRenderer.GetItemStackRenderInfo(game, slot, EnumItemRenderTarget.Gui, 0.016f);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.NotSame(first, scratch);
            Assert.NotSame(second, scratch);
            Assert.NotSame(first.TextureSize, second.TextureSize);
            Assert.Same(slot, first.InSlot);
            Assert.Same(slot, second.InSlot);
        }

        [Fact]
        public void ScratchFields_ResetToVanillaDefaultsBetweenHeterogeneousSlots()
        {
            PatchRenderItemstackToGui();
            var (_, renderer, _, itemMesh, _) = CreateHarness();

            var customTexture = new LoadedTexture(null, 999, 32, 32) { IgnoreUndisposed = true };
            var customSize = new Size2i(777, 888);
            var dirtyItem = CreateItem(id: 1, voxelized: false, alphaTest: 0.42f);
            dirtyItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                info.CullFaces = true;
                info.TextureId = 12345;
                info.TextureSize = customSize;
                info.AlphaTest = 0.91f;
                info.HalfTransparent = true;
                info.NormalShaded = true;
                info.ApplyColor = true;
                info.OverlayTexture = customTexture;
                info.OverlayOpacity = 0.85f;
                info.DamageEffect = 0.63f;
                info.dt = 9.5f;
            };

            ItemRenderInfo observedSnapshot = null;
            Size2i observedTextureSize = null;
            bool observedSameScratch = false;
            var cleanItem = CreateItem(id: 1, voxelized: true, alphaTest: 0.0f);
            cleanItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                observedSameScratch = ReferenceEquals(info, GuiItemRenderInfoOptimization.GetThreadScratchForTesting());
                observedTextureSize = info.TextureSize;
                observedSnapshot = new ItemRenderInfo
                {
                    ModelRef = info.ModelRef,
                    Transform = info.Transform,
                    CullFaces = info.CullFaces,
                    TextureId = info.TextureId,
                    TextureSize = new Size2i(info.TextureSize.Width, info.TextureSize.Height),
                    AlphaTest = info.AlphaTest,
                    HalfTransparent = info.HalfTransparent,
                    NormalShaded = info.NormalShaded,
                    ApplyColor = info.ApplyColor,
                    OverlayTexture = info.OverlayTexture,
                    OverlayOpacity = info.OverlayOpacity,
                    DamageEffect = info.DamageEffect,
                    InSlot = info.InSlot,
                    dt = info.dt
                };
            };

            var dirtySlot = new FakeSlot(new ItemStack(dirtyItem));
            var cleanSlot = new FakeSlot(new ItemStack(cleanItem));

            renderer.RenderItemstackToGui(dirtySlot, 0, 0, 0, 32f, -1, 0.25f);
            renderer.RenderItemstackToGui(cleanSlot, 0, 0, 0, 32f, -1, 0.05f);

            Assert.True(observedSameScratch);
            Assert.NotNull(observedSnapshot);
            Assert.Same(itemMesh, observedSnapshot.ModelRef);
            Assert.Null(observedSnapshot.Transform);
            Assert.False(observedSnapshot.CullFaces);
            Assert.Equal(0, observedSnapshot.TextureId);
            Assert.NotSame(customSize, observedTextureSize);
            Assert.Equal(777, customSize.Width);
            Assert.Equal(888, customSize.Height);
            Assert.Equal(512, observedSnapshot.TextureSize.Width);
            Assert.Equal(256, observedSnapshot.TextureSize.Height);
            Assert.Equal(0.0f, observedSnapshot.AlphaTest);
            Assert.False(observedSnapshot.HalfTransparent);
            Assert.False(observedSnapshot.NormalShaded);
            Assert.False(observedSnapshot.ApplyColor);
            Assert.Null(observedSnapshot.OverlayTexture);
            Assert.Equal(0.0f, observedSnapshot.OverlayOpacity);
            Assert.Equal(0.0f, observedSnapshot.DamageEffect);
            Assert.Same(cleanSlot, observedSnapshot.InSlot);
            Assert.Equal(0.05f, observedSnapshot.dt);

            ItemRenderInfo scratch = GuiItemRenderInfoOptimization.GetThreadScratchForTesting();
            Assert.Null(scratch.InSlot);
            Assert.Null(scratch.OverlayTexture);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
        }

        [Fact]
        public void OnBeforeRender_OrderingAndRefReplacementWithReentrancy_MatchesVanilla()
        {
            PatchRenderItemstackToGui();
            var (_, renderer, _, _, _) = CreateHarness();

            var order = new List<string>();
            var item = CreateItem(id: 1);
            item.CollectibleBehaviors = new CollectibleBehavior[]
            {
                new ProbeBehavior(item, _ => order.Add("behavior"))
            };
            item.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo _) =>
            {
                order.Add("collectible");
            };

            var slot = new FakeSlot(new ItemStack(item))
            {
                BeforeRenderHook = _ => order.Add("slot")
            };

            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Equal(new[] { "slot", "collectible", "behavior" }, order);

            // Replace ref renderinfo with null ModelRef -> InGuiIdle must NOT run.
            var replacement = new ItemRenderInfo { ModelRef = null, Transform = null };
            var replacingItem = CreateItem(id: 1);
            replacingItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                info = replacement;
            };
            renderer.RenderItemstackToGui(new FakeSlot(new ItemStack(replacingItem)), 0, 0, 0, 32f, -1, 0.016f);
            Assert.Equal(0, replacingItem.InGuiIdleCalls);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());

            // Replace ref renderinfo with a custom non-null ModelRef and trigger reentrant RenderItemstackToGui inside InGuiIdle.
            var secondMesh = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            var replacementWithMesh = new ItemRenderInfo { ModelRef = secondMesh, Transform = null };
            var nestedItem = CreateItem(id: 1);
            var nestedSlot = new FakeSlot(new ItemStack(nestedItem));
            bool nestedReusedScratch = false;
            nestedItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                nestedReusedScratch = ReferenceEquals(info, GuiItemRenderInfoOptimization.GetThreadScratchForTesting());
            };

            var promotingItem = CreateItem(id: 3);
            promotingItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                info = replacementWithMesh;
            };
            promotingItem.InGuiIdleHook = (_, _) =>
            {
                renderer.RenderItemstackToGui(nestedSlot, 0, 0, 0, 32f, -1, 0.016f);
            };

            renderer.RenderItemstackToGui(new FakeSlot(new ItemStack(promotingItem)), 0, 0, 0, 32f, -1, 0.016f);
            Assert.Equal(1, promotingItem.InGuiIdleCalls);
            Assert.Equal(1, nestedItem.InGuiIdleCalls);
            Assert.True(nestedReusedScratch);
            Assert.Same(secondMesh, replacementWithMesh.ModelRef);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting().InSlot);
        }

        [Fact]
        public void ExceptionPaths_ClearScratchAndReleaseInUseFlag()
        {
            PatchRenderItemstackToGui();
            var (_, renderer, _, _, _) = CreateHarness();

            var throwingBeforeRender = CreateItem(id: 1);
            throwingBeforeRender.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo _) =>
            {
                throw new InvalidOperationException("boom in OnBeforeRender");
            };
            var slot1 = new FakeSlot(new ItemStack(throwingBeforeRender));

            var ex1 = Assert.Throws<Exception>(() => renderer.RenderItemstackToGui(slot1, 0, 0, 0, 32f, -1, 0.016f));
            Assert.IsType<InvalidOperationException>(ex1.InnerException);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting().InSlot);

            var throwingInGuiIdle = CreateItem(id: 1);
            throwingInGuiIdle.InGuiIdleHook = (_, _) => throw new InvalidOperationException("boom in InGuiIdle");
            var slot2 = new FakeSlot(new ItemStack(throwingInGuiIdle));

            var ex2 = Assert.Throws<Exception>(() => renderer.RenderItemstackToGui(slot2, 0, 0, 0, 32f, -1, 0.016f));
            Assert.IsType<InvalidOperationException>(ex2.InnerException);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting().InSlot);

            // Null slot throws before GetItemStackRenderInfoForGui runs; depth and scratch stay balanced.
            Assert.Throws<Exception>(() => renderer.RenderItemstackToGui((ItemSlot)null, 0, 0, 0, 32f, -1, 0.016f));
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());

            // Subsequent healthy call reuses the scratch cleanly.
            var healthyItem = CreateItem(id: 1);
            var healthySlot = new FakeSlot(new ItemStack(healthyItem));
            renderer.RenderItemstackToGui(healthySlot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Equal(1, healthyItem.InGuiIdleCalls);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting().InSlot);
        }

        [Fact]
        public void ReentrantRenderItemstackToGui_DoesNotCorruptOuterScratch()
        {
            PatchRenderItemstackToGui();
            var (_, renderer, _, _, _) = CreateHarness();

            var innerItem = CreateItem(id: 1);
            ItemRenderInfo innerObserved = null;
            innerItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                innerObserved = info;
            };
            var innerSlot = new FakeSlot(new ItemStack(innerItem));

            var outerItem = CreateItem(id: 1);
            ItemRenderInfo outerObserved = null;
            ItemSlot outerInSlotAfterInner = null;
            outerItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                outerObserved = info;
                info.DamageEffect = 0.77f;
                renderer.RenderItemstackToGui(innerSlot, 0, 0, 0, 32f, -1, 0.016f);
                outerInSlotAfterInner = info.InSlot;
                Assert.Equal(0.77f, info.DamageEffect);
            };
            var outerSlot = new FakeSlot(new ItemStack(outerItem));

            renderer.RenderItemstackToGui(outerSlot, 0, 0, 0, 32f, -1, 0.016f);

            Assert.NotNull(outerObserved);
            Assert.NotNull(innerObserved);
            Assert.NotSame(outerObserved, innerObserved);
            Assert.Same(outerSlot, outerInSlotAfterInner);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting().InSlot);
        }

        [Fact]
        public void RotOverlayTexture_ReusesSafelyAndRecoversFromDisposalDriftAndSessionReset()
        {
            PatchRenderItemstackToGui();
            var (game, renderer, fakeRenderApi, _, _) = CreateHarness();

            LoadedTexture rot1 = null;
            LoadedTexture rot2 = null;
            LoadedTexture freshOverlay = null;
            LoadedTexture zeroLevelOverlay = null;
            float rot2Opacity = 0f;

            var rottingItem1 = CreateItem(id: 1);
            rottingItem1.PerishState = new TransitionState { TransitionLevel = 0.35f };
            rottingItem1.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                rot1 = info.OverlayTexture;
            };

            var rottingItem2 = CreateItem(id: 1);
            rottingItem2.PerishState = new TransitionState { TransitionLevel = 0.75f };
            rottingItem2.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                rot2 = info.OverlayTexture;
                rot2Opacity = info.OverlayOpacity;
            };

            var zeroRotItem = CreateItem(id: 1);
            zeroRotItem.PerishState = new TransitionState { TransitionLevel = 0f };
            zeroRotItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                zeroLevelOverlay = info.OverlayTexture;
            };

            var freshItem = CreateItem(id: 1);
            freshItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                freshOverlay = info.OverlayTexture;
            };

            var modOverlay = new LoadedTexture(null, 777, 16, 16) { IgnoreUndisposed = true };
            var modItem = CreateItem(id: 1);
            modItem.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                info.OverlayTexture = modOverlay;
                info.OverlayOpacity = 0.5f;
            };

            var rotSlot1 = new FakeSlot(new ItemStack(rottingItem1));
            var rotSlot2 = new FakeSlot(new ItemStack(rottingItem2));
            var zeroRotSlot = new FakeSlot(new ItemStack(zeroRotItem));
            var freshSlot = new FakeSlot(new ItemStack(freshItem));
            var modSlot = new FakeSlot(new ItemStack(modItem));

            renderer.RenderItemstackToGui(rotSlot1, 0, 0, 0, 32f, -1, 0.016f);
            renderer.RenderItemstackToGui(modSlot, 0, 0, 0, 32f, -1, 0.016f);
            renderer.RenderItemstackToGui(freshSlot, 0, 0, 0, 32f, -1, 0.016f);
            renderer.RenderItemstackToGui(zeroRotSlot, 0, 0, 0, 32f, -1, 0.016f);
            renderer.RenderItemstackToGui(rotSlot2, 0, 0, 0, 32f, -1, 0.016f);

            Assert.NotNull(rot1);
            Assert.Same(rot1, rot2);
            Assert.Equal(42, rot2.TextureId);
            Assert.Equal(0.75f, rot2Opacity);
            Assert.Null(freshOverlay);
            Assert.Null(zeroLevelOverlay);
            Assert.False(modOverlay.Disposed);
            Assert.Equal(777, modOverlay.TextureId);
            Assert.Equal(2, fakeRenderApi.GetOrLoadTextureCalls);

            // External TextureId drift detaches the mutated wrapper and allocates a clean LoadedTexture.
            rot2.TextureId = 555;
            LoadedTexture rotAfterDrift = null;
            rottingItem2.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                rotAfterDrift = info.OverlayTexture;
            };
            renderer.RenderItemstackToGui(rotSlot2, 0, 0, 0, 32f, -1, 0.016f);
            Assert.NotNull(rotAfterDrift);
            Assert.NotSame(rot2, rotAfterDrift);
            Assert.Equal(555, rot2.TextureId);
            Assert.Equal(42, rotAfterDrift.TextureId);

            // External Disposed state also triggers recreation.
            AccessTools.Field(typeof(LoadedTexture), "disposed").SetValue(rotAfterDrift, true);
            LoadedTexture rotAfterDispose = null;
            rottingItem2.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                rotAfterDispose = info.OverlayTexture;
            };
            renderer.RenderItemstackToGui(rotSlot2, 0, 0, 0, 32f, -1, 0.016f);
            Assert.NotNull(rotAfterDispose);
            Assert.NotSame(rotAfterDrift, rotAfterDispose);

            // Switching ClientCoreAPI (new session) recreates the rot overlay wrapper.
            var newApi = (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
            AccessTools.Field(typeof(ClientCoreAPI), "renderapi").SetValue(newApi, fakeRenderApi);
            game.api = newApi;
            LoadedTexture rotAfterApiSwitch = null;
            rottingItem2.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                rotAfterApiSwitch = info.OverlayTexture;
            };
            renderer.RenderItemstackToGui(rotSlot2, 0, 0, 0, 32f, -1, 0.016f);
            Assert.NotNull(rotAfterApiSwitch);
            Assert.NotSame(rotAfterDispose, rotAfterApiSwitch);

            // Steady-state rotting slot renders allocate zero bytes for OverlayTexture / AssetLocation.
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                renderer.RenderItemstackToGui(rotSlot2, 0, 0, 0, 32f, -1, 0.016f);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            Assert.True(allocated < 512, $"Expected near-zero allocation across 200 rotting slot renders, got {allocated} B.");

            // Session reset drops cached LoadedTexture / ICoreClientAPI references.
            GuiItemRenderInfoOptimization.ResetSessionState();
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadRotOverlayForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting());
        }

        [Fact]
        public void RenderVariant_HandlesPrecedenceValidAltsAndInvalidBoundsSafely()
        {
            PatchRenderItemstackToGui();
            var (game, renderer, _, defaultMesh, _) = CreateHarness();

            var alt1 = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            var alt2 = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            game.TesselatorManager.altItemModelRefsInventory[1] = new[] { alt1, alt2 };

            MultiTextureMeshRef observed = null;
            var item = CreateItem(id: 1);
            item.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                observed = info.ModelRef;
            };

            // Attributes selects alt 1; TempAttributes overrides to alt 2.
            var stack = new ItemStack(item);
            stack.Attributes.SetInt("renderVariant", 1);
            var slot = new FakeSlot(stack);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Same(alt1, observed);

            stack.TempAttributes.SetInt("renderVariant", 2);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Same(alt2, observed);

            // Boundary out-of-range (Length + 1 == 3) resets TempAttributes to 0 instead of throwing.
            stack.TempAttributes.SetInt("renderVariant", 3);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Same(defaultMesh, observed);
            Assert.Equal(0, stack.TempAttributes.GetInt("renderVariant"));

            // Negative variant also resets to 0.
            stack.TempAttributes.SetInt("renderVariant", -1);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Same(defaultMesh, observed);
            Assert.Equal(0, stack.TempAttributes.GetInt("renderVariant"));

            // Null alt array resets to 0 safely.
            game.TesselatorManager.altItemModelRefsInventory[1] = null;
            stack.TempAttributes.SetInt("renderVariant", 1);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.Same(defaultMesh, observed);
            Assert.Equal(0, stack.TempAttributes.GetInt("renderVariant"));
        }

        [Theory]
        [InlineData(EnumChunkRenderPass.Opaque)]
        [InlineData(EnumChunkRenderPass.TopSoil)]
        [InlineData(EnumChunkRenderPass.Meta)]
        [InlineData(EnumChunkRenderPass.Transparent)]
        public void BlockRenderPassAndAtlasSize_MatchVanillaBehavior(EnumChunkRenderPass renderPass)
        {
            PatchRenderItemstackToGui();
            var (game, renderer, _, _, blockMesh) = CreateHarness();

            var block = new ProbeBlock
            {
                BlockId = 2,
                Code = new AssetLocation("game:probeblock-" + renderPass),
                RenderPass = renderPass,
                RenderAlphaTest = 0.25f,
                Shape = new CompositeShape { VoxelizeTexture = false },
                GuiTransform = null,
                CollectibleBehaviors = Array.Empty<CollectibleBehavior>()
            };
            ItemRenderInfo scratchObserved = null;
            block.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                scratchObserved = new ItemRenderInfo
                {
                    ModelRef = info.ModelRef,
                    CullFaces = info.CullFaces,
                    HalfTransparent = info.HalfTransparent,
                    NormalShaded = info.NormalShaded,
                    ApplyColor = info.ApplyColor,
                    AlphaTest = info.AlphaTest,
                    TextureSize = new Size2i(info.TextureSize.Width, info.TextureSize.Height)
                };
            };

            var slot = new FakeSlot(new ItemStack(block));
            ItemRenderInfo vanilla = InventoryItemRenderer.GetItemStackRenderInfo(game, slot, EnumItemRenderTarget.Gui, 0.016f);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);

            Assert.NotNull(scratchObserved);
            Assert.Same(blockMesh, scratchObserved.ModelRef);
            Assert.Equal(vanilla.ModelRef, scratchObserved.ModelRef);
            Assert.Equal(vanilla.CullFaces, scratchObserved.CullFaces);
            Assert.Equal(vanilla.HalfTransparent, scratchObserved.HalfTransparent);
            Assert.Equal(vanilla.NormalShaded, scratchObserved.NormalShaded);
            Assert.Equal(vanilla.ApplyColor, scratchObserved.ApplyColor);
            Assert.Equal(vanilla.AlphaTest, scratchObserved.AlphaTest);
            Assert.Equal(vanilla.TextureSize.Width, scratchObserved.TextureSize.Width);
            Assert.Equal(vanilla.TextureSize.Height, scratchObserved.TextureSize.Height);
        }

        [Fact]
        public void EmptyAndMissingCodeSlots_ReturnCleanlyWithZeroAllocations()
        {
            PatchRenderItemstackToGui();
            var (_, renderer, _, _, _) = CreateHarness();
            var emptySlot = new FakeSlot(null);

            var missingCodeItem = CreateItem(id: 1);
            missingCodeItem.Code = null;
            var missingCodeSlot = new FakeSlot(new ItemStack(missingCodeItem));

            renderer.RenderItemstackToGui(emptySlot, 0, 0, 0, 32f, -1, 0.016f);
            renderer.RenderItemstackToGui(missingCodeSlot, 0, 0, 0, 32f, -1, 0.016f);

            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                renderer.RenderItemstackToGui(emptySlot, 0, 0, 0, 32f, -1, 0.016f);
                renderer.RenderItemstackToGui(missingCodeSlot, 0, 0, 0, 32f, -1, 0.016f);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            Assert.True(allocated < 512, $"Expected near-zero allocation for empty/missing-code slots, got {allocated} B.");
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());
            Assert.Null(GuiItemRenderInfoOptimization.GetThreadScratchForTesting().InSlot);
        }

        [Fact]
        public void InactiveDirectCallAndTranspilerMismatch_FailSafeToVanilla()
        {
            PatchRenderItemstackToGui();
            var (game, renderer, _, _, _) = CreateHarness();
            var item = CreateItem(id: 1);
            var slot = new FakeSlot(new ItemStack(item));

            // Direct call outside RenderItemstackToGui (RenderCallDepth == 0) must fall back to vanilla and never lock ScratchInUse.
            ItemRenderInfo direct = GuiItemRenderInfoOptimization.GetItemStackRenderInfoForGui(game, slot, EnumItemRenderTarget.Gui, 0.016f);
            Assert.NotNull(direct);
            Assert.False(GuiItemRenderInfoOptimization.IsScratchInUseForTesting());

            // Cleanup disables the hook so RenderItemstackToGui falls back to fresh vanilla instances.
            GuiItemRenderInfoOptimization.Cleanup();
            ItemRenderInfo firstAfterCleanup = null;
            ItemRenderInfo secondAfterCleanup = null;
            int callIndex = 0;
            item.BeforeRenderHook = (ICoreClientAPI _, ItemStack _, EnumItemRenderTarget _, ref ItemRenderInfo info) =>
            {
                if (callIndex++ == 0)
                {
                    firstAfterCleanup = info;
                }
                else
                {
                    secondAfterCleanup = info;
                }
            };
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            renderer.RenderItemstackToGui(slot, 0, 0, 0, 32f, -1, 0.016f);
            Assert.NotNull(firstAfterCleanup);
            Assert.NotNull(secondAfterCleanup);
            Assert.NotSame(firstAfterCleanup, secondAfterCleanup);

            // Transpiler fails closed when call-site count != 1.
            var dummyInstructions = new[]
            {
                new CodeInstruction(OpCodes.Nop),
                new CodeInstruction(OpCodes.Ret)
            };
            var output = new List<CodeInstruction>(GuiItemRenderInfoOptimization.TranspileRenderItemstackToGui(dummyInstructions));
            Assert.False(GuiItemRenderInfoOptimization.TranspilerApplied);
            Assert.Equal(2, output.Count);
            Assert.Equal(OpCodes.Nop, output[0].opcode);
        }
    }
}
