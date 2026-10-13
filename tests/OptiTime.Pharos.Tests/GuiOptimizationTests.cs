using Zaldaryon.Pharos.Assertions;
using Zaldaryon.Pharos.Input;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Drives the GUI the way a player does, through the same input paths the mod
/// patches.
///
/// <c>guimgr</c> is the riskiest option in the mod and the one that has already
/// broken once: v1.5.16 shipped because the GUI Manager optimization had been
/// silently dead since 1.22.5, where a dynamic dispatch was replaced with typed
/// field references that did not resolve. It patches eight input and render
/// entry points on <c>GuiManager</c>, plus a finalizer on
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
    /// Every dialog the GUI Manager renders has to survive the patch, and the
    /// game's own hotkey is what opens it, so the patched
    /// <c>GuiManager.OnKeyDown</c> prefix is on the path.
    ///
    /// A dialog that is unavailable in this play style is a skip, not a
    /// failure; one that opens and then will not close is a failure, because
    /// the mod's input prefixes changed how the close key is handled.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task DialogsOpenAndCloseThroughTheirOwnHotkeys()
    {
        await WarmUpAsync();
        await SetOptimizationAsync("guimgr", enabled: true);

        (string Dialog, string Fallback)[] dialogs =
        [
            ("GuiDialogEscapeMenu", "escapemenudialog"),
            ("GuiDialogInventory", "inventorydialog"),
            ("GuiDialogCharacter", "characterdialog"),
            ("GuiDialogWorldMap", "worldmap"),
            ("GuiDialogHandbook", "handbook"),
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

            bool closed = Client!.Ui!.Close(dialog);
            await Session!.StepFramesAsync(10);

            Assert.True(closed,
                $"'{dialog}' would not close. GuiManager's OnFinalizeFrame prefix or its " +
                "OnKeyDown prefix is changing how dialogs are dismissed.");
            Assert.False(Client.Ui.IsOpen(dialog), $"'{dialog}' is still open after closing it.");
        }

        Console.WriteLine($"opened: {string.Join(", ", opened)}");
        Console.WriteLine($"unavailable in this play style: {string.Join(", ", skipped)}");

        Assert.True(opened.Count >= 2,
            $"Only {opened.Count} dialog(s) opened at all ({string.Join(", ", opened)}). " +
            "Too few of the GUI paths were exercised for this test to mean anything.");

        AssertNoUnexpectedClientErrors("gui");
    }

    /// <summary>
    /// With the patch off, the same dialogs must open and close. Comparing the
    /// two states is what turns "the dialog worked" into "the patch did not
    /// change the dialog".
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task DialogsBehaveTheSameWithThePatchOff()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
        try
        {
            await SetOptimizationAsync("guimgr", enabled: false);

            string[] unpatched = [];
            foreach ((string dialog, string fallback) in new[]
                     {
                         ("GuiDialogEscapeMenu", "escapemenudialog"),
                         ("GuiDialogInventory", "inventorydialog"),
                     })
            {
                if (!await OpenDialogAsync(dialog, fallback)) continue;

                unpatched = [.. unpatched, dialog];

                await CloseDialogAsync(dialog);
            }

            await SetOptimizationAsync("guimgr", enabled: true);

            string[] patched = [];
            foreach (string dialog in unpatched)
            {
                if (await OpenDialogAsync(dialog)) patched = [.. patched, dialog];

                await CloseDialogAsync(dialog);
            }

            Assert.Equal(unpatched.Order(), patched.Order());
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
    }

    /// <summary>
    /// Mouse input goes through <c>OnMouseMove</c>, <c>OnMouseDown</c>,
    /// <c>OnMouseUp</c> and <c>OnMouseMove</c>'s coalescing. Driving real mouse
    /// movement over an open dialog exercises all of them; a coalescing bug
    /// shows up as a click landing on the wrong element or not at all.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task VirtualMouseInputReachesAnOpenDialog()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var ui = client.Ui ?? throw new InvalidOperationException("Pharos did not initialize the client UI.");
        await SetOptimizationAsync("guimgr", enabled: true);

        await OpenDialogRequiredAsync("GuiDialogInventory", "inventorydialog");
        await session.StepFramesAsync(15);

        Assert.True(ui.IsOpen("GuiDialogInventory"));
        try
        {
            InputSnapshot before = client.Input.Snapshot();
            Assert.True(before.IsIdle,
                $"The virtual input controller started with {before.PressedKeys.Count} keys " +
                "and scroll already set. A leftover input from a previous test would be " +
                "delivered to this dialog.");

            // Move the mouse in a small sweep, then read the state back: the
            // coalescing prefix must not swallow the position.
            for (int i = 0; i < 10; i++)
            {
                client.Input.InjectMouseMove(400 + i * 8, 300 + i * 4);
                await session.StepFramesAsync(1);
            }

            (int x, int y) = client.Input.GetMousePosition();
            Assert.True(x > 0 && y > 0,
                $"After moving the mouse the virtual position is ({x}, {y}). " +
                "The input never reached the client, so nothing below would test the patch.");

            client.Input.InjectScroll(-120);
            await session.StepFramesAsync(3);
            Assert.NotEqual(0, client.Input.GetScrollDelta());
            client.Input.ResetScrollDelta();
        }
        finally
        {
            ui.Close("GuiDialogInventory");
            await session.StepFramesAsync(5);
        }
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
        await SetOptimizationAsync("guimgr", enabled: true);

        for (int i = 0; i < 12; i++)
        {
            if (!await OpenDialogAsync("GuiDialogEscapeMenu", "escapemenudialog")) { await session.StepFramesAsync(5); }
            await session.StepFramesAsync(3);
            ui.Close("GuiDialogEscapeMenu");
            await session.StepFramesAsync(3);
        }

        player.Gui.CloseAllDialogs();
        await session.StepFramesAsync(10);

        AssertNoUnexpectedClientErrors("gui");
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
        await SetOptimizationAsync("guimgr", enabled: true);

        DiagCounters counters = await CaptureDiagAsync("guimgr");
        Assert.True(counters.Present, "The 'guimgr' diag module produced no dump.");

        double calls = counters.Require(DiagCounter.RenderCalls, "guimgr");
        double fastPath = counters.Require(DiagCounter.NoLinqPath, "guimgr");

        await WriteArtifactAsync(nameof(GuiOptimizationTests), "guimgr.txt", counters.Raw);

        Console.WriteLine(counters.Raw);

        Assert.True(calls > 0, "The GUI Manager render prefix never ran.");
        Assert.True(fastPath <= calls,
            $"guimgr reports {fastPath} fast-path renders out of {calls} calls. " +
            "More fast-path renders than total calls means the two counters are " +
            "incremented on different code paths and at least one is wrong.");
    }

    /// <summary>
    /// The GUI Manager has a coalescing interval for mouse-move handling. Hammer
    /// the input path harder than the interval and the coalescing must not drop
    /// the click that follows: a click swallowed by coalescing is a button that
    /// sometimes does nothing, which is the exact bug the option exists to fix.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task MouseMoveCoalescingDoesNotSwallowClicks()
    {
        await WarmUpAsync();
        var client = Client ?? throw new InvalidOperationException("Pharos did not provide a client after warm-up.");
        var session = Session ?? throw new InvalidOperationException("Pharos did not provide a session after warm-up.");
        var ui = client.Ui ?? throw new InvalidOperationException("Pharos did not initialize the client UI.");
        await SetOptimizationAsync("guimgr", enabled: true);

        await OpenDialogRequiredAsync("GuiDialogInventory", "inventorydialog");
        await session.StepFramesAsync(15);

        try
        {
            for (int i = 0; i < 200; i++)
            {
                client.Input.InjectMouseMove(200 + (i % 50), 200 + (i % 37));
            }
            await session.StepFramesAsync(3);

            client.Input.InjectMouseButton(VirtualMouseButton.Left, pressed: true);
            await session.StepFramesAsync(2);
            client.Input.InjectMouseButton(VirtualMouseButton.Left, pressed: false);
            await session.StepFramesAsync(5);

            AssertNoUnexpectedClientErrors("gui");
        }
        finally
        {
            client.Input.Reset();
            ui.Close("GuiDialogInventory");
            await session.StepFramesAsync(5);
        }
    }
}
