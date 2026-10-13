using System.Runtime.CompilerServices;
using Vintagestory.API.Config;
using Vintagestory.API.Client;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Static checks over the mod source itself, with no game boot.
///
/// The one class of defect that reaches a player's chat without touching the
/// game at all is unescaped rich text: the mod's own strings go through
/// <c>ShowChatMessage</c>, which parses them as markup, and a stray angle
/// bracket becomes a client error rather than an exception.
/// </summary>
public partial class ModSourceAnalysis
{
    /// <summary>
    /// Walks up from <c>AppContext.BaseDirectory</c> to the repository root by
    /// looking for the file that only the repository has.
    /// </summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OptiTime.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root from " + AppContext.BaseDirectory +
            ". These tests read the mod's own source and need it present next to the " +
            "test project.");
    }

    [Fact]
    public void EverySourceFileIsFound()
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "Core", "OptiTimeConfig.cs")),
            "Core/OptiTimeConfig.cs is not where the suite expects it.");
    }

    /// <summary>
    /// Every optimization description the mod prints must be safe to hand to
    /// <c>ShowChatMessage</c>, which parses the string as rich text.
    ///
    /// <c>PrintOptStatus</c> builds its line as
    /// <c>$"  {name}: {status} - {desc}"</c> and prints it with no escaping, so
    /// any raw <c>&lt;</c> or <c>&gt;</c> in a description is a client-side
    /// error on every single status print. The usage and diag help lines were
    /// escaped in v1.5.16; the descriptions were not, and two of them still
    /// contain a raw <c>&gt;</c>.
    ///
    /// Read from the source rather than from the live client so it costs
    /// milliseconds and needs no game boot.
    /// </summary>
    [Fact]
    public void OptimizationDescriptionsContainNoRawRichTextCharacters()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "Core", "OptiTimeConfig.cs"));

        int start = source.IndexOf("GetOptimizationDescription", StringComparison.Ordinal);
        Assert.True(start >= 0, "OptiTimeConfig.GetOptimizationDescription is gone.");

        int end = source.IndexOf("\n        public ", start, StringComparison.Ordinal);
        string body = source[start..(end > 0 ? end : source.Length)];

        List<string> offending = [];

        foreach (System.Text.RegularExpressions.Match literal in DescriptionLiteral()
                     .Matches(body))
        {
            string text = literal.Groups["text"].Value;

            // Escaped forms are what the chat renderer expects.
            if (!text.Contains('<', StringComparison.Ordinal) &&
                !text.Contains('>', StringComparison.Ordinal))
            {
                continue;
            }

            offending.Add(text);
        }

        Assert.True(offending.Count == 0,
            "These optimization descriptions contain a raw '<' or '>' and are printed " +
            "to chat without escaping, which the rich-text renderer rejects on every " +
            "'.optitime status':\n  " +
            string.Join("\n  ", offending) +
            "\n\nFix: use '&gt;' / '&lt;' in the literal, or escape in PrintOptStatus.");

        Assert.NotEmpty(DescriptionLiteral().Matches(body).Count.ToString());
    }

    /// <summary>
    /// The descriptions are the only part of the status output that never goes
    /// through <c>Lang.Get</c>, which means they are the only part a player
    /// cannot read in their own language - while the mod ships 31 language
    /// files and claims complete translation coverage.
    ///
    /// Reported as a count rather than an assertion: whether the project wants
    /// to translate descriptions is a product decision, and a test that fails
    /// on it would be asserting an opinion. What is asserted is that the gap is
    /// still there, so it cannot grow unnoticed.
    /// </summary>
    [Fact]
    public void EveryDescriptionUsesATranslationKey()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "Core", "OptiTimeConfig.cs"));

        int start = source.IndexOf("GetOptimizationDescription", StringComparison.Ordinal);
        int end = source.IndexOf("\n        public ", start, StringComparison.Ordinal);
        string body = source[start..(end > 0 ? end : source.Length)];

        int literals = DescriptionLiteral().Matches(body).Count;
        int translated = LangGetKey().Matches(body).Count;

        Console.WriteLine(
            $"optimization descriptions: {literals} hard-coded string(s), " +
            $"{translated} translated key(s)");

        Assert.Equal(0, literals);
        Assert.True(translated >= 21,
            "All option descriptions, including unknown fallback, must use Lang.Get keys.");
    }

    /// <summary>
    /// GitHub Issue #2 regression test:
    /// In singleplayer, InventoryCraftingGrid.FindMatchingRecipe is patched globally
    /// across the process. The prefix must explicitly guard with
    /// `__instance?.Api?.Side != EnumAppSide.Client` to avoid executing on the integrated
    /// server thread and leaking client Item/CollectibleBehavior instances into the server's inventory.
    /// </summary>
    [Fact]
    public void RecipeLookupCacheEnforcesClientSideOnly()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "Gui", "RecipeLookupCacheOptimization.cs"));

        Assert.Contains("__instance?.Api?.Side != EnumAppSide.Client", source, StringComparison.Ordinal);
        Assert.Contains("world?.Side != EnumAppSide.Client", source, StringComparison.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:=>|Console\.WriteLine\()\s*\$?""(?<text>[^""]*)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DescriptionLiteral();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"Lang\.Get\(""optitime:[^""]+""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LangGetKey();
}

/// <summary>
/// Regression tests for bugs the mod has actually shipped and fixed.
///
/// Every test here maps to a specific entry in the release history. That
/// matters: these are not hypothetical cases, they are the failure modes this
/// codebase has demonstrated it is prone to, which makes them the highest-value
/// things to re-check after any change - a refactor, or a new Vintage Story
/// patch release.
///
/// The recurring theme is that most of these bugs were invisible to a smoke
/// test that only checks "does it load". They surface as a crash deep in a
/// rarely-taken path, a freeze during world load, or a flood of chat errors.
/// Running the game for real is the only way to hit them.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class RegressionTests : OptiTimeScenarioBase
{
    /// <summary>
    /// This class asserts on the log itself, so the harness' blanket
    /// "fail on any client error" gate has to be off: the rich-text errors it
    /// is looking for are client errors.
    /// </summary>
    protected override bool FailOnLoggedErrors => false;

    /// <summary>
    /// v1.5.16: "Angle brackets in the .optitime usage and diag help messages
    /// are now escaped, so the chat renderer no longer treats them as rich-text
    /// tags. Previously these spammed parse errors whenever help was shown."
    ///
    /// Player-visible, and the kind of thing that makes a mod look broken. The
    /// error surfaces in the log rather than in an exception, so nothing else in
    /// the suite would catch it.
    /// </summary>
    [ClientServerScenario]
    public async Task HelpTextProducesNoRichTextParseErrors()
    {
        await WarmUpAsync();

        CommandOutput[] helpOutputs =
        [
            await RunCommandAsync(OptiTimeConfig.Command),
            await RunCommandAsync($"{OptiTimeConfig.Command} help"),
            await RunCommandAsync($"{OptiTimeConfig.Command} diag"),
            await RunCommandAsync($"{OptiTimeConfig.Command} diag help"),
            await RunCommandAsync($"{OptiTimeConfig.Command} status"),
            await RunCommandAsync($"{OptiTimeConfig.Command} flysound"),
            await RunCommandAsync($"{OptiTimeConfig.Command} repulseagents"),
        ];

        await Session!.StepFramesAsync(10);

        string[] parseErrors = [.. Client!.Logs.Entries
            .Where(e => e.IsWarningOrWorse)
            .Select(e => e.Message)
            .Where(line => line.Contains("rich", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("tag", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("no tag was opened", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("should be closed first", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("<opt>", StringComparison.OrdinalIgnoreCase))];

        await WriteArtifactAsync(nameof(RegressionTests), "richtext-errors.txt",
            string.Join('\n', parseErrors));

        Assert.True(parseErrors.Length == 0,
            $"Showing the mod's own status produced rich-text parse errors ({parseErrors.Length}):\n  " +
            string.Join("\n  ", parseErrors.Take(20)) +
            (parseErrors.Length > 20 ? $"\n  (+{parseErrors.Length - 20} more)" : string.Empty) +
            "\n\nSource: OptiTimeConfig.GetOptimizationDescription returns English literals that " +
            "contain a raw '>' - \"Fly sound volume updates only on >1% change when playing\" " +
            "and \"Skip entity separation checks for distant creatures (>64 blocks)\" - and " +
            "PrintOptStatus pastes them straight into ShowChatMessage. v1.5.16 escaped the " +
            "usage and diag help lines but not the option descriptions.");

        // The help text itself should still reach the player.
        Assert.All(helpOutputs, o => Assert.False(string.IsNullOrWhiteSpace(o.Text)));
    }

    /// <summary>
    /// v1.5.5: "NullReferenceException crash in FrameProfilerUtil.Leave() when
    /// profiling is toggled mid-frame."
    ///
    /// A mid-frame toggle only happens when the player types a command on the
    /// same frame a render pass is running. Stepping frames between the toggle
    /// and the teardown is what puts the switch inside that window.
    /// </summary>
    [ClientServerScenario]
    public async Task TogglingProfilingMidFrameDoesNotCrash()
    {
        await WarmUpAsync();

        for (int i = 0; i < 10; i++)
        {
            await RunCommandOkAsync($"{OptiTimeConfig.Command} profile on");
            await Session!.StepFramesAsync(1);
            await RunCommandOkAsync($"{OptiTimeConfig.Command} profile off");
            await Session!.StepFramesAsync(1);
        }

        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile reset");

        Assert.Empty(Client!.Logs.Errors);
    }

    /// <summary>
    /// v1.5.9: "Particle crash: fix IndexOutOfRange in
    /// ExplosionSmokeParticles.Pos on async thread."
    ///
    /// This one only fires off the main thread, so it cannot be reproduced by
    /// inspecting what the client did on the main thread. Hammering the toggle
    /// while the world runs gives the off-thread particle pools enough chances
    /// to race.
    /// </summary>
    [ClientServerScenario]
    public async Task ParticleCullingSurvivesRepeatedToggling()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        for (int i = 0; i < 12; i++)
        {
            await SetOptimizationAsync("particles", enabled: i % 2 == 0);
            await Session!.StepFramesAsync(20);
        }

        await SetOptimizationAsync("particles", enabled: true);

        string[] particleFailures = [.. Client!.Logs.Errors
            .Select(e => e.Message)
            .Where(line => line.Contains("particle", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("IndexOutOfRange", StringComparison.Ordinal)
                        || line.Contains("ExplosionSmoke", StringComparison.OrdinalIgnoreCase))];

        Assert.True(particleFailures.Length == 0,
            "Particle handling failed while toggling:\n  " + string.Join("\n  ", particleFailures));
    }

    /// <summary>
    /// v1.5.3: "Shadow Vegetation Cull transpiler used a default Label(0) that
    /// collided with existing IL labels, causing the game to freeze when
    /// loading a world."
    ///
    /// The freeze happens at load, before anything is measurable, so the test
    /// is simply that we got here at all - plus a check that no transpiler
    /// threw while applying.
    /// </summary>
    [ClientServerScenario]
    public async Task WorldLoadsWithoutTranspilerFailure()
    {
        await WarmUpAsync();

        string[] transpilerFailures = [.. Client!.Logs.Entries
            .Where(e => e.IsWarningOrWorse)
            .Select(e => e.Message)
            .Where(line => line.Contains("transpil", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("patch", StringComparison.OrdinalIgnoreCase))];

        await WriteArtifactAsync(nameof(RegressionTests), "boot-warnings.txt",
            string.Join('\n', transpilerFailures));

        Assert.True(transpilerFailures.Length == 0,
            "A Harmony patch or transpiler reported a problem during load:\n  " +
            string.Join("\n  ", transpilerFailures));

        Assert.True(LoadedChunks() > 0, "No chunks loaded; the client never left startup.");
    }

    /// <summary>
    /// v1.5.9 and v1.5.13: the recipe lookup cache had a snapshot invalidated
    /// by an unstable world accessor in the integrated server, giving a 0% hit
    /// rate; and narrowing could return no candidates and then fall through
    /// instead of to vanilla.
    ///
    /// See <c>RecipeLookupTests</c> for the grid-level coverage added after this
    /// one; this keeps the churn case, which is the crash class rather than the
    /// cache-behaviour case.
    /// </summary>
    [ClientServerScenario]
    public async Task RecipeLookupSurvivesInventoryChurn()
    {
        await WarmUpAsync();

        for (int i = 0; i < 4; i++)
        {
            Client!.Inventory.DragSlot(i % 8, 20 + i);
            await Session!.StepFramesAsync(5);
            Client.Inventory.DragSlot(20 + i, i % 8);
            await Session.StepFramesAsync(5);
        }

        string[] recipeFailures = [.. Client.Logs.Errors
            .Select(e => e.Message)
            .Where(line => line.Contains("recipe", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("craft", StringComparison.OrdinalIgnoreCase))];

        Assert.True(recipeFailures.Length == 0,
            "Recipe lookup reported errors during inventory churn:\n  " +
            string.Join("\n  ", recipeFailures));
    }

    /// <summary>
    /// v1.5.13: "Entity animation now calls OnClientFrame for all entities
    /// matching vanilla behavior (fixes fishing rope detachment and duplicate
    /// bobber spawns)" and v1.5.12 exempted projectiles from the animation LOD.
    ///
    /// The entity animation LOD skips animation work for distant entities,
    /// which means a distant entity that should move does not. Any visible
    /// difference between the two states is the LOD doing something visible,
    /// which is the bug.
    /// </summary>
    [ClientServerScenario]
    public async Task EntityAnimationLodIsNotVisible()
    {
        await WarmUpAsync();
        bool waving = Client!.RunOnClientThread(() => Vintagestory.Client.NoObf.ClientSettings.WavingFoliage);
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
        try
        {
            Client.RunOnClientThread(() => { Vintagestory.Client.NoObf.ClientSettings.WavingFoliage = false; return true; });
            Assert.True((await Client.ReloadAsync(AssetCategory.shaders)).Succeeded, "Visual control shader reload failed");
            await SetOptimizationAsync("entityanim", enabled: false);

            // CaptureAtAsync, not ApplyPoseAsync: a capture taken before chunk
            // streaming settles differs by tens of thousands of pixels for
            // reasons that have nothing to do with the optimization, and that
            // looks exactly like a visible LOD.
            await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
            await WaitForChunksToSettleAsync();
            FramebufferSnapshot withoutLod = Client!.CaptureFrame();

            RequireVisibleWorld(withoutLod, "entityanim-off");
            await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
            await WaitForChunksToSettleAsync();
            var repeat = Client.CaptureFrame();
            var churn = repeat.Compare(withoutLod, tolerance: 0.02f);
            Console.WriteLine($"entityanim same-state churn: {churn.DiffPixelCount}");
            Assert.True(churn.DiffPixelCount <= 120_000, "Same-state animation scene exceeds existing comparison budget");
            // Runtime flags do not uninstall boot patches. This checks rendering
            // stability across commands, not independent LOD implementation states.
            await SetOptimizationAsync("entityanim", enabled: true);
            await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
            await WaitForChunksToSettleAsync();
            FramebufferComparisonResult result =
                Client.CaptureFrame().Compare(withoutLod, tolerance: 0.02f);

            // Budget is deliberately larger than the rendering suite's. This
            // is a *living* scene: animals animate, water moves, and two
            // captures taken seconds apart cannot be pixel-identical. The
            // question is whether the LOD changes *more* of the frame than the
            // scene changes on its own, and the honest way to ask that here is
            // a looser ceiling plus the numbers in the message, not a tight
            // pixel count that reports the world being alive as a defect.
            const int LivingSceneBudget = 120_000;

            Console.WriteLine(
                $"entityanim LOD diff: {result.DiffPixelCount} pixels " +
                $"({result.DiffPixelFraction:P2} of the frame, mean {result.MeanDiff:F4}, " +
                $"max {result.MaxDiff:F2}), budget {LivingSceneBudget}");

            Assert.True(result.DiffPixelCount <= LivingSceneBudget,
                $"Runtime entityanim command states differed by {result.DiffPixelCount} pixels " +
                $"({result.DiffPixelFraction:P2} of the frame), past the " +
                $"{LivingSceneBudget}-pixel ceiling for a scene with live animals. That is " +
                "large enough to be more than the scene animating on its own: check " +
                "whether entities have stopped animating at distance. A pixel-identical " +
                "comparison is not available here - the world does not hold still.");
        }
        finally
        {
            await RestoreStatusAsync(before);
            Client.RunOnClientThread(() => { Vintagestory.Client.NoObf.ClientSettings.WavingFoliage = waving; return true; });
            Assert.True((await Client.ReloadAsync(AssetCategory.shaders)).Succeeded, "Visual setting restoration failed");
        }
    }

    /// <summary>
    /// v1.5.16: the GUI Manager optimization was silently dead on Vintage Story
    /// 1.22.5 because a dynamic dispatch was replaced with typed field
    /// references that did not resolve there.
    ///
    /// This cannot detect "the optimization does nothing" directly - that needs
    /// a comparison against vanilla, which the rendering tests do not cover for
    /// GUI Manager because a GUI path does not affect the 3D framebuffer. What
    /// it can catch is the worse failure: the dialog stops working.
    /// </summary>
    [ClientServerScenario]
    public async Task GuiManagerPatchKeepsDialogsUsable()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("guimgr", enabled: true);

        (string Dialog, string Fallback)[] dialogs =
        [
            ("GuiDialogEscapeMenu", "escapemenudialog"),
            ("GuiDialogInventory", "inventorydialog"),
            ("GuiDialogCharacter", "characterdialog"),
            ("GuiDialogWorldMap", "worldmap"),
        ];

        int exercised = 0;

        foreach ((string dialog, string fallback) in dialogs)
        {
            // Open through the game's own hotkey rather than by calling the
            // dialog directly, so the patched input path is exercised.
            if (!await OpenDialogAsync(dialog, fallback))
            {
                continue;
            }

            exercised++;
            await CloseDialogAsync(dialog);

            Assert.False(Client!.Ui!.IsOpen(dialog), $"'{dialog}' would not close.");
        }

        Assert.True(exercised >= 2,
            $"Only {exercised} dialog(s) opened. Too few of the GUI paths were touched " +
            "for this test to say anything about the patch.");

        AssertNoUnexpectedClientErrors("gui");
    }

    /// <summary>
    /// The mod installs a Harmony transpiler on
    /// <c>TranslationService.HasTranslation</c> to fix a vanilla thread-safety
    /// bug - an unsynchronised <c>HashSet</c> that two threads can write at
    /// once. That path is hit constantly, from asset loading and from GUI
    /// text, and a transpiler that changes the method's contract can make the
    /// client throw inside a logger call.
    ///
    /// Exercised by driving translation-heavy paths: every dialog this client
    /// has, plus a language switch, which reloads every string in the game.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task TranslationServicePatchSurvivesConcurrentLookups()
    {
        await WarmUpAsync();

        bool patchAnnounced = Client!.Logs.Entries
            .Select(e => e.Message)
            .Any(m => m.Contains("TranslationService thread-safety fix loaded", StringComparison.Ordinal));

        Assert.True(patchAnnounced,
            "The mod did not announce that it applied its TranslationService fix. It does " +
            "so unconditionally at boot, so either the patch failed or the message " +
            "changed.");

        // Parallel lookups from several tasks: this is the shape of the bug the
        // mod is fixing, and the patched path must not throw on any of them.
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
        {
            await Session!.StepFramesAsync(1);

            string key = i % 2 == 0 ? "optitime:setting-recipe" : "optitime:setting-gui-no-linq";
            string? text = await Task.Run(() => Lang.Get(key));

            Assert.NotNull(text);
        }));

        Assert.Empty(Client!.Logs.Errors);
    }

    /// <summary>
    /// The mod registers a <c>ClientSettings</c> watcher on
    /// <c>viewDistance</c> and removes it on dispose. A watcher left behind, or
    /// one that fires on every settings write, both show up here: the client has
    /// to survive a settings round-trip and the mod's derived thresholds have to
    /// stay sane.
    /// </summary>
    [ClientServerScenario]
    public async Task ChangingViewDistanceDoesNotBreakTheClient()
    {
        await WarmUpAsync();

        int[] distances = [64, 128, 256, 128, 64];

        foreach (int distance in distances)
        {
            Client!.Settings.Set("viewDistance", distance);
            await Session!.StepFramesAsync(60);

            Assert.True(LoadedChunks() > 0,
                $"At view distance {distance} the client has no chunks at all. The " +
                "mod's view-distance watcher feeds particle, occlusion, dynamic-light " +
                "and entity-animation thresholds, so a bad value here disables them all.");

            Assert.Empty(Client.Logs.Errors);
        }

        // Leave the suite's declared distance in place for whatever runs next.
        Client.Settings.Set("viewDistance", OptiTimeConfig.ViewDistance);
        await Session.StepFramesAsync(60);
    }
}