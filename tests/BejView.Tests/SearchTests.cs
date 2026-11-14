using System.Text;
using System.Text.RegularExpressions;
using BejView.Core.Documents;
using BejView.Core.Search;
using BejView.Core.Text;

namespace BejView.Tests;

public sealed class SearchTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TextDocument> Open(Model m, int linesPer = 3, int bytesPer = 1 << 20)
    {
        string path = _tmp.Write(Guid.NewGuid().ToString("N") + ".txt", m.Bytes);
        var doc = TextDocument.Open(path, encoding: m.Kind);
        doc.UseIndexSpacing(linesPer, bytesPer, 64);
        await doc.IndexAsync(ct: Ct);
        return doc;
    }

    /// <summary>The slow, plain answer: Regex over the whole text, positions converted one by one.</summary>
    private static List<SearchHit> Expected(Model m, SearchQuery q)
    {
        string pattern = q.IsRegex ? q.Text : Regex.Escape(q.Text);
        if (q.WholeWord) pattern = $@"\b(?:{pattern})\b";
        var opt = RegexOptions.CultureInvariant | RegexOptions.Multiline | (q.CaseSensitive ? 0 : RegexOptions.IgnoreCase);
        var hits = new List<SearchHit>();
        foreach (Match x in new Regex(pattern, opt).Matches(m.Text))
        {
            int line = m.LineOfChar(x.Index);
            int col = x.Index - m.CharStarts[line];
            long at = m.ByteOffsetOfChar(x.Index);
            int bytes = m.Encoding.GetByteCount(m.Text.AsSpan(x.Index, x.Length));
            hits.Add(new SearchHit(line, at, bytes, col, x.Length));
        }
        return hits;
    }

    public static TheoryData<TextEncodingKind, bool, bool, bool> Cases()
    {
        var d = new TheoryData<TextEncodingKind, bool, bool, bool>();
        foreach (var kind in new[] { TextEncodingKind.Utf8, TextEncodingKind.Utf16LE, TextEncodingKind.Utf16BE, TextEncodingKind.Latin1 })
        foreach (bool caseSensitive in new[] { true, false })
        foreach (bool word in new[] { false, true })
            d.Add(kind, kind != TextEncodingKind.Latin1 && caseSensitive, caseSensitive, word);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Search_matches_the_plain_answer_on_random_text(TextEncodingKind kind, bool bom, bool caseSensitive, bool word)
    {
        for (int seed = 1; seed <= 15; seed++)
        {
            var r = new Random(seed * 104729 + (int)kind);
            // No lines over 1 MB here, so units always meet at line starts (see the long-line test below).
            var m = new Model(Model.RandomText(r, r.Next(1, 150), latin1Only: kind == TextEncodingKind.Latin1, longLineMax: 400), kind, bom);
            using var doc = await Open(m);

            string needle = PickNeedle(r, m.Text);
            foreach (var q in new[]
                     {
                         new SearchQuery(needle, caseSensitive, IsRegex: false, word),
                         new SearchQuery("[aé€]+b?", caseSensitive, IsRegex: true, word),
                         new SearchQuery("^E", caseSensitive, IsRegex: true, word),
                     })
            {
                var expected = Expected(m, q);
                foreach (int threads in new[] { 1, 4 })
                {
                    var got = SearchEngine.Run(doc, q, int.MaxValue, null, null, Ct, threads, null, unitBytes: 64, overlap: 4096);
                    Assert.Equal(expected.Count, got.TotalMatches);
                    Assert.Equal(expected, got.Hits);
                }
            }
        }
    }

    private static string PickNeedle(Random r, string text)
    {
        for (int tries = 0; tries < 50; tries++)
        {
            int len = r.Next(1, 4);
            if (text.Length < len) break;
            int at = r.Next(text.Length - len + 1);
            string s = text.Substring(at, len);
            if (s.IndexOfAny(['\n', '\r']) < 0 && !char.IsLowSurrogate(s[0]) && !char.IsHighSurrogate(s[^1])) return s;
        }
        return "a";
    }

    [Fact]
    public async Task Hits_after_non_ASCII_text_are_on_the_right_line()
    {
        // 1.0 put this on line 0 (proof test P3).
        var m = new Model(new string('é', 100) + "\ntarget here\nlast\n", TextEncodingKind.Utf8, false);
        using var doc = await Open(m);
        foreach (var q in new[] { new SearchQuery("target", CaseSensitive: true), new SearchQuery("TARGET") })
        {
            var s = SearchEngine.Run(doc, q, log: null, ct: Ct);
            var hit = Assert.Single(s.Hits);
            Assert.Equal(1, hit.Line);
            Assert.Equal(0, hit.Column);
            Assert.Equal(201, hit.ByteOffset);
        }
    }

    [Fact]
    public async Task Invalid_UTF8_bytes_do_not_shift_the_positions()
    {
        // 0xFF is not valid UTF-8 and shows as U+FFFD (3 bytes if re-encoded); the match must still point at the real bytes.
        byte[] bytes = [(byte)'x', 0xFF, 0xFE, (byte)' ', (byte)'h', (byte)'i', (byte)'\n', 0xC3, (byte)'h', (byte)'i', (byte)'\n'];
        string path = _tmp.Write("bad.txt", bytes);
        using var doc = TextDocument.Open(path, encoding: TextEncodingKind.Utf8);
        await doc.IndexAsync(ct: Ct);

        var s = SearchEngine.Run(doc, new SearchQuery("HI"), ct: Ct);
        Assert.Equal([4L, 8L], s.Hits.Select(h => h.ByteOffset));
        Assert.Equal([0L, 1L], s.Hits.Select(h => h.Line));
    }

    [Fact]
    public async Task Hits_past_the_limit_are_counted_but_not_kept()
    {
        // 1.0 kept every hit with copies of its context lines (proof test P5).
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append("the cat\n");
        var m = new Model(sb.ToString(), TextEncodingKind.Utf8, false);
        string path = _tmp.Write("common.txt", m.Bytes);
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);

        int batches = 0;
        var s = SearchEngine.Run(doc, new SearchQuery("the"), maxHits: 10_000, onHits: _ => batches++, ct: Ct);

        Assert.Equal(200_000, s.TotalMatches);
        Assert.Equal(10_000, s.Hits.Count);
        Assert.True(s.LimitReached);
        Assert.Equal(Enumerable.Range(0, 10_000).Select(i => (long)i), s.Hits.Select(h => h.Line));
        Assert.True(batches >= 1);
    }

    [Fact]
    public async Task Matches_inside_a_very_long_line_are_found_across_unit_edges()
    {
        // 3 MB line with markers every 100 KB; units of 256 KB cut it at mid-line checkpoints.
        const int len = 3 * 1024 * 1024;
        var bytes = new byte[len + 1];
        Array.Fill(bytes, (byte)'.');
        var expected = new List<long>();
        for (int at = 50_000; at + 6 < len; at += 100_000)
        {
            "MARKER"u8.CopyTo(bytes.AsSpan(at));
            expected.Add(at);
        }
        bytes[^1] = (byte)'\n';
        string path = _tmp.Write("long.txt", bytes);
        using var doc = TextDocument.Open(path);
        doc.UseIndexSpacing(256, 64 * 1024, 1 << 20);
        await doc.IndexAsync(ct: Ct);

        foreach (var q in new[] { new SearchQuery("MARKER", CaseSensitive: true), new SearchQuery("marker"), new SearchQuery("MARK(ER)", IsRegex: true) })
        {
            var s = SearchEngine.Run(doc, q, int.MaxValue, null, null, Ct, 4, null, unitBytes: 256 * 1024, overlap: 1024);
            Assert.Equal(expected, s.Hits.Select(h => h.ByteOffset));
            Assert.All(s.Hits, h => Assert.Equal(0, h.Line));
            Assert.All(s.Hits, h => Assert.Equal(6, h.ByteLength));
        }
    }

    [Fact]
    public async Task A_bad_regular_expression_explains_itself()
    {
        var m = new Model("abc\n", TextEncodingKind.Utf8, false);
        using var doc = await Open(m);
        var ex = Assert.Throws<SearchException>(() => SearchEngine.Run(doc, new SearchQuery("(unclosed", IsRegex: true), ct: Ct));
        Assert.Contains("regular expression", ex.Message);
        Assert.Contains("Regex", ex.Fix);
        Assert.Throws<SearchException>(() => SearchEngine.Run(doc, new SearchQuery(""), ct: Ct));
    }

    [Fact]
    public async Task A_search_can_be_cancelled()
    {
        byte[] big = new byte[64 * 1024 * 1024];
        Array.Fill(big, (byte)'a');
        for (int i = 99; i < big.Length; i += 100) big[i] = (byte)'\n';
        using var doc = TextDocument.Open(_tmp.Write("big.txt", big));
        await doc.IndexAsync(ct: Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchEngine.RunAsync(doc, new SearchQuery("zzz"), ct: cts.Token));
    }

    [Fact]
    public async Task Case_sensitive_plain_text_in_UTF8_uses_the_byte_search()
    {
        var m = new Model("Alpha beta\n", TextEncodingKind.Utf8, false);
        using var doc = await Open(m);
        Assert.Equal("Literal (bytes)", SearchEngine.Run(doc, new SearchQuery("beta", CaseSensitive: true), ct: Ct).Method);
        Assert.Equal("Literal (bytes, ignore ASCII case)", SearchEngine.Run(doc, new SearchQuery("BETA"), ct: Ct).Method);
        Assert.Single(SearchEngine.Run(doc, new SearchQuery("BETA"), ct: Ct).Hits);
        Assert.Equal("Regex", SearchEngine.Run(doc, new SearchQuery("béta"), ct: Ct).Method);
        Assert.Equal("Regex", SearchEngine.Run(doc, new SearchQuery("beta", WholeWord: true), ct: Ct).Method);
    }
}
