using System;
using Vintagestory.API.Common;

namespace OptiTime;

/// <summary>
/// Central detection for fishing-related content.
///
/// OptiTime intentionally does NOT optimize anything that involves fishing —
/// fishing poles, bait, or the fishing/bait grid recipes. The vanilla fishing
/// systems (ItemFishingPole, EntityBobber and the ClothManager rope) are left to
/// run completely unmodified, so no optimization can ever alter fishing mechanics
/// or rendering.
///
/// Detection is purely code/attribute based (no hard reference to gameplay-assembly
/// types), so it also covers modded poles and bait that follow the vanilla naming
/// or set the <c>isFishBait</c> attribute.
/// </summary>
internal static class FishingCompat
{
    /// <summary>True if the collectible is a fishing pole or any kind of fishing bait.</summary>
    internal static bool IsFishingCollectible(CollectibleObject collectible)
    {
        AssetLocation code = collectible?.Code;
        if (code == null)
        {
            return false;
        }

        string path = code.Path;
        if (path.StartsWith("fishingpole", StringComparison.Ordinal) ||
            path.StartsWith("fishingbait", StringComparison.Ordinal))
        {
            return true;
        }

        // Live/dead earthworms and all dedicated bait set this attribute.
        return collectible.Attributes?.IsTrue("isFishBait") == true;
    }

    /// <summary>True if any occupied input slot holds a fishing pole or bait.</summary>
    internal static bool GridInvolvesFishing(ItemSlot[] slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ItemStack stack = slots[i]?.Itemstack;
            if (stack != null && IsFishingCollectible(stack.Collectible))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if the recipe outputs, or is crafted from, a fishing pole or bait.</summary>
    internal static bool IsFishingRecipe(GridRecipe recipe)
    {
        if (recipe == null)
        {
            return false;
        }

        if (IsFishingCollectible(recipe.Output?.ResolvedItemStack?.Collectible))
        {
            return true;
        }

        CraftingRecipeIngredient[] resolved = recipe.ResolvedIngredients;
        if (resolved != null)
        {
            for (int i = 0; i < resolved.Length; i++)
            {
                if (IsFishingCollectible(resolved[i]?.ResolvedItemStack?.Collectible))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
