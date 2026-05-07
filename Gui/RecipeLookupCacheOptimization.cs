using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace OptiTime
{
    public static class RecipeLookupCacheOptimization
    {
        private const int MaxPositiveCache = 512;
        private const int MaxGridCache = 512;
        private const int MaxItemCache = 1024;
        private static readonly string[] NoIgnoredStackAttributes = Array.Empty<string>();

        private sealed class RecipeMetadata
        {
            public bool HasResolvedIngredients;
            public bool IsShapeless;
            public int ExactRequiredSlots;
            public int MinRequiredSlots;
        }

        private sealed class CandidateEntry
        {
            public GridRecipe Recipe;
            public int RecipeOrder;
        }

        private sealed class FallbackEntry
        {
            public CraftingRecipeIngredient Ingredient;
            public CandidateEntry Candidate;
        }

        // Snapshot fields are frozen after BuildSnapshot completes. The Snapshot reference
        // itself is published atomically via the static `snapshot` field under snapshotLock;
        // once published the frozen dictionaries are immutable and lock-free for readers.
        private sealed class Snapshot
        {
            public IWorldAccessor World;
            public int RecipeCount;
            public int Generation;
            public FrozenDictionary<GridRecipe, int> RecipeOrders;
            public FrozenDictionary<DirectIngredientKey, CandidateEntry[]> Direct;
            public FrozenDictionary<EnumItemClass, FallbackEntry[]> Fallback;
        }

        private sealed class SearchState
        {
            public GridRecipe PreviousRecipe;
        }

        private readonly struct DirectIngredientKey : IEquatable<DirectIngredientKey>
        {
            public readonly EnumItemClass Type;
            public readonly string Code;

            public DirectIngredientKey(EnumItemClass type, string code)
            {
                Type = type;
                Code = code ?? string.Empty;
            }

            public bool Equals(DirectIngredientKey other)
            {
                return Type == other.Type && string.Equals(Code, other.Code, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is DirectIngredientKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return ((int)Type * 397) ^ StringComparer.Ordinal.GetHashCode(Code);
            }
        }

        private static readonly ConcurrentDictionary<GridRecipe, RecipeMetadata> recipeMetadataCache = new();
        private static readonly ConcurrentDictionary<string, GridRecipe> positiveMatchCache = new();
        private static readonly ConcurrentQueue<string> positiveMatchOrder = new();
        private static readonly ConcurrentDictionary<string, GridRecipe[]> gridCandidateCache = new();
        private static readonly ConcurrentQueue<string> gridCandidateOrder = new();
        private static readonly ConcurrentDictionary<string, GridRecipe[]> itemCandidateCache = new();
        private static readonly ConcurrentQueue<string> itemCandidateOrder = new();
        private static readonly Lock snapshotLock = new();
        private static Snapshot snapshot;
        private static int generation;
        private static ConditionalWeakTable<InventoryCraftingGrid, SearchState> searchStates = new();

        private static readonly MethodInfo matchesWithWorld = AccessTools.Method(typeof(GridRecipe), "Matches", new[] { typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int) });
        private static readonly MethodInfo matchesShapeLess = AccessTools.Method(typeof(GridRecipe), "MatchesShapeLess", new[] { typeof(ItemSlot[]), typeof(IWorldAccessor), typeof(IRecipeIngredient[]) });
        private static readonly MethodInfo matchesAtPosition = AccessTools.Method(typeof(GridRecipe), "MatchesAtPosition", new[] { typeof(int), typeof(int), typeof(ItemSlot[]), typeof(int), typeof(int), typeof(IRecipeIngredient[]) });
        private static readonly PropertyInfo resolvedIngredientsProperty = AccessTools.Property(typeof(GridRecipe), "ResolvedIngredients");

        // .NET 8+ MethodInvoker: zero-alloc invocation for up to 4 args; for >4 args the
        // Span<object?> overload still amortizes dispatch cost vs MethodInfo.Invoke.
        // Created lazily to handle the case where AccessTools.Method returns null.
        private static readonly System.Reflection.MethodInvoker matchesWithWorldInvoker = matchesWithWorld != null ? System.Reflection.MethodInvoker.Create(matchesWithWorld) : null;
        private static readonly System.Reflection.MethodInvoker matchesShapeLessInvoker = matchesShapeLess != null ? System.Reflection.MethodInvoker.Create(matchesShapeLess) : null;
        private static readonly System.Reflection.MethodInvoker matchesAtPositionInvoker = matchesAtPosition != null ? System.Reflection.MethodInvoker.Create(matchesAtPosition) : null;

        public static bool MatchesWithWorld_Prefix(GridRecipe __instance, IPlayer forPlayer, IWorldAccessor world, ItemSlot[] ingredients, int gridWidth, ref bool __result)
        {
            return MatchesPrefixCore(__instance, forPlayer, world, ingredients, gridWidth, ref __result);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryCraftingGrid), "FindMatchingRecipe")]
        public static bool FindMatchingRecipe_Prefix(InventoryCraftingGrid __instance)
        {
            if (__instance?.Api?.World == null)
            {
                return true;
            }

            try
            {
                int slotCount = __instance.Count - 1;
                int gridWidth = (int)Math.Sqrt(slotCount);
                if (slotCount <= 0 || gridWidth * gridWidth != slotCount)
                {
                    return true;
                }

                ItemSlot[] slots = new ItemSlot[slotCount];
                for (int i = 0; i < slotCount; i++)
                {
                    slots[i] = __instance[i];
                }

                ItemSlot outputSlot = __instance[slotCount];
                IPlayer player = __instance.Player;
                IWorldAccessor world = __instance.Api.World;
                Snapshot snap = GetSnapshot(world);
                if (outputSlot == null || snap == null)
                {
                    return true;
                }

                __instance.MatchingRecipe = null;
                outputSlot.Itemstack = null;

                SearchState state = searchStates.GetValue(__instance, _ => new SearchState());
                string positiveKey = PositiveKey(snap.Generation, player?.PlayerUID, slots);

                if (state.PreviousRecipe != null && state.PreviousRecipe.Enabled && InvokeRecipeMatches(state.PreviousRecipe, player, world, slots, gridWidth))
                {
                    ApplyMatch(__instance, state.PreviousRecipe, slots, outputSlot, slotCount);
                    StoreBounded(positiveMatchCache, positiveMatchOrder, positiveKey, state.PreviousRecipe, MaxPositiveCache);
                    return false;
                }

                state.PreviousRecipe = null;

                if (positiveMatchCache.TryGetValue(positiveKey, out GridRecipe cached) && cached.Enabled && InvokeRecipeMatches(cached, player, world, slots, gridWidth))
                {
                    state.PreviousRecipe = cached;
                    ApplyMatch(__instance, cached, slots, outputSlot, slotCount);
                    return false;
                }

                positiveMatchCache.TryRemove(positiveKey, out _);
                GridRecipe[] candidates = GetGridCandidates(snap, slots);

                for (int pass = 0; pass < 2; pass++)
                {
                    bool shapeless = pass == 1;
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        GridRecipe recipe = candidates[i];
                        if (recipe.Shapeless != shapeless)
                        {
                            continue;
                        }

                        if (!InvokeRecipeMatches(recipe, player, world, slots, gridWidth))
                        {
                            continue;
                        }

                        state.PreviousRecipe = recipe;
                        ApplyMatch(__instance, recipe, slots, outputSlot, slotCount);
                        StoreBounded(positiveMatchCache, positiveMatchOrder, positiveKey, recipe, MaxPositiveCache);
                        return false;
                    }
                }

                __instance.dirtySlots.Add(slotCount);
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool MatchesPrefixCore(GridRecipe recipe, IPlayer player, IWorldAccessor world, ItemSlot[] ingredients, int gridWidth, ref bool result)
        {
            if (recipe == null || player?.Entity?.Api == null)
            {
                return true;
            }

            try
            {
                if (!player.Entity.Api.Event.TriggerMatchesRecipe(player, (IRecipeBase)recipe, ingredients))
                {
                    result = false;
                    return false;
                }

                if (!player.Entity.Api.Event.TriggerMatchesRecipe(player, recipe, ingredients, gridWidth))
                {
                    result = false;
                    return false;
                }

                if (ingredients == null || ingredients.Length == 0 || gridWidth <= 0)
                {
                    result = false;
                    return false;
                }

                int gridHeight = ingredients.Length / gridWidth;
                if (gridWidth < recipe.Width || gridHeight < recipe.Height)
                {
                    result = false;
                    return false;
                }

                RecipeMetadata metadata = GetRecipeMetadata(recipe);
                if (metadata?.HasResolvedIngredients == true)
                {
                    int nonEmptySlots = CountNonEmpty(ingredients);
                    bool fastReject = metadata.IsShapeless ? nonEmptySlots < metadata.MinRequiredSlots : nonEmptySlots != metadata.ExactRequiredSlots;
                    if (fastReject)
                    {
                        result = false;
                        return false;
                    }
                }

                if (!TryEvaluate(recipe, world ?? player.Entity.Api.World, ingredients, gridWidth, out bool matched))
                {
                    return true;
                }

                result = matched;
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool TryEvaluate(GridRecipe recipe, IWorldAccessor world, ItemSlot[] ingredients, int gridWidth, out bool matched)
        {
            matched = false;
            var resolved = recipe.ResolvedIngredients;
            if (resolved == null) return false;

            if (recipe.Shapeless)
            {
                if (matchesShapeLessInvoker == null) return false;
                matched = (bool)matchesShapeLessInvoker.Invoke(recipe, ingredients, world, resolved);
                return true;
            }

            if (matchesAtPositionInvoker == null) return false;

            int gridHeight = ingredients.Length / gridWidth;
            // 6-arg MethodInvoker overload requires Span<object?>; reference-type stackalloc
            // isn't permitted, so allocate one heap object[6] outside the loop and reuse the
            // slots that don't change between iterations.
            object[] args = new object[6];
            args[2] = ingredients;
            args[3] = gridWidth;
            args[4] = recipe.Width;
            args[5] = resolved;
            for (int col = 0; col <= gridWidth - recipe.Width; col++)
            {
                for (int row = 0; row <= gridHeight - recipe.Height; row++)
                {
                    args[0] = col;
                    args[1] = row;
                    if ((bool)matchesAtPositionInvoker.Invoke(recipe, args))
                    {
                        matched = true;
                        return true;
                    }
                }
            }

            return true;
        }

        private static Snapshot GetSnapshot(IWorldAccessor world)
        {
            if (world?.GridRecipes == null)
            {
                return null;
            }

            if (snapshot != null && ReferenceEquals(snapshot.World, world) && snapshot.RecipeCount == world.GridRecipes.Count)
            {
                return snapshot;
            }

            lock (snapshotLock)
            {
                if (snapshot != null && ReferenceEquals(snapshot.World, world) && snapshot.RecipeCount == world.GridRecipes.Count)
                {
                    return snapshot;
                }

                snapshot = BuildSnapshot(world);
                ResetCaches();
                return snapshot;
            }
        }

        private static Snapshot BuildSnapshot(IWorldAccessor world)
        {
            int recipeCount = world.GridRecipes.Count;
            var recipeOrders = new Dictionary<GridRecipe, int>(recipeCount);
            var directBuilder = new Dictionary<DirectIngredientKey, List<CandidateEntry>>();
            var fallbackBuilder = new Dictionary<EnumItemClass, List<FallbackEntry>>();

            for (int recipeOrder = 0; recipeOrder < recipeCount; recipeOrder++)
            {
                GridRecipe recipe = world.GridRecipes[recipeOrder];
                if (recipe == null || !recipe.Enabled)
                {
                    continue;
                }

                recipeOrders[recipe] = recipeOrder;

                CraftingRecipeIngredient[] resolved = GetResolvedIngredients(recipe);
                if (resolved == null || resolved.Length == 0)
                {
                    continue;
                }

                for (int i = 0; i < resolved.Length; i++)
                {
                    if (resolved[i] is not CraftingRecipeIngredient ingredient)
                    {
                        continue;
                    }

                    CandidateEntry candidate = new() { Recipe = recipe, RecipeOrder = recipeOrder };
                    if (TryGetDirectKey(ingredient, out DirectIngredientKey directKey))
                    {
                        if (!directBuilder.TryGetValue(directKey, out List<CandidateEntry> directList))
                        {
                            directBuilder[directKey] = directList = new List<CandidateEntry>();
                        }

                        directList.Add(candidate);
                    }
                    else
                    {
                        if (!fallbackBuilder.TryGetValue(ingredient.Type, out List<FallbackEntry> fallbackList))
                        {
                            fallbackBuilder[ingredient.Type] = fallbackList = new List<FallbackEntry>();
                        }

                        fallbackList.Add(new FallbackEntry { Ingredient = ingredient, Candidate = candidate });
                    }
                }
            }

            var directFrozen = new Dictionary<DirectIngredientKey, CandidateEntry[]>(directBuilder.Count);
            foreach (var kvp in directBuilder)
            {
                directFrozen[kvp.Key] = kvp.Value.ToArray();
            }
            var fallbackFrozen = new Dictionary<EnumItemClass, FallbackEntry[]>(fallbackBuilder.Count);
            foreach (var kvp in fallbackBuilder)
            {
                fallbackFrozen[kvp.Key] = kvp.Value.ToArray();
            }

            return new Snapshot
            {
                World = world,
                RecipeCount = recipeCount,
                Generation = ++generation,
                RecipeOrders = recipeOrders.ToFrozenDictionary(),
                Direct = directFrozen.ToFrozenDictionary(),
                Fallback = fallbackFrozen.ToFrozenDictionary(),
            };
        }

        private static GridRecipe[] GetGridCandidates(Snapshot snap, ItemSlot[] slots)
        {
            string key = snap.Generation + "|" + GridStateKey(slots);
            if (gridCandidateCache.TryGetValue(key, out GridRecipe[] cached))
            {
                return cached;
            }

            List<CandidateEntry> candidates = new();
            for (int i = 0; i < slots.Length; i++)
            {
                ItemStack stack = slots[i]?.Itemstack;
                if (stack == null || stack.StackSize == 0)
                {
                    continue;
                }

                GridRecipe[] perItem = GetItemCandidates(snap, stack);
                for (int j = 0; j < perItem.Length; j++)
                {
                    candidates.Add(new CandidateEntry { Recipe = perItem[j], RecipeOrder = snap.RecipeOrders[perItem[j]] });
                }
            }

            candidates.Sort(static (a, b) => a.RecipeOrder.CompareTo(b.RecipeOrder));
            List<GridRecipe> ordered = new(candidates.Count);
            HashSet<GridRecipe> seen = new();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (seen.Add(candidates[i].Recipe))
                {
                    ordered.Add(candidates[i].Recipe);
                }
            }

            GridRecipe[] computed = ordered.ToArray();
            StoreBounded(gridCandidateCache, gridCandidateOrder, key, computed, MaxGridCache);
            return computed;
        }

        private static GridRecipe[] GetItemCandidates(Snapshot snap, ItemStack stack)
        {
            string key = ItemKey(snap.Generation, stack);
            if (itemCandidateCache.TryGetValue(key, out GridRecipe[] cached))
            {
                return cached;
            }

            List<CandidateEntry> candidates = new();
            DirectIngredientKey directKey = new(stack.Class, stack.Collectible?.Code?.ToShortString() ?? string.Empty);
            if (snap.Direct.TryGetValue(directKey, out CandidateEntry[] direct))
            {
                candidates.AddRange(direct);
            }

            if (snap.Fallback.TryGetValue(stack.Class, out FallbackEntry[] fallback))
            {
                for (int i = 0; i < fallback.Length; i++)
                {
                    if (fallback[i].Ingredient.SatisfiesAsIngredient(stack, false))
                    {
                        candidates.Add(fallback[i].Candidate);
                    }
                }
            }

            candidates.Sort(static (a, b) => a.RecipeOrder.CompareTo(b.RecipeOrder));
            List<GridRecipe> ordered = new(candidates.Count);
            HashSet<GridRecipe> seen = new();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (seen.Add(candidates[i].Recipe))
                {
                    ordered.Add(candidates[i].Recipe);
                }
            }

            GridRecipe[] computed = ordered.ToArray();
            StoreBounded(itemCandidateCache, itemCandidateOrder, key, computed, MaxItemCache);
            return computed;
        }

        private static bool TryGetDirectKey(CraftingRecipeIngredient ingredient, out DirectIngredientKey key)
        {
            key = default;
            if (ingredient == null || ingredient.MatchingType != EnumRecipeMatchType.Exact || ingredient.Code == null)
            {
                return false;
            }

            ItemStack resolvedStack = ingredient.ResolvedItemStack;
            if (resolvedStack == null)
            {
                return false;
            }

            if (resolvedStack.Attributes != null && resolvedStack.Attributes.Count > 0)
            {
                return false;
            }

            string code = resolvedStack.Collectible?.Code?.ToShortString();
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            key = new DirectIngredientKey(ingredient.Type, code);
            return true;
        }

        private static RecipeMetadata GetRecipeMetadata(GridRecipe recipe)
        {
            if (recipe == null)
            {
                return null;
            }

            if (recipeMetadataCache.TryGetValue(recipe, out RecipeMetadata cached))
            {
                return cached;
            }

            RecipeMetadata built = BuildRecipeMetadata(recipe);
            recipeMetadataCache.TryAdd(recipe, built);
            return built;
        }

        private static RecipeMetadata BuildRecipeMetadata(GridRecipe recipe)
        {
            CraftingRecipeIngredient[] resolved = GetResolvedIngredients(recipe);
            RecipeMetadata meta = new() { IsShapeless = recipe.Shapeless, HasResolvedIngredients = resolved != null && resolved.Length > 0 };
            if (!meta.HasResolvedIngredients)
            {
                return meta;
            }

            if (!recipe.Shapeless)
            {
                for (int i = 0; i < resolved.Length; i++)
                {
                    if (resolved[i] != null)
                    {
                        meta.ExactRequiredSlots++;
                    }
                }

                meta.MinRequiredSlots = meta.ExactRequiredSlots;
                return meta;
            }

            HashSet<int> seen = new();
            for (int i = 0; i < resolved.Length; i++)
            {
                if (resolved[i] is not CraftingRecipeIngredient ingredient)
                {
                    continue;
                }

                if (ingredient.MatchingType != EnumRecipeMatchType.Exact || ingredient.IsTool)
                {
                    meta.MinRequiredSlots++;
                    continue;
                }

                if (ingredient.RecipeAttributes == null && ingredient.ResolvedItemStack != null)
                {
                    int sig = (((int)ingredient.Type * 397) ^ ingredient.Quantity) ^ ingredient.ResolvedItemStack.GetHashCode(NoIgnoredStackAttributes);
                    if (seen.Add(sig))
                    {
                        meta.MinRequiredSlots++;
                    }

                    continue;
                }

                meta.MinRequiredSlots++;
            }

            meta.ExactRequiredSlots = meta.MinRequiredSlots;
            return meta;
        }

        private static CraftingRecipeIngredient[] GetResolvedIngredients(GridRecipe recipe)
        {
            return recipe.ResolvedIngredients;
        }

        private static bool InvokeRecipeMatches(GridRecipe recipe, IPlayer player, IWorldAccessor world, ItemSlot[] slots, int gridWidth)
        {
            if (matchesWithWorldInvoker == null) return false;
            // 4-arg MethodInvoker overload: zero-alloc dispatch.
            return (bool)matchesWithWorldInvoker.Invoke(recipe, player, world, slots, gridWidth);
        }

        private static void ApplyMatch(InventoryCraftingGrid inventory, GridRecipe recipe, ItemSlot[] slots, ItemSlot outputSlot, int outputSlotId)
        {
            inventory.MatchingRecipe = recipe;
            recipe.GenerateOutputStack(slots, outputSlot);
            inventory.dirtySlots.Add(outputSlotId);
        }

        private static int CountNonEmpty(ItemSlot[] slots)
        {
            int count = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i]?.Itemstack != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static string PositiveKey(int gen, string playerUid, ItemSlot[] slots)
        {
            return gen + "|" + (playerUid ?? string.Empty) + "|" + GridStateKey(slots);
        }

        private static string ItemKey(int gen, ItemStack stack)
        {
            StringBuilder sb = new(96);
            sb.Append(gen).Append('|');
            AppendStackKey(sb, stack);
            return sb.ToString();
        }

        private static string GridStateKey(ItemSlot[] slots)
        {
            StringBuilder sb = new(slots.Length * 24);
            for (int i = 0; i < slots.Length; i++)
            {
                sb.Append(i).Append(':');
                AppendStackKey(sb, slots[i]?.Itemstack);
                sb.Append(';');
            }

            return sb.ToString();
        }

        private static void AppendStackKey(StringBuilder sb, ItemStack stack)
        {
            if (stack == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append((int)stack.Class)
              .Append('/')
              .Append(stack.Collectible?.Code?.ToShortString() ?? string.Empty)
              .Append('/')
              .Append(stack.StackSize)
              .Append('/')
              .Append(stack.GetHashCode(NoIgnoredStackAttributes));
        }

        private static void StoreBounded<T>(ConcurrentDictionary<string, T> cache, ConcurrentQueue<string> order, string key, T value, int maxSize)
        {
            if (cache.TryAdd(key, value))
            {
                order.Enqueue(key);
            }
            else
            {
                cache[key] = value;
            }

            while (cache.Count > maxSize && order.TryDequeue(out string evicted))
            {
                cache.TryRemove(evicted, out _);
            }
        }

        private static void ResetCaches()
        {
            positiveMatchCache.Clear();
            gridCandidateCache.Clear();
            itemCandidateCache.Clear();
            while (positiveMatchOrder.TryDequeue(out _)) { }
            while (gridCandidateOrder.TryDequeue(out _)) { }
            while (itemCandidateOrder.TryDequeue(out _)) { }
            searchStates = new ConditionalWeakTable<InventoryCraftingGrid, SearchState>();
        }

        public static void Cleanup()
        {
            recipeMetadataCache.Clear();
            snapshot = null;
            ResetCaches();
        }
    }
}
