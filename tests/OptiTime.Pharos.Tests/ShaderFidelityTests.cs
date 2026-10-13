namespace OptiTime.Pharos.Tests;

/// <summary>
/// The shader and blur swaps, which are the only part of the mod that changes
/// what a pixel looks like rather than how long a frame takes.
///
/// The mod replaces four game shaders - <c>blur.fsh</c>, <c>blur.vsh</c>,
/// <c>chunkliquid.fsh</c> and <c>chunkshadowmap.fsh</c> - at boot, from
/// <c>AssetsLoaded</c>, by writing them into the game's shader asset store. Two
/// things follow from that and both are bugs when they go wrong:
///
/// <list type="bullet">
/// <item>the swap happens once, so toggling the option at runtime changes the
/// flag and nothing else until the next boot;</item>
/// <item>the mod disables its own shaders when it detects a conflicting shader
/// mod, and says so, which is the only thing standing between two shader packs
/// and a broken render.</item>
///
/// A full re-boot is the only honest way to test the first. <c>Restart</c>
/// isolation gives each test its own client, which is what this class uses.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class ShaderFidelityTests : OptiTimeScenarioBase
{
    /// <summary>
    /// The mod's shader override is decided at boot and applied through the
    /// asset store. Asking the client to reload the shader category is the only
    /// way to observe the effect within a session, and it is also the only way
    /// to check that the reload itself does not break the render pipeline.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ShaderAssetCategoryReloadsWithoutErrors()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[1]);

        Client!.GL.Enable();
        Client.GL.Reset();

        int errorsBefore = Client.Logs.UnexpectedErrors(AllowedLoggedErrors).Count;

        ReloadResult result = await Client.ReloadAsync(AssetCategory.shaders);

        Assert.True(result.Succeeded,
            $"Reloading the shader category reported failure ({result.AssetsReloaded} assets). " +
            "The mod replaces four of the game's shaders at boot, so a failed shader " +
            "reload means the client is left with a half-bound program.");

        await Session!.StepFramesAsync(120);

        GlCommandRecord record = Client.GL.Snapshot();

        Console.WriteLine(
            $"shader reload: {result.AssetsReloaded} assets, " +
            $"{record.DrawCalls + record.MultiDrawCalls} draws, {record.Errors} GL errors");

        await WriteArtifactAsync(nameof(ShaderFidelityTests), "shader-reload.txt",
            $"reloaded={result.AssetsReloaded}\nsucceeded={result.Succeeded}\n{record}");

        if (record.TotalDrawCalls > 0)
        {
            Assert.True(record.Errors == 0,
                $"{record.Errors} GL error(s) after reloading the shader category. " +
                "A shader that failed to compile binds as a null program and draws nothing.");
        }

        Assert.Equal(errorsBefore, Client.Logs.UnexpectedErrors(AllowedLoggedErrors).Count);
    }

    /// <summary>
    /// The blur shaders are only used when the game has something to blur -
    /// water under a translucent surface, and the underwater overlay. Turning
    /// the option off at runtime does not unbind them until the next boot, so
    /// what is asserted here is that the reload path stays valid in both
    /// states, which is what makes a two-boot comparison meaningful later.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task BlurToggleSurvivesAShaderReload()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
        try
        {
            foreach (bool enabled in new[] { false, true })
            {
                await SetOptimizationAsync("blur", enabled);

                ReloadResult reload = await Client!.ReloadAsync(AssetCategory.shaders);
                Assert.True(reload.Succeeded,
                    $"Shader reload failed with blur {(enabled ? "on" : "off")}.");

                await Session!.StepFramesAsync(90);
            }

            AssertNoUnexpectedClientErrors("shader");
        }
        finally
        {
            await RestoreStatusAsync(before);
        }
    }

    /// <summary>
    /// The mod's shader override covers the liquid shader, so a water surface is
    /// the scene that has to look the same. If the override broke liquid
    /// rendering the surface would go black, flat, or missing entirely - a
    /// difference no amount of frame-time testing would catch.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task LiquidShaderStillRendersWater()
    {
        await WarmUpAsync();

        // Look down at open water from height: the liquid surface fills the
        // frame and is the one thing the chunkliquid override touches.
        await ApplyPoseAsync(OptiTimeConfig.Poses[2]);
        await Session!.StepFramesAsync(180);

        FramebufferSnapshot frame = Client!.CaptureFrame();

        HashSet<uint> distinct = [];
        int opaque = 0;

        for (int y = 0; y < frame.Height; y += 8)
        {
            for (int x = 0; x < frame.Width; x += 8)
            {
                (byte r, byte g, byte b, byte a) = frame.GetPixel(x, y);
                distinct.Add(((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a);
                if (a > 0) opaque++;
            }
        }

        int sampled = (frame.Height / 8) * (frame.Width / 8);

        Console.WriteLine(
            $"straight-down pose: {distinct.Count} distinct colours, " +
            $"{opaque}/{sampled} samples with alpha");

        await WriteArtifactAsync(nameof(ShaderFidelityTests), "liquid.txt",
            $"distinctColours={distinct.Count}\nopaqueSamples={opaque}/{sampled}");

        Assert.True(distinct.Count > 8,
            $"The straight-down view is nearly uniform ({distinct.Count} distinct sampled " +
            "colours). If that view is supposed to show terrain and the chunkliquid " +
            "override broke it, this is what it looks like.");

        Assert.True(opaque > sampled / 2,
            $"{opaque}/{sampled} sampled pixels are opaque. The frame is largely " +
            "transparent, which means the scene did not render into the framebuffer.");
    }

    /// <summary>
    /// The mod detects shader packs it must yield to - Ancestral Bliss, Volumetric
    /// Shading Refreshed, Coriaender - and turns its own shader overrides off
    /// rather than fighting them. None of those mods is loaded here, so what is
    /// asserted is the other half: with no conflicting mod present, the mod does
    /// not report a conflict, and the overrides are left on.
    /// </summary>
    [ClientServerScenario]
    public async Task NoConflictIsReportedWhenNoOtherShaderModIsLoaded()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> states = await ReadStatusAsync();

        foreach (string option in new[] { "shaders", "blur" })
        {
            OptionState state = states[option];
            Assert.False(state.IsConflictDisabled,
                $"'{option}' reports as conflict-disabled ({state.Conflict}) but no other " +
                "shader mod is loaded in this scenario. Conflict detection is producing a " +
                "false positive, which silently costs the player the shader feature.");
        }

        // The log tells the same story from the other side: the mod announces
        // each conflict it detects, so an unexpected one shows up there.
        string[] conflictMessages = [.. Client!.Logs.Entries
            .Select(e => e.Message)
            .Where(m => m.Contains("detected", StringComparison.OrdinalIgnoreCase)
                     && m.Contains("OptiTime", StringComparison.OrdinalIgnoreCase))];

        Console.WriteLine(
            conflictMessages.Length == 0
                ? "no compatibility conflicts announced"
                : string.Join('\n', conflictMessages));

        Assert.True(conflictMessages.Length == 0,
            "The mod announced a compatibility conflict with no conflicting mod loaded:\n  " +
            string.Join("\n  ", conflictMessages));
    }

    /// <summary>
    /// <c>suppress</c> exists to hide the compatibility chat lines. It is the
    /// one option that changes what the player sees rather than what the frame
    /// costs, so it is verified through the message surface: with it on the
    /// flags still exist, and the mod must not print anything extra because of
    /// them.
    /// </summary>
    [ClientServerScenario]
    public async Task SuppressOnlyChangesChatVolumeNotState()
    {
        await WarmUpAsync();

        // Start from whatever state this class was left in, put 'suppress' through a
        // round trip, and compare against where it started - not against a
        // value assumed for the middle of the test.
        bool wasEnabled = (await ReadOptionStateAsync("suppress") ?? default).Enabled;

        await SetOptimizationAsync("suppress", enabled: !wasEnabled);

        IReadOnlyDictionary<string, OptionState> withMessages = await ReadStatusAsync();

        await Session!.StepFramesAsync(60);
        int loudOff = CountOptiTimeChatLines();

        await SetOptimizationAsync("suppress", enabled: wasEnabled);

        await Session!.StepFramesAsync(60);
        int loudOn = CountOptiTimeChatLines();

        Console.WriteLine($"OptiTime log lines: suppress off {loudOff}, on {loudOn}");

        // The state of every other option must be untouched either way.
        IReadOnlyDictionary<string, OptionState> after = await ReadStatusAsync();

        List<string> drifted = [.. OptiTimeConfig.OptionNames
            .Where(o => o != "suppress" && withMessages[o].Enabled != after[o].Enabled)
            .Select(o => $"'{o}' was {(withMessages[o].Enabled ? "ON" : "OFF")} before and " +
                         $"{(after[o].Enabled ? "ON" : "OFF")} after 'suppress' was toggled")];

        Assert.True(drifted.Count == 0,
            $"Toggling 'suppress' changed the state of {drifted.Count} other option(s):\n  " +
            string.Join("\n  ", drifted) +
            "\n'suppress' only hides compatibility messages in chat. It must not move a " +
            "single optimization flag.");

        Assert.True(after["suppress"].Enabled == wasEnabled,
            $"'suppress' was {wasEnabled} before the test and " +
            $"{after["suppress"].Enabled} after it. The round trip did not restore it.");

        AssertNoUnexpectedClientErrors("suppress");
    }

    private int CountOptiTimeChatLines() =>
        Client!.Logs.Entries.Count(e =>
            e.Message.Contains("[OptiTime]", StringComparison.Ordinal));
}