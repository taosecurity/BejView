namespace BejView.Core.Filtering;

/// <summary>
/// A sorted set of line numbers stored as ranges [start, end), so "every line except a few" or "a block of 10 million
/// matching lines" takes a few bytes. Rows are the positions in the filtered list: row 0 is the first line in the set.
/// Thread-safe: the filter adds lines on a worker thread while the window reads rows.
/// </summary>
public sealed class LineRanges
{
    private readonly object _lock = new();
    private readonly List<long> _starts = [];
    private readonly List<long> _ends = [];
    /// <summary>Rows before range i (prefix sums), for row-to-line lookups.</summary>
    private readonly List<long> _rowsBefore = [];
    private long _rows;

    /// <summary>Lines in the set.</summary>
    public long RowCount { get { lock (_lock) return _rows; } }

    public int RangeCount { get { lock (_lock) return _starts.Count; } }

    /// <summary>The line after the last line in the set (0 when empty). Lines must be added in increasing order.</summary>
    public long End { get { lock (_lock) return _ends.Count == 0 ? 0 : _ends[^1]; } }

    /// <summary>Roughly how much memory the ranges take.</summary>
    public long MemoryBytes { get { lock (_lock) return (long)_starts.Capacity * 24; } }

    /// <summary>Adds one line. Lines at or before the last one already added are ignored (repeats are harmless).</summary>
    public void Add(long line) => AddRange(line, line + 1);

    /// <summary>Adds lines [start, end). The part at or before the last line already added is ignored.</summary>
    public void AddRange(long start, long end)
    {
        lock (_lock)
        {
            long last = _ends.Count == 0 ? long.MinValue : _ends[^1];
            if (start < last) start = last;
            if (end <= start) return;
            if (start == last)
            {
                _ends[^1] = end;      // touches the last range: grow it
            }
            else
            {
                _starts.Add(start);
                _ends.Add(end);
                _rowsBefore.Add(_rows);
            }
            _rows += end - start;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _starts.Clear();
            _ends.Clear();
            _rowsBefore.Clear();
            _rows = 0;
        }
    }

    /// <summary>The line shown at <paramref name="row"/>.</summary>
    public long LineAt(long row)
    {
        lock (_lock)
        {
            if (row < 0 || row >= _rows) throw new ArgumentOutOfRangeException(nameof(row), row, $"The filter holds {_rows:N0} lines.");
            int i = RangeOfRowLocked(row);
            return _starts[i] + (row - _rowsBefore[i]);
        }
    }

    /// <summary>The row of <paramref name="line"/>, or of the first line after it in the set; -1 when no line in the set is at or after it.</summary>
    public long RowAtOrAfter(long line)
    {
        lock (_lock)
        {
            int lo = 0, hi = _ends.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_ends[mid] > line) { found = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            if (found < 0) return -1;
            long within = Math.Max(0, line - _starts[found]);
            return _rowsBefore[found] + within;
        }
    }

    public bool Contains(long line)
    {
        long row = RowAtOrAfter(line);
        return row >= 0 && LineAt(row) == line;
    }

    /// <summary>
    /// The lines for <paramref name="count"/> rows from <paramref name="firstRow"/>, as runs of consecutive lines
    /// (Line, Count), so callers can read each run in one go.
    /// </summary>
    public List<(long Line, int Count)> Runs(long firstRow, int count)
    {
        var runs = new List<(long, int)>();
        lock (_lock)
        {
            if (firstRow < 0 || firstRow >= _rows || count <= 0) return runs;
            long row = firstRow;
            long endRow = Math.Min(_rows, firstRow + count);
            int i = RangeOfRowLocked(row);
            while (row < endRow && i < _starts.Count)
            {
                long line = _starts[i] + (row - _rowsBefore[i]);
                long n = Math.Min(_ends[i] - line, endRow - row);
                runs.Add((line, (int)n));
                row += n;
                i++;
            }
        }
        return runs;
    }

    private int RangeOfRowLocked(long row)
    {
        int lo = 0, hi = _rowsBefore.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_rowsBefore[mid] <= row) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
