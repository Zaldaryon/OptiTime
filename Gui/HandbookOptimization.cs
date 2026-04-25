using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace OptiTime
{
    /// <summary>
    /// Optimizes handbook page loading by caching relationship data.
    /// Primary: Cache relationship data (block drops, harvests, entities, containers, fuels, molds)
    /// Secondary: Cache reflection MethodInfo objects to reduce overhead
        /// Tertiary: Freeze the final index once and serve read-heavy handbook lookups from immutable snapshots
        /// </summary>
    public class HandbookOptimization
    {
        private sealed class StackKeyCacheEntry
        {
            public int Hash;
            public string Key = string.Empty;
        }

        // Relationship caches (Primary optimization) - mutable while indexing
        private static ConcurrentDictionary<string, ConcurrentQueue<ItemStack>> blockDropsCache = new();
        private static ConcurrentDictionary<string, ConcurrentQueue<ItemStack>> harvestBlocksCache = new();
        private static ConcurrentQueue<ItemStack> containersCache = new();
        private static ConcurrentQueue<ItemStack> fuelsCache = new();
        private static ConcurrentQueue<ItemStack> moldsCache = new();
        private static ConcurrentQueue<ItemStack> anvilsCache = new();
        private static ConcurrentDictionary<string, ConcurrentQueue<string>> killCreaturesCache = new();
        private static ConcurrentDictionary<string, ConcurrentQueue<string>> harvestCreaturesCache = new();

        // Storage relationship caches (addStorableInfo / addStoredInInfo)
        private static ConcurrentDictionary<string, ConcurrentQueue<ItemStack>> storableInCache = new();
        private static ConcurrentDictionary<string, ConcurrentQueue<ItemStack>> storedInCache = new();
        private static ConcurrentDictionary<string, bool> groundStorableCache = new();

        // Immutable snapshots after indexing completes
        private static FrozenDictionary<string, ItemStack[]> frozenBlockDropsCache = null;
        private static FrozenDictionary<string, ItemStack[]> frozenHarvestBlocksCache = null;
        private static FrozenDictionary<string, string[]> frozenKillCreaturesCache = null;
        private static FrozenDictionary<string, string[]> frozenHarvestCreaturesCache = null;
        private static FrozenDictionary<string, ItemStack[]> frozenStorableInCache = null;
        private static FrozenDictionary<string, ItemStack[]> frozenStoredInCache = null;
        private static FrozenDictionary<string, bool> frozenGroundStorableCache = null;
        private static ItemStack[] frozenContainersCache = null;
        private static ItemStack[] frozenFuelsCache = null;
        private static ItemStack[] frozenMoldsCache = null;
        private static ItemStack[] frozenAnvilsCache = null;

        // Cached allStacks order for sorting (built once during indexing)
        private static Dictionary<string, int> allStacksOrderCache = null;
        private static FrozenDictionary<string, int> allStacksOrderFrozen = null;
        private static bool isFrozen = false;
        private static ConditionalWeakTable<ItemStack, StackKeyCacheEntry> stackKeyCache = new();

        // Indexing state
        private static bool isIndexed = false;
        private static bool isIndexing = false;
        private static readonly System.Threading.Lock indexLock = new();
        private static System.Threading.CancellationTokenSource indexCts = null;
        private static int disposeGeneration = 0;

        // Cached reflection MethodInfo objects to reduce overhead
        private static System.Reflection.MethodInfo cachedAddGeneralInfo;
        private static System.Reflection.MethodInfo cachedAddDropsInfo;
        private static System.Reflection.MethodInfo cachedAddObtainedThroughInfo;
        private static System.Reflection.MethodInfo cachedAddFoundInInfo;
        private static System.Reflection.MethodInfo cachedAddAlloyForInfo;
        private static System.Reflection.MethodInfo cachedAddAlloyedFromInfo;
        private static System.Reflection.MethodInfo cachedAddProcessesIntoInfo;
        private static System.Reflection.MethodInfo cachedAddIngredientForInfo;
        private static System.Reflection.MethodInfo cachedAddCreatedByInfo;
        private static System.Reflection.MethodInfo cachedAddProcessorForInfo;
        private static System.Reflection.MethodInfo cachedAddEatenByInfo;
        private static System.Reflection.MethodInfo cachedAddExtraSections;
        private static System.Reflection.MethodInfo cachedAddStorableInfo;
        private static System.Reflection.MethodInfo cachedAddStoredInInfo;
        private static System.Reflection.MethodInfo cachedAddHeading;
        private static System.Reflection.FieldInfo cachedCollObjField;
        private static System.Reflection.MethodInfo cachedGetCollectibleInterface;
        private static System.Reflection.MethodInfo cachedOnHandbookPageComposed;
        private static Type cachedBehaviorType;
        private static Type cachedCustomHandbookContentType;
        private static readonly System.Threading.Lock reflectionInitLock = new();
        private static System.Reflection.FieldInfo cachedTinyPaddingField;
        private static System.Reflection.FieldInfo cachedTinyIndentField;
        private static System.Reflection.FieldInfo cachedMediumPaddingField;

        public static void ClearCache()
        {
            blockDropsCache.Clear();
            harvestBlocksCache.Clear();

            // Clear the concurrent bags by creating new instances
            containersCache = new ConcurrentQueue<ItemStack>();
            fuelsCache = new ConcurrentQueue<ItemStack>();
            moldsCache = new ConcurrentQueue<ItemStack>();
            anvilsCache = new ConcurrentQueue<ItemStack>();

            killCreaturesCache.Clear();
            harvestCreaturesCache.Clear();
            
            storableInCache.Clear();
            storedInCache.Clear();
            groundStorableCache.Clear();

            allStacksOrderCache = null;
            allStacksOrderFrozen = null;
            stackKeyCache = new ConditionalWeakTable<ItemStack, StackKeyCacheEntry>();

            frozenBlockDropsCache = null;
            frozenHarvestBlocksCache = null;
            frozenKillCreaturesCache = null;
            frozenHarvestCreaturesCache = null;
            frozenStorableInCache = null;
            frozenStoredInCache = null;
            frozenGroundStorableCache = null;
            frozenContainersCache = null;
            frozenFuelsCache = null;
            frozenMoldsCache = null;
            frozenAnvilsCache = null;
            isFrozen = false;

            isIndexed = false;
            isIndexing = false;

            // Cancel any running indexing task
            try { indexCts?.Cancel(); } catch { }
            indexCts?.Dispose();
            indexCts = null;
            Interlocked.Increment(ref disposeGeneration);

            // Clear reflection caches
            cachedAddGeneralInfo = null;
            cachedAddDropsInfo = null;
            cachedAddObtainedThroughInfo = null;
            cachedAddFoundInInfo = null;
            cachedAddAlloyForInfo = null;
            cachedAddAlloyedFromInfo = null;
            cachedAddProcessesIntoInfo = null;
            cachedAddIngredientForInfo = null;
            cachedAddCreatedByInfo = null;
            cachedAddExtraSections = null;
            cachedAddStorableInfo = null;
            cachedAddStoredInInfo = null;
            cachedAddHeading = null;
            cachedCollObjField = null;
            cachedGetCollectibleInterface = null;
            cachedOnHandbookPageComposed = null;
            cachedBehaviorType = null;
            cachedCustomHandbookContentType = null;
            cachedTinyPaddingField = null;
            cachedTinyIndentField = null;
            cachedMediumPaddingField = null;

            // Clear storage reflection cache
            storageReflectionInitialized = false;
            cachedBlockShelfType = null;
            cachedBlockToolRackType = null;
            cachedBlockMoldRackType = null;
            cachedBlockBookshelfType = null;
            cachedBlockScrollRackType = null;
            cachedBlockDisplayCaseType = null;
            cachedBlockAntlerMountType = null;
            cachedBlockOmokTableType = null;
            cachedBlockAnimalTrapType = null;
            cachedBlockCrockType = null;
            cachedLiquidInterfaceType = null;
            cachedBlockLiquidContainerBaseType = null;
            cachedShelfLayoutMethod = null;
            cachedDisplayCaseHeightField = null;
            cachedIsAppetizingBaitMethod = null;
            cachedCanFitBaitMethod = null;
            cachedGetCollectibleInterfaceMethod = null;
            cachedGetCurrentLitresMethod = null;
            cachedGetContainablePropsMethod = null;
        }

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
                                var harvestedStacks = harvestedStacksField?.GetValue(harvestable) as BlockDropItemStack[];

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

        private static string GetStackKey(IWorldAccessor world, ItemStack stack)
        {
            if (stack == null) return "null";

            int hash = stack.GetHashCode(GlobalConstants.IgnoredStackAttributes);
            if (stackKeyCache.TryGetValue(stack, out var cachedEntry) && cachedEntry.Hash == hash && cachedEntry.Key != null)
            {
                return cachedEntry.Key;
            }

            string key;
            if (stack.Attributes != null && stack.Attributes.Count > 0)
            {
                ITreeAttribute tree = stack.Attributes.Clone();
                foreach (var val in GlobalConstants.IgnoredStackAttributes) tree.RemoveAttribute(val);

                var sortedtree = tree.SortedCopy(true);
                if (tree.Count != 0)
                {
                    string treeStr = TreeAttribute.ToJsonToken(sortedtree);
                    key = (stack.Class.Name()) + "-" + stack.Collectible.Code.ToShortString() + "-" + treeStr;
                    var entry = stackKeyCache.GetOrCreateValue(stack);
                    entry.Hash = hash;
                    entry.Key = key;
                    return key;
                }
            }

            key = (stack.Class.Name()) + "-" + stack.Collectible.Code.ToShortString();
            var finalEntry = stackKeyCache.GetOrCreateValue(stack);
            finalEntry.Hash = hash;
            finalEntry.Key = key;
            return key;
        }

        /// <summary>
        /// Build allStacks order cache once during indexing for efficient sorting
        /// </summary>
        private static void BuildAllStacksOrderCache(IWorldAccessor world, ItemStack[] allStacks)
        {
            if (allStacks == null || allStacks.Length == 0) return;

            var orderCache = new Dictionary<string, int>(allStacks.Length);
            for (int i = 0; i < allStacks.Length; i++)
            {
                string key = GetStackKey(world, allStacks[i]);
                if (!orderCache.ContainsKey(key))
                    orderCache[key] = i;
            }

            allStacksOrderCache = orderCache;
        }

        /// <summary>
        /// Sort list by original allStacks order to match vanilla behavior exactly
        /// </summary>
        private static void SortByAllStacksOrder(ICoreClientAPI capi, List<ItemStack> list)
        {
            if (list == null || list.Count <= 1) return;
            if (allStacksOrderFrozen == null && allStacksOrderCache == null) return; // Not indexed yet

            try
            {
                // Sort by cached order (O(n log n) with O(1) lookups)
                list.Sort((a, b) =>
                {
                    string keyA = GetStackKey(capi.World, a);
                    string keyB = GetStackKey(capi.World, b);

                    int indexA = allStacksOrderFrozen != null && allStacksOrderFrozen.TryGetValue(keyA, out int idxA)
                        ? idxA
                        : allStacksOrderCache != null && allStacksOrderCache.TryGetValue(keyA, out int idxAFromMutable)
                            ? idxAFromMutable
                            : int.MaxValue;

                    int indexB = allStacksOrderFrozen != null && allStacksOrderFrozen.TryGetValue(keyB, out int idxB)
                        ? idxB
                        : allStacksOrderCache != null && allStacksOrderCache.TryGetValue(keyB, out int idxBFromMutable)
                            ? idxBFromMutable
                            : int.MaxValue;

                    return indexA.CompareTo(indexB);
                });
            }
            catch
            {
                // If sorting fails, return unsorted (better than crashing)
            }
        }

        /// <summary>
        /// Primary optimization: Get cached block drops instead of recalculating
        /// </summary>
        public static List<ItemStack> GetCachedBlockDrops(ICoreClientAPI capi, ItemStack stack)
        {
            if (!isIndexed) return null;

            string key = GetStackKey(capi.World, stack);
            if (isFrozen && frozenBlockDropsCache != null && frozenBlockDropsCache.TryGetValue(key, out var frozenResult))
            {
                return frozenResult.Length > 0 ? frozenResult.ToList() : new List<ItemStack>();
            }
            if (blockDropsCache.TryGetValue(key, out var result))
            {
                var list = result.ToList();
                // Sort by original allStacks order to match vanilla behavior
                SortByAllStacksOrder(capi, list);
                return list;
            }
            return new List<ItemStack>();
        }

        /// <summary>
        /// Primary optimization: Get cached harvest blocks instead of recalculating
        /// </summary>
        public static List<ItemStack> GetCachedHarvestBlocks(ICoreClientAPI capi, ItemStack stack)
        {
            if (!isIndexed) return null;

            string key = GetStackKey(capi.World, stack);
            if (isFrozen && frozenHarvestBlocksCache != null && frozenHarvestBlocksCache.TryGetValue(key, out var frozenResult))
            {
                return frozenResult.Length > 0 ? frozenResult.ToList() : new List<ItemStack>();
            }
            if (harvestBlocksCache.TryGetValue(key, out var result))
            {
                var list = result.ToList();
                // Sort by original allStacks order to match vanilla behavior
                SortByAllStacksOrder(capi, list);
                return list;
            }
            return new List<ItemStack>();
        }

        /// <summary>
        /// Primary optimization: Get cached creature kills instead of recalculating
        /// </summary>
        public static List<string> GetCachedKillCreatures(ICoreClientAPI capi, ItemStack stack)
        {
            if (!isIndexed) return null;

            string key = GetStackKey(capi.World, stack);
            if (isFrozen && frozenKillCreaturesCache != null && frozenKillCreaturesCache.TryGetValue(key, out var frozenResult))
            {
                return frozenResult.Length > 0 ? frozenResult.ToList() : new List<string>();
            }
            if (killCreaturesCache.TryGetValue(key, out var result))
            {
                return result.Distinct().ToList();  // Remove duplicates
            }
            return new List<string>();
        }

        /// <summary>
        /// Primary optimization: Get cached harvest creatures instead of recalculating
        /// </summary>
        public static List<string> GetCachedHarvestCreatures(ICoreClientAPI capi, ItemStack stack)
        {
            if (!isIndexed) return null;

            string key = GetStackKey(capi.World, stack);
            if (isFrozen && frozenHarvestCreaturesCache != null && frozenHarvestCreaturesCache.TryGetValue(key, out var frozenResult))
            {
                return frozenResult.Length > 0 ? frozenResult.ToList() : new List<string>();
            }
            if (harvestCreaturesCache.TryGetValue(key, out var result))
            {
                return result.Distinct().ToList();  // Remove duplicates
            }
            return new List<string>();
        }

        /// <summary>
        /// Get cached containers/fuels/molds - no longer needs to loop through all stacks
        /// </summary>
        public static void GetCachedGlobalLookups(out List<ItemStack> containers, out List<ItemStack> fuels, out List<ItemStack> molds, out List<ItemStack> anvils)
        {
            containers = isFrozen && frozenContainersCache != null
                ? frozenContainersCache.ToList()
                : containersCache.ToList();
            fuels = isFrozen && frozenFuelsCache != null
                ? frozenFuelsCache.ToList()
                : fuelsCache.ToList();
            molds = isFrozen && frozenMoldsCache != null
                ? frozenMoldsCache.ToList()
                : moldsCache.ToList();
            anvils = isFrozen && frozenAnvilsCache != null
                ? frozenAnvilsCache.ToList()
                : anvilsCache.ToList();
        }

        /// <summary>
        /// Get cached storage containers for an item (addStorableInfo)
        /// </summary>
        public static List<ItemStack> GetCachedStorableIn(ICoreClientAPI capi, ItemStack stack, out bool isGroundStorable)
        {
            if (!isIndexed)
            {
                isGroundStorable = false;
                return null;
            }

            string key = GetStackKey(capi.World, stack);
            isGroundStorable = (frozenGroundStorableCache != null && frozenGroundStorableCache.ContainsKey(key))
                || groundStorableCache.ContainsKey(key);
            
            if (isFrozen && frozenStorableInCache != null && frozenStorableInCache.TryGetValue(key, out var frozenResult))
            {
                return frozenResult.Length > 0 ? frozenResult.ToList() : new List<ItemStack>();
            }
            if (storableInCache.TryGetValue(key, out var result))
            {
                var list = result.ToList();
                // Sort by original allStacks order to match vanilla behavior
                SortByAllStacksOrder(capi, list);
                return list;
            }
            return new List<ItemStack>();
        }

        /// <summary>
        /// Get cached items that can be stored in a container (addStoredInInfo)
        /// </summary>
        public static List<ItemStack> GetCachedStoredIn(ICoreClientAPI capi, ItemStack stack)
        {
            if (!isIndexed) return null;

            string key = GetStackKey(capi.World, stack);
            if (isFrozen && frozenStoredInCache != null && frozenStoredInCache.TryGetValue(key, out var frozenResult))
            {
                return frozenResult.Length > 0 ? frozenResult.ToList() : new List<ItemStack>();
            }
            if (storedInCache.TryGetValue(key, out var result))
            {
                var list = result.ToList();
                // Sort by original allStacks order to match vanilla behavior
                SortByAllStacksOrder(capi, list);
                return list;
            }
            return new List<ItemStack>();
        }

        private static int GetCachedPadding(System.Reflection.FieldInfo fieldInfo, int fallback)
        {
            try
            {
                if (fieldInfo != null)
                    return (int)fieldInfo.GetValue(null);
            }
            catch { }
            return fallback;
        }

        private static int GetTinyPadding() => GetCachedPadding(cachedTinyPaddingField, 2);
        private static int GetTinyIndent() => GetCachedPadding(cachedTinyIndentField, 2);
        private static int GetMediumPadding() => GetCachedPadding(cachedMediumPaddingField, 14);

        private static void FreezeIndexCaches(ICoreClientAPI capi)
        {
            try
            {
                if (isFrozen)
                    return;

                var blockDropsBuilder = new Dictionary<string, ItemStack[]>(StringComparer.Ordinal);
                foreach (var entry in blockDropsCache)
                {
                    blockDropsBuilder[entry.Key] = FinalizeItemStackCollection(entry.Value, capi, true);
                }
                frozenBlockDropsCache = blockDropsBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                var harvestBlocksBuilder = new Dictionary<string, ItemStack[]>(StringComparer.Ordinal);
                foreach (var entry in harvestBlocksCache)
                {
                    harvestBlocksBuilder[entry.Key] = FinalizeItemStackCollection(entry.Value, capi, true);
                }
                frozenHarvestBlocksCache = harvestBlocksBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                var killCreaturesBuilder = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var entry in killCreaturesCache)
                {
                    var values = entry.Value.ToArray();
                    if (values.Length > 0)
                    {
                        Array.Sort(values);
                        values = DistinctInPlace(values);
                    }
                    killCreaturesBuilder[entry.Key] = values;
                }
                frozenKillCreaturesCache = killCreaturesBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                var harvestCreaturesBuilder = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var entry in harvestCreaturesCache)
                {
                    var values = entry.Value.ToArray();
                    if (values.Length > 0)
                    {
                        Array.Sort(values);
                        values = DistinctInPlace(values);
                    }
                    harvestCreaturesBuilder[entry.Key] = values;
                }
                frozenHarvestCreaturesCache = harvestCreaturesBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                var storableBuilder = new Dictionary<string, ItemStack[]>(StringComparer.Ordinal);
                foreach (var entry in storableInCache)
                {
                    storableBuilder[entry.Key] = FinalizeItemStackCollection(entry.Value, capi, true);
                }
                frozenStorableInCache = storableBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                var storedBuilder = new Dictionary<string, ItemStack[]>(StringComparer.Ordinal);
                foreach (var entry in storedInCache)
                {
                    storedBuilder[entry.Key] = FinalizeItemStackCollection(entry.Value, capi, true);
                }
                frozenStoredInCache = storedBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                var groundBuilder = new Dictionary<string, bool>(groundStorableCache.Count, StringComparer.Ordinal);
                foreach (var entry in groundStorableCache)
                {
                    groundBuilder[entry.Key] = entry.Value;
                }
                frozenGroundStorableCache = groundBuilder.ToFrozenDictionary(StringComparer.Ordinal);

                frozenContainersCache = RemoveNullsAndSort(capi, containersCache.ToArray());
                frozenFuelsCache = RemoveNullsAndSort(capi, fuelsCache.ToArray());
                frozenMoldsCache = RemoveNullsAndSort(capi, moldsCache.ToArray());
                frozenAnvilsCache = RemoveNullsAndSort(capi, anvilsCache.ToArray());

                if (allStacksOrderCache != null)
                {
                    allStacksOrderFrozen = allStacksOrderCache.ToFrozenDictionary(StringComparer.Ordinal);
                }

                if (ProfilingHelper.Enabled)
                {
                    ProfilingHelper.Mark("opt-handbook-freeze", $"done");
                }

                isFrozen = true;
            }
            catch (Exception ex)
            {
                capi?.Logger?.Error($"[OptiTime] Error freezing handbook cache: {ex}");
            }
        }

        private static ItemStack[] FinalizeItemStackCollection(ConcurrentQueue<ItemStack> source, ICoreClientAPI capi, bool sort)
        {
            if (source == null || source.IsEmpty)
            {
                return Array.Empty<ItemStack>();
            }

            var list = new List<ItemStack>(source.Count);
            foreach (var stack in source)
            {
                if (stack != null)
                {
                    list.Add(stack);
                }
            }

            list = UniqueStacksByKey(capi, list);
            if (sort && list.Count > 1)
            {
                SortByAllStacksOrder(capi, list);
            }

            return list.ToArray();
        }

        private static string[] DistinctInPlace(string[] values)
        {
            if (values == null || values.Length <= 1) return values ?? Array.Empty<string>();

            Array.Sort(values, StringComparer.Ordinal);

            int write = 0;
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                if (value == null) continue;
                if (write == 0 || !string.Equals(value, values[write - 1], StringComparison.Ordinal))
                    values[write++] = value;
            }

            if (write == values.Length)
            {
                return values;
            }

            var deduped = new string[write];
            Array.Copy(values, 0, deduped, 0, write);
            return deduped;
        }

        private static ItemStack[] RemoveNullsAndSort(ICoreClientAPI capi, ItemStack[] stacks)
        {
            if (stacks == null || stacks.Length == 0)
            {
                return Array.Empty<ItemStack>();
            }

            var list = new List<ItemStack>(stacks.Length);
            foreach (var stack in stacks)
            {
                if (stack != null)
                {
                    list.Add(stack);
                }
            }

            list = UniqueStacksByKey(capi, list);

            if (list.Count > 1)
            {
                SortByAllStacksOrder(capi, list);
            }

            return list.ToArray();
        }

        private static void AddHeadingCached(List<RichTextComponentBase> components, ICoreClientAPI capi, string heading, ref bool haveText)
        {
            if (cachedAddHeading != null)
            {
                object[] args = new object[] { components, capi, heading, haveText };
                cachedAddHeading.Invoke(null, args);
                haveText = (bool)args[3];
                return;
            }

            int mediumPadding = GetMediumPadding();
            if (haveText) components.Add(new ClearFloatTextComponent(capi, mediumPadding));
            haveText = true;
            var headc = new RichTextComponent(capi, Lang.Get(heading) + "\n", CairoFont.WhiteSmallText());
            components.Add(headc);
        }

        private static List<ItemStack> UniqueStacksByKey(ICoreClientAPI capi, List<ItemStack> stacks)
        {
            if (stacks == null || stacks.Count < 2)
                return stacks ?? new List<ItemStack>();

            HashSet<string> seen = new HashSet<string>();
            List<ItemStack> unique = new List<ItemStack>(stacks.Count);
            foreach (var stack in stacks)
            {
                if (stack == null) continue;
                string key = GetStackKey(capi.World, stack);
                if (seen.Add(key))
                {
                    unique.Add(stack);
                }
            }
            return unique;
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

            // addStorableInfo - OPTIMIZED with cache
            AddStorableInfoOptimized(capi, openDetailPageFor, stack, components, marginTop);

            // addStoredInInfo - OPTIMIZED with cache
            AddStoredInInfoOptimized(capi, openDetailPageFor, stack, components, marginTop);

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

        public static void Cleanup()
        {
            ClearCache();
        }
    }
}
