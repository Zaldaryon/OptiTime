namespace OptiTime.Pharos.Tests;

/// <summary>
/// Unit tests for the suite's own parsing.
///
/// The suite reads the mod's state out of two loose formats - a status listing
/// and a set of <c>key=value</c> counter lines - and every assertion about the
/// mod goes through those parsers. A parser that is too lenient is the most
/// dangerous thing in a test suite of this shape: it turns a real failure into
/// a silent pass. These tests pin the failure behaviour directly, with no game
/// boot, so they run in milliseconds and can be the first thing a developer
/// runs after editing a parser.
/// </summary>
public class HarnessParserTests
{
    // ------------------------------------------------------------- status lines

    /// <summary>
    /// The exact line shape <c>OptiTimeConfig.PrintOptStatus</c> writes.
    /// Taken from the mod rather than invented, so a change there shows up as a
    /// failing parse rather than as silently different results.
    /// </summary>
    private const string StatusSample =
        """
        === [OptiTime] Status ===
          shaders: ON - Reduces shader processing
          blur: OFF - Disables the blur effect, which can look like this
          dynlights: OFF (conflict with another mod) - Culls dynamic lights
          particles: ON - Spawns fewer particles
          profiling: OFF
        """;

    [Fact]
    public void StatusParserReadsEnabledAndDisabledOptions()
    {
        IReadOnlyDictionary<string, OptionState> states = OptiTimeConfig.ReadStatus(StatusSample);

        Assert.True(states["shaders"].Enabled);
        Assert.False(states["blur"].Enabled);
        Assert.True(states["particles"].Enabled);
    }

    /// <summary>
    /// The reason a conflict is reported sits between the state and the dash:
    /// <c>OFF (reason)</c>. Without this the parser would read every
    /// conflict-disabled option as a plain off, and a module the mod refused to
    /// enable would look like one a test had turned off itself.
    /// </summary>
    [Fact]
    public void StatusParserSeparatesConflictFromOff()
    {
        IReadOnlyDictionary<string, OptionState> states = OptiTimeConfig.ReadStatus(StatusSample);

        Assert.False(states["dynlights"].Enabled);
        Assert.True(states["dynlights"].IsConflictDisabled);
        Assert.Equal("conflict with another mod", states["dynlights"].Conflict);

        Assert.False(states["blur"].IsConflictDisabled);
    }

    /// <summary>
    /// The regression this parser exists to prevent: the descriptions contain
    /// the word "off" and "on", so a substring search reads the wrong answer.
    /// "Disables the blur effect, which can look like this" contains "off" in
    /// "Disables"? No - but "switches on" would contain "on". Either way, the
    /// token after the colon is the only reliable source.
    /// </summary>
    [Theory]
    [InlineData("  particles: ON - Turns noise off near the player", true)]
    [InlineData("  particles: OFF - Turns noise on near the player", false)]
    public void StatusParserIgnoresTheDescriptionText(string line, bool expected)
    {
        OptionState? state = OptiTimeConfig.ReadOptionState(line, "particles");

        Assert.NotNull(state);
        Assert.Equal(expected, state!.Value.Enabled);
    }

    [Fact]
    public void StatusParserReportsNothingForAnAbsentOption()
    {
        Assert.Null(OptiTimeConfig.ReadOptionState(StatusSample, "entityanim"));
    }

    /// <summary>
    /// The second command that answers "is this option on" prints a different
    /// shape: a detail card whose state line is keyed on the literal word
    /// "Status", not on the option name. Missing it made
    /// <c>SetOptimizationAsync</c> report every toggle as unconfirmed, because
    /// <c>.optitime recipe on</c> was verified against a card the parser could
    /// not read.
    /// </summary>
    [Fact]
    public void StatusParserReadsTheSingleOptionDetailCard()
    {
        const string card =
            """
            === Recipe Lookup ===
            Status: ON
            Description: Early dimension rejection for impossible crafting matches
            """;

        OptionState? state = OptiTimeConfig.ReadOptionState(card, "recipe");

        Assert.NotNull(state);
        Assert.True(state!.Value.Enabled);
        Assert.False(state.Value.IsConflictDisabled);
        Assert.Equal("recipe", state.Value.Option);
    }

    [Fact]
    public void StatusParserReadsAConflictReasonFromTheDetailCard()
    {
        const string card =
            """
            === Dynamic Lights ===
            Status: OFF (Another mod already patches SystemRenderPlayerEffects)
            Description: Adaptive light culling by view distance
            """;

        OptionState? state = OptiTimeConfig.ReadOptionState(card, "dynlights");

        Assert.NotNull(state);
        Assert.False(state!.Value.Enabled);
        Assert.True(state.Value.IsConflictDisabled);
    }

    [Fact]
    public void StatusParserIgnoresOptionsTheSuiteDoesNotKnow()
    {
        IReadOnlyDictionary<string, OptionState> states =
            OptiTimeConfig.ReadStatus("  brandnewoption: ON - something upstream added");

        Assert.Empty(states);
    }

    // --------------------------------------------------------------- diag lines

    [Fact]
    public void DiagParserReadsTheModsOwnLineFormat()
    {
        DiagCounters counters = DiagReader.Parse(
            "requested=120 allowed=28 (23.3%) culled byVD=40 byFrustum=30 byOccupancy=12 byBudget=10 elapsedS=9.9");

        Assert.Equal(120, counters.Number(DiagCounter.ParticlesRequested));
        Assert.Equal(28, counters.Number(DiagCounter.ParticlesAllowed));
        Assert.Equal(40, counters.Number(DiagCounter.ParticlesCulledByViewDistance));
        Assert.Equal(30, counters.Number(DiagCounter.ParticlesCulledByFrustum));
        Assert.Equal(12, counters.Number(DiagCounter.ParticlesCulledByOccupancy));
        Assert.Equal(10, counters.Number(DiagCounter.ParticlesCulledByBudget));
        Assert.Equal(9.9, counters.Number(DiagCounter.ElapsedSeconds));
    }

    /// <summary>
    /// Percentages are rendered beside the value they describe, in parentheses,
    /// and the framing has to come off before the value parses - otherwise
    /// "23.3" and "(23.3%)" are two different strings and one of them is not a
    /// counter.
    /// </summary>
    [Fact]
    public void DiagParserHandlesAPercentageBesideTheValue()
    {
        DiagCounters counters = DiagReader.Parse("lookups=800 hits=764 (95.5%) misses=36 invalidations=4 elapsedS=3.0");

        Assert.Equal(800, counters.Number(DiagCounter.Lookups));
        Assert.Equal(764, counters.Number(DiagCounter.Hits));
        Assert.Equal(36, counters.Number(DiagCounter.Misses));
        Assert.Equal(4, counters.Number(DiagCounter.Invalidations));

        // The percentage itself is not a counter under any name.
        Assert.Null(counters.Number("95.5"));
    }

    [Fact]
    public void DiagParserKeepsNonNumericValues()
    {
        DiagCounters counters = DiagReader.Parse("indexer=complete cacheLookups=12 hits=10 misses=2 elapsedS=1.0");

        Assert.Equal("complete", counters.Text(DiagCounter.Indexer));
        Assert.Equal(12, counters.Number(DiagCounter.CacheLookups));
    }

    /// <summary>
    /// A module that produced no dump is different from a module that dumped
    /// zeros. The first is "nothing called Dump", the second is "it ran and
    /// counted nothing", and every caller treats them differently.
    /// </summary>
    [Fact]
    public void DiagParserDistinguishesAbsentFromZero()
    {
        Assert.False(DiagReader.Parse(null).Present);
        Assert.False(DiagReader.Parse(string.Empty).Present);

        DiagCounters zeroed = DiagReader.Parse("calls=0 skipped=0 elapsedS=1.0");
        Assert.True(zeroed.Present);
        Assert.Equal(0, zeroed.Number(DiagCounter.Calls));
    }

    [Fact]
    public void DiagParserNamesTheModuleAndTheMissingCounter()
    {
        DiagCounters counters = DiagReader.Parse("calls=5 skipped=2 elapsedS=1.0");

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => counters.Require("totallyNewKey", "ambientsound"));

        Assert.Contains("totallyNewKey", error.Message);
        Assert.Contains("ambientsound", error.Message);
        Assert.Contains("calls", error.Message);
    }

    [Fact]
    public void DiagParserFindsTheLastDumpOfAModuleInALog()
    {
        const string log =
            """
            [OptiTime/diag/particles] requested=1 allowed=1 (100.0%) elapsedS=0.1
            [OptiTime/diag/occlusion] framesWithDynamicThreshold=10 elapsedS=1.0 rate=600/s
            [OptiTime/diag/particles] requested=200 allowed=50 (25.0%) culled byVD=150 byFrustum=0 byOccupancy=0 byBudget=0 elapsedS=10.0
            """;

        string? body = DiagReader.Body(log, "particles");

        Assert.NotNull(body);
        Assert.Equal(200, DiagReader.Parse(body).Number(DiagCounter.ParticlesRequested));
        Assert.Single(DiagReader.Lines(log, "occlusion"));
    }

    /// <summary>
    /// The mod writes the same line to chat and to the log, escaping angle
    /// brackets on the chat copy only. A dump body that has been through chat
    /// must still parse, so the module name is matched case-insensitively and
    /// the escaped form is not required.
    /// </summary>
    [Fact]
    public void DiagParserMatchesTheModuleNameCaseInsensitively()
    {
        const string log = "[OptiTime/diag/EntityAnim] entities=12 full=8 lodMedium=3 lodFar=1 skipped=33.3% elapsedS=2.0";

        DiagCounters counters = DiagReader.Parse(DiagReader.Body(log, "entityanim"));

        Assert.True(counters.Present);
        Assert.Equal(12, counters.Number(DiagCounter.Entities));
    }

    /// <summary>
    /// The counters are read back from a process-wide static that every test in
    /// the class shares, under the default rollback isolation. A parser that
    /// returned the first dump rather than the last would make a second test
    /// read the first test's numbers and pass for the wrong reason.
    /// </summary>
    [Fact]
    public void DiagParserUsesTheMostRecentDump()
    {
        const string log =
            """
            [OptiTime/diag/guimgr] renderCalls=100 noLinqPath=0 (0.0%) elapsedS=1.0
            [OptiTime/diag/guimgr] renderCalls=900 noLinqPath=880 (97.8%) elapsedS=9.0
            """;

        DiagCounters counters = DiagReader.Parse(DiagReader.Body(log, "guimgr"));

        Assert.Equal(900, counters.Number(DiagCounter.RenderCalls));
        Assert.Equal(880, counters.Number(DiagCounter.NoLinqPath));
    }
}