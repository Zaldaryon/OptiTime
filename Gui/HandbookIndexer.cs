using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace OptiTime
{
    /// <summary>
    /// Handbook optimization: async indexing of stack, entity, and storage relationships.
    /// </summary>
    public partial class HandbookOptimization
    {
        /// <summary>
        /// Build the relationship index asynchronously so handbook opening does not block the client thread.
        /// </summary>
        public static void InitializeIndexAsync(ICoreClientAPI capi, ItemStack[] allStacks)
        {
            if (isIndexed || isIndexing) return;

            lock (indexLock)
            {
                if (isIndexed || isIndexing) return;
                isIndexing = true;
                indexCts?.Dispose();
                indexCts = new System.Threading.CancellationTokenSource();
            }

            var cts = indexCts;
            int startGen = Volatile.Read(ref disposeGeneration);

            Task.Run(() =>
            {
                try
                {
                    var token = cts.Token;
                    token.ThrowIfCancellationRequested();

                    capi.Logger.VerboseDebug("[OptiTime] Starting handbook relationship indexing...");
                    var startTime = capi.World.ElapsedMilliseconds;
                    if (ProfilingHelper.Enabled)
                    {
                        ProfilingHelper.Mark("opt-handbook-index-start", $"count={allStacks.Length}");
                    }

                    BuildAllStacksOrderCache(capi.World, allStacks);
                    token.ThrowIfCancellationRequested();

                    Parallel.ForEach(allStacks, new ParallelOptions { CancellationToken = token }, stack =>
                    {
                        IndexStackRelationships(capi, stack, allStacks);
                    });

                    token.ThrowIfCancellationRequested();
                    IndexEntityRelationships(capi);
                    token.ThrowIfCancellationRequested();
                    IndexStorageRelationships(capi, allStacks);
                    token.ThrowIfCancellationRequested();

                    // Check generation before writing results — if Cleanup ran, discard
                    if (Volatile.Read(ref disposeGeneration) != startGen) return;

                    FreezeIndexCaches(capi);

                    var elapsed = capi.World.ElapsedMilliseconds - startTime;
                    capi.Logger.Notification($"[OptiTime] Handbook indexing completed in {elapsed}ms. Indexed {allStacks.Length} items.");
                    if (ProfilingHelper.Enabled)
                    {
                        ProfilingHelper.Mark("opt-handbook-index-end", $"ms={elapsed}");
                    }

                    lock (indexLock)
                    {
                        if (Volatile.Read(ref disposeGeneration) != startGen) return;
                        isIndexed = true;
                        isIndexing = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when Cleanup is called during indexing
                }
                catch (Exception ex)
                {
                    capi.Logger.Error($"[OptiTime] Error during handbook indexing: {ex}");
                    lock (indexLock)
                    {
                        isIndexing = false;
                    }
                }
            });
        }

        private static void IndexStackRelationships(ICoreClientAPI capi, ItemStack stack, ItemStack[] allStacks)
        {
            try
            {
                string stackKey = GetStackKey(capi.World, stack);

                // Index what blocks drop this item
                if (stack.Block != null)
                {
                    var droppedStacks = stack.Block.GetDropsForHandbook(stack, capi.World.Player);
                    if (droppedStacks != null)
                    {
                        foreach (var dstack in droppedStacks)
                        {
                            if (dstack?.ResolvedItemstack == null) continue;
                            string targetKey = GetStackKey(capi.World, dstack.ResolvedItemstack);
                            blockDropsCache.AddOrUpdate(targetKey,
                                key =>
                                {
                                    var queue = new ConcurrentQueue<ItemStack>();
                                    queue.Enqueue(stack);
                                    return queue;
                                },
                                (key, queue) => { queue.Enqueue(stack); return queue; });
                        }
                    }

                    // Index harvest relationships
                    // Vanilla priority (addObtainedThroughInfo): Harvestable ?? FruitingBush, then FruitTreeBranch
                    // Vanilla priority (addDropsInfo): FruitingBush first, then Harvestable, then FruitTreeBranch
                    // Ref: CollectibleBehaviorHandbookTextAndExtraInfo.cs lines 226-260, 340-365
                    BlockDropItemStack[] harvestedStacks = null;

                    var harvestableType = AccessTools.TypeByName("Vintagestory.GameContent.BlockBehaviorHarvestable");
                    if (harvestableType != null)
                    {
                        var getBehaviorMethod = AccessTools.Method(stack.Block.GetType(), "GetBehavior");
                        if (getBehaviorMethod != null)
                        {
                            var genericMethod = getBehaviorMethod.MakeGenericMethod(harvestableType);
                            var harvestable = genericMethod.Invoke(stack.Block, null);
                            if (harvestable != null)
                            {
                                var harvestedStacksField = AccessTools.Field(harvestableType, "harvestedStacks");
                                harvestedStacks = harvestedStacksField?.GetValue(harvestable) as BlockDropItemStack[];
                            }
                        }
                    }

                    // BlockBehaviorFruitingBush — berry bushes (blueberry, cranberry, etc.)
                    // Ref: BlockBehaviorFruitingBush.cs — harvestedStacks loaded from block.Attributes["harvestedStacks"]
                    if (harvestedStacks == null)
                    {
                        var fruitingBushType = AccessTools.TypeByName("Vintagestory.GameContent.BlockBehaviorFruitingBush");
                        if (fruitingBushType != null)
                        {
                            var getBehaviorMethod = AccessTools.Method(stack.Block.GetType(), "GetBehavior");
                            if (getBehaviorMethod != null)
                            {
                                var genericMethod = getBehaviorMethod.MakeGenericMethod(fruitingBushType);
                                var fruitingBush = genericMethod.Invoke(stack.Block, null);
                                if (fruitingBush != null)
                                {
                                    var field = AccessTools.Field(fruitingBushType, "harvestedStacks");
                                    harvestedStacks = field?.GetValue(fruitingBush) as BlockDropItemStack[];
                                }
                            }
                        }
                    }

                    // BlockFruitTreeBranch — fruit trees (cherry, peach, etc.)
                    // Ref: BlockFruitTreeBranch.cs — TypeProps[type].FruitStacks
                    if (harvestedStacks == null)
                    {
                        var fruitTreeBranchType = AccessTools.TypeByName("Vintagestory.GameContent.BlockFruitTreeBranch");
                        if (fruitTreeBranchType != null && fruitTreeBranchType.IsInstanceOfType(stack.Block))
                        {
                            var typePropsField = AccessTools.Field(fruitTreeBranchType, "TypeProps");
                            if (typePropsField?.GetValue(stack.Block) is System.Collections.IDictionary typeProps)
                            {
                                string treeType = stack.Attributes?.GetString("type", "unknown") ?? "unknown";
                                if (typeProps.Contains(treeType))
                                {
                                    var props = typeProps[treeType];
                                    var fruitStacksField = AccessTools.Field(props.GetType(), "FruitStacks");
                                    harvestedStacks = fruitStacksField?.GetValue(props) as BlockDropItemStack[];
                                }
                            }
                        }
                    }

                    if (harvestedStacks != null)
                    {
                        foreach (var hstack in harvestedStacks)
                        {
                            if (hstack?.ResolvedItemstack == null) continue;
                            string targetKey = GetStackKey(capi.World, hstack.ResolvedItemstack);
                            harvestBlocksCache.AddOrUpdate(targetKey,
                                key =>
                                {
                                    var queue = new ConcurrentQueue<ItemStack>();
                                    queue.Enqueue(stack);
                                    return queue;
                                },
                                (key, queue) => { queue.Enqueue(stack); return queue; });
                        }
                    }
                }

                // Cache containers, fuels, and molds during indexing
                if (stack.ItemAttributes?.KeyExists("cookingContainerSlots") == true)
                {
                    containersCache.Enqueue(stack);
                }

                var combustibleProps = stack.Collectible?.GetCombustibleProperties(capi.World, stack, null);
                if (combustibleProps?.BurnDuration != null || combustibleProps?.BurnTemperature != null)
                {
                    fuelsCache.Enqueue(stack);
                }

                var blockToolMoldType = AccessTools.TypeByName("Vintagestory.GameContent.BlockToolMold");
                var blockIngotMoldType = AccessTools.TypeByName("Vintagestory.GameContent.BlockIngotMold");
                var collectibleType = stack.Collectible.GetType();

                if ((blockToolMoldType != null && blockToolMoldType.IsAssignableFrom(collectibleType)) ||
                    (blockIngotMoldType != null && blockIngotMoldType.IsAssignableFrom(collectibleType)))
                {
                    moldsCache.Enqueue(stack);
                }

                var blockAnvilType = AccessTools.TypeByName("Vintagestory.GameContent.BlockAnvil");
                if (blockAnvilType != null && blockAnvilType.IsAssignableFrom(collectibleType))
                {
                    anvilsCache.Enqueue(stack);
                }
            }
            catch (Exception ex)
            {
                // Log errors but continue indexing
                capi?.Logger?.VerboseDebug($"[OptiTime] Handbook indexing error for {stack?.Collectible?.Code}: {ex.Message}");
            }
        }

        private static void IndexEntityRelationships(ICoreClientAPI capi)
        {
            try
            {
                foreach (var entityType in capi.World.EntityTypes)
                {
                    if (entityType.Drops != null)
                    {
                        foreach (var drop in entityType.Drops)
                        {
                            if (drop?.ResolvedItemstack == null) continue;
                            string stackKey = GetStackKey(capi.World, drop.ResolvedItemstack);
                            string creatureName = Lang.Get(entityType.Code.Domain + ":item-creature-" + entityType.Code.Path);

                            killCreaturesCache.AddOrUpdate(stackKey,
                                key =>
                                {
                                    var queue = new ConcurrentQueue<string>();
                                    queue.Enqueue(creatureName);
                                    return queue;
                                },
                                (key, queue) =>
                                {
                                    queue.Enqueue(creatureName);  // Allow duplicates, will be de-duped on return
                                    return queue;
                                });
                        }
                    }

                    var harvestableDrops = entityType.Attributes?["harvestableDrops"]?.AsArray<BlockDropItemStack>();
                    if (harvestableDrops != null)
                    {
                        foreach (var hstack in harvestableDrops)
                        {
                            hstack.Resolve(capi.World, "handbook info", new AssetLocation());
                            if (hstack?.ResolvedItemstack == null) continue;

                            string stackKey = GetStackKey(capi.World, hstack.ResolvedItemstack);
                            string code = entityType.Code.Domain + ":item-creature-" + entityType.Code.Path;

                            if (entityType.Attributes?["handbook"]["groupcode"]?.Exists == true)
                            {
                                code = entityType.Attributes["handbook"]["groupcode"].AsString();
                            }

                            string creatureName = Lang.Get(code);

                            harvestCreaturesCache.AddOrUpdate(stackKey,
                                key =>
                                {
                                    var queue = new ConcurrentQueue<string>();
                                    queue.Enqueue(creatureName);
                                    return queue;
                                },
                                (key, queue) =>
                                {
                                    queue.Enqueue(creatureName);  // Allow duplicates, will be de-duped on return
                                    return queue;
                                });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                capi?.Logger?.Error($"[OptiTime] Error indexing entity relationships: {ex.Message}");
            }
        }

        // Cached reflection for storage relationship checks (F3: hoisted out of O(N²) loop)

        private static Type cachedBlockShelfType;
        private static Type cachedBlockToolRackType;
        private static Type cachedBlockMoldRackType;
        private static Type cachedBlockBookshelfType;
        private static Type cachedBlockScrollRackType;
        private static Type cachedBlockDisplayCaseType;
        private static Type cachedBlockAntlerMountType;
        private static Type cachedBlockOmokTableType;
        private static Type cachedBlockAnimalTrapType;
        private static Type cachedBlockCrockType;
        private static Type cachedLiquidInterfaceType;
        private static Type cachedBlockLiquidContainerBaseType;
        private static System.Reflection.MethodInfo cachedShelfLayoutMethod;
        private static System.Reflection.FieldInfo cachedDisplayCaseHeightField;
        private static System.Reflection.MethodInfo cachedIsAppetizingBaitMethod;
        private static System.Reflection.MethodInfo cachedCanFitBaitMethod;
        private static System.Reflection.MethodInfo cachedGetCollectibleInterfaceMethod;
        private static System.Reflection.MethodInfo cachedGetCurrentLitresMethod;
        private static System.Reflection.MethodInfo cachedGetContainablePropsMethod;
        private static bool storageReflectionInitialized;
        private static readonly System.Threading.Lock storageReflectionLock = new();

        private static void InitializeStorageReflection()
        {
            if (storageReflectionInitialized) return;
            lock (storageReflectionLock)
            {
                if (storageReflectionInitialized) return;

                cachedBlockShelfType = AccessTools.TypeByName("Vintagestory.GameContent.BlockShelf");
                cachedBlockToolRackType = AccessTools.TypeByName("Vintagestory.GameContent.BlockToolRack");
                cachedBlockMoldRackType = AccessTools.TypeByName("Vintagestory.GameContent.BlockMoldRack");
                cachedBlockBookshelfType = AccessTools.TypeByName("Vintagestory.GameContent.BlockBookshelf");
                cachedBlockScrollRackType = AccessTools.TypeByName("Vintagestory.GameContent.BlockScrollRack");
                cachedBlockDisplayCaseType = AccessTools.TypeByName("Vintagestory.GameContent.BlockDisplayCase");
                cachedBlockAntlerMountType = AccessTools.TypeByName("Vintagestory.GameContent.BlockAntlerMount");
                cachedBlockOmokTableType = AccessTools.TypeByName("Vintagestory.GameContent.BlockOmokTable");
                cachedBlockAnimalTrapType = AccessTools.TypeByName("Vintagestory.GameContent.BlockAnimalTrap");
                cachedBlockCrockType = AccessTools.TypeByName("Vintagestory.GameContent.BlockCrock");
                cachedLiquidInterfaceType = AccessTools.TypeByName("Vintagestory.GameContent.ILiquidInterface");
                cachedBlockLiquidContainerBaseType = AccessTools.TypeByName("Vintagestory.GameContent.BlockLiquidContainerBase");

                var blockEntityShelfType = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityShelf");
                if (blockEntityShelfType != null)
                    cachedShelfLayoutMethod = AccessTools.Method(blockEntityShelfType, "GetShelvableLayout", new[] { typeof(ItemStack) });

                if (cachedBlockDisplayCaseType != null)
                    cachedDisplayCaseHeightField = AccessTools.Field(cachedBlockDisplayCaseType, "height");

                if (cachedBlockAnimalTrapType != null)
                {
                    cachedIsAppetizingBaitMethod = AccessTools.Method(cachedBlockAnimalTrapType, "IsAppetizingBait");
                    cachedCanFitBaitMethod = AccessTools.Method(cachedBlockAnimalTrapType, "CanFitBait");
                }

                cachedGetCollectibleInterfaceMethod = AccessTools.Method(typeof(CollectibleObject), "GetCollectibleInterface");

                if (cachedLiquidInterfaceType != null)
                    cachedGetCurrentLitresMethod = AccessTools.Method(cachedLiquidInterfaceType, "GetCurrentLitres");

                if (cachedBlockLiquidContainerBaseType != null)
                    cachedGetContainablePropsMethod = AccessTools.Method(cachedBlockLiquidContainerBaseType, "GetContainableProps", new[] { typeof(ItemStack) });

                storageReflectionInitialized = true;
            }
        }

        private static void IndexStorageRelationships(ICoreClientAPI capi, ItemStack[] allStacks)
        {
            try
            {
                InitializeStorageReflection();
                var groundStorableType = AccessTools.TypeByName("Vintagestory.GameContent.CollectibleBehaviorGroundStorable");

                // Index ground storable items
                foreach (var stack in allStacks)
                {
                    if (stack?.Collectible == null) continue;

                    if (groundStorableType != null && stack.Collectible.HasBehavior(groundStorableType, false))
                    {
                        string stackKey = GetStackKey(capi.World, stack);
                        groundStorableCache.TryAdd(stackKey, true);
                    }
                }

                // Index storage relationships with the same directional rules the vanilla handbook uses.
                foreach (var item in allStacks)
                {
                    if (item?.Collectible == null) continue;

                    foreach (var container in allStacks)
                    {
                        if (container?.Collectible == null) continue;

                        try
                        {
                            if (CanItemBeStoredInContainer(capi, item, container))
                            {
                                string itemKey = GetStackKey(capi.World, item);
                                storableInCache.AddOrUpdate(itemKey,
                                    key =>
                                    {
                                        var queue = new ConcurrentQueue<ItemStack>();
                                        queue.Enqueue(container);
                                        return queue;
                                    },
                                    (key, queue) => { queue.Enqueue(container); return queue; });
                            }

                            if (CanContainerStoreItem(capi, container, item))
                            {
                                string containerKey = GetStackKey(capi.World, container);
                                storedInCache.AddOrUpdate(containerKey,
                                    key =>
                                    {
                                        var queue = new ConcurrentQueue<ItemStack>();
                                        queue.Enqueue(item);
                                        return queue;
                                    },
                                    (key, queue) => { queue.Enqueue(item); return queue; });
                            }
                        }
                        catch (Exception ex)
                        {
                            capi?.Logger?.VerboseDebug($"[OptiTime] Error checking storage relationship: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                capi?.Logger?.Error($"[OptiTime] Error indexing storage relationships: {ex.Message}");
            }
        }

        private static bool CanItemBeStoredInContainer(ICoreClientAPI capi, ItemStack item, ItemStack container)
        {
            if (cachedBlockShelfType != null && cachedBlockShelfType.IsInstanceOfType(container.Collectible))
            {
                if (cachedShelfLayoutMethod != null)
                {
                    var layout = cachedShelfLayoutMethod.Invoke(null, new object[] { item });
                    if (layout != null) return true;
                }
            }

            if (cachedBlockToolRackType != null && cachedBlockToolRackType.IsInstanceOfType(container.Collectible))
            {
                if (item.Collectible.Tool != null || item.ItemAttributes?["rackable"].AsBool() == true)
                    return true;
            }

            if (item.ItemAttributes is not JsonObject attr) return false;

            if (cachedBlockMoldRackType != null && cachedBlockMoldRackType.IsInstanceOfType(container.Collectible) && attr["moldrackable"].AsBool())
                return true;

            if (cachedBlockBookshelfType != null && cachedBlockBookshelfType.IsInstanceOfType(container.Collectible) && attr["bookshelveable"].AsBool())
                return true;

            if (cachedBlockScrollRackType != null && cachedBlockScrollRackType.IsInstanceOfType(container.Collectible) && attr["scrollrackable"].AsBool())
                return true;

            if (attr["displaycaseable"].AsBool() && cachedBlockDisplayCaseType != null && cachedBlockDisplayCaseType.IsInstanceOfType(container.Collectible))
            {
                float minHeight = attr["displaycase"]["minHeight"].AsFloat(0.25f);
                if (cachedDisplayCaseHeightField != null)
                {
                    float height = (float)cachedDisplayCaseHeightField.GetValue(container.Collectible);
                    if (height >= minHeight) return true;
                }
            }

            if (cachedBlockAntlerMountType != null && cachedBlockAntlerMountType.IsInstanceOfType(container.Collectible) && attr["antlerMountable"].AsBool())
                return true;

            if (cachedBlockOmokTableType != null && cachedBlockOmokTableType.IsInstanceOfType(container.Collectible) && attr["omokpiece"].AsBool())
                return true;

            if (cachedBlockAnimalTrapType != null && cachedBlockAnimalTrapType.IsInstanceOfType(container.Collectible))
            {
                if (cachedIsAppetizingBaitMethod != null && cachedCanFitBaitMethod != null)
                {
                    try
                    {
                        bool appetizing = (bool)cachedIsAppetizingBaitMethod.Invoke(container.Collectible, new object[] { capi, item });
                        bool canFit = (bool)cachedCanFitBaitMethod.Invoke(container.Collectible, new object[] { capi, item });
                        if (appetizing && canFit) return true;
                    }
                    catch { }
                }
            }

            if (attr["waterTightContainerProps"].Exists)
            {
                if (cachedLiquidInterfaceType != null && cachedGetCollectibleInterfaceMethod != null)
                {
                    var genericMethod = cachedGetCollectibleInterfaceMethod.MakeGenericMethod(cachedLiquidInterfaceType);
                    var liquidInterface = genericMethod.Invoke(container.Collectible, null);
                    if (liquidInterface != null && cachedGetCurrentLitresMethod != null)
                    {
                        float litres = (float)cachedGetCurrentLitresMethod.Invoke(liquidInterface, new object[] { container });
                        if (litres <= 0) return true;
                    }
                }
            }

            if (cachedBlockCrockType != null && cachedBlockCrockType.IsInstanceOfType(container.Collectible) && attr["crockable"].AsBool())
                return true;

            return false;
        }

        private static bool CanContainerStoreItem(ICoreClientAPI capi, ItemStack container, ItemStack item)
        {
            if (cachedBlockShelfType != null && cachedBlockShelfType.IsInstanceOfType(container.Collectible))
            {
                if (cachedShelfLayoutMethod != null)
                {
                    var layout = cachedShelfLayoutMethod.Invoke(null, new object[] { item });
                    if (layout != null) return true;
                }
            }

            if (cachedBlockToolRackType != null && cachedBlockToolRackType.IsInstanceOfType(container.Collectible))
            {
                if (item.Collectible.Tool != null || item.ItemAttributes?["rackable"].AsBool() == true)
                    return true;
            }

            if (item.ItemAttributes is not JsonObject attr) return false;

            if (cachedBlockMoldRackType != null && cachedBlockMoldRackType.IsInstanceOfType(container.Collectible) && attr["moldrackable"].AsBool())
                return true;

            if (cachedBlockBookshelfType != null && cachedBlockBookshelfType.IsInstanceOfType(container.Collectible) && attr["bookshelveable"].AsBool())
                return true;

            if (cachedBlockScrollRackType != null && cachedBlockScrollRackType.IsInstanceOfType(container.Collectible) && attr["scrollrackable"].AsBool())
                return true;

            if (attr["displaycaseable"].AsBool() && cachedBlockDisplayCaseType != null && cachedBlockDisplayCaseType.IsInstanceOfType(container.Collectible))
            {
                float minHeight = attr["displaycase"]["minHeight"].AsFloat(0.25f);
                if (cachedDisplayCaseHeightField != null)
                {
                    float height = (float)cachedDisplayCaseHeightField.GetValue(container.Collectible);
                    if (height >= minHeight) return true;
                }
            }

            if (cachedBlockAntlerMountType != null && cachedBlockAntlerMountType.IsInstanceOfType(container.Collectible) && attr["antlerMountable"].AsBool())
                return true;

            if (cachedBlockOmokTableType != null && cachedBlockOmokTableType.IsInstanceOfType(container.Collectible) && attr["omokpiece"].AsBool())
                return true;

            if (cachedBlockAnimalTrapType != null && cachedBlockAnimalTrapType.IsInstanceOfType(container.Collectible))
            {
                if (cachedIsAppetizingBaitMethod != null && cachedCanFitBaitMethod != null)
                {
                    try
                    {
                        bool appetizing = (bool)cachedIsAppetizingBaitMethod.Invoke(container.Collectible, new object[] { capi, item });
                        bool canFit = (bool)cachedCanFitBaitMethod.Invoke(container.Collectible, new object[] { capi, item });
                        if (appetizing && canFit) return true;
                    }
                    catch { }
                }
            }

            if (cachedLiquidInterfaceType != null && cachedGetCollectibleInterfaceMethod != null)
            {
                var genericMethod = cachedGetCollectibleInterfaceMethod.MakeGenericMethod(cachedLiquidInterfaceType);
                var liquidInterface = genericMethod.Invoke(container.Collectible, null);
                if (liquidInterface != null && cachedGetContainablePropsMethod != null)
                {
                    var containableProps = cachedGetContainablePropsMethod.Invoke(null, new object[] { item });
                    if (containableProps != null)
                    {
                        var whenFilledField = AccessTools.Field(containableProps.GetType(), "WhenFilled");
                        var whenFilledProperty = AccessTools.Property(containableProps.GetType(), "WhenFilled");
                        object whenFilled = whenFilledField?.GetValue(containableProps) ?? whenFilledProperty?.GetValue(containableProps);
                        if (whenFilled == null) return true;
                    }
                }
            }

            if (cachedBlockCrockType != null && cachedBlockCrockType.IsInstanceOfType(container.Collectible) && attr["crockable"].AsBool())
                return true;

            return false;
        }
    }
}
