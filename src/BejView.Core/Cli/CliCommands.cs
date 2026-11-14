using System.Diagnostics;
using System.Globalization;
using BejView.Core.Documents;
using BejView.Core.IO;
using BejView.Core.Logging;
using BejView.Core.Search;

namespace BejView.Core.Cli;

/// <summary>
/// The console commands <c>info</c> and <c>find</c>. They write results to <c>output</c> and messages to <c>error</c>,
/// and return an <see cref="ExitCodes"/> value, so the exe and the tests run exactly the same code.
/// </summary>
public static class CliCommands
{
    public static async Task<int> InfoAsync(CliOptions o, TextWriter output, TextWriter error, Logger log, CancellationToken ct = default)
    {
        var doc = OpenOrReport(o, error, log, TextDocument.DefaultMaxLineBytes);
        if (doc == null) return ExitCodes.FileError;
        using (doc)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await doc.IndexAsync(ct: ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                return ReadFailed(doc.Path, ex, error, log);
            }
            sw.Stop();
            double secs = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
            var inv = CultureInfo.InvariantCulture;
            output.WriteLine(string.Create(inv, $"File:       {TerminalText.Safe(doc.Path)}"));
            output.WriteLine(string.Create(inv, $"Size:       {doc.Length:N0} bytes") + (doc.Length >= 1024 ? $" ({FormatSize(doc.Length)})" : ""));
            output.WriteLine(string.Create(inv, $"Encoding:   {doc.Encoding.DisplayName} ({doc.Encoding.Reason})"));
            output.WriteLine(string.Create(inv, $"Lines:      {doc.LineCount:N0}"));
            output.WriteLine(doc.LineCount > 0 && doc.Index.LastLineIsOpen
                ? "Last line:  has no line break at the end (the file may still be being written)"
                : "Last line:  ends with a line break");
            output.WriteLine(string.Create(inv, $"Indexed in: {sw.Elapsed.TotalSeconds:F2} s ({doc.Length / secs / 1e6:F0} MB/s)"));
            output.WriteLine(string.Create(inv, $"Index size: {doc.Index.CheckpointCount:N0} checkpoints, {doc.Index.MemoryBytes / 1024.0:N0} KB in memory"));
            await output.FlushAsync(ct).ConfigureAwait(false);
            return ExitCodes.Ok;
        }
    }

    /// <summary>
    /// Prints each line with a match as <c>LINE:COLUMN: text</c> (1-based, like <c>grep -n</c> plus a column), one row per
    /// line, using the line's first match for the column. The column is <c>?</c> inside a line too long to place it.
    /// Lines longer than the display limit (64 KB) are printed up to the limit. With <see cref="CliOptions.CountOnly"/>
    /// it prints the number of matches only. Exit 0 when something matched, 1 when nothing did (like grep).
    /// </summary>
    public static async Task<int> FindAsync(CliOptions o, TextWriter output, TextWriter error, Logger log, CancellationToken ct = default)
    {
        var doc = OpenOrReport(o, error, log, TextDocument.DefaultMaxLineBytes);
        if (doc == null) return ExitCodes.FileError;
        using (doc)
        {
            var query = new SearchQuery(o.Find ?? "", o.CaseSensitive, o.Regex, o.WholeWord);
            int max = o.MaxHits ?? SearchEngine.DefaultMaxHits;
            SearchSummary summary;
            try
            {
                await doc.IndexAsync(ct: ct).ConfigureAwait(false);
                summary = await SearchEngine.RunAsync(doc, query, o.CountOnly ? 0 : max, ct: ct, log: log).ConfigureAwait(false);
            }
            catch (SearchException ex)
            {
                log.Warning($"find: {ex.Message}");
                await error.WriteLineAsync($"BejView: {ex.Message} {ex.Fix}").ConfigureAwait(false);
                return ExitCodes.SearchError;
            }
            catch (IOException ex)
            {
                return ReadFailed(doc.Path, ex, error, log);
            }

            if (o.CountOnly)
            {
                await output.WriteLineAsync(summary.TotalMatches.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
            else
            {
                WriteHits(doc, summary.Hits, output);
                if (summary.LimitReached)
                    await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"BejView: listed the first {summary.Hits.Count:N0} of {summary.TotalMatches:N0} matches. Use --max N to list more, or --count to count them.")).ConfigureAwait(false);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            return summary.TotalMatches > 0 ? ExitCodes.Ok : ExitCodes.NoMatches;
        }
    }

    private static void WriteHits(TextDocument doc, IReadOnlyList<SearchHit> hits, TextWriter output)
    {
        // Hits are in file order; read lines in runs so a block of nearby matches costs one read.
        int i = 0;
        while (i < hits.Count)
        {
            long first = hits[i].Line;
            int j = i;
            while (j + 1 < hits.Count && hits[j + 1].Line - first < 256) j++;
            int span = (int)(hits[j].Line - first + 1);
            var lines = doc.ReadLines(first, span);
            long lastPrinted = -1;
            for (int k = i; k <= j; k++)
            {
                var h = hits[k];
                if (h.Line == lastPrinted) continue;
                lastPrinted = h.Line;
                int idx = (int)(h.Line - first);
                if (idx >= lines.Count) break;
                var line = lines[idx];
                string col = h.Column >= 0 ? (h.Column + 1).ToString(CultureInfo.InvariantCulture) : "?";
                output.Write((h.Line + 1).ToString(CultureInfo.InvariantCulture));
                output.Write(':');
                output.Write(col);
                output.Write(": ");
                output.WriteLine(TerminalText.Safe(line.Text)); // a file's escape sequences can't drive the terminal
            }
            i = j + 1;
        }
    }

    private static TextDocument? OpenOrReport(CliOptions o, TextWriter error, Logger log, int maxLineBytes)
    {
        try
        {
            return TextDocument.Open(o.File!, log, o.Encoding, maxLineBytes);
        }
        catch (FileOpenException ex)
        {
            log.Error($"{ex.Message} {ex.Fix}");
            error.WriteLine($"BejView: {ex.Message} {ex.Fix}");
            return null;
        }
    }

    private static int ReadFailed(string path, IOException ex, TextWriter error, Logger log)
    {
        log.Error($"Reading {path} failed", ex);
        error.WriteLine($"BejView: reading {path} failed ({ex.Message}). Check that the drive is still there and try again.");
        return ExitCodes.FileError;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:F1} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:F2} GB"),
    };
}
