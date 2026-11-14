using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Platform;
using BejView.Core.Settings;
using BejView.Core.Text;

namespace BejView.Tests;

public class CommandLineTests
{
    private static CliOptions Ok(params string[] args)
    {
        var r = CommandLine.Parse(args);
        Assert.True(r.Ok, r.Error);
        return r.Options!;
    }

    private static string Bad(params string[] args)
    {
        var r = CommandLine.Parse(args);
        Assert.False(r.Ok);
        return r.Error!;
    }

    [Fact]
    public void A_file_opens_the_window_with_options()
    {
        var o = Ok("app.log", "--line", "42", "--find=ERROR", "-f");
        Assert.Equal(Command.Gui, o.Command);
        Assert.Equal("app.log", o.File);
        Assert.Equal(42, o.Line);
        Assert.Equal("ERROR", o.Find);
        Assert.True(o.Follow);
        Assert.Equal(Command.Gui, Ok().Command);
    }

    [Fact]
    public void Find_takes_a_file_and_text_and_search_options()
    {
        var o = Ok("find", "app.log", "error", "--word", "-c", "--max", "50");
        Assert.Equal(Command.Find, o.Command);
        Assert.Equal(("app.log", "error"), (o.File, o.Find));
        Assert.True(o.WholeWord && o.CaseSensitive);
        Assert.Equal(50, o.MaxHits);

        // Text that starts with a dash goes after --.
        Assert.Equal("-x", Ok("find", "a.log", "--", "-x").Find);
    }

    [Fact]
    public void Info_paths_help_and_version()
    {
        Assert.Equal("a.log", Ok("info", "a.log").File);
        Assert.Equal(Command.Paths, Ok("paths").Command);
        Assert.Equal(Command.Help, Ok("--help").Command);
        Assert.Equal(Command.Version, Ok("--version").Command);
        Assert.Equal(TextEncodingKind.Utf16BE, Ok("info", "a.log", "--encoding", "UTF-16BE").Encoding);
    }

    [Theory]
    [InlineData("--line", "0")]
    [InlineData("a.log", "--line", "x")]
    [InlineData("--bogus")]
    [InlineData("a.log", "b.log")]
    [InlineData("find", "a.log")]
    [InlineData("info")]
    [InlineData("a.log", "--word")]
    [InlineData("a.log", "--encoding", "ebcdic")]
    [InlineData("--follow")]
    [InlineData("paths", "extra")]
    public void Mistakes_get_a_plain_message(params string[] args)
    {
        string error = Bad(args);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("Exception", error);
    }

    [Fact]
    public void Help_lists_every_exit_code()
    {
        foreach (int code in new[] { ExitCodes.Ok, ExitCodes.NoMatches, ExitCodes.BadArguments, ExitCodes.FileError, ExitCodes.SearchError })
            Assert.Contains(code.ToString(), CommandLine.HelpText);
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private SettingsStore Store(out AppPaths paths)
    {
        paths = new AppPaths(Path.Combine(_tmp.Path, "BejView"), Path.DirectorySeparatorChar);
        return new SettingsStore(paths, Logger.Null);
    }

    [Fact]
    public void First_run_imports_the_1_0_recent_files_once()
    {
        var store = Store(out var paths);
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllLines(paths.LegacyRecentFile, [@"C:\logs\newest.log", @"C:\logs\older.log", "", @"C:\logs\oldest.log"]);

        var s = store.Load();
        Assert.Equal([@"C:\logs\newest.log", @"C:\logs\older.log", @"C:\logs\oldest.log"], s.RecentFiles);
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(paths.LegacyRecentFile)); // left as it was

        File.WriteAllLines(paths.LegacyRecentFile, [@"C:\other.log"]);
        Assert.DoesNotContain(@"C:\other.log", store.Load().RecentFiles); // not imported again
    }

    [Fact]
    public void Settings_round_trip_and_bad_values_are_fixed()
    {
        var store = Store(out var paths);
        var s = new AppSettings { DarkMode = true, FontSize = 14, WordWrap = true, MaxHits = 500 };
        s.AddRecent("/var/log/a.log");
        Assert.True(store.Save(s));

        var back = store.Load();
        Assert.True(back.DarkMode && back.WordWrap);
        Assert.Equal(14, back.FontSize);
        Assert.Equal(500, back.MaxHits);
        Assert.Equal(["/var/log/a.log"], back.RecentFiles);

        File.WriteAllText(paths.SettingsFile, """{ "SchemaVersion": 2, "FontSize": 400, "MaxHits": 5, "MaxLineBytes": 1 }""");
        var fixedUp = store.Load();
        Assert.Equal(10, fixedUp.FontSize);
        Assert.Equal(10_000, fixedUp.MaxHits);
        Assert.Equal(64 * 1024, fixedUp.MaxLineBytes);
    }

    [Fact]
    public void A_broken_settings_file_is_kept_aside_and_defaults_are_used()
    {
        var store = Store(out var paths);
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(paths.SettingsFile, "{ not json");
        var s = store.Load();
        Assert.False(s.DarkMode);
        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Recent_files_keep_the_newest_first_without_repeats()
    {
        var s = new AppSettings { MaxRecent = 3 };
        foreach (string p in new[] { "a", "b", "c", "a", "d" }) s.AddRecent(p);
        Assert.Equal(["d", "a", "c"], s.RecentFiles);
    }
}

public class AppPathsTests
{
    [Fact]
    public void Windows_uses_the_1_0_folder_under_APPDATA()
    {
        var p = AppPaths.ForWindows(n => n == "APPDATA" ? @"C:\Users\rb\AppData\Roaming" : null);
        Assert.Equal(@"C:\Users\rb\AppData\Roaming\BejView", p.ConfigDir);
        Assert.Equal(@"C:\Users\rb\AppData\Roaming\BejView\recent_files.txt", p.LegacyRecentFile);
        Assert.Equal(@"C:\Users\rb\AppData\Roaming\BejView\bejview.log", p.DefaultLogFile);
    }

    [Fact]
    public void Linux_follows_XDG_config_home()
    {
        Assert.Equal("/home/rb/.config/BejView", AppPaths.ForLinux(n => n == "HOME" ? "/home/rb" : null).ConfigDir);
        Assert.Equal("/cfg/BejView", AppPaths.ForLinux(n => n switch { "HOME" => "/home/rb", "XDG_CONFIG_HOME" => "/cfg/", _ => null }).ConfigDir);
    }
}
