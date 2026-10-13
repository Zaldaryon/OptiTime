using System.IO.Compression;
using System.Text.Json;
using Zaldaryon.Pharos.Assertions;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// The mod's translations, checked against the artifact a player downloads and
/// against the running client's own language loader.
///
/// Two separate failure modes live here. The first is packaging: a language
/// file that never made it into the zip, or one whose keys drifted from
/// <c>en.json</c>, means a player sees raw keys like
/// <c>optitime:cmd-enabled</c> in their chat. The second is runtime: a key that
/// is present in the file but never resolves at runtime, which Pharos can catch
/// directly because it observes the game's own missing-translation accounting.
/// </summary>
public static class ModArtifact
{
    private static readonly Lazy<string?> ResolvedZipPath = new(ResolveZipPath);

    /// <summary>
    /// The built mod zip, resolved from the suite's declared artifact path.
    ///
    /// Read from the zip rather than from <c>assets/</c> in the source tree: the
    /// zip is what ships, and a file that is present in the repository but
    /// missing from the build is exactly the bug this cannot see otherwise.
    ///
    /// Named <c>ZipPath</c> rather than <c>Path</c> so it does not shadow
    /// <see cref="System.IO.Path"/> for every caller in this file.
    /// </summary>
    public static string? ZipPath => ResolvedZipPath.Value;

    public static string? ResolveZipPath()
    {
        string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            OptiTimeConfig.ModAssembly));

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Every entry in the zip under a prefix, as archive paths.</summary>
    public static IReadOnlyList<string> Entries(ZipArchive archive, string prefix) =>
        [.. archive.Entries
            .Select(e => e.FullName)
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];

    public static ZipArchive Open()
    {
        string zipPath = ZipPath
            ?? throw new InvalidOperationException(
                $"The built mod was not found at '{OptiTimeConfig.ModAssembly}' relative to " +
                $"'{AppContext.BaseDirectory}'. Build the mod before running this suite.");

        return ZipFile.OpenRead(zipPath);
    }
}

/// <summary>
/// Static checks over the built mod artifact. No game boot: these read the zip,
/// so they run in seconds and can be the first thing a CI job does.
/// </summary>
public partial class ModArtifactTests
{
    /// <summary>
    /// The zip is opened per test rather than cached: an archive is a
    /// disposable, and a shared one disposed by the first test turns the rest
    /// into <c>ObjectDisposedException</c>s that look like real failures.
    ///
    /// The set of languages to compare against comes from the game's own
    /// <c>languages.json</c> registry rather than from the files sitting in the
    /// lang folder - that folder also contains the registry itself, and a game
    /// update that adds a language adds it there first.
    /// </summary>
    [Fact]
    public void ModShipsALanguageFileForEveryVanillaLanguage()
    {
        using ZipArchive zip = ModArtifact.Open();

        string[] shipped = [.. ModArtifact
            .Entries(zip, "assets/optitime/lang/")
            .Select(n => Path.GetFileName(n))
            .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)];

        Assert.Contains("en.json", shipped);

        string[] vanilla = VanillaLanguageFiles();

        Console.WriteLine(
            $"the mod ships {shipped.Length} language file(s); the game declares {vanilla.Length}");

        string[] missing = [.. vanilla.Except(shipped, StringComparer.OrdinalIgnoreCase)];
        string[] extra = [.. shipped.Except(vanilla, StringComparer.OrdinalIgnoreCase)];

        Assert.True(missing.Length == 0,
            $"The game declares {vanilla.Length} languages; the mod is missing " +
            $"{string.Join(", ", missing)}." +
            (extra.Length == 0
                ? string.Empty
                : $" The mod also ships {string.Join(", ", extra)}, which the game does not " +
                  "declare."));
    }

    /// <summary>
    /// The languages the game itself declares, from
    /// <c>assets/game/lang/languages.json</c>.
    /// </summary>
    private static string[] VanillaLanguageFiles()
    {
        string registry = Path.Combine(GameInstall(), "assets", "game", "lang", "languages.json");

        Assert.True(File.Exists(registry),
            $"'{registry}' is not there. The vanilla language set is read from the game's " +
            "own registry rather than hardcoded, so a game update that adds a language is " +
            "caught without editing this file.");

        // The registry is JSONC: unquoted keys and trailing commas.
        string[] codes = [.. LanguageCode()
            .Matches(File.ReadAllText(registry))
            .Select(m => m.Groups["code"].Value)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Assert.NotEmpty(codes);

        return [.. codes.Select(c => c + ".json").OrderBy(n => n, StringComparer.Ordinal)];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bcode\s*:\s*""(?<code>[^""]+)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LanguageCode();

    /// <summary>
    /// Every key present in <c>en.json</c> has to be present in every other
    /// file. A language file that gained or lost a key falls back to English at
    /// runtime, which is survivable, but a key that was renamed in English only
    /// is not - the player sees the key itself.
    /// </summary>
    [Fact]
    public void EveryLanguageFileHasTheSameKeysAsEnglish()
    {
        using ZipArchive zip = ModArtifact.Open();

        Dictionary<string, JsonElement> english = ReadLang(zip, "en.json");
        Assert.NotEmpty(english);

        List<string> problems = [];

        foreach (string file in LangFiles(zip))
        {
            if (file.Equals("en.json", StringComparison.OrdinalIgnoreCase)) continue;

            Dictionary<string, JsonElement> other = ReadLang(zip, file);

            string[] missing = [.. english.Keys.Where(k => !other.ContainsKey(k))];
            string[] extra = [.. other.Keys.Where(k => !english.ContainsKey(k))];

            if (missing.Length > 0)
            {
                problems.Add(
                    $"{file}: missing {string.Join(", ", missing.Take(8))}" +
                    (missing.Length > 8 ? $" (+{missing.Length - 8} more)" : string.Empty));
            }

            if (extra.Length > 0)
            {
                problems.Add(
                    $"{file}: has keys English does not - " +
                    $"{string.Join(", ", extra.Take(8))}" +
                    (extra.Length > 8 ? $" (+{extra.Length - 8} more)" : string.Empty));
            }
        }

        Assert.True(problems.Count == 0,
            $"Language files disagree with en.json ({english.Count} keys):\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>
    /// No value may be blank. A key present but empty renders as nothing, which
    /// is worse than a missing key because it looks translated.
    /// </summary>
    [Fact]
    public void NoTranslationValueIsBlank()
    {
        using ZipArchive zip = ModArtifact.Open();

        List<string> blanks = [];

        foreach (string file in LangFiles(zip))
        {
            foreach ((string key, JsonElement value) in ReadLang(zip, file))
            {
                if (value.ValueKind != JsonValueKind.String) continue;

                string text = value.GetString() ?? string.Empty;
                if (text.Trim().Length == 0)
                {
                    blanks.Add($"{file}:{key}");
                }
            }
        }

        Assert.True(blanks.Count == 0,
            $"These translations are blank:\n  {string.Join("\n  ", blanks.Take(40))}" +
            (blanks.Count > 40 ? $"\n  (+{blanks.Count - 40} more)" : string.Empty));
    }

    /// <summary>
    /// Rich-text placeholders have to survive translation. The mod's strings
    /// interpolate the option name into a sentence; a translator who rewords
    /// the sentence and drops the placeholder ships a message that no longer
    /// names what was toggled.
    /// </summary>
    [Fact]
    public void EveryTranslationKeepsItsPlaceholders()
    {
        using ZipArchive zip = ModArtifact.Open();

        Dictionary<string, JsonElement> english = ReadLang(zip, "en.json");
        List<string> problems = [];

        foreach (string file in LangFiles(zip))
        {
            if (file.Equals("en.json", StringComparison.OrdinalIgnoreCase)) continue;

            Dictionary<string, JsonElement> other = ReadLang(zip, file);

            foreach ((string key, JsonElement value) in english)
            {
                if (value.ValueKind != JsonValueKind.String) continue;
                if (!other.TryGetValue(key, out JsonElement translated)) continue;
                if (translated.ValueKind != JsonValueKind.String) continue;

                string[] expected = [.. Match(value.GetString()!).Distinct().Order()];
                string[] actual = [.. Match(translated.GetString()!).Distinct().Order()];

                if (!expected.SequenceEqual(actual))
                {
                    problems.Add(
                        $"{file}:{key} has [{string.Join(", ", actual)}], " +
                        $"English has [{string.Join(", ", expected)}]");
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"Translations dropped or added a rich-text placeholder:\n  " +
            string.Join("\n  ", problems.Take(40)) +
            (problems.Count > 40 ? $"\n  (+{problems.Count - 40} more)" : string.Empty));
    }

    /// <summary>
    /// The four shader files the mod ships are the entire visual feature. If the
    /// packaging step drops one, the shader override silently does nothing on
    /// a fresh install, and the mod still reports shaders as ON.
    /// </summary>
    [Fact]
    public void ModShipsTheFourShaderFilesItClaimsToOverride()
    {
        using ZipArchive zip = ModArtifact.Open();

        string[] expected =
        [
            "assets/game/shaders/blur.fsh",
            "assets/game/shaders/blur.vsh",
            "assets/game/shaders/chunkliquid.fsh",
            "assets/game/shaders/chunkshadowmap.fsh",
        ];

        string[] present = [.. ModArtifact.Entries(zip, "assets/").Select(n => n.ToLowerInvariant())];

        string[] missing = [.. expected.Where(e => !present.Contains(e.ToLowerInvariant()))];

        Assert.True(missing.Length == 0,
            $"The mod declares these shader overrides in OptiTimeShaderPaths but the " +
            $"built zip does not contain them: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// Rich-text placeholders, matched in both the English string and the
    /// translation so the two can be compared directly.
    /// </summary>
    private static partial class Placeholders
    {
        [System.Text.RegularExpressions.GeneratedRegex(@"\{[^{}]*\}")]
        internal static partial System.Text.RegularExpressions.Regex Pattern();
    }

    private static string[] Match(string text) =>
        [.. Placeholders.Pattern().Matches(text).Select(m => m.Value)];

    private static IEnumerable<string> LangFiles(ZipArchive zip) =>
        ModArtifact.Entries(zip, "assets/optitime/lang/")
            .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.Ordinal)!;

    private static Dictionary<string, JsonElement> ReadLang(ZipArchive zip, string file)
    {
        ZipArchiveEntry entry = zip.Entries.FirstOrDefault(
            e => e.FullName.EndsWith($"/{file}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{file}' is not in the built mod.");

        using StreamReader reader = new(entry.Open());
        string json = reader.ReadToEnd();

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new InvalidOperationException($"'{file}' is not a JSON object.");
    }

    private static string GameInstall()
    {
        string? configured = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        Assert.False(string.IsNullOrWhiteSpace(configured),
            "Set VINTAGE_STORY so the suite can compare the mod's language list against the game's own.");
        Assert.True(Directory.Exists(configured), "VINTAGE_STORY must point to an installed Vintage Story directory.");

        return configured!;
    }
}

/// <summary>
/// Translation behaviour at runtime, through the game's own missing-key
/// accounting rather than by reading files.
/// </summary>
[ServerWorld(
    seed: OptiTimeConfig.WorldSeed,
    playStyle: OptiTimeConfig.PlayStyle,
    worldType: OptiTimeConfig.WorldType)]
[ClientSetting("viewDistance", OptiTimeConfig.ViewDistance)]
[PharosMods(OptiTimeConfig.ModAssembly)]
public class TranslationRuntimeTests : OptiTimeScenarioBase
{
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task NewlySupportedLanguagesResolveTheirOwnText()
    {
        await WarmUpAsync();
        foreach (string language in new[] { "be", "bg", "lt" })
        {
            using IDisposable selected = Client!.Lang.Use(language);
            await Session!.StepFramesAsync(10);
            string path = Path.Combine(ModSourceAnalysis.RepositoryRoot,
                "assets", "optitime", "lang", language + ".json");
            using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(path));
            string text = expected.RootElement.GetProperty("optitime:setting-recipe").GetString()!;
            Assert.Equal(text, Vintagestory.API.Config.Lang.Get("optitime:setting-recipe"));
            string unknown = expected.RootElement.GetProperty("optitime:cmd-unknown")
                .GetString()!.Replace("{0}", "nonsense", StringComparison.Ordinal);
            CommandOutput reply = await RunCommandAsync($"{OptiTimeConfig.Command} nonsense");
            Assert.Contains(unknown, reply.Text);
        }
    }

    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task DescriptionsFollowTheClientLanguage()
    {
        await WarmUpAsync();
        CommandOutput english;
        using (Client!.Lang.Use("en"))
        {
            english = await RunCommandAsync($"{OptiTimeConfig.Command} flysound");
        }
        using (Client!.Lang.Use("de"))
        {
            await Session!.StepFramesAsync(10);
            CommandOutput german = await RunCommandAsync($"{OptiTimeConfig.Command} flysound");
            Assert.NotEqual(english.Text, german.Text);
            Assert.DoesNotContain("optitime:description-", german.Text);
        }
    }

    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task EveryModChatLineResolvesToATranslation()
    {
        await WarmUpAsync();

        // Exercise every user-facing string the mod owns: help, status, the
        // enable/disable confirmations, the unknown-option reply, the profile
        // lines, and the diag dumps.
        foreach (string option in OptiTimeConfig.OptionNames)
        {
            await RunCommandAsync($"{OptiTimeConfig.Command} {option}");
            await RunCommandAsync($"{OptiTimeConfig.Command} {option} off");
            await RunCommandAsync($"{OptiTimeConfig.Command} {option} on");
        }

        await RunCommandAsync(OptiTimeConfig.Command);
        await RunCommandAsync($"{OptiTimeConfig.Command} nonsense");
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all on");
        await RunCommandAsync($"{OptiTimeConfig.Command} diag all dump");
        await RunCommandAsync($"{OptiTimeConfig.Command} profile on");
        await RunCommandAsync($"{OptiTimeConfig.Command} profile");
        await RunCommandAsync($"{OptiTimeConfig.Command} profile off");

        LanguageDriver lang = Client!.Lang;
        lang.Reset();

        await RunCommandAsync($"{OptiTimeConfig.Command} status");
        await Session!.StepFramesAsync(10);

        string[] missing = [.. lang.MissingKeysOf(OptiTimeConfig.ModId)
            .Select(m => m.Key)
            .Distinct()
            .ToArray()];

        await WriteArtifactAsync(nameof(TranslationRuntimeTests), "missing.txt",
            string.Join('\n', missing));

        Assert.True(missing.Length == 0,
            $"The client could not resolve {missing.Length} OptiTime translation key(s): " +
            $"{string.Join(", ", missing.Take(20))}. A player sees the key itself, not a " +
            "message.");
    }

    /// <summary>
    /// Switching language at runtime must switch the mod's messages too. The
    /// mod resolves everything through <c>Lang.Get</c>, which is correct, and
    /// this is the test that would catch it going back to literals.
    /// </summary>
    [ClientServerScenario(TimeoutMs = 600_000)]
    public async Task ModMessagesFollowTheClientLanguage()
    {
        await WarmUpAsync();

        LanguageDriver lang = Client!.Lang;
        string original = lang.Current;

        try
        {
            CommandOutput english = await RunCommandAsync($"{OptiTimeConfig.Command} nonsense");

            using IDisposable german = lang.Use("de");
            await Session!.StepFramesAsync(10);

            CommandOutput translated = await RunCommandAsync($"{OptiTimeConfig.Command} nonsense");

            Assert.False(string.IsNullOrWhiteSpace(translated.Text),
                "The mod printed nothing after the client language changed to German.");

            Assert.NotEqual(english.Text, translated.Text);
        }
        finally
        {
            // `Use` restores on dispose; this covers the case where the test
            // failed between the switch and the dispose.
            if (lang.Current != original)
            {
                using IDisposable restore = lang.Use(original);
            }
        }
    }
}
