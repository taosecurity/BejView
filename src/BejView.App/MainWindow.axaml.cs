using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using BejView.App.Views;
using BejView.Core;
using BejView.Core.Highlighting;
using BejView.Core.IO;
using BejView.Core.Navigation;
using BejView.Core.Platform;
using BejView.Core.Settings;
using BejView.Core.Text;

namespace BejView.App;

public partial class MainWindow : Window
{
    private const string ReadMeUrl = "https://github.com/taosecurity/BejView#readme";
    private static readonly int[] LimitChoices = [1_000, 10_000, 100_000, 1_000_000];
    private static readonly string[] FontChoices =
    [
        "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New", "JetBrains Mono", "Fira Code",
        "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono",
    ];
    private static readonly (string Label, TextEncodingKind? Kind)[] EncodingChoices =
    [
        ("Detect automatically", null), ("UTF-8", TextEncodingKind.Utf8), ("UTF-16 LE (Windows \"Unicode\")", TextEncodingKind.Utf16LE),
        ("UTF-16 BE", TextEncodingKind.Utf16BE), ("Latin-1 (ISO-8859-1)", TextEncodingKind.Latin1),
    ];

    private readonly AppSession _s;
    private readonly DispatcherTimer _timer;
    private int _ticks;
    private bool _shownIndexing;

    // Needed by the XAML loader / previewer.
    public MainWindow() : this(AppSession.Current) { }

    internal MainWindow(AppSession session)
    {
        _s = session;
        InitializeComponent();
        Title = $"BejView {AppInfo.Version}";
        var st = _s.Settings;
        if (st.WindowWidth >= MinWidth) Width = st.WindowWidth;
        if (st.WindowHeight >= MinHeight) Height = st.WindowHeight;
        if (st.WindowMaximized) WindowState = WindowState.Maximized;

        LoadOptionsIntoMenu();
        BuildRecentMenu();
        BuildLimitMenu();
        BuildPresetMenu();
        BuildFontMenu();
        BuildEncodingMenu();
        AddKeys();

        Tabs.SelectionChanged += (_, _) => OnTabChanged();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
        Closing += (_, _) => SaveWindowState();
        Closed += async (_, _) =>
        {
            _timer.Stop();
            foreach (var t in AllTabs.ToList()) await t.CloseAsync();
        };
        UpdateStatusBar();
    }

    // ---------------- tabs ----------------

    internal IEnumerable<DocumentTab> AllTabs => Tabs.Items.OfType<TabItem>().Select(i => i.Content).OfType<DocumentTab>();
    internal DocumentTab? CurrentTab => (Tabs.SelectedItem as TabItem)?.Content as DocumentTab;

    /// <summary>Opens a file in a new tab (or shows its tab if it's already open). Returns null when it can't be opened.</summary>
    internal async Task<DocumentTab?> OpenFileAsync(string path, TextEncodingKind? encoding = null)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { full = path; }
        var existing = AllTabs.FirstOrDefault(t => DocumentTab.SamePath(t.FilePath, full));
        if (existing != null)
        {
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(i => i.Content == existing);
            SetStatus($"{existing.Title} is already open.");
            return existing;
        }

        SetStatus($"Opening {full}...");
        DocumentTab tab;
        try
        {
            tab = await DocumentTab.OpenAsync(_s, full, encoding ?? _s.Options.Encoding);
        }
        catch (FileOpenException ex)
        {
            _s.Log.Warning($"{ex.Message} {ex.Fix}");
            SetStatus($"{ex.Message} {ex.Fix}", error: true);
            if (IsVisible) await Dialogs.ShowMessage(this, "Can't open the file", $"{ex.Message}\n\n{ex.Fix}");
            return null;
        }

        tab.Status += (text, error) => { if (CurrentTab == tab) SetStatus(text, error, log: false); }; // the tab logs its own
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "✕", Padding = new Thickness(4, 0), FontSize = 11, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close (Ctrl+W)");
        header.Children.Add(close);
        var item = new TabItem { Header = header, Content = tab };
        ToolTip.SetTip(item, tab.FilePath);
        close.Click += (_, _) => _ = CloseTabAsync(tab);
        tab.Main.ZoomRequested += d => ChangeFontSize(d);
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
        EmptyHint.IsVisible = false;

        _s.Settings.AddRecent(tab.FilePath);
        _s.SaveSettings();
        BuildRecentMenu();
        if (_s.Settings.FollowByDefault) tab.SetFollow(true);
        tab.Main.Focus();
        SetStatus($"Opened {tab.FilePath} ({tab.Doc.Encoding.DisplayName}). Indexing...");
        return tab;
    }

    /// <summary>The file, line, search and follow from the command line.</summary>
    internal async void OpenFromCommandLine()
    {
        var o = _s.Options;
        if (o.File == null) return;
        var tab = await OpenFileAsync(o.File);
        if (tab == null) return;
        if (o.Follow) tab.SetFollow(true);
        if (o.Line is { } line) tab.GoTo(new GoToTarget(GoToKind.Line, line));
        if (o.Find != null) await tab.StartSearchAsync(o.Find, o.CaseSensitive, o.WholeWord, o.Regex);
    }

    internal async Task CloseTabAsync(DocumentTab tab)
    {
        var item = Tabs.Items.OfType<TabItem>().FirstOrDefault(i => i.Content == tab);
        if (item == null) return;
        Tabs.Items.Remove(item);
        EmptyHint.IsVisible = Tabs.Items.Count == 0;
        await tab.CloseAsync();
        UpdateStatusBar();
        ScheduleRelease();
    }

    /// <summary>
    /// Collects a closed tab's memory a moment after it closes, once nothing holds the tab any more: <see cref="CloseTabAsync"/>
    /// and its caller (the Ctrl+W handler) hold it until they return, and queued layout and render jobs can too.
    /// Collecting inside CloseTabAsync freed nothing on the 50 GB dump (beta 2), and the memory waited for the next tab to
    /// close. This is its own method on purpose: a lambda written inside CloseTabAsync would share the compiler's closure
    /// with the lambda there that uses <c>tab</c>, and so keep the closed tab alive until it ran.
    /// </summary>
    private void ScheduleRelease() =>
        DispatcherTimer.RunOnce(() =>
        {
            ReleaseMemory("closing a tab");
            UpdateStatusBar(forceRam: true);
            MemoryReleased?.Invoke();
        }, TimeSpan.FromMilliseconds(250));

    /// <summary>Raised right after the collection that follows closing a tab (for tests).</summary>
    internal event Action? MemoryReleased;

    /// <summary>
    /// Hands the memory of a closed file (its index, results, filter and buffers) back to Windows now. Without this,
    /// .NET keeps it until it next needs a collection, so RAM in the status bar didn't drop after Ctrl+W (beta 1, test 3.7).
    /// One blocking collection of a small heap takes a few milliseconds, and it only runs when a tab closes.
    /// </summary>
    private void ReleaseMemory(string why)
    {
        static long Mb() { using var p = Process.GetCurrentProcess(); return p.PrivateMemorySize64 / 1048576; }
        long before = Mb();
        var sw = Stopwatch.StartNew();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _s.Log.Debug($"Memory after {why}: {before:N0} MB -> {Mb():N0} MB (collection took {sw.ElapsedMilliseconds} ms).");
    }

    private void OnTabChanged()
    {
        UpdateEncodingMenu();
        FollowOpt.IsChecked = CurrentTab?.Following == true;
        UpdateStatusBar();
        CurrentTab?.Main.Focus();
    }

    // ---------------- drag and drop ----------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;
        e.Handled = true;
        foreach (var f in files)
        {
            string? path = f.TryGetLocalPath();
            if (path != null) await OpenFileAsync(path);
        }
    }

    // ---------------- the tick and the status bar ----------------

    internal void OnTick()
    {
        foreach (var t in AllTabs) t.Tick();
        // Every 0.3 s, and at once when indexing finishes (so the bar never lingers on 'Indexing 99%').
        bool indexing = CurrentTab?.IsIndexing == true;
        if (++_ticks % 3 == 0 || indexing != _shownIndexing) UpdateStatusBar();
    }

    private void UpdateStatusBar(bool forceRam = false)
    {
        var t = CurrentTab;
        _shownIndexing = t?.IsIndexing == true;
        if (t == null)
        {
            LinesText.Text = CaretText.Text = EncodingText.Text = FollowText.Text = "";
            IndexBar.IsVisible = false;
        }
        else
        {
            var d = t.Doc;
            if (t.IsIndexing)
            {
                IndexBar.IsVisible = true;
                IndexBar.Value = t.IndexProgress;
                LinesText.Text = $"Indexing {t.IndexProgress:P0}: {d.LineCount:N0} lines so far";
            }
            else
            {
                IndexBar.IsVisible = false;
                LinesText.Text = $"{d.LineCount:N0} lines, {Core.Cli.CliCommands.FormatSize(d.Length)}";
            }
            var v = t.ActiveView;
            CaretText.Text = v.Count == 0 ? "" : $"Ln {CaretFileLine(v) + 1:N0}, Col {v.Caret.Col + 1:N0}";
            EncodingText.Text = d.Encoding.DisplayName + (t.ForcedEncoding != null ? " (chosen)" : "");
            FollowText.Text = t.FollowText;
            FollowText.Foreground = t.Following && !t.AutoScroll ? Brushes.DarkOrange : null;
            if (!t.Following || t.AutoScroll) FollowText.ClearValue(TextBlock.ForegroundProperty);
            FollowOpt.IsChecked = t.Following;
        }
        if (forceRam || _ticks % 10 == 0 || RamText.Text is null or "")
        {
            using var p = Process.GetCurrentProcess();
            RamText.Text = $"RAM {p.PrivateMemorySize64 / 1048576.0:N0} MB";
            ToolTip.SetTip(RamText, $"Private memory {p.PrivateMemorySize64 / 1048576.0:N0} MB, working set {p.WorkingSet64 / 1048576.0:N0} MB");
        }
    }

    private static long CaretFileLine(TextView v)
    {
        if (v.Source == null || v.Count == 0) return 0;
        if (v.Source.IsWholeFile) return v.Caret.Row;
        try
        {
            var l = v.Source.Read(v.Caret.Row, 1);
            return l.Count > 0 ? l[0].Line.Number : 0;
        }
        catch (Exception) { return 0; }
    }

    internal void SetStatus(string text, bool error = false, bool log = true)
    {
        if (log)
        {
            if (error) _s.Log.Warning(text);
            else _s.Log.Info(text);
        }
        StatusText.Text = text;
        // Errors in orange-red; otherwise back to the theme's colour. (A null Foreground would draw nothing: SquareSnap beta 2's bug.)
        if (error) StatusText.Foreground = Brushes.OrangeRed;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
        ToolTip.SetTip(StatusText, text);
    }

    internal string StatusMessage => StatusText.Text ?? "";

    // ---------------- keys ----------------

    private void AddKeys()
    {
        void K(string gesture, Action a) => KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse(gesture), Command = new Relay(a) });
        void T(string gesture, Func<DocumentTab, Task> a) => K(gesture, () => { if (CurrentTab is { } t) _ = a(t); });
        void D(string gesture, Action<DocumentTab> a) => K(gesture, () => { if (CurrentTab is { } t) a(t); });

        K("Ctrl+O", () => _ = OpenDialogAsync());
        T("Ctrl+W", CloseTabAsync);
        K("Ctrl+Tab", () => StepTab(1));
        K("Ctrl+Shift+Tab", () => StepTab(-1));
        T("F5", t => t.RefreshAsync());
        T("Ctrl+S", t => t.SaveSelectionAsync());
        K("Ctrl+C", () => { if (FocusedTextBox() is { } tb) tb.Copy(); else if (CurrentTab is { } t) _ = t.CopyAsync(); });
        T("Ctrl+Shift+C", t => t.CopyLinesWithNumbersAsync());
        K("Ctrl+A", () => { if (FocusedTextBox() is { } tb) tb.SelectAll(); else CurrentTab?.ActiveView.SelectAll(); });
        K("Ctrl+F", FocusSearch);
        D("Ctrl+N", t => t.StepHit(1));
        D("F3", t => t.StepHit(1));
        D("Ctrl+P", t => t.StepHit(-1));
        D("Shift+F3", t => t.StepHit(-1));
        T("Ctrl+Shift+M", t => t.FilterAsync(invert: false));
        T("Ctrl+Shift+O", t => t.FilterAsync(invert: true));
        D("Escape", t => t.CancelWork());
        K("Ctrl+G", () => _ = GoToDialogAsync());
        D("Ctrl+B", t => t.GoToStart());
        D("Ctrl+E", t => t.GoToEnd());
        D("Ctrl+F2", t => t.ToggleBookmark());
        D("F2", t => t.StepBookmark(1));
        D("Shift+F2", t => t.StepBookmark(-1));
        K("Ctrl+L", ToggleFollow);
        K("Alt+Z", () => SetOption(WrapOpt, !_s.Settings.WordWrap));
        K("Ctrl+D", () => SetOption(DarkOpt, !_s.Settings.DarkMode));
        foreach (var g in new[] { "Ctrl+OemPlus", "Ctrl+Add", "Ctrl+Shift+OemPlus" }) K(g, () => ChangeFontSize(1));
        foreach (var g in new[] { "Ctrl+OemMinus", "Ctrl+Subtract" }) K(g, () => ChangeFontSize(-1));
        foreach (var g in new[] { "Ctrl+D0", "Ctrl+NumPad0" }) K(g, () => SetFontSize(10));
        K("F1", () => _ = Dialogs.ShowMessage(this, "Keyboard shortcuts", Dialogs.KeysText, monospace: true));
    }

    private TextBox? FocusedTextBox() => FocusManager?.GetFocusedElement() as TextBox;

    private void StepTab(int dir)
    {
        int n = Tabs.Items.Count;
        if (n < 2) return;
        Tabs.SelectedIndex = (Tabs.SelectedIndex + dir + n) % n;
    }

    private void FocusSearch()
    {
        if (CurrentTab is not { } t) return;
        // Start from the selected text when it's short and on one line.
        var v = t.ActiveView;
        if (v.HasSelection && v.Selection.Start.Row == v.Selection.End.Row && v.SelectedText(200, out _) is { Length: > 0 } sel)
            t.SearchBox.Text = sel;
        t.SearchBox.Focus();
        t.SearchBox.SelectAll();
    }

    private void ToggleFollow()
    {
        if (CurrentTab is not { } t) return;
        t.SetFollow(!t.Following);
        FollowOpt.IsChecked = t.Following;
        UpdateStatusBar();
    }

    // ---------------- options ----------------

    private void LoadOptionsIntoMenu()
    {
        var st = _s.Settings;
        WrapOpt.IsChecked = st.WordWrap;
        NumbersOpt.IsChecked = st.ShowLineNumbers;
        HighlightOpt.IsChecked = st.HighlightsEnabled;
        DarkOpt.IsChecked = st.DarkMode;
        FollowDefaultOpt.IsChecked = st.FollowByDefault;
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem m) SetOption(m, m.IsChecked);
    }

    /// <summary>Applies one of the checkbox options, saves the settings, and updates every open tab.</summary>
    internal void SetOption(MenuItem item, bool on)
    {
        var st = _s.Settings;
        item.IsChecked = on;
        if (item == WrapOpt) st.WordWrap = on;
        else if (item == NumbersOpt) st.ShowLineNumbers = on;
        else if (item == HighlightOpt) st.HighlightsEnabled = on;
        else if (item == FollowDefaultOpt) st.FollowByDefault = on;
        else if (item == DarkOpt)
        {
            st.DarkMode = on;
            if (Application.Current != null) Application.Current.RequestedThemeVariant = on ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        _s.SaveSettings();
        ApplyViewSettings();
    }

    private void ApplyViewSettings()
    {
        foreach (var t in AllTabs) t.ApplyViewSettings();
    }

    internal void ChangeFontSize(int dir) => SetFontSize(_s.Settings.FontSize + dir);

    private void SetFontSize(double points)
    {
        _s.Settings.FontSize = Math.Clamp(points, 6, 48);
        _s.SaveSettings();
        ApplyViewSettings();
        SetStatus($"Text size {_s.Settings.FontSize:0} pt.");
    }

    private void BuildLimitMenu()
    {
        var items = new List<MenuItem>();
        foreach (int n in LimitChoices)
        {
            var m = new MenuItem { Header = $"List the first {n:N0} matches", ToggleType = MenuItemToggleType.Radio, GroupName = "limit", IsChecked = _s.Settings.MaxHits == n };
            m.Click += (_, _) =>
            {
                _s.Settings.MaxHits = n;
                _s.SaveSettings();
                SetStatus($"Searches now list the first {n:N0} matches (all are still counted). It applies to the next search.");
            };
            items.Add(m);
        }
        LimitMenu.ItemsSource = items;
    }

    private void BuildPresetMenu()
    {
        var items = new List<MenuItem>();
        foreach (string name in HighlightPresets.Names)
        {
            var m = new MenuItem { Header = name };
            m.Click += async (_, _) =>
            {
                bool isPreset = HighlightPresets.Names.Any(n => SameRules(HighlightPresets.Get(n)!, _s.Settings.HighlightRules));
                if (!isPreset && _s.Settings.HighlightRules.Count > 0 &&
                    !await Dialogs.Confirm(this, "Replace the rules?", $"Replace your {_s.Settings.HighlightRules.Count} highlight rule(s) with the {name} preset?", "Replace"))
                    return;
                _s.Settings.HighlightRules = HighlightPresets.Get(name)!;
                SetOption(HighlightOpt, true);
                SetStatus($"Highlight rules: the {name} preset.");
            };
            items.Add(m);
        }
        PresetMenu.ItemsSource = items;
    }

    private static bool SameRules(List<HighlightRule> a, List<HighlightRule> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.ToString() == p.Second.ToString());

    private async void EditRules_Click(object? sender, RoutedEventArgs e)
    {
        var rules = await Dialogs.EditRules(this, _s.Settings.HighlightRules);
        if (rules == null) return;
        _s.Settings.HighlightRules = rules;
        SetOption(HighlightOpt, true);
        SetStatus($"Saved {rules.Count} highlight rule(s).");
    }

    private void BuildFontMenu()
    {
        HashSet<string> installed;
        try { installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { installed = []; }
        string defaults = new AppSettings().FontFamily;
        string first = _s.Settings.FontFamily.Split(',')[0].Trim();
        var items = new List<MenuItem>();
        var def = new MenuItem { Header = "Default (Cascadia Mono, Consolas, ...)", ToggleType = MenuItemToggleType.Radio, GroupName = "font", IsChecked = _s.Settings.FontFamily == defaults };
        def.Click += (_, _) => SetFont(defaults);
        items.Add(def);
        foreach (string f in FontChoices.Where(installed.Contains))
        {
            var m = new MenuItem { Header = f, ToggleType = MenuItemToggleType.Radio, GroupName = "font", IsChecked = _s.Settings.FontFamily != defaults && f.Equals(first, StringComparison.OrdinalIgnoreCase) };
            m.Click += (_, _) => SetFont($"{f}, {defaults}");
            items.Add(m);
        }
        FontMenu.ItemsSource = items;
    }

    private void SetFont(string families)
    {
        _s.Settings.FontFamily = families;
        _s.SaveSettings();
        ApplyViewSettings();
        SetStatus($"Font: {families.Split(',')[0]}.");
    }

    private void BuildEncodingMenu()
    {
        var items = new List<MenuItem>();
        foreach (var (label, kind) in EncodingChoices)
        {
            var m = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "encoding", Tag = kind };
            m.Click += async (_, _) =>
            {
                if (CurrentTab is not { } t) { SetStatus("Open a file first, then pick how to read it."); UpdateEncodingMenu(); return; }
                if (t.ForcedEncoding == kind) return;
                await t.ReopenAsync(kind);
                UpdateEncodingMenu();
            };
            items.Add(m);
        }
        EncodingMenu.ItemsSource = items;
        UpdateEncodingMenu();
    }

    private void UpdateEncodingMenu()
    {
        if (EncodingMenu.ItemsSource is not List<MenuItem> items) return;
        var t = CurrentTab;
        foreach (var m in items)
        {
            var kind = (TextEncodingKind?)m.Tag;
            m.IsChecked = t != null && t.ForcedEncoding == kind;
            if (kind == null) m.Header = t == null ? "Detect automatically" : $"Detect automatically (found {(t.ForcedEncoding == null ? t.Doc.Encoding.DisplayName : "...")})";
        }
    }

    private void BuildRecentMenu()
    {
        var items = new List<Control>();
        foreach (string path in _s.Settings.RecentFiles)
        {
            var m = new MenuItem { Header = new TextBlock { Text = path } }; // a TextBlock, so underscores in paths aren't access keys
            m.Click += async (_, _) =>
            {
                if (!File.Exists(path))
                {
                    _s.Settings.RecentFiles.Remove(path);
                    _s.SaveSettings();
                    BuildRecentMenu();
                    SetStatus($"{path} isn't there any more; it was taken off the recent list.", error: true);
                    return;
                }
                await OpenFileAsync(path);
            };
            items.Add(m);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
        else
        {
            items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear the list" };
            clear.Click += (_, _) => { _s.Settings.RecentFiles.Clear(); _s.SaveSettings(); BuildRecentMenu(); };
            items.Add(clear);
        }
        RecentMenu.ItemsSource = items;
    }

    private void SaveWindowState()
    {
        var st = _s.Settings;
        st.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            st.WindowWidth = Math.Round(Width);
            st.WindowHeight = Math.Round(Height);
        }
        _s.SaveSettings();
    }

    // ---------------- dialogs ----------------

    internal async Task OpenDialogAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open files (read-only)", AllowMultiple = true });
        foreach (var f in files)
        {
            string? path = f.TryGetLocalPath();
            if (path != null) await OpenFileAsync(path);
        }
    }

    private async Task GoToDialogAsync()
    {
        if (CurrentTab is not { } t) return;
        var d = t.Doc;
        string hint = $"{t.Title}: {d.LineCount:N0} lines{(t.IsIndexing ? " indexed so far" : "")}, {d.Length:N0} bytes.";
        var target = await Dialogs.AskGoTo(this, hint);
        if (target is { } g) t.GoTo(g);
    }

    private static void OpenWithShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { /* nothing useful to do; the path is shown in About */ }
    }

    // ---------------- menu clicks ----------------

    private void Open_Click(object? sender, RoutedEventArgs e) => _ = OpenDialogAsync();
    private void Reload_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab?.RefreshAsync();
    private void SaveSelection_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab?.SaveSelectionAsync();
    private void CloseTab_Click(object? sender, RoutedEventArgs e) { if (CurrentTab is { } t) _ = CloseTabAsync(t); }
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
    private void Copy_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab?.CopyAsync();
    private void CopyNumbered_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab?.CopyLinesWithNumbersAsync();
    private void SelectAll_Click(object? sender, RoutedEventArgs e) => CurrentTab?.ActiveView.SelectAll();
    private void Find_Click(object? sender, RoutedEventArgs e) => FocusSearch();
    private void FindNext_Click(object? sender, RoutedEventArgs e) => CurrentTab?.StepHit(1);
    private void FindPrevious_Click(object? sender, RoutedEventArgs e) => CurrentTab?.StepHit(-1);
    private void FilterMatching_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab?.FilterAsync(invert: false);
    private void FilterOther_Click(object? sender, RoutedEventArgs e) => _ = CurrentTab?.FilterAsync(invert: true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => CurrentTab?.CancelWork();
    private void GoTo_Click(object? sender, RoutedEventArgs e) => _ = GoToDialogAsync();
    private void Begin_Click(object? sender, RoutedEventArgs e) => CurrentTab?.GoToStart();
    private void End_Click(object? sender, RoutedEventArgs e) => CurrentTab?.GoToEnd();
    private void ToggleBookmark_Click(object? sender, RoutedEventArgs e) => CurrentTab?.ToggleBookmark();
    private void NextBookmark_Click(object? sender, RoutedEventArgs e) => CurrentTab?.StepBookmark(1);
    private void PrevBookmark_Click(object? sender, RoutedEventArgs e) => CurrentTab?.StepBookmark(-1);
    private void ClearBookmarks_Click(object? sender, RoutedEventArgs e) => CurrentTab?.ClearBookmarks();
    private void Follow_Click(object? sender, RoutedEventArgs e) => ToggleFollow();
    private void Bigger_Click(object? sender, RoutedEventArgs e) => ChangeFontSize(1);
    private void Smaller_Click(object? sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void NormalSize_Click(object? sender, RoutedEventArgs e) => SetFontSize(10);
    private void Keys_Click(object? sender, RoutedEventArgs e) => _ = Dialogs.ShowMessage(this, "Keyboard shortcuts", Dialogs.KeysText, monospace: true);
    private void OpenLog_Click(object? sender, RoutedEventArgs e)
    {
        // Notepad and Explorer run outside the Store package, so they get the real location (AppPaths.RealLocation).
        string? log = _s.Log.FilePath;
        if (log != null && File.Exists(log)) OpenWithShell(AppPaths.RealLocation(log));
        else SetStatus($"There's no log file yet ({log}). Problems are written there when they happen; start with --verbose for a full log.");
    }
    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_s.Store.Paths.ConfigDir);
        OpenWithShell(AppPaths.RealLocation(_s.Store.Paths.ConfigDir));
    }
    private void ReadMe_Click(object? sender, RoutedEventArgs e) => OpenWithShell(ReadMeUrl);
    /// <summary>The credits line in Help > About, in the user's words (2026-11-13).</summary>
    internal const string AboutCredits =
        "Developed by Richard Bejtlich. Version 1.0 programmed by Claude Sonnet 3.7. Version 2.0 programmed by Claude Opus 5.5.";
    private void About_Click(object? sender, RoutedEventArgs e) => _ = Dialogs.ShowMessage(this, "About BejView",
        $"BejView {AppInfo.Version}\n\nView and search very large text files. Read-only: it never changes a file.\n\n" +
        $"{AboutCredits}\nMIT License.\nhttps://github.com/taosecurity/BejView\n\n" +
        $"Settings: {AppPaths.RealLocation(_s.Store.Paths.SettingsFile)}\nLog: {(_s.Log.FilePath is string f ? AppPaths.RealLocation(f) : "none")}");

    /// <summary>Minimal ICommand for key bindings.</summary>
    private sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
