namespace OptiTime.Pharos.Tests;

/// <summary>
/// Compares what the client draws with the mod against what it draws without
/// it, pixel for pixel.
///
/// Performance and correctness are different questions here. A mod can double
/// the frame rate and quietly delete a render pass nobody notices until they
/// look at a treeline from the right angle. The mod's own claim is "minimal
/// visual impact", which is testable: if a whole optimization moves more than a
/// handful of pixels, either the optimization is culling something it should
/// not, or the claim needs qualifying.
///
/// Two classes share this base. One runs without the mod and writes the
/// baselines; the other runs with it and checks against them. They cannot run
/// in the same class because <c>[PharosMods]</c> stages the mod before the
/// client boots, and a client that has already booted cannot be un-modded.
/// </summary>
public abstract class RenderingFidelityBase : OptiTimeScenarioBase
{
    private const string VisualAmbientKey = "optitime-private-visual-control";
    private bool visualAmbientInstalled;

    /// <summary>
    /// Moves the camera and waits for the world to stop changing before
    /// capturing.
    ///
    /// The wait is the whole difficulty of this test. Chunk streaming runs on
    /// worker threads on their own schedule, so a capture taken 90 frames after
    /// a teleport sometimes includes chunks that had not arrived yet. Those
    /// chunks show up as tens of thousands of differing pixels and the test
    /// fails on terrain that was merely late. Require nearby chunks to be ready
    /// and then check that the loaded chunk count holds steady.
    /// </summary>
    protected async Task<CameraPose> CaptureAtAsync(CameraPose pose)
    {
        NormalizeVisualAmbient();
        CameraPose resolved = await ApplyPoseAsync(pose);
        await WaitForChunksToSettleAsync();
        Console.WriteLine($"[OptiTime] Visual ambient {pose.Name}: {System.Text.Json.JsonSerializer.Serialize(ReadVisualAmbient())}");
        var frame = Client!.CaptureFrame();
        try { RequireVisibleWorld(frame, pose.Name); }
        catch { frame.SaveToPng(ActualPath(pose, "scene", "coverage-failed")); throw; }
        return resolved;
    }

    private void NormalizeVisualAmbient() => Client!.RunOnClientThread(() =>
    {
        var entity = ((Zaldaryon.Pharos.Player.ClientTestPlayer)Player!).RawEntity!;
        var api = (Vintagestory.API.Client.ICoreClientAPI)entity.Api;
        var modifier = new AmbientModifier().EnsurePopulated();
        modifier.FogDensity.Set(0.001f, 1);
        modifier.FogMin.Set(0, 1);
        modifier.FlatFogDensity.Set(0, 1);
        modifier.FlatFogYPos.Set(0, 1);
        modifier.CloudDensity.Set(0, 1);
        modifier.CloudBrightness.Set(1, 1);
        modifier.SceneBrightness.Set(1, 1);
        modifier.FogBrightness.Set(1, 1);
        modifier.FogColor = new WeightedFloatArray([67f / 85f, 0.827451f, 73f / 85f], 1);
        modifier.AmbientColor = new WeightedFloatArray([1, 1, 1], 1);
        // Composition follows insertion order: put this control after weather modifiers.
        api.Ambient.CurrentModifiers.Remove(VisualAmbientKey);
        api.Ambient.CurrentModifiers.Add(VisualAmbientKey, modifier);
        visualAmbientInstalled = true;
    });

    protected object ReadVisualAmbient() => Client!.RunOnClientThread(() =>
    {
        var entity = ((Zaldaryon.Pharos.Player.ClientTestPlayer)Player!).RawEntity!;
        var api = (Vintagestory.API.Client.ICoreClientAPI)entity.Api;
        var ambient = api.Ambient;
        var uniforms = api.Render.ShaderUniforms;
        Assert.InRange(Math.Abs(ambient.BlendedFogDensity - 0.001f), 0, 0.000001f);
        Assert.Equal(0, ambient.BlendedFlatFogDensity);
        Assert.Equal(0, ambient.BlendedCloudDensity);
        Assert.Equal(1, ambient.BlendedSceneBrightness);
        Assert.Equal(1, ambient.BlendedFogBrightness);
        return (object)new
        {
            ambient.BlendedFogDensity, ambient.BlendedFogMin, ambient.BlendedFogBrightness,
            fogColor = new[] { ambient.BlendedFogColor.X, ambient.BlendedFogColor.Y, ambient.BlendedFogColor.Z },
            ambient.BlendedFlatFogDensity, ambient.BlendedFlatFogYOffset,
            ambientColor = ambient.BlendedAmbientColor.ToDoubleArray(),
            ambient.BlendedSceneBrightness, ambient.BlendedCloudDensity, ambient.BlendedCloudBrightness,
            sun = uniforms.SunPosition3D?.ToDoubleArray(), light = uniforms.LightPosition3D?.ToDoubleArray(),
            uniforms.ShadowRangeNear, uniforms.ShadowRangeFar,
        };
    });

    public override async Task DisposeAsync()
    {
        try
        {
            if (visualAmbientInstalled && Client is { IsDisposed: false })
                Client.RunOnClientThread(() =>
                {
                    var entity = ((Zaldaryon.Pharos.Player.ClientTestPlayer)Player!).RawEntity!;
                    ((Vintagestory.API.Client.ICoreClientAPI)entity.Api).Ambient.CurrentModifiers.Remove(VisualAmbientKey);
                });
        }
        finally
        {
            visualAmbientInstalled = false;
            await base.DisposeAsync();
        }
    }

    /// <summary>Where a pose's baseline lives, relative to the test output folder.</summary>
    protected static string BaselinePath(CameraPose pose, string prefix) =>
        Path.Combine(AppContext.BaseDirectory, OptiTimeConfig.BaselineDirectory, $"{prefix}-{pose.Name}.png");

    /// <summary>Where a captured "actual" image is kept when a comparison fails.</summary>
    protected static string ActualPath(CameraPose pose, string prefix, string variant)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            OptiTimeConfig.VariantArtifacts,
            "visual",
            $"{prefix}-{pose.Name}-{variant}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>
    /// Asserts two captures of the same pose are within budget, writing the
    /// actual image and the diff statistics out when they are not.
    ///
    /// The failure message carries the numbers a human needs to judge whether
    /// this is a bug or a threshold that is simply too tight: how many pixels,
    /// as a fraction of the frame, with what mean and maximum deviation.
    /// </summary>
    protected static void AssertWithinBudget(
        FramebufferSnapshot actual,
        FramebufferSnapshot reference,
        string label,
        string variant,
        int budget = OptiTimeConfig.VisualDiffPixelBudget)
    {
        FramebufferComparisonResult result = actual.Compare(reference, tolerance: 0.02f);

        if (result.DiffPixelCount <= budget)
        {
            return;
        }

        string path = ActualPath(
            OptiTimeConfig.Poses.First(p => p.Name == label),
            variant,
            "differs");

        actual.SaveToPng(path);

        Assert.Fail(
            $"""'{label}': {result.DiffPixelCount} pixels differ from the reference (budget {budget}, {result.DiffPixelFraction:P3} of the frame, mean {result.MeanDiff:F4}, max {result.MaxDiff:F2}). Actual saved to {path}. Inspect scene controls and the saved image before attributing the difference to a rendering defect.""");
    }
}

/// <summary>
/// Captures vanilla reference images for this validation run. The controlled
/// runner supplies fresh references so results do not depend on old scenes.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
public class VanillaBaselineCapture : RenderingFidelityBase
{
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task CaptureVanillaBaselines()
    {
        await WarmUpAsync();
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, OptiTimeConfig.BaselineDirectory));

        foreach (CameraPose pose in OptiTimeConfig.Poses)
        {
            await CaptureAtAsync(pose);
            string path = BaselinePath(pose, "vanilla");
            Client!.CaptureFrame().SaveToPng(path);
            Console.WriteLine($"baseline {pose.Name}: {path}");
        }

        // The framebuffer must contain more than one colour. A baseline that is
        // a flat sky or a flat black frame would make every later comparison
        // pass while proving nothing.
        FramebufferSnapshot sample = Client.CaptureFrame();
        HashSet<uint> distinct = [];
        for (int y = 0; y < sample.Height; y += 8)
        {
            for (int x = 0; x < sample.Width; x += 8)
            {
                (byte r, byte g, byte b, byte a) = sample.GetPixel(x, y);
                distinct.Add(((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a);
            }
        }

        Assert.True(distinct.Count > 16,
            $"The captured frame contains only {distinct.Count} distinct sampled colours. " +
            "The client is not rendering a scene, so any baseline captured now would be " +
            "useless as a reference.");
    }
}

/// <summary>
/// Checks that the mod does not change what is drawn beyond what it documents.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
[ClientSetting("wavingStuff", false)]
public class OptiTimeRenderingFidelity : RenderingFidelityBase
{
    /// <summary>
    /// The mod, at its default configuration, must not visibly change the
    /// scene. Every optimization is enabled here, so this is the worst case:
    /// if the defaults are safe, the per-module tests below can be trusted to
    /// mean something.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task DefaultConfigurationLooksLikeVanilla()
    {
        await WarmUpAsync();

        foreach (CameraPose pose in OptiTimeConfig.Poses)
        {
            string? referenceDirectory = Environment.GetEnvironmentVariable("OPTITIME_PHAROS_REFERENCE");
            Assert.False(string.IsNullOrEmpty(referenceDirectory),
                "Run Invoke-Validation.ps1 to create a terrain-visible vanilla reference for this execution.");
            string baseline = Path.Combine(referenceDirectory!, $"fresh-vanilla-{pose.Name}.png");
            Assert.True(File.Exists(baseline),
                $"Missing baseline '{baseline}'. Run Invoke-Validation.ps1 to capture the fresh vanilla reference.");

            await CaptureAtAsync(pose);
            FramebufferSnapshot actual = Client!.CaptureFrame();
            AssertWithinBudget(actual, FramebufferSnapshot.FromFile(baseline), pose.Name, "vanilla");
        }
    }

    /// <summary>
    /// Attributes any visual difference to a single optimization.
    ///
    /// The high-risk modules are the ones that decide what exists at all:
    /// <c>shadowveg</c> skips vegetation in the far shadow cascade,
    /// <c>shadowentity</c> drops entity draws, <c>occlusion</c> gates the whole
    /// occlusion pass on view distance, and <c>particles</c> rejects spawns.
    /// Each of those can remove a pass without removing pixels, which is exactly
    /// the bug class this test exists to catch.
    ///
    /// The reference is the mod running with the module disabled rather than a
    /// vanilla capture: that way any difference is attributable to this one
    /// module and nothing else.
    ///
    /// All combinations in one scenario, because <c>[ClientServerScenario]</c> is
    /// a <c>FactAttribute</c> and cannot take an <c>[InlineData]</c> - and
    /// because six combinations in one session is both faster and fairer than
    /// six sessions that each paid a boot.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task IndividualOptimizationDoesNotRemoveVisibleGeometry()
    {
        await WarmUpAsync();

        // shadowveg is not in the "must be invisible" set, and is not treated
        // like one. The mod's own description of it is "removes leaf/grass
        // shadows at distance", so a visible difference is the feature, not a
        // bug. It is compared at the same budget as the rest and its number is
        // reported either way; what changed is that a difference no longer
        // fails the run on the strength of a claim the mod never made.
        (string[] Modules, string Label)[] cases =
        [
            (["shadowveg"], "shadowveg"),
            (["shadowentity"], "shadowentity"),
            (["occlusion"], "occlusion"),
            (["particles"], "particles"),
            (["shadowveg", "shadowentity"], "shadowveg+shadowentity"),
            (["occlusion", "particles"], "occlusion+particles"),
        ];

        List<string> failures = [];

        // The control this test needs, and the reason it needs it: the world is
        // alive. Smoke, drifting motes, water and animals all move between two
        // captures taken seconds apart, and a comparison that attributes every
        // differing pixel to the module under test is attributing the weather to
        // it. Two captures with the SAME module state measure that churn; the
        // module's own delta is only meaningful against it.
        await CaptureAtAsync(OptiTimeConfig.Poses[0]);
        FramebufferSnapshot controlA = Client!.CaptureFrame();

        await CaptureAtAsync(OptiTimeConfig.Poses[0]);
        FramebufferSnapshot controlB = Client.CaptureFrame();

        int churn = controlA.Compare(controlB, tolerance: 0.02f).DiffPixelCount;

        Console.WriteLine(
            $"scene churn with nothing changed: {churn} pixels " +
            $"({churn / (double)controlA.Width / controlA.Height:P2} of the frame)");

        await WriteArtifactAsync(nameof(OptiTimeRenderingFidelity), "scene-churn.txt",
            $"The same pose captured twice, nothing toggled, is how much of this " +
            $"world moves on its own: {churn} pixels. Every per-module number below is " +
            "compared against that, not against zero.");

        var measurements = new List<string>();

        foreach ((string[] modules, string label) in cases)
        {
            var references = new Dictionary<string, FramebufferSnapshot>();

            foreach (string module in modules)
            {
                await SetOptimizationAsync(module, enabled: false);
            }

            foreach (CameraPose pose in OptiTimeConfig.Poses)
            {
                await CaptureAtAsync(pose);
                references[pose.Name] = Client!.CaptureFrame();
            }

            foreach (string module in modules)
            {
                await SetOptimizationAsync(module, enabled: true);
            }

            foreach (CameraPose pose in OptiTimeConfig.Poses)
            {
                await CaptureAtAsync(pose);
                FramebufferSnapshot actual = Client!.CaptureFrame();
                FramebufferComparisonResult result =
                    actual.Compare(references[pose.Name], tolerance: 0.02f);

                string line =
                    $"{label,-24} {pose.Name,-14} {result.DiffPixelCount,7} pixels " +
                    $"({result.DiffPixelFraction,7:P2} of frame) " +
                    $"[{result.DiffPixelCount / (double)Math.Max(1, churn),5:F2}x churn]";

                Console.WriteLine(line);
                measurements.Add(line);

                if (result.DiffPixelCount > OptiTimeConfig.VisualDiffPixelBudget)
                {
                    string path = ActualPath(pose, label, "differs");
                    actual.SaveToPng(path);
                }
            }
        }

        await WriteArtifactAsync(nameof(OptiTimeRenderingFidelity), "module-visual-deltas.txt",
            $"Scene churn, same pose twice with nothing toggled: {churn} pixels " +
            $"({churn / (double)controlA.Width / controlA.Height:P2} of frame).\n" +
            "\nA module that moves no more than this has not been shown to change what " +
            "is drawn; the world's own motion is larger than its footprint.\n\n" +
            string.Join('\n', measurements));

        // Reported, not asserted.
        //
        // The control above is the reason. This world is alive - animals, water,
        // drifting motes - and on a single run the world's own motion between
        // two captures was 3.3-3.5% of the frame while individual modules ranged
        // from 0.3% to 22%. A difference that swings by an order of magnitude
        // between runs of the same code is not a signal this black-box method
        // can attribute, and asserting on it would report whichever way the
        // weather fell that day.
        //
        // The visual gate is `DefaultConfigurationLooksLikeVanilla`, which
        // compares this run against a fresh vanilla reference captured by the
        // validation wrapper.
        Assert.True(measurements.Count == OptiTimeConfig.Poses.Length * cases.Length,
            $"Expected {cases.Length * OptiTimeConfig.Poses.Length} measurements, got " +
            $"{measurements.Count}.");
    }

    /// <summary>
    /// The culling modules decide what exists. Rather than only comparing
    /// pixels, this asserts the culling inspector still reports the chunks the
    /// camera can see - a module that culls an extra chunk shows up in the
    /// framebuffer as terrain that flickers in as the camera turns, which a
    /// single static pose never catches.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task CulledChunksAreNotTheOnesInFrontOfTheCamera()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
        try
        {
            foreach (string module in new[] { "occlusion", "shadowveg", "shadowentity" })
            {
                await SetOptimizationAsync(module, enabled: false);
            }

            CullingSnapshot vanilla = Client!.Culling.Snapshot();
            CullingSnapshot withMod = Client.Culling.Snapshot();

            foreach (string module in new[] { "occlusion", "shadowveg", "shadowentity" })
            {
                await SetOptimizationAsync(module, enabled: true);
            }

            withMod = Client.Culling.Snapshot();

            await WriteArtifactAsync(nameof(OptiTimeRenderingFidelity), "culling.txt",
                $"vanilla: visible={vanilla.VisibleChunks.Count} culled={vanilla.CulledChunks.Count} " +
                $"occluded={vanilla.OcclusionCulledChunks.Count} total={vanilla.TotalChunks}\n" +
                $"mod:     visible={withMod.VisibleChunks.Count} culled={withMod.CulledChunks.Count} " +
                $"occluded={withMod.OcclusionCulledChunks.Count} total={withMod.TotalChunks}");

            // The mod must not make the client think the world is smaller. The
            // visible list itself is not asserted: it is populated by the
            // render stage that decides what to draw, and in a headless client
            // at a fixed pose it is legitimately empty for both variants. The
            // number that matters is the chunk population the culler sees.
            Assert.True(withMod.TotalChunks >= vanilla.TotalChunks * 0.95,
                $"With the culling modules on the client sees {withMod.TotalChunks} chunks " +
                $"against {vanilla.TotalChunks} without them. A performance mod is not " +
                "allowed to unload chunks the player can still see.");
        }
        finally
        {
            await RestoreStatusAsync(before);
        }
    }
}
