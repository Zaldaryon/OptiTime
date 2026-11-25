using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

namespace OptiTime.Tests;

public class OptimumCompatibilityTests
{
    private const string SupportedGameVersion = "1.22.7";

    private static readonly string[] OptimumLangKeys =
    {
        "optitime:compat-optimum-title",
        "optitime:compat-optimum-desc",
        "optitime:compat-optimum-reason"
    };

    static OptimumCompatibilityTests()
    {
        string gameDirectory = FindGameDirectory();
        if (gameDirectory == null)
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Lib"), Path.Combine(gameDirectory, "Mods") })
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }

            return null;
        };

        foreach (string relative in new[] { "VintagestoryLib.dll", "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll" })
        {
            string path = Path.Combine(gameDirectory, relative);
            if (File.Exists(path) && !AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal)))
            {
                Assembly.LoadFrom(path);
            }
        }
    }

    [Fact]
    public void DetectReportsNoHostOnAVanillaApi()
    {
        Assert.False(OptimumCompatibility.Detect());
        Assert.False(OptimumCompatibility.IsHost);
    }

    [Fact]
    public void SignatureDoesNotDependOnTheCulture()
    {
        MethodInfo sample = typeof(OptimumCompatibilityTests).GetMethod(nameof(SampleWithFloatLiteral), BindingFlags.NonPublic | BindingFlags.Static);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            string brazilian = OptimumOwnership.Signature(sample);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = OptimumOwnership.Signature(sample);

            Assert.Equal(invariant, brazilian);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void SignatureChangesWhenTheBodyChanges()
    {
        MethodInfo first = typeof(OptimumCompatibilityTests).GetMethod(nameof(SampleWithFloatLiteral), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo second = typeof(OptimumCompatibilityTests).GetMethod(nameof(SampleWithAnotherLiteral), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo guarded = typeof(OptimumCompatibilityTests).GetMethod(nameof(SampleWithTryCatch), BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotEqual(OptimumOwnership.Signature(first), OptimumOwnership.Signature(second));
        Assert.NotEqual(OptimumOwnership.Signature(first), OptimumOwnership.Signature(guarded));
    }

    [Fact]
    public void VanillaSignatureTableMatchesTheGameAssemblies()
    {
        if (GameVersion.ShortGameVersion != SupportedGameVersion)
        {
            return;
        }

        SortedDictionary<string, string> actual = OptimumOwnership.DescribeAllTargets();
        IReadOnlyDictionary<string, string> recorded = OptimumVanillaSignatures.ForGameVersion(SupportedGameVersion);
        Assert.NotNull(recorded);

        bool same = actual.Count == recorded.Count && actual.All(p => recorded.TryGetValue(p.Key, out string value) && value == p.Value);
        Assert.True(same, "The recorded vanilla signatures differ from the game. Replace the table with:\n"
            + string.Join("\n", actual.Select(p => $"            [\"{p.Key}\"] = \"{p.Value}\",")));
    }

    [Fact]
    public void VanillaShaderHashTableMatchesTheGameFiles()
    {
        string gameDirectory = FindGameDirectory();
        if (gameDirectory == null || GameVersion.ShortGameVersion != SupportedGameVersion)
        {
            return;
        }

        IReadOnlyDictionary<string, string> recorded = OptimumVanillaSignatures.ShadersForGameVersion(SupportedGameVersion);
        Assert.NotNull(recorded);

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in new[] { "shaders/blur.fsh", "shaders/blur.vsh", "shaders/chunkliquid.fsh", "shaders/chunkshadowmap.fsh" })
        {
            actual[path] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(gameDirectory, "assets", "game", path)))).ToLowerInvariant();
        }

        bool same = actual.Count == recorded.Count && actual.All(p => recorded.TryGetValue(p.Key, out string value) && value == p.Value);
        Assert.True(same, "The recorded vanilla shader hashes differ from the game. Replace the table with:\n"
            + string.Join("\n", actual.Select(p => $"            [\"{p.Key}\"] = \"{p.Value}\",")));
    }

    [Fact]
    public void EveryFeatureStaysActiveOnAVanillaBodyAndYieldsToOptimumSettings()
    {
        if (GameVersion.ShortGameVersion != SupportedGameVersion)
        {
            return;
        }

        foreach (OptimumOwnership.Feature feature in OptimumOwnership.Features)
        {
            Assert.Null(OptimumOwnership.YieldReason(feature, SupportedGameVersion, _ => false));

            string withSetting = OptimumOwnership.YieldReason(feature, SupportedGameVersion, name => name == feature.OptimumSetting);
            Assert.Equal(feature.OptimumSetting != null, withSetting != null);
        }
    }

    [Fact]
    public void AnUnknownGameVersionMakesEveryFeatureYield()
    {
        foreach (OptimumOwnership.Feature feature in OptimumOwnership.Features)
        {
            string reason = OptimumOwnership.YieldReason(feature, "0.0.0", _ => false);
            Assert.Contains("no vanilla signatures", reason);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(":")]
    [InlineData("NoColonHere")]
    [InlineData("No.Such.Type:Method")]
    [InlineData("System.String:Intern")]
    [InlineData("System.Object:Finalize")]
    public void OddTargetsNeverThrowAndMakeTheFeatureYield(string target)
    {
        var feature = new OptimumOwnership.Feature("Anything", new[] { target });

        string reason = OptimumOwnership.YieldReason(feature, SupportedGameVersion, name => throw new InvalidOperationException(name));

        Assert.NotNull(reason);
    }

    [Fact]
    public void TheOptimumChecksCatchTheirOwnFailures()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));

        string policy = Section(source, "private void ApplyOptimumPolicy", "private void ApplyOptimumShaderPolicy");
        Assert.Contains("catch (Exception ex)", policy);
        Assert.Contains("ForceOffForSession", policy);

        string shaders = Section(source, "private void ApplyOptimumShaderPolicy(", "private void ApplyOptimumShaderPolicyCore");
        Assert.Contains("catch (Exception ex)", shaders);
        Assert.Contains("HideOptiTimeShaders", shaders);

        string audit = Section(source, "private void AuditOptimumPatches", "private void DetectAndHandleConflicts");
        Assert.True(audit.Split("catch (Exception ex)").Length - 1 >= 2, "Both the loop and each patch audit must handle failures.");
    }

    [Fact]
    public void AMissingTargetMakesTheFeatureYield()
    {
        if (GameVersion.ShortGameVersion != SupportedGameVersion)
        {
            return;
        }

        var feature = new OptimumOwnership.Feature("Anything", new[] { "Vintagestory.Client.NoObf.NoSuchType:NoSuchMethod" });

        Assert.Contains("was not found", OptimumOwnership.YieldReason(feature, SupportedGameVersion, _ => false));
    }

    [Fact]
    public void EveryPatchTargetInTheSourceIsCoveredByTheFeatureTable()
    {
        string root = FindRepositoryRoot();
        var covered = new HashSet<string>(OptimumOwnership.Features.SelectMany(f => f.Targets).Concat(OptimumOwnership.AlwaysOnTargets), StringComparer.Ordinal);
        var literal = new Regex("AccessTools\\.Method\\(\\s*\"([A-Za-z0-9_.]+:[A-Za-z0-9_]+)\"", RegexOptions.Compiled);

        var missing = new List<string>();
        foreach (string directory in new[] { "Core", "Entity", "Gui", "Systems", "Audio" })
        {
            foreach (string file in Directory.GetFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in literal.Matches(File.ReadAllText(file)))
                {
                    if (!covered.Contains(match.Groups[1].Value))
                    {
                        missing.Add(Path.GetFileName(file) + " -> " + match.Groups[1].Value);
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "These patch targets are missing from OptimumOwnership:\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData("/home/player/Games/optitime-test/assets/game/", false)]
    [InlineData("/tmp/optitime-data/Cache/unpack/Other-1.0.zip_abc/assets", false)]
    [InlineData("/home/player/.local/share/vintagestory/assets/survival/", false)]
    [InlineData("/home/player/.config/VintagestoryData/Mods/OptiTime-1.5.17.zip", true)]
    [InlineData("/tmp/optitime-data/Cache/unpack/OptiTime-1.5.17.zip_f5bb10ba6b51/assets", true)]
    [InlineData("C:\\Users\\player\\AppData\\Roaming\\VintagestoryData\\Mods\\optitime\\assets", true)]
    [InlineData("/home/player/optitime/Cache/unpack/Other-1.0.zip_abc/assets", false)]
    [InlineData("/home/player/optitime/assets/game/", false)]
    [InlineData("/home/player/optitime/assets/survival", false)]
    [InlineData("/home/player/optitime/Mods/OptiTime-1.5.17.zip/", true)]
    [InlineData("/home/player/optitime/Mods/optitime/assets", true)]
    [InlineData("", false)]
    public void OnlyTheModsOwnFoldersAreOptiTimeOrigins(string originPath, bool expected)
    {
        Assert.Equal(expected, OptiTimeMod.IsModOriginPath(originPath, "OptiTime-1.5.17.zip", "optitime"));
    }

    [Fact]
    public void ForcingAnOptimizationOffKeepsTheStoredChoice()
    {
        OptiTimeConfig config = new() { EntityAnimationOptimizations = true };

        config.ForceOffForSession(nameof(OptiTimeConfig.EntityAnimationOptimizations), "Provided by Optimum");

        Assert.False(config.EntityAnimationOptimizations);
        Assert.True(config.IsForcedOffForSession(nameof(OptiTimeConfig.EntityAnimationOptimizations)));
        Assert.True(config.IsConflictDisabled(nameof(OptiTimeConfig.EntityAnimationOptimizations)));
        Assert.Equal("Provided by Optimum", config.GetConflictReason(nameof(OptiTimeConfig.EntityAnimationOptimizations)));
        Assert.True(config.GetStoredValue(nameof(OptiTimeConfig.EntityAnimationOptimizations)));

        OptiTimeConfig persisted = config.PersistedView();
        Assert.NotSame(config, persisted);
        Assert.True(persisted.EntityAnimationOptimizations);
        Assert.False(config.EntityAnimationOptimizations);
    }

    [Fact]
    public void ChangingAForcedOffOptimizationOnlyChangesWhatIsStored()
    {
        OptiTimeConfig config = new() { ParticleOptimizations = true };
        config.ForceOffForSession(nameof(OptiTimeConfig.ParticleOptimizations), "Provided by Optimum");

        config.SetOptimization(nameof(OptiTimeConfig.ParticleOptimizations), false);

        Assert.False(config.ParticleOptimizations);
        Assert.False(config.GetStoredValue(nameof(OptiTimeConfig.ParticleOptimizations)));
        Assert.False(config.PersistedView().ParticleOptimizations);

        config.SetOptimization(nameof(OptiTimeConfig.ParticleOptimizations), true);

        Assert.False(config.ParticleOptimizations);
        Assert.True(config.PersistedView().ParticleOptimizations);
    }

    [Fact]
    public void WithoutOverridesThePersistedViewIsTheConfigItself()
    {
        OptiTimeConfig config = new();

        Assert.Same(config, config.PersistedView());

        config.SetOptimization(nameof(OptiTimeConfig.HandbookOptimizations), false);
        Assert.False(config.HandbookOptimizations);
        Assert.False(config.GetStoredValue(nameof(OptiTimeConfig.HandbookOptimizations)));
    }

    [Fact]
    public void TheOptimumPolicyNeverSavesOrReloadsAndRunsBeforeAnythingIsSaved()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));

        string policy = Section(source, "private void ApplyOptimumPolicy", "private void ApplyOptimumShaderPolicy");
        Assert.DoesNotContain(".Save(", policy);
        Assert.DoesNotContain("DisableOptimizationForConflict", policy);
        Assert.DoesNotContain("SetOptimization(", policy);

        string shaders = Section(source, "private void ApplyOptimumShaderPolicy", "private void AuditOptimumPatches");
        Assert.DoesNotContain(".Save(", shaders);

        int clientStart = source.IndexOf("public override void StartClientSide", StringComparison.Ordinal);
        int gate = source.IndexOf("ApplyOptimumPolicy(api);", clientStart, StringComparison.Ordinal);
        int firstSave = source.IndexOf("config.Save(api);", clientStart, StringComparison.Ordinal);
        int firstHarmony = source.IndexOf("new Harmony(", clientStart, StringComparison.Ordinal);
        int audit = source.IndexOf("AuditOptimumPatches(api);", clientStart, StringComparison.Ordinal);
        int finalize = source.IndexOf("api.Event.LevelFinalize += OnLevelFinalize;", clientStart, StringComparison.Ordinal);

        Assert.True(gate > clientStart && gate < firstSave && gate < firstHarmony);
        Assert.True(audit > firstHarmony && audit < finalize);
    }

    [Fact]
    public void RevertingSomeShadersHidesThemInTheOriginInsteadOfEditingTheAssetDictionary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));
        string revert = Section(source, "private void TryDisableOptiTimeShaders", "private bool IsOptiTimeOrigin");

        Assert.Contains("HideOptiTimeShaders(api, new List<string>(pathsToDisable), reason);", revert);
        Assert.DoesNotContain("allAssets.Remove", revert);
        Assert.DoesNotContain("AllAssets.Remove", revert);
    }

    [Fact]
    public void AssetsLoadedAppliesTheOptimumShaderPolicyBeforeTheRegularShaderLogic()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));
        int assets = source.IndexOf("public override void AssetsLoaded", StringComparison.Ordinal);
        int gate = source.IndexOf("ApplyOptimumShaderPolicy(clientApi);", assets, StringComparison.Ordinal);
        int regular = source.IndexOf("if (!config.ShaderOptimizations || ancestralBlissDetected)", assets, StringComparison.Ordinal);

        Assert.True(gate > assets && gate < regular);
    }

    [Fact]
    public void EveryLanguageFileHasTheOptimumMessages()
    {
        string langDirectory = Path.Combine(FindRepositoryRoot(), "assets", "optitime", "lang");
        string[] files = Directory.GetFiles(langDirectory, "*.json");
        Assert.Equal(34, files.Length);

        foreach (string file in files)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (string key in OptimumLangKeys)
            {
                Assert.True(document.RootElement.TryGetProperty(key, out JsonElement value), $"{Path.GetFileName(file)} is missing {key}");
                string text = value.GetString();
                Assert.False(string.IsNullOrWhiteSpace(text), $"{Path.GetFileName(file)} has an empty {key}");
                Assert.Contains("Optimum", text);
                Assert.DoesNotContain('—', text);
                Assert.DoesNotContain('–', text);
            }
        }
    }

    private static string Section(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"Could not find {start}");
        return source.Substring(from, to - from);
    }

    private static float SampleWithFloatLiteral(float value) => value * 1.5f + 0.25f;

    private static float SampleWithAnotherLiteral(float value) => value * 2.5f + 0.25f;

    private static float SampleWithTryCatch(float value)
    {
        try
        {
            return value * 1.5f + 0.25f;
        }
        catch (InvalidOperationException)
        {
            return 0f;
        }
    }

    private static string FindGameDirectory()
    {
        foreach (string candidate in new[]
        {
            Environment.GetEnvironmentVariable("VINTAGE_STORY"),
            Environment.GetEnvironmentVariable("VINTAGE_STORY_HOME"),
            Environment.GetEnvironmentVariable("VINTAGE_STORY_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "VintageStory"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vintagestory")
        })
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "VintagestoryAPI.dll")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OptiTime.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the OptiTime repository root.");
    }
}
