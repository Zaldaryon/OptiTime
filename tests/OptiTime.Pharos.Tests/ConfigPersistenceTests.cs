using System.Text.Json;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// The mod's configuration is a file the player edits by hand and the mod
/// rewrites behind their back whenever a command runs. Both halves matter:
/// a lost flag silently turns an optimization off, and a corrupted file stops
/// the mod loading at all.
///
/// The file is read from the client's own data folder, which Pharos isolates
/// per run, so what is asserted here is exactly what a fresh install produces
/// and exactly what a player's file would look like after a toggle.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class ConfigPersistenceTests : OptiTimeScenarioBase
{
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    public async Task ModWritesAConfigFileOnFirstRun()
    {
        await WarmUpAsync();

        Assert.True(File.Exists(ModConfigPath),
            $"No config at '{ModConfigPath}'. A player who changes nothing still needs " +
            "a file to edit, and the mod's own loader falls back to a hard-coded " +
            "default when the file is absent - so its absence hides every later " +
            "default-value problem.");
    }

    /// <summary>
    /// Every optimization must exist in the file, and the file must be readable
    /// JSON.
    ///
    /// A flag that exists in code but not in the persisted class is a flag that
    /// resets on every launch. That is invisible in a single session, which is
    /// why it needs an explicit test rather than being left to a soak.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    public async Task ConfigFileIsValidJsonAndCoversEveryOption()
    {
        await WarmUpAsync();

        string json = await File.ReadAllTextAsync(ModConfigPath);
        await WriteArtifactAsync(nameof(ConfigPersistenceTests), "OptiTime.json", json);

        Dictionary<string, JsonElement> root;
        try
        {
            root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        }
        catch (JsonException ex)
        {
            Assert.Fail($"'{ModConfigPath}' is not valid JSON: {ex.Message}\n\n{json}");
            return;
        }

        List<string> missing = [.. OptiTimeConfig.Options.Values
            .Where(property => !root.ContainsKey(property))
            .Distinct(StringComparer.Ordinal)];

        Assert.True(missing.Count == 0,
            $"The config file has no entry for: {string.Join(", ", missing)}.\n" +
            "Those options will reset to a hard-coded default on every launch, " +
            "because the mod only writes back what the serialised class contains.");

        // And each one has to be a real boolean, not a string or a number: the
        // mod reads them with plain property setters.
        foreach ((string option, string property) in OptiTimeConfig.Options)
        {
            if (!root.TryGetValue(property, out JsonElement value)) continue;

            Assert.True(value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"'{property}' (the option '{option}') is {value.ValueKind} in the config " +
                "file, not a boolean.");
        }
    }

    /// <summary>
    /// Toggling through the command has to reach the file, not just the
    /// in-memory object. This is the failure mode where the command reports
    /// success, the status line reflects the change, and the next launch is
    /// back to the old setting.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    public async Task ToggleIsWrittenThroughToTheFile()
    {
        await WarmUpAsync();

        string property = OptiTimeConfig.Options["particles"];

        try
        {
            await SetOptimizationAsync("particles", enabled: false);
            Assert.False(await ReadConfigBoolAsync(property),
                $"'{property}' is still true in the file after 'particles off'. " +
                "The command changed the object but not the disk.");

            await SetOptimizationAsync("particles", enabled: true);
            Assert.True(await ReadConfigBoolAsync(property),
                $"'{property}' is still false in the file after 'particles on'.");
        }
        finally
        {
            await SetOptimizationAsync("particles", enabled: true);
        }
    }

    /// <summary>
    /// A first install turns the visual shader options on. The mod's
    /// <c>CreateFirstInstallConfig</c> overrides the class defaults for
    /// <c>ShaderOptimizations</c> and <c>BlurOptimizationEnabled</c>, which is
    /// the opposite of what the plain property initialisers say - so a test that
    /// reads the class defaults instead of the file gets the wrong answer for
    /// both, and the difference is invisible to anyone who never deletes their
    /// config.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    public async Task FirstInstallEnablesTheShaderOptions()
    {
        await WarmUpAsync();

        string json = await File.ReadAllTextAsync(ModConfigPath);

        Assert.True(await ReadConfigBoolAsync(OptiTimeConfig.Options["shaders"]),
            "A fresh install left 'ShaderOptimizations' off, but OptiTimeMod's " +
            "CreateFirstInstallConfig turns it on. A player would silently lose the " +
            "shader swap that ships by default.");
    }

    /// <summary>
    /// Reading the status twice with nothing in between must give the same
    /// answer. A status line that drifts - most plausibly because it reports a
    /// live counter or a conflict reason that changes over time - is unusable as
    /// a scripting interface.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    public async Task StatusIsStableAcrossRepeatedReads()
    {
        await WarmUpAsync();

        await Session!.StepFramesAsync(60);

        IReadOnlyDictionary<string, OptionState> first = await ReadStatusAsync();
        await Session!.StepFramesAsync(120);
        IReadOnlyDictionary<string, OptionState> second = await ReadStatusAsync();

        foreach ((string option, OptionState before) in first)
        {
            Assert.True(second.TryGetValue(option, out OptionState after),
                $"'{option}' disappeared from the status output between two reads.");

            Assert.Equal(before.Enabled, after.Enabled);
            Assert.Equal(before.Conflict, after.Conflict);
        }
    }

    /// <summary>
    /// The mod's runtime hook for external config tooling,
    /// <c>OptiTimeMod.ApplyRuntimeConfig</c>, is public and static precisely so
    /// a ConfigLib-style mod can call it after changing settings out of band.
    /// Every one of the hooks it drives has to tolerate being called when
    /// nothing is loaded yet, because that is the state a config screen is in
    /// when it runs.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 1_800_000)]
    public async Task RuntimeConfigHookSurvivesRepeatedApplication()
    {
        await WarmUpAsync();

        // Every toggle goes through ApplyRuntimeConfig. Two rounds of the full
        // set exercises all the Configure/SetLogger call sites it reaches,
        // including the particle and GUI paths that hold static state. Two,
        // not ten: each round is forty toggles plus twenty status reads, and
        // ten rounds of that overruns the watchdog without reaching a call site
        // the two do not.
        for (int round = 0; round < 2; round++)
        {
            IReadOnlyDictionary<string, OptionState> before = await SnapshotStatusAsync();
            foreach (string option in OptiTimeConfig.OptionNames)
            {
                await SetOptimizationAsync(option, enabled: round % 2 == 0);
            }

            await RestoreStatusAsync(before);
        }

        await Session!.StepFramesAsync(30);
        AssertNoUnexpectedClientErrors("config");
    }

    private async Task<bool> ReadConfigBoolAsync(string property)
    {
        string json = await File.ReadAllTextAsync(ModConfigPath);
        Dictionary<string, JsonElement>? root =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        return root is not null
            && root.TryGetValue(property, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }
}