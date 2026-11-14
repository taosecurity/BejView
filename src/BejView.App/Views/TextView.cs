using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Utilities;
using BejView.Core.Cli;
using BejView.Core.Highlighting;
using BejView.Core.Navigation;
using BejView.Core.Search;

namespace BejView.App.Views;

/// <summary>A position in a view: a row and a character column in that row's text.</summary>
internal readonly record struct TextPos(long Row, int Col) : IComparable<TextPos>
{
    public int CompareTo(TextPos other) => Row != other.Row ? Row.CompareTo(other.Row) : Col.CompareTo(other.Col);
    public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// A read-only text view over any number of lines. It reads and lays out only the rows on screen (plus a few kept in a
/// small cache), so a 30 GB file costs no more than a short one. Line numbers, horizontal scroll, word wrap, the
/// "line continues" mark for capped lines, highlight rules, search-match highlighting, bookmarks, selection.
/// </summary>
internal sealed class TextView : Control
{
    private const double GutterPad = 10;
    private const double BookmarkLane = 12;

    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical, AllowAutoHide = false, SmallChange = 1 };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false, SmallChange = 20 };
    private readonly Dictionary<long, CachedLine> _cache = [];
    private readonly List<(CachedLine Line, double Y)> _visible = [];
    private bool _dirty = true;
    private bool _syncing;
    private bool _syncPosted;

    private ILineSource? _source;
    private long _knownCount;
    private long _top;
    private int _topSub;          // word wrap: wrapped rows of the top line scrolled out of sight
    private double _hOffset;
    private double _maxWidth;
    private TextPos _caret;
    private TextPos _anchor;
    private bool _dragging;
    private bool _dragMoved;

    private Typeface _typeface = new(new FontFamily("Cascadia Mono, Consolas, monospace"));
    private double _fontSize = 13.33;
    private double _lineHeight = 16;
    private double _charWidth = 8;
    private bool _wrap;
    private bool _lineNumbers = true;
    private Highlighter _highlighter = Highlighter.Empty;
    private Palette _palette = Palette.Light;
    private string? _emptyText;

    public TextView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        VisualChildren.Add(_vbar);
        LogicalChildren.Add(_vbar);
        VisualChildren.Add(_hbar);
        LogicalChildren.Add(_hbar);
        _vbar.Cursor = new Cursor(StandardCursorType.Arrow);
        _hbar.Cursor = new Cursor(StandardCursorType.Arrow);
        _vbar.ValueChanged += (_, e) => { if (!_syncing) ScrollTo((long)Math.Round(e.NewValue), user: true); };
        _hbar.ValueChanged += (_, e) => { if (!_syncing) SetHorizontalOffset(e.NewValue); };
        MeasureFont();
    }

    // ---------------- events ----------------

    /// <summary>The user scrolled (wheel, scroll bar, keys or dragging); follow mode uses it to pause or resume.</summary>
    public event Action? UserScrolled;
    public event Action? CaretMoved;
    /// <summary>A click (not a drag) on a row: the results and filter panes jump the main view there.</summary>
    public event Action<ViewLine>? RowClicked;
    /// <summary>Ctrl+wheel: +1 bigger, -1 smaller.</summary>
    public event Action<int>? ZoomRequested;

    // ---------------- what to show ----------------

    public ILineSource? Source
    {
        get => _source;
        set
        {
            _source = value;
            _knownCount = value?.Count ?? 0;
            _top = 0;
            _topSub = 0;
            _hOffset = 0;
            _maxWidth = 0;
            _caret = _anchor = default;
            ClearCache();
        }
    }

    /// <summary>Search hits to highlight, sorted by line (the window's list; read on the UI thread only).</summary>
    public IReadOnlyList<SearchHit>? Hits { get; set; }

    /// <summary>Index in <see cref="Hits"/> of the current match (drawn in a stronger colour), or -1.</summary>
    public int CurrentHit { get; set; } = -1;

    public Bookmarks? Bookmarks { get; set; }

    public Highlighter Highlighter
    {
        get => _highlighter;
        set { _highlighter = value; ClearCache(); }
    }

    /// <summary>Text shown when there are no rows (for example "No matching lines").</summary>
    public string? EmptyText
    {
        get => _emptyText;
        set { _emptyText = value; InvalidateVisual(); }
    }

    public bool WordWrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value) return;
            _wrap = value;
            _hOffset = 0;
            _topSub = 0;
            ClearCache();
            InvalidateArrange();
        }
    }

    public bool ShowLineNumbers
    {
        get => _lineNumbers;
        set { _lineNumbers = value; ClearCache(); }
    }

    /// <summary>Sets the font from a fallback list ("Cascadia Mono, Consolas, monospace") and a size in points.</summary>
    public void SetFont(string families, double points)
    {
        _typeface = new Typeface(new FontFamily(families));
        _fontSize = Math.Round(points * 96.0 / 72.0, 2);
        MeasureFont();
        ClearCache();
    }

    public double LineHeight => _lineHeight;

    private void MeasureFont()
    {
        using var probe = MakeLayout("0", null, null, double.PositiveInfinity);
        _lineHeight = Math.Ceiling(probe.Height);
        _charWidth = probe.WidthIncludingTrailingWhitespace;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            _palette = ActualThemeVariant == ThemeVariant.Dark ? Palette.Dark : Palette.Light;
            ClearCache();
        }
        else if (change.Property == BoundsProperty)
        {
            var (oldB, newB) = change.GetOldAndNewValue<Rect>();
            if (_wrap && Math.Abs(oldB.Width - newB.Width) > 0.5) ClearCache();
            else Dirty();
        }
    }

    /// <summary>
    /// Call after the source's lines changed. <paramref name="reset"/>: everything may have changed (a rewritten file);
    /// otherwise lines were only added (the last line may have grown).
    /// </summary>
    public void Refresh(bool reset = false)
    {
        long count = _source?.Count ?? 0;
        if (reset)
        {
            ClearCache();
            _top = Math.Min(_top, Math.Max(0, count - 1));
            _maxWidth = 0;
        }
        else
        {
            long from = Math.Max(0, _knownCount - 1);
            foreach (long row in _cache.Keys.Where(r => r >= from).ToList()) Evict(row);
        }
        _knownCount = count;
        if (_caret.Row >= count) _caret = _anchor = new TextPos(Math.Max(0, count - 1), 0);
        Dirty();
    }

    // ---------------- scrolling ----------------

    public long TopRow => _top;
    public double HorizontalOffset => _hOffset;
    public long Count => _source?.Count ?? 0;

    private Rect TextArea
    {
        get
        {
            double w = Bounds.Width - VBarWidth - GutterWidth;
            double h = Bounds.Height - HBarHeight;
            return new Rect(GutterWidth, 0, Math.Max(0, w), Math.Max(0, h));
        }
    }

    private double VBarWidth => _vbar.IsVisible ? _vbar.DesiredSize.Width : 0;
    private double HBarHeight => _hbar.IsVisible ? _hbar.DesiredSize.Height : 0;

    private double GutterWidth
    {
        get
        {
            if (!_lineNumbers) return BookmarkLane;
            long maxLine = Math.Max(_source?.Document.LineCount ?? 1, 1);
            int digits = Math.Max(4, (int)Math.Floor(Math.Log10(maxLine)) + 1);
            // Line numbers are drawn with "N0" (thousands commas), so leave room for them too:
            // 1,039,994,364 is 10 digits + 3 commas. Without this, large files lost leading digits.
            int chars = digits + (digits - 1) / 3;
            return BookmarkLane + chars * _charWidth + GutterPad;
        }
    }

    /// <summary>Rows that fit on screen in full (without word wrap; with it, an estimate).</summary>
    public int FullRows => Math.Max(1, (int)Math.Floor(TextArea.Height / _lineHeight));

    private long MaxTop
    {
        get
        {
            long count = Count;
            if (count == 0) return 0;
            if (!_wrap) return Math.Max(0, count - FullRows);
            // Word wrap: the first row from which the rest of the file fills the screen.
            double h = 0;
            long row = count - 1;
            while (row >= 0)
            {
                h += Line(row)?.Height ?? _lineHeight;
                if (h > TextArea.Height) return Math.Min(count - 1, row + 1);
                row--;
            }
            return 0;
        }
    }

    /// <summary>True when the last row is on screen (follow mode keeps scrolling while this holds).</summary>
    public bool IsAtEnd => Count == 0 || _top >= MaxTop;

    public void ScrollTo(long row, bool user = false)
    {
        long t = Math.Clamp(row, 0, MaxTop);
        if (t != _top || _topSub != 0)
        {
            _top = t;
            _topSub = 0;
            Dirty();
        }
        if (user) UserScrolled?.Invoke();
    }

    /// <summary>Scrolls by screen rows (wrapped rows count one by one with word wrap on).</summary>
    public void ScrollBy(int rows, bool user = false)
    {
        if (!_wrap || rows == 0)
        {
            ScrollTo(_top + rows, user);
            return;
        }
        long maxTop = MaxTop;
        for (int i = 0; i < Math.Abs(rows); i++)
        {
            if (rows > 0)
            {
                if (_top >= maxTop) { _topSub = 0; _top = maxTop; break; }
                int lineRows = Line(_top)?.Rows ?? 1;
                if (_topSub + 1 < lineRows) _topSub++;
                else { _top++; _topSub = 0; }
            }
            else
            {
                if (_topSub > 0) _topSub--;
                else if (_top > 0) { _top--; _topSub = Math.Max(0, (Line(_top)?.Rows ?? 1) - 1); }
            }
        }
        Dirty();
        if (user) UserScrolled?.Invoke();
    }

    public void ScrollToEnd(bool user = false) => ScrollTo(long.MaxValue, user);

    /// <summary>Puts <paramref name="row"/> on screen, about a third of the way down when it has to move.</summary>
    public void EnsureVisible(long row, bool user = false)
    {
        int rows = FullRows;
        if (row >= _top && row < _top + rows - (_wrap ? Math.Min(rows - 1, 3) : 0)) { if (user) UserScrolled?.Invoke(); return; }
        ScrollTo(row - rows / 3, user);
    }

    private void SetHorizontalOffset(double x)
    {
        double max = Math.Max(0, _maxWidth + _charWidth * 4 - TextArea.Width);
        x = _wrap ? 0 : Math.Clamp(x, 0, max);
        if (Math.Abs(x - _hOffset) < 0.01) return;
        _hOffset = x;
        Dirty();
    }

    private void EnsureColumnVisible(TextPos p)
    {
        if (_wrap) return;
        var c = Line(p.Row);
        if (c == null) return;
        double x = c.Layout.HitTestTextPosition(Math.Min(p.Col, c.TextLength)).X;
        double w = TextArea.Width;
        if (x < _hOffset + _charWidth) SetHorizontalOffset(x - w / 4);
        else if (x > _hOffset + w - _charWidth * 2) SetHorizontalOffset(x - w * 3 / 4);
    }

    // ---------------- caret and selection ----------------

    public TextPos Caret => _caret;
    public TextPos Anchor => _anchor;
    public bool HasSelection => _caret != _anchor;

    /// <summary>The selection with start before end.</summary>
    public (TextPos Start, TextPos End) Selection => _anchor <= _caret ? (_anchor, _caret) : (_caret, _anchor);

    public void SetCaret(TextPos p, bool extend = false, bool scroll = true)
    {
        long count = Count;
        if (count == 0) { _caret = _anchor = default; Dirty(); return; }
        p = new TextPos(Math.Clamp(p.Row, 0, count - 1), Math.Max(0, p.Col));
        _caret = p;
        if (!extend) _anchor = p;
        if (scroll)
        {
            EnsureVisible(p.Row);
            EnsureColumnVisible(p);
        }
        Dirty();
        CaretMoved?.Invoke();
    }

    /// <summary>Selects characters [<paramref name="startCol"/>, <paramref name="endCol"/>) on one row and shows them.</summary>
    public void SelectRange(long row, int startCol, int endCol)
    {
        SetCaret(new TextPos(row, endCol), scroll: false);
        _anchor = new TextPos(row, startCol);
        EnsureVisible(row);
        EnsureColumnVisible(new TextPos(row, startCol));
        EnsureColumnVisible(new TextPos(row, endCol));
        Dirty();
    }

    public void SelectAll()
    {
        long count = Count;
        if (count == 0) return;
        var last = _source!.Read(count - 1, 1);
        _anchor = new TextPos(0, 0);
        _caret = new TextPos(count - 1, last.Count > 0 ? last[0].Line.Text.Length : 0);
        Dirty();
        CaretMoved?.Invoke();
    }

    /// <summary>The rows the selection (or the caret row) covers, first to last.</summary>
    public (long First, long Last) SelectedRows
    {
        get
        {
            var (s, e) = Selection;
            long last = e.Row > s.Row && e.Col == 0 ? e.Row - 1 : e.Row; // a selection ending at a line start doesn't take that line
            return (s.Row, Math.Max(s.Row, last));
        }
    }

    // ---------------- line cache ----------------

    private sealed class CachedLine(ViewLine line, TextLayout layout, string display, int textLength, IBrush? brush) : IDisposable
    {
        public ViewLine View { get; } = line;
        public TextLayout Layout { get; } = layout;
        /// <summary>The text drawn: the line (control characters made visible) plus the "continues" mark.</summary>
        public string Display { get; } = display;
        /// <summary>Characters of real text (the "continues" mark comes after them).</summary>
        public int TextLength { get; } = textLength;
        public IBrush? Brush { get; } = brush;
        public double Height => Math.Max(Layout.Height, 1);
        public int Rows => Math.Max(1, Layout.TextLines.Count);
        public void Dispose() => Layout.Dispose();
    }

    private void Dirty()
    {
        _dirty = true;
        InvalidateVisual();
    }

    private void ClearCache()
    {
        foreach (var c in _cache.Values) c.Dispose();
        _cache.Clear();
        Dirty();
    }

    private void Evict(long row)
    {
        if (_cache.Remove(row, out var c)) c.Dispose();
    }

    private CachedLine? Line(long row)
    {
        if (_source == null || row < 0 || row >= _source.Count) return null;
        if (_cache.TryGetValue(row, out var c)) return c;
        IReadOnlyList<ViewLine> lines;
        try { lines = _source.Read(row, 1); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentOutOfRangeException) { return null; }
        if (lines.Count == 0) return null;
        return Build(lines[0]);
    }

    private CachedLine Build(ViewLine v)
    {
        if (_cache.TryGetValue(v.Row, out var existing)) return existing;
        string text = Sanitize(v.Line.Text);
        string display = text;
        List<ValueSpan<TextRunProperties>>? overrides = null;
        if (v.Line.Truncated)
        {
            string mark = $"  ⟶ line continues ({CliCommands.FormatSize(v.Line.ByteLength)} in all; File > Save selection keeps it whole)";
            display = text + mark;
            overrides = [new ValueSpan<TextRunProperties>(text.Length, mark.Length,
                new GenericTextRunProperties(_typeface, _fontSize, foregroundBrush: _palette.Marker))];
        }
        IBrush? brush = null;
        if (_highlighter.Count > 0 && _highlighter.Match(v.Line.Text) is { } color) brush = _palette.Highlights[color];
        double maxWidth = _wrap ? Math.Max(_charWidth * 8, TextArea.Width - _charWidth) : double.PositiveInfinity;
        var c = new CachedLine(v, MakeLayout(display, brush, overrides, maxWidth), display, text.Length, brush);
        _cache[v.Row] = c;
        if (!_wrap) _maxWidth = Math.Max(_maxWidth, c.Layout.WidthIncludingTrailingWhitespace);
        return c;
    }

    private TextLayout MakeLayout(string text, IBrush? brush, IReadOnlyList<ValueSpan<TextRunProperties>>? overrides, double maxWidth) =>
        new(text, _typeface, _fontSize, brush ?? _palette.Text, TextAlignment.Left,
            _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming.None, null, FlowDirection.LeftToRight,
            maxWidth, double.PositiveInfinity, 0, 0, 0, null, overrides);

    /// <summary>Control characters would draw as nothing or as boxes; show them as visible symbols (one char each, so columns don't move). Tabs stay tabs.</summary>
    internal static string Sanitize(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] >= ' ' || s[i] == '\t') && s[i] != '\u007F') i++;
        if (i == s.Length) return s;
        var sb = new StringBuilder(s);
        for (; i < sb.Length; i++)
        {
            char ch = sb[i];
            if (ch < ' ' && ch != '\t') sb[i] = (char)(0x2400 + ch);
            else if (ch == '\u007F') sb[i] = '␡';
        }
        return sb.ToString();
    }

    /// <summary>
    /// Works out which rows are on screen, reading and laying out the ones not cached yet. While drawing
    /// (<paramref name="inRender"/>), the scroll bars can't be changed, so their update waits until just after the frame.
    /// </summary>
    private void LayoutRows(bool inRender = false)
    {
        if (!_dirty) return;
        _dirty = false;
        _visible.Clear();
        long count = Count;
        _top = Math.Clamp(_top, 0, Math.Max(0, count - 1));
        var area = TextArea;
        if (_source != null && count > 0 && area.Height > 0)
        {
            int want = (int)Math.Ceiling(area.Height / _lineHeight) + 1;
            double y = -_topSub * _lineHeight;
            long row = _top;
            bool stop = false;
            while (!stop && y < area.Height && row < count)
            {
                int n = (int)Math.Min(want, count - row);
                IReadOnlyList<ViewLine> batch = [];
                if (!AllCached(row, n))
                {
                    try { batch = _source.Read(row, n); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentOutOfRangeException)
                    {
                        break; // the file went away or shrank; the next refresh sorts it out
                    }
                }
                for (int k = 0; k < n && y < area.Height; k++, row++)
                {
                    if (!_cache.TryGetValue(row, out var c))
                    {
                        int i = batch.Count == 0 ? -1 : (int)(row - batch[0].Row);
                        if (i < 0 || i >= batch.Count) { stop = true; break; }
                        c = Build(batch[i]);
                    }
                    _visible.Add((c, y));
                    y += c.Height;
                }
            }
        }

        // Keep the cache to what's on screen plus a little.
        var keep = new HashSet<long>(_visible.Select(v => v.Line.View.Row));
        foreach (long r in _cache.Keys.Where(r => !keep.Contains(r) && Math.Abs(r - _top) > 200).ToList()) Evict(r);
        if (_cache.Count > 2000) foreach (long r in _cache.Keys.Where(r => !keep.Contains(r)).ToList()) Evict(r);

        if (!inRender) SyncScrollBars();
        else if (!_syncPosted)
        {
            _syncPosted = true;
            Dispatcher.UIThread.Post(() => { _syncPosted = false; SyncScrollBars(); }, DispatcherPriority.Background);
        }
    }

    private bool AllCached(long row, int n)
    {
        for (long r = row; r < row + n; r++) if (!_cache.ContainsKey(r)) return false;
        return true;
    }

    private void SyncScrollBars()
    {
        _syncing = true;
        try
        {
            long count = Count;
            _vbar.Maximum = Math.Max(0, MaxTop);
            _vbar.ViewportSize = FullRows;
            _vbar.LargeChange = Math.Max(1, FullRows - 1);
            _vbar.Value = Math.Min(_top, _vbar.Maximum);
            _vbar.IsEnabled = count > FullRows;
            double hmax = Math.Max(0, _maxWidth + _charWidth * 4 - TextArea.Width);
            bool showH = !_wrap && hmax > 0;
            if (_hbar.IsVisible != showH)
            {
                _hbar.IsVisible = showH;
                InvalidateArrange();
            }
            _hbar.Maximum = hmax;
            _hbar.ViewportSize = TextArea.Width;
            _hbar.LargeChange = Math.Max(_charWidth, TextArea.Width * 0.8);
            _hbar.Value = Math.Min(_hOffset, hmax);
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- layout and drawing ----------------

    protected override Size MeasureOverride(Size availableSize)
    {
        _vbar.Measure(availableSize);
        _hbar.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double vw = _vbar.DesiredSize.Width;
        double hh = _hbar.IsVisible ? _hbar.DesiredSize.Height : 0;
        _vbar.Arrange(new Rect(finalSize.Width - vw, 0, vw, Math.Max(0, finalSize.Height - hh)));
        _hbar.Arrange(new Rect(0, finalSize.Height - hh, Math.Max(0, finalSize.Width - vw), hh));
        Dirty();
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        var p = _palette;
        ctx.FillRectangle(p.Background, new Rect(Bounds.Size));
        LayoutRows(inRender: true);
        var area = TextArea;
        double gutter = GutterWidth;
        ctx.FillRectangle(p.GutterBackground, new Rect(0, 0, gutter, area.Height));

        if (_visible.Count == 0 && !string.IsNullOrEmpty(_emptyText))
        {
            using var t = MakeLayout(_emptyText, p.GutterText, null, double.PositiveInfinity);
            t.Draw(ctx, new Point(gutter + _charWidth, _lineHeight * 0.5));
        }

        var (selStart, selEnd) = Selection;
        bool hasSel = HasSelection;
        using (ctx.PushClip(new Rect(0, 0, area.Right, area.Height)))
        {
            foreach (var (c, y) in _visible)
            {
                long row = c.View.Row;
                long fileLine = c.View.Line.Number;
                if (row == _caret.Row && !hasSel)
                    ctx.FillRectangle(p.CurrentLine, new Rect(gutter, y, area.Width, c.Height));

                if (_lineNumbers)
                {
                    using var num = MakeLayout((fileLine + 1).ToString("N0", CultureInfo.InvariantCulture), p.GutterText, null, double.PositiveInfinity);
                    num.Draw(ctx, new Point(gutter - GutterPad / 2 - num.WidthIncludingTrailingWhitespace, y));
                }
                if (Bookmarks?.Contains(fileLine) == true)
                {
                    double d = Math.Min(BookmarkLane - 4, _lineHeight - 4);
                    ctx.DrawRectangle(p.Bookmark, null, new Rect(3, y + (_lineHeight - d) / 2, d, d), d / 4, d / 4);
                }

                using (ctx.PushClip(new Rect(area.X, y, area.Width, c.Height)))
                using (ctx.PushTransform(Matrix.CreateTranslation(area.X - _hOffset, y)))
                {
                    if (hasSel && row >= selStart.Row && row <= selEnd.Row)
                    {
                        int from = row == selStart.Row ? Math.Min(selStart.Col, c.TextLength) : 0;
                        int to = row == selEnd.Row ? Math.Min(selEnd.Col, c.TextLength) : c.TextLength;
                        foreach (var r in c.Layout.HitTestTextRange(from, Math.Max(0, to - from))) ctx.FillRectangle(p.Selection, r);
                        if (row != selEnd.Row)
                        {
                            var end = c.Layout.HitTestTextPosition(c.TextLength);
                            ctx.FillRectangle(p.Selection, new Rect(end.X, end.Y, _charWidth / 2, end.Height));
                        }
                    }
                    foreach (var (col, len, current) in HitsOn(fileLine, c.TextLength))
                        foreach (var r in c.Layout.HitTestTextRange(col, len))
                            ctx.FillRectangle(current ? p.CurrentMatch : p.Match, r);

                    c.Layout.Draw(ctx, new Point(0, 0));

                    if (row == _caret.Row && IsFocused)
                    {
                        var cr = c.Layout.HitTestTextPosition(Math.Min(_caret.Col, c.TextLength));
                        ctx.FillRectangle(p.Caret, new Rect(cr.X, cr.Y, 1.5, Math.Max(cr.Height, _lineHeight)));
                    }
                }
            }
        }
    }

    /// <summary>The search hits on one file line, as (column, length, is current), clipped to the shown text.</summary>
    private IEnumerable<(int Col, int Len, bool Current)> HitsOn(long fileLine, int textLength)
    {
        var hits = Hits;
        if (hits == null || hits.Count == 0) yield break;
        int lo = 0, hi = hits.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (hits[mid].Line < fileLine) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < hits.Count && hits[i].Line == fileLine; i++)
        {
            var h = hits[i];
            if (h.Column < 0 || h.Column >= textLength) continue;
            yield return (h.Column, Math.Min(h.CharLength, textLength - h.Column), i == CurrentHit);
        }
    }

    // ---------------- hit testing ----------------

    /// <summary>The text position under a point in this control.</summary>
    internal TextPos PositionAt(Point pt)
    {
        LayoutRows();
        if (_visible.Count == 0) return default;
        var area = TextArea;
        foreach (var (c, y) in _visible)
        {
            if (pt.Y < y + c.Height || ReferenceEquals(c, _visible[^1].Line))
            {
                if (pt.Y < y && ReferenceEquals(c, _visible[0].Line)) return new TextPos(c.View.Row, 0);
                var local = new Point(Math.Max(0, pt.X - area.X + _hOffset), Math.Clamp(pt.Y - y, 0, c.Height - 1));
                var hit = c.Layout.HitTestPoint(local);
                return new TextPos(c.View.Row, Math.Clamp(hit.TextPosition, 0, c.TextLength));
            }
        }
        return new TextPos(_visible[^1].Line.View.Row, _visible[^1].Line.TextLength);
    }

    // ---------------- input ----------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;
        Focus();
        var pt = e.GetPosition(this);
        if (pt.X > TextArea.Right || pt.Y > TextArea.Bottom) return; // the scroll bars
        var pos = PositionAt(pt);
        if (e.ClickCount == 2)
        {
            SelectWordAt(pos);
            e.Handled = true;
            return;
        }
        bool extend = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        SetCaret(pos, extend, scroll: false);
        _dragging = true;
        _dragMoved = extend;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var pt = e.GetPosition(this);
        var area = TextArea;
        if (pt.Y < 0) ScrollBy(-1, user: true);
        else if (pt.Y > area.Height) ScrollBy(1, user: true);
        var pos = PositionAt(pt);
        if (pos != _caret)
        {
            _dragMoved = true;
            _caret = pos;
            EnsureColumnVisible(pos);
            Dirty();
            CaretMoved?.Invoke();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (!_dragMoved && _visible.Count > 0)
        {
            var c = _visible.FirstOrDefault(v => v.Line.View.Row == _caret.Row).Line;
            if (c != null) RowClicked?.Invoke(c.View);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            ZoomRequested?.Invoke(e.Delta.Y > 0 ? 1 : -1);
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            double d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            SetHorizontalOffset(_hOffset - d * _charWidth * 8);
        }
        else
        {
            ScrollBy(-(int)Math.Round(e.Delta.Y * 3), user: true);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _source == null) return;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        int page = Math.Max(1, FullRows - 1);
        TextPos c = _caret;
        switch (e.Key)
        {
            case Key.Up: Move(new TextPos(c.Row - 1, c.Col)); break;
            case Key.Down: Move(new TextPos(c.Row + 1, c.Col)); break;
            case Key.PageUp:
                ScrollBy(-page, user: true);
                Move(new TextPos(c.Row - page, c.Col));
                break;
            case Key.PageDown:
                ScrollBy(page, user: true);
                Move(new TextPos(c.Row + page, c.Col));
                break;
            case Key.Home when ctrl: Move(new TextPos(0, 0)); break;
            case Key.End when ctrl: Move(new TextPos(Count - 1, 0)); ScrollToEnd(user: true); break;
            case Key.Home: Move(new TextPos(c.Row, 0)); break;
            case Key.End: Move(new TextPos(c.Row, LengthOf(c.Row))); break;
            case Key.Left: Move(new TextPos(c.Row, Math.Max(0, Math.Min(c.Col, LengthOf(c.Row)) - 1))); break;
            case Key.Right: Move(new TextPos(c.Row, Math.Min(LengthOf(c.Row), c.Col + 1))); break;
            default: return;
        }
        e.Handled = true;

        void Move(TextPos p)
        {
            SetCaret(p, shift);
            UserScrolled?.Invoke();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private int LengthOf(long row) => Line(row)?.TextLength ?? 0;

    private void SelectWordAt(TextPos pos)
    {
        var c = Line(pos.Row);
        if (c == null) return;
        string t = c.View.Line.Text;
        int i = Math.Min(pos.Col, t.Length);
        static bool W(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        int s = i, e = i;
        if (i < t.Length && W(t[i]) || i > 0 && W(t[i - 1]))
        {
            while (s > 0 && W(t[s - 1])) s--;
            while (e < t.Length && W(t[e])) e++;
        }
        else if (i < t.Length) e = i + 1;
        _anchor = new TextPos(pos.Row, s);
        _caret = new TextPos(pos.Row, e);
        Dirty();
        CaretMoved?.Invoke();
    }

    // ---------------- reading the selection ----------------

    /// <summary>
    /// The selected text with line breaks, or null when it's longer than <paramref name="maxChars"/>.
    /// <paramref name="cutLines"/> is true when a selected line is longer than the part shown (only the shown part is copied).
    /// </summary>
    public string? SelectedText(int maxChars, out bool cutLines)
    {
        cutLines = false;
        if (_source == null || !HasSelection) return null;
        var (s, e) = Selection;
        var sb = new StringBuilder();
        for (long row = s.Row; row <= e.Row;)
        {
            int n = (int)Math.Min(1000, e.Row - row + 1);
            var lines = _source.Read(row, n);
            if (lines.Count == 0) break;
            foreach (var v in lines)
            {
                string t = v.Line.Text;
                int from = v.Row == s.Row ? Math.Min(s.Col, t.Length) : 0;
                int to = v.Row == e.Row ? Math.Min(e.Col, t.Length) : t.Length;
                if (v.Line.Truncated && to == t.Length) cutLines = true;
                sb.Append(t, from, Math.Max(0, to - from));
                if (v.Row != e.Row) sb.Append(Environment.NewLine);
                if (sb.Length > maxChars) return null;
            }
            row += lines.Count;
        }
        return sb.ToString();
    }

    /// <summary>The selected rows (or the caret row) as "LINE: text" with 1-based file line numbers; null past <paramref name="maxRows"/>.</summary>
    public string? SelectedLinesWithNumbers(int maxRows)
    {
        if (_source == null || Count == 0) return null;
        var (first, last) = SelectedRows;
        if (last - first + 1 > maxRows) return null;
        var sb = new StringBuilder();
        for (long row = first; row <= last;)
        {
            var lines = _source.Read(row, (int)Math.Min(1000, last - row + 1));
            if (lines.Count == 0) break;
            foreach (var v in lines)
                sb.Append((v.Line.Number + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(v.Line.Text).Append(Environment.NewLine);
            row += lines.Count;
        }
        return sb.ToString();
    }

    // ---------------- for tests ----------------

    internal IReadOnlyList<(long Row, double Y, double Height)> VisibleRows
    {
        get
        {
            LayoutRows();
            return _visible.Select(v => (v.Line.View.Row, v.Y, v.Line.Height)).ToList();
        }
    }

    /// <summary>Where the search matches on a visible row are drawn, in this control's coordinates.</summary>
    internal List<Rect> MatchRects(long row)
    {
        LayoutRows();
        var area = TextArea;
        var result = new List<Rect>();
        foreach (var (c, y) in _visible)
        {
            if (c.View.Row != row) continue;
            foreach (var (col, len, _) in HitsOn(c.View.Line.Number, c.TextLength))
                foreach (var r in c.Layout.HitTestTextRange(col, len))
                    result.Add(r.Translate(new Vector(area.X - _hOffset, y)));
        }
        return result;
    }

    /// <summary>The text drawn for a visible row (including the "line continues" mark) and its colour (null = normal text).</summary>
    internal (string Text, IBrush? Brush)? RowDisplay(long row)
    {
        LayoutRows();
        foreach (var (c, _) in _visible)
            if (c.View.Row == row) return (c.Display, c.Brush);
        return null;
    }

    internal Palette CurrentPalette => _palette;
    internal Rect TextAreaForTests => TextArea;
}
