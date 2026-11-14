using System.Diagnostics;
using BejView.Core.Documents;
using BejView.Core.Logging;
using BejView.Core.Search;

namespace BejView.Core.Filtering;

public sealed record LineFilterResult
{
    public required SearchQuery Query { get; init; }
    public required bool Inverted { get; init; }
    /// <summary>Lines in the filter.</summary>
    public required long Lines { get; init; }
    /// <summary>Lines in the file when the filter started (the filter covers these).</summary>
    public required long SearchedLines { get; init; }
    /// <summary>True when the filter stopped early because the result broke into more than the allowed number of separate blocks.</summary>
    public required bool StoppedAtLimit { get; init; }
    /// <summary>When <see cref="StoppedAtLimit"/>: the filter is complete up to (not including) this line.</summary>
    public required long CompleteUpTo { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Builds the filter pane's list: the lines that match a search, or the lines that don't (like grep and grep -v).
/// The lines go into a <see cref="LineRanges"/> as they're found, so the pane fills while the filter runs.
/// </summary>
public static class LineFilter
{
    /// <summary>
    /// Most separate blocks of lines kept (each takes 24 bytes): 1,000,000 blocks is about 24 MB. A filter only
    /// reaches this when matching and non-matching lines alternate a million times.
    /// </summary>
    public const int DefaultMaxRanges = 1_000_000;

    public static Task<LineFilterResult> RunAsync(TextDocument doc, SearchQuery query, bool invert, LineRanges target,
        int maxRanges = DefaultMaxRanges, IProgress<double>? progress = null, CancellationToken ct = default, Logger? log = null) =>
        Task.Run(() => Run(doc, query, invert, target, maxRanges, progress, ct, log), ct);

    /// <summary>Throws <see cref="SearchException"/> for an unusable query and <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static LineFilterResult Run(TextDocument doc, SearchQuery query, bool invert, LineRanges target,
        int maxRanges = DefaultMaxRanges, IProgress<double>? progress = null, CancellationToken ct = default, Logger? log = null)
    {
        log ??= Logger.Null;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRanges, 1);
        var sw = Stopwatch.StartNew();
        target.Clear();
        long lines = doc.LineCount;
        long next = 0;              // invert: the first line not yet known to match or not
        bool stopped = false;
        long completeUpTo = lines;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);

        void OnHits(IReadOnlyList<SearchHit> hits)
        {
            if (stopped) return; // results already in flight when the limit was reached
            foreach (var h in hits)
            {
                if (h.Line >= lines) break;
                if (invert)
                {
                    if (h.Line < next) continue;
                    target.AddRange(next, h.Line);
                    next = h.Line + 1;
                }
                else
                {
                    target.Add(h.Line);
                }
                if (target.RangeCount > maxRanges)
                {
                    stopped = true;
                    completeUpTo = h.Line;
                    limit.Cancel();
                    return;
                }
            }
        }

        try
        {
            SearchEngine.MatchingLines(doc, query, OnHits, progress, limit.Token, log: Logger.Null);
            if (invert) target.AddRange(next, lines);
        }
        catch (OperationCanceledException) when (stopped && !ct.IsCancellationRequested)
        {
            // The block limit was reached: keep what was found so far.
        }

        var result = new LineFilterResult
        {
            Query = query, Inverted = invert, Lines = target.RowCount, SearchedLines = lines,
            StoppedAtLimit = stopped, CompleteUpTo = stopped ? completeUpTo : lines, Elapsed = sw.Elapsed,
        };
        log.Info($"Filter {(invert ? "not matching" : "matching")} {query} in {doc.Path}: {result.Lines:N0} of {lines:N0} lines, " +
                 $"{target.RangeCount:N0} blocks ({target.MemoryBytes / 1024:N0} KB), {sw.Elapsed.TotalSeconds:F2} s" +
                 (stopped ? $"; stopped at line {completeUpTo + 1:N0} (more than {maxRanges:N0} blocks)." : "."));
        return result;
    }
}
