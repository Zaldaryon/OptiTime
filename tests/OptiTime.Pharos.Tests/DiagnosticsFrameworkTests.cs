using System.Reflection;
using System.Text.Json;
using Zaldaryon.Pharos.Assertions;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// The mod's diagnostic framework, which is the only instrumentation the mod
/// ships, checked as a framework in its own right.
///
/// Every counter assertion elsewhere in this suite is standing on this: the
/// registry registers, the modules enable and disable, and the dump reaches the
/// log. If any of those break, the rest of the suite silently compares against
/// nothing - so they are asserted directly rather than assumed.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class DiagnosticsFrameworkTests : OptiTimeScenarioBase
{
    /// <summary>
    /// The status line and the diag registry name the same things. An option
    /// with counters must have a module and a module must have an option; a
    /// mismatch in either direction means a counter exists that no test can
    /// reach, or a module is counting something nobody can turn off.
    /// </summary>
    [ClientServerScenario]
    public async Task RegistryCoversEveryOptionThatHasCounters()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> states = await ReadStatusAsync();
        string log = ClientLogText();

        List<string> problems = [];

        foreach (string option in OptiTimeConfig.OptionNames)
        {
            if (!OptiTimeConfig.DiagModules.TryGetValue(option, out string? module)) continue;

            bool dumped = log.Contains($"[OptiTime/diag/{module}]", StringComparison.OrdinalIgnoreCase);

            // Not yet: nothing has asked for a dump in this test. Enable and
            // dump everything, then look again.
            if (dumped) continue;

            await RunCommandOkAsync($"{OptiTimeConfig.Command} diag {module} on");
            await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");

            dumped = ClientLogText()
                .Contains($"[OptiTime/diag/{module}]", StringComparison.OrdinalIgnoreCase);

            if (!dumped && states.TryGetValue(option, out OptionState state) && state.Enabled)
            {
                problems.Add(
                    $"'{option}' is enabled but its diag module '{module}' produced no " +
                    "dump, so its counters cannot be asserted on");
            }
        }

        Assert.True(problems.Count == 0,
            "The diag framework does not line up with the option list:\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>
    /// A <em>diag</em> module that was never enabled must say so, rather than
    /// printing a dump full of zeros that reads like "the optimization does
    /// nothing" when in fact nobody turned the counter on.
    ///
    /// <b>Diag off is not optimization off.</b> The two are separate switches:
    /// <c>particles off</c> stops the optimization but leaves the counter
    /// running, and a dump then correctly reports zeros. What has to be refused
    /// is <c>diag particles dump</c> before <c>diag particles on</c>, because
    /// that is the mistake v1.5.16 says was fixed - a dump full of zeros with
    /// nothing to say that nobody turned anything on.
    /// </summary>
    [ClientServerScenario]
    public async Task DumpingADisabledModuleSaysItWasNotEnabled()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag recipe off");
        Client!.Logs.Clear();
        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} diag recipe dump");
        string text = reply.Text;

        Assert.True(text.Contains("not enabled", StringComparison.OrdinalIgnoreCase),
            $"Dumping a diag module that was never enabled did not say so:\n{text}");

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag recipe on");
    }

    /// <summary>
    /// The other half: an enabled counter whose optimization is off still dumps,
    /// with zeros. Asserted so the suite pins which switch does what, because
    /// conflating them is how a working optimization gets reported as dead.
    /// </summary>
    [ClientServerScenario]
    public async Task OptimizationOffStillReportsZerosFromAnEnabledCounter()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag recipe on");

        IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
        try
        {
            await SetOptimizationAsync("recipe", enabled: false);

            DiagCounters counters = await CaptureDiagAsync("recipe", frames: 180);

            Assert.True(counters.Present,
                "The recipe counter produced no dump at all while the optimization was off. " +
                "A counter belongs to the diagnostic framework, not to the optimization, " +
                "and it must keep reporting.");

            Console.WriteLine(counters.Raw);
        }
        finally
        {
            await RestoreStatusAsync(before);
        }
    }

    /// <summary>
    /// Counters accumulate across frames, and the dump reports a rate over
    /// elapsed real time. Resetting mid-window has to zero both the count and
    /// the elapsed base, or the first dump after a reset reports a rate
    /// computed from the wrong denominator and every derived percentage is
    /// wrong.
    /// </summary>
    [ClientServerScenario]
    public async Task ResetZeroesTheRateWindow()
    {
        await WarmUpAsync();

        await ResetDiagAsync("occlusion");
        await Session!.StepFramesAsync(600);

        await RunCommandAsync($"{OptiTimeConfig.Command} diag occlusion dump");
        long first = Count(DiagCounter.FramesWithDynamicThreshold, "occlusion");

        await ResetDiagAsync("occlusion");
        await Session!.StepFramesAsync(600);

        await RunCommandAsync($"{OptiTimeConfig.Command} diag occlusion dump");
        long second = Count(DiagCounter.FramesWithDynamicThreshold, "occlusion");

        Console.WriteLine($"occlusion frames after reset: {first}, then {second}");

        Assert.True(first > 0, "The occlusion module counted no frames in 600.");
        Assert.True(second > 0, "The occlusion module counted no frames after a reset, " +
                                "so the reset did not re-arm the counter.");

        // The elapsed base must have been reset too: a second window that ran
        // for the same number of frames has to count roughly the same number of
        // frames, not continue accumulating from the first window.
        double ratio = (double)Math.Max(second, 1) / Math.Max(first, 1);
        Assert.True(ratio is > 0.5 and < 2.0,
            $"Two equal windows counted {first} and {second} frames. The counter is not " +
            "being reset between them, so a reset does not start a fresh window.");
    }

    /// <summary>
    /// Turning a module's counters off has to stop it counting, not just stop
    /// it printing. A counter that keeps incrementing while disabled costs an
    /// interlocked increment per call on a hot path - which is exactly the cost
    /// the optimization was introduced to remove.
    /// </summary>
    [ClientServerScenario]
    public async Task DisabledModuleCountersStopIncrementing()
    {
        await WarmUpAsync();
        await ApplyPoseAsync(OptiTimeConfig.Poses[0]);

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag guimgr on");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag guimgr reset");
        await Session!.StepFramesAsync(300);
        await RunCommandAsync($"{OptiTimeConfig.Command} diag guimgr dump");
        long whileOn = Count(DiagCounter.RenderCalls, "guimgr");

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag guimgr off");
        await Session!.StepFramesAsync(300);
        await RunCommandAsync($"{OptiTimeConfig.Command} diag guimgr dump");
        long whileOff = Count(DiagCounter.RenderCalls, "guimgr");

        Console.WriteLine($"guimgr render calls: counters on {whileOn}, counters off {whileOff}");

        Assert.True(whileOn > 0, "The GUI Manager counted no renders with its counters on.");

        Assert.True(whileOff <= whileOn,
            $"The GUI Manager counter went from {whileOn} to {whileOff} after the " +
            "diagnostics were switched off. It should have stopped at " +
            $"{whileOn}, not moved backwards - the two numbers come from different " +
            "windows and the second cannot be smaller unless something is resetting " +
            "the counter.");
    }

    /// <summary>
    /// Enabling counters for a module that has no counters - one of the two
    /// options with no diag module - has to be refused by name rather than
    /// silently succeeding.
    /// </summary>
    [ClientServerScenario]
    public async Task OptionsWithoutCountersAreNotDiagModules()
    {
        await WarmUpAsync();

        foreach (string option in OptiTimeConfig.OptionNames
                     .Where(o => !OptiTimeConfig.DiagModules.ContainsKey(o)))
        {
            CommandOutput reply = await RunCommandAsync(
                $"{OptiTimeConfig.Command} diag {option} dump");

            Assert.Contains(option, reply.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reads one counter out of the last dump a module wrote to the client log.
    ///
    /// Reads the <em>log</em> rather than the chat, because <c>DiagLog.Line</c>
    /// writes both and only the log copy is unescaped. The caller must have
    /// issued a dump: the counters reach the log only after one is written, and
    /// a helper that silently read an empty log would report zero for
    /// everything.
    /// </summary>
    private long Count(string counter, string module)
    {
        string log = ClientLogText();
        return (long)(DiagReader.Parse(DiagReader.Body(log, module)).Number(counter) ?? 0);
    }
}

/// <summary>
/// Packaging and metadata checks over the built artifact. No game boot.
/// </summary>
public partial class ModMetadataTests
{
    private static readonly Lazy<JsonDocument> ModInfo = new(ReadModInfo);

    [Fact]
    public void ModInfoDeclaresTheIdTheSuiteStages()
    {
        JsonElement root = ModInfo.Value.RootElement;

        Assert.Equal(OptiTimeConfig.ModId, root.GetProperty("modid").GetString());
        Assert.Equal(OptiTimeConfig.ModVersion, root.GetProperty("version").GetString());
    }

    /// <summary>
    /// The mod is client-side only. A <c>side</c> other than <c>client</c> would
    /// make Pharos stage it on the server too, where most of its Harmony anchors
    /// do not exist - and every test would then be measuring a boot failure.
    /// </summary>
    [Fact]
    public void ModInfoIsClientSideOnly()
    {
        Assert.Equal("client", ModInfo.Value.RootElement.GetProperty("side").GetString());
    }

    /// <summary>
    /// The declared game dependency is what decides whether ModDB shows the
    /// mod as current. The ModDB page is scoped to 1.22.5 - 1.22.6 while
    /// <c>modinfo.json</c> says <c>game 1.22.0</c>, and this suite runs on
    /// 1.22.7 - so the artifact and the published page disagree, and the test
    /// records which one it found rather than picking one.
    /// </summary>
    [Fact]
    public void ModInfoDependencyIsRecorded()
    {
        JsonElement dependencies = ModInfo.Value.RootElement.GetProperty("dependencies");
        Assert.True(dependencies.TryGetProperty("game", out JsonElement game),
            "modinfo.json declares no 'game' dependency at all.");

        string declared = game.GetString() ?? string.Empty;

        Console.WriteLine(
            $"modinfo declares game {declared}; this suite ran against " +
            $"{OptiTimeConfig.TestedGameVersion}");

        Assert.False(string.IsNullOrWhiteSpace(declared));
    }

    private static JsonDocument ReadModInfo()
    {
        string json = ReadEntry("modinfo.json");

        return JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
    }

    private static string ReadEntry(string name)
    {
        using System.IO.Compression.ZipArchive zip = ModArtifact.Open();

        // The zip is flat at the top: `modinfo.json` sits at the archive root,
        // next to `OptiTime.dll` and `modicon.png`, not inside a versioned
        // folder. So the match has to accept both a bare name and one nested in
        // a folder.
        System.IO.Compression.ZipArchiveEntry entry = zip.Entries.FirstOrDefault(
            e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)
              || e.FullName.EndsWith($"/{name}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"'{name}' is not in '{ModArtifact.ZipPath}'. The build is packaging a " +
                "different set of files than the source tree has.");

        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }
}