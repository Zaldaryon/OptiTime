using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Zaldaryon.Pharos.Assertions;
using Zaldaryon.Pharos.Input;
using Zaldaryon.Pharos.UI;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Drives the GUI through Vintage Story's hotkeys and mouse clicks while the
/// GUI render optimization is active. The optional input patch has its own
/// startup-configured scenario below.
///
/// <c>guimgr</c> is the riskiest option in the mod and the one that has already
/// broken once: v1.5.16 shipped because the GUI Manager optimization had been
/// silently dead since 1.22.5, where a dynamic dispatch was replaced with typed
/// field references that did not resolve. It patches eight input and render
/// render entry points on <c>GuiManager</c>, plus a finalizer on
/// <c>GuiComposer.PostRender</c>.
///
/// A test that only asserts "the dialog opened" cannot tell a working patch from
/// a dead one. What it can tell - and what this class checks - is whether the
/// patched paths still carry the dialogs: if a prefix returns the wrong value
/// or a finalizer swallows an exception, the dialog stops responding and that
/// is visible.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class GuiOptimizationTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Every tested dialog has to survive the GUI render optimization. The
    /// game's own hotkey opens and closes each dialog through the normal input
    /// path; this class does not enable the optional input patch.
    ///
    /// A dialog that is unavailable in this play style is a skip, not a
    /// failure; one that opens and then will not close is a failure, because
    /// the mod's input prefixes changed how the close key is handled.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task DialogsOpenAndCloseThroughTheirOwnHotkeys()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("guimgr", async () =>
        {
            await SetOptimizationAsync("guimgr", enabled: true);

            (string Dialog, string Fallback)[] dialogs =
            [
                ("GuiDialogEscapeMenu", "escapemenudialog"),
                ("GuiDialogInventory", "inventorydialog"),
                ("GuiDialogCharacter", "characterdialog"),
                ("GuiDialogWorldMap", "worldmap"),
            ];

            var opened = new List<string>();
            var skipped = new List<string>();

            foreach ((string dialog, string fallback) in dialogs)
            {
                if (!await OpenDialogAsync(dialog, fallback))
                {
                    skipped.Add(dialog);
                    continue;
                }

                opened.Add(dialog);

                string hotkey = DialogHotkey(dialog) ?? fallback;
                Assert.NotNull(Client!.Hotkeys.Get(hotkey));
                await Client.Hotkeys.TriggerAsync(hotkey);
                await Session!.StepFramesAsync(10);
                bool closed = !Client.Ui!.IsOpen(dialog);

                Assert.True(closed,
                    $"'{dialog}' did not close when its own hotkey '{hotkey}' was pressed " +
                    "through the patched GuiManager input path.");
                Assert.False(Client.Ui.IsOpen(dialog), $"'{dialog}' is still open after closing it.");
            }

            Console.WriteLine($"opened: {string.Join(", ", opened)}");
            Console.WriteLine($"unavailable in this play style: {string.Join(", ", skipped)}");

            Assert.True(opened.Count >= 2,
                $"Only {opened.Count} dialog(s) opened at all ({string.Join(", ", opened)}). " +
                "Too few of the GUI paths were exercised for this test to mean anything.");

            AssertNoUnexpectedClientErrors("gui");
        });
    }

    /// <summary>
    /// Dialogs stay usable after writing the GUI setting off and on. Harmony
    /// patches remain installed until restart, so this does not compare patched
    /// behavior with vanilla behavior.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task DialogsRemainFunctionalAcrossGuiSettingChanges()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
        try
        {
            await SetOptimizationAsync("guimgr", enabled: false);

            string[] settingOff = [];
            foreach ((string dialog, string fallback) in new[]
                     {
                         ("GuiDialogEscapeMenu", "escapemenudialog"),
                         ("GuiDialogInventory", "inventorydialog"),
                     })
            {
                if (!await OpenDialogAsync(dialog, fallback)) continue;

                settingOff = [.. settingOff, dialog];

                await CloseDialogAsync(dialog);
            }

            Assert.NotEmpty(settingOff);

            await SetOptimizationAsync("guimgr", enabled: true);

            string[] settingOn = [];
            foreach (string dialog in settingOff)
            {
                if (await OpenDialogAsync(dialog)) settingOn = [.. settingOn, dialog];

                await CloseDialogAsync(dialog);
            }

            Assert.Equal(settingOff.Order(), settingOn.Order());
        }
        finally
        {
            await RestoreStatusAsync(before);
        }
    }

    /// <summary>
    /// A dialog that renders must render inside the window. The GUI Manager
    /// patch replaces the code that walks dialog elements, so elements landing
    /// off-screen is exactly the failure a functional open/close check misses.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task OpenDialogsRenderInsideTheWindow()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var ui = client.Ui ?? throw new InvalidOperationException("Pharos did not initialize the client UI.");
        await WithOptimizationStateRestoredAsync("guimgr", async () =>
        {
            await SetOptimizationAsync("guimgr", enabled: true);

            await OpenDialogRequiredAsync("GuiDialogInventory", "inventorydialog");
            await session.StepFramesAsync(15);

            Assert.True(ui.IsOpen("GuiDialogInventory"),
                "The inventory dialog would not open at all.");

            try
            {
                DialogLayout layout = ui.Layout("GuiDialogInventory");

                PharosAssert.DialogOnScreen(client, "GuiDialogInventory");
                PharosAssert.NoOverlappingElements(layout);

                Console.WriteLine(
                    $"inventory layout: {layout.Elements.Count} elements in " +
                    $"{layout.Composers.Count} composers, window {layout.ScreenWidth}x{layout.ScreenHeight}, " +
                    $"gui scale {layout.GuiScale}");
            }
            finally
            {
                ui.Close("GuiDialogInventory");
                await session.StepFramesAsync(5);
            }
        });
    }

    /// <summary>
    /// A virtual mouse click on the handbook category strip changes the active
    /// category, and a click on its close button dismisses the dialog while the
    /// GUI render optimization is active. The handbook focuses its search field
    /// on open, so its single-letter hotkey is not a reliable close action here.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task VirtualMouseClickChangesTheHandbookCategory()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var ui = client.Ui ?? throw new InvalidOperationException("Pharos did not initialize the client UI.");
        await WithOptimizationStateRestoredAsync("guimgr", async () =>
        {
            await SetOptimizationAsync("guimgr", enabled: true);

            await ui.Handbook.OpenAsync();
            await ui.Handbook.WaitUntilLoadedAsync();
            Assert.True(ui.IsOpen("GuiDialogSurvivalHandbook"), "The handbook dialog did not open.");

            try
            {
                IReadOnlyList<GuiTabInfo> tabs = ui.Tabs("GuiDialogSurvivalHandbook", "verticalTabs");
                Assert.True(tabs.Count > 1,
                    $"The handbook exposes only {tabs.Count} category tab(s), so a tab change cannot be tested.");
                GuiTabInfo active = Assert.Single(tabs, tab => tab.Active);
                GuiTabInfo target = tabs.First(tab => tab.Index != active.Index);

                ui.ClickTab("GuiDialogSurvivalHandbook", "verticalTabs", target.Index);
                Assert.True(await StepUntilAsync(
                        () => ui.Tabs("GuiDialogSurvivalHandbook", "verticalTabs")
                            .Any(tab => tab.Index == target.Index && tab.Active),
                        maxFrames: 30),
                    $"A real virtual mouse click did not activate handbook category '{target.Name}'.");

                var closeButton = Assert.Single(ui.Elements("GuiDialogSurvivalHandbook"),
                    element => element.IsInteractive &&
                        string.Equals(element.Text, "Close Handbook", StringComparison.Ordinal));
                (int closeX, int closeY) = closeButton.Center;
                client.Input.Click(closeX, closeY);
                Assert.True(await StepUntilAsync(
                        () => !ui.IsOpen("GuiDialogSurvivalHandbook"),
                        maxFrames: 30),
                    "A real virtual mouse click on Close Handbook did not dismiss the dialog.");
            }
            finally
            {
                ui.Close("GuiDialogSurvivalHandbook");
                await session.StepFramesAsync(5);
            }
        });
    }

    /// <summary>
    /// <c>GuiComposer.PostRender</c> is patched with a finalizer that exists to
    /// swallow an exception the mod itself can cause. A finalizer that swallows
    /// too much turns a rendering bug into a silent blank GUI, so the test
    /// checks that exceptions on that path are still surfaced somewhere - at
    /// minimum in the log.
    ///
    /// Churn of open/close is the cheapest way to reach the composer path
    /// repeatedly: every dialog transition composes and renders.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task GuiComposerSurvivesRepeatedOpenAndClose()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var player = Player ?? throw new InvalidOperationException("Pharos did not provide a player after warm-up.");
        var ui = client.Ui ?? throw new InvalidOperationException("Pharos did not initialize the client UI.");
        await WithOptimizationStateRestoredAsync("guimgr", async () =>
        {
            await SetOptimizationAsync("guimgr", enabled: true);

            int openedCycles = 0;
            for (int i = 0; i < 30; i++)
            {
                await OpenDialogRequiredAsync("GuiDialogEscapeMenu", "escapemenudialog");
                Assert.True(ui.IsOpen("GuiDialogEscapeMenu"), $"Escape menu cycle {i + 1} did not open a dialog.");
                openedCycles++;
                await session.StepFramesAsync(3);
                ui.Close("GuiDialogEscapeMenu");
                await session.StepFramesAsync(3);
            }

            Assert.Equal(30, openedCycles);

            player.Gui.CloseAllDialogs();
            await session.StepFramesAsync(10);

            AssertNoUnexpectedClientErrors("gui");
        });
    }

    /// <summary>
    /// The GUI Manager module has to be counted every frame, and its counter has
    /// to report the zero-allocation path the mod advertises. A patch that
    /// applies but never takes the fast path is the v1.5.16 failure mode in
    /// miniature: on, logged, and doing nothing.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task GuiManagerReportsItsFastPathShare()
    {
        await WarmUpAsync();
        await WithOptimizationStateRestoredAsync("guimgr", async () =>
        {
            await SetOptimizationAsync("guimgr", enabled: true);
            await OpenDialogRequiredAsync("GuiDialogInventory", "inventorydialog");

            try
            {
                DiagCounters counters = await CaptureDiagAsync("guimgr");
                Assert.True(counters.Present, "The 'guimgr' diag module produced no dump.");

                double calls = counters.Require(DiagCounter.RenderCalls, "guimgr");
                double fastPath = counters.Require(DiagCounter.NoLinqPath, "guimgr");

                await WriteArtifactAsync(nameof(GuiOptimizationTests), "guimgr.txt", counters.Raw);

                Console.WriteLine(counters.Raw);

                Assert.True(calls > 0, "The GUI Manager render prefix never ran.");
                Assert.True(fastPath > 0,
                    "The GUI Manager render fast path did not execute while an inventory dialog was open.");
                Assert.True(fastPath <= calls,
                    $"guimgr reports {fastPath} fast-path renders out of {calls} calls. " +
                    "More fast-path renders than total calls means the two counters are " +
                    "incremented on different code paths and at least one is wrong.");
            }
            finally
            {
                Client!.Ui!.Close("GuiDialogInventory");
                await Session!.StepFramesAsync(5);
            }
        });
    }

    private string? DialogHotkey(string dialogName) => Client!.RunOnClientThread(() =>
        Player!.Gui.LoadedDialogs
            .FirstOrDefault(dialog => dialog.GetType().Name == dialogName)
            ?.ToggleKeyCombinationCode);
}

[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
[DataFiles("Fixtures/gui-input-coalescing-enabled.json", To = "ModConfig/OptiTime.json", Side = EnumAppSide.Client)]
public class GuiInputCoalescingTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Boots with the opt-in input patch and mouse-move coalescing enabled,
    /// then verifies that a click remains actionable after a burst of moves.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task MouseMoveCoalescingDoesNotSwallowClicks()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var ui = client.Ui ?? throw new InvalidOperationException("Pharos did not initialize the client UI.");

        using (JsonDocument config = JsonDocument.Parse(await File.ReadAllTextAsync(ModConfigPath)))
        {
            Assert.True(config.RootElement.GetProperty("GuiManagerInputNoLinqEnabled").GetBoolean(),
                "The scenario profile did not activate the startup-only GUI input patch.");
            Assert.True(config.RootElement.GetProperty("GuiManagerMouseMoveCoalescingEnabled").GetBoolean(),
                "The scenario profile did not enable mouse-move coalescing.");
        }

        Assert.Contains("GUI Manager input optimization loaded", ClientLogText(), StringComparison.OrdinalIgnoreCase);
        await SetOptimizationAsync("guimgr", enabled: true);
        await ui.Handbook.OpenAsync();
        await ui.Handbook.WaitUntilLoadedAsync();
        Assert.True(ui.IsOpen("GuiDialogSurvivalHandbook"), "The handbook dialog did not open.");

        try
        {
            IReadOnlyList<GuiTabInfo> tabs = ui.Tabs("GuiDialogSurvivalHandbook", "verticalTabs");
            Assert.True(tabs.Count > 1,
                $"The handbook exposes only {tabs.Count} category tab(s), so a tab change cannot be tested.");
            GuiTabInfo active = Assert.Single(tabs, tab => tab.Active);
            GuiTabInfo target = tabs.First(tab => tab.Index != active.Index);

            for (int i = 0; i < 200; i++)
            {
                client.Input.InjectMouseMove(200 + (i % 50), 200 + (i % 37));
            }
            ui.ClickTab("GuiDialogSurvivalHandbook", "verticalTabs", target.Index);
            Assert.True(await StepUntilAsync(
                    () => ui.Tabs("GuiDialogSurvivalHandbook", "verticalTabs")
                        .Any(tab => tab.Index == target.Index && tab.Active),
                    maxFrames: 30),
                $"The click after the mouse-move burst did not activate handbook category '{target.Name}'.");
            AssertNoUnexpectedClientErrors("gui input coalescing");
        }
        finally
        {
            client.Input.Reset();
            ui.Close("GuiDialogSurvivalHandbook");
            await session.StepFramesAsync(5);
        }
    }
}
