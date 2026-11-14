using System.Globalization;
using BejView.Core.Documents;

namespace BejView.Core.Navigation;

public enum GoToKind
{
    /// <summary>A 1-based line number.</summary>
    Line,
    /// <summary>A position through the file, 0 to 100.</summary>
    Percent,
    /// <summary>A byte offset from the start of the file.</summary>
    ByteOffset,
}

/// <summary>
/// What the user typed in "Go to": <c>12345</c> (line), <c>50%</c> (percent through the file), or a byte offset as
/// <c>@1048576</c>, <c>0x100000</c> or <c>1048576b</c>. Thousands separators (1,234,567 or 1_234_567) are allowed.
/// </summary>
public readonly record struct GoToTarget(GoToKind Kind, long Value, double Percent = 0)
{
    public const string Help = "Type a line number (12345), a percentage (50%), or a byte offset (@1048576, 0x100000 or 1048576b).";

    public static bool TryParse(string? input, out GoToTarget target, out string error)
    {
        target = default;
        error = "";
        string s = (input ?? "").Trim().Replace(",", "").Replace("_", "").Replace(" ", "");
        if (s.Length == 0) { error = "Type where to go. " + Help; return false; }

        if (s.EndsWith('%'))
        {
            if (!double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p) || double.IsNaN(p) || p < 0 || p > 100)
            {
                error = $"'{input}' isn't a percentage from 0% to 100%.";
                return false;
            }
            target = new GoToTarget(GoToKind.Percent, 0, p);
            return true;
        }

        bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool at = s.StartsWith('@');
        bool b = s.EndsWith('b') || s.EndsWith('B');
        if (hex || at || b)
        {
            // In 0x... a trailing b is a hex digit, not the "bytes" suffix.
            string digits = hex ? s[2..] : at ? s[1..] : s[..^1];
            bool ok = hex
                ? long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long off)
                : long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out off);
            if (!ok || off < 0) { error = $"'{input}' isn't a byte offset. {Help}"; return false; }
            target = new GoToTarget(GoToKind.ByteOffset, off);
            return true;
        }

        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long line) || line < 1)
        {
            error = $"'{input}' isn't a line number from 1 up. {Help}";
            return false;
        }
        target = new GoToTarget(GoToKind.Line, line);
        return true;
    }

    /// <summary>The byte offset this target points at (Line targets: -1, they need the index instead).</summary>
    public long ByteOffsetIn(TextDocument doc) => Kind switch
    {
        GoToKind.ByteOffset => Value,
        GoToKind.Percent => doc.Index.ContentStart + (long)Math.Round((doc.Length - doc.Index.ContentStart) * (Percent / 100.0)),
        _ => -1,
    };

    /// <summary>
    /// True when the index doesn't reach the target yet (a line or offset past the part indexed so far). The window
    /// waits for indexing in that case. A target past the end of a fully indexed file is not pending: it goes to the last line.
    /// </summary>
    public bool IsPending(TextDocument doc)
    {
        if (doc.IsFullyIndexed) return false;
        return Kind == GoToKind.Line ? Value > doc.LineCount : ByteOffsetIn(doc) >= doc.Index.ScannedTo;
    }

    /// <summary>The 0-based line to show, clamped to the lines indexed so far (0 for an empty file).</summary>
    public long ResolveLine(TextDocument doc)
    {
        long lines = doc.LineCount;
        if (lines == 0) return 0;
        if (Kind == GoToKind.Line) return Math.Clamp(Value - 1, 0, lines - 1);
        long line = doc.Index.LineOfOffset(ByteOffsetIn(doc));
        return Math.Clamp(line, 0, lines - 1);
    }

    public override string ToString() => Kind switch
    {
        GoToKind.Line => $"line {Value:N0}",
        GoToKind.Percent => $"{Percent.ToString("0.##", CultureInfo.InvariantCulture)}%",
        _ => $"byte {Value:N0}",
    };
}
