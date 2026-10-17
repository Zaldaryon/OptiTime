using System.Text.RegularExpressions;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Every knob the suite has, in one place. Nothing below should be duplicated
/// elsewhere: when a value changes, all tests change together.
/// </summary>
public static partial class OptiTimeConfig
{
    // ---------------------------------------------------------------- identity

    /// <summary>
    /// OptiTime's mod id, from modinfo.json. Confirmed by the crash reports on
    /// ModDB, which list the assembly as `com.zaldaryon.optitime` and the mod as
    /// `optitime@1.5.16`.
    ///
    /// Pharos attributes renderers, tick listeners and translation keys by this
    /// string, so a wrong value makes every per-module assertion silently
    /// vacuous. <c>ModuleInventoryTests</c> fails loudly if it is wrong.
    /// </summary>
    public const string ModId = "optitime";

    /// <summary>The client command that drives every optimization at runtime.</summary>
    public const string Command = ".optitime";

    /// <summary>
    /// Path to the built mod, relative to the test assembly's output folder.
    ///
    /// The output folder is <c>tests/OptiTime.Pharos.Tests/bin/&lt;cfg&gt;/&lt;tfm&gt;/</c>,
    /// which is five levels below the repository root, so this needs five
    /// <c>..</c> segments. Four resolves to <c>tests/bin/</c>, which does not
    /// exist, and Pharos then silently boots a client with no mod in it - every
    /// assertion passes vacuously.
    ///
    /// <c>[PharosMods]</c> only accepts compile-time constants, so this cannot
    /// come from an environment variable.
    /// </summary>
    public const string ModAssembly = "../../../../../bin/OptiTime-1.5.17.zip";

    /// <summary>The version the suite stages and every artifact is expected to carry.</summary>
    public const string ModVersion = "1.5.17";

    // ------------------------------------------------------------------ items

    /// <summary>
    /// Item codes the suite asks the game to resolve, most specific first.
    ///
    /// Vintage Story item codes are not the English names, and a wrong one
    /// throws <c>ArgumentException: "The game has no item or block ..."</c>
    /// from inside the registry rather than returning null. Worse, the obvious
    /// guesses are wrong in different ways per item: recipes match the
    /// <em>item</em> <c>plank-*</c> and output the <em>block</em>
    /// <c>planks-&#123;wood&#125;-ud</c>, and an iron axe is
    /// <c>axe-felling-iron</c> or <c>axe-scrap-iron</c> depending on which
    /// variant group the material lands in.
    ///
    /// Lists rather than single values, so the first code that resolves wins and
    /// a variant group changing between game versions degrades the test to the
    /// next candidate instead of failing it.
    /// </summary>
    public static readonly string[] PlankCodes = ["game:plank-oak", "game:planks-oak", "game:planks-oak-ud"];
    public static readonly string[] CobblestoneCodes =
    [
        // The block, and the sibling variants of a stone item, because the
        // registry a headless client exposes is narrower than the asset tree and
        // ResolveStack derives alternates from whatever does resolve.
        "game:cobblestone",
        "game:stone-granite",
        "game:marble",
    ];
    public static readonly string[] StickCodes = ["game:stick", "game:loosestick"];
    public static readonly string[] IronAxeCodes = ["game:axe-felling-iron", "game:axe-scrap-iron", "game:axe-bearded-iron"];
    public static readonly string[] FishingPoleCodes = ["game:fishingpole-simple-bamboo", "game:fishingpole-simple-wood"];
    public static readonly string[] RawFishCodes = ["game:fishraw", "game:fishchunk", "game:fishfillet"];

    /// <summary>
    /// The mod's config file, relative to the client's data path. Taken
    /// verbatim from <c>OptiTimeConfig.Load</c>:
    /// <c>Path.Combine(api.DataBasePath, "ModConfig", "OptiTime.json")</c>.
    /// </summary>
    public const string ModConfigRelativePath = "ModConfig/OptiTime.json";

    // ----------------------------------------------------------------- options

    /// <summary>
    /// Every option the mod's own command accepts, mapped to the config property
    /// behind it.
    ///
    /// Source of truth is <c>OptiTimeMod.OptimizationMap</c> and
    /// <c>OptiTimeConfig.PrintStatus</c>, which agree exactly. This list is
    /// checked against the running mod by <c>ModuleInventoryTests</c>, so an
    /// option added to the product cannot silently go untested.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Options =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ambientsound"] = "AmbientSoundOptimizations",
            ["bgfps"] = "BackgroundFpsLimiterEnabled",
            ["blur"] = "BlurOptimizationEnabled",
            ["dynlights"] = "DynamicLightOptimizations",
            ["entityanim"] = "EntityAnimationOptimizations",
            ["entityinterp"] = "EntityInterpolationOptimizations",
            ["flysound"] = "FlySoundOptimizations",
            ["framepace"] = "PreciseFramePacingEnabled",
            ["guimgr"] = "GuiManagerOptimizations",
            ["handbook"] = "HandbookOptimizations",
            ["occlusion"] = "OcclusionCullingOptimizations",
            ["particles"] = "ParticleOptimizations",
            ["recipe"] = "RecipeLookupOptimizations",
            ["repulseagents"] = "RepulseAgentsOptimizations",
            ["shaders"] = "ShaderOptimizations",
            ["shadowentity"] = "EntityShadowDistanceCullEnabled",
            ["shadowveg"] = "ShadowFarVegetationCullEnabled",
            ["suppress"] = "SuppressCompatibilityMessages",
            ["tickingblocks"] = "TickingBlocksOptimizations",
            ["weatherwind"] = "WeatherWindOptimizations",
        };

    /// <summary>The twenty option names, in the order the mod prints them.</summary>
    public static readonly string[] OptionNames = [.. Options.Keys];

    /// <summary>
    /// Options that are not performance optimizations and must not be toggled
    /// by the benchmark: <c>suppress</c> only hides compatibility chat lines, so
    /// toggling it changes what the test sees in chat rather than what it costs.
    /// </summary>
    public static readonly string[] NonOptimizations = ["suppress"];

    /// <summary>
    /// Options whose effect needs a second player on the server.
    ///
    /// <c>entityinterp</c> smooths remote entity movement, and the embedded
    /// server starts with one client and nobody else. Benchmarking it here would
    /// show no delta and report "dead code" for a module that is working fine.
    /// <c>MultiplayerOptimizationTests</c> joins a headless player and covers it.
    /// </summary>
    public static readonly string[] NeedsRemoteEntities = ["entityinterp"];

    /// <summary>
    /// Options whose effect needs an unfocused game window.
    ///
    /// <c>bgfps</c> only lowers the frame cap when the window is not focused.
    /// Pharos runs a hidden GLFW window that always has focus, so this one cannot
    /// be measured here and is reported as untested rather than as having no
    /// effect.
    /// </summary>
    public static readonly string[] NeedsUnfocusedWindow = ["bgfps"];

    /// <summary>
    /// Options the in-process benchmark leaves alone.
    ///
    /// <c>shaders</c> and <c>blur</c> swap GL shader assets. The state is bound
    /// when the shader registry loads, so a runtime toggle does not actually
    /// change what is drawn until the asset category is reloaded or the client
    /// reboots. <c>ShaderFidelityTests</c> drives the reload path explicitly.
    /// </summary>
    public static readonly string[] NeedsReboot = ["shaders", "blur"];

    /// <summary>
    /// Per-module expected effect, quoted from the v1.5.16 diag table on ModDB.
    /// The benchmark reports the measured value next to this one so a claim that
    /// silently stopped holding shows up as a number, not a vibe.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Claim> Claims =
        new Dictionary<string, Claim>(StringComparer.OrdinalIgnoreCase)
        {
            ["particles"] = new("45,607 spawns culled", 0.765),
            ["ambientsound"] = new("5,836 updates skipped", 0.813),
            ["weatherwind"] = new("5,890 ticks skipped", 0.750),
            ["shadowveg"] = new("7,853 passes fully culled", 1.000),
            ["repulseagents"] = new("158,284 physics ticks skipped", 0.473),
            ["entityanim"] = new("29,024 ticks skipped via LOD", 0.214),
            ["shadowentity"] = new("51,745 draws culled", 0.215),
            ["tickingblocks"] = new("69,810 allocations avoided (1.6 MB)", 0.248),
            ["guimgr"] = new("7,853 renders via zero-alloc path", 1.000),
            ["flysound"] = new("141 calls suppressed", 0.540),
            ["entityinterp"] = new("extrapolation limited to 4% of frames", 0.040),
            ["bgfps"] = new("~27 fps unfocused vs 60+ focused", 0.550),
            ["framepace"] = new("461 precise frames, avg overshoot 0.40ms", 1.000),
        };

    /// <summary>
    /// The diag module registered for each option, when there is one.
    ///
    /// The registry in <c>Core/Diagnostics</c> covers eighteen of the twenty
    /// options. <c>shaders</c>/<c>blur</c> and <c>suppress</c> have no counters
    /// because they are asset and message toggles rather than hot paths, so they
    /// are exercised through the asset reload and the chat surface instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DiagModules =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ambientsound"] = "ambientsound",
            ["bgfps"] = "bgfps",
            ["dynlights"] = "dynlights",
            ["entityanim"] = "entityanim",
            ["entityinterp"] = "entityinterp",
            ["flysound"] = "flysound",
            ["framepace"] = "framepace",
            ["guimgr"] = "guimgr",
            ["handbook"] = "handbook",
            ["occlusion"] = "occlusion",
            ["particles"] = "particles",
            ["recipe"] = "recipe",
            ["repulseagents"] = "repulse",
            ["shadowentity"] = "shadowentity",
            ["shadowveg"] = "shadowveg",
            ["tickingblocks"] = "tickingblocks",
            ["weatherwind"] = "weatherwind",
        };

    /// <summary>
    /// What the mod logs for each optimization when its Harmony patch applies,
    /// and what it logs instead when it deliberately declines to apply one.
    ///
    /// The "declines" column exists because one optimization does this by
    /// design: <c>shadowentity</c> skips itself at vanilla's default shadow map
    /// quality, because at 0 the far cascade range is shorter than its cull
    /// distance and the transpiler would never cull anything. That is correct
    /// behaviour, but the option still reports ON, so a test that only looked
    /// for the "loaded" line would report it as silently dead - the same shape
    /// as the v1.5.16 GUI Manager bug, differing only in that this one explains
    /// itself in the log.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, LoadAnnouncement> LoadAnnouncements =
        new Dictionary<string, LoadAnnouncement>(StringComparer.OrdinalIgnoreCase)
        {
            ["entityanim"] = new("Entity animation optimization loaded"),
            ["particles"] = new("Particle optimization loaded"),
            ["occlusion"] = new("Occlusion culling optimization loaded"),
            ["ambientsound"] = new("Ambient sound optimization loaded"),
            ["flysound"] = new("Fly sound optimization loaded"),
            ["guimgr"] = new("GUI Manager render optimization loaded"),
            ["handbook"] = new("Handbook optimization loaded"),
            ["recipe"] = new("Recipe lookup optimization loaded"),
            ["weatherwind"] = new("Weather wind speed throttle loaded"),
            ["tickingblocks"] = new("Ticking blocks GC optimization loaded"),
            ["shadowveg"] = new("Shadow far vegetation cull loaded"),
            ["shadowentity"] = new(
                "Entity shadow distance cull loaded",
                "Entity shadow distance cull skipped"),
            ["framepace"] = new("Precise frame pacing optimization loaded"),
        };

    // ------------------------------------------------------------------ world

    /// <summary>
    /// Fixed so the vanilla run and the OptiTime run generate the same terrain,
    /// vegetation and entities. Without this the A/B compares two scenes and
    /// every number is noise.
    /// </summary>
    public const int WorldSeed = 20260731;

    /// <summary>
    /// The world generator to run.
    ///
    /// <c>standard</c>, not <c>superflat</c>. Vintage Story registers exactly
    /// two world generators - see <c>SaveGame.StartNewWorld</c>, which picks
    /// <c>superflat</c> for a creative-building play style and <c>standard</c>
    /// for everything else - and any other name makes the server log
    /// "This save game requires world generator X but no such generator was
    /// found! No terrain will generate!" before it quietly generates a flat,
    /// empty world. Every vegetation, particle and shadow test then measures
    /// an empty scene.
    /// </summary>
    public const string WorldType = "standard";

    /// <summary>
    /// Creative observer avoids damage and death dialogs during profiling.
    /// Standard world generation still supplies terrain, vegetation and animals.
    /// Inventory coverage is creative-specific; survival UI is not certified.
    /// </summary>
    public const string PlayStyle = "creativebuilding";

    /// <summary>
    /// The game version these results belong to, written into every report.
    ///
    /// OptiTime 1.5.16 declares a dependency of <c>game 1.22.0</c> in
    /// modinfo.json, while the ModDB page is scoped to 1.22.5 - 1.22.6 and
    /// flagged there as potentially outdated. The suite runs on whatever the
    /// installed game is, and records it, so a result is never attributed to a
    /// build it did not come from.
    /// </summary>
    public const string TestedGameVersion = "1.22.7";

    /// <summary>
    /// Render distance the client boots at, in chunks.
    ///
    /// Deliberately 256, not the default. <c>occlusion</c> gates itself on view
    /// distance - <c>OcclusionCullingOptimization.UpdateViewDistance</c> computes
    /// <c>clamp((vd/32)^2, 70, 200)</c>, which is 64 at 192 and therefore
    /// clamped up to the 70 floor - and <c>particles</c> only scales spawns at
    /// 384+ and 512+. A short distance leaves both dormant and the tests would
    /// pass without testing anything.
    /// </summary>
    public const int ViewDistance = 256;

    // ------------------------------------------------------------------ poses

    /// <summary>
    /// Spawn-relative XZ offsets and surface-relative Y clearances:
    /// open terrain for occlusion and shadow culling, a treeline for shadowveg,
    /// sky and horizon for the shader swaps, and straight down to catch chunk
    /// popping and occlusion holes.
    /// </summary>
    public static readonly CameraPose[] Poses =
    [
        new("overlook",     [0.5, 32.0, 0.5],    [64.0, 1.0, 64.0]),
        new("horizon",      [0.5, 8.0, 0.5],     [256.0, 0.0, 256.0]),
        new("straightdown", [0.5, 64.0, 0.5],    [0.5, 1.0, 0.5]),
        new("ground",       [0.5, 16.0, 0.5],    [48.0, 3.0, 48.0]),
    ];

    /// <summary>
    /// Frames to run before anything is measured. The first frames compile
    /// shaders, mesh chunks and JIT the mod's transpilers; measuring them
    /// measures startup, not steady state.
    /// </summary>
    public const int WarmupFrames = 240;

    /// <summary>
    /// Frames per measurement sample. Long enough for P95 and P99 to mean
    /// something, and long enough for the mod's diag counters to accumulate a
    /// non-trivial rate (they are computed over elapsed real time).
    /// </summary>
    public const int MeasureFrames = 300;

    /// <summary>
    /// Frames to let the world settle after a pose change, so chunk streaming
    /// finishes before the framebuffer is read.
    /// </summary>
    public const int PoseSettleFrames = 90;

    /// <summary>
    /// The hour of day every visual comparison is taken at.
    ///
    /// The world's clock keeps running while a scenario steps, and two
    /// framebuffer captures taken minutes apart are taken at different sun
    /// angles. Pinning the clock is what makes a "did this optimization change
    /// what is drawn" comparison a comparison of the optimization. Midday, so
    /// the sun is high and shadows are short - fewer shadow pixels to move, and
    /// a shorter shadow travels less when the light angle shifts slightly.
    /// </summary>
    public const float FixedHourOfDay = 12f;

    /// <summary>
    /// How long a diag module is left accumulating before it is dumped. The mod
    /// computes its rates over elapsed wall time, so a window that is too short
    /// produces rates of zero and reads as "the module did nothing".
    /// </summary>
    public const int DiagWindowFrames = 600;

    // ---------------------------------------------------------------- output

    /// <summary>Per-variant measurement reports, for diffing outside the test process.</summary>
    public const string VariantArtifacts = "artifacts";

    /// <summary>PNGs captured by the vanilla run, which the OptiTime run diffs against.</summary>
    public const string BaselineDirectory = "baselines";

    /// <summary>
    /// How many pixels may differ before a frame counts as visually wrong.
    /// Generous on purpose: the mod is allowed to differ slightly, since it
    /// culls particles and vegetation by design. A large diff means geometry is
    /// missing, which is a bug.
    /// </summary>
    public const int VisualDiffPixelBudget = 20_000;

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// Pulls one option's state out of the mod's output.
    ///
    /// Two shapes, because the mod has two commands that answer the question:
    ///
    /// <list type="bullet">
    /// <item><c>.optitime status</c> prints one line per option:
    /// <c>  &lt;name&gt;: ON|OFF - &lt;description&gt;</c>, and for a
    /// conflict-disabled option <c>OFF (&lt;reason&gt;)</c> - with a space
    /// between the state and the parenthesis, because it is built with an
    /// interpolated string rather than concatenated. The description is free
    /// text and regularly contains the word "off" ("turns noise off near the
    /// player"), so a substring search over the whole line is not safe: it
    /// would read "enabled" and report the module as off. Only the token
    /// immediately after the colon is inspected.</item>
    /// <item><c>.optitime &lt;option&gt;</c> prints a detail card:
    /// <c>=== Recipe Lookup ===</c>, <c>Status: ON</c>, <c>Description: ...</c>.
    /// The state line's key is the literal word "Status", not the option name,
    /// so it has to be read on its own terms.</item>
    /// </list>
    ///
    /// Returns null - not a default-constructed <see cref="OptionState"/> - when
    /// the option is absent. A default struct has <c>Enabled == false</c>, which
    /// is indistinguishable from an option that is genuinely off, and that is
    /// the difference between "the mod has no such option" and "the module is
    /// disabled".
    /// </summary>
    public static OptionState? ReadOptionState(string statusText, string option)
    {
        if (ParseStatus(statusText).TryGetValue(option, out OptionState listed))
        {
            return listed;
        }

        return ParseDetailCard(statusText, option);
    }

    /// <summary>
    /// Reads the <c>Status:</c> line out of a single-option detail card.
    ///
    /// The card's own header carries the display name ("Recipe Lookup"), not
    /// the option name, so the option is whatever the caller asked about; only
    /// the state and its reason are taken from the text.
    /// </summary>
    private static OptionState? ParseDetailCard(string text, string option)
    {
        foreach (string line in text.Split('\n'))
        {
            Match m = StatusCard().Match(line.Trim());
            if (!m.Success) continue;

            string reason = m.Groups["reason"].Value;

            return new OptionState(
                option,
                m.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase),
                reason.Length == 0 ? null : reason);
        }

        return null;
    }

    [GeneratedRegex(@"^status:\s*(?<state>ON|OFF)(?:\s*\((?<reason>[^)]*)\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusCard();

    /// <summary>
    /// Reads every option's state out of the full <c>.optitime status</c> dump.
    ///
    /// Only options this suite knows about are returned: a line the mod prints
    /// that is not in <see cref="Options"/> is a new option upstream, and
    /// <c>ModuleInventoryTests</c> reports that from the unknown-option reply
    /// rather than silently absorbing it here.
    /// </summary>
    public static IReadOnlyDictionary<string, OptionState> ReadStatus(string statusText) =>
        ParseStatus(statusText);

    private static IReadOnlyDictionary<string, OptionState> ParseStatus(string statusText)
    {
        var result = new Dictionary<string, OptionState>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in statusText.Split('\n'))
        {
            Match m = StatusLine().Match(line.Trim());
            if (!m.Success) continue;

            string name = m.Groups["name"].Value;
            if (!Options.ContainsKey(name)) continue;

            bool on = m.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);
            string reason = m.Groups["reason"].Value;

            result[name] = new OptionState(
                name,
                on,
                reason.Length == 0 ? null : reason);
        }

        return result;
    }

    /// <summary>
    /// The line shape <c>OptiTimeConfig.PrintOptStatus</c> writes, quoted from
    /// the mod rather than invented:
    /// <c>$"  {name}: {status} - {desc}"</c>, where <c>status</c> is
    /// <c>"ON"</c>, <c>"OFF"</c> or <c>"OFF (reason)"</c> - note the space
    /// before the parenthesis, which is what an interpolated string produces.
    /// </summary>
    [GeneratedRegex(
        @"^(?<name>[a-z]+):\s*(?<state>ON|OFF)(?:\s*\((?<reason>[^)]*)\))?\s*(?:-.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();
}

/// <summary>How one option reported itself.</summary>
/// <param name="Option">The option name as the mod spells it.</param>
/// <param name="Enabled">Whether the config flag is true.</param>
/// <param name="Conflict">
/// Non-null when the mod refused to enable the option because it detected a
/// conflicting patch from another mod. The option still reads as OFF.
/// </param>
public readonly record struct OptionState(string Option, bool Enabled, string? Conflict)
{
    /// <summary>The mod would not turn this on because of a conflict.</summary>
    public bool IsConflictDisabled => Conflict is not null;

    public override string ToString() =>
        Conflict is null ? $"{Option}={(Enabled ? "ON" : "OFF")}" : $"{Option}=OFF ({Conflict})";
}

/// <summary>
/// A performance claim the mod makes in its own docs, kept so the benchmark can
/// print measured numbers next to promised ones.
/// </summary>
/// <param name="Description">The claim as published.</param>
/// <param name="ExpectedReduction">Fraction the mod claims to remove, 0-1.</param>
public readonly record struct Claim(string Description, double ExpectedReduction);

/// <summary>
/// What the mod says about one optimization at boot: the line it logs when the
/// patch applied, and - for the one optimization that may decline - the line it
/// logs instead.
/// </summary>
/// <param name="Loaded">Logged when the Harmony patch was applied.</param>
/// <param name="Declined">Logged when the mod deliberately did not apply one.</param>
public readonly record struct LoadAnnouncement(string Loaded, string? Declined = null);

/// <summary>
/// A fixed camera pose. Both variants must reach it identically, or the
/// framebuffer comparison measures the camera instead of the mod.
/// </summary>
/// <param name="Name">File name stem for the baseline PNG.</param>
/// <param name="Eye">Where the camera stands.</param>
/// <param name="Target">What the camera looks at.</param>
public readonly record struct CameraPose(string Name, double[] Eye, double[] Target);
