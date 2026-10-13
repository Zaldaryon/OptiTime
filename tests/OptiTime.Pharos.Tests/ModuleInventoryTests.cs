namespace OptiTime.Pharos.Tests;

/// <summary>
/// Establishes what the loaded mod actually exposes, before anything else
/// asserts about it.
///
/// This asks the running mod which options it exposes, then fails with a
/// concrete diff if the test inventory and the build disagree. It helps catch
/// options that were added to the product but left out of validation.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class ModuleInventoryTests : OptiTimeScenarioBase
{
    /// <summary>
    /// Every option the mod advertises must appear in the status output, with a
    /// state this suite can parse.
    ///
    /// Matched by parsing the line shape the mod documents
    /// (<c>  &lt;name&gt;: ON|OFF - &lt;description&gt;</c>) rather than by
    /// substring search. A substring search passes on a status dump that
    /// mentions an option only inside its description, which is precisely the
    /// drift this test is here to catch.
    /// </summary>
    [ClientServerScenario]
    public async Task EveryDocumentedOptionIsExposed()
    {
        await WarmUpAsync();

        CommandOutput status = await RunCommandOkAsync($"{OptiTimeConfig.Command} status");
        IReadOnlyDictionary<string, OptionState> states = OptiTimeConfig.ReadStatus(status.Text);

        await WriteArtifactAsync(nameof(ModuleInventoryTests), "status.txt", status.Text);

        List<string> missing = [.. OptiTimeConfig.OptionNames.Where(o => !states.ContainsKey(o))];
        List<string> unparsed = [.. OptiTimeConfig.OptionNames
            .Where(o => !missing.Contains(o))
            .Where(o => states[o].ToString().Length == 0)];

        Assert.True(missing.Count == 0 && unparsed.Count == 0,
            $"{missing.Count} documented option(s) are absent from '.optitime status' and " +
            $"{unparsed.Count} could not be parsed. Absent: " +
            $"{(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}. " +
            "Either the docs list removed options, or the build is missing live ones.\n\n" +
            $"--- status output ---\n{status.Text}");

        // The dump also reports the profiling flag on its own line. Losing it
        // would mean the profile subcommand stopped working.
        Assert.Contains("profiling:", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The mod's own option list must match the suite's, exactly.
    ///
    /// The status output proves the options are reachable. It does not prove
    /// the mod has no <em>extra</em> ones: an option added upstream and left
    /// out of this file would never be measured. The unknown-option reply is
    /// what makes the two comparable - the mod answers it with
    /// <c>"Available: a, b, c"</c>.
    /// </summary>
    [ClientServerScenario]
    public async Task ModKnowsExactlyTheOptionsThisSuiteExpects()
    {
        await WarmUpAsync();

        CommandOutput reply = await RunCommandAsync(
            $"{OptiTimeConfig.Command} definitely-not-an-optimization");

        string? listLine = reply.ChatLines
            .Select(Unescape)
            .FirstOrDefault(l => l.TrimStart().StartsWith("available:", StringComparison.OrdinalIgnoreCase));

        Assert.True(listLine is not null,
            "The unknown-option reply did not include the list of available options:\n" +
            string.Join('\n', reply.ChatLines.Select(Unescape)));

        string[] advertised = listLine!
            .Split([':', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .ToArray();

        Assert.True(advertised.Length > 5,
            $"The available-options list parsed as {advertised.Length} entries " +
            $"({string.Join(", ", advertised)}), which cannot be right.");

        string[] expected = [.. OptiTimeConfig.OptionNames.OrderBy(o => o, StringComparer.Ordinal)];
        string[] actual = [.. advertised.OrderBy(o => o, StringComparer.Ordinal)];

        Assert.True(expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
            "The mod advertises a different set of options than this suite knows about.\n" +
            $"  only in the suite: {Diff(expected, actual)}\n" +
            $"  only in the mod:    {Diff(actual, expected)}\n" +
            "A new option upstream has to be added to OptiTimeConfig.Options and given a " +
            "test, or it ships unmeasured.\n\n" +
            $"--- reply ---\n{listLine}");

        static string Diff(string[] a, string[] b) =>
            a.Except(b, StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("(none)").Aggregate(
                (x, y) => x + ", " + y);
    }

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>
    /// Toggling must round-trip for every option the command accepts.
    ///
    /// A module that reports success but does not change state is worse than one
    /// that errors, because the benchmark would then measure a module that was
    /// never applied and report its numbers as if they meant something.
    ///
    /// Driven from the suite's own list rather than a hand-written
    /// <c>[InlineData]</c> set, so an option added upstream is covered here
    /// automatically instead of being quietly skipped - and as one scenario
    /// rather than twenty, because <c>[ClientServerScenario]</c> is a
    /// <c>FactAttribute</c> and cannot take parameters.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 900_000)]
    public async Task ToggleRoundTrips()
    {
        await WarmUpAsync();

        List<string> failures = [];

        foreach (string optimization in OptiTimeConfig.OptionNames)
        {
            try
            {
                await SetOptimizationAsync(optimization, enabled: false);
                await Session!.StepFramesAsync(10);
                await SetOptimizationAsync(optimization, enabled: true);
                await Session!.StepFramesAsync(10);

                // Put it back to the documented default. The suite shares one
                // client across tests, and the config file survives Pharos'
                // rollback, so leaving a module off would silently change what
                // every later test exercises.
                await SetOptimizationAsync(optimization, enabled: true);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{optimization}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"{OptiTimeConfig.OptionNames.Length} options toggled off and on again");

        Assert.True(failures.Count == 0,
            $"{failures.Count} option(s) did not round-trip:\n  " +
            string.Join("\n  ", failures));
    }
}

    /// <summary>
/// The mod's own boot announcements, in a class of their own.
///
/// Split out for one reason: Pharos clears the client log on rollback, so only
/// the first test in a class can see what the mod logged while booting. An
/// audit of those announcements is therefore only meaningful as the sole test
/// here - put it in a class with other tests and it silently starts reading a
/// log that has been wiped.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class BootPatchAuditTests : OptiTimeScenarioBase
{
    /// <summary>
    /// A Harmony patch that fails to apply is the single most common way a
    /// client-side performance mod breaks against a game update: the version
    /// moves, the anchor method changes shape, the patch silently does
    /// nothing. OptiTime has shipped this class of bug several times - the
    /// GuiManager optimization was dead on 1.22.5 until v1.5.16.
    ///
    /// For every optimization the mod reports as ON, the mod must also have
    /// logged that it loaded the patch - or, for the one that may decline by
    /// design, logged why it declined. Reporting ON with neither is the exact
    /// silent failure this looks for, and it is invisible to every performance
    /// test because a dead patch simply costs nothing.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task EveryEnabledOptimizationAppliedItsHarmonyPatch()
    {
        await WarmUpAsync();

        IReadOnlyDictionary<string, OptionState> states = await ReadStatusAsync();
        string log = ClientLogText();

        List<string> silentlyDead = [];
        List<string> declined = [];

        foreach ((string option, LoadAnnouncement announcement) in OptiTimeConfig.LoadAnnouncements)
        {
            if (!states.TryGetValue(option, out OptionState state) || !state.Enabled) continue;

            if (log.Contains(announcement.Loaded, StringComparison.OrdinalIgnoreCase)) continue;

            if (announcement.Declined is not null &&
                log.Contains(announcement.Declined, StringComparison.OrdinalIgnoreCase))
            {
                // Correct behaviour, but the status line does not say so: the
                // player reads "shadowentity: ON" and pays for nothing.
                declined.Add(
                    $"{option} reports ON but did not apply its patch - the mod declined " +
                    $"({announcement.Declined}), and the only evidence is one log line");
                continue;
            }

            silentlyDead.Add($"{option} is ON but never logged '{announcement.Loaded}'");
        }

        await WriteArtifactAsync(nameof(BootPatchAuditTests), "boot-log.txt", log);

        string summary =
            $"patches applied: {OptiTimeConfig.LoadAnnouncements.Count - declined.Count - silentlyDead.Count}" +
            $"/{OptiTimeConfig.LoadAnnouncements.Count}" +
            (declined.Count == 0 ? string.Empty : $"; declined: {string.Join("; ", declined)}");

        Console.WriteLine(summary);
        Console.WriteLine($"enabled at boot: {states.Count(s => s.Value.Enabled)}/{states.Count}");

        Assert.True(silentlyDead.Count == 0,
            "The mod reports these optimizations as enabled but never applied their " +
            "Harmony patch, so they are doing nothing while claiming to work:\n  " +
            string.Join("\n  ", silentlyDead) +
            "\n\nEither the anchor method moved in this game version or the patch threw " +
            "silently. This is the v1.5.16 GuiManager bug shape.");
    }
}

/// <summary>
/// The mod's own boot log, in a class of their own for the same reason:
/// Pharos clears the client log on rollback, so only the first test in a class
/// sees what the mod logged while booting.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class BootLogTests : OptiTimeScenarioBase
{
    /// <summary>
    /// The mod must never log an error merely to report that it turned
    /// something off. A disabled optimization is a warning or a chat line; an
    /// error means something threw.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task BootsWithoutErrors()
    {
        await WarmUpAsync();

        Assert.True(Client!.Logs.Errors.Count == 0,
            "The client logged errors while booting or running with the mod:\n  " +
            string.Join("\n  ", Client.Logs.Errors));
    }

    /// <summary>
    /// The mod's own wording matters more than its behaviour here. Every
    /// notification it emits is prefixed <c>[OptiTime]</c>, every diag line
    /// <c>[OptiTime/diag/&lt;module&gt;]</c>; a stray unprefixed line is
    /// indistinguishable from a vanilla message to anyone reading the log after
    /// a crash.
    ///
    /// Scoped to what happens after the test clears the log and issues a
    /// command, rather than the whole boot log. The engine's own mod loader
    /// also prints lines containing "optitime" - the dependency order, the
    /// system types it instantiated, the assembly path - and those are the game
    /// talking about a mod, not the mod talking. Trying to separate the two by
    /// substring is not tractable; scoping to the mod's own output is.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task EveryModMessageIsPrefixed()
    {
        await WarmUpAsync();
        await RunCommandOkAsync($"{OptiTimeConfig.Command} diag all on");

        // Everything up to here is boot chatter from the engine.
        Client!.Logs.Clear();

        await RunCommandAsync($"{OptiTimeConfig.Command} status");
        await Session!.StepFramesAsync(120);
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");
        await RunCommandAsync($"{OptiTimeConfig.Command} interpdiag dump");
        await RunCommandAsync($"{OptiTimeConfig.Command} nonsense");
        await RunCommandAsync($"{OptiTimeConfig.Command} profile on");

        // The mod writes through two prefixes: `[OptiTime] ...` from its
        // notifications, and `[OptiTime/diag/<module>] ...` from the counter
        // dump. Both count. A third shape - an OptiTime line carrying no prefix
        // at all - is the one being looked for.
        string[] stray = [.. Client!.Logs.Entries
            .Select(e => e.Message)
            .Where(m => m.Contains("OptiTime", StringComparison.OrdinalIgnoreCase))
            .Where(m => !m.Contains("[OptiTime]", StringComparison.Ordinal)
                     && !m.Contains("[OptiTime/diag/", StringComparison.Ordinal))];

        await WriteArtifactAsync(nameof(BootLogTests), "post-command-log.txt", ClientLogText());

        Console.WriteLine(
            $"{Client!.Logs.Entries.Count} line(s) logged after the commands, " +
            $"{stray.Length} of them mentioning OptiTime without the prefix");

        Assert.True(stray.Length == 0,
            "Lines the mod logged in response to its own commands that mention it without " +
            "the '[OptiTime]' prefix:\n  " + string.Join("\n  ", stray.Take(20)));
    }
}
