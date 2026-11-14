using BejView.Core.Documents;
using BejView.Core.Filtering;

namespace BejView.App.Views;

/// <summary>One row of a <see cref="TextView"/>: the row number in the view and the file line it shows.</summary>
internal readonly record struct ViewLine(long Row, LineText Line);

/// <summary>What a <see cref="TextView"/> shows: rows that map to lines of one file.</summary>
internal interface ILineSource
{
    TextDocument Document { get; }

    long Count { get; }

    /// <summary>True when row N is file line N (the main view); false for filtered lists.</summary>
    bool IsWholeFile { get; }

    IReadOnlyList<ViewLine> Read(long firstRow, int count);

    /// <summary>The row showing <paramref name="line"/>, or the next row after it; -1 when none.</summary>
    long RowOfLine(long line);
}

/// <summary>Every line of the file.</summary>
internal sealed class DocumentSource(TextDocument doc) : ILineSource
{
    public TextDocument Document { get; } = doc;
    public long Count => Document.LineCount;
    public bool IsWholeFile => true;

    public IReadOnlyList<ViewLine> Read(long firstRow, int count)
    {
        var lines = Document.ReadLines(firstRow, count);
        var result = new ViewLine[lines.Count];
        for (int i = 0; i < lines.Count; i++) result[i] = new ViewLine(firstRow + i, lines[i]);
        return result;
    }

    public long RowOfLine(long line) => line < Count ? Math.Max(0, line) : -1;
}

/// <summary>The filter pane: the lines in a <see cref="LineRanges"/>.</summary>
internal sealed class RangesSource(TextDocument doc, LineRanges ranges) : ILineSource
{
    public TextDocument Document { get; } = doc;
    public LineRanges Ranges { get; } = ranges;
    public long Count => Ranges.RowCount;
    public bool IsWholeFile => false;

    public IReadOnlyList<ViewLine> Read(long firstRow, int count)
    {
        var result = new List<ViewLine>(count);
        long row = firstRow;
        foreach (var (line, n) in Ranges.Runs(firstRow, count))
        {
            foreach (var l in Document.ReadLines(line, n)) result.Add(new ViewLine(row++, l));
        }
        return result;
    }

    public long RowOfLine(long line) => Ranges.RowAtOrAfter(line);
}

/// <summary>The results pane: one row per line that has a search hit. The list is owned by the UI thread.</summary>
internal sealed class HitLinesSource(TextDocument doc, List<long> lines) : ILineSource
{
    public TextDocument Document { get; } = doc;
    public List<long> Lines { get; } = lines;
    public long Count => Lines.Count;
    public bool IsWholeFile => false;

    public IReadOnlyList<ViewLine> Read(long firstRow, int count)
    {
        var result = new List<ViewLine>(count);
        long end = Math.Min(Lines.Count, firstRow + count);
        long row = firstRow;
        while (row < end)
        {
            // Read runs of consecutive lines in one go.
            long start = Lines[(int)row];
            long runEnd = row + 1;
            while (runEnd < end && Lines[(int)runEnd] == Lines[(int)runEnd - 1] + 1) runEnd++;
            foreach (var l in Document.ReadLines(start, (int)(runEnd - row))) result.Add(new ViewLine(row++, l));
            if (row < runEnd) break; // the file got shorter
        }
        return result;
    }

    public long RowOfLine(long line)
    {
        int i = Lines.BinarySearch(line);
        if (i < 0) i = ~i;
        return i < Lines.Count ? i : -1;
    }
}
