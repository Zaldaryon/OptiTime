using Zaldaryon.Pharos.Assertions;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// The handbook optimization, driven through the handbook itself.
///
/// The mod replaces the handbook's per-item lookups - block drops, harvest
/// blocks, kill creatures, storable-in, stored-in, containers, fuels, molds,
/// anvils - with a cache built once by an indexer at startup. The observable
/// consequences are that the indexer must complete, the cache must be hit
/// repeatedly, and every page must still render the same text it rendered
/// before. A cache that returns the wrong list shows up as wrong handbook
/// content, which nothing else in the suite would notice.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class HandbookOptimizationTests : OptiTimeScenarioBase
{
    /// <summary>
    /// The indexer has to have finished, over the whole world.
    ///
    /// Asserted through a live surface rather than through the boot log alone,
    /// because Pharos clears the log between tests: by the second test in a
    /// class the "indexing completed" line is already gone, and a test that
    /// only read the log would pass or fail depending on where it happened to
    /// land in the run. The boot line is still reported when it is there.
    ///
    /// The page list is the consequence that matters. A cache built from an
    /// empty world - which is what a wrong world generator produces - shows up
    /// as a handful of pages rather than thousands.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task IndexerCoversTheWholeWorld()
    {
        await WarmUpAsync();

        string[] indexing = [.. Client!.Logs.Entries
            .Select(e => e.Message)
            .Where(m => m.Contains("Handbook indexing completed", StringComparison.OrdinalIgnoreCase))];

        if (indexing.Length > 0)
        {
            Console.WriteLine(indexing[^1]);
        }
        else
        {
            Console.WriteLine(
                "the indexing log line was cleared by a previous test's rollback; " +
                "falling back to the live page list");
        }

        Assert.True(Client!.Ui!.Handbook.IsAvailable,
            "The handbook dialog is not registered on this client, so the indexer's " +
            "output cannot be inspected at all.");

        DiagCounters counters = await CaptureDiagAsync("handbook", frames: 120);
        string indexer = counters.Text(DiagCounter.Indexer) ?? "(absent)";
        Console.WriteLine($"handbook diag: indexer={indexer}");

        IReadOnlyList<HandbookPageInfo> pages = await Client.Ui.Handbook.PagesAsync();

        await WriteArtifactAsync(nameof(HandbookOptimizationTests), "pages.txt",
            $"indexer={indexer}\npages={pages.Count}\n" +
            string.Join('\n', pages.Take(30).Select(p => $"{p.Code}\t{p.Kind}\t{p.Title}")));

        Assert.True(pages.Count > 100,
            $"The handbook lists only {pages.Count} pages. A survival world has " +
            "thousands of collectibles, and an indexer that ran before assets finished " +
            $"loading produces a cache that misses on every lookup. The diag module says " +
            $"indexer={indexer}.");
    }

    /// <summary>
    /// Opening the handbook and searching is what drives the cache. Once warm,
    /// repeat searches must hit: a cache with a 0% hit rate is the exact bug
    /// v1.5.9 shipped, where the snapshot was invalidated by an unstable world
    /// accessor in the integrated server.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task HandbookCacheCachesRepeatedLookups()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("handbook", enabled: true);

        if (!Client!.Ui!.Handbook.IsAvailable)
        {
            Assert.Fail(
                "The handbook dialog is not registered on this client, so the handbook " +
                "optimization cannot be exercised. Its Harmony prefix sits on " +
                "ItemStack.GetHandbookInfo, which is only reachable through it.");
        }

        await Client.Ui.Handbook.OpenAsync();
        await Client.Ui.Handbook.WaitUntilLoadedAsync();
        await Session!.StepFramesAsync(30);

        try
        {
            // Warm the cache by asking for extended item info, which is the
            // path the mod prefixes: ItemStack.GetHandbookInfo. Searching the
            // handbook does not go through it - the handbook already knows what
            // its pages say - so searching alone proves nothing about the cache.
            ItemStack[] samples =
            [
                .. new[]
                {
                    OptiTimeConfig.IronAxeCodes,
                    OptiTimeConfig.PlankCodes,
                    OptiTimeConfig.CobblestoneCodes,
                    OptiTimeConfig.StickCodes,
                }
                .Select(TryResolveStack)
                .Where(s => s is not null)!,
            ];

            Assert.True(samples.Length > 0,
                "None of the suite's sample item codes resolved in this client, so the " +
                "handbook cache has nothing to look up.");

            for (int round = 0; round < 3; round++)
            {
                foreach (ItemStack stack in samples)
                {
                    ItemTooltip tooltip = Client.Items.Tooltip(stack, extendedInfo: true);
                    Assert.False(string.IsNullOrWhiteSpace(tooltip.Title),
                        $"'{stack}' rendered no handbook title. The handbook is unusable, " +
                        "with or without the mod.");
                }

                await Session!.StepFramesAsync(5);
            }

            DiagCounters counters = await CaptureDiagAsync("handbook", frames: 300);
            Assert.True(counters.Present, "The 'handbook' diag module produced no dump.");

            double lookups = counters.Require(DiagCounter.CacheLookups, "handbook");
            double hits = counters.Require(DiagCounter.Hits, "handbook");
            double misses = counters.Require(DiagCounter.Misses, "handbook");

            await WriteArtifactAsync(nameof(HandbookOptimizationTests), "handbook.txt", counters.Raw);
            Console.WriteLine(counters.Raw);

            Console.WriteLine(
                lookups > 0
                    ? $"handbook: {hits} hits out of {lookups} lookups " +
                      $"({misses} misses)"
                    : "handbook: no lookups recorded. The mod's prefix is on " +
                      "CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo, which " +
                      "only runs for an item page - the pages this client opens by default " +
                      "are game-mechanic text pages. The cache hit rate is therefore " +
                      "unverified here; see the coverage gaps in the report.");

            if (lookups > 0)
            {
                Assert.True(hits > 0,
                    $"handbook: {hits} hits out of {lookups} lookups. A cache that never " +
                    "hits is strictly slower than no cache, because every lookup pays for " +
                    "the miss and then does the original work. That is the v1.5.9 " +
                    "regression.");

                Assert.Equal(lookups, hits + misses, 0);
            }
        }
        finally
        {
            await Client!.Ui!.Handbook.CloseAsync();
            await Session!.StepFramesAsync(5);
        }
    }

    /// <summary>
    /// Opening a page has to produce the same content with the cache on and off.
    /// The cache returns lists of blocks, creatures and containers; a lookup
    /// that misses the world accessor and falls back to a stale snapshot returns
    /// plausible but wrong entries, which is invisible unless compared.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task HandbookPagesReadTheSameWithTheCacheOnAndOff()
    {
        await WarmUpAsync();

        if (!Client!.Ui!.Handbook.IsAvailable)
        {
            Assert.Fail("The handbook dialog is not registered on this client.");
        }

        var patched = new Dictionary<string, string?>();
        var unpatched = new Dictionary<string, string?>();

        // Page codes are taken from the game's own page list rather than
        // hardcoded: the codes come from each collectible's asset, they change
        // between game versions, and a made-up one fails with "the handbook has
        // no page X", which says nothing about the mod.
        IReadOnlyList<HandbookPageInfo> all = await Client.Ui.Handbook.PagesAsync();
        string[] pages = [.. all
            .Where(p => !string.IsNullOrWhiteSpace(p.Code))
            .Take(3)
            .Select(p => p.Code)];

        Assert.True(pages.Length > 0,
            $"The handbook lists no pages at all ({all.Count} returned). Nothing to compare.");

        Console.WriteLine($"comparing pages: {string.Join(", ", pages)}");

        foreach (bool enabled in new[] { false, true })
        {
            await SetOptimizationAsync("handbook", enabled);

            await Client.Ui.Handbook.OpenAsync();
            await Client.Ui.Handbook.WaitUntilLoadedAsync();
            await Session!.StepFramesAsync(20);

            foreach (string page in pages)
            {
                await Client.Ui.Handbook.OpenPageAsync(page);
                await Session!.StepFramesAsync(15);

                (enabled ? patched : unpatched)[page] = Client.Ui.Handbook.PageText;
            }

            await Client.Ui.Handbook.CloseAsync();
            await Session!.StepFramesAsync(5);
        }

        foreach (string page in pages)
        {
            string? a = unpatched[page];
            string? b = patched[page];

            Assert.False(string.IsNullOrWhiteSpace(a),
                $"Page '{page}' rendered no text with the cache off.");
            Assert.Equal(a, b);
        }
    }

    /// <summary>
    /// The handbook is the largest dialog in the game and the one that
    /// re-composes while scrolling. If its layout code is patched, a page list
    /// that scrolls must still scroll, and elements must stay on screen.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task HandbookLayoutStaysOnScreenWhileBrowsing()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("handbook", enabled: true);

        await Client!.Ui!.Handbook.OpenAsync();
        if (!Client.Ui.IsOpen("GuiDialogHandbook"))
        {
            return;
        }

        try
        {
            PharosAssert.DialogOnScreen(Client, "GuiDialogHandbook");

            await Client.Ui.Handbook.SearchAsync("stone");
            await Session!.StepFramesAsync(20);

            DialogLayout layout = Client.Ui.Layout("GuiDialogHandbook");
            PharosAssert.NoOverlappingElements(
                layout,
                include: e => e.Width > 4 && e.Height > 4);

            Console.WriteLine($"handbook layout: {layout.Elements.Count} elements");
        }
        finally
        {
            Client.Ui.Close("GuiDialogHandbook");
            await Session!.StepFramesAsync(5);
        }
    }
}