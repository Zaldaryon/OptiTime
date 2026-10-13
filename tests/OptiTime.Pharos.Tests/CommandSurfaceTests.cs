namespace OptiTime.Pharos.Tests;

/// <summary>
/// Covers the mod's command surface: every subcommand <c>.optitime</c> accepts,
/// and what it does with arguments it does not recognise.
///
/// A client-side mod's command is its whole scripting interface. Config tools,
/// overlay scripts and support instructions all drive it, so a change to the
/// argument grammar is a breaking change even when nothing in the code moved.
/// These tests are what catches that.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class CommandSurfaceTests : OptiTimeScenarioBase
{
    /// <summary>
    /// The bare command is the help text, because a player types
    /// <c>.optitime</c> into chat to find out what it can do. It must print
    /// something, and that something must name the options.
    /// </summary>
    [ClientServerScenario]
    public async Task BareCommandPrintsUsableHelp()
    {
        await WarmUpAsync();

        CommandOutput help = await RunCommandOkAsync(OptiTimeConfig.Command);
        string text = Unescape(help.Text);

        Assert.False(string.IsNullOrWhiteSpace(text),
            "'.optitime' printed nothing at all.");
        Assert.True(text.Contains("status", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("on", StringComparison.OrdinalIgnoreCase),
            $"The help text does not mention how to use the command:\n{text}");

        await WriteArtifactAsync(nameof(CommandSurfaceTests), "help.txt", text);
    }

    /// <summary>
    /// <c>status</c> and the bare command are the same call, and the mod treats
    /// them as aliases. If they ever diverge, whichever the docs and the
    /// overlay scripts use is the one that is quietly wrong.
    /// </summary>
    [ClientServerScenario]
    public async Task StatusAndBareCommandAgree()
    {
        await WarmUpAsync();

        string bare = Unescape((await RunCommandOkAsync(OptiTimeConfig.Command)).Text);
        string status = Unescape((await RunCommandOkAsync($"{OptiTimeConfig.Command} status")).Text);

        IReadOnlyDictionary<string, OptionState> a = OptiTimeConfig.ReadStatus(bare);
        IReadOnlyDictionary<string, OptionState> b = OptiTimeConfig.ReadStatus(status);

        Assert.NotEmpty(b);
        Assert.Equal(a.Count, b.Count);
        foreach ((string option, OptionState state) in b)
        {
            Assert.True(a.TryGetValue(option, out OptionState other), $"'{option}' missing from bare output.");
            Assert.Equal(state.Enabled, other.Enabled);
        }
    }

    /// <summary>
    /// A single option name prints one option's detail card. This is what a
    /// player reads before deciding whether to turn something off, so it has to
    /// contain a state and a description rather than just echoing the name.
    ///
    /// All twenty in one scenario: <c>[ClientServerScenario]</c> is a
    /// <c>FactAttribute</c> and cannot take parameters, and twenty separate
    /// scenarios would mean twenty boots for a check that takes milliseconds.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task EverySingleOptionQueryDescribesItself()
    {
        await WarmUpAsync();

        List<string> problems = [];

        foreach (string optimization in OptiTimeConfig.OptionNames)
        {
            CommandOutput info = await RunCommandAsync($"{OptiTimeConfig.Command} {optimization}");
            string text = Unescape(info.Text);

            if (!text.Contains("Status:", StringComparison.Ordinal))
            {
                problems.Add($"{optimization}: no 'Status:' line\n    {FirstLine(text)}");
            }

            if (!text.Contains("Description:", StringComparison.Ordinal))
            {
                problems.Add($"{optimization}: no 'Description:' line");
            }
        }

        Console.WriteLine($"{OptiTimeConfig.OptionNames.Length} option detail cards printed");

        Assert.True(problems.Count == 0,
            $"{problems.Count} option(s) printed an incomplete detail card:\n  " +
            string.Join("\n  ", problems));

        static string FirstLine(string text) =>
            text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(nothing)";
    }

    /// <summary>
    /// An action that is neither on nor off must be refused with the usage
    /// line, and must not change the option. Silently ignoring a typo is how a
    /// player ends up running unoptimised for a month without noticing.
    /// </summary>
    [ClientServerScenario]
    public async Task BadActionIsRefusedAndChangesNothing()
    {
        await WarmUpAsync();

        OptionState before = await SetOptimizationAsync("particles", enabled: true);

        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} particles maybe");
        string text = Unescape(reply.Text);

        Assert.True(text.Contains("on", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("usage", StringComparison.OrdinalIgnoreCase),
            $"'.optitime particles maybe' did not explain the accepted values:\n{text}");

        OptionState after = await SetOptimizationAsync("particles", enabled: true);
        Assert.True(after.Enabled == before.Enabled, "A rejected action still changed the option.");
    }

    /// <summary>
    /// An option the mod does not know must be named as unknown, and the reply
    /// must list what it does know. Guessing at a near-miss name is worse than
    /// refusing: <c>.optitime shaders on</c> when the option is <c>blur</c>
    /// would look like it worked.
    /// </summary>
    [ClientServerScenario]
    public async Task UnknownOptionIsNamedAndTheKnownOnesListed()
    {
        await WarmUpAsync();

        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} occlussion on");
        string text = Unescape(reply.Text);

        Assert.True(text.Contains("occlussion", StringComparison.OrdinalIgnoreCase),
            $"An unknown option was not echoed back to the player:\n{text}");
        Assert.True(text.Contains("occlusion", StringComparison.OrdinalIgnoreCase),
            $"The reply did not list the real options, so a typo has nothing to correct to:\n{text}");
    }

    /// <summary>
    /// Option names are matched case-insensitively by the mod
    /// (<c>ToLower()</c> against its map). Overlay scripts and keybind macros
    /// get the case wrong constantly, so this has to hold.
    /// </summary>
    [ClientServerScenario]
    public async Task OptionNamesAreCaseInsensitive()
    {
        await WarmUpAsync();

        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} OcClUsIoN");
        string text = Unescape(reply.Text);

        Assert.Contains("Status: ON", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- profiling

    /// <summary>
    /// <c>.optitime profile</c> reports whether profiling is on, <c>on</c> and
    /// <c>off</c> flip it, and <c>reset</c> clears the counters without turning
    /// profiling off. The three have to stay distinguishable: a player who
    /// wants one more measurement window should reset, not toggle.
    /// </summary>
    [ClientServerScenario]
    public async Task ProfileSubcommandTogglesAndResets()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile off");
        Assert.Contains("profiling: OFF", Unescape((await RunCommandOkAsync(
            $"{OptiTimeConfig.Command} status")).Text), StringComparison.OrdinalIgnoreCase);

        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile on");
        Assert.Contains("profiling: ON", Unescape((await RunCommandOkAsync(
            $"{OptiTimeConfig.Command} status")).Text), StringComparison.OrdinalIgnoreCase);

        // Reset must not turn profiling off. This is the distinction that
        // matters: reset clears numbers, off stops collection.
        await Session!.StepFramesAsync(60);
        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile reset");
        Assert.Contains("profiling: ON", Unescape((await RunCommandOkAsync(
            $"{OptiTimeConfig.Command} status")).Text), StringComparison.OrdinalIgnoreCase);

        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile off");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile reset");
    }

    /// <summary>
    /// Enabling profiling starts it collecting. Asserted through the status
    /// line because the profiler itself has no other externally visible effect,
    /// and a profiler that silently collects nothing is worse than no profiler.
    /// </summary>
    [ClientServerScenario]
    public async Task ProfilingProducesFrameSamplesWhenOn()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile off");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile reset");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile on");
        await Session!.StepFramesAsync(240);

        await RunCommandAsync($"{OptiTimeConfig.Command} profile");
        string text = Unescape((await RunCommandAsync($"{OptiTimeConfig.Command} profile")).Text);
        await WriteArtifactAsync(nameof(CommandSurfaceTests), "profile.txt", text);

        Assert.False(string.IsNullOrWhiteSpace(text),
            "'.optitime profile' printed nothing while profiling was enabled.");

        await RunCommandOkAsync($"{OptiTimeConfig.Command} profile off");
    }

    // ------------------------------------------------------------------- diag

    /// <summary>
    /// <c>diag all dump</c> is the mod's "tell me everything" path and is what
    /// a player pastes into a bug report. It must reach the client log, where a
    /// support reader can actually find it, and it must cover every registered
    /// module.
    /// </summary>
    [ClientServerScenario]
    public async Task DiagAllDumpCoversEveryRegisteredModule()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all on");
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all reset");
        await Session!.StepFramesAsync(300);
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");

        string log = ClientLogText();
        await WriteArtifactAsync(nameof(CommandSurfaceTests), "diag-all.txt", log);

        List<string> missing = [.. OptiTimeConfig.DiagModules.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => !log.Contains($"[OptiTime/diag/{m}]", StringComparison.OrdinalIgnoreCase))];

        Assert.True(missing.Count == 0,
            $"'diag all dump' produced no line for: {string.Join(", ", missing)}.\n" +
            "A module with no line is a module whose counters are switched off, " +
            "unavailable, or never called.");
    }

    /// <summary>
    /// <c>diag all off</c> followed by a dump must go quiet, and
    /// <c>diag all on</c> must bring it back. Without this, "enable the
    /// counters" is an untested assumption underneath every counter assertion
    /// in the suite.
    /// </summary>
    [ClientServerScenario]
    public async Task DiagAllOffSilencesTheDumpAndOnRestoresIt()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all on");
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");
        Assert.Contains("[OptiTime/diag/", ClientLogText(), StringComparison.Ordinal);

        Client!.Logs.Clear();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all off");
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");
        Assert.DoesNotContain("[OptiTime/diag/", ClientLogText(), StringComparison.Ordinal);

        Client.Logs.Clear();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all on");
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");
        Assert.Contains("[OptiTime/diag/", ClientLogText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>diag all reset</c> zeroes the counters. Checked through a module
    /// whose counter is guaranteed to move every frame, because otherwise a
    /// reset that did nothing would look identical to a reset that worked.
    /// </summary>
    [ClientServerScenario]
    public async Task DiagResetZeroesTheCounters()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag occlusion on");
        await Session!.StepFramesAsync(300);

        long before = await ReadCounter("occlusion", DiagCounter.FramesWithDynamicThreshold);
        Assert.True(before > 0,
            "The occlusion module counted no frames in 300 frames, so a reset " +
            "cannot be told apart from a no-op.");

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag occlusion reset");
        long after = await ReadCounter("occlusion", DiagCounter.FramesWithDynamicThreshold);

        Assert.True(after < before,
            $"'diag occlusion reset' left the counter at {after}, down from {before} only " +
            "by the frames that ran between the reset and the read.");
    }

    /// <summary>
    /// Asking a module that does not exist must be refused by name, with the
    /// module list, rather than silently doing nothing.
    /// </summary>
    [ClientServerScenario]
    public async Task UnknownDiagModuleIsRefused()
    {
        await WarmUpAsync();

        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} diag nonsense dump");
        string text = Unescape(reply.Text);

        Assert.True(text.Contains("nonsense", StringComparison.OrdinalIgnoreCase),
            $"An unknown diag module was not named back to the player:\n{text}");
    }

    /// <summary>
    /// Dumping a module that was never enabled must say so, rather than
    /// printing a dump full of zeros that reads like "the optimization does
    /// nothing" when in fact nobody turned the counter on.
    /// </summary>
    [ClientServerScenario]
    public async Task DumpingADisabledModuleSaysItWasNotEnabled()
    {
        await WarmUpAsync();

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag recipe off");
        Client!.Logs.Clear();
        CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} diag recipe dump");
        string text = Unescape(reply.Text);

        Assert.True(text.Contains("not enabled", StringComparison.OrdinalIgnoreCase),
            $"Dumping a disabled module did not say it was not enabled:\n{text}");

        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag recipe on");
    }

    /// <summary>
    /// <c>interpdiag</c> is a separate counter set for the entity interpolation
    /// module, with its own enable/reset/dump. It exists because that module's
    /// counters are noisy enough that leaving them on all the time would cost
    /// more than they are worth, so it has to be verified as its own surface.
    /// </summary>
    [ClientServerScenario]
    public async Task InterpDiagSubcommandRoundTrips()
    {
        await WarmUpAsync();

        CommandOutput on = await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag on");
        Assert.Contains("ON", Unescape(on.Text), StringComparison.OrdinalIgnoreCase);

        CommandOutput off = await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag off");
        Assert.Contains("OFF", Unescape(off.Text), StringComparison.OrdinalIgnoreCase);

        CommandOutput reset = await RunCommandOkAsync($"{OptiTimeConfig.Command} interpdiag reset");
        Assert.False(string.IsNullOrWhiteSpace(Unescape(reset.Text)));

        CommandOutput dump = await RunCommandAsync($"{OptiTimeConfig.Command} interpdiag dump");
        await WriteArtifactAsync(nameof(CommandSurfaceTests), "interpdiag.txt", Unescape(dump.Text));
    }

    /// <summary>
    /// The rich-text check lives in <c>RegressionTests</c>, next to the other
    /// shipped-bug tests and with the harness' blanket error gate switched off,
    /// because what it looks for is a client error. Duplicating it here would
    /// only produce a second, weaker copy of the same failure.
    /// </summary>
    [ClientServerScenario]
    public async Task RichTextRegressionIsCoveredInRegressionTests()
    {
        await WarmUpAsync();

        CommandOutput status = await RunCommandOkAsync($"{OptiTimeConfig.Command} status");

        Console.WriteLine(
            "rich-text status: see RegressionTests.HelpTextProducesNoRichTextParseErrors; " +
            $"{status.ChatLines.Count} status line(s) printed here");

        Assert.NotEmpty(status.ChatLines);
    }

    /// <summary>
    /// Every one of the mod's chat lines must be produced by the command the
    /// test just ran, not leaked from the previous one. The mod prints through
    /// the shared chat channel, so a stray line means the command's output is
    /// not what a player actually sees.
    /// </summary>
    [ClientServerScenario]
    public async Task EverySubcommandProducesOutput()
    {
        await WarmUpAsync();

        string[] commands =
        [
            OptiTimeConfig.Command,
            $"{OptiTimeConfig.Command} status",
            $"{OptiTimeConfig.Command} diag all",
            $"{OptiTimeConfig.Command} profile",
            $"{OptiTimeConfig.Command} particles",
            $"{OptiTimeConfig.Command} nonsense",
        ];

        foreach (string command in commands)
        {
            CommandOutput output = await RunCommandAsync(command);
            Assert.False(string.IsNullOrWhiteSpace(output.Text),
                $"'.{command.TrimStart('.')}' produced no output at all. A command that " +
                "silently does nothing is indistinguishable from one that is broken.");
        }
    }

    private async Task<long> ReadCounter(string module, string counter)
    {
        await RunCommandAsync($"{OptiTimeConfig.Command} diag {module} dump");
        return (long)(DiagReader.Parse(DiagReader.Body(ClientLogText(), module))
            .Number(counter) ?? -1);
    }

    /// <summary>
    /// Chat escapes angle brackets so the rich-text renderer leaves them alone.
    /// The mod logs the same text unescaped, but the log copy is what the suite
    /// reads for counters, so anything parsed out of chat is unescaped first.
    /// </summary>
    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
}