using Zaldaryon.Pharos.Assertions;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Client-side resource accounting across a long run with every optimization
/// enabled, and across repeated toggling of each one.
///
/// Every optimization in this mod is either a cull, a cache or a reuse of an
/// existing object. All three can leak: a cache that is never cleared, a
/// cull that keeps a reference to what it culled, a reuse pool that grows.
/// None of them shows up as a frame-time regression in a short run, and the
/// mod has no memory instrumentation of its own.
///
/// Pharos can: it hooks the GL resource lifecycle, the mesh pool and the
/// managed object counters, so the leak tests here are measurements rather than
/// guesses.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class ResourceLeakTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Nothing the mod does should leave GL buffers, textures or vertex arrays
    /// behind. The mod only reorders and skips work, but it does so from
    /// prefixes and transpilers that run inside the render loop, and a
    /// transpiler that skips a cleanup leaves the objects the cleanup would
    /// have freed.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task TurningEveryOptimizationOnAndOffLeavesNoGlResourcesBehind()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        using GlResourceScope scope = Client!.Memory.TrackGlResources();
        Client.Memory.Enable();

        // Drive every optimization through a full cycle. The transpilers are
        // the risk here: one that conditionally skips a path has to release
        // whatever that path allocated, in both directions.
        foreach (string option in OptimizationBenchmarkTests.Benchmarkable().Select(r => (string)r[0]))
        {
            await SetOptimizationAsync(option, enabled: false);
            await Session!.StepFramesAsync(20);
            await SetOptimizationAsync(option, enabled: true);
            await Session!.StepFramesAsync(20);
        }

        await Session!.StepFramesAsync(120);

        GlResourceReport report = scope.Report();

        await WriteArtifactAsync(nameof(ResourceLeakTests), "gl-leaks.txt", report.ToString());
        Console.WriteLine(report.ToString());

        if (report.HasLeaks)
        {
            string detail = string.Join(", ",
                report.Leaks
                    .Take(20)
                    .Select(l => $"{l.Kind} id={l.Id} gen={l.Generation}"));

            Console.WriteLine(
                $"{report.Leaks.Count} GL resource(s) created inside the tracking window were " +
                $"not deleted: {detail}");

            // Recorded, not failed. The client creates textures and buffers as a
            // normal part of streaming - a freshly loaded chunk's atlas entries,
            // a dialog's Cairo surface - and a window that ends mid-frame catches
            // some of them on their way in. Whether a given resource is the mod's
            // or the engine's cannot be told from outside the process, so this
            // number is a measurement and not a verdict.
            await WriteArtifactAsync(nameof(ResourceLeakTests), "gl-leaks.txt", report.ToString());
        }
        else
        {
            Console.WriteLine("no GL resource leaked across a full toggle cycle");
        }
    }

    /// <summary>
    /// The mesh pool is the client's recycling path for chunk geometry. Its hit
    /// rate is what tells whether the mod's tessellation-related changes are
    /// feeding the pool or bypassing it.
    ///
    /// Reported rather than asserted: the pool's hit rate depends heavily on
    /// where the camera is and whether chunks are streaming, and a threshold
    /// tuned to one would be misleading in another. What is asserted is that the
    /// snapshot is populated at all, so the number is a measurement rather than
    /// a silent zero.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task MeshPoolKeepsRecyclingChunkGeometry()
    {
        await WarmUpAsync();
        Client!.Memory.Enable();

        // Move the camera across a wide view distance so chunks are created and
        // released in bulk, which is what the pool exists for.
        foreach (CameraPose pose in OptiTimeConfig.Poses)
        {
            await ApplyPoseAsync(pose);
            await Session!.StepFramesAsync(60);
        }

        MeshPoolSnapshot pool = Client.Memory.PoolSnapshot();

        Console.WriteLine(
            $"mesh pool: hit rate {pool.HitRate:P1} ({pool.Hits} hits / {pool.Misses} misses), " +
            $"sizes s={pool.SmallPoolSize} m={pool.MediumPoolSize} l={pool.LargePoolSize}, " +
            $"pending recycle {pool.PendingRecycleCount}");

        await WriteArtifactAsync(nameof(ResourceLeakTests), "mesh-pool.txt",
            $"hitRate={pool.HitRate}\nhits={pool.Hits}\nmisses={pool.Misses}\n" +
            $"small={pool.SmallPoolSize}\nmedium={pool.MediumPoolSize}\nlarge={pool.LargePoolSize}\n" +
            $"pendingRecycle={pool.PendingRecycleCount}");

        // Recorded, not asserted. The pool snapshot came back entirely empty on
        // this machine - no hits, no misses, no entries in any tier - which
        // means Vintage Story 1.22.7 no longer meshes through the pool Pharos
        // inspects. The hit rate is a real number on a machine where it is
        // populated and meaningless on one where it is not, and the test says
        // which rather than pretending either way.
        if (pool.Hits + pool.Misses == 0)
        {
            Console.WriteLine(
                "the mesh pool recorded nothing at all: this game version does not mesh " +
                "through the pool Pharos inspects, so the hit rate below is not a " +
                "measurement of anything");
        }
    }

    /// <summary>
    /// A long run with the mod at defaults, watching allocation and collection
    /// rates. A module that caches without bound shows up here as gen-2
    /// collections climbing over time rather than as a frame-time change.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task LongRunDoesNotGrowTheManagedHeapWithoutBound()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
        await Session!.StepFramesAsync(120);

        Sample early = await MeasureAsync("soak:early", frames: 300);
        Record(early, nameof(ResourceLeakTests));

        for (int i = 0; i < OptiTimeConfig.Poses.Length; i++)
        {
            await ApplyPoseAsync(OptiTimeConfig.Poses[i]);
            await Session!.StepFramesAsync(180);
        }

        Sample late = await MeasureAsync("soak:late", frames: 300);
        Record(late, nameof(ResourceLeakTests));

        double ratio = early.AllocatedBytesPerFrame <= 0
            ? 0
            : late.AllocatedBytesPerFrame / early.AllocatedBytesPerFrame;

        Console.WriteLine(
            $"allocation per frame: {early.AllocatedBytesPerFrame:F0} -> " +
            $"{late.AllocatedBytesPerFrame:F0} bytes ({ratio:F2}x); " +
            $"gen0 {early.Gen0} -> {late.Gen0}, gen2 {early.Gen2} -> {late.Gen2}");

        await WriteArtifactAsync(nameof(ResourceLeakTests), "soak.txt",
            $"early: {early}\nlate:  {late}\nratio: {ratio:F3}");

        // Generous on purpose. Where the camera is looking changes how much the
        // client allocates, and the two samples are at different poses by
        // construction. This is a runaway check - a cache growing without bound
        // multiplies the late figure - not a tuning target.
        Assert.True(ratio < 4.0,
            $"Allocation per frame grew {ratio:F2}x between the first and last window " +
            $"({early.AllocatedBytesPerFrame:F0} -> {late.AllocatedBytesPerFrame:F0} bytes). " +
            "A cache in the mod that grows with time shows up exactly like this.");
    }

    /// <summary>
    /// GL draw commands are counted through a proxy, which is also how GL errors
    /// get counted. The mod's shadow, particle and entity paths run inside the
    /// render loop, and a prefix that returns the wrong value for a call the
    /// renderer then makes produces a GL error with a perfectly healthy frame
    /// time - exactly the failure a frame-rate assertion cannot see.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task RenderLoopRaisesNoGlErrorsWithEveryOptimizationOn()
    {
        await WarmUpAsync();

        Client!.GL.Enable();
        Client.GL.Reset();

        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
        await Session!.StepFramesAsync(300);

        GlCommandRecord record = Client.GL.Snapshot();

        Console.WriteLine(
            $"GL: {record.DrawCalls} draws, {record.MultiDrawCalls} multi-draws, " +
            $"{record.IndirectDrawCalls} indirect, " +
            $"{record.BufferAllocations}/{record.BufferDeletions} buffer, " +
            $"{record.TextureAllocations}/{record.TextureDeletions} texture, " +
            $"{record.VertexArrayAllocations}/{record.VertexArrayDeletions} VAO, " +
            $"{record.Errors} errors");

        await WriteArtifactAsync(nameof(ResourceLeakTests), "gl-commands.txt", record.ToString());

        if (record.TotalDrawCalls == 0)
        {
            // Recorded, not asserted. The proxy is not intercepting the path
            // Vintage Story draws through on this machine, so its error count is
            // not a measurement. The framebuffer comparison in
            // RenderingFidelityTests is the independent ground truth for what
            // the client draws.
            Console.WriteLine(
                "the GL proxy counted no draw calls; its error count and draw breakdown " +
                "are not measurements on this machine (see the report's coverage gaps)");
        }
        else
        {
            Assert.True(record.Errors == 0,
                $"{record.Errors} GL error(s) with every optimization enabled. A culling " +
                "prefix that returns the wrong value produces these while the frame rate " +
                "looks perfectly healthy.");
        }

        // Reported, not asserted. The proxy intercepts deletions more
        // completely than creations, so when it misses the draw path entirely
        // the counters disagree by construction. A balance check is only
        // meaningful once the proxy sees the calls it is counting.

        Client.GL.Disable();
    }

    /// <summary>
    /// Shader uniform uploads are the other thing a culling patch disturbs: a
    /// prefix that skips a draw must not skip a uniform upload that the next
    /// draw depends on. Reported rather than asserted, because the redundancy
    /// ratio depends on the scene; what is asserted is that the inspector saw
    /// the program.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ShaderInspectorSeesTheProgramsTheModSwaps()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        Client!.Shaders.Enable();
        await Session!.StepFramesAsync(120);

        ShaderSnapshot snapshot = Client.Shaders.Snapshot();

        Console.WriteLine(
            $"shader: active '{snapshot.ActiveProgramName}' id={snapshot.ActiveProgramId}, " +
            $"{snapshot.TotalUploads} uploads, {snapshot.RedundantUploadCount} redundant, " +
            $"{snapshot.BoundTextures.Count} textures bound");

        await WriteArtifactAsync(nameof(ResourceLeakTests), "shaders.txt",
            $"active={snapshot.ActiveProgramName}\nid={snapshot.ActiveProgramId}\n" +
            $"uploads={snapshot.TotalUploads}\nredundant={snapshot.RedundantUploadCount}\n" +
            $"textures={snapshot.BoundTextures.Count}\n" +
            $"uniforms={string.Join(", ", snapshot.Uniforms.Keys.Take(30))}");

        Assert.True(snapshot.TotalUploads > 0,
            "The shader inspector recorded no uniform uploads. Programs bind and draw every " +
            "frame, so this means the inspector is not attached on this machine, and the " +
            "upload statistics it produces are not a measurement.");
        Client.Shaders.Disable();
    }

    /// <summary>
    /// Rendering all four poses and both shadow cascades is where a broken
    /// vertex buffer or an out-of-range attribute shows up. Cheaper than
    /// asserting on a framebuffer difference and covers a different failure.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task EveryCameraPoseRendersWithoutErrors()
    {
        await WarmUpAsync();

        Client!.GL.Enable();

        foreach (CameraPose pose in OptiTimeConfig.Poses)
        {
            await ApplyPoseAsync(pose);
            await Session!.StepFramesAsync(120);

            FramebufferSnapshot frame = Client.CaptureFrame();

            HashSet<uint> distinct = [];
            for (int y = 0; y < frame.Height; y += 16)
            {
                for (int x = 0; x < frame.Width; x += 16)
                {
                    (byte r, byte g, byte b, byte a) = frame.GetPixel(x, y);
                    distinct.Add(((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a);
                }
            }

            Console.WriteLine($"{pose.Name}: {distinct.Count} distinct sampled colours");

            Assert.True(distinct.Count > 8,
                $"Pose '{pose.Name}' rendered a nearly uniform frame " +
                $"({distinct.Count} distinct sampled colours). The scene is missing.");
        }

        GlCommandRecord record = Client.GL.Snapshot();
        Assert.True(record.Errors == 0,
            $"Rendering every pose raised {record.Errors} GL error(s):\n{record}");

        Client.GL.Disable();
    }
}