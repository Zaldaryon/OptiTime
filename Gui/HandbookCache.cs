using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace OptiTime
{
    /// <summary>
    /// Handbook optimization: cache state, frozen snapshots, accessors, and utility methods.
    /// </summary>
    public partial class HandbookOptimization
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


        public static void Cleanup()
        {
            ClearCache();
        }
    }
}
