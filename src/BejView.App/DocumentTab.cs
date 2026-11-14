using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BejView.App.Views;
using BejView.Core.Cli;
using BejView.Core.Documents;
using BejView.Core.Filtering;
using BejView.Core.Highlighting;
using BejView.Core.IO;
using BejView.Core.Navigation;
using BejView.Core.Search;
using BejView.Core.Text;

namespace BejView.App;

/// <summary>Calls the handler on the reporting thread (Progress&lt;T&gt; would post to the UI and pile up).</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>
/// One open file: the main view, the search bar, the Results and Filter panes, follow mode, bookmarks and the
/// background work (indexing, search, filter). The window calls <see cref="Tick"/> ten times a second; background
/// work only sets fields, and Tick moves them onto the screen.
/// </summary>
internal sealed class DocumentTab : UserControl
{
    /// <summary>Most characters copied to the clipboard at once; bigger selections go through Save selection.</summary>
    public const int MaxCopyChars = 50_000_000;
    public const int MaxCopyRows = 1_000_000;
    private const int FollowEveryTicks = 5;          // follow mode checks the file every 0.5 s

    private readonly AppSession _s;
    private readonly ConcurrentQueue<IReadOnlyList<SearchHit>> _incoming = new();
    private readonly List<SearchHit> _hits = [];
    private readonly List<long> _hitLines = [];
    private readonly Grid _grid;
    private readonly TabControl _bottomTabs;
    private readonly TabItem _resultsTab;
    private readonly TabItem _filterTab;
    private readonly GridSplitter _splitter;
    private readonly TextBlock _filterStatus;
    private readonly TextBlock _searchStatus;
    private readonly ProgressBar _searchProgressBar;
    private readonly Button _cancelButton;

    private CancellationTokenSource? _indexCts;
    private Task _indexTask = Task.CompletedTask;
    private double _indexProgress;
    private readonly Stopwatch _indexClock = new();

    private CancellationTokenSource? _searchCts;
    private Task _searchTask = Task.CompletedTask;
    private double _searchProgress;
    private int _currentHit = -1;
    private SearchQuery? _lastQuery;

    private CancellationTokenSource? _filterCts;
    private Task _filterTask = Task.CompletedTask;
    private double _filterProgress;
    private LineRanges _filterRanges = new();

    private int _followTicks;
    private bool _refreshing;
    private GoToTarget? _pendingGoTo;
    private long _saveProgress = -1;
    private bool _closed;

    public TextDocument Doc { get; private set; }
    public TextView Main { get; } = new();
    public TextView Results { get; } = new();
    public TextView FilterView { get; } = new();
    public TextBox SearchBox { get; } = new() { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
    public ToggleButton CaseToggle { get; } = Toggle("Aa", "Match case (upper and lower case exactly)");
    public ToggleButton WordToggle { get; } = Toggle("W", "Whole words only");
    public ToggleButton RegexToggle { get; } = Toggle(".*", "Regular expression (.NET syntax)");
    public Bookmarks Bookmarks { get; } = new();

    /// <summary>The view the user last clicked in (copy, save and bookmarks act on it).</summary>
    public TextView ActiveView { get; private set; }

    public bool IsIndexing { get; private set; }
    public double IndexProgress => _indexProgress;
    public bool IsSearching { get; private set; }
    public bool IsFiltering { get; private set; }
    public bool Following { get; private set; }
    /// <summary>Follow mode keeps the end in view while this is true; scrolling up pauses it, scrolling to the end resumes.</summary>
    public bool AutoScroll { get; private set; } = true;
    public TextEncodingKind? ForcedEncoding { get; private set; }
    public IReadOnlyList<SearchHit> Hits => _hits;
    public int CurrentHit => _currentHit;
    public LineRanges FilterRanges => _filterRanges;
    public SearchSummary? LastSummary { get; private set; }
    public LineFilterResult? LastFilter { get; private set; }
    public string FilePath => Doc.Path;
    public string Title => System.IO.Path.GetFileName(Doc.Path);

    /// <summary>A message for the status bar (error = shown in red).</summary>
    public event Action<string, bool>? Status;

    private DocumentTab(AppSession session, TextDocument doc, TextEncodingKind? forced)
    {
        _s = session;
        Doc = doc;
        ForcedEncoding = forced;
        ActiveView = Main;
        Main.Source = new DocumentSource(doc);
        Main.Bookmarks = Bookmarks;
        Main.Hits = _hits;
        Results.Hits = _hits;
        Results.Bookmarks = Bookmarks;
        Results.EmptyText = "Search results appear here.";
        Results.Source = new HitLinesSource(doc, _hitLines);
        FilterView.Hits = _hits;
        FilterView.Bookmarks = Bookmarks;
        FilterView.EmptyText = "Type a search, then click Matching lines or Other lines.";
        FilterView.Source = new RangesSource(doc, _filterRanges);
        ApplyViewSettings();

        foreach (var v in new[] { Main, Results, FilterView })
            v.GotFocus += (_, _) => ActiveView = v;
        Main.UserScrolled += () => { if (Following) AutoScroll = Main.IsAtEnd; };
        Results.RowClicked += v => GoToHitOnLine(v.Line.Number);
        FilterView.RowClicked += v => ShowLine(v.Line.Number, focusMain: false);

        // ---- search bar ----
        SearchBox.PlaceholderText = "Search (Enter), Shift+Enter for previous";
        SearchBox.KeyDown += OnSearchKey;
        CaseToggle.IsChecked = _s.Settings.CaseSensitive;
        WordToggle.IsChecked = _s.Settings.WholeWord;
        RegexToggle.IsChecked = _s.Settings.UseRegex;
        _searchStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _searchProgressBar = new ProgressBar { Width = 90, Minimum = 0, Maximum = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        _cancelButton = Btn("Cancel", "Stop the search or filter (Esc)", () => CancelWork());
        _cancelButton.IsVisible = false;
        var bar = new DockPanel { Margin = new Thickness(6, 4), LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        left.Children.Add(SearchBox);
        left.Children.Add(CaseToggle);
        left.Children.Add(WordToggle);
        left.Children.Add(RegexToggle);
        left.Children.Add(Btn("Find all", "Search the whole file (Enter)", () => _ = SearchAsync()));
        left.Children.Add(Btn("▲", "Previous match (Ctrl+P, Shift+F3)", () => StepHit(-1)));
        left.Children.Add(Btn("▼", "Next match (Ctrl+N, F3)", () => StepHit(1)));
        left.Children.Add(new Border { Width = 8 });
        left.Children.Add(Btn("Matching lines", "Filter: show only the lines that match, in the Filter pane", () => _ = FilterAsync(invert: false)));
        left.Children.Add(Btn("Other lines", "Filter: show only the lines that don't match (like grep -v)", () => _ = FilterAsync(invert: true)));
        left.Children.Add(_cancelButton);
        left.Children.Add(_searchProgressBar);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);
        bar.Children.Add(_searchStatus);

        // ---- bottom pane ----
        _filterStatus = new TextBlock { Margin = new Thickness(6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        var filterPanel = new DockPanel();
        DockPanel.SetDock(_filterStatus, Dock.Top);
        filterPanel.Children.Add(_filterStatus);
        filterPanel.Children.Add(FilterView);
        _resultsTab = new TabItem { Header = "Results", Content = Results };
        _filterTab = new TabItem { Header = "Filter", Content = filterPanel };
        _bottomTabs = new TabControl { ItemsSource = new[] { _resultsTab, _filterTab }, Padding = new Thickness(0) };
        var closeBottom = Btn("✕", "Hide the Results and Filter pane", () => ShowBottom(null));
        closeBottom.HorizontalAlignment = HorizontalAlignment.Right;
        closeBottom.VerticalAlignment = VerticalAlignment.Top;
        closeBottom.Margin = new Thickness(0, 4, 6, 0);
        var bottom = new Grid();
        bottom.Children.Add(_bottomTabs);
        bottom.Children.Add(closeBottom);

        _splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Height = 5, Background = Brushes.Transparent };
        _grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,0") };
        Grid.SetRow(bar, 0);
        Grid.SetRow(Main, 1);
        Grid.SetRow(_splitter, 2);
        Grid.SetRow(bottom, 3);
        _grid.Children.Add(bar);
        _grid.Children.Add(Main);
        _grid.Children.Add(_splitter);
        _grid.Children.Add(bottom);
        _splitter.IsVisible = false;
        Content = _grid;

        StartIndexing();
    }

    /// <summary>Opens a file (on a worker thread, so a slow network drive can't freeze the window) and starts indexing it.</summary>
    public static async Task<DocumentTab> OpenAsync(AppSession s, string path, TextEncodingKind? encoding = null)
    {
        var doc = await Task.Run(() => TextDocument.Open(path, s.Log, encoding, s.Settings.MaxLineBytes));
        return new DocumentTab(s, doc, encoding);
    }

    private static ToggleButton Toggle(string text, string tip)
    {
        var t = new ToggleButton { Content = text, MinWidth = 32, Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(t, tip);
        return t;
    }

    private static Button Btn(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Shows a message in the status bar (when this tab is showing) and logs it (--verbose shows every step).</summary>
    private void Say(string text, bool error = false)
    {
        if (error) _s.Log.Warning($"{Title}: {text}");
        else _s.Log.Info($"{Title}: {text}");
        Status?.Invoke(text, error);
    }

    /// <summary>Font, wrap, line numbers and highlight rules from the settings, for all three views.</summary>
    public void ApplyViewSettings()
    {
        var st = _s.Settings;
        var hl = Highlighter.Empty;
        if (st.HighlightsEnabled)
        {
            hl = Highlighter.Compile(st.HighlightRules, out var problems);
            foreach (var p in problems) _s.Log.Warning(p);
        }
        foreach (var v in new[] { Main, Results, FilterView })
        {
            v.SetFont(st.FontFamily, st.FontSize);
            v.WordWrap = st.WordWrap;
            v.ShowLineNumbers = st.ShowLineNumbers;
            v.Highlighter = hl;
        }
    }

    // ---------------- indexing and the tick ----------------

    private void StartIndexing()
    {
        var cts = new CancellationTokenSource();
        _indexCts = cts;
        IsIndexing = true;
        _indexProgress = 0;
        _indexClock.Restart();
        var doc = Doc;
        _indexTask = Task.Run(() => doc.IndexAsync(new InlineProgress<double>(p => _indexProgress = p), cts.Token));
        _indexTask.ContinueWith(t => Dispatcher.UIThread.Post(() => OnIndexed(t, doc)), TaskScheduler.Default);
    }

    private void OnIndexed(Task t, TextDocument doc)
    {
        if (!ReferenceEquals(doc, Doc) || _closed) return;
        IsIndexing = false;
        _indexClock.Stop();
        Main.Refresh();
        if (t.IsFaulted)
        {
            var ex = t.Exception!.GetBaseException();
            _s.Log.Error($"Indexing {Doc.Path} failed", ex);
            Say($"Reading {Title} stopped: {ex.Message}. The lines read so far are shown. Check that the drive is still there.", error: true);
        }
        else if (!t.IsCanceled)
        {
            Say($"{Title}: {Doc.LineCount:N0} lines, {CliCommands.FormatSize(Doc.Length)}, indexed in {_indexClock.Elapsed.TotalSeconds:F1} s.");
        }
        if (Following && AutoScroll) Main.ScrollToEnd();
    }

    /// <summary>Moves background results onto the screen. Called by the window ten times a second.</summary>
    public void Tick()
    {
        if (_closed) return;
        if (IsIndexing)
        {
            Main.Refresh();
            if (Following && AutoScroll) Main.ScrollToEnd();
        }
        if (_pendingGoTo is { } g && !g.IsPending(Doc))
        {
            _pendingGoTo = null;
            GoTo(g);
        }
        DrainHits();
        if (IsSearching || IsFiltering)
        {
            _searchProgressBar.Value = IsSearching ? _searchProgress : _filterProgress;
        }
        if (IsSearching) _searchStatus.Text = $"Searching... {_hits.Count:N0} found";
        if (IsFiltering)
        {
            FilterView.Refresh();
            _filterStatus.Text = $"Filtering... {_filterRanges.RowCount:N0} lines so far";
        }
        if (_saveProgress >= 0) Status?.Invoke($"Saving the selection... {CliCommands.FormatSize(_saveProgress)} written", false); // progress: not logged

        if (Following && !IsIndexing && !_refreshing && ++_followTicks >= FollowEveryTicks)
        {
            _followTicks = 0;
            _ = RefreshAsync(fromFollow: true);
        }
    }

    private void DrainHits()
    {
        bool any = false;
        while (_incoming.TryDequeue(out var batch))
        {
            foreach (var h in batch)
            {
                _hits.Add(h);
                if (_hitLines.Count == 0 || _hitLines[^1] != h.Line) _hitLines.Add(h.Line);
            }
            any = true;
        }
        if (!any) return;
        Results.Refresh();
        Main.InvalidateVisual();
        FilterView.InvalidateVisual();
        // The pane lists one row per line, so the header counts lines (a line with two matches is one row).
        _resultsTab.Header = $"Results ({Plural(_hitLines.Count, "line")})";
    }

    /// <summary>Looks at the file again (F5, and every half second in follow mode).</summary>
    public async Task RefreshAsync(bool fromFollow = false)
    {
        if (_refreshing || IsIndexing) return;
        _refreshing = true;
        var doc = Doc;
        try
        {
            var r = await doc.RefreshAsync();
            if (!ReferenceEquals(doc, Doc) || _closed) return;
            switch (r)
            {
                case RefreshResult.Grew:
                    _s.Log.Debug($"{Title} grew to {Doc.Length:N0} bytes, {Doc.LineCount:N0} lines.");
                    Main.Refresh();
                    if (Following && AutoScroll) Main.ScrollToEnd();
                    if (!fromFollow) Say($"{Title} grew: now {Doc.LineCount:N0} lines.");
                    break;
                case RefreshResult.Reindexed:
                    CancelWork();
                    ClearResults();
                    Bookmarks.TrimTo(Doc.LineCount);
                    Main.Refresh(reset: true);
                    Results.Refresh(reset: true);
                    FilterView.Refresh(reset: true);
                    if (Following && AutoScroll) Main.ScrollToEnd();
                    Say($"{Title} got shorter or was replaced (a new log?), so it was read again from the start. Search results were cleared.");
                    break;
                default:
                    if (!fromFollow) Say($"{Title} hasn't changed.");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _s.Log.Error($"Refreshing {doc.Path} failed", ex);
            Say($"Can't read {Title} any more ({ex.Message}). It may have been deleted or its drive removed.", error: true);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---------------- follow ----------------

    public void SetFollow(bool on)
    {
        Following = on;
        AutoScroll = true;
        _followTicks = FollowEveryTicks; // check at once
        if (on)
        {
            Main.ScrollToEnd();
            Main.SetCaret(new TextPos(Math.Max(0, Main.Count - 1), 0), scroll: false);
            Say($"Following {Title}: new lines show as they're written. Scroll up to pause; Ctrl+E (End of file) resumes.");
        }
        else
        {
            Say("Follow mode off.");
        }
    }

    /// <summary>Follow mode, for the status bar.</summary>
    public string FollowText => !Following ? "" : AutoScroll ? "Following" : "Follow paused (Ctrl+E resumes)";

    // ---------------- going places ----------------

    public void GoTo(GoToTarget t)
    {
        if (t.IsPending(Doc))
        {
            _pendingGoTo = t;
            Say($"Going to {t} once indexing reaches it...");
            return;
        }
        ShowLine(t.ResolveLine(Doc), focusMain: true);
    }

    /// <summary>Shows a file line in the main view with the caret on it.</summary>
    public void ShowLine(long line, bool focusMain)
    {
        if (Main.Count == 0) return;
        line = Math.Clamp(line, 0, Main.Count - 1);
        Main.SetCaret(new TextPos(line, 0), scroll: false);
        Main.ScrollTo(line - Main.FullRows / 3);
        if (Following) AutoScroll = Main.IsAtEnd;
        if (focusMain) Main.Focus();
    }

    public void GoToStart()
    {
        Main.SetCaret(new TextPos(0, 0), scroll: false);
        Main.ScrollTo(0, user: true);
        Main.Focus();
    }

    public void GoToEnd()
    {
        Main.SetCaret(new TextPos(Math.Max(0, Main.Count - 1), 0), scroll: false);
        Main.ScrollToEnd(user: true);
        Main.Focus();
    }

    /// <summary>The file line under the caret of the active view.</summary>
    private long CaretLine()
    {
        var v = ActiveView;
        if (v.Source == null || v.Count == 0) return 0;
        var lines = v.Source.Read(v.Caret.Row, 1);
        return lines.Count > 0 ? lines[0].Line.Number : 0;
    }

    public void ToggleBookmark()
    {
        long line = CaretLine();
        bool on = Bookmarks.Toggle(line);
        InvalidateViews();
        Say(on ? $"Bookmarked line {line + 1:N0} ({Bookmarks.Count} bookmark(s)). F2 goes to the next one." : $"Removed the bookmark on line {line + 1:N0}.");
    }

    public void StepBookmark(int dir)
    {
        long line = Main.Caret.Row;
        long? next = dir > 0 ? Bookmarks.Next(line) : Bookmarks.Previous(line);
        if (next == null) { Say("No bookmarks yet: Ctrl+F2 bookmarks the current line."); return; }
        ShowLine(next.Value, focusMain: true);
    }

    public void ClearBookmarks()
    {
        Bookmarks.Clear();
        InvalidateViews();
        Say("Bookmarks cleared.");
    }

    private void InvalidateViews()
    {
        Main.InvalidateVisual();
        Results.InvalidateVisual();
        FilterView.InvalidateVisual();
    }

    // ---------------- search ----------------

    private SearchQuery? QueryFromBar()
    {
        string text = SearchBox.Text ?? "";
        if (text.Length == 0)
        {
            Say("Type something to search for first (Ctrl+F).");
            SearchBox.Focus();
            return null;
        }
        var st = _s.Settings;
        st.CaseSensitive = CaseToggle.IsChecked == true;
        st.WholeWord = WordToggle.IsChecked == true;
        st.UseRegex = RegexToggle.IsChecked == true;
        _s.SaveSettings();
        return new SearchQuery(text, st.CaseSensitive, st.UseRegex, st.WholeWord);
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when (e.KeyModifiers & KeyModifiers.Shift) != 0:
                StepHit(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_hits.Count > 0 && _lastQuery != null && QueryMatchesBar(_lastQuery) && !IsSearching) StepHit(1);
                else _ = SearchAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                if (IsSearching || IsFiltering) CancelWork();
                else Main.Focus();
                e.Handled = true;
                break;
        }
    }

    private bool QueryMatchesBar(SearchQuery q) =>
        q.Text == SearchBox.Text && q.CaseSensitive == (CaseToggle.IsChecked == true) &&
        q.WholeWord == (WordToggle.IsChecked == true) && q.IsRegex == (RegexToggle.IsChecked == true);

    /// <summary>Starts a search for the given text (the command line's --find).</summary>
    public Task StartSearchAsync(string text, bool caseSensitive, bool word, bool regex)
    {
        SearchBox.Text = text;
        CaseToggle.IsChecked = caseSensitive;
        WordToggle.IsChecked = word;
        RegexToggle.IsChecked = regex;
        return SearchAsync();
    }

    public async Task SearchAsync()
    {
        var q = QueryFromBar();
        if (q == null) return;
        await StopSearchAsync();
        ClearResults();
        _lastQuery = q;
        ShowBottom(_resultsTab);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        _searchProgress = 0;
        SetBusy(true);
        _searchStatus.ClearValue(TextBlock.ForegroundProperty);
        var doc = Doc;
        var run = RunSearch(doc, q, cts.Token);
        _searchTask = run;
        try
        {
            var summary = await run;
            DrainHits();
            LastSummary = summary;
            string text = SearchSummaryText(q.ToString(), summary.TotalMatches, summary.Hits.Count, _hitLines.Count, summary.LimitReached, summary.Elapsed);
            _searchStatus.Text = text;
            Say(text);
            // A go-to waiting for the index (--line with --find) lands first, so the first match is taken from there.
            if (_pendingGoTo is { } g && !g.IsPending(Doc)) { _pendingGoTo = null; GoTo(g); }
            if (_hits.Count > 0 && _currentHit < 0) GoToHit(FirstHitAtOrAfter(Main.Caret.Row));
        }
        catch (OperationCanceledException)
        {
            DrainHits();
            _searchStatus.Text = $"Search stopped: {_hits.Count:N0} found before Cancel.";
        }
        catch (SearchException ex)
        {
            _searchStatus.Text = $"{ex.Message} {ex.Fix}";
            _searchStatus.Foreground = Brushes.OrangeRed;
            Say($"{ex.Message} {ex.Fix}", error: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_closed) Say($"The search stopped: {ex.Message}", error: true);
        }
        finally
        {
            if (_searchCts == cts) _searchCts = null;
            cts.Dispose();
            IsSearching = false;
            SetBusy(IsFiltering);
        }
    }

    private async Task<SearchSummary> RunSearch(TextDocument doc, SearchQuery q, CancellationToken ct)
    {
        await WaitForIndexAsync(ct);
        return await SearchEngine.RunAsync(doc, q, _s.Settings.MaxHits, batch => _incoming.Enqueue(batch),
            new InlineProgress<double>(p => _searchProgress = p), ct, log: _s.Log);
    }

    /// <summary>Searches cover the whole file, so they wait for indexing to finish first.</summary>
    private async Task WaitForIndexAsync(CancellationToken ct)
    {
        if (!IsIndexing) return;
        Dispatcher.UIThread.Post(() => _searchStatus.Text = "Waiting for indexing to finish...");
        var wait = new TaskCompletionSource();
        using (ct.Register(() => wait.TrySetCanceled()))
            await Task.WhenAny(_indexTask, wait.Task);
        ct.ThrowIfCancellationRequested();
    }

    private void SetBusy(bool busy)
    {
        _cancelButton.IsVisible = busy;
        _searchProgressBar.IsVisible = busy;
    }

    private void ClearResults()
    {
        while (_incoming.TryDequeue(out _)) { }
        _hits.Clear();
        _hitLines.Clear();
        _currentHit = -1;
        LastSummary = null;
        Main.CurrentHit = Results.CurrentHit = FilterView.CurrentHit = -1;
        Results.Refresh(reset: true);
        Main.InvalidateVisual();
        _resultsTab.Header = "Results";
        _searchStatus.Text = "";
    }

    private int FirstHitAtOrAfter(long line)
    {
        int lo = 0, hi = _hits.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_hits[mid].Line < line) lo = mid + 1;
            else hi = mid;
        }
        return lo < _hits.Count ? lo : 0;
    }

    /// <summary>Next (+1) or previous (-1) match, wrapping around; starts a search if there are no results yet.</summary>
    public void StepHit(int dir)
    {
        if (_hits.Count == 0)
        {
            if (!IsSearching && !string.IsNullOrEmpty(SearchBox.Text)) _ = SearchAsync();
            else if (!IsSearching) Say("No search results: type in the search box and press Enter (Ctrl+F).");
            return;
        }
        int i;
        if (_currentHit < 0 || _hits[_currentHit].Line != Main.Caret.Row)
        {
            // The caret moved since the last match: continue from the caret, not from the old match.
            i = FirstHitAtOrAfter(Main.Caret.Row);   // wraps to 0 when nothing is after the caret
            if (dir < 0)
            {
                i--;
                if (i < 0) i = _hits.Count - 1;
            }
        }
        else
        {
            i = (_currentHit + dir + _hits.Count) % _hits.Count;
        }
        GoToHit(i);
    }

    private void GoToHitOnLine(long line)
    {
        int i = FirstHitAtOrAfter(line);
        if (i < _hits.Count) GoToHit(i);
    }

    public void GoToHit(int i)
    {
        if (i < 0 || i >= _hits.Count) return;
        _currentHit = i;
        Main.CurrentHit = Results.CurrentHit = FilterView.CurrentHit = i;
        var h = _hits[i];
        if (h.Column >= 0) Main.SelectRange(h.Line, h.Column, h.Column + h.CharLength);
        else Main.SetCaret(new TextPos(h.Line, 0));
        if (Following) AutoScroll = Main.IsAtEnd;
        long row = Results.Source!.RowOfLine(h.Line);
        if (row >= 0) Results.SetCaret(new TextPos(row, 0));
        Results.InvalidateVisual();
        FilterView.InvalidateVisual();
        string more = LastSummary is { LimitReached: true } s ? $" (of {s.TotalMatches:N0}, first {_hits.Count:N0} listed)" : "";
        Say($"Match {i + 1:N0} of {_hits.Count:N0}{more}: line {h.Line + 1:N0}" + (h.Column >= 0 ? $", column {h.Column + 1:N0}." : "."));
    }

    // ---------------- filter ----------------

    public async Task FilterAsync(bool invert)
    {
        var q = QueryFromBar();
        if (q == null) return;
        await StopFilterAsync();
        _filterRanges = new LineRanges();
        FilterView.Source = new RangesSource(Doc, _filterRanges);
        FilterView.EmptyText = "No lines yet...";
        _filterTab.Header = invert ? "Filter: other lines" : "Filter: matching lines";
        ShowBottom(_filterTab);
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        IsFiltering = true;
        _filterProgress = 0;
        SetBusy(true);
        _filterStatus.ClearValue(TextBlock.ForegroundProperty);
        _filterStatus.Text = "Filtering...";
        var doc = Doc;
        var ranges = _filterRanges;
        var run = RunFilter(doc, q, invert, ranges, cts.Token);
        _filterTask = run;
        try
        {
            var r = await run;
            LastFilter = r;
            FilterView.Refresh();
            string what = invert ? $"don't match {q}" : $"match {q}";
            _filterStatus.Text = $"{r.Lines:N0} of {r.SearchedLines:N0} lines {what} ({r.Elapsed.TotalSeconds:F1} s). Click a line to show it above." +
                (r.StoppedAtLimit ? $" Stopped at line {r.CompleteUpTo + 1:N0}: the result broke into over a million separate pieces." : "");
            FilterView.EmptyText = invert ? "Every line matches." : "No lines match.";
            Say(_filterStatus.Text);
        }
        catch (OperationCanceledException)
        {
            FilterView.Refresh();
            _filterStatus.Text = $"Filter stopped: {ranges.RowCount:N0} lines found before Cancel.";
        }
        catch (SearchException ex)
        {
            _filterStatus.Text = $"{ex.Message} {ex.Fix}";
            _filterStatus.Foreground = Brushes.OrangeRed;
            Say($"{ex.Message} {ex.Fix}", error: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_closed) Say($"The filter stopped: {ex.Message}", error: true);
        }
        finally
        {
            if (_filterCts == cts) _filterCts = null;
            cts.Dispose();
            IsFiltering = false;
            SetBusy(IsSearching);
        }
    }

    private async Task<LineFilterResult> RunFilter(TextDocument doc, SearchQuery q, bool invert, LineRanges ranges, CancellationToken ct)
    {
        await WaitForIndexAsync(ct);
        return await LineFilter.RunAsync(doc, q, invert, ranges, progress: new InlineProgress<double>(p => _filterProgress = p), ct: ct, log: _s.Log);
    }

    public void CancelWork()
    {
        _searchCts?.Cancel();
        _filterCts?.Cancel();
    }

    private async Task StopSearchAsync()
    {
        _searchCts?.Cancel();
        try { await _searchTask; } catch (Exception) { /* reported by the run that owned it */ }
    }

    private async Task StopFilterAsync()
    {
        _filterCts?.Cancel();
        try { await _filterTask; } catch (Exception) { /* reported by the run that owned it */ }
    }

    /// <summary>Shows the bottom pane on a tab, or hides it (null).</summary>
    public void ShowBottom(TabItem? tab)
    {
        var row = _grid.RowDefinitions[3];
        if (tab == null)
        {
            row.Height = new GridLength(0);
            _splitter.IsVisible = false;
            Main.Focus();
            return;
        }
        if (row.Height.Value < 60) row.Height = new GridLength(Math.Max(160, Bounds.Height * 0.3));
        _splitter.IsVisible = true;
        _bottomTabs.SelectedItem = tab;
    }

    internal bool BottomVisible => _grid.RowDefinitions[3].Height.Value > 0;
    internal TabItem ResultsTab => _resultsTab;

    private static string Plural(long n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// The search status line. Over the result limit only the matches are counted, not their lines, so the line count is
    /// given for the listed part alone: "1,940,400 matches in 3.7 s; the first 10,000 (on 9,005 lines) are listed".
    /// </summary>
    internal static string SearchSummaryText(string query, long total, int listed, int listedLines, bool limitReached, TimeSpan elapsed)
    {
        if (total == 0) return $"No matches for {query}.";
        string matches = total == 1 ? "1 match" : $"{total:N0} matches";
        string time = $"{elapsed.TotalSeconds:F1} s";
        return limitReached
            ? $"{matches} in {time}; the first {listed:N0} (on {Plural(listedLines, "line")}) are listed (Edit > Result limit)"
            : $"{matches} on {Plural(listedLines, "line")} in {time}";
    }
    internal TabItem FilterTab => _filterTab;
    internal string SearchStatusText => _searchStatus.Text ?? "";
    internal string FilterStatusText => _filterStatus.Text ?? "";

    // ---------------- copy and save ----------------

    public async Task CopyAsync()
    {
        var v = ActiveView;
        string? text;
        string what;
        if (!v.HasSelection)
        {
            text = v.SelectedLinesWithNumbers(1) is { } withNumber ? withNumber[(withNumber.IndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd('\r', '\n') : null;
            what = "the current line";
        }
        else
        {
            text = v.SelectedText(MaxCopyChars, out bool cut);
            var (first, last) = v.SelectedRows;
            what = $"{last - first + 1:N0} line(s)" + (cut ? " (very long lines are copied up to the part shown; File > Save selection keeps them whole)" : "");
        }
        if (text == null)
        {
            Say("The selection is too big for the clipboard (over 50 million characters). Use File > Save selection instead.", error: true);
            return;
        }
        if (await SetClipboardAsync(text)) Say($"Copied {what}.");
    }

    public async Task CopyLinesWithNumbersAsync()
    {
        var text = ActiveView.SelectedLinesWithNumbers(MaxCopyRows);
        if (text == null)
        {
            Say($"That's more than {MaxCopyRows:N0} lines to copy. Use File > Save selection instead.", error: true);
            return;
        }
        var (first, last) = ActiveView.SelectedRows;
        if (await SetClipboardAsync(text)) Say($"Copied {last - first + 1:N0} line(s) with their line numbers.");
    }

    private async Task<bool> SetClipboardAsync(string text)
    {
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip == null) { Say("The clipboard isn't available.", error: true); return false; }
        try
        {
            await clip.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            _s.Log.Error("Copying to the clipboard failed", ex);
            Say($"Copying failed: {ex.Message}", error: true);
            return false;
        }
    }

    /// <summary>Writes the selected bytes of the file, unchanged, to a new file the user picks.</summary>
    public async Task SaveSelectionAsync()
    {
        var v = ActiveView;
        if (!v.HasSelection)
        {
            Say("Select some text first: drag with the mouse, Shift+click, or Ctrl+A for everything.");
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var (first, last) = v.SelectedRows;
        string ext = System.IO.Path.GetExtension(Doc.Path);
        string name = System.IO.Path.GetFileNameWithoutExtension(Doc.Path);
        IStorageFolder? folder = null;
        try { folder = await top.StorageProvider.TryGetFolderFromPathAsync(System.IO.Path.GetDirectoryName(Doc.Path)!); } catch (Exception) { /* no start folder */ }
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save selection as",
            SuggestedFileName = $"{name}-selection{ext}",
            SuggestedStartLocation = folder,
            ShowOverwritePrompt = true,
        });
        string? path = file?.TryGetLocalPath();
        if (file == null) return;
        if (path == null) { Say("That location can't be written to directly; pick a folder on a drive.", error: true); return; }
        await SaveSelectionToAsync(v, path);
    }

    /// <summary>Saves the selection to <paramref name="path"/> (tests call this directly). Never writes to an open file.</summary>
    internal async Task<bool> SaveSelectionToAsync(TextView v, string path)
    {
        if (SamePath(path, Doc.Path))
        {
            Say("That's the file being shown. BejView never changes the files it shows: pick another name.", error: true);
            return false;
        }
        var (s, e) = v.Selection;
        var source = v.Source!;
        var doc = Doc;
        _saveProgress = 0;
        try
        {
            long written = await Task.Run(() => WriteSelection(doc, source, s, e, path));
            Say($"Saved {CliCommands.FormatSize(written)} to {path}.");
            _s.Log.Info($"Saved selection rows {s.Row + 1}-{e.Row + 1} of {doc.Path} ({written:N0} bytes) to {path}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _s.Log.Error($"Saving the selection to {path} failed", ex);
            Say($"Can't save to {path}: {ex.Message}", error: true);
            return false;
        }
        finally
        {
            _saveProgress = -1;
        }
    }

    private long WriteSelection(TextDocument doc, ILineSource source, TextPos s, TextPos e, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var progress = new InlineProgress<long>(n => _saveProgress = n);
        long written = 0;
        int bom = doc.Encoding.BomLength;
        long ByteAt(LineText line, int col, bool isEnd) =>
            line.Truncated && col >= line.Text.Length && isEnd ? line.ByteStart + line.ByteLength : doc.ByteOffsetOfColumn(line, col);

        var startLine = source.Read(s.Row, 1)[0].Line;
        var endLine = source.Read(e.Row, 1)[0].Line;
        if (bom > 0) written += doc.CopyBytes(0, bom, fs); // keep a UTF-16 file readable as UTF-16

        if (source.IsWholeFile)
        {
            long a = ByteAt(startLine, s.Col, false), b = ByteAt(endLine, e.Col, true);
            return written + doc.CopyBytes(a, b, fs, progress);
        }
        for (long row = s.Row; row <= e.Row;)
        {
            var lines = source.Read(row, (int)Math.Min(1000, e.Row - row + 1));
            if (lines.Count == 0) break;
            foreach (var l in lines)
            {
                long a = l.Row == s.Row ? ByteAt(l.Line, s.Col, false) : l.Line.ByteStart;
                long b = l.Row == e.Row ? ByteAt(l.Line, e.Col, true) : l.Line.ByteStart + l.Line.ByteLength;
                written += doc.CopyBytes(a, b, fs);
                progress.Report(written);
            }
            row += lines.Count;
        }
        return written;
    }

    internal static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- encodings and closing ----------------

    /// <summary>Opens the file again with a chosen encoding (null = detect), keeping the place.</summary>
    public async Task ReopenAsync(TextEncodingKind? kind)
    {
        long topByte = Main.Count > 0 ? SafeLineStart(Main.TopRow) : 0;
        await StopAllAsync();
        TextDocument fresh;
        try
        {
            fresh = await Task.Run(() => TextDocument.Open(Doc.Path, _s.Log, kind, _s.Settings.MaxLineBytes));
        }
        catch (FileOpenException ex)
        {
            Say($"{ex.Message} {ex.Fix}", error: true);
            StartIndexing();
            return;
        }
        var old = Doc;
        Doc = fresh;
        ForcedEncoding = kind;
        old.Dispose();
        ClearResults();
        Bookmarks.Clear();
        _filterRanges = new LineRanges();
        Main.Source = new DocumentSource(fresh);
        Results.Source = new HitLinesSource(fresh, _hitLines);
        FilterView.Source = new RangesSource(fresh, _filterRanges);
        _filterStatus.Text = "";
        StartIndexing();
        _pendingGoTo = new GoToTarget(GoToKind.ByteOffset, topByte);
        Say($"Reopened {Title} as {fresh.Encoding.DisplayName}{(kind == null ? " (detected)" : "")}.");
    }

    private long SafeLineStart(long line)
    {
        try { return Doc.Index.LineStart(line); }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    private async Task StopAllAsync()
    {
        _indexCts?.Cancel();
        CancelWork();
        try { await _indexTask; } catch (Exception) { /* cancelled or failed; reported already */ }
        await StopSearchAsync();
        await StopFilterAsync();
        IsIndexing = false;
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        await StopAllAsync();
        Doc.Dispose();
        _s.Log.Info($"Closed {Doc.Path}.");
    }
}
