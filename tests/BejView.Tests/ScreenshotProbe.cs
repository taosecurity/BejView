using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using BejView.App;
using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Platform;
using BejView.Core.Settings;

namespace BejView.Tests;

/// <summary>
/// Opt-in visual check: set BJV_PROBE_DIR to a folder and run this test to save the window as PNGs (light and dark,
/// with a Papyrus-style log, a search and the filter pane). Claude can't see a real screen, so this is how the layout
/// gets looked at before a beta.
/// </summary>
public sealed class ScreenshotProbe : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [AvaloniaFact]
    public async Task RenderWindow()
    {
        string? outDir = Environment.GetEnvironmentVariable("BJV_PROBE_DIR");
        if (outDir == null) return;
        var sb = new StringBuilder();
        sb.Append("[11/11/2026 - 10:15:01AM] Papyrus log opened (PC)\n");
        for (int i = 0; i < 400; i++)
        {
            sb.Append(i % 37 == 5 ? $"[11/11/2026 - 10:15:{i % 60:00}AM] error: Cannot call GetValue() on a None object, aborting function call\n"
                : i % 23 == 9 ? $"[11/11/2026 - 10:15:{i % 60:00}AM] warning: Property Foo on script Bar attached to (0001{i:0000}) cannot be initialized\n"
                : i % 37 == 6 ? "[11/11/2026 - 10:15:02AM] Stack:\n\t[ (00012345)].SomeScript.OnInit() - \"SomeScript.psc\" Line 12\n"
                : $"[11/11/2026 - 10:15:{i % 60:00}AM] [MyMod] quest stage {i} set, timer {i * 7} ms\n");
        }
        sb.Append(new string('x', 70_000)).Append('\n');
        string path = _tmp.Write("Papyrus.0.log", Encoding.UTF8.GetBytes(sb.ToString()));

        foreach (bool dark in new[] { false, true })
        {
            var paths = new AppPaths(Path.Combine(_tmp.Path, "cfg" + dark), Path.DirectorySeparatorChar);
            var s = new AppSession(new CliOptions(), Logger.Null, new SettingsStore(paths, Logger.Null), new AppSettings { DarkMode = dark });
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var w = new MainWindow(s) { Width = 1200, Height = 760 };
            w.Show();
            var t = (await w.OpenFileAsync(path))!;
            while (t.IsIndexing) { w.OnTick(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            t.Bookmarks.Toggle(3);
            await t.StartSearchAsync("error", false, false, false);
            t.ShowLine(0, true);
            t.GoToHit(1);
            await t.FilterAsync(invert: false);
            w.OnTick();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            using var frame = w.CaptureRenderedFrame()!;
#pragma warning disable CS0618 // Bitmap.Save(string) is obsolete in Avalonia 12; fine for a probe
            frame.Save(Path.Combine(outDir, dark ? "window-dark.png" : "window-light.png"));
#pragma warning restore CS0618
            w.Close();
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }
}
