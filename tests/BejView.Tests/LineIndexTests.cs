using BejView.Core.Documents;
using BejView.Core.Text;

namespace BejView.Tests;

public sealed class LineIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    public static TheoryData<TextEncodingKind, bool, int, int, int> Cases()
    {
        var d = new TheoryData<TextEncodingKind, bool, int, int, int>();
        foreach (var kind in new[] { TextEncodingKind.Utf8, TextEncodingKind.Utf16LE, TextEncodingKind.Utf16BE, TextEncodingKind.Latin1 })
        foreach (bool bom in new[] { false, true })
        {
            if (kind == TextEncodingKind.Latin1 && bom) continue;
            d.Add(kind, bom, 3, 16, 16);           // tiny spacing: checkpoints everywhere, inside long lines too
            d.Add(kind, bom, 1, 64, 32);
            d.Add(kind, bom, 256, 1 << 20, 4 << 20); // the real defaults
        }
        return d;
    }

    private TextDocument OpenModel(Model m, int linesPer, int bytesPer, int block)
    {
        string path = _tmp.Write(Guid.NewGuid().ToString("N") + ".txt", m.Bytes);
        var doc = TextDocument.Open(path, encoding: m.Kind);
        doc.UseIndexSpacing(linesPer, bytesPer, block);
        return doc;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Index_matches_a_brute_force_count_on_random_text(TextEncodingKind kind, bool bom, int linesPer, int bytesPer, int block)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var r = new Random(seed * 7919 + (int)kind);
            var m = new Model(Model.RandomText(r, r.Next(0, 120), latin1Only: kind == TextEncodingKind.Latin1), kind, bom);
            using var doc = OpenModel(m, linesPer, bytesPer, block);
            await doc.IndexAsync(ct: TestContext.Current.CancellationToken);

            Assert.Equal(m.Lines.Count, doc.LineCount);
            for (int i = 0; i < m.Lines.Count; i++)
                Assert.Equal(m.Starts[i], doc.Index.LineStart(i));

            var lines = doc.ReadLines(0, m.Lines.Count + 5);
            Assert.Equal(m.Lines, lines.Select(l => l.Text));

            for (int k = 0; k < 40 && m.Text.Length > 0; k++)
            {
                int ci = r.Next(m.Text.Length);
                if (char.IsLowSurrogate(m.Text[ci])) ci--;
                Assert.Equal(m.LineOfChar(ci), doc.Index.LineOfOffset(m.ByteOffsetOfChar(ci)));
            }
        }
    }

    [Fact]
    public async Task Five_million_lines_need_well_under_a_megabyte_of_index()
    {
        // 1.0 allocated about 80 MB for this file (proof test P2): 16 bytes per line.
        const int lines = 5_000_000;
        byte[] data = new byte[lines * 2];
        for (int i = 0; i < data.Length; i += 2) { data[i] = (byte)'x'; data[i + 1] = (byte)'\n'; }
        using var doc = TextDocument.Open(_tmp.Write("many.txt", data));

        await doc.IndexAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(lines, doc.LineCount);
        Assert.True(doc.Index.MemoryBytes <= 1 << 20, $"index holds {doc.Index.MemoryBytes:N0} bytes");
        Assert.Equal(lines / 256 + 1, doc.Index.CheckpointCount);
        Assert.Equal("x", doc.ReadLine(lines - 1).Text);
        Assert.Equal(4_000_000L * 2, doc.Index.LineStart(4_000_000));
    }

    [Fact]
    public async Task The_line_after_a_very_long_line_is_found_without_reading_the_long_line()
    {
        // 50 MB line: checkpoints inside it every 1 MB keep each lookup to about 1 MB of reading.
        const int longLen = 50 * 1024 * 1024;
        byte[] head = "first\n"u8.ToArray(), tail = "\nafter line\n"u8.ToArray();
        byte[] data = new byte[head.Length + longLen + tail.Length];
        head.CopyTo(data, 0);
        data.AsSpan(head.Length, longLen).Fill((byte)'a');
        tail.CopyTo(data, head.Length + longLen);
        using var doc = TextDocument.Open(_tmp.Write("long.txt", data));
        await doc.IndexAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(doc.Index.CheckpointCount >= 50, $"{doc.Index.CheckpointCount} checkpoints");
        Assert.Equal(3, doc.LineCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var lines = doc.ReadLines(0, 10);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(3, lines.Count);
        Assert.Equal("first", lines[0].Text);
        Assert.True(lines[1].Truncated);
        Assert.Equal(TextDocument.DefaultMaxLineBytes, lines[1].Text.Length);
        Assert.Equal(longLen + 1, lines[1].ByteLength);
        Assert.Equal("after line", lines[2].Text);
        // 1.0 allocated over 3 bytes per byte of the line (proof test P8); here it's the 64 KB shown, not 50 MB.
        Assert.True(allocated < 2 << 20, $"reading the lines allocated {allocated:N0} bytes");
    }
}
