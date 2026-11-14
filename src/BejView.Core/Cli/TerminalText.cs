using System.Globalization;
using System.Text;

namespace BejView.Core.Cli;

/// <summary>
/// Makes text from a file safe to print to a terminal (step 6, 2026-11-14, PLAN.md's security points).
/// A file can hold escape sequences (ESC [ ... or OSC ... BEL) that change a terminal's title, colours or clipboard,
/// or a carriage return that hides the start of a line. Every control character except tab is printed as a visible
/// \xNN code instead: the C0 controls (U+0000-U+001F), DEL (U+007F) and the C1 controls (U+0080-U+009F).
/// </summary>
public static class TerminalText
{
    public static bool IsUnsafe(char c) => (c < 0x20 && c != '\t') || (c >= 0x7F && c <= 0x9F);

    public static string Safe(string text)
    {
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsUnsafe(text[i])) { first = i; break; }
        }
        if (first < 0) return text;
        var sb = new StringBuilder(text.Length + 16);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (IsUnsafe(c)) sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
