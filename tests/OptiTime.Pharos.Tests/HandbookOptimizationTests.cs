using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
    /// A fresh vanilla profile must not disable the Handbook optimization during
    /// startup. The ModDB report requires the reporter's conflicting modlist to
    /// reproduce exactly; this control only checks OptiTime's no-conflict boot.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task HandbookOptimizationStartsEnabledWithoutConflictingMods()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> states = await ReadStatusAsync();
        OptionState handbook = states["handbook"];

        await WriteArtifactAsync(nameof(HandbookOptimizationTests), "handbook-startup.txt",
            $"status={handbook}");

        Assert.True(handbook.Enabled,
            $"Handbook started disabled in the isolated vanilla profile. Status: {handbook}. " +
            "The comment's third-party conflict still needs its exact modlist and boot log.");
    }

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
        Assert.Equal("ready", indexer);
    }

    /// <summary>
    /// A real Handbook stack with indexed block drops must return the same
    /// relationship repeatedly from the frozen cache. The page-text scenario
    /// below separately verifies the Handbook UI with the optimization on and off.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task HandbookBlockDropCacheReturnsRepeatedHits()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("handbook", async () =>
        {
            await SetOptimizationAsync("handbook", enabled: true);
            DiagCounters readiness = await CaptureDiagAsync("handbook", frames: 1);
            Assert.True(readiness.Present, "The 'handbook' diag module produced no dump.");
            Assert.Equal("ready", readiness.Text(DiagCounter.Indexer));

            await ResetDiagAsync("handbook");
            var client = Client!;
            string[] lookup = client.RunOnClientThread(() =>
            {
                var clientApi = (ICoreClientAPI)client.Client.api;
                Assembly mod = AppDomain.CurrentDomain.GetAssemblies()
                    .SingleOrDefault(assembly => assembly.GetName().Name == "OptiTime")
                    ?? throw new InvalidOperationException("The OptiTime assembly is not loaded in the client process.");
                Type handbookType = mod.GetType("OptiTime.HandbookOptimization")
                    ?? throw new InvalidOperationException("The Handbook optimization type is missing.");
                MethodInfo getDrops = handbookType.GetMethod(
                        "GetCachedBlockDrops",
                        BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(handbookType.FullName, "GetCachedBlockDrops");

                List<ItemStack> ReadDrops(ItemStack stack) =>
                    (List<ItemStack>?)getDrops.Invoke(null, [clientApi, stack])
                    ?? throw new InvalidOperationException("The handbook index has not completed.");

                foreach (CollectibleObject collectible in client.Client.World.Collectibles)
                {
                    IEnumerable<ItemStack>? handbookStacks = collectible.GetHandBookStacks(clientApi);
                    if (handbookStacks is null) continue;

                    foreach (ItemStack stack in handbookStacks)
                    {
                        List<ItemStack> drops = ReadDrops(stack);
                        if (drops.Count == 0) continue;

                        List<ItemStack> repeatedDrops = ReadDrops(stack);
                        string[] firstCodes = [.. drops.Select(drop => drop.Collectible.Code.ToString())];
                        string[] repeatedCodes = [.. repeatedDrops.Select(drop => drop.Collectible.Code.ToString())];
                        Assert.Equal(firstCodes, repeatedCodes);
                        return new[] { $"stack={stack.Class}:{stack.Collectible.Code}" }
                            .Concat(firstCodes)
                            .ToArray();
                    }
                }

                throw new InvalidOperationException(
                    "No registered Handbook stack returned an indexed block-drop relationship.");
            });

            await RunCommandAsync($"{OptiTimeConfig.Command} diag handbook dump");
            DiagCounters counters = DiagReader.Parse(
                DiagReader.Body(ClientLogText(), "handbook"));
            double lookups = counters.Require(DiagCounter.CacheLookups, "handbook");
            double hits = counters.Require(DiagCounter.Hits, "handbook");
            double misses = counters.Require(DiagCounter.Misses, "handbook");

            await WriteArtifactAsync(nameof(HandbookOptimizationTests), "handbook-cache.txt",
                $"{string.Join('\n', lookup)}\n{counters.Raw}");
            Console.WriteLine(counters.Raw);

            Assert.True(lookups >= 2, "The Handbook block-drop cache was never queried.");
            Assert.True(hits >= 2, $"Repeated access recorded only {hits} cache hits.");
            Assert.Equal(lookups, hits + misses, 0);
        });
    }

    /// <summary>
    /// Opening two stack pages selected from the loaded page list has to
    /// produce the same page text after saving the Handbook setting off and on.
    /// Both samples run in one
    /// client process, so the startup Harmony patch remains installed; this
    /// compares only page text, not vanilla behavior, item-stack widgets, or
    /// images.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task HandbookPageTextRemainsStableAcrossSettingChanges()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("handbook", async () =>
        {
            if (!Client!.Ui!.Handbook.IsAvailable)
            {
                Assert.Fail("The handbook dialog is not registered on this client.");
            }

            var settingOn = new Dictionary<string, string?>();
            var settingOff = new Dictionary<string, string?>();

            // Page codes are taken from the game's own page list rather than
            // hardcoded: the codes come from each collectible's asset, they change
            // between game versions, and a made-up one fails with "the handbook has
            // no page X", which says nothing about the mod.
            string[] pages = await FindTwoStackPagesWithTextAsync();

            Console.WriteLine($"comparing page text across saved setting values: {string.Join(", ", pages)}");

            foreach (bool settingEnabled in new[] { false, true })
            {
                await SetOptimizationAsync("handbook", settingEnabled);
                await ResetDiagAsync("handbook");

                await Client.Ui.Handbook.OpenAsync();
                await Client.Ui.Handbook.WaitUntilLoadedAsync();
                await Session!.StepFramesAsync(20);
                try
                {
                    foreach (string page in pages)
                    {
                        await Client.Ui.Handbook.OpenPageAsync(page);
                        await Session!.StepFramesAsync(15);

                        (settingEnabled ? settingOn : settingOff)[page] = Client.Ui.Handbook.PageText;
                    }
                }
                finally
                {
                    await Client.Ui.Handbook.CloseAsync();
                    await Session!.StepFramesAsync(5);
                }
                await RunCommandAsync($"{OptiTimeConfig.Command} diag handbook dump");

                if (settingEnabled)
                {
                    DiagCounters counters = DiagReader.Parse(
                        DiagReader.Body(ClientLogText(), "handbook"));
                    Assert.True(counters.Require(DiagCounter.CacheLookups, "handbook") > 0,
                        "Opening stack pages with the cache on did not invoke the optimized target.");
                }
            }

            foreach (string page in pages)
            {
                string? off = settingOff[page];
                string? on = settingOn[page];

                Assert.False(string.IsNullOrWhiteSpace(off),
                    $"Page '{page}' rendered no text with the saved setting off.");
                Assert.Equal(off, on);
            }
        });
    }

    /// <summary>
    /// The handbook dialog stays on screen without overlapping controls after a
    /// search narrows the page list.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task HandbookLayoutHasNoOverlapsAfterSearch()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("handbook", async () =>
        {
            await SetOptimizationAsync("handbook", enabled: true);

            await Client!.Ui!.Handbook.OpenAsync();
            Assert.True(Client.Ui.Handbook.IsOpen,
                "The handbook did not open, so its layout was not checked.");

            try
            {
                PharosAssert.DialogOnScreen(Client, "GuiDialogSurvivalHandbook");

                await Client.Ui.Handbook.SearchAsync("stone");
                await Session!.StepFramesAsync(20);

                DialogLayout layout = Client.Ui.Layout("GuiDialogSurvivalHandbook");
                PharosAssert.NoOverlappingElements(
                    layout,
                    include: e => e.Width > 4 && e.Height > 4);

                Console.WriteLine($"handbook layout: {layout.Elements.Count} elements");
            }
            finally
            {
                Client.Ui.Close("GuiDialogSurvivalHandbook");
                await Session!.StepFramesAsync(5);
            }
        });
    }

    private async Task<string[]> FindTwoStackPagesWithTextAsync()
    {
        var handbook = Client!.Ui!.Handbook;
        IReadOnlyList<HandbookPageInfo> pages = await handbook.PagesAsync();
        var found = new List<string>(capacity: 2);

        await handbook.OpenAsync();
        try
        {
            foreach (HandbookPageInfo page in pages.Where(page => page.Kind == "stack"))
            {
                await handbook.OpenPageAsync(page.Code);
                await Session!.StepFramesAsync(15);
                if (!string.IsNullOrWhiteSpace(handbook.PageText))
                {
                    found.Add(page.Code);
                    if (found.Count == 2) break;
                }
            }
        }
        finally
        {
            await handbook.CloseAsync();
            await Session!.StepFramesAsync(5);
        }

        Assert.Equal(2, found.Count);
        return [.. found];
    }
}
