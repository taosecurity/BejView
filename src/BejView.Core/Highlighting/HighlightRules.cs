using System.Text.RegularExpressions;

namespace BejView.Core.Highlighting;

/// <summary>Colours a highlight rule can use. The window picks a shade of each that reads well in the light and dark themes.</summary>
public enum HighlightColor
{
    Red,
    Amber,
    Green,
    Blue,
    Purple,
    Gray,
}

/// <summary>One rule: lines that contain <see cref="Pattern"/> are drawn in <see cref="Color"/>. Saved in settings.json.</summary>
public sealed class HighlightRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool CaseSensitive { get; set; }

    /// <summary>A <see cref="HighlightColor"/> name (Red, Amber, Green, Blue, Purple or Gray).</summary>
    public string Color { get; set; } = nameof(HighlightColor.Red);

    public HighlightRule() { }

    public HighlightRule(string pattern, HighlightColor color, bool isRegex = false, bool caseSensitive = false)
    {
        Pattern = pattern;
        Color = color.ToString();
        IsRegex = isRegex;
        CaseSensitive = caseSensitive;
    }

    public HighlightRule Clone() => new() { Pattern = Pattern, IsRegex = IsRegex, CaseSensitive = CaseSensitive, Color = Color };

    public override string ToString() => $"{Color}: {(IsRegex ? "regex " : "")}\"{Pattern}\"{(CaseSensitive ? " (case)" : "")}";
}

public static class HighlightPresets
{
    public const string Papyrus = "Papyrus log";
    public const string ErrorsAndWarnings = "Errors and warnings";

    public static IReadOnlyList<string> Names { get; } = [Papyrus, ErrorsAndWarnings];

    /// <summary>
    /// For Starfield's Papyrus logs (Documents\My Games\Starfield\Logs\Script\Papyrus.0.log): errors red, warnings amber,
    /// stack lines purple, the VM's own messages (log opened, freezing, thawing) blue.
    /// </summary>
    public static List<HighlightRule> PapyrusRules() =>
    [
        new(@"\berror:", HighlightColor.Red, isRegex: true),
        new(@"\bwarning:", HighlightColor.Amber, isRegex: true),
        new(@"^\s*(\[[^\]]*\] )?stack:|^\s*\[.*\]\.[\w:]+\.[\w:]+\(\)", HighlightColor.Purple, isRegex: true),
        new(@"Papyrus log (opened|closed)|\bVM is (freezing|frozen|thawing)|\bLog (opened|closed)\b", HighlightColor.Blue, isRegex: true),
    ];

    /// <summary>For logs in general: ERROR, FATAL, EXCEPTION and FAILED red; WARN amber; DEBUG and TRACE gray.</summary>
    public static List<HighlightRule> ErrorsAndWarningsRules() =>
    [
        new(@"\b(error|fatal|critical|exception|fail(ed|ure)?)\b", HighlightColor.Red, isRegex: true),
        new(@"\bwarn(ing)?\b", HighlightColor.Amber, isRegex: true),
        new(@"\b(debug|trace|verbose)\b", HighlightColor.Gray, isRegex: true),
    ];

    public static List<HighlightRule>? Get(string name) => name switch
    {
        Papyrus => PapyrusRules(),
        ErrorsAndWarnings => ErrorsAndWarningsRules(),
        _ => null,
    };
}

/// <summary>Compiled highlight rules. The first rule that matches a line decides its colour.</summary>
public sealed class Highlighter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private readonly (Regex Regex, HighlightColor Color, HighlightRule Rule)[] _rules;

    public static Highlighter Empty { get; } = new([]);

    private Highlighter((Regex, HighlightColor, HighlightRule)[] rules) => _rules = rules;

    public int Count => _rules.Length;

    /// <summary>Compiles the rules. Rules with an empty or invalid pattern or an unknown colour are skipped and described in <paramref name="problems"/>.</summary>
    public static Highlighter Compile(IEnumerable<HighlightRule> rules, out List<string> problems)
    {
        problems = [];
        var list = new List<(Regex, HighlightColor, HighlightRule)>();
        int n = 0;
        foreach (var r in rules)
        {
            n++;
            if (string.IsNullOrEmpty(r.Pattern)) { problems.Add($"Highlight rule {n} has no text to look for; it's skipped."); continue; }
            if (!Enum.TryParse<HighlightColor>(r.Color, ignoreCase: true, out var color) || !Enum.IsDefined(color))
            {
                problems.Add($"Highlight rule {n} ({r.Pattern}) has the colour '{r.Color}'; use Red, Amber, Green, Blue, Purple or Gray. It's skipped.");
                continue;
            }
            var options = RegexOptions.CultureInvariant | (r.CaseSensitive ? 0 : RegexOptions.IgnoreCase);
            try
            {
                list.Add((new Regex(r.IsRegex ? r.Pattern : Regex.Escape(r.Pattern), options, Timeout), color, r));
            }
            catch (ArgumentException ex)
            {
                problems.Add($"Highlight rule {n} isn't a valid regular expression ({ex.Message}). Fix it or untick Regex; it's skipped.");
            }
        }
        return new Highlighter([.. list]);
    }

    /// <summary>The colour for a line, or null when no rule matches. A rule that takes too long on a line is treated as not matching.</summary>
    public HighlightColor? Match(string line)
    {
        foreach (var (regex, color, _) in _rules)
        {
            try
            {
                if (regex.IsMatch(line)) return color;
            }
            catch (RegexMatchTimeoutException)
            {
                // A slow pattern on a very long line: leave the line uncoloured rather than freeze the window.
            }
        }
        return null;
    }
}
