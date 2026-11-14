using System.Text;
using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Settings;
using BejView.Core.Highlighting;
using BejView.Core.Platform;

namespace BejView.Tests;

/// <summary>The info and find commands, run through the same parser the exe uses.</summary>
public sealed class CliCommandTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var parsed = CommandLine.Parse(args);
        Assert.True(parsed.Ok, parsed.Error);
        var o = parsed.Options!;
        var output = new StringWriter();
        var error = new StringWriter();
        int code = o.Command switch
        {
            Command.Info => await CliCommands.InfoAsync(o, output, error, Logger.Null, Ct),
            Command.Find => await CliCommands.FindAsync(o, output, error, Logger.Null, Ct),
            _ => throw new InvalidOperationException(),
        };
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private string Log() => _tmp.Write("app.log", Encoding.UTF8.GetBytes(
        "starting up\nERROR: disk full\nall fine\nerror again, and Error once more\ncafé ERROR here\nlast line no break"));

    [Fact]
    public async Task Info_prints_size_encoding_and_lines()
    {
        string p = Log();
        var (code, output, _) = await Run("info", p);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("Lines:      6", output);
        Assert.Contains("Encoding:   UTF-8", output);
        Assert.Contains("no line break at the end", output);
        Assert.Contains($"Size:       {new FileInfo(p).Length:N0} bytes", output);
    }

    [Fact]
    public async Task Info_on_a_missing_file_says_so_and_exits_3()
    {
        var (code, _, err) = await Run("info", Path.Combine(_tmp.Path, "nope.log"));
        Assert.Equal(ExitCodes.FileError, code);
        Assert.Contains("doesn't exist", err);
        Assert.Contains("Check the path", err);
    }

    [Fact]
    public async Task Find_prints_grep_style_line_and_column_once_per_line()
    {
        var (code, output, err) = await Run("find", Log(), "error");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("2:1: ERROR: disk full\n4:1: error again, and Error once more\n5:6: café ERROR here\n", output);
        Assert.Equal("", err);
    }

    [Fact]
    public async Task Find_shows_control_characters_as_codes_so_a_file_cant_drive_the_terminal()
    {
        // ESC [ 31m (colour), OSC 0 ; title BEL (window title), a bare CR (hides the start), DEL and C1 CSI (U+009B).
        string p = _tmp.Write("evil.log", Encoding.UTF8.GetBytes(
            "ok\nhit \u001b[31mred\u001b]0;pwned\u0007\rcover\ttab \u007f\u009b2J\nfine\n"));
        var (code, output, _) = await Run("find", p, "hit");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("2:1: hit \\x1B[31mred\\x1B]0;pwned\\x07\\x0Dcover\ttab \\x7F\\x9B2J\n", output);
        Assert.DoesNotContain('\u001b', output);
    }

    [Fact]
    public void TerminalText_leaves_ordinary_text_alone()
    {
        const string s = "café ERROR\there 日本 é";
        Assert.Same(s, TerminalText.Safe(s));
        Assert.Equal("a\\x00b", TerminalText.Safe("a\0b"));
    }

    [Fact]
    public async Task Find_honours_case_word_and_regex()
    {
        string p = Log();
        Assert.Equal("2:1: ERROR: disk full\n5:6: café ERROR here\n", (await Run("find", p, "ERROR", "--case")).Out);
        Assert.Equal("4:18: error again, and Error once more\n", (await Run("find", p, "E[a-z]+r", "--regex", "--case")).Out);
        Assert.Equal("", (await Run("find", p, "rro", "--word")).Out);
    }

    [Fact]
    public async Task Find_count_counts_every_match()
    {
        var (code, output, _) = await Run("find", Log(), "error", "--count");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("4\n", output);
    }

    [Fact]
    public async Task Find_with_a_limit_says_how_many_were_left_out()
    {
        var (code, output, err) = await Run("find", Log(), "error", "--max", "2");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("2:1: ERROR: disk full\n4:1: error again, and Error once more\n", output);
        Assert.Contains("first 2 of 4 matches", err);
    }

    [Fact]
    public async Task Find_with_nothing_found_exits_1_like_grep()
    {
        var (code, output, _) = await Run("find", Log(), "zebra");
        Assert.Equal(ExitCodes.NoMatches, code);
        Assert.Equal("", output);
    }

    [Fact]
    public async Task Find_with_a_bad_regex_explains_and_exits_4()
    {
        var (code, _, err) = await Run("find", Log(), "([a-", "--regex");
        Assert.Equal(ExitCodes.SearchError, code);
        Assert.Contains("isn't a valid regular expression", err);
        Assert.Contains("turn off Regex", err);
    }

    [Fact]
    public async Task Find_reads_UTF16_with_the_right_columns()
    {
        string p = _tmp.Write("u16.log", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("ééé hit\r\nno\r\n€ hit hit\r\n")]);
        var (code, output, _) = await Run("find", p, "hit");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("1:5: ééé hit\n3:3: € hit hit\n", output);
    }

    [Fact]
    public async Task Find_with_a_forced_encoding_uses_it()
    {
        // Latin-1 bytes for "café hit": read as UTF-8 the é would be invalid; forced Latin-1 shows it.
        string p = _tmp.Write("l1.log", [.. Encoding.Latin1.GetBytes("café hit\n")]);
        var (_, output, _) = await Run("find", p, "hit", "--encoding", "latin1");
        Assert.Equal("1:6: café hit\n", output);
    }

    [Fact]
    public void Highlight_rules_survive_a_settings_round_trip()
    {
        var paths = new AppPaths(Path.Combine(_tmp.Path, "cfg"), Path.DirectorySeparatorChar);
        var store = new SettingsStore(paths, Logger.Null);
        var s = new AppSettings { HighlightRules = [new("TODO", HighlightColor.Green, caseSensitive: true)], HighlightsEnabled = false, WindowWidth = 1200 };
        Assert.True(store.Save(s));
        var back = store.Load();
        var rule = Assert.Single(back.HighlightRules);
        Assert.Equal("TODO", rule.Pattern);
        Assert.Equal("Green", rule.Color);
        Assert.True(rule.CaseSensitive);
        Assert.False(back.HighlightsEnabled);
        Assert.Equal(1200, back.WindowWidth);
    }

    [Fact]
    public void New_settings_start_with_the_Papyrus_preset()
    {
        var paths = new AppPaths(Path.Combine(_tmp.Path, "fresh"), Path.DirectorySeparatorChar);
        var s = new SettingsStore(paths, Logger.Null).Load();
        Assert.Equal(HighlightPresets.PapyrusRules().Count, s.HighlightRules.Count);
        Assert.True(s.HighlightsEnabled);
    }
}
