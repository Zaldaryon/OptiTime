using Zaldaryon.Pharos.Reporting;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Legacy runtime configuration stress scenarios. Requested OFF/ON states do
/// not remove/reinstall boot-time Harmony patches, so these rows are telemetry
/// only: neither module attribution nor vanilla comparisons. BootPerformanceTests
/// provide separately booted variants. Existing stress thresholds remain unchanged.
///
/// The tests assert invariants that should hold under any hardware - no new
/// errors, no extra allocation, no tail-latency blow-up - and *report* the
/// speed numbers without asserting them. Wall-clock deltas on a shared or
/// virtualised machine are far too noisy to gate on, and a test that fails
/// intermittently teaches people to ignore failures.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class OptimizationBenchmarkTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Everything the A/B can actually measure in this harness. The exclusions
    /// are documented on the config properties; they are excluded because the
    /// measurement would be meaningless, not because the modules are untested.
    /// </summary>
    public static IEnumerable<object[]> Benchmarkable() =>
        OptiTimeConfig.OptionNames
            .Where(o => !OptiTimeConfig.NonOptimizations.Contains(o, StringComparer.OrdinalIgnoreCase))
            .Where(o => !OptiTimeConfig.NeedsReboot.Contains(o, StringComparer.OrdinalIgnoreCase))
            .Where(o => !OptiTimeConfig.NeedsRemoteEntities.Contains(o, StringComparer.OrdinalIgnoreCase))
            .Where(o => !OptiTimeConfig.NeedsUnfocusedWindow.Contains(o, StringComparer.OrdinalIgnoreCase))
            .Select(o => new object[] { o });

    /// <summary>
    /// One scenario measures every benchmarkable module.
    ///
    /// Not a theory over the module name, deliberately. Pharos'
    /// <c>[ClientServerScenario]</c> derives from xUnit's
    /// <c>FactAttribute</c>, so it cannot take parameters, and a theory would
    /// fall back to <c>[Theory]</c> - which is not the attribute that wires up
    /// the server, the client, the world and the timeout. Sixteen separate
    /// scenario methods would also mean sixteen boots at roughly fifty seconds
    /// each, for the same measurement.
    ///
    /// Every module's failure is collected and reported together rather than
    /// asserted as it goes, so one broken module does not hide the state of the
    /// other fifteen.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 3_600_000)]
    public async Task ModuleChangesTheCostOfSteadyState()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        // The mod's own counters cost nothing while they are off, and having
        // them on lets the dump be compared against what we measured ourselves.
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all on");

        List<string> failures = [];

        foreach (string optimization in Benchmarkable().Select(r => (string)r[0]))
        {
            failures.AddRange(await MeasureModuleAsync(optimization));
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} per-module invariant(s) broken:\n  " +
            string.Join("\n  ", failures));
    }

    /// <summary>
    /// Measures one module and returns the invariants it broke, empty when it
    /// broke none.
    /// </summary>
    private async Task<List<string>> MeasureModuleAsync(string optimization)
    {
        List<string> failures = [];

        await SetOptimizationAsync(optimization, enabled: false);
        await WarmUpAsync();
        int errorsBefore = Client!.Logs.UnexpectedErrors(AllowedLoggedErrors).Count;

        Sample off = await MeasureAsync($"{optimization}:requested-off");
        Record(off, nameof(OptimizationBenchmarkTests));
        await Session!.StepFramesAsync(OptiTimeConfig.MeasureFrames);
        await DumpDiagAsync(optimization, "off");

        await SetOptimizationAsync(optimization, enabled: true);
        await WarmUpAsync();

        Sample on = await MeasureAsync($"{optimization}:requested-on");
        Record(on, nameof(OptimizationBenchmarkTests));
        await Session!.StepFramesAsync(OptiTimeConfig.MeasureFrames);
        await DumpDiagAsync(optimization, "on");

        Report(optimization, off, on);

        // A performance optimization that allocates more than the vanilla path
        // it replaced is a bug, and this is the one check that holds regardless
        // of how fast or slow the machine is.
        //
        // The thresholds are not zero. The client allocates around eighty
        // kilobytes per frame doing things no mod touches - chunk streaming,
        // particle pools, GUI composition - and the run-to-run spread on that
        // is one to two percent. A gate at exactly zero would fire on every
        // module on every run and teach everyone to ignore it. What is gated is
        // an increase large enough to matter: ten percent, or four kilobytes
        // per frame, whichever is larger.
        double allocationDelta = on.AllocatedBytesPerFrame - off.AllocatedBytesPerFrame;

        if (allocationDelta > Math.Max(4_096, off.AllocatedBytesPerFrame * 0.10))
        {
            failures.Add(
                $"{optimization}: allocating {on.AllocatedBytesPerFrame:F0} B/frame with the " +
                $"module on, versus {off.AllocatedBytesPerFrame:F0} B/frame with it off " +
                $"({allocationDelta / Math.Max(1, off.AllocatedBytesPerFrame):+0.0%;-3}). " +
                "A performance optimization must not add steady-state allocation.");
        }

        // Two or three extra gen-0 collections over three hundred frames is
        // noise; a materially higher rate is not.
        if (on.Gen0 > off.Gen0 * 1.5 + 4)
        {
            failures.Add(
                $"{optimization}: {on.Gen0} gen-0 collections with the module on, " +
                $"{off.Gen0} with it off.");
        }

        if (on.P95 > off.P95 * 1.25)
        {
            failures.Add(
                $"{optimization}: P95 went from {off.P95.TotalMilliseconds:F2}ms to " +
                $"{on.P95.TotalMilliseconds:F2}ms. A tail-latency blow-up that large is a " +
                "regression even if the median improved.");
        }

        foreach (LogEntry error in Client.Logs.UnexpectedErrors(AllowedLoggedErrors).Skip(errorsBefore))
        {
            failures.Add($"{optimization}: logged {error}");
        }

        return failures;
    }

    /// <summary>
    /// Legacy configuration stress test, not a whole-mod or vanilla baseline.
    /// Runtime toggles leave boot-time patches installed. Use independent boots
    /// for performance comparisons; do not attribute these telemetry deltas.
    ///
    /// The per-module tests above attribute cost to individual modules; this one
    /// catches interactions between them, where two optimizations that each help
    /// alone together do not.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task WholeModVersusFullyDisabled()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all on");

        string[] measurable = [.. Benchmarkable().Select(r => (string)r[0])];

        foreach (string option in measurable)
        {
            await SetOptimizationAsync(option, enabled: false);
        }
        await WarmUpAsync();
        Sample allOff = await MeasureAsync("all:requested-off");
        Record(allOff, nameof(OptimizationBenchmarkTests));

        foreach (string option in measurable)
        {
            await SetOptimizationAsync(option, enabled: true);
        }
        await WarmUpAsync();
        Sample allOn = await MeasureAsync("all:requested-on");
        Record(allOn, nameof(OptimizationBenchmarkTests));

        ReportAll(allOff, allOn);

        Assert.True(allOn.AllocatedBytesPerFrame <= allOff.AllocatedBytesPerFrame * 1.10,
            $"The mod as a whole allocates {allOn.AllocatedBytesPerFrame:F0} B/frame, " +
            $"versus {allOff.AllocatedBytesPerFrame:F0} B/frame after runtime configuration changes - a " +
            $"{((allOn.AllocatedBytesPerFrame - allOff.AllocatedBytesPerFrame) / Math.Max(1, allOff.AllocatedBytesPerFrame)):P1} " +
            "net allocation change. Past ten percent that is a regression.");
    }

    /// <summary>
    /// Every render stage is timed independently by Pharos. The mod's modules
    /// that hook rendering - particles, entity animation, dynamic lights,
    /// shadows, GUI - each sit on a stage, so a module that made its own stage
    /// slower while making the frame faster overall would be invisible in a
    /// median-only measurement.
    ///
    /// Reported, not asserted. Stage timings move with what the camera is
    /// looking at, and the camera is only fixed to within a frame.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task RenderStageBreakdownForTheModAtDefaults()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        FrameMeasurement measurement = await Session!.MeasureFramesAsync(OptiTimeConfig.MeasureFrames);
        Sample sample = new(
            "stages:default",
            measurement.Frames,
            measurement.Median,
            measurement.P95,
            measurement.P99,
            measurement.Work.Mean,
            measurement.Work.Min,
            measurement.Work.Max,
            measurement.AllocatedBytesPerFrame,
            measurement.ProcessAllocatedBytes / Math.Max(1, measurement.Frames),
            measurement.Gen0Collections,
            measurement.Gen1Collections,
            measurement.Gen2Collections);

        Record(sample, nameof(OptimizationBenchmarkTests));

        string breakdown = string.Join(Environment.NewLine,
            measurement.Stages
                .OrderByDescending(s => s.PerFrame.Mean)
                .Select(s => $"  {s.Stage,-28} {s.PerFrame.Mean.TotalMilliseconds,8:F3} ms  " +
                             $"calls={s.Calls} over {s.FramesRun} frames"));

        await WriteArtifactAsync(
            nameof(OptimizationBenchmarkTests), "render-stages.txt",
            $"median {measurement.Median.TotalMilliseconds:F3} ms\n{breakdown}");

        Assert.True(measurement.Stages.Count > 0,
            "Pharos reported no render stage timings. The stage breakdown is what says " +
            "which part of the frame a module actually moved.");
    }

    /// <summary>
    /// Prints the measured delta next to the claim the mod publishes, so a
    /// promise that quietly stopped holding shows up as a number.
    /// </summary>
    private static void Report(string optimization, Sample off, Sample on)
    {
        string claim = OptiTimeConfig.Claims.TryGetValue(optimization, out Claim c)
            ? $"{c.Description} ({c.ExpectedReduction:P0} reduction)"
            : "no published figure";

        string line =
            $"""
             {optimization}
               claim          {claim}
               median         {off.MedianFps,7:F1} -> {on.MedianFps,7:F1} render steps/s
               p95            {off.P95Fps,7:F1} -> {on.P95Fps,7:F1} render steps/s
               alloc/frame    {off.AllocatedBytesPerFrame,7:F0} -> {on.AllocatedBytesPerFrame,7:F0} bytes
               process/frame  {off.ProcessAllocatedBytesPerFrame,7} -> {on.ProcessAllocatedBytesPerFrame,7} bytes
               gen0           {off.Gen0,7} -> {on.Gen0,7}
             """;

        Console.WriteLine("CONFIGURATION-ONLY TELEMETRY: installed boot-time patches unchanged; no module benefit established.");
        Console.WriteLine(line);

        // Unchanged measurements cannot distinguish an unchanged boot-time
        // patch from an unexercised workload or an inactive module.
        bool noMovement =
            Math.Abs((on.Median - off.Median).TotalMilliseconds) < 0.05 &&
            Math.Abs(on.AllocatedBytesPerFrame - off.AllocatedBytesPerFrame) < 1 &&
            on.Gen0 == off.Gen0;

        if (noMovement)
        {
            Console.WriteLine(
                $"  {optimization}: no measurable change; runtime config comparison does not establish activation or liveness.");
        }

        BenchmarkReport.Append(optimization, claim, off, on, noMovement);
    }

    private static void ReportAll(Sample off, Sample on)
    {
        string line =
            $"""
             runtime requested OFF -> ON (boot-time patch state unchanged; not vanilla vs mod)
               median         {off.MedianFps,7:F1} -> {on.MedianFps,7:F1} render steps/s
               p95            {off.P95Fps,7:F1} -> {on.P95Fps,7:F1} render steps/s
               alloc/frame    {off.AllocatedBytesPerFrame,7:F0} -> {on.AllocatedBytesPerFrame,7:F0} bytes
               process/frame  {off.ProcessAllocatedBytesPerFrame,7} -> {on.ProcessAllocatedBytesPerFrame,7} bytes
               gen0           {off.Gen0,7} -> {on.Gen0,7}
               gen1           {off.Gen1,7} -> {on.Gen1,7}
               gen2           {off.Gen2,7} -> {on.Gen2,7}
             """;

        Console.WriteLine(line);
        BenchmarkReport.Append("all", "the whole mod", off, on, noMovement: false);
    }

    /// <summary>
    /// Saves the mod's own counters next to ours. The diag text layout is not
    /// part of any published contract, so it is written out for a human to
    /// read rather than parsed into assertions that would break on a cosmetic
    /// reformat.
    /// </summary>
    private async Task DumpDiagAsync(string optimization, string state)
    {
        CommandOutput dump = await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");
        string module = OptiTimeConfig.DiagModules.GetValueOrDefault(optimization, optimization);

        await WriteArtifactAsync(
            nameof(OptimizationBenchmarkTests),
            $"diag-{optimization}-{state}.txt",
            $"--- {optimization} {state} ---\n" +
            (DiagReader.Body(dump.Text, module) ?? $"(no [{module}] line in this dump)"));
    }
}

/// <summary>
/// Accumulates every benchmark row so the run produces one table, whether it
/// passed or failed. <c>Record</c> already writes JSON lines per test; this is
/// the human-readable join of those, written on process exit.
/// </summary>
public static class BenchmarkReport
{
    private static readonly object Gate = new();
    private static readonly List<string> Rows = [];

    public static void Append(string optimization, string claim, Sample off, Sample on, bool noMovement)
    {
        lock (Gate)
        {
            Rows.Add(string.Join('\t',
            [
                optimization,
                claim,
                off.MedianFps.ToString("F1"),
                on.MedianFps.ToString("F1"),
                off.AllocatedBytesPerFrame.ToString("F0"),
                on.AllocatedBytesPerFrame.ToString("F0"),
                off.Gen0.ToString(),
                on.Gen0.ToString(),
                noMovement ? "NO-MOVEMENT" : string.Empty,
            ]));
        }
    }

    public static void WriteIfAny()
    {
        lock (Gate)
        {
            if (Rows.Count == 0) return;

            string dir = Path.Combine(AppContext.BaseDirectory, OptiTimeConfig.VariantArtifacts);
            Directory.CreateDirectory(dir);

            File.WriteAllLines(
                Path.Combine(dir, "benchmark-summary.tsv"),
                [
                    "module\tclaim\trender-steps-per-second-requested-off\trender-steps-per-second-requested-on\talloc-requested-off\talloc-requested-on\tgen0-requested-off\tgen0-requested-on\twarning",
                    .. Rows,
                ]);
        }
    }
}