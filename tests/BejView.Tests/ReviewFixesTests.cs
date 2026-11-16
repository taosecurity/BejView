using System.Text;
using BejView.Core.Documents;
using BejView.Core.Search;

namespace BejView.Tests;

/// <summary>2.0.1: fixes from the 2026-11-16 code review of the 2.0.0 source.</summary>
public sealed class ReviewFixesTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TextDocument> Open(string name, string text)
    {
        var doc = TextDocument.Open(_tmp.Write(name, Encoding.UTF8.GetBytes(text)));
        await doc.IndexAsync(ct: Ct);
        return doc;
    }

    [Fact]
    public async Task Regex_end_of_line_matches_in_a_CRLF_file()
    {
        using var doc = await Open("crlf.log", "an error\r\nfine\r\nanother error\r\nerror here\r\n");
        var s = SearchEngine.Run(doc, new SearchQuery("error$", IsRegex: true), ct: Ct);
        Assert.Equal(2, s.TotalMatches);
        var lf = await Open("lf.log", "an error\nfine\nanother error\n");
        using (lf) Assert.Equal(2, SearchEngine.Run(lf, new SearchQuery(@"\d*error$", IsRegex: true), ct: Ct).TotalMatches);
    }

    [Theory]
    [InlineData("a$", @"a(?=\r?$)")]
    [InlineData(@"cost \$5", @"cost \$5")]
    [InlineData("[$]x", "[$]x")]
    [InlineData("[]$]$", @"[]$](?=\r?$)")]
    [InlineData("^$", @"^(?=\r?$)")]
    public void Only_anchor_dollars_are_rewritten(string pattern, string expected) =>
        Assert.Equal(expected, SearchEngine.EndOfLineBeforeCr(pattern));

    [Fact]
    public async Task A_rotated_log_with_the_same_long_header_is_still_seen()
    {
        string header = "=== The same banner every time, longer than the 256-byte fingerprint " + new string('=', 300) + "\n";
        string path = _tmp.Write("app.log", Encoding.UTF8.GetBytes(header + "old 1\nold 2\nold 3\nold 4\n"));
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);
        File.Move(path, Path.Combine(_tmp.Path, "app.1.log"));
        File.WriteAllText(path, header + "new 1\n");
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal("new 1", doc.ReadLines(1, 1)[0].Text);
        File.AppendAllText(path, "new 2\n");
        Assert.Equal(RefreshResult.Grew, await doc.RefreshAsync(Ct));   // growing is not a rotation
    }

    [Fact]
    public async Task A_file_opened_empty_then_rewritten_in_place_is_seen()
    {
        string path = _tmp.Write("empty.log", []);
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);
        File.WriteAllText(path, "first line\nsecond\n");
        Assert.Equal(RefreshResult.Grew, await doc.RefreshAsync(Ct));
        // Rewritten in place with the same length or longer: the start no longer matches what was taken as it grew.
        File.WriteAllText(path, "FIRST LINE\nsecond line, longer\n");
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal("FIRST LINE", doc.ReadLines(0, 1)[0].Text);
    }

    [Fact]
    public async Task Many_hits_on_one_line_get_the_right_columns()
    {
        string line = string.Concat(Enumerable.Repeat("é,", 500));   // two-byte characters: columns are not byte offsets
        using var doc = await Open("long.txt", "head\n" + line + "\n");
        
        var s = SearchEngine.Run(doc, new SearchQuery(",", CaseSensitive: true), maxHits: 1000, ct: Ct);
        Assert.Equal(500, s.TotalMatches);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => i * 2 + 1), s.Hits.OrderBy(h => h.ByteOffset).Select(h => h.Column));
    }
}
