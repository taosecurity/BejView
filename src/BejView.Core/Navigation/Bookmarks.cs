namespace BejView.Core.Navigation;

/// <summary>Bookmarked lines (0-based) in one open file. Next and Previous wrap around, like most editors.</summary>
public sealed class Bookmarks
{
    private readonly SortedSet<long> _lines = [];

    public int Count => _lines.Count;
    public IReadOnlyCollection<long> Lines => _lines;
    public bool Contains(long line) => _lines.Contains(line);

    /// <summary>Adds the bookmark, or removes it if it's there. Returns true when the line is now bookmarked.</summary>
    public bool Toggle(long line)
    {
        if (_lines.Remove(line)) return false;
        _lines.Add(line);
        return true;
    }

    public void Clear() => _lines.Clear();

    /// <summary>The first bookmark after <paramref name="line"/>, wrapping to the first one; null when there are none.</summary>
    public long? Next(long line)
    {
        if (_lines.Count == 0) return null;
        var after = _lines.GetViewBetween(line + 1, long.MaxValue);
        return after.Count > 0 ? after.Min : _lines.Min;
    }

    /// <summary>The last bookmark before <paramref name="line"/>, wrapping to the last one; null when there are none.</summary>
    public long? Previous(long line)
    {
        if (_lines.Count == 0) return null;
        var before = _lines.GetViewBetween(long.MinValue, line - 1);
        return before.Count > 0 ? before.Max : _lines.Max;
    }

    /// <summary>Drops bookmarks at or past <paramref name="lineCount"/> (after a log got shorter).</summary>
    public void TrimTo(long lineCount) => _lines.RemoveWhere(l => l >= lineCount);
}
