using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace BejView.Core.Text;

public enum TextEncodingKind
{
    Utf8,
    Utf16LE,
    Utf16BE,
    /// <summary>ISO-8859-1: every byte is one character. Used for files that aren't valid UTF-8 (old Windows-1252 text mostly reads right).</summary>
    Latin1,
}

/// <summary>
/// A file's encoding: how to decode it, where the text starts (after any byte order mark), and how a line break looks
/// in bytes. Line breaks are always LF (0x0A, or the 16-bit unit 0x000A); a CR before it is stripped when a line is shown,
/// so CRLF and LF files both work.
/// </summary>
public sealed class TextEncoding
{
    public TextEncodingKind Kind { get; }

    /// <summary>Bytes of byte order mark at the start of the file (0, 2 or 3). The first line starts after them.</summary>
    public int BomLength { get; }

    /// <summary>Why this encoding was chosen, for the log.</summary>
    public string Reason { get; }

    public Encoding Encoding { get; }

    /// <summary>Bytes per code unit: 1 for UTF-8 and Latin-1, 2 for UTF-16. Offsets inside the text are multiples of this.</summary>
    public int UnitSize => Kind is TextEncodingKind.Utf16LE or TextEncodingKind.Utf16BE ? 2 : 1;

    /// <summary>Bytes in one line break (LF).</summary>
    public int NewlineLength => UnitSize;

    public string DisplayName => Kind switch
    {
        TextEncodingKind.Utf8 => BomLength > 0 ? "UTF-8 (BOM)" : "UTF-8",
        TextEncodingKind.Utf16LE => "UTF-16 LE",
        TextEncodingKind.Utf16BE => "UTF-16 BE",
        _ => "Latin-1",
    };

    public TextEncoding(TextEncodingKind kind, int bomLength, string reason)
    {
        Kind = kind;
        BomLength = bomLength;
        Reason = reason;
        Encoding = kind switch
        {
            // No BOM output, and invalid bytes become U+FFFD instead of throwing.
            TextEncodingKind.Utf8 => new UTF8Encoding(false, false),
            TextEncodingKind.Utf16LE => new UnicodeEncoding(false, false, false),
            TextEncodingKind.Utf16BE => new UnicodeEncoding(true, false, false),
            _ => Encoding.Latin1,
        };
    }

    /// <summary>
    /// Picks the encoding from the first bytes of a file: a byte order mark if there is one; otherwise UTF-16 when
    /// most of every other byte is zero (text without a BOM saved by some Windows tools), UTF-8 when the sample is
    /// valid UTF-8, and Latin-1 for anything else.
    /// </summary>
    public static TextEncoding Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return new(TextEncodingKind.Utf8, 3, "UTF-8 byte order mark");
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return new(TextEncodingKind.Utf16LE, 2, "UTF-16 LE byte order mark");
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new(TextEncodingKind.Utf16BE, 2, "UTF-16 BE byte order mark");
        if (head.IsEmpty)
            return new(TextEncodingKind.Utf8, 0, "empty file");

        if (head.Length >= 4)
        {
            int pairs = head.Length / 2, zeroEven = 0, zeroOdd = 0;
            for (int i = 0; i + 1 < head.Length; i += 2)
            {
                if (head[i] == 0) zeroEven++;
                if (head[i + 1] == 0) zeroOdd++;
            }
            // Mostly-ASCII UTF-16 has a zero in every high byte; real UTF-8/Latin-1 text has almost none.
            if (zeroOdd * 10 >= pairs * 6 && zeroEven * 10 < pairs)
                return new(TextEncodingKind.Utf16LE, 0, "no BOM; zero high bytes look like UTF-16 LE");
            if (zeroEven * 10 >= pairs * 6 && zeroOdd * 10 < pairs)
                return new(TextEncodingKind.Utf16BE, 0, "no BOM; zero high bytes look like UTF-16 BE");
        }

        // The sample may end in the middle of a character: ignore up to 3 trailing bytes of an unfinished sequence.
        ReadOnlySpan<byte> sample = head[..Utf8CompleteLength(head)];
        if (Utf8.IsValid(sample))
            return new(TextEncodingKind.Utf8, 0, "no BOM; the first bytes are valid UTF-8");
        return new(TextEncodingKind.Latin1, 0, "no BOM and not valid UTF-8; reading as Latin-1");
    }

    public static TextEncoding FromKind(TextEncodingKind kind, ReadOnlySpan<byte> head)
    {
        int bom = kind switch
        {
            TextEncodingKind.Utf8 when head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) => 3,
            TextEncodingKind.Utf16LE when head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) => 2,
            TextEncodingKind.Utf16BE when head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) => 2,
            _ => 0,
        };
        return new(kind, bom, "chosen by the user");
    }

    /// <summary>Byte index of the next line break in <paramref name="block"/> (aligned to the code unit), or -1.</summary>
    public int IndexOfNewline(ReadOnlySpan<byte> block)
    {
        switch (Kind)
        {
            case TextEncodingKind.Utf16LE:
            case TextEncodingKind.Utf16BE:
            {
                var units = MemoryMarshal.Cast<byte, ushort>(block[..(block.Length & ~1)]);
                int i = units.IndexOf(Utf16Newline);
                return i < 0 ? -1 : i * 2;
            }
            default:
                return block.IndexOf((byte)'\n');
        }
    }

    /// <summary>Number of line breaks in <paramref name="block"/>.</summary>
    public int CountNewlines(ReadOnlySpan<byte> block) => Kind switch
    {
        TextEncodingKind.Utf16LE or TextEncodingKind.Utf16BE =>
            MemoryMarshal.Cast<byte, ushort>(block[..(block.Length & ~1)]).Count(Utf16Newline),
        _ => block.Count((byte)'\n'),
    };

    /// <summary>
    /// Moves a byte offset back to the start of a character, so text cut there decodes cleanly
    /// (UTF-8: if the byte at the cut is a continuation byte, moves back to its lead byte; UTF-16: even offset).
    /// <paramref name="bytes"/> must contain the byte at index <paramref name="cut"/>; if it doesn't, the cut is kept.
    /// </summary>
    public int AlignBack(ReadOnlySpan<byte> bytes, int cut)
    {
        if (UnitSize == 2) return cut & ~1;
        if (Kind != TextEncodingKind.Utf8) return cut;
        int c = cut;
        while (c > 0 && cut - c < 3 && c < bytes.Length && (bytes[c] & 0xC0) == 0x80)
            c--;
        return c;
    }

    /// <summary>The 16-bit value of LF as it reads from memory on this machine for this file's byte order.</summary>
    private ushort Utf16Newline =>
        (Kind == TextEncodingKind.Utf16LE) == BitConverter.IsLittleEndian ? (ushort)0x000A : (ushort)0x0A00;

    /// <summary>Length of <paramref name="bytes"/> without an unfinished UTF-8 sequence at its end.</summary>
    public static int Utf8CompleteLength(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length;
        for (int back = 1; back <= 3 && back <= n; back++)
        {
            byte b = bytes[n - back];
            if ((b & 0xC0) == 0x80) continue;          // continuation byte: keep looking for the lead byte
            int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return need > back ? n - back : n;         // the lead byte's sequence is cut short
        }
        return n;
    }
}
