using BejView.Core.Cli;
using LauncherProgram = BejView.Launcher.Program;

namespace BejView.Tests;

/// <summary>BejView.com, the terminal helper: it passes the arguments on exactly as typed and waits only when BejView prints.</summary>
public class LauncherTests
{
    [Theory]
    [InlineData(@"BejView.com", "")]
    [InlineData(@"""C:\Program Files\BejView\BejView.com""   ", "")]
    [InlineData(@"BejView.com --help", " --help")]
    [InlineData(@".\BejView find big.log ""warn|error"" --regex", @" find big.log ""warn|error"" --regex")]
    [InlineData(@"""D:\My Tools\BejView.com"" info ""D:\My Logs\Papyrus.0.log""", @" info ""D:\My Logs\Papyrus.0.log""")]
    [InlineData("BejView.com\tpaths", " paths")]
    public void Arguments_pass_through_unchanged(string commandLine, string expected)
        => Assert.Equal(expected, LauncherProgram.ArgumentsPart(commandLine));

    [Theory]
    [InlineData(true, "info", "big.log")]
    [InlineData(true, "find", "big.log", "error")]
    [InlineData(true, "paths")]
    [InlineData(true, "--help")]
    [InlineData(true, "--version")]
    [InlineData(true, "big.log", "--verbose")]
    [InlineData(true, "--no-such-option")]
    [InlineData(false)]
    [InlineData(false, "big.log")]
    [InlineData(false, "Papyrus.0.log", "--follow")]
    [InlineData(false, "big.log", "--line", "5000", "--find", "error")]
    public void Waits_only_when_BejView_prints(bool wait, params string[] args)
        => Assert.Equal(wait, LauncherProgram.ShouldWait(args));

    [Fact]
    public void Help_mentions_the_helper_and_exit_code_5()
    {
        Assert.Contains("BejView.com", CommandLine.HelpText);
        Assert.Contains("5  BejView.com", CommandLine.HelpText);
        Assert.EndsWith("Enjoy BejView!", CommandLine.HelpText.TrimEnd());
        Assert.Equal(5, ExitCodes.StartFailed);
    }

    // FindTarget (step 5): the zip, the WinGet layout (Links symlink -> cli\bejview.exe, BejView.exe one folder up)
    // and a broken install where BejView.exe is really the helper. Paths are built with Path.Combine so the test
    // runs on Linux too.
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "bjv-find");
    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void Finds_BejView_exe_next_to_the_helper()
    {
        string exe = P("zip", "BejView.exe");
        Assert.Equal(exe, LauncherProgram.FindTarget(P("zip", "BejView.com"), f => f == exe, f => f));
    }

    [Fact]
    public void Finds_BejView_exe_through_the_WinGet_link_one_folder_up()
    {
        string link = P("Links", "bejview.exe"), helper = P("pkg", "cli", "bejview.exe"), exe = P("pkg", "BejView.exe");
        Assert.Equal(exe, LauncherProgram.FindTarget(link, f => f == exe || f == helper, f => f == link ? helper : f));
    }

    [Fact]
    public void Prefers_the_folder_of_the_helper_over_the_folder_above()
    {
        string here = P("a", "b", "BejView.exe"), above = P("a", "BejView.exe");
        Assert.Equal(here, LauncherProgram.FindTarget(P("a", "b", "BejView.com"), f => f == here || f == above, f => f));
    }

    [Fact]
    public void Never_starts_itself_when_BejView_exe_is_really_the_helper()
    {
        // What a WinGet hard link named bejview.exe would do: BejView.exe replaced by a link to the helper.
        string helper = P("pkg", "BejView.com"), exe = P("pkg", "BejView.exe");
        Assert.Null(LauncherProgram.FindTarget(helper, f => f == exe, f => f == exe ? helper : f));
    }

    [Fact]
    public void Reports_nothing_when_BejView_exe_is_missing()
        => Assert.Null(LauncherProgram.FindTarget(P("x", "BejView.com"), _ => false, f => f));
}
