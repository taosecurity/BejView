using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using BejView.App;
using BejView.App.Views;
using BejView.Core.Cli;
using BejView.Core.Highlighting;
using BejView.Core.Logging;
using BejView.Core.Navigation;
using BejView.Core.Platform;
using BejView.Core.Settings;
using BejView.Core.Text;

[assembly: AvaloniaTestApplication(typeof(BejView.Tests.HeadlessApp))]

namespace BejView.Tests;

/// <summary>Headless Avalonia with real Skia rendering, so frames can be checked pixel by pixel.</summary>
public sealed class HeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Drives the real BejView window: open, scroll, search, highlight, follow, filter, tabs, encodings, copy and save.</summary>
public sealed class UiTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly List<MainWindow> _windows = [];

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
        _tmp.Dispose();
    }

    private AppSession Session(Action<AppSettings>? tweak = null, CliOptions? options = null)
    {
        var paths = new AppPaths(Path.Combine(_tmp.Path, "cfg"), Path.DirectorySeparatorChar);
        var st = new AppSettings();
        tweak?.Invoke(st);
        return new AppSession(options ?? new CliOptions(), Logger.Null, new SettingsStore(paths, Logger.Null), st);
    }

    private MainWindow Window(AppSession s)
    {
        var w = new MainWindow(s) { Width = 1000, Height = 700 };
        w.Show();
        _windows.Add(w);
        return w;
    }

    /// <summary>Runs the window's ticks and the dispatcher until <paramref name="done"/> holds.</summary>
    private static async Task Until(MainWindow w, Func<bool> done, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("The window didn't get there in time.");
            w.OnTick();
            Settle(w);
            await Task.Delay(10);
        }
        w.OnTick();
        Settle(w);
    }

    /// <summary>Runs pending work, layout and one render, as a real window would between frames.</summary>
    private static void Settle(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Task Indexed(MainWindow w, DocumentTab t) => Until(w, () => !t.IsIndexing);

    private string Lines(string name, int count, Func<int, string>? text = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(text?.Invoke(i) ?? $"line {i:000000} INFO nothing to see").Append('\n');
        return _tmp.Write(name, Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private async Task<(MainWindow W, DocumentTab T)> Open(string path, Action<AppSettings>? tweak = null)
    {
        var w = Window(Session(tweak));
        var t = await w.OpenFileAsync(path);
        Assert.NotNull(t);
        await Indexed(w, t);
        return (w, t);
    }

    // ---------------- opening and scrolling ----------------

    [AvaloniaFact]
    public async Task Opens_shows_the_first_lines_and_indexes_in_the_background()
    {
        string p = Lines("big.log", 300_000);
        var w = Window(Session());
        var t = (await w.OpenFileAsync(p))!;
        Dispatcher.UIThread.RunJobs();
        // The first screen is there straight away, whatever the indexer has reached.
        await Until(w, () => t.Main.VisibleRows.Count > 0, 5000);
        Assert.Equal("line 000000 INFO nothing to see", t.Main.RowDisplay(0)!.Value.Text);
        await Indexed(w, t);
        Assert.Equal(300_000, t.Main.Count);
        Assert.Contains("300,000 lines", w.FindControl<TextBlock>("LinesText")!.Text);
        Assert.Contains("UTF-8", w.FindControl<TextBlock>("EncodingText")!.Text);
    }

    [AvaloniaFact]
    public async Task Scrolling_by_wheel_keys_and_jumps_shows_the_right_lines()
    {
        var (w, t) = await Open(Lines("s.log", 5000));
        var v = t.Main;
        int rows = v.FullRows;
        Assert.True(rows > 10);

        v.ScrollTo(1234);
        Assert.Equal(1234, v.VisibleRows[0].Row);
        Assert.Equal("line 001234 INFO nothing to see", v.RowDisplay(1234)!.Value.Text);

        // Mouse wheel: 3 rows per notch.
        var centre = v.TranslatePoint(new Point(200, 100), w)!.Value;
        w.MouseWheel(centre, new Vector(0, -2));
        Assert.Equal(1240, v.TopRow);

        // Keys: Ctrl+End, Ctrl+Home, Page Down.
        v.Focus();
        w.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
        Assert.True(v.IsAtEnd);
        Assert.Equal(4999, v.VisibleRows[^1].Row);
        w.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.Home, null);
        Assert.Equal(0, v.TopRow);
        Settle(w);
        int page = v.FullRows - 1;
        w.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        Assert.Equal(page, v.TopRow);

        // 1.0's Ctrl+E / Ctrl+B.
        w.KeyPress(Key.E, RawInputModifiers.Control, PhysicalKey.E, "e");
        Assert.True(v.IsAtEnd);
        w.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b");
        Assert.Equal(0, v.TopRow);
    }

    [AvaloniaFact]
    public async Task Go_to_line_percent_and_byte_offset()
    {
        var (_, t) = await Open(Lines("g.log", 1000)); // 32 bytes a line
        t.GoTo(new GoToTarget(GoToKind.Line, 500));
        Assert.Equal(499, t.Main.Caret.Row);
        Assert.Contains(t.Main.VisibleRows, r => r.Row == 499);
        Assert.True(GoToTarget.TryParse("25%", out var pct, out _));
        t.GoTo(pct);
        Assert.Equal(250, t.Main.Caret.Row);
        Assert.True(GoToTarget.TryParse("@" + (32 * 700 + 5), out var off, out _));
        t.GoTo(off);
        Assert.Equal(700, t.Main.Caret.Row);
    }

    [AvaloniaFact]
    public async Task Long_lines_scroll_sideways_or_wrap_and_capped_lines_say_they_continue()
    {
        string p = Lines("long.log", 20, i => i == 3 ? new string('x', 10_000) : i == 5 ? "wrap " + new string('y', 600) : $"short {i}");
        var (w, t) = await Open(p, s => s.MaxLineBytes = 4096);
        var v = t.Main;
        var capped = v.RowDisplay(3)!.Value.Text;
        Assert.StartsWith(new string('x', 4096), capped);
        Assert.Contains("line continues", capped);

        // Without wrap the long line scrolls sideways.
        v.SetCaret(new TextPos(5, 600));
        Assert.True(v.HorizontalOffset > 0);

        // With wrap it takes several rows and there is no sideways scrolling.
        w.SetOption(w.FindControl<MenuItem>("WrapOpt")!, true);
        Dispatcher.UIThread.RunJobs();
        v.ScrollTo(5); // line 3 (4 KB) now wraps into dozens of rows
        var row5 = v.VisibleRows.First(r => r.Row == 5);
        Assert.True(row5.Height > v.LineHeight * 3, $"wrapped height {row5.Height}");
        Assert.Equal(0, v.HorizontalOffset);
    }

    // ---------------- search and highlights ----------------

    [AvaloniaFact]
    public async Task Search_panel_finds_counts_steps_and_highlights_the_match_itself()
    {
        string p = Lines("h.log", 400, i => i % 40 == 7 ? $"row {i:000} has a NEEDLE in it" : $"row {i:000} plain");
        var (w, t) = await Open(p);
        t.SearchBox.Text = "needle";
        t.SearchBox.Focus();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(w, () => !t.IsSearching && t.LastSummary != null);

        Assert.Equal(10, t.Hits.Count);
        Assert.Contains("10 matches on 10 lines", t.SearchStatusText);
        Assert.Equal("Results (10 lines)", t.ResultsTab.Header);
        Assert.True(t.BottomVisible);
        Assert.Equal(10, t.Results.Count);
        Assert.Equal(0, t.CurrentHit);
        Assert.Equal(7, t.Main.Caret.Row);

        // Ctrl+N and Ctrl+P (1.0's keys) step through the matches.
        w.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, "n");
        Assert.Equal(1, t.CurrentHit);
        Assert.Equal(47, t.Main.Caret.Row);
        w.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");
        Assert.Equal(0, t.CurrentHit);

        // The highlight sits exactly on "NEEDLE": columns 13-18 of "row 007 has a NEEDLE in it".
        Settle(w);
        var rects = t.Main.MatchRects(7);
        var r = Assert.Single(rects);
        double charW = r.Width / 6;
        var area = t.Main.TextAreaForTests;
        Assert.InRange(r.X - area.X, 14 * charW - 1, 14 * charW + 1);

        // And it's drawn there: the current-match colour inside, the plain background just before it.
        var pal = t.Main.CurrentPalette;
        var inside = Pixel(w, t.Main, new Point(r.X + 1, r.Y + 1));
        var before = Pixel(w, t.Main, new Point(r.X - charW * 0.5, r.Y + 1));
        Assert.Equal(Rgb(pal.CurrentMatch), inside);
        Assert.NotEqual(Rgb(pal.CurrentMatch), before);
    }

    [AvaloniaFact]
    public async Task Highlights_land_on_the_right_characters_after_accents_and_emoji()
    {
        string p = Lines("u.log", 3, i => i == 1 ? "é€😀 x NEEDLE" : "nothing");
        var (w, t) = await Open(p);
        await t.StartSearchAsync("NEEDLE", true, false, false);
        Settle(w);
        var hit = Assert.Single(t.Hits);
        Assert.Equal(7, hit.Column); // é, €, 😀 (2 UTF-16 units), space, x, space
        var rect = Assert.Single(t.Main.MatchRects(1));
        // The match rectangle starts after the "x " (it doesn't cover the emoji or the x).
        var pal = t.Main.CurrentPalette;
        Assert.Equal(Rgb(pal.CurrentMatch), Pixel(w, t.Main, new Point(rect.X + 1, rect.Y + 1)));
        Assert.NotEqual(Rgb(pal.CurrentMatch), Pixel(w, t.Main, new Point(rect.X - 3, rect.Y + 1)));
        Assert.Equal(Rgb(pal.CurrentMatch), Pixel(w, t.Main, new Point(rect.Right - 2, rect.Y + 1)));
    }

    [AvaloniaFact]
    public async Task Search_errors_are_shown_in_plain_words_and_the_limit_is_respected()
    {
        string p = Lines("e.log", 500, i => $"hit {i}");
        var (w, t) = await Open(p, s => s.MaxHits = 100);
        t.RegexToggle.IsChecked = true;
        await t.StartSearchAsync("(unclosed", false, false, true);
        Assert.Contains("isn't a valid regular expression", t.SearchStatusText);
        Assert.Contains("turn off Regex", t.SearchStatusText);
        Assert.Contains("turn off Regex", w.StatusMessage);

        await t.StartSearchAsync("hit", false, false, false);
        Assert.Equal(100, t.Hits.Count);
        Assert.Equal(500, t.LastSummary!.TotalMatches);
        Assert.Contains("500 matches in", t.SearchStatusText);
        Assert.Contains("the first 100 (on 100 lines) are listed", t.SearchStatusText);
    }

    [AvaloniaFact]
    public async Task Over_the_limit_the_line_count_is_for_the_listed_part_and_the_tab_counts_lines()
    {
        // Beta 1 said "1,940,400 matches on 9,005+ lines": right, but it read as if every match was on 9,005 lines.
        string p = Lines("two.log", 500, i => i < 20 ? $"hit {i} and hit again, pair pair" : $"hit {i} and hit again");
        var (w, t) = await Open(p, s => s.MaxHits = 100);
        await t.StartSearchAsync("hit", false, false, false);
        Assert.Equal(1000, t.LastSummary!.TotalMatches);
        Assert.Equal(100, t.Hits.Count);
        Assert.Equal("1,000 matches", t.SearchStatusText[..t.SearchStatusText.IndexOf(" in ")]);
        Assert.Contains("the first 100 (on 50 lines) are listed", t.SearchStatusText);
        Assert.Equal(50, t.Results.Count);
        Assert.Equal("Results (50 lines)", t.ResultsTab.Header);

        // Under the limit: matches and their lines, and the tab still counts lines.
        await t.StartSearchAsync("pair", false, false, false);
        Assert.StartsWith("40 matches on 20 lines in ", t.SearchStatusText);
        Assert.Equal("Results (20 lines)", t.ResultsTab.Header);
    }

    [Fact]
    public void Search_summary_wording()
    {
        var s = TimeSpan.FromSeconds(3.71);
        Assert.Equal("No matches for x.", DocumentTab.SearchSummaryText("x", 0, 0, 0, false, s));
        Assert.Equal("1 match on 1 line in 3.7 s", DocumentTab.SearchSummaryText("x", 1, 1, 1, false, s));
        Assert.Equal("1,940,400 matches in 3.7 s; the first 10,000 (on 9,005 lines) are listed (Edit > Result limit)",
            DocumentTab.SearchSummaryText("x", 1_940_400, 10_000, 9_005, true, s));
    }

    [AvaloniaFact]
    public async Task A_search_can_be_cancelled()
    {
        string p = Lines("c.log", 400_000);
        var (w, t) = await Open(p);
        t.SearchBox.Text = "[a-z]+ [a-z]+$";
        t.RegexToggle.IsChecked = true;
        var run = t.SearchAsync();
        t.CancelWork();
        await run;
        Assert.False(t.IsSearching);
        Assert.True(t.SearchStatusText.Contains("stopped") || t.SearchStatusText.Contains("matches"), t.SearchStatusText);
    }

    [AvaloniaFact]
    public async Task Papyrus_lines_are_coloured_by_the_preset()
    {
        string p = Lines("Papyrus.0.log", 4, i => i switch
        {
            0 => "[11/11/2026 - 10:15:01AM] Papyrus log opened (PC)",
            1 => "[11/11/2026 - 10:15:02AM] error: Cannot call Foo() on a None object",
            2 => "[11/11/2026 - 10:15:02AM] warning: Property Bar not found",
            _ => "[11/11/2026 - 10:15:03AM] [MyMod] hello",
        });
        var (w, t) = await Open(p);
        var pal = t.Main.CurrentPalette;
        Assert.Same(pal.Highlights[HighlightColor.Blue], t.Main.RowDisplay(0)!.Value.Brush);
        Assert.Same(pal.Highlights[HighlightColor.Red], t.Main.RowDisplay(1)!.Value.Brush);
        Assert.Same(pal.Highlights[HighlightColor.Amber], t.Main.RowDisplay(2)!.Value.Brush);
        Assert.Null(t.Main.RowDisplay(3)!.Value.Brush);

        w.SetOption(w.FindControl<MenuItem>("HighlightOpt")!, false);
        Assert.Null(t.Main.RowDisplay(1)!.Value.Brush);
    }

    // ---------------- follow ----------------

    [AvaloniaFact]
    public async Task Follow_mode_shows_new_lines_and_pauses_when_scrolled_up()
    {
        string path = Path.Combine(_tmp.Path, "live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        void Append(int from, int n)
        {
            var sb = new StringBuilder();
            for (int i = from; i < from + n; i++) sb.Append($"live {i}\n");
            writer.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            writer.Flush();
        }
        Append(0, 200);
        var (w, t) = await Open(path);
        t.SetFollow(true);
        Assert.True(t.Main.IsAtEnd);

        Append(200, 100);
        await Until(w, () => t.Main.Count == 300);
        Assert.True(t.Main.IsAtEnd);
        Assert.Equal(299, t.Main.VisibleRows[^1].Row);
        Assert.Equal("Following", w.FindControl<TextBlock>("FollowText")!.Text);

        // Scrolling up pauses the auto-scroll; new lines still arrive.
        var at = t.Main.TranslatePoint(new Point(200, 100), w)!.Value;
        w.MouseWheel(at, new Vector(0, 20));
        Assert.False(t.AutoScroll);
        long top = t.Main.TopRow;
        Append(300, 50);
        await Until(w, () => t.Main.Count == 350);
        Assert.Equal(top, t.Main.TopRow);
        Assert.Contains("paused", w.FindControl<TextBlock>("FollowText")!.Text);

        // Ctrl+E resumes.
        t.Main.Focus();
        w.KeyPress(Key.E, RawInputModifiers.Control, PhysicalKey.E, "e");
        Assert.True(t.AutoScroll);
        Append(350, 10);
        await Until(w, () => t.Main.Count == 360);
        Assert.True(t.Main.IsAtEnd);
        Assert.Equal(359, t.Main.VisibleRows[^1].Row);
    }

    [AvaloniaFact]
    public async Task A_log_that_is_replaced_is_read_again()
    {
        string path = Lines("rot.log", 100, i => $"old {i}");
        var (w, t) = await Open(path);
        t.SetFollow(true);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.Write("new 0\nnew 1\n"u8);
        await Until(w, () => t.Main.Count == 2);
        Assert.Equal("new 0", t.Main.RowDisplay(0)!.Value.Text);
        Assert.Contains("read again", w.StatusMessage);
    }

    // ---------------- filter ----------------

    [AvaloniaFact]
    public async Task Filter_pane_shows_matching_or_other_lines_and_clicking_one_jumps_there()
    {
        string p = Lines("f.log", 1000, i => i % 100 == 42 ? $"{i} ERROR bad" : $"{i} fine");
        var (w, t) = await Open(p);
        t.SearchBox.Text = "error";
        await t.FilterAsync(invert: false);
        Settle(w);
        Assert.Equal(10, t.FilterView.Count);
        Assert.Equal("142 ERROR bad", t.FilterView.RowDisplay(1)!.Value.Text);
        Assert.Contains("10 of 1,000 lines match", t.FilterStatusText);

        // A click on a filter line shows that line in the main view.
        Settle(w);
        var row = t.FilterView.VisibleRows.First(r => r.Row == 3);
        var pt = t.FilterView.TranslatePoint(new Point(t.FilterView.TextAreaForTests.X + 20, row.Y + row.Height / 2), w)!.Value;
        w.MouseDown(pt, MouseButton.Left);
        w.MouseUp(pt, MouseButton.Left);
        Assert.Equal(342, t.Main.Caret.Row);
        Assert.Contains(t.Main.VisibleRows, r => r.Row == 342);

        await t.FilterAsync(invert: true);
        Settle(w);
        Assert.Equal(990, t.FilterView.Count);
        t.FilterView.ScrollTo(40);
        Assert.Equal("41 fine", t.FilterView.RowDisplay(41)!.Value.Text);
        Assert.Equal("43 fine", t.FilterView.RowDisplay(42)!.Value.Text);
    }

    // ---------------- copy (beta 1 test 8.5: "only copies one line, not three") ----------------

    [AvaloniaFact]
    public async Task Ctrl_C_copies_every_selected_line_after_a_drag_or_shift_down()
    {
        string p = Lines("copy.log", 60, i => i % 10 == 5 ? $"line {i} error" : $"line {i}");
        var (w, t) = await Open(p);
        // As in the test plan: a search first, and a click in the Results pane.
        await t.StartSearchAsync("error", false, false, false);
        Settle(w);
        var r = t.Results.VisibleRows.First();
        var rp = t.Results.TranslatePoint(new Point(t.Results.TextAreaForTests.X + 20, r.Y + r.Height / 2), w)!.Value;
        w.MouseDown(rp, MouseButton.Left);
        w.MouseUp(rp, MouseButton.Left);
        Settle(w);

        // Drag from the start of one line to the end of the line two below it.
        var rows = t.Main.VisibleRows;
        var a = rows[2];
        var b = rows[4];
        var area = t.Main.TextAreaForTests;
        var from = t.Main.TranslatePoint(new Point(area.X + 1, a.Y + a.Height / 2), w)!.Value;
        var to = t.Main.TranslatePoint(new Point(area.Right - 5, b.Y + b.Height / 2), w)!.Value;
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point(from.X + 30, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
        w.MouseMove(to, RawInputModifiers.LeftMouseButton);
        w.MouseUp(to, MouseButton.Left);
        Settle(w);
        Assert.True(t.Main.HasSelection);
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        string? clip = await Clip(w);
        Assert.Equal(3, clip!.Split(Environment.NewLine).Length);
        Assert.Contains("copied 3 line(s)", w.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Shift+Down twice from a click.
        w.MouseDown(from, MouseButton.Left);
        w.MouseUp(from, MouseButton.Left);
        w.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        w.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        w.KeyPress(Key.End, RawInputModifiers.Shift, PhysicalKey.End, null);
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        clip = await Clip(w);
        Assert.Equal(3, clip!.Split(Environment.NewLine).Length);
    }

    // ---------------- closing a tab frees its memory (beta 1 test 3.7: "RAM stayed at 338 MB") ----------------

    [AvaloniaFact]
    public async Task A_closed_tab_and_its_document_are_freed()
    {
        string p = Lines("free.log", 200_000);
        var w = Window(Session());
        var (tab, doc) = await OpenSearchFilterClose(w, p);
        // The helper's own state (which holds the tab) is cleared only once this continuation has let it finish.
        await Task.Yield();
        for (int i = 0; i < 5 && (tab.IsAlive || doc.IsAlive); i++)
        {
            Settle(w);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(doc.IsAlive, "the closed tab's TextDocument is still referenced");
        Assert.False(tab.IsAlive, "the closed DocumentTab is still referenced");
    }

    [AvaloniaFact]
    public async Task Closing_a_tab_frees_it_with_the_apps_own_collection()
    {
        // Beta 2 on the 50 GB dump: RAM fell only when a second tab was closed, because the app's one collection ran
        // while the closed tab was still held (by a closure in CloseTabAsync). So look at the moment that collection
        // ends, with no GC.Collect of our own: a chance collection in between can't hide the fault.
        string p = Lines("own.log", 200_000);
        var w = Window(Session());
        bool? docAlive = null, tabAlive = null;
        WeakReference? tab = null, doc = null;
        w.MemoryReleased += () => { if (doc != null) { docAlive = doc.IsAlive; tabAlive = tab!.IsAlive; } };
        (tab, doc) = await OpenSearchFilterClose(w, p, settleAfterClose: false);
        await Task.Yield();
        for (int i = 0; i < 40 && docAlive == null; i++)
        {
            Settle(w);
            await Task.Delay(50);
        }
        Assert.NotNull(docAlive); // the collection ran
        Assert.False(docAlive, "the closed tab's TextDocument survived the app's collection");
        Assert.False(tabAlive, "the closed DocumentTab survived the app's collection");
    }

    // settleAfterClose false: leave the window's queued jobs (the app's own collection) to run after this method's
    // state, which holds the tab, is gone, as they do after Ctrl+W in the real app.
    private async Task<(WeakReference Tab, WeakReference Doc)> OpenSearchFilterClose(MainWindow w, string path, bool settleAfterClose = true)
    {
        var t = (await w.OpenFileAsync(path))!;
        await Indexed(w, t);
        await t.StartSearchAsync("INFO", false, false, false);
        t.SearchBox.Text = "000";
        await t.FilterAsync(invert: false);
        t.Main.Focus();
        Settle(w);
        var refs = (new WeakReference(t), new WeakReference(t.Doc));
        await w.CloseTabAsync(t);
        if (settleAfterClose) Settle(w);
        return refs;
    }

    private static async Task<string?> Clip(MainWindow w)
    {
        Settle(w);
        for (int i = 0; i < 50; i++)
        {
            var s = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(w.Clipboard!);
            if (s != null) return s;
            Settle(w);
        }
        return null;
    }

    // ---------------- tabs, encodings, bookmarks ----------------

    [AvaloniaFact]
    public async Task Tabs_open_once_per_file_and_close()
    {
        string a = Lines("a.log", 10), b = Lines("b.log", 20);
        var s = Session();
        var w = Window(s);
        var ta = await w.OpenFileAsync(a);
        var tb = await w.OpenFileAsync(b);
        Assert.Equal(2, w.AllTabs.Count());
        Assert.Same(tb, w.CurrentTab);
        Assert.Same(ta, await w.OpenFileAsync(a)); // the same file again: its tab comes forward
        Assert.Same(ta, w.CurrentTab);
        Assert.Equal(2, w.AllTabs.Count());
        await w.CloseTabAsync(ta!);
        Assert.Same(tb, Assert.Single(w.AllTabs));
        // Recent files remember both, newest first.
        Assert.Equal([Path.GetFullPath(b), Path.GetFullPath(a)], s.Settings.RecentFiles);
    }

    [AvaloniaFact]
    public async Task A_missing_file_gives_a_message_with_the_fix()
    {
        var w = Window(Session());
        var t = await OpenHidden(w, Path.Combine(_tmp.Path, "nope.log"));
        Assert.Null(t);
        Assert.Contains("doesn't exist", w.StatusMessage);
    }

    private static async Task<DocumentTab?> OpenHidden(MainWindow w, string path)
    {
        w.Hide(); // no dialog while hidden
        try { return await w.OpenFileAsync(path); }
        finally { w.Show(); }
    }

    [AvaloniaFact]
    public async Task Encoding_menu_reopens_the_file_with_a_forced_encoding()
    {
        string p = _tmp.Write("w.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("première ligne\r\nzweite Zeile\r\n")]);
        var (w, t) = await Open(p);
        Assert.Equal(TextEncodingKind.Utf16LE, t.Doc.Encoding.Kind);
        Assert.Equal("première ligne", t.Main.RowDisplay(0)!.Value.Text);

        await t.ReopenAsync(TextEncodingKind.Latin1);
        await Indexed(w, t);
        Assert.Equal(TextEncodingKind.Latin1, t.Doc.Encoding.Kind);
        Assert.Equal(TextEncodingKind.Latin1, t.ForcedEncoding);
        Assert.Contains("(chosen)", w.FindControl<TextBlock>("EncodingText")!.Text);

        await t.ReopenAsync(null);
        await Indexed(w, t);
        Assert.Equal(TextEncodingKind.Utf16LE, t.Doc.Encoding.Kind);
        Assert.Equal("zweite Zeile", t.Main.RowDisplay(1)!.Value.Text);
    }

    [AvaloniaFact]
    public async Task Bookmarks_toggle_and_step_with_the_keys()
    {
        var (w, t) = await Open(Lines("b.log", 500));
        t.Main.Focus();
        t.ShowLine(100, true);
        w.KeyPress(Key.F2, RawInputModifiers.Control, PhysicalKey.F2, null);
        t.ShowLine(300, true);
        w.KeyPress(Key.F2, RawInputModifiers.Control, PhysicalKey.F2, null);
        t.ShowLine(0, true);
        w.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Assert.Equal(100, t.Main.Caret.Row);
        w.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Assert.Equal(300, t.Main.Caret.Row);
        w.KeyPress(Key.F2, RawInputModifiers.Shift, PhysicalKey.F2, null);
        Assert.Equal(100, t.Main.Caret.Row);
        Assert.Equal(2, t.Bookmarks.Count);
    }

    // ---------------- copy and save ----------------

    [AvaloniaFact]
    public async Task Selection_copies_as_text_and_saves_the_exact_bytes()
    {
        string p = _tmp.Write("sel.log", Encoding.UTF8.GetBytes("zero\r\ncafé one\r\ntwo\r\nthree\r\n"));
        var (w, t) = await Open(p);
        var v = t.Main;
        v.SetCaret(new TextPos(1, 2));
        v.SetCaret(new TextPos(3, 2), extend: true);
        Assert.Equal("fé one" + Environment.NewLine + "two" + Environment.NewLine + "th", v.SelectedText(1000, out _));
        Assert.Equal("2: café one" + Environment.NewLine + "3: two" + Environment.NewLine + "4: three" + Environment.NewLine, v.SelectedLinesWithNumbers(100));

        string outPath = Path.Combine(_tmp.Path, "out.log");
        Assert.True(await t.SaveSelectionToAsync(v, outPath));
        Assert.Equal(Encoding.UTF8.GetBytes("fé one\r\ntwo\r\nth"), File.ReadAllBytes(outPath));

        // Never over the file being shown.
        Assert.False(await t.SaveSelectionToAsync(v, p));
        Assert.Contains("never changes the files it shows", w.StatusMessage);
        Assert.Equal("zero\r\ncafé one\r\ntwo\r\nthree\r\n", File.ReadAllText(p));
    }

    [AvaloniaFact]
    public async Task Saving_a_selection_from_the_filter_pane_keeps_only_those_lines()
    {
        string p = Lines("fs.log", 50, i => i % 10 == 0 ? $"keep {i}" : $"drop {i}");
        var (_, t) = await Open(p);
        t.SearchBox.Text = "keep";
        await t.FilterAsync(false);
        t.FilterView.Focus();
        t.FilterView.SelectAll();
        string outPath = Path.Combine(_tmp.Path, "kept.log");
        Assert.True(await t.SaveSelectionToAsync(t.FilterView, outPath));
        Assert.Equal("keep 0\nkeep 10\nkeep 20\nkeep 30\nkeep 40", File.ReadAllText(outPath));
    }

    // ---------------- settings, theme and the command line ----------------

    [AvaloniaFact]
    public async Task Theme_font_and_wrap_are_saved_and_dark_mode_changes_the_view()
    {
        var s = Session();
        var w = Window(s);
        var t = (await w.OpenFileAsync(Lines("t.log", 10)))!;
        await Indexed(w, t);
        w.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
        Dispatcher.UIThread.RunJobs();
        Assert.True(s.Settings.DarkMode);
        Assert.Same(Palette.Dark, t.Main.CurrentPalette);
        w.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal, "=");
        Assert.Equal(11, s.Settings.FontSize);
        var saved = new SettingsStore(s.Store.Paths, Logger.Null).Load();
        Assert.True(saved.DarkMode);
        Assert.Equal(11, saved.FontSize);
        Assert.Contains(Path.GetFullPath(t.FilePath), saved.RecentFiles);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }

    [AvaloniaFact]
    public void First_run_shows_the_recent_files_imported_from_1_0()
    {
        string cfg = Path.Combine(_tmp.Path, "cfg");
        Directory.CreateDirectory(cfg);
        File.WriteAllLines(Path.Combine(cfg, AppPaths.LegacyRecentFileName), [@"C:\logs\one.log", @"C:\logs\two.log"]);
        var store = new SettingsStore(new AppPaths(cfg, Path.DirectorySeparatorChar), Logger.Null);
        var s = new AppSession(new CliOptions(), Logger.Null, store, store.Load());
        var w = Window(s);
        var recent = w.FindControl<MenuItem>("RecentMenu")!;
        var headers = ((IEnumerable<Control>)recent.ItemsSource!).OfType<MenuItem>().Select(m => (m.Header as TextBlock)?.Text).ToList();
        Assert.Equal(@"C:\logs\one.log", headers[0]);
        Assert.Equal(@"C:\logs\two.log", headers[1]);
    }

    [AvaloniaFact]
    public async Task Command_line_line_find_and_follow_are_applied()
    {
        string p = Lines("cl.log", 2000, i => i == 1500 ? "the TARGET line" : $"line {i}");
        var parsed = CommandLine.Parse([p, "--line", "900", "--find", "TARGET", "--follow"]);
        Assert.True(parsed.Ok, parsed.Error);
        var w = Window(Session(options: parsed.Options));
        w.OpenFromCommandLine();
        await Until(w, () => w.CurrentTab is { IsIndexing: false, IsSearching: false, LastSummary: not null });
        var t = w.CurrentTab!;
        Assert.True(t.Following);
        Assert.Single(t.Hits);
        Assert.Equal(1500, t.Hits[0].Line);
        Assert.Equal(1500, t.Main.Caret.Row);
    }

    // ---------------- pixels ----------------

    private static (byte R, byte G, byte B) Rgb(IBrush b)
    {
        var c = ((ISolidColorBrush)b).Color;
        return (c.R, c.G, c.B);
    }

    /// <summary>The colour drawn at a point of <paramref name="control"/> in the window's last frame.</summary>
    private static unsafe (byte R, byte G, byte B) Pixel(MainWindow w, Control control, Point p)
    {
        var at = control.TranslatePoint(p, w)!.Value;
        using var frame = w.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        double scale = frame.PixelSize.Width / w.Bounds.Width;
        int x = (int)(at.X * scale), y = (int)(at.Y * scale);
        byte* q = (byte*)buffer.Address + y * buffer.RowBytes + x * 4;
        bool rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        return rgba ? (q[0], q[1], q[2]) : (q[2], q[1], q[0]);
    }
}
