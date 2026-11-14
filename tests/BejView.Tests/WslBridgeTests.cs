using BejView.Core.Cli;
using BejView.Core.Platform;
using BejView.Core.Text;

namespace BejView.Tests;

/// <summary>
/// The WSL bridge decisions (step 4). These are pure functions, so they run the same on Windows and in WSL Debian;
/// the real wslpath and the start of BejView.exe are checked by hand under WSL (NOTES.md, step 4).
/// </summary>
public class WslBridgeTests
{
    private const string Exe = "/mnt/d/Tools/BejView-2.0.0-win-x64/BejView.exe";

    private static readonly BridgeEnvironment Wsl2 = new()
    {
        IsWsl = true,
        InteropEnabled = true,
        MountRoot = "/mnt/",
        CurrentDirectory = "/mnt/d/Starfield Dev",
        WindowsExe = Exe,
        WindowsExeSource = "on PATH",
    };

    private static CliOptions Opts(params string[] args)
    {
        var r = CommandLine.Parse(args);
        Assert.True(r.Ok, r.Error);
        return r.Options!;
    }

    // Stands in for wslpath -w, using the tested rules.
    private static string ToWin(string p) => Wsl.ToWindowsPath(p, "Debian");

    private static BridgeDecision Decide(BridgeEnvironment env, params string[] args) => WslBridge.Decide(Opts(args), env, ToWin);

    [Fact]
    public void A_file_on_a_windows_drive_opens_in_the_windows_exe()
    {
        var d = Decide(Wsl2, "/mnt/c/Users/rb/Documents/My Games/Starfield/Logs/Script/Papyrus.0.log");
        Assert.Equal(BridgeAction.UseWindowsExe, d.Action);
        Assert.Equal(Exe, d.WindowsExe);
        Assert.Equal(@"C:\Users\rb\Documents\My Games\Starfield\Logs\Script\Papyrus.0.log", d.WindowsFile);
        Assert.Equal(["--", d.WindowsFile!], d.WindowsArgs);
        Assert.Null(d.Warning);
        Assert.Contains("Windows drive", d.Reason);
    }

    [Fact]
    public void A_relative_path_is_taken_from_the_current_folder()
    {
        var d = Decide(Wsl2, "TestData/../TestData/./dump.txt");
        Assert.Equal("/mnt/d/Starfield Dev/TestData/dump.txt", d.LinuxFile);
        Assert.Equal(@"D:\Starfield Dev\TestData\dump.txt", d.WindowsFile);

        var home = Decide(Wsl2 with { CurrentDirectory = "/home/rb" }, "../../mnt/c/x.log");
        Assert.Equal(BridgeAction.UseWindowsExe, home.Action);
        Assert.Equal(@"C:\x.log", home.WindowsFile);
    }

    [Fact]
    public void Wslg_forces_the_linux_window()
    {
        var d = Decide(Wsl2, "/mnt/c/big.log", "--wslg");
        Assert.Equal(BridgeAction.RunHere, d.Action);
        Assert.Contains("--wslg", d.Reason);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void A_file_on_the_linux_file_system_stays_in_linux()
    {
        var d = Decide(Wsl2, "/home/rb/logs/app.log");
        Assert.Equal(BridgeAction.RunHere, d.Action);
        Assert.Contains("Linux file system", d.Reason);
        // Not a drive: /mnt/wsl and /mnt/wslg are WSL's own folders.
        Assert.Equal(BridgeAction.RunHere, Decide(Wsl2, "/mnt/wslg/weston.log").Action);
        Assert.Equal(BridgeAction.RunHere, Decide(Wsl2, "/mnt/wsl/x.log").Action);
    }

    [Fact]
    public void No_file_shows_the_linux_window()
    {
        var d = Decide(Wsl2);
        Assert.Equal(BridgeAction.RunHere, d.Action);
        Assert.Contains("No file", d.Reason);
    }

    [Theory]
    [InlineData("info", "/mnt/c/big.log")]
    [InlineData("find", "/mnt/c/big.log", "ERROR")]
    [InlineData("paths")]
    [InlineData("--help")]
    [InlineData("--version")]
    public void Console_commands_always_run_in_linux(params string[] args)
    {
        var d = Decide(Wsl2, args);
        Assert.Equal(BridgeAction.RunHere, d.Action);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void Outside_wsl_nothing_is_bridged()
    {
        var notWsl = new BridgeEnvironment { IsWsl = false, WindowsExe = Exe, InteropEnabled = true };
        Assert.Equal(BridgeAction.RunHere, Decide(notWsl, "/mnt/c/big.log").Action);
        Assert.Contains("--wslg has nothing to change", Decide(notWsl, "/mnt/c/big.log", "--wslg").Reason);
    }

    [Fact]
    public void Without_interop_or_the_exe_it_falls_back_to_the_linux_window_and_says_why()
    {
        var noInterop = Decide(Wsl2 with { InteropEnabled = false }, "/mnt/c/big.log");
        Assert.Equal(BridgeAction.RunHere, noInterop.Action);
        Assert.Contains("interop", noInterop.Warning);

        var noExe = Decide(Wsl2 with { WindowsExe = null }, "/mnt/c/big.log");
        Assert.Equal(BridgeAction.RunHere, noExe.Action);
        Assert.Contains(WslBridge.ExeEnvironmentVariable, noExe.Warning);
        Assert.Contains("export", noExe.Warning);
    }

    [Fact]
    public void Window_options_are_passed_on_with_windows_paths()
    {
        var d = Decide(Wsl2, "/mnt/c/logs/Papyrus.0.log", "--line", "500", "--find", "-error text", "--case", "--word", "--regex",
            "--max", "50", "--encoding", "utf16le", "--follow", "--verbose", "--log", "bridge.log");
        Assert.Equal(BridgeAction.UseWindowsExe, d.Action);
        Assert.Equal(
            ["--line", "500", "--find", "-error text", "--follow", "--case", "--word", "--regex", "--max", "50",
             "--encoding", "utf16le", "--verbose", "--log", @"D:\Starfield Dev\bridge.log", "--", @"C:\logs\Papyrus.0.log"],
            d.WindowsArgs);

        // What BejView.exe makes of them is the same window and options (apart from the paths).
        var back = CommandLine.Parse(d.WindowsArgs.ToArray());
        Assert.True(back.Ok, back.Error);
        var o = back.Options!;
        Assert.Equal(Command.Gui, o.Command);
        Assert.Equal(@"C:\logs\Papyrus.0.log", o.File);
        Assert.Equal(500, o.Line);
        Assert.Equal("-error text", o.Find);
        Assert.True(o.Follow && o.CaseSensitive && o.WholeWord && o.Regex && o.Verbose);
        Assert.Equal(50, o.MaxHits);
        Assert.Equal(TextEncodingKind.Utf16LE, o.Encoding);
        Assert.False(o.Wslg);
    }

    [Fact]
    public void A_file_whose_name_starts_with_a_dash_still_reaches_windows_as_a_file()
    {
        var d = Decide(Wsl2, "--", "-odd.log");
        Assert.Equal(@"D:\Starfield Dev\-odd.log", d.WindowsFile);
        Assert.Equal(@"D:\Starfield Dev\-odd.log", CommandLine.Parse(d.WindowsArgs.ToArray()).Options!.File);
    }

    [Fact]
    public void Each_encoding_is_passed_by_a_name_bejview_exe_reads_back()
    {
        foreach (var k in Enum.GetValues<TextEncodingKind>())
        {
            var o = new CliOptions { File = "/mnt/c/a.txt", Encoding = k };
            var args = WslBridge.WindowsArguments(o, @"C:\a.txt", "/", "/mnt/", ToWin);
            Assert.Equal(k, CommandLine.Parse(args.ToArray()).Options!.Encoding);
        }
    }

    [Fact]
    public void The_windows_exe_is_found_in_the_variable_then_next_to_this_program_then_on_path()
    {
        var files = new HashSet<string>
        {
            "/mnt/c/Tools/BejView.exe",
            "/opt/bejview/BejView.exe",
            "/mnt/c/Users/rb/AppData/Local/Microsoft/WindowsApps/BejView.exe",
            "/usr/local/bin/BejView.exe", // a Linux folder on PATH is never used
        };
        Func<string, string?> Env(string? variable, string path) => n => n switch
        {
            WslBridge.ExeEnvironmentVariable => variable,
            "PATH" => path,
            _ => null,
        };
        const string path = "/usr/local/bin:/usr/bin:/mnt/c/WINDOWS/system32:/mnt/c/Users/rb/AppData/Local/Microsoft/WindowsApps";

        Assert.Equal(("/mnt/c/Tools/BejView.exe", "in $BEJVIEW_WINDOWS_EXE"),
            WslBridge.FindWindowsExe(Env("/mnt/c/Tools/BejView.exe", path), files.Contains, "/opt/bejview", "/mnt/"));
        // A Windows path in the variable works too, with or without quotes.
        Assert.Equal("/mnt/c/Tools/BejView.exe",
            WslBridge.FindWindowsExe(Env("\"C:\\Tools\\BejView.exe\"", path), files.Contains, null, "/mnt/").Path);
        Assert.Equal(("/opt/bejview/BejView.exe", "next to this program"),
            WslBridge.FindWindowsExe(Env(null, path), files.Contains, "/opt/bejview/", "/mnt/"));
        Assert.Equal(("/mnt/c/Users/rb/AppData/Local/Microsoft/WindowsApps/BejView.exe", "on PATH"),
            WslBridge.FindWindowsExe(Env(null, path), files.Contains, "/home/rb", "/mnt/"));
        Assert.Equal((null, null), WslBridge.FindWindowsExe(Env(null, "/usr/local/bin:/usr/bin"), files.Contains, null, "/mnt/"));
    }

    [Fact]
    public void A_wrong_variable_is_reported_and_not_replaced_by_a_search()
    {
        var files = new HashSet<string> { "/mnt/c/Users/rb/AppData/Local/Microsoft/WindowsApps/BejView.exe" };
        Func<string, string?> env = n => n switch
        {
            WslBridge.ExeEnvironmentVariable => "/mnt/c/Tols/BejView.exe",
            "PATH" => "/mnt/c/Users/rb/AppData/Local/Microsoft/WindowsApps",
            _ => null,
        };
        Assert.Equal((null, null), WslBridge.FindWindowsExe(env, files.Contains, null, "/mnt/"));
        Assert.Contains("doesn't exist", WslBridge.ConfiguredExeProblem(env, files.Contains, "/mnt/"));
        Assert.Contains("isn't a full", WslBridge.ConfiguredExeProblem(n => n == WslBridge.ExeEnvironmentVariable ? "BejView.exe" : null, _ => true, "/mnt/"));
        Assert.Null(WslBridge.ConfiguredExeProblem(_ => null, _ => false, "/mnt/"));
    }

    [Fact]
    public void A_custom_mount_root_from_wsl_conf_is_used()
    {
        Assert.Equal("/mnt/", Wsl.MountRootFromConf(null));
        Assert.Equal("/mnt/", Wsl.MountRootFromConf("[boot]\nsystemd=true\n"));
        Assert.Equal("/", Wsl.MountRootFromConf("[automount]\nroot = /\n"));
        Assert.Equal("/drives/", Wsl.MountRootFromConf("[boot]\nroot=/nope\n\n[automount]  \r\n  # comment\n  Root = \"/drives\" # here\r\n"));
        Assert.Equal("/mnt/", Wsl.MountRootFromConf("[automount]\nroot = relative\n"));

        var custom = Wsl2 with { MountRoot = "/drives/", CurrentDirectory = "/" };
        var d = WslBridge.Decide(Opts("/drives/e/x.log"), custom, p => Wsl.ToWindowsPath(p, "Debian", "/drives/"));
        Assert.Equal(BridgeAction.UseWindowsExe, d.Action);
        Assert.Equal(@"E:\x.log", d.WindowsFile);
        Assert.Equal(BridgeAction.RunHere, WslBridge.Decide(Opts("/mnt/c/x.log"), custom, ToWin).Action);
    }

    [Theory]
    [InlineData("/mnt/c", true)]
    [InlineData("/mnt/c/", true)]
    [InlineData("/mnt/D/x", true)]
    [InlineData("/mnt/wslg/x", false)]
    [InlineData("/mnt/cc/x", false)]
    [InlineData("/mnt/", false)]
    [InlineData("/home/c/x", false)]
    public void Drive_mounts_are_one_letter_under_the_root(string path, bool expected) =>
        Assert.Equal(expected, Wsl.IsOnDriveMount(path));

    [Fact]
    public void Interop_is_on_only_when_the_handler_says_enabled()
    {
        Assert.True(Wsl.InteropEnabled(f => f == Wsl.InteropFile ? "enabled" : null));
        Assert.False(Wsl.InteropEnabled(f => f == Wsl.InteropFile ? "disabled" : null));
        Assert.True(Wsl.InteropEnabled(f => f.EndsWith("-late") ? "enabled\n" : null));
        Assert.False(Wsl.InteropEnabled(_ => null));
    }
}
