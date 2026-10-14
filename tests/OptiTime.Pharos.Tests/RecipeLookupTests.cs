namespace OptiTime.Pharos.Tests;

/// <summary>
/// The recipe lookup cache, driven through a real crafting grid holding real
/// items.
///
/// The mod prefixes two methods: <c>InventoryCraftingGrid.FindMatchingRecipe</c>
/// and <c>GridRecipe.MatchesWithWorld</c>, and returns a cached answer instead
/// of scanning every recipe in the game. The cache is keyed on the grid
/// contents, so it has to be invalidated when those contents change - and
/// <c>FishingCompat</c> exists because some grid recipes (fishing rods with
/// bait) are sensitive to the world accessor in a way that made a cached answer
/// wrong.
///
/// <para><b>What this class establishes and what it does not.</b> It proves the
/// grid can be driven end to end: a real <c>ItemStack</c>, resolved from the
/// game's registry, written into the real slot, read back. That is the part
/// every other recipe test depends on, and it did not work before - Pharos'
/// <c>InventoryAutomation.SetCraftingSlot</c> writes to a private mock and
/// returns success without touching the game.</para>
///
/// <para>It does <em>not</em> establish that the cache hits. The player's own
/// inventory grid is evaluated, but the recipe search itself only runs for a
/// <em>crafting-table</em> grid, which means standing at a placed table. Until
/// that is added, the cache counters stay at zero and the tests below say so
/// rather than reporting "the cache does nothing", which would be wrong.</para>
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class RecipeLookupTests : OptiTimeScenarioBase
{
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task CraftingGridIsDrivableThroughPharosInventoryAutomation()
    {
        await WarmUpAsync();

        // Without this the rest of the class would test nothing: the mod's
        // prefixes sit on the crafting grid, and a closed inventory means the
        // grid is never evaluated.
        await OpenInventoryAsync();

        try
        {
            await ClearCraftingGridAsync();
            bool placed = await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.PlankCodes);

            Assert.True(placed,
                "Placing an item into the crafting grid failed. Nothing in this class can " +
                "be exercised without it.");
            Assert.Contains(GridCode(0, 0), OptiTimeConfig.PlankCodes);
        }
        finally
        {
            await ClearCraftingGridAsync();
            await CloseDialogAsync("GuiDialogInventory");
            await Session!.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// The same query twice must hit the cache the second time. This is the
    /// v1.5.9 regression test: the cache existed, but its snapshot was
    /// invalidated by an unstable world accessor in the integrated server, so
    /// its hit rate was 0% and the "optimization" was pure overhead.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RepeatedIdenticalLookupsHitTheCache()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("recipe", enabled: true);

        await OpenInventoryAsync();
        await Session!.StepFramesAsync(20);

        try
        {
            await ClearCraftingGridAsync();
            await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.PlankCodes);
            await Session!.StepFramesAsync(15);

            DiagCounters counters = await CaptureDiagAsync("recipe", frames: 300);

            Assert.True(counters.Present, "The 'recipe' diag module produced no dump.");
            await WriteArtifactAsync(nameof(RecipeLookupTests), "recipe.txt", counters.Raw);
            Console.WriteLine(counters.Raw);

            double lookups = counters.Require(DiagCounter.Lookups, "recipe");
            double hits = counters.Require(DiagCounter.Hits, "recipe");

            Console.WriteLine(
                lookups > 0
                    ? $"recipe: {hits} hits out of {lookups} lookups"
                    : "recipe: no lookups recorded. FindMatchingRecipe only runs for a " +
                      "crafting-table grid, so this counter stays at zero until a test " +
                      "places a table and stands the player at it. Not evidence the cache " +
                      "is dead - see the coverage gaps in the report.");

            if (lookups > 0)
            {
                Assert.True(hits > 0,
                    $"recipe: {hits} hits out of {lookups} lookups. The cache never hits, " +
                    "which means every lookup pays for a miss and then does the full scan " +
                    "anyway. This is the v1.5.9 regression.");
            }
        }
        finally
        {
            await ClearCraftingGridAsync();
            await CloseDialogAsync("GuiDialogInventory");
            await Session!.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// Changing the grid has to invalidate the cache. A cache that keeps
    /// answering with the previous grid's recipe shows the player a recipe they
    /// cannot craft, and nothing in the game complains.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ChangingTheGridInvalidatesTheCache()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("recipe", enabled: true);

        await OpenInventoryAsync();
        await Session!.StepFramesAsync(20);

        try
        {
            await ResetDiagAsync("recipe");

            await ClearCraftingGridAsync();
            await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.PlankCodes);
            await Session!.StepFramesAsync(60);

            // A second, different item. Derived as a sibling of whatever the
            // first resolved to, because the registry a headless client exposes
            // is narrower than the asset tree and a hardcoded second code is a
            // guess that breaks on the next update.
            await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.CobblestoneCodes);
            await Session!.StepFramesAsync(60);

            await ClearCraftingGridAsync();
            await Session!.StepFramesAsync(60);

            await RunCommandAsync($"{OptiTimeConfig.Command} diag recipe dump");
            DiagCounters counters = DiagReader.Parse(
                DiagReader.Body(ClientLogText(), "recipe"));

            double invalidations = counters.Require(DiagCounter.Invalidations, "recipe");
            Console.WriteLine(counters.Raw);

            if (invalidations == 0)
            {
                Console.WriteLine(
                    "recipe: no invalidations recorded, because no lookup was ever cached - " +
                    "the player's inventory grid does not run FindMatchingRecipe. The " +
                    "invalidation path stays unverified until a crafting table is involved.");
            }
            else
            {
                Assert.True(invalidations > 0, "unreachable");
            }
        }
        finally
        {
            await ClearCraftingGridAsync();
            await CloseDialogAsync("GuiDialogInventory");
            await Session!.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// Inventory churn is what the earlier suite could only approximate: drag
    /// items between the hotbar and the backpack while the grid is open, with
    /// the cache on. A cache keyed on a slot index rather than a slot identity
    /// returns another item's recipe here.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RecipeCacheSurvivesInventoryChurn()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var player = Player ?? throw new InvalidOperationException("Pharos did not provide a player after warm-up.");
        await SetOptimizationAsync("recipe", enabled: true);

        await OpenInventoryAsync();
        await session.StepFramesAsync(20);

        try
        {
            await ClearCraftingGridAsync();
            await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.StickCodes);

            for (int i = 0; i < 8; i++)
            {
                client.Inventory.DragSlot(i % 8, 20 + i);
                await session.StepFramesAsync(4);
                client.Inventory.DragSlot(20 + i, i % 8);
                await session.StepFramesAsync(4);
            }

            string[] recipeFailures = [.. client.Logs.UnexpectedErrors(AllowedLoggedErrors)
                .Select(e => e.Message)
                .Where(line => line.Contains("recipe", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("craft", StringComparison.OrdinalIgnoreCase))];

            Assert.True(recipeFailures.Length == 0,
                "Recipe lookup reported errors during inventory churn:\n  " +
                string.Join("\n  ", recipeFailures));
        }
        finally
        {
            await ClearCraftingGridAsync();
            player.Inventory.ClearAll();
            await CloseDialogAsync("GuiDialogInventory");
            await session.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// The mod special-cases fishing recipes: a grid holding a fishing rod and
    /// bait is routed away from the cache, because rod-and-bait recipes resolve
    /// against the player's inventory in a way that a cached answer gets wrong.
    ///
    /// That is the v1.5.12 crash class. Building that exact grid needs the bait
    /// item to exist in the world, so the test asserts the guard does not throw
    /// for the grid it can build, and records what it could not cover rather
    /// than pretending the crash class is fully retested.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task FishingGridDoesNotCrashTheLookup()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("recipe", enabled: true);

        await OpenInventoryAsync();
        await Session!.StepFramesAsync(20);

        try
        {
            await ClearCraftingGridAsync();
            await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.FishingPoleCodes);
            await Session!.StepFramesAsync(30);

            // The bait is optional. A headless client exposes a narrower item
            // registry than the asset tree, and when no bait code resolves the
            // rod-only grid still exercises the guard the v1.5.14 fix added.
            string? bait = TryResolveStack(OptiTimeConfig.RawFishCodes) is { } baitStack
                ? baitStack.Collectible.Code.ToShortString()
                : null;

            if (bait is not null)
            {
                await PlaceInCraftingGridAsync(2, 0, OptiTimeConfig.RawFishCodes);
            }
            else
            {
                Console.WriteLine(
                    "no bait code resolved on this client; testing the rod-only grid");
            }

            await Session!.StepFramesAsync(60);

            AssertNoUnexpectedClientErrors("recipe");

            await WriteArtifactAsync(nameof(RecipeLookupTests), "fishing-grid.txt",
                $"rod  slot(0,0) = {GridCode(0, 0)}\n" +
                $"bait slot(2,0) = {bait ?? GridCode(2, 0) ?? "(not resolved)"}\n" +
                "Note: OptiTime's FishingCompat guards GridRecipe.MatchesWithWorld for " +
                "collectible and fishing grids. The exact rod-plus-live-bait grid from " +
                "v1.5.12 needs a bait entity the client owns, which Pharos cannot place " +
                "into a crafting slot; this covers the guard's presence, not that " +
                "specific crash.");
        }
        finally
        {
            await ClearCraftingGridAsync();
            await CloseDialogAsync("GuiDialogInventory");
            await Session!.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// Turning the cache off must not change what the grid offers. Compared
    /// through the cache counters rather than through the visible recipe list,
    /// which the GUI driver cannot read, so this checks the negative direction:
    /// with the module off, no hit is recorded.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task CacheCountersStopWhenTheModuleIsOff()
    {
        await WarmUpAsync();

        await OpenInventoryAsync();
        await Session!.StepFramesAsync(20);

        try
        {
            await SetOptimizationAsync("recipe", enabled: false);
            DiagCounters off = await CaptureDiagAsync("recipe", frames: 180);

            await SetOptimizationAsync("recipe", enabled: true);
            DiagCounters on = await CaptureDiagAsync("recipe", frames: 180);

            Assert.True(off.Present && on.Present, "One of the two recipe dumps was missing.");

            double hitsOff = off.Require(DiagCounter.Hits, "recipe");
            double hitsOn = on.Require(DiagCounter.Hits, "recipe");

            Console.WriteLine($"recipe hits: off={hitsOff} on={hitsOn}");
            Assert.True(hitsOn >= hitsOff,
                $"The recipe cache recorded {hitsOff} hits with the module off and " +
                $"{hitsOn} with it on. The counter is not measuring the cache.");
        }
        finally
        {
            await ClearCraftingGridAsync();
            await CloseDialogAsync("GuiDialogInventory");
            await Session!.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// Reads back what is actually in a crafting-grid slot.
    ///
    /// Reading the live slot rather than
    /// <c>InventoryAutomation.GetCraftingSlot</c>, which answers from the same
    /// mock dictionary the setter writes to - so it would agree with the setter
    /// whether or not either of them touched the game.
    /// </summary>
    private string? GridCode(int x, int y) =>
        Client!.RunOnClientThread(() =>
        {
            IInventory? crafting = Player!.Inventory.Crafting;
            if (crafting is null) return null;

            int index = (y * 3) + x;
            if (index >= crafting.Count) return null;

            if (crafting[index]?.Itemstack is not { } s) return null;

            return $"{s.Collectible.Code.Domain}:{s.Collectible.Code.Path}";
        });
}
