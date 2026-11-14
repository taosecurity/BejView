using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BejView.App;
using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Navigation;
using BejView.Core.Platform;
using BejView.Core.Settings;

namespace BejView.Tests;

/// <summary>
/// Opt-in timing of the real window on a big file: set BJV_BIG_FILE to a file (make one with
/// <c>bench gen FILE 10240</c>) and BJV_PROBE_DIR to a folder for bigfile.txt. Measures the first screen, indexing,
/// random jumps (each read, laid out and drawn), go to %, search and filter, and the process memory.
/// </summary>
public sealed class BigFileProbe : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [AvaloniaFact]
    public async Task OpenScrollSearchFilter()
    {
        string? file = Environment.GetEnvironmentVariable("BJV_BIG_FILE");
        string? outDir = Environment.GetEnvironmentVariable("BJV_PROBE_DIR");
        if (file == null || outDir == null) return;
        var report = new StringBuilder();
        void Note(string s) => report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{s}"));
        static long Mb() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64 / 1048576; }

        var paths = new AppPaths(Path.Combine(_tmp.Path, "cfg"), Path.DirectorySeparatorChar);
        var s = new AppSession(new CliOptions(), Logger.Null, new SettingsStore(paths, Logger.Null), new AppSettings());
        var w = new MainWindow(s) { Width = 1200, Height = 760 };
        w.Show();
        void Frame()
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Note($"File: {file} ({new FileInfo(file).Length:N0} bytes)");
        Note($"Memory before open: {Mb():N0} MB");
        var sw = Stopwatch.StartNew();
        var t = (await w.OpenFileAsync(file))!;
        while (t.Main.VisibleRows.Count == 0) { w.OnTick(); Frame(); await Task.Delay(1); }
        Note($"First screen: {sw.Elapsed.TotalMilliseconds:F0} ms (indexing {t.IndexProgress:P0} done)");
        t.GoTo(new GoToTarget(GoToKind.Line, 2_000_000));
        var pend = Stopwatch.StartNew();
        while (t.Main.Caret.Row != 1_999_999 && pend.ElapsedMilliseconds < 60_000) { w.OnTick(); Frame(); await Task.Delay(5); }
        Note($"Go to line 2,000,000 during indexing: {pend.Elapsed.TotalMilliseconds:F0} ms after asking");
        while (t.IsIndexing) { w.OnTick(); await Task.Delay(20); }
        Note($"Indexed: {sw.Elapsed.TotalSeconds:F1} s, {t.Doc.LineCount:N0} lines; memory {Mb():N0} MB");

        var r = new Random(1);
        var jumps = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            var j = Stopwatch.StartNew();
            t.Main.ScrollTo(r.NextInt64(t.Main.Count));
            Frame();
            jumps.Add(j.Elapsed.TotalMilliseconds);
        }
        jumps.Sort();
        Note($"200 random jumps (read + layout + draw): median {jumps[100]:F1} ms, 95th {jumps[190]:F1} ms, worst {jumps[^1]:F1} ms");

        var wheel = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) { t.Main.ScrollBy(3, user: true); Frame(); }
        Note($"200 wheel steps: {wheel.Elapsed.TotalMilliseconds / 200:F1} ms each");

        Assert.True(GoToTarget.TryParse("50%", out var half, out _));
        var g = Stopwatch.StartNew();
        t.GoTo(half);
        Frame();
        Note($"Go to 50%: {g.Elapsed.TotalMilliseconds:F1} ms (line {t.Main.Caret.Row + 1:N0})");

        var sr = Stopwatch.StartNew();
        string word = Environment.GetEnvironmentVariable("BJV_BIG_SEARCH") ?? "ZQXJ";
        await t.StartSearchAsync(word, false, false, false);
        Note($"Search {word}: {sr.Elapsed.TotalSeconds:F1} s, {t.LastSummary?.TotalMatches:N0} matches; memory {Mb():N0} MB");

        var fr = Stopwatch.StartNew();
        await t.FilterAsync(invert: false);
        Note($"Filter (matching) {word}: {fr.Elapsed.TotalSeconds:F1} s, {t.FilterRanges.RowCount:N0} lines; memory {Mb():N0} MB");
        var fi = Stopwatch.StartNew();
        await t.FilterAsync(invert: true);
        Note($"Filter (other lines): {fi.Elapsed.TotalSeconds:F1} s, {t.FilterRanges.RowCount:N0} lines in {t.FilterRanges.RangeCount:N0} blocks; memory {Mb():N0} MB");
        t.FilterView.ScrollTo(t.FilterView.Count / 2);
        Frame();
        Note($"Filter pane mid-list row shows file line {t.FilterView.VisibleRows.Count} rows; first {t.FilterView.RowDisplay(t.FilterView.TopRow)?.Text[..Math.Min(40, t.FilterView.RowDisplay(t.FilterView.TopRow)?.Text.Length ?? 0)]}");
        // Beta 1 test 3.7: RAM stayed up after Ctrl+W. Closing now hands the memory back (MainWindow.ReleaseMemory).
        Note($"Memory before closing the tab: {Mb():N0} MB");
        var cl = Stopwatch.StartNew();
        await w.CloseTabAsync(t);
        Frame();
        Note($"Memory just after closing the tab: {Mb():N0} MB ({cl.Elapsed.TotalMilliseconds:F0} ms to close)");
        w.Close();
        File.WriteAllText(Path.Combine(outDir, "bigfile.txt"), report.ToString());
    }
}
