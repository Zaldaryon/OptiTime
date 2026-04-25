using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace OptiTime
{
    /// <summary>
    /// Handbook optimization: Harmony prefix, reflection cache, and optimized rendering.
    /// Split into partial class files:
    ///   HandbookCache.cs    — state, caches, accessors, freeze, cleanup
    ///   HandbookIndexer.cs  — async indexing, stack/entity/storage relationship indexing
    ///   HandbookOptimization.cs — Harmony prefix, optimized rendering, reflection (this file)
    /// </summary>
    public partial class HandbookOptimization
    {
        private static void InitializeReflectionCache()
        {
            if (cachedBehaviorType != null) return; // Already initialized

            lock (reflectionInitLock)
            {
                if (cachedBehaviorType != null) return; // Double-check after lock

                cachedBehaviorType = AccessTools.TypeByName("Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo");
                if (cachedBehaviorType == null) return;

                cachedAddGeneralInfo = AccessTools.Method(cachedBehaviorType, "addGeneralInfo");
                cachedAddDropsInfo = AccessTools.Method(cachedBehaviorType, "addDropsInfo");
                cachedAddObtainedThroughInfo = AccessTools.Method(cachedBehaviorType, "addObtainedThroughInfo");
                cachedAddFoundInInfo = AccessTools.Method(cachedBehaviorType, "addFoundInInfo");
                cachedAddAlloyForInfo = AccessTools.Method(cachedBehaviorType, "addAlloyForInfo");
                cachedAddAlloyedFromInfo = AccessTools.Method(cachedBehaviorType, "addAlloyedFromInfo");
                cachedAddProcessesIntoInfo = AccessTools.Method(cachedBehaviorType, "addProcessesIntoInfo");
                cachedAddIngredientForInfo = AccessTools.Method(cachedBehaviorType, "addIngredientForInfo");
                cachedAddCreatedByInfo = AccessTools.Method(cachedBehaviorType, "addCreatedByInfo");
                cachedAddProcessorForInfo = AccessTools.Method(cachedBehaviorType, "addProcessorForInfo");
                cachedAddEatenByInfo = AccessTools.Method(cachedBehaviorType, "addEatenByInfo");
                cachedAddExtraSections = AccessTools.Method(cachedBehaviorType, "addExtraSections");
                cachedAddStorableInfo = AccessTools.Method(cachedBehaviorType, "addStorableInfo");
                cachedAddStoredInInfo = AccessTools.Method(cachedBehaviorType, "addStoredInInfo");
                cachedAddHeading = AccessTools.Method(cachedBehaviorType, "AddHeading");
                cachedCollObjField = AccessTools.Field(cachedBehaviorType, "collObj");
                cachedTinyPaddingField = AccessTools.Field(cachedBehaviorType, "TinyPadding");
                cachedTinyIndentField = AccessTools.Field(cachedBehaviorType, "TinyIndent");
                cachedMediumPaddingField = AccessTools.Field(cachedBehaviorType, "MediumPadding");

                // Cache custom interface reflection
                cachedCustomHandbookContentType = AccessTools.TypeByName("Vintagestory.GameContent.ICustomHandbookPageContent");
                cachedGetCollectibleInterface = AccessTools.Method(typeof(CollectibleObject), "GetCollectibleInterface");
                if (cachedCustomHandbookContentType != null)
                {
                    cachedOnHandbookPageComposed = AccessTools.Method(cachedCustomHandbookContentType, "OnHandbookPageComposed");
                }
            }
        }


        private static string GetPageCodeForStack(ItemStack stack)
        {
            if (stack == null || stack.Collectible?.Code == null) return string.Empty;

            if (stack.Attributes != null && stack.Attributes.Count > 0)
            {
                ITreeAttribute tree = stack.Attributes.Clone();
                foreach (var val in GlobalConstants.IgnoredStackAttributes) tree.RemoveAttribute(val);

                var sortedtree = tree.SortedCopy(true);
                if (tree.Count != 0)
                {
                    string treeStr = TreeAttribute.ToJsonToken(sortedtree);
                    return (stack.Class.Name()) + "-" + stack.Collectible.Code.ToShortString() + "-" + treeStr;
                }
            }

            return (stack.Class.Name()) + "-" + stack.Collectible.Code.ToShortString();
        }

        private static bool AddObtainedThroughInfoOptimized(
            ICoreClientAPI capi,
            ItemStack[] allStacks,
            ActionConsumable<string> openDetailPageFor,
            ItemStack stack,
            List<RichTextComponentBase> components,
            List<ItemStack> breakBlocks)
        {
            List<string> killCreatures = GetCachedKillCreatures(capi, stack) ?? new List<string>();
            List<string> harvestCreatures = GetCachedHarvestCreatures(capi, stack) ?? new List<string>();
            List<ItemStack> harvestBlocks = GetCachedHarvestBlocks(capi, stack) ?? new List<ItemStack>();
            harvestBlocks = UniqueStacksByKey(capi, harvestBlocks);

            bool haveText = components.Count > 0;
            int tinyPadding = GetTinyPadding();
            int tinyIndent = GetTinyIndent();

            if (killCreatures.Count > 0)
            {
                AddHeadingCached(components, capi, "Obtained by killing", ref haveText);
                components.Add(new ClearFloatTextComponent(capi, tinyPadding));
                var comp = new RichTextComponent(capi, string.Join(", ", killCreatures) + "\n", CairoFont.WhiteSmallText());
                comp.PaddingLeft = tinyIndent;
                components.Add(comp);
            }

            if (harvestCreatures.Count > 0)
            {
                AddHeadingCached(components, capi, "handbook-obtainedby-killing-harvesting", ref haveText);
                components.Add(new ClearFloatTextComponent(capi, tinyPadding));
                var comp = new RichTextComponent(capi, string.Join(", ", harvestCreatures) + "\n", CairoFont.WhiteSmallText());
                comp.PaddingLeft = tinyIndent;
                components.Add(comp);
            }

            if (breakBlocks != null && breakBlocks.Count > 0)
            {
                AddHeadingCached(components, capi, "Obtained by breaking", ref haveText);
                components.Add(new ClearFloatTextComponent(capi, tinyPadding));

                while (breakBlocks.Count > 0)
                {
                    ItemStack dstack = breakBlocks[0];
                    breakBlocks.RemoveAt(0);
                    if (dstack == null) continue;

                    SlideshowItemstackTextComponent comp = new SlideshowItemstackTextComponent(capi, dstack, breakBlocks, 40, EnumFloat.Inline, (cs) => openDetailPageFor(GetPageCodeForStack(cs)));
                    components.Add(comp);
                }

                components.Add(new ClearFloatTextComponent(capi, tinyPadding));
            }

            if (harvestBlocks.Count > 0)
            {
                AddHeadingCached(components, capi, "handbook-obtainedby-block-harvesting", ref haveText);
                components.Add(new ClearFloatTextComponent(capi, tinyPadding));

                while (harvestBlocks.Count > 0)
                {
                    ItemStack hstack = harvestBlocks[0];
                    harvestBlocks.RemoveAt(0);
                    if (hstack == null) continue;

                    SlideshowItemstackTextComponent comp = new SlideshowItemstackTextComponent(capi, hstack, harvestBlocks, 40, EnumFloat.Inline, (cs) => openDetailPageFor(GetPageCodeForStack(cs)));
                    components.Add(comp);
                }

                components.Add(new ClearFloatTextComponent(capi, tinyPadding));
            }

            return haveText;
        }

        /// <summary>
        /// Harmony prefix for CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo
        /// </summary>
        public static bool GetHandbookInfo_Prefix(
            object __instance,
            ItemSlot inSlot,
            ICoreClientAPI capi,
            ItemStack[] allStacks,
            ActionConsumable<string> openDetailPageFor,
            ref RichTextComponentBase[] __result)
        {
            try
            {
                ItemStack stack = inSlot.Itemstack;
                if (stack == null) return true; // Let original method handle null

                // If indexing is complete, use cached data
                if (isIndexed)
                {
                    __result = GetHandbookInfoOptimized(__instance, inSlot, capi, allStacks, openDetailPageFor);
                    return false; // Skip original method
                }

                // If indexing hasn't started yet, start it now
                if (!isIndexed && !isIndexing && allStacks != null)
                {
                    InitializeIndexAsync(capi, allStacks);
                }

                // Fall back to original method while indexing
                return true;
            }
            catch (Exception ex)
            {
                capi?.Logger?.Error($"[OptiTime] Error in handbook optimization: {ex}");
                return true; // Fall back to original on error
            }
        }

        /// <summary>
        /// Optimized version of GetHandbookInfo using cached data
        /// </summary>
        private static RichTextComponentBase[] GetHandbookInfoOptimized(
            object behaviorInstance,
            ItemSlot inSlot,
            ICoreClientAPI capi,
            ItemStack[] allStacks,
            ActionConsumable<string> openDetailPageFor)
        {
            ItemStack stack = inSlot.Itemstack;
            List<RichTextComponentBase> components = new List<RichTextComponentBase>();

            // Initialize cached reflection objects on first use
            InitializeReflectionCache();

            // Validate critical reflection methods loaded successfully
            if (cachedAddGeneralInfo == null || cachedAddDropsInfo == null || cachedAddObtainedThroughInfo == null)
            {
                capi.Logger.Error("[OptiTime] Failed to initialize handbook reflection cache - some methods not found");
                // Can't use optimized path without reflection, but we shouldn't reach here since we checked isIndexed
                return new RichTextComponentBase[0];
            }

            // Primary optimization: Use cached data instead of iterating through all stacks
            List<ItemStack> breakBlocks = GetCachedBlockDrops(capi, stack);
            List<ItemStack> harvestBlocks = GetCachedHarvestBlocks(capi, stack);
            List<string> killCreatures = GetCachedKillCreatures(capi, stack);
            List<string> harvestCreatures = GetCachedHarvestCreatures(capi, stack);

            // Get cached global lookups (containers, fuels, molds, anvils) - no longer loops through all stacks!
            GetCachedGlobalLookups(out var containers, out var fuels, out var molds, out var anvils);

            // Call the original helper methods via reflection to maintain compatibility
            // We're only optimizing the data gathering, not the rendering logic

            // addGeneralInfo
            object[] marginParams = new object[] { inSlot, capi, stack, components, 0f, 0f };
            cachedAddGeneralInfo?.Invoke(behaviorInstance, marginParams);
            float marginTop = (float)marginParams[4];
            float marginBottom = (float)marginParams[5];

            // addDropsInfo
            cachedAddDropsInfo?.Invoke(behaviorInstance, new object[] { capi, openDetailPageFor, stack, components, marginTop, breakBlocks });

            // addObtainedThroughInfo - optimized with caches for creatures/harvest blocks
            bool haveText = AddObtainedThroughInfoOptimized(capi, allStacks, openDetailPageFor, stack, components, breakBlocks);

            // addFoundInInfo
            object[] foundInParams = new object[] { capi, openDetailPageFor, stack, components, marginTop, haveText };
            cachedAddFoundInInfo?.Invoke(behaviorInstance, foundInParams);
            haveText = (bool)foundInParams[5];

            // addAlloyForInfo
            object[] alloyForParams = new object[] { capi, openDetailPageFor, stack, components, marginTop, containers, fuels, haveText };
            cachedAddAlloyForInfo?.Invoke(behaviorInstance, alloyForParams);
            haveText = (bool)alloyForParams[7];

            // addAlloyedFromInfo
            object[] alloyedFromParams = new object[] { capi, allStacks, openDetailPageFor, stack, components, marginTop, containers, fuels, haveText };
            cachedAddAlloyedFromInfo?.Invoke(behaviorInstance, alloyedFromParams);
            haveText = (bool)alloyedFromParams[8];

            // addProcessesIntoInfo
            object[] processesParams = new object[] { capi, openDetailPageFor, stack, components, marginTop, marginBottom, containers, fuels, haveText };
            cachedAddProcessesIntoInfo?.Invoke(behaviorInstance, processesParams);
            haveText = (bool)processesParams[8];

            // addProcessorForInfo (new in 1.22)
            object[] processorForParams = new object[] { capi, allStacks, openDetailPageFor, stack, components, marginTop, marginBottom, containers, fuels, molds, anvils, haveText };
            if (cachedAddProcessorForInfo != null)
            {
                cachedAddProcessorForInfo.Invoke(behaviorInstance, processorForParams);
                haveText = (bool)processorForParams[11];
            }

            // addIngredientForInfo
            object[] ingredientParams = new object[] { capi, allStacks, openDetailPageFor, stack, components, marginTop, containers, fuels, molds, haveText };
            cachedAddIngredientForInfo?.Invoke(behaviorInstance, ingredientParams);
            haveText = (bool)ingredientParams[9];

            // addCreatedByInfo (1.22: now takes anvils as 10th param)
            object[] createdByParams = new object[] { capi, allStacks, openDetailPageFor, stack, components, marginTop, containers, fuels, molds, anvils, haveText };
            cachedAddCreatedByInfo?.Invoke(behaviorInstance, createdByParams);

            // addExtraSections
            cachedAddExtraSections?.Invoke(behaviorInstance, new object[] { capi, stack, components, marginTop });

            // addEatenByInfo (new in 1.22)
            cachedAddEatenByInfo?.Invoke(behaviorInstance, new object[] { capi, stack, components, marginTop });

            // addStorableInfo — fall back to vanilla to ensure complete container coverage
            // Vanilla handles: BlockBehaviorDisplay/PlacementSurfaces, BlockPlantContainer,
            // BlockTroughBase/ItemSlotTrough, IWearableStatsSupplier/allowedDresstypes,
            // animalHusbandryStorables category, plus all existing types.
            // Ref: CollectibleBehaviorHandbookTextAndExtraInfo.cs addStorableInfo lines 2500-2640
            cachedAddStorableInfo?.Invoke(behaviorInstance, new object[] { capi, allStacks, openDetailPageFor, stack, components, marginTop });

            // addStoredInInfo — fall back to vanilla to ensure complete coverage
            // Vanilla handles: BlockPie special rendering (MealstackTextComponent with pie recipes),
            // BlockBehaviorDisplay, BlockPlantContainer, BlockTroughBase, IWearableStatsSupplier.
            // Ref: CollectibleBehaviorHandbookTextAndExtraInfo.cs addStoredInInfo lines 2650-2760
            cachedAddStoredInInfo?.Invoke(behaviorInstance, new object[] { capi, allStacks, openDetailPageFor, stack, components, marginTop });

            // Call custom handbook page content using cached reflection
            var collObj = cachedCollObjField?.GetValue(behaviorInstance);
            if (collObj != null && cachedGetCollectibleInterface != null && cachedCustomHandbookContentType != null)
            {
                try
                {
                    var genericMethod = cachedGetCollectibleInterface.MakeGenericMethod(cachedCustomHandbookContentType);
                    var customInterface = genericMethod.Invoke(collObj, null);
                    if (customInterface != null && cachedOnHandbookPageComposed != null)
                    {
                        cachedOnHandbookPageComposed.Invoke(customInterface, new object[] { components, inSlot, capi, allStacks, openDetailPageFor });
                    }
                }
                catch (Exception ex)
                {
                    capi?.Logger?.VerboseDebug($"[OptiTime] Error invoking custom handbook content: {ex.Message}");
                }
            }

            return components.ToArray();
        }

        private static void AddStorableInfoOptimized(ICoreClientAPI capi, ActionConsumable<string> openDetailPageFor, ItemStack stack, List<RichTextComponentBase> components, float marginTop)
        {
            // Get cached storage containers
            var storableContainers = GetCachedStorableIn(capi, stack, out bool groundStorable);
            if (storableContainers == null) return; // Cache not ready
            
            // Categorize containers
            List<ItemStack> foodStorables = new List<ItemStack>();
            List<ItemStack> liquidStorables = new List<ItemStack>();
            List<ItemStack> displayStorables = new List<ItemStack>();
            
            var blockCrockType = AccessTools.TypeByName("Vintagestory.GameContent.BlockCrock");
            var liquidInterfaceType = AccessTools.TypeByName("Vintagestory.GameContent.ILiquidInterface");
            
            foreach (var container in storableContainers)
            {
                if (blockCrockType != null && blockCrockType.IsInstanceOfType(container.Collectible))
                {
                    foodStorables.Add(container);
                }
                else if (liquidInterfaceType != null)
                {
                    var getInterfaceMethod = AccessTools.Method(typeof(CollectibleObject), "GetCollectibleInterface");
                    if (getInterfaceMethod != null)
                    {
                        var genericMethod = getInterfaceMethod.MakeGenericMethod(liquidInterfaceType);
                        var liquidInterface = genericMethod.Invoke(container.Collectible, null);
                        if (liquidInterface != null)
                        {
                            liquidStorables.Add(container);
                            continue;
                        }
                    }
                    displayStorables.Add(container);
                }
                else
                {
                    displayStorables.Add(container);
                }
            }
            
            displayStorables = UniqueStacksByKey(capi, displayStorables);
            liquidStorables = UniqueStacksByKey(capi, liquidStorables);
            foodStorables = UniqueStacksByKey(capi, foodStorables);
            
            if (!(foodStorables.Count > 0 || displayStorables.Count > 0 || liquidStorables.Count > 0 || groundStorable)) return;
            
            int smallPadding = GetCachedPadding(cachedMediumPaddingField, 7);
            int tinyPadding = GetTinyPadding();
            int tinyIndent = GetTinyIndent();
            
            bool haveText = components.Count > 0;
            components.Add(new ClearFloatTextComponent(capi, tinyPadding + 1));
            
            AddHeadingCached(components, capi, "Storable in/on", ref haveText);
            
            if (groundStorable)
            {
                components.Add(new ClearFloatTextComponent(capi, smallPadding));
                var comp = new RichTextComponent(capi, "", CairoFont.WhiteSmallText());
                comp.PaddingLeft = tinyIndent;
                components.Add(comp);
                var vtmlUtilType = AccessTools.TypeByName("Vintagestory.API.Client.VtmlUtil");
                if (vtmlUtilType != null)
                {
                    var richtextifyMethod = AccessTools.Method(vtmlUtilType, "Richtextify", new[] { typeof(ICoreClientAPI), typeof(string), typeof(CairoFont) });
                    if (richtextifyMethod != null)
                    {
                        var richComponents = richtextifyMethod.Invoke(null, new object[] { capi, Lang.Get("handbook-storable-ground") + "\n", CairoFont.WhiteSmallText() });
                        if (richComponents is IEnumerable<RichTextComponentBase> enumerable)
                        {
                            components.AddRange(enumerable);
                        }
                    }
                }
            }
            
            if (displayStorables.Count > 0)
            {
                components.Add(new ClearFloatTextComponent(capi, smallPadding));
                AddSubHeadingCached(components, capi, openDetailPageFor, "handbook-storable-displaycontainers", null);
                
                int firstPadding = tinyPadding;
                while (displayStorables.Count > 0)
                {
                    ItemStack dstack = displayStorables[0];
                    displayStorables.RemoveAt(0);
                    if (dstack == null) continue;
                    
                    SlideshowItemstackTextComponent comp = new SlideshowItemstackTextComponent(capi, dstack, displayStorables, 40, EnumFloat.Inline, (cs) => openDetailPageFor(GetPageCodeForStack(cs)));
                    comp.PaddingLeft = firstPadding;
                    firstPadding = 0;
                    components.Add(comp);
                }
                components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
            }
            
            if (liquidStorables.Count > 0)
            {
                components.Add(new ClearFloatTextComponent(capi, smallPadding));
                AddSubHeadingCached(components, capi, openDetailPageFor, "handbook-storable-liquidcontainers", null);
                
                int firstPadding = tinyPadding;
                while (liquidStorables.Count > 0)
                {
                    ItemStack dstack = liquidStorables[0];
                    liquidStorables.RemoveAt(0);
                    if (dstack == null) continue;
                    
                    SlideshowItemstackTextComponent comp = new SlideshowItemstackTextComponent(capi, dstack, liquidStorables, 40, EnumFloat.Inline, (cs) => openDetailPageFor(GetPageCodeForStack(cs)));
                    comp.PaddingLeft = firstPadding;
                    firstPadding = 0;
                    components.Add(comp);
                }
                components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
            }
            
            if (foodStorables.Count > 0)
            {
                components.Add(new ClearFloatTextComponent(capi, smallPadding));
                AddSubHeadingCached(components, capi, openDetailPageFor, "handbook-storable-foodcontainers", null);
                
                int firstPadding = tinyPadding;
                while (foodStorables.Count > 0)
                {
                    ItemStack dstack = foodStorables[0];
                    foodStorables.RemoveAt(0);
                    if (dstack == null) continue;
                    
                    SlideshowItemstackTextComponent comp = new SlideshowItemstackTextComponent(capi, dstack, foodStorables, 40, EnumFloat.Inline, (cs) => openDetailPageFor(GetPageCodeForStack(cs)));
                    comp.PaddingLeft = firstPadding;
                    firstPadding = 0;
                    components.Add(comp);
                }
                components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
            }
        }

        private static void AddStoredInInfoOptimized(ICoreClientAPI capi, ActionConsumable<string> openDetailPageFor, ItemStack stack, List<RichTextComponentBase> components, float marginTop)
        {
            var storables = GetCachedStoredIn(capi, stack);
            if (storables == null) return; // Cache not ready
            
            storables = UniqueStacksByKey(capi, storables);
            
            if (storables.Count == 0) return;
            
            int smallPadding = GetCachedPadding(cachedMediumPaddingField, 7);
            int tinyPadding = GetTinyPadding();
            
            bool haveText = components.Count > 0;
            components.Add(new ClearFloatTextComponent(capi, smallPadding));
            AddHeadingCached(components, capi, "handbook-storedin", ref haveText);
            
            int firstPadding = tinyPadding;
            while (storables.Count > 0)
            {
                ItemStack dstack = storables[0];
                storables.RemoveAt(0);
                if (dstack == null) continue;
                
                SlideshowItemstackTextComponent comp = new SlideshowItemstackTextComponent(capi, dstack, storables, 40, EnumFloat.Inline, (cs) => openDetailPageFor(GetPageCodeForStack(cs)));
                comp.PaddingLeft = firstPadding;
                firstPadding = 0;
                components.Add(comp);
            }
            components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
        }

        private static void AddSubHeadingCached(List<RichTextComponentBase> components, ICoreClientAPI capi, ActionConsumable<string> openDetailPageFor, string langCode, string linkPageCode)
        {
            int tinyIndent = GetTinyIndent();
            var comp = new RichTextComponent(capi, "", CairoFont.WhiteSmallText());
            comp.PaddingLeft = tinyIndent;
            components.Add(comp);
            
            if (linkPageCode != null)
            {
                var linkComp = new LinkTextComponent(capi, Lang.Get(langCode) + "\n", CairoFont.WhiteSmallText(), (cs) => openDetailPageFor(linkPageCode));
                components.Add(linkComp);
            }
            else
            {
                components.Add(new RichTextComponent(capi, Lang.Get(langCode) + "\n", CairoFont.WhiteSmallText()));
            }
        }

    }
}
