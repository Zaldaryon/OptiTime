using System.Globalization;
using System.Text.RegularExpressions;

namespace OptiTime.Pharos.Tests;

/// <summary>
/// Reads the mod's own diagnostic counters back out of the client log.
///
/// The mod emits every counter twice: once to chat through
/// <c>ICoreClientAPI.ShowChatMessage</c> and once to the log through
/// <c>Logger.Notification</c>, both from <c>DiagLog.Line</c>. The log copy is
/// the one worth asserting on:
///
/// <list type="bullet">
/// <item>it is captured by Pharos' <c>LogCapture</c>, which is cleared between
/// tests, so "how many since this test started" needs no bookkeeping;</item>
/// <item>the chat copy escapes <c>&lt;</c> and <c>&gt;</c> for the rich-text
/// renderer, so a counter containing angle brackets reads differently depending
/// on which copy you parse;</item>
/// <item>a chat line the client dropped because nobody was listening is still in
/// the log, so a test cannot silently measure nothing and pass.</item>
/// </list>
///
/// The line shape is fixed by <c>DiagLog.Line</c>:
/// <c>[OptiTime/diag/&lt;module&gt;] key=value key=value</c>. The key names come
/// from each <c>Module*.Dump</c> and are enumerated by <see cref="DiagCounter"/>.
/// </summary>
public static partial class DiagReader
{
    [GeneratedRegex(@"\[OptiTime/diag/(?<module>[a-z]+)\]\s*(?<body>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagLine();

    /// <summary>
    /// Pulls the body of a module's dump out of a captured log or chat text.
    /// Returns null when the module never dumped, which is different from a
    /// module that dumped zero: zero means "it ran and counted nothing", null
    /// means "nothing called Dump".
    /// </summary>
    public static string? Body(string logText, string module) =>
        Lines(logText, module).LastOrDefault();

    /// <summary>Every dump line a module produced, oldest first.</summary>
    public static IReadOnlyList<string> Lines(string logText, string module)
    {
        var result = new List<string>();

        foreach (string line in logText.Split('\n'))
        {
            Match m = DiagLine().Match(line.TrimEnd('\r'));
            if (m.Success &&
                string.Equals(m.Groups["module"].Value, module, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(m.Groups["body"].Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a dump body into its counters.
    ///
    /// Values are kept as strings and converted on demand rather than being
    /// forced to a double at parse time: <c>shadowQ</c> and <c>cullDist=80</c>
    /// are integers, <c>avgOvershoot=0.40</c> is a fixed-point double written
    /// with the invariant culture, and anything the mod adds later parses
    /// without this file changing.
    /// </summary>
    public static DiagCounters Parse(string? body)
    {
        var counters = new DiagCounters(body ?? string.Empty);

        if (string.IsNullOrWhiteSpace(body)) return counters;

        foreach (Match m in KeyValue().Matches(body))
        {
            string key = m.Groups["key"].Value;
            string raw = m.Groups["value"].Value.Trim();

            // "(12.3%)" is a rendering of the value beside it, not a counter.
            raw = raw.Trim('(', ')');
            raw = raw.TrimEnd('%');

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                counters.Set(key, number, raw);
            }
            else
            {
                counters.Set(key, raw);
            }
        }

        return counters;
    }

    [GeneratedRegex(@"(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<value>[^\s]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();
}

/// <summary>
/// One diag dump's worth of counters. Every getter is nullable on purpose: a
/// key the mod renamed is a missing value, and a test that says so is far more
/// useful than one that silently reads zero.
/// </summary>
public sealed class DiagCounters(string raw)
{
    private readonly Dictionary<string, double> _numbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    /// <summary>The line as the mod wrote it, kept for failure messages.</summary>
    public string Raw { get; } = raw;

    /// <summary>Whether the module produced a dump at all.</summary>
    public bool Present => Raw.Length != 0;

    /// <summary>Counter names the dump actually contained.</summary>
    public IReadOnlyCollection<string> Keys => _numbers.Keys.Concat(_text.Keys).Distinct().ToList();

    internal void Set(string key, double number, string text) { _numbers[key] = number; _text[key] = text; }
    internal void Set(string key, string text) => _text[key] = text;

    /// <summary>
    /// A counter that is not in the dump reads as null, not as zero.
    ///
    /// <c>Dictionary.GetValueOrDefault</c> returns <c>default(T)</c> for a
    /// missing key, and <c>double</c> converts to <c>double?</c> as 0 rather
    /// than null - so a missing counter would read as a counter of zero. That
    /// is the worst possible failure here: every "this module did nothing"
    /// assertion would pass against a counter that was never there.
    /// </summary>
    public double? Number(string key) =>
        _numbers.TryGetValue(key, out double value) ? value : null;

    public string? Text(string key) =>
        _text.GetValueOrDefault(key);

    /// <summary>
    /// A counter that must be present, with a message naming the module and
    /// showing the raw line when it is not. The alternative - a test that
    /// compares a defaulted zero against a threshold - produces a failure
    /// message that does not say which of those two things went wrong.
    /// </summary>
    public double Require(string key, string module)
    {
        if (_numbers.TryGetValue(key, out double value)) return value;

        throw new InvalidOperationException(
            $"The '{module}' diag dump has no counter named '{key}'." +
            (_text.TryGetValue(key, out string? text)
                ? $" It is present but not numeric: '{text}'."
                : $" Present counters: {(Keys.Count == 0 ? "(none)" : string.Join(", ", Keys))}.") +
            $" Raw line: {Raw}");
    }

    public override string ToString() => Raw;
}