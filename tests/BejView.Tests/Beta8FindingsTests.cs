using BejView.Core.Cli;
using BejView.Core.Platform;

namespace BejView.Tests;

/// <summary>Fixes for what the user's beta 8 test found (2026-11-13).</summary>
public class Beta8FindingsTests
{
    // The message .NET gave on rb's WSL Debian, which had no libfontconfig1 (check 13.2).
    private const string RbMessage =
        "Unable to load shared library 'libSkiaSharp' or one of its dependencies. In order to help diagnose loading problems, " +
        "consider using a tool like strace. If you're using glibc, consider setting the LD_DEBUG environment variable: \n" +
        "libfontconfig.so.1: cannot open shared object file: No such file or directory\n" +
        "/mnt/d/Starfield Dev/Tools/BejView-2.0.0-beta.8-linux-x64/libSkiaSharp.so: cannot open shared object file: No such file or directory\n" +
        "/home/rb/.net/bejview/3w3xwzRa-ja_/liblibSkiaSharp.so: cannot open shared object file: No such file or directory\n" +
        "/home/rb/.net/bejview/3w3xwzRa-ja_/libSkiaSharp: cannot open shared object file: No such file or directory\n";

    private static Exception AsThrown(string inner) =>
        new TypeInitializationException("SkiaSharp.SKImageInfo", new DllNotFoundException(inner));

    [Fact]
    public void A_missing_system_library_is_named_with_the_package_to_install()
    {
        var ex = AsThrown(RbMessage);
        Assert.Equal(["libfontconfig.so.1"], MissingLibrary.Find(ex)); // not the bundled libSkiaSharp lines
        string msg = MissingLibrary.Explain(ex)!;
        Assert.Contains("libfontconfig.so.1 is missing", msg);
        Assert.Contains("sudo apt install libfontconfig1", msg);
        Assert.Contains("sudo pacman -S fontconfig", msg);
        Assert.Contains("sudo dnf install fontconfig", msg);
    }

    [Fact]
    public void Several_missing_libraries_are_listed_once_each()
    {
        var ex = AsThrown("libX11.so.6: cannot open shared object file\nlibICE.so.6: cannot open shared object file\nlibX11.so.6: cannot open shared object file");
        Assert.Equal(["libX11.so.6", "libICE.so.6"], MissingLibrary.Find(ex));
        Assert.Contains("sudo apt install libx11-6 libice6", MissingLibrary.Explain(ex));
    }

    [Fact]
    public void An_unknown_library_still_gets_named_and_other_errors_are_left_alone()
    {
        Assert.Contains("libweird.so.3", MissingLibrary.Explain(AsThrown("libweird.so.3: cannot open shared object file")));
        Assert.Null(MissingLibrary.Explain(new InvalidOperationException("something else")));
        Assert.Equal(["libfontconfig.so.1"], MissingLibrary.Find(AsThrown("/tmp/fake/libfontconfig.so.1: file too short"))
            .Select(l => l[(l.LastIndexOf('/') + 1)..]));
        Assert.Null(MissingLibrary.Explain(AsThrown("/x/libSkiaSharp.so: cannot open shared object file"))); // bundled only
    }

    [Theory]
    [InlineData("--verbose", "info", "app.log")]          // check 13.4: options before the command
    [InlineData("-v", "info", "app.log")]
    [InlineData("--log", "x.log", "info", "app.log")]     // an option's value isn't taken for the command
    [InlineData("info", "app.log", "--verbose")]
    public void The_command_may_come_after_options(params string[] args)
    {
        var r = CommandLine.Parse(args);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Command.Info, r.Options!.Command);
        Assert.Equal("app.log", r.Options.File);
    }

    [Fact]
    public void A_file_called_info_can_still_be_opened()
    {
        var a = CommandLine.Parse(["--", "info"]);
        Assert.Equal((Command.Gui, "info"), (a.Options!.Command, a.Options.File));
        var b = CommandLine.Parse(["--log", "info", "app.log"]); // "info" is the log file here
        Assert.Equal((Command.Gui, "info", "app.log"), (b.Options!.Command, b.Options.LogFile, b.Options.File));
        var c = CommandLine.Parse(["--verbose", "find", "app.log", "error", "--count"]);
        Assert.Equal((Command.Find, "app.log", "error", true), (c.Options!.Command, c.Options.File, c.Options.Find, c.Options.CountOnly));
    }
}
