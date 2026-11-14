using System.Text;
using System.Text.RegularExpressions;
using BejView.Core.Documents;
using BejView.Core.Filtering;
using BejView.Core.Highlighting;
using BejView.Core.Navigation;
using BejView.Core.Search;
using BejView.Core.Text;

namespace BejView.Tests;

/// <summary>The Core pieces added for the window in step 3: matching lines, the filter, highlights, go to, bookmarks, byte ranges.</summary>
public sealed class Step3CoreTests : IDisposable
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

    private static Regex ToRegex(SearchQuery q)
    {
        string pattern = q.IsRegex ? q.Text : Regex.Escape(q.Text);
        if (q.WholeWord) pattern = $@"\b(?:{pattern})\b";
        return new Regex(pattern, RegexOptions.CultureInvariant | (q.CaseSensitive ? 0 : RegexOptions.IgnoreCase));
    }

    /// <summary>Plain answer: the lines (0-based) where the pattern matches, testing each line on its own.</summary>
    private static List<long> MatchingLinesPlain(Model m, SearchQuery q)
    {
        var re = ToRegex(q);
        return m.Lines.Select((l, i) => (l, i)).Where(x => re.IsMatch(x.l)).Select(x => (long)x.i).ToList();
    }

    public static TheoryData<TextEncodingKind> Kinds() =>
        new(TextEncodingKind.Utf8, TextEncodingKind.Utf16LE, TextEncodingKind.Utf16BE, TextEncodingKind.Latin1);

    // ---- matching lines (search in lines-only mode) ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task MatchingLines_reports_each_matching_line_once_with_its_first_match(TextEncodingKind kind)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var r = new Random(seed * 7919 + (int)kind);
            var m = new Model(Model.RandomText(r, r.Next(1, 150), latin1Only: kind == TextEncodingKind.Latin1, longLineMax: 400), kind, bom: seed % 2 == 0);
            using var doc = await Open(m);
            foreach (var q in new[]
                     {
                         new SearchQuery("a", CaseSensitive: true),
                         new SearchQuery("E", CaseSensitive: false),
                         new SearchQuery("x", WholeWord: true),
                         new SearchQuery("[éü]+", IsRegex: true),
                     })
            {
                var all = SearchEngine.Run(doc, q, int.MaxValue, null, null, Ct, 4, null, unitBytes: 64, overlap: 4096).Hits;
                var firstPerLine = all.GroupBy(h => h.Line).Select(g => g.First()).ToList();

                var got = new List<SearchHit>();
                var s = SearchEngine.Run(doc, q, int.MaxValue, b => got.AddRange(b), null, Ct, 4, null, unitBytes: 64, overlap: 4096, linesOnly: true);

                Assert.Equal(firstPerLine, got);
                Assert.Equal(firstPerLine.Count, s.TotalMatches);
                Assert.Empty(s.Hits);
                Assert.Equal(MatchingLinesPlain(m, q), got.Select(h => h.Line).ToList());
            }
        }
    }

    // ---- LineRanges ----

    [Fact]
    public void LineRanges_matches_a_plain_list()
    {
        var r = new Random(42);
        for (int round = 0; round < 50; round++)
        {
            var ranges = new LineRanges();
            var plain = new List<long>();
            long line = 0;
            for (int i = 0; i < r.Next(0, 60); i++)
            {
                line += r.Next(0, 4);
                if (r.Next(3) == 0)
                {
                    long end = line + r.Next(0, 5);
                    ranges.AddRange(line, end);
                    for (long l = line; l < end; l++) if (plain.Count == 0 || l > plain[^1]) plain.Add(l);
                    line = end;
                }
                else
                {
                    ranges.Add(line);
                    if (plain.Count == 0 || line > plain[^1]) plain.Add(line);
                    if (r.Next(4) == 0) ranges.Add(line); // repeats are ignored
                }
            }

            Assert.Equal(plain.Count, ranges.RowCount);
            for (int row = 0; row < plain.Count; row++) Assert.Equal(plain[row], ranges.LineAt(row));
            for (long l = 0; l <= line + 2; l++)
            {
                int expectedRow = plain.FindIndex(x => x >= l);
                Assert.Equal(expectedRow, ranges.RowAtOrAfter(l));
                Assert.Equal(plain.Contains(l), ranges.Contains(l));
            }
            int first = plain.Count == 0 ? 0 : r.Next(plain.Count);
            int count = r.Next(1, 20);
            var flat = ranges.Runs(first, count).SelectMany(x => Enumerable.Range(0, x.Count).Select(k => x.Line + k)).ToList();
            Assert.Equal(plain.Skip(first).Take(count).ToList(), flat);
        }
    }

    [Fact]
    public void LineRanges_merges_touching_lines_into_one_range()
    {
        var ranges = new LineRanges();
        for (long l = 0; l < 1_000_000; l++) ranges.Add(l);
        ranges.AddRange(2_000_000, 3_000_000);
        Assert.Equal(2, ranges.RangeCount);
        Assert.Equal(2_000_000, ranges.RowCount);
        Assert.Equal(2_500_000, ranges.LineAt(1_500_000));
    }

    // ---- LineFilter ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Filter_shows_matching_or_non_matching_lines(TextEncodingKind kind)
    {
        for (int seed = 1; seed <= 8; seed++)
        {
            var r = new Random(seed * 31 + (int)kind);
            var m = new Model(Model.RandomText(r, r.Next(1, 200), latin1Only: kind == TextEncodingKind.Latin1, longLineMax: 300), kind, bom: false);
            using var doc = await Open(m);
            var q = new SearchQuery("b");
            var plain = MatchingLinesPlain(m, q);
            var all = Enumerable.Range(0, m.Lines.Count).Select(i => (long)i).ToList();

            var matching = new LineRanges();
            var res = LineFilter.Run(doc, q, invert: false, matching, ct: Ct);
            Assert.Equal(plain, Enumerable.Range(0, (int)matching.RowCount).Select(i => matching.LineAt(i)).ToList());
            Assert.Equal(plain.Count, res.Lines);
            Assert.False(res.StoppedAtLimit);

            var notMatching = new LineRanges();
            res = LineFilter.Run(doc, q, invert: true, notMatching, ct: Ct);
            Assert.Equal(all.Except(plain).ToList(), Enumerable.Range(0, (int)notMatching.RowCount).Select(i => notMatching.LineAt(i)).ToList());
            Assert.Equal(m.Lines.Count, res.SearchedLines);
        }
    }

    [Fact]
    public async Task Filter_on_lines_longer_than_a_search_unit_lists_each_line_once()
    {
        // Lines of 3-5 KB with index checkpoints every 512 bytes: search units start inside lines.
        var sb = new StringBuilder();
        var r = new Random(5);
        var expected = new List<long>();
        for (int i = 0; i < 40; i++)
        {
            string body = new string('x', r.Next(3000, 5000));
            if (r.Next(2) == 0) { body = body.Insert(r.Next(body.Length), "NEEDLE").Insert(r.Next(body.Length), "NEEDLE"); expected.Add(i); }
            sb.Append(body).Append('\n');
        }
        var m = new Model(sb.ToString(), TextEncodingKind.Utf8, false);
        using var doc = await Open(m, linesPer: 256, bytesPer: 512);
        var ranges = new LineRanges();
        LineFilter.Run(doc, new SearchQuery("needle"), false, ranges, ct: Ct);
        Assert.Equal(expected, Enumerable.Range(0, (int)ranges.RowCount).Select(i => ranges.LineAt(i)).ToList());
    }

    [Fact]
    public async Task Filter_stops_cleanly_at_the_block_limit()
    {
        // Every other line matches: each match is its own block.
        string text = string.Concat(Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? "hit\n" : "miss\n"));
        using var doc = await Open(new Model(text, TextEncodingKind.Utf8, false), linesPer: 256);
        var ranges = new LineRanges();
        var res = LineFilter.Run(doc, new SearchQuery("hit"), false, ranges, maxRanges: 100, ct: Ct);
        Assert.True(res.StoppedAtLimit);
        Assert.InRange(ranges.RangeCount, 100, 101);
        Assert.True(res.CompleteUpTo > 0 && res.CompleteUpTo < 1000);
        for (int row = 0; row < ranges.RowCount; row++) Assert.Equal(row * 2L, ranges.LineAt(row));
    }

    [Fact]
    public async Task Filter_reports_a_bad_regex_with_a_fix()
    {
        using var doc = await Open(new Model("a\nb\n", TextEncodingKind.Utf8, false));
        var ex = Assert.Throws<SearchException>(() => LineFilter.Run(doc, new SearchQuery("(", IsRegex: true), false, new LineRanges(), ct: Ct));
        Assert.Contains("Regex", ex.Fix);
    }

    // ---- highlights ----

    [Fact]
    public void Papyrus_preset_colours_errors_warnings_stacks_and_VM_messages()
    {
        var h = Highlighter.Compile(HighlightPresets.PapyrusRules(), out var problems);
        Assert.Empty(problems);
        Assert.Equal(HighlightColor.Red, h.Match("[11/11/2026 - 10:15:01AM] error: Cannot call GetValue() on a None object, aborting function call"));
        Assert.Equal(HighlightColor.Amber, h.Match("[11/11/2026 - 10:15:01AM] warning: Property Foo on script Bar attached to (00012345) cannot be initialized"));
        Assert.Equal(HighlightColor.Purple, h.Match("[11/11/2026 - 10:15:01AM] Stack:"));
        Assert.Equal(HighlightColor.Purple, h.Match("\t[ (00012345)].SomeScript.OnInit() - \"SomeScript.psc\" Line 12"));
        Assert.Equal(HighlightColor.Blue, h.Match("[11/11/2026 - 10:15:01AM] Papyrus log opened (PC)"));
        Assert.Equal(HighlightColor.Blue, h.Match("[11/11/2026 - 10:15:02AM] VM is freezing..."));
        Assert.Null(h.Match("[11/11/2026 - 10:15:01AM] [MYMOD] Started quest"));
        Assert.Null(h.Match("[11/11/2026 - 10:15:01AM] no errors here, terror: is not one either"));
    }

    [Fact]
    public void Rules_with_problems_are_skipped_and_explained()
    {
        var rules = new List<HighlightRule>
        {
            new("(", HighlightColor.Red, isRegex: true),
            new() { Pattern = "x", Color = "Pink" },
            new() { Pattern = "", Color = "Red" },
            new("WARN", HighlightColor.Amber, caseSensitive: true),
        };
        var h = Highlighter.Compile(rules, out var problems);
        Assert.Equal(1, h.Count);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("Pink"));
        Assert.Equal(HighlightColor.Amber, h.Match("a WARN b"));
        Assert.Null(h.Match("a warn b"));
    }

    [Fact]
    public void First_matching_rule_wins()
    {
        var h = Highlighter.Compile([new("error", HighlightColor.Red), new("err", HighlightColor.Blue)], out _);
        Assert.Equal(HighlightColor.Red, h.Match("an ERROR"));
        Assert.Equal(HighlightColor.Blue, h.Match("stderr"));
    }

    // ---- go to ----

    [Theory]
    [InlineData("12345", GoToKind.Line, 12345)]
    [InlineData(" 1,234,567 ", GoToKind.Line, 1234567)]
    [InlineData("1_000", GoToKind.Line, 1000)]
    [InlineData("@1048576", GoToKind.ByteOffset, 1048576)]
    [InlineData("0x100000", GoToKind.ByteOffset, 1048576)]
    [InlineData("0x1b", GoToKind.ByteOffset, 27)]
    [InlineData("4096b", GoToKind.ByteOffset, 4096)]
    public void GoTo_parses_lines_and_offsets(string input, GoToKind kind, long value)
    {
        Assert.True(GoToTarget.TryParse(input, out var t, out _));
        Assert.Equal(kind, t.Kind);
        Assert.Equal(value, t.Value);
    }

    [Theory]
    [InlineData("50%", 50.0)]
    [InlineData("0%", 0.0)]
    [InlineData("100 %", 100.0)]
    [InlineData("12.5%", 12.5)]
    public void GoTo_parses_percentages(string input, double p)
    {
        Assert.True(GoToTarget.TryParse(input, out var t, out _));
        Assert.Equal(GoToKind.Percent, t.Kind);
        Assert.Equal(p, t.Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("101%")]
    [InlineData("@-1")]
    [InlineData("0xZZ")]
    public void GoTo_rejects_nonsense_with_a_message(string input)
    {
        Assert.False(GoToTarget.TryParse(input, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task GoTo_resolves_to_the_right_line()
    {
        // 100 lines of 10 bytes each ("line 0000\n").
        string text = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:0000}\n"));
        using var doc = await Open(new Model(text, TextEncodingKind.Utf8, bom: true));
        long Resolve(string s) { Assert.True(GoToTarget.TryParse(s, out var t, out _)); return t.ResolveLine(doc); }
        Assert.Equal(0, Resolve("1"));
        Assert.Equal(41, Resolve("42"));
        Assert.Equal(99, Resolve("5000"));            // past the end: the last line
        Assert.Equal(50, Resolve("50%"));
        Assert.Equal(0, Resolve("0%"));
        Assert.Equal(99, Resolve("100%"));
        Assert.Equal(12, Resolve("@" + (3 + 125)));   // BOM 3 bytes + 12.5 lines
        Assert.False(new GoToTarget(GoToKind.Line, 5000).IsPending(doc));
    }

    // ---- bookmarks ----

    [Fact]
    public void Bookmarks_toggle_and_wrap()
    {
        var b = new Bookmarks();
        Assert.Null(b.Next(0));
        Assert.True(b.Toggle(10));
        Assert.True(b.Toggle(50));
        Assert.True(b.Toggle(30));
        Assert.Equal(30, b.Next(10));
        Assert.Equal(10, b.Next(50));      // wraps
        Assert.Equal(50, b.Previous(10));  // wraps
        Assert.Equal(30, b.Previous(50));
        Assert.Equal(10, b.Next(0));
        Assert.False(b.Toggle(30));
        Assert.Equal(50, b.Next(10));
        b.TrimTo(20);
        Assert.Equal([10L], b.Lines);
    }

    // ---- byte offsets of columns and byte copies ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ByteOffsetOfColumn_is_the_inverse_of_CharColumn(TextEncodingKind kind)
    {
        var r = new Random(77 + (int)kind);
        var m = new Model(Model.RandomText(r, 80, latin1Only: kind == TextEncodingKind.Latin1, longLineMax: 300), kind, bom: true);
        using var doc = await Open(m);
        foreach (var line in doc.ReadLines(0, m.Lines.Count))
        {
            int charStart = m.CharStarts[(int)line.Number];
            for (int col = 0; col <= line.Text.Length; col++)
            {
                if (col > 0 && col < line.Text.Length && char.IsLowSurrogate(line.Text[col])) continue; // inside an emoji
                long expected = m.ByteOffsetOfChar(charStart + col);
                long got = doc.ByteOffsetOfColumn(line, col);
                Assert.Equal(expected, got);
                Assert.Equal(col, doc.CharColumn(line, got));
            }
        }
    }

    [Fact]
    public async Task ByteOffsetOfColumn_is_exact_after_invalid_UTF8_bytes()
    {
        byte[] bytes = [(byte)'x', 0xFF, 0xFE, (byte)' ', (byte)'h', (byte)'i', (byte)'\n'];
        string path = _tmp.Write("bad.txt", bytes);
        using var doc = TextDocument.Open(path, encoding: TextEncodingKind.Utf8);
        await doc.IndexAsync(ct: Ct);
        var line = doc.ReadLine(0);
        int col = line.Text.IndexOf('h');
        Assert.Equal(4, doc.ByteOffsetOfColumn(line, col));
    }

    [Fact]
    public async Task CopyBytes_copies_the_exact_range()
    {
        var r = new Random(3);
        byte[] data = new byte[3_000_000];
        r.NextBytes(data);
        string path = _tmp.Write("data.bin", data);
        using var doc = TextDocument.Open(path, encoding: TextEncodingKind.Latin1);
        await doc.IndexAsync(ct: Ct);
        using var ms = new MemoryStream();
        long n = doc.CopyBytes(12345, 2_500_000, ms, ct: Ct);
        Assert.Equal(2_500_000 - 12345, n);
        Assert.Equal(data.AsSpan(12345, (int)n).ToArray(), ms.ToArray());
    }
}
