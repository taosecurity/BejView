using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using BejView.Core.Documents;
using BejView.Core.Index;
using BejView.Core.Logging;
using BejView.Core.Text;

namespace BejView.Core.Search;

public sealed record SearchQuery(string Text, bool CaseSensitive = false, bool IsRegex = false, bool WholeWord = false)
{
    public override string ToString() =>
        $"\"{Text}\"" + (CaseSensitive ? " case" : "") + (IsRegex ? " regex" : "") + (WholeWord ? " word" : "");
}

/// <summary>One match.</summary>
/// <param name="Line">0-based line number.</param>
/// <param name="ByteOffset">Where the match starts in the file.</param>
/// <param name="ByteLength">Length of the match in bytes.</param>
/// <param name="Column">Character column in the line, or -1 when the match is inside a line over 1 MB long and its line start is in an earlier search unit.</param>
/// <param name="CharLength">Length of the match in characters.</param>
public readonly record struct SearchHit(long Line, long ByteOffset, int ByteLength, int Column, int CharLength);

public sealed class SearchSummary
{
    public required SearchQuery Query { get; init; }

    /// <summary>The first matches in file order, at most the limit asked for.</summary>
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    /// <summary>Every match in the searched part of the file, including the ones past the limit.</summary>
    public required long TotalMatches { get; init; }

    public bool LimitReached => TotalMatches > Hits.Count;

    /// <summary>Bytes searched: the part of the file indexed when the search started.</summary>
    public required long SearchedBytes { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>"Literal (bytes)" or "Regex", for the log and benchmarks.</summary>
    public required string Method { get; init; }
}

/// <summary>The search text can't be used. <see cref="Exception.Message"/> says why; <see cref="Fix"/> says what to do.</summary>
public sealed class SearchException(string message, string fix, Exception? inner = null) : Exception(message, inner)
{
    public string Fix { get; } = fix;
}

/// <summary>
/// Searches a <see cref="TextDocument"/>. The indexed part of the file is cut into units of about 1 MB at checkpoints
/// (so each unit knows its first line number), the units are searched in parallel, and the results are put back in
/// file order. Positions are kept in bytes and mapped to lines and columns exactly, so UTF-8 text with accents and
/// UTF-16 files give the right lines (1.0 mixed characters and bytes).
/// <list type="bullet">
/// <item>Case-sensitive plain text in UTF-8 or Latin-1 is searched as raw bytes (vectorised, no decoding).</item>
/// <item>Everything else (ignore case, whole word, regular expressions, UTF-16) decodes each unit and uses .NET Regex.</item>
/// <item>Known limits, all at the edge between two units: a match that spans a line break can be missed. Inside a line
/// longer than 1 MB, units also meet mid-line (with a 64 KB overlap), so a repeating or variable-length match that
/// crosses that edge can be counted twice or cut short, and <c>^</c> can match at the edge. Plain words on normal lines
/// are never affected.</item>
/// </list>
/// </summary>
public static class SearchEngine
{
    public const int DefaultMaxHits = 10_000;
    internal const int UnitBytes = 1 << 20;
    internal const int Overlap = 64 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);

    public static Task<SearchSummary> RunAsync(TextDocument doc, SearchQuery query, int maxHits = DefaultMaxHits,
        Action<IReadOnlyList<SearchHit>>? onHits = null, IProgress<double>? progress = null,
        CancellationToken ct = default, int? parallelism = null, Logger? log = null) =>
        Task.Run(() => Run(doc, query, maxHits, onHits, progress, ct, parallelism, log), ct);

    /// <summary>
    /// Runs a search on the calling thread (plus worker threads). <paramref name="onHits"/> gets each new batch of hits
    /// in file order as soon as it's ready. Throws <see cref="SearchException"/> for an unusable query and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static SearchSummary Run(TextDocument doc, SearchQuery query, int maxHits = DefaultMaxHits,
        Action<IReadOnlyList<SearchHit>>? onHits = null, IProgress<double>? progress = null,
        CancellationToken ct = default, int? parallelism = null, Logger? log = null) =>
        Run(doc, query, maxHits, onHits, progress, ct, parallelism, log, UnitBytes, Overlap);

    /// <summary>
    /// Finds the lines that hold a match (for the filter pane): at most one hit per line (its first match), streamed to
    /// <paramref name="onHits"/> in file order and not kept, so memory stays small however many lines match.
    /// <see cref="SearchSummary.TotalMatches"/> is the number of matching lines and <see cref="SearchSummary.Hits"/> is
    /// empty. A line over 1 MB long can be reported more than once (once per search unit it spans); callers drop repeats.
    /// </summary>
    public static SearchSummary MatchingLines(TextDocument doc, SearchQuery query, Action<IReadOnlyList<SearchHit>> onHits,
        IProgress<double>? progress = null, CancellationToken ct = default, int? parallelism = null, Logger? log = null) =>
        Run(doc, query, int.MaxValue, onHits, progress, ct, parallelism, log, UnitBytes, Overlap, linesOnly: true);

    /// <summary>Tests use small units and overlaps to exercise the places where units meet.</summary>
    internal static SearchSummary Run(TextDocument doc, SearchQuery query, int maxHits,
        Action<IReadOnlyList<SearchHit>>? onHits, IProgress<double>? progress,
        CancellationToken ct, int? parallelism, Logger? log, int unitBytes, int overlap, bool linesOnly = false)
    {
        log ??= Logger.Null;
        ArgumentOutOfRangeException.ThrowIfNegative(maxHits);
        if (string.IsNullOrEmpty(query.Text))
            throw new SearchException("The search text is empty.", "Type something to search for.");

        var sw = Stopwatch.StartNew();
        // One index for the whole search: after a log rotation the document moves to the new file, but these units,
        // their offsets and line numbers belong to this index's file and encoding.
        var index = doc.Index;
        var matcher = Matcher.Create(query, index.Encoding);
        var units = BuildUnits(index, unitBytes, overlap);
        long searched = units.Count == 0 ? 0 : units[^1].End - units[0].Start;
        int p = Math.Max(1, parallelism ?? Environment.ProcessorCount);
        var hits = new List<SearchHit>(Math.Min(maxHits, 1024));
        long total = 0;
        // Buffers belong to this search and are freed with it (the shared pool would keep up to ~12 MB per thread).
        var buffers = new Buffers(p);

        for (int b = 0; b < units.Count; b += p)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(p, units.Count - b);
            var results = new UnitResult[n];
            bool collect = linesOnly || hits.Count < maxHits;
            int cap = linesOnly ? int.MaxValue : maxHits - hits.Count;
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = p, CancellationToken = ct },
                j => results[j] = SearchUnit(index, units[b + j], matcher, collect ? cap : 0, linesOnly, buffers, ct));

            foreach (var r in results)
            {
                total += r.Count;
                if (linesOnly)
                {
                    if (r.Hits.Count > 0) onHits?.Invoke(r.Hits);
                    continue;
                }
                int take = Math.Min(r.Hits.Count, maxHits - hits.Count);
                if (take > 0)
                {
                    var batch = r.Hits.GetRange(0, take);
                    hits.AddRange(batch);
                    onHits?.Invoke(batch);
                }
            }
            progress?.Report((double)(b + n) / units.Count);
        }

        progress?.Report(1.0);
        var summary = new SearchSummary
        {
            Query = query, Hits = hits, TotalMatches = total, SearchedBytes = searched,
            Elapsed = sw.Elapsed, Method = matcher.Method,
        };
        log.Info($"Search {query} in {doc.Path}: {total:N0} {(linesOnly ? "matching lines" : "matches")} ({hits.Count:N0} kept) in {searched:N0} bytes, " +
                 $"{sw.Elapsed.TotalSeconds:F2} s, {matcher.Method}, {p} threads.");
        return summary;
    }

    internal readonly record struct Unit(long Start, long StartLine, bool StartIsLineStart, long End, bool EndIsLineStart, long FileEnd, int Overlap);

    private sealed record UnitResult(List<SearchHit> Hits, long Count);

    internal static List<Unit> BuildUnits(TextDocument doc, int unitBytes = UnitBytes, int overlap = Overlap) =>
        BuildUnits(doc.Index, unitBytes, overlap);

    private static List<Unit> BuildUnits(LineIndex index, int unitBytes, int overlap)
    {
        long end = index.ScannedTo;
        long count = index.CheckpointCount;
        var units = new List<Unit>();
        long i = 0;
        var cp = index.CheckpointAt(0);
        while (cp.Offset < end)
        {
            var (ni, ncp) = index.Floor(cp.Offset + unitBytes);
            if (ni <= i)
            {
                ni = i + 1 < count ? i + 1 : -1;
                if (ni >= 0) ncp = index.CheckpointAt(ni);
            }
            bool last = ni < 0 || ncp.Offset >= end;
            long uEnd = last ? end : ncp.Offset;
            units.Add(new Unit(cp.Offset, cp.Line, cp.IsLineStart, uEnd, last || ncp.IsLineStart, end, overlap));
            if (last) break;
            i = ni;
            cp = ncp;
        }
        return units;
    }

    /// <summary>Per-search buffer pools: one byte and one char buffer per thread, dropped when the search ends.</summary>
    private sealed class Buffers(int threads)
    {
        public ArrayPool<byte> Bytes { get; } = ArrayPool<byte>.Create(16 << 20, threads);
        public ArrayPool<char> Chars { get; } = ArrayPool<char>.Create(16 << 20, threads);
    }

    private static UnitResult SearchUnit(LineIndex index, Unit u, Matcher m, int cap, bool linesOnly, Buffers buffers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int own = (int)(u.End - u.Start);
        int extra = u.EndIsLineStart ? 0 : (int)Math.Min(Math.Max(u.Overlap, m.MaxBytes * 2), u.FileEnd - u.End);
        byte[] buffer = buffers.Bytes.Rent(own + extra);
        try
        {
            int n = index.Source.Read(u.Start, buffer.AsSpan(0, own + extra));
            if (index.Encoding.UnitSize == 2) n &= ~1;
            var hits = new List<SearchHit>();
            long count = m.Search(buffer.AsSpan(0, n), Math.Min(own, n), u, index.Encoding, hits, cap, linesOnly, buffers.Chars);
            return new UnitResult(hits, count);
        }
        finally
        {
            buffers.Bytes.Return(buffer);
        }
    }

    // ---- matchers ----

    internal static string EndOfLineBeforeCr(string pattern) => Matcher.EndOfLineBeforeCr(pattern);

    private abstract class Matcher
    {
        public abstract string Method { get; }

        /// <summary>Most bytes one match can take (for the overlap inside long lines).</summary>
        public abstract int MaxBytes { get; }

        /// <summary>
        /// Counts matches that start in the first <paramref name="own"/> bytes; keeps up to <paramref name="cap"/> of them.
        /// With <paramref name="linesOnly"/>, only the first match on each line is counted and kept.
        /// </summary>
        public abstract long Search(ReadOnlySpan<byte> bytes, int own, Unit u, TextEncoding enc, List<SearchHit> hits, int cap, bool linesOnly, ArrayPool<char> chars);

        /// <summary>
        /// The text searched still holds each line's CR in a CRLF file, and .NET's <c>$</c> (Multiline) matches only
        /// before LF, so <c>error$</c> found nothing in Windows logs. Every <c>$</c> that is an anchor (not escaped, not
        /// inside a character class) becomes <c>(?=\r?$)</c>: the end of the line, before an optional CR.
        /// </summary>
        internal static string EndOfLineBeforeCr(string pattern)
        {
            if (!pattern.Contains('$')) return pattern;
            var sb = new StringBuilder(pattern.Length + 16);
            bool inClass = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    sb.Append(c).Append(pattern[++i]);
                    continue;
                }
                if (inClass)
                {
                    if (c == ']') inClass = false;
                    sb.Append(c);
                    continue;
                }
                if (c == '[')
                {
                    inClass = true;
                    sb.Append(c);
                    // A ']' right after '[' or '[^' is a literal, not the end of the class.
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') sb.Append(pattern[++i]);
                    if (i + 1 < pattern.Length && pattern[i + 1] == ']') sb.Append(pattern[++i]);
                    continue;
                }
                if (c == '$') sb.Append(@"(?=\r?$)");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static Matcher Create(SearchQuery q, TextEncoding enc)
        {
            if (q.CaseSensitive && !q.IsRegex && !q.WholeWord && !q.Text.Contains('\n') &&
                (enc.Kind == TextEncodingKind.Utf8 || (enc.Kind == TextEncodingKind.Latin1 && q.Text.All(c => c <= 'ÿ'))))
                return new ByteMatcher(enc.Encoding.GetBytes(q.Text), q.Text.Length, ignoreCase: false);

            // Ignore case with a plain ASCII word (the usual log search): compare bytes with ASCII case folding.
            // Same results as the Regex path except that the Kelvin sign (U+212A) and long s (U+017F) don't count as k and s.
            if (!q.CaseSensitive && !q.IsRegex && !q.WholeWord && System.Text.Ascii.IsValid(q.Text) && !q.Text.Contains('\n') &&
                enc.Kind is TextEncodingKind.Utf8 or TextEncodingKind.Latin1)
                return new ByteMatcher(Encoding.ASCII.GetBytes(q.Text), q.Text.Length, ignoreCase: true);

            string pattern = q.IsRegex ? EndOfLineBeforeCr(q.Text) : Regex.Escape(q.Text);
            if (q.WholeWord) pattern = $@"\b(?:{pattern})\b";
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!q.CaseSensitive) options |= RegexOptions.IgnoreCase;
            try
            {
                return new RegexMatcher(new Regex(pattern, options, RegexTimeout), q.IsRegex ? 1 << 20 : enc.Encoding.GetMaxByteCount(q.Text.Length));
            }
            catch (ArgumentException ex)
            {
                throw new SearchException($"The search text isn't a valid regular expression: {ex.Message}",
                    "Fix the pattern, or turn off Regex to search for the text exactly as typed.", ex);
            }
        }
    }

    private sealed class ByteMatcher(byte[] needle, int charLength, bool ignoreCase) : Matcher
    {
        private readonly byte _lower = ToLower(needle[0]);
        private readonly byte _upper = ToUpper(needle[0]);

        public override string Method => ignoreCase ? "Literal (bytes, ignore ASCII case)" : "Literal (bytes)";
        public override int MaxBytes => needle.Length;

        private static byte ToLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
        private static byte ToUpper(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b & ~0x20) : b;

        private int Find(ReadOnlySpan<byte> s)
        {
            if (!ignoreCase) return s.IndexOf(needle);
            int from = 0;
            while (true)
            {
                int rel = _lower == _upper ? s[from..].IndexOf(_lower) : s[from..].IndexOfAny(_lower, _upper);
                if (rel < 0) return -1;
                int at = from + rel;
                if (at + needle.Length > s.Length) return -1;
                if (System.Text.Ascii.EqualsIgnoreCase(s.Slice(at, needle.Length), needle)) return at;
                from = at + 1;
            }
        }

        public override long Search(ReadOnlySpan<byte> bytes, int own, Unit u, TextEncoding enc, List<SearchHit> hits, int cap, bool linesOnly, ArrayPool<char> chars)
        {
            long count = 0;
            long line = u.StartLine;
            int lineStart = u.StartIsLineStart ? 0 : -1; // -1: the unit starts inside a line whose start we don't know
            int last = 0;
            int i = 0;
            // The column is counted on from the previous hit on the same line, so many hits on one long line cost
            // one pass over it (it was counted from the line start each time: hits x line length).
            int colFrom = lineStart, colAt = 0;
            while (i < own)
            {
                int rel = Find(bytes[i..]);
                if (rel < 0) break;
                int at = i + rel;
                if (at >= own) break;
                count++;
                if (hits.Count < cap)
                {
                    var gap = bytes[last..at];
                    int nls = gap.Count((byte)'\n');
                    if (nls > 0)
                    {
                        line += nls;
                        lineStart = last + gap.LastIndexOf((byte)'\n') + 1;
                        colFrom = lineStart;
                        colAt = 0;
                    }
                    last = at;
                    int col = -1;
                    if (lineStart >= 0)
                    {
                        col = colAt + enc.Encoding.GetCharCount(bytes[colFrom..at]);
                        colFrom = at;
                        colAt = col;
                    }
                    hits.Add(new SearchHit(line, u.Start + at, needle.Length, col, charLength));
                }
                i = at + needle.Length;
                if (linesOnly)
                {
                    // Skip the rest of this line (only UTF-8 and Latin-1 come here, so the byte '\n' is the line break).
                    int nl = bytes[i..].IndexOf((byte)'\n');
                    if (nl < 0) break;
                    i += nl + 1;
                }
            }
            return count;
        }
    }

    private sealed class RegexMatcher(Regex regex, int maxBytes) : Matcher
    {
        public override string Method => "Regex";
        public override int MaxBytes => maxBytes;

        public override long Search(ReadOnlySpan<byte> bytes, int own, Unit u, TextEncoding enc, List<SearchHit> hits, int cap, bool linesOnly, ArrayPool<char> chars)
        {
            bool utf8 = enc.Kind == TextEncodingKind.Utf8;
            bool exactCounts = !utf8 || Utf8.IsValid(bytes);
            int charCount = enc.Encoding.GetCharCount(bytes);
            char[] buf = chars.Rent(Math.Max(charCount, 1));
            try
            {
                int len = enc.Encoding.GetChars(bytes, buf);
                ReadOnlySpan<char> text = buf.AsSpan(0, len);

                long count = 0;
                long line = u.StartLine;
                int lineStart = u.StartIsLineStart ? 0 : -1;
                int lastChar = 0;
                long lastByte = 0;
                int skipUntil = -1; // linesOnly: matches before this char index are on a line already reported
                foreach (var m in regex.EnumerateMatches(text))
                {
                    if (m.Index < skipUntil) continue;
                    var gap = text[lastChar..m.Index];
                    long at = lastByte + ByteCount(gap, bytes, lastByte, enc, exactCounts);
                    if (at >= own) break;
                    count++;
                    if (hits.Count >= cap)
                    {
                        lastChar = m.Index;
                        lastByte = at;
                        continue;
                    }
                    int nls = gap.Count('\n');
                    if (nls > 0)
                    {
                        line += nls;
                        lineStart = lastChar + gap.LastIndexOf('\n') + 1;
                    }
                    var matched = text.Slice(m.Index, m.Length);
                    int byteLen = (int)ByteCount(matched, bytes, at, enc, exactCounts);
                    int col = lineStart < 0 ? -1 : m.Index - lineStart;
                    hits.Add(new SearchHit(line, u.Start + at, byteLen, col, m.Length));
                    lastChar = m.Index;
                    lastByte = at;
                    if (linesOnly)
                    {
                        int nl = text[(m.Index + m.Length)..].IndexOf('\n');
                        if (nl < 0) break;
                        skipUntil = m.Index + m.Length + nl + 1;
                    }
                }
                return count;
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new SearchException($"The regular expression took too long on part of the file (over {RegexTimeout.TotalSeconds:F0} s).",
                    "Make the pattern more specific (avoid nested repeats such as (a+)+), or search for plain text.", ex);
            }
            finally
            {
                chars.Return(buf);
            }
        }

        /// <summary>
        /// Bytes taken by <paramref name="chars"/>, which were decoded from <paramref name="bytes"/> starting at
        /// <paramref name="byteStart"/>. Valid text: re-encode count (fast). UTF-8 with invalid bytes: each U+FFFD may
        /// stand for 1-3 bytes, so walk the original bytes instead.
        /// </summary>
        private static long ByteCount(ReadOnlySpan<char> chars, ReadOnlySpan<byte> bytes, long byteStart, TextEncoding enc, bool exact)
        {
            if (exact) return enc.Encoding.GetByteCount(chars);
            int pos = (int)byteStart, produced = 0;
            while (produced < chars.Length && pos < bytes.Length)
            {
                System.Buffers.OperationStatus _ = Rune.DecodeFromUtf8(bytes[pos..], out Rune r, out int used);
                produced += r.Utf16SequenceLength;
                pos += used;
            }
            return pos - byteStart;
        }
    }
}
