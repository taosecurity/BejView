using System.Text;
using BejView.Core.Text;

namespace BejView.Tests;

/// <summary>Temporary files plus a slow, obviously-correct model of lines and offsets to check the fast code against.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bejview-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string name, byte[] bytes)
    {
        string p = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A text, how it's stored, and the expected lines.</summary>
public sealed class Model
{
    public string Text { get; }
    public TextEncodingKind Kind { get; }
    public bool Bom { get; }
    public byte[] Bytes { get; }
    public int BomLength { get; }
    public Encoding Encoding { get; }

    /// <summary>Expected lines (CR stripped), their start offsets in bytes, and their start index in <see cref="Text"/>.</summary>
    public List<string> Lines { get; } = [];
    public List<long> Starts { get; } = [];
    public List<int> CharStarts { get; } = [];

    public Model(string text, TextEncodingKind kind, bool bom)
    {
        Text = text;
        Kind = kind;
        Bom = bom;
        Encoding = kind switch
        {
            TextEncodingKind.Utf8 => new UTF8Encoding(false),
            TextEncodingKind.Utf16LE => new UnicodeEncoding(false, false),
            TextEncodingKind.Utf16BE => new UnicodeEncoding(true, false),
            _ => Encoding.Latin1,
        };
        byte[] pre = !bom ? [] : kind switch
        {
            TextEncodingKind.Utf8 => [0xEF, 0xBB, 0xBF],
            TextEncodingKind.Utf16LE => [0xFF, 0xFE],
            TextEncodingKind.Utf16BE => [0xFE, 0xFF],
            _ => [],
        };
        BomLength = pre.Length;
        Bytes = [.. pre, .. Encoding.GetBytes(text)];

        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl;
            string line = text[start..end];
            if (nl >= 0 && line.EndsWith('\r')) line = line[..^1];
            Lines.Add(line);
            CharStarts.Add(start);
            Starts.Add(ByteOffsetOfChar(start));
            start = nl < 0 ? text.Length : nl + 1;
        }
    }

    public long ByteOffsetOfChar(int charIndex) => BomLength + Encoding.GetByteCount(Text.AsSpan(0, charIndex));

    public int LineOfChar(int charIndex)
    {
        int line = 0;
        for (int i = 0; i < charIndex; i++) if (Text[i] == '\n') line++;
        return line;
    }

    /// <summary>Random text: short lines mostly, some empty, some long; CRLF or LF; accents, euro signs and emoji (UTF-16 surrogate pairs).</summary>
    public static string RandomText(Random r, int lines, bool latin1Only = false, int longLineChance = 20, int longLineMax = 3000)
    {
        string[] pool = latin1Only
            ? ["a", "b", "c", "x", " ", "é", "ü", "ß", "E", "R", "O"]
            : ["a", "b", "c", "x", " ", "é", "€", "😀", "E", "R", "O", "ü"];
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            int len = r.Next(longLineChance) == 0 ? r.Next(200, longLineMax) : r.Next(0, 25);
            for (int j = 0; j < len; j++) sb.Append(pool[r.Next(pool.Length)]);
            bool last = i == lines - 1;
            if (last && r.Next(2) == 0) break;     // sometimes no line break at the end
            sb.Append(r.Next(3) == 0 ? "\r\n" : "\n");
        }
        return sb.ToString();
    }
}
