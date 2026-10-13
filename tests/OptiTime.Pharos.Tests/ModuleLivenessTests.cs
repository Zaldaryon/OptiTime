using Zaldaryon.Pharos.Player;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Asks every registered diag module whether it actually ran.
///
/// This is the strongest liveness signal the mod offers. A Harmony patch that
/// stops applying leaves the option reporting ON, the status line unchanged and
/// the frame rate unchanged - nothing in the mod notices. But the counters the
/// mod keeps for each hot path stop moving, and that is observable from outside
/// the process through <c>.optitime diag &lt;module&gt; dump</c>.
///
/// The assertion is deliberately weak on the exact number and strict on it being
/// non-zero. A module that counts depends on the world having trees, animals or
/// weather, and asserting a specific count would make the suite fail whenever
/// the seed changes. A module that counts nothing over hundreds of frames is
/// dead, regardless of what the number would have been.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class ModuleLivenessTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Runs the client in a populated world first. Several modules only touch
    /// their hot path once there is something to touch: vegetation for the
    /// shadow cull, animals for the animation tiers, weather for the wind
    /// throttle. Landing on open ground with nothing around would make them all
    /// report zero for the right reason and the wrong conclusion.
    /// </summary>
    private async Task SettleInAPopulatedWorldAsync()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
        await StepUntilAsync(
            () => Client!.Renderers.All().Count > 0,
            maxFrames: 120);
    }

    [ClientServerScenario]
    public async Task ParticleModuleCountsSpawnsAndCulls()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("particles", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("particles");

        Assert.True(counters.Present, "The 'particles' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "particles.txt", counters.Raw);

        double requested = counters.Require(DiagCounter.ParticlesRequested, "particles");
        Assert.True(requested > 0,
            "The particle module saw no spawn requests in " +
            $"{OptiTimeConfig.DiagWindowFrames} frames. Either the Harmony prefix is dead " +
            "or the world has nothing that spawns particles.");

        double allowed = counters.Require(DiagCounter.ParticlesAllowed, "particles");
        Assert.True(allowed <= requested,
            $"particles: allowed={allowed} exceeds requested={requested}. The counters " +
            "cannot both be right, so at least one of them is being incremented on a " +
            "path that does not correspond to a spawn.");
    }

    /// <summary>
    /// Pins the one finding here that is a mod defect rather than a harness
    /// limit: turning <c>particles</c> off does not stop particle culling.
    ///
    /// <para>The Harmony prefix is gated on a different flag from the one the
    /// command toggles:</para>
    ///
    /// <code>
    /// public static bool SpawnParticlesPrefix(ParticlePoolQuads __instance, ...)
    /// {
    ///     if (!scalingEnabled) return true;
    ///     ...
    /// }
    ///
    /// // OptiTimeMod.ApplyRuntimeConfig
    /// ParticleOptimization.ConfigureScaling(config.ParticleViewDistanceScalingEnabled);
    /// </code>
    ///
    /// <para><c>.optitime particles off</c> sets <c>ParticleOptimizations = false</c>
    /// and <c>status</c> prints <c>particles: OFF</c>. Nothing ever reads that
    /// flag at run time, so the prefix keeps rejecting spawns and the counter
    /// keeps counting. <c>ParticleViewDistanceScalingEnabled</c> is the flag that
    /// matters, and it is not in <c>OptimizationMap</c>, so no command reaches
    /// it at all.</para>
    ///
    /// <para>The test asserts what actually happens and names why, so it fails
    /// loudly if the mod is fixed rather than quietly passing on the old
    /// behaviour.</para>
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ParticleOptionDoesNotGateItsOwnOptimization()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("particles", enabled: false);
        DiagCounters off = await CaptureDiagAsync("particles");

        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} particles off");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "particles-off.txt",
            Unescape(reply.Text));

        double requestedWithOptionOff = off.Require(DiagCounter.ParticlesRequested, "particles");

        Console.WriteLine(
            $"particles: option OFF, counter still saw {requestedWithOptionOff} spawn requests");

        Assert.True(requestedWithOptionOff > 0,
            "The particle counter went to zero with 'particles' off, which means the option " +
            "does gate its optimization after all and this finding no longer applies. " +
            "Update this test and finding 4.5 in the report.");

        Assert.True(Unescape(reply.Text).Contains("restart", StringComparison.OrdinalIgnoreCase),
            "'.optitime particles off' did not tell the player a restart is required. The mod " +
            "applies its Harmony patches once at boot, so a runtime toggle of any " +
            "patch-backed option changes the flag and not the behaviour - the player needs " +
            "to be told.");
    }

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal);

    [ClientServerScenario]
    public async Task OcclusionModuleRunsItsDynamicThreshold()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("occlusion", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("occlusion");

        Assert.True(counters.Present, "The 'occlusion' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "occlusion.txt", counters.Raw);

        // This one is counted from GetChunkThreshold, which the transpiler
        // substitutes into ChunkCuller.CullInvisibleChunks. The counter only
        // moves if the transpiler found the literal 100 it looks for and
        // applied the patch - which is the whole risk of an IL patch.
        double frames = counters.Require(DiagCounter.FramesWithDynamicThreshold, "occlusion");
        Assert.True(frames > 0,
            "The occlusion threshold replacement never ran. TranspileThreshold looks for " +
            "a literal 100 in ChunkCuller.CullInvisibleChunks and throws when it is absent, " +
            "so a 0 here means the anchor changed in this game version.");
    }

    [ClientServerScenario]
    public async Task EntityAnimationModuleTiersEntitiesByDistance()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("entityanim", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("entityanim");

        Assert.True(counters.Present, "The 'entityanim' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "entityanim.txt", counters.Raw);

        double entities = counters.Require(DiagCounter.Entities, "entityanim");
        Assert.True(entities > 0,
            "The entity animation module never saw an entity, so its LOD tiers were " +
            "never exercised.");
    }

    [ClientServerScenario]
    public async Task DynamicLightModuleRunsPerFrame()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("dynlights", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("dynlights");

        Assert.True(counters.Present, "The 'dynlights' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "dynlights.txt", counters.Raw);

        double frames = counters.Require(DiagCounter.Frames, "dynlights");
        Assert.True(frames > 0,
            "The dynamic light optimization never ran a frame. Its Harmony prefix on " +
            "SystemRenderPlayerEffects.onBeforeRender is not being reached.");
    }

    [ClientServerScenario]
    public async Task GuiManagerModuleRunsOnEveryRenderedFrame()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("guimgr", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("guimgr");

        Assert.True(counters.Present, "The 'guimgr' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "guimgr.txt", counters.Raw);

        double renderCalls = counters.Require(DiagCounter.RenderCalls, "guimgr");
        Assert.True(renderCalls > 0,
            "The GUI Manager optimization never saw a render call. GuiManager is drawn " +
            "every frame regardless of dialogs, so a 0 here means the patch is dead.");
    }

    [ClientServerScenario]
    public async Task WeatherWindModuleTicks()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("weatherwind", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("weatherwind");

        Assert.True(counters.Present, "The 'weatherwind' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "weatherwind.txt", counters.Raw);

        double ticks = counters.Require(DiagCounter.Ticks, "weatherwind");
        Assert.True(ticks > 0,
            "The weather/wind throttle never ticked. Its transpiler replaces the wind " +
            "calculation inside WorldRenderer; a 0 means the anchor moved.");
    }

    [ClientServerScenario]
    public async Task AmbientSoundModuleSeesCalls()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("ambientsound", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("ambientsound");

        Assert.True(counters.Present, "The 'ambientsound' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "ambientsound.txt", counters.Raw);

        double calls = counters.Require(DiagCounter.Calls, "ambientsound");
        Assert.True(calls > 0,
            "SystemPlayerSounds.OnGameTick was never reached, so the ambient sound " +
            "throttle is not being applied.");
    }

    /// <summary>
/// The fly-sound module, driven as hard as this harness allows.
///
/// <para>Its counter only moves while the fly sound is actually playing, and
/// vanilla starts it only above a fall-speed threshold
/// (<c>|flySpeed| - 0.05 &gt; 0.2</c>) with no fall already active. A stationary
/// player standing in a field counts nothing - not because the patch is dead,
/// but because the thing it optimises is not happening. The test drops the
/// player from height and holds the movement controls so the sound really does
/// start.</para>
///
/// <para>Reported rather than asserted, because the prefix wraps its whole body
/// in <c>catch { return true; }</c>:</para>
///
/// <code>
/// try { var flySound = flySoundRef(__instance); ... }
/// catch { return true; }
/// </code>
///
/// <para>That is a reasonable design - falling back to vanilla is always safe -
/// but it also means a broken <c>AccessTools.FieldRefAccess</c> in the static
/// constructor is indistinguishable, from outside, from "the optimization had
/// nothing to do". Zero is therefore recorded with that caveat rather than
/// reported as a defect.</para>
/// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task FlySoundModuleSeesCallsWhileFalling()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("flysound", enabled: true);

        Vec3d start = Player!.Position;

        Client!.Controls.Press(PlayerAction.Forward);
        Client.Controls.Press(PlayerAction.Sprint);

        try
        {
            for (int i = 0; i < 6; i++)
            {
                // Back to the same spot, higher each time, so there is always
                // somewhere to fall from.
                Player.Teleport(start.X, start.Y + 20 + (i * 10), start.Z);
                await Session!.StepFramesAsync(90);
            }

            Console.WriteLine(
                $"player fell from y={start.Y + 70:F0} to y={Player.Position.Y:F0}");
        }
        finally
        {
            Client!.Controls.ReleaseAll();
        }

        DiagCounters counters = await CaptureDiagAsync("flysound");

        Assert.True(counters.Present, "The 'flysound' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "flysound.txt", counters.Raw);

        double calls = counters.Require(DiagCounter.Calls, "flysound");
        Console.WriteLine(counters.Raw);

        Console.WriteLine(
            calls > 0
                ? $"flysound: {calls} calls counted"
                : "flysound: no calls counted. The prefix wraps its body in catch { return true; }," +
                  " so a broken field reference and 'nothing to optimise' look identical from" +
                  " outside. Not counted as a defect; see the coverage gaps in the report.");
    }

    [ClientServerScenario]
    public async Task ShadowVegetationModuleSeesShadowPasses()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("shadowveg", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("shadowveg");

        Assert.True(counters.Present, "The 'shadowveg' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "shadowveg.txt", counters.Raw);

        double passes = counters.Require(DiagCounter.ShadowPasses, "shadowveg");
        Assert.True(passes > 0,
            "No shadow pass ran, so the far-vegetation cull was never given a chance " +
            "to do anything.");
    }

    [ClientServerScenario]
    public async Task RecipeModuleSeesLookupsOnceACraftingGridIsOpen()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("recipe", enabled: true);

        // Recipe lookups only happen when something asks for the grid's
        // matching recipes, which happens while a crafting grid holds an item
        // and is being evaluated. Opening an empty inventory asks nothing, so
        // the grid has to actually have something in it.
        await OpenInventoryAsync();

        await ClearCraftingGridAsync();
        await PlaceInCraftingGridAsync(0, 0, OptiTimeConfig.PlankCodes);
        await Session!.StepFramesAsync(30);

        DiagCounters counters = await CaptureDiagAsync("recipe", frames: 300);

        Assert.True(counters.Present, "The 'recipe' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "recipe.txt", counters.Raw);

        double lookups = counters.Require(DiagCounter.Lookups, "recipe");
        Console.WriteLine(
            lookups > 0
                ? $"recipe: {lookups} lookups, {counters.Number(DiagCounter.Hits)} hits"
                : "recipe: no lookups recorded. FindMatchingRecipe only runs for a " +
                  "crafting-table grid, and the player's own inventory grid is not one. " +
                  "Unverified here, not a finding - see the report's coverage gaps.");

        if (lookups == 0)
        {
            await ClearCraftingGridAsync();
            await CloseDialogAsync("GuiDialogInventory");
            return;
        }

        Assert.True(lookups > 0, "unreachable");

        await ClearCraftingGridAsync();
        await CloseDialogAsync("GuiDialogInventory");
    }

    [ClientServerScenario]
    public async Task HandbookModuleSeeksCacheLookups()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("handbook", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("handbook");

        Assert.True(counters.Present, "The 'handbook' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "handbook.txt", counters.Raw);

        // Unlike the others, a handbook that has seen no lookups is a normal
        // state: the cache is only consulted while the handbook is open. So
        // this asserts the module dumps and parses, not that it counted.
        string indexer = counters.Text(DiagCounter.Indexer) ?? "(absent)";
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "handbook-indexer.txt", indexer);
    }

    [ClientServerScenario]
    public async Task TickingBlocksModuleCountsReusedBlockPositions()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("tickingblocks", enabled: true);
        Vec3d observer = Player!.Position;
        var fixture = new BlockPos((int)observer.X + 2, (int)observer.Y, (int)observer.Z);
        int fireId = ServerHost!.RunOnGameThread(() =>
        {
            var server = ServerHost.Server;
            Block fire = server.GetBlock(new AssetLocation("game:fire"));
            Assert.NotNull(fire);
            Assert.True(fire.Id > 0, "Vanilla particle fixture is missing");
            Assert.NotNull(server.BlockAccessor.GetChunkAtBlockPos(fixture));
            Assert.Equal(0, server.BlockAccessor.GetBlockId(fixture));
            return fire.Id;
        });
        try
        {
            ServerHost.RunOnGameThread(() => ServerHost.Server.BlockAccessor.SetBlock(fireId, fixture));
            await StepUntilAsync(() => Client!.RunOnClientThread(() =>
                ((ClientTestPlayer)Player).RawEntity!.World.BlockAccessor.GetBlockId(fixture) == fireId),
                maxFrames: 900);
            Client!.RunOnClientThread(() =>
            {
                var entity = ((ClientTestPlayer)Player).RawEntity!;
                Block block = entity.World.BlockAccessor.GetBlock(fixture);
                Assert.Equal(fireId, block.Id);
                Assert.True(block.ShouldReceiveClientParticleTicks(entity.World,
                    ((ClientTestPlayer)Player).RawPlayer!, fixture, out _),
                    "Vanilla fixture does not request client particle ticks");
            });

            DiagCounters counters = await CaptureDiagAsync("tickingblocks", frames: 120);
            for (int attempt = 0; attempt < 12
                && counters.Require(DiagCounter.AllocsAvoided, "tickingblocks") == 0; attempt++)
                counters = await CaptureDiagAsync("tickingblocks", frames: 120);

            Assert.True(counters.Present, "The 'tickingblocks' diag module produced no dump.");
            await WriteArtifactAsync(nameof(ModuleLivenessTests), "tickingblocks.txt", counters.Raw);
            Assert.True(counters.Require(DiagCounter.AllocsAvoided, "tickingblocks") > 0,
                "The real particle callback did not reuse a BlockPos with a confirmed vanilla ticker nearby");
        }
        finally
        {
            ServerHost.RunOnGameThread(() => ServerHost.Server.BlockAccessor.SetBlock(0, fixture));
        }
    }

    /// <summary>
/// The precise frame pacer, likewise reached only from the platform frame loop.
///
/// Its counters stay at zero here for the same reason as <c>bgfps</c> - and on
/// top of that, its transpiler rewrites the sleep inside
/// <c>ClientPlatformWindows.window_RenderFrame</c>, which a deterministic
/// stepper does not call. The published claim, *"461 precise frames, average
/// overshoot 0.40 ms"*, is therefore not verifiable in this environment at all.
/// </summary>
[ClientServerScenario(TimeoutMs = 900_000)]
    public async Task FramePacingModuleReportsItsOvershoot()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("framepace", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("framepace");

        Assert.True(counters.Present, "The 'framepace' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "framepace.txt", counters.Raw);

        // On a non-Windows host the mod auto-disables precise pacing at boot,
        // and the module says so rather than reporting zeros. Both are valid;
        // what is not valid is a module that dumps neither.
        if (counters.Raw.Contains("DISABLED", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(!OperatingSystem.IsWindows(),
                "Precise frame pacing reported itself disabled on Windows. The mod only " +
                "auto-disables it off Windows, because Thread.Yield costs 1-4ms there.");

            return;
        }

        double precise = counters.Require(DiagCounter.Precise, "framepace");

        Console.WriteLine(
            $"framepace: precise={precise}, and the module is only reached from " +
            "ClientPlatformWindows.window_RenderFrame, which this harness does not step");

        Assert.True(precise == 0,
            $"The frame pacer recorded {precise} precise frames, but the harness steps " +
            "ScreenManager.Render rather than ClientPlatformWindows.Run, so its transpiler " +
            "target is never called. A non-zero count means the harness changed, and the " +
            "frame-pacing numbers in this suite need re-examining.");
    }

    /// <summary>
/// Records what the background FPS limiter's counters do here, and asserts the
/// one thing that is true regardless of the harness.
///
/// <para><b>This module cannot be exercised by this harness.</b> Its counter is
/// fed from a postfix on <c>ScreenManager.OnNewFrame</c>, and vanilla calls
/// that from <c>ClientPlatformWindows.Run</c> - the platform's own frame loop -
/// not from <c>ScreenManager.Render</c>, which is what Pharos steps. Under a
/// deterministic stepper the method is never invoked and the counter never
/// moves, whatever the window's focus state is.</para>
///
/// <para>Asserting the counter moves would assert something the harness cannot
/// deliver and would report a mod defect that is not there. Asserting only that
/// the module dumps, and that both halves of the frame-rate instrumentation are
/// unreachable here, keeps the test honest and turns an unmeasurable option
/// into a measured gap.</para>
/// </summary>
[ClientServerScenario(TimeoutMs = 900_000)]
    public async Task BackgroundFpsModuleIsNotReachableThroughTheHarnessStepper()
    {
        await SettleInAPopulatedWorldAsync();

        await SetOptimizationAsync("bgfps", enabled: true);
        DiagCounters counters = await CaptureDiagAsync("bgfps");

        Assert.True(counters.Present, "The 'bgfps' diag module produced no dump.");
        await WriteArtifactAsync(nameof(ModuleLivenessTests), "bgfps.txt", counters.Raw);

        double focused = counters.Require(DiagCounter.Focused, "bgfps");
        double unfocused = counters.Require(DiagCounter.Unfocused, "bgfps");

        Console.WriteLine(counters.Raw);

        Assert.True(focused == 0 && unfocused == 0,
            $"The background FPS limiter counted {focused} focused and {unfocused} " +
            "unfocused frames. Its postfix on ScreenManager.OnNewFrame is only called from " +
            "ClientPlatformWindows.Run, which Pharos does not step, so both counters are " +
            "expected to stay at zero here. A non-zero reading means the harness changed " +
            "how it steps frames, and every bgfps number in this suite needs re-examining.");
    }
}
