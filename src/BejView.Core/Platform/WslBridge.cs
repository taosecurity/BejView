using BejView.Core.Cli;

namespace BejView.Core.Platform;

/// <summary>What the Linux build does with a command line when it starts.</summary>
public enum BridgeAction
{
    /// <summary>Run here: a console command, the Linux window, or anything not under WSL.</summary>
    RunHere,
    /// <summary>Hand the file to the Windows BejView.exe and exit.</summary>
    UseWindowsExe,
}

/// <summary>Everything the bridge decision depends on, gathered once so the decision itself has no side effects.</summary>
public sealed record BridgeEnvironment
{
    public bool IsWsl { get; init; }
    /// <summary>WSL can run Windows programs (the WSLInterop handler is registered and enabled).</summary>
    public bool InteropEnabled { get; init; }
    /// <summary>Where the Windows drives are mounted: /mnt/ unless /etc/wsl.conf says otherwise.</summary>
    public string MountRoot { get; init; } = "/mnt/";
    public string CurrentDirectory { get; init; } = "/";
    /// <summary>The Windows BejView.exe as a Linux path, or null if none was found.</summary>
    public string? WindowsExe { get; init; }
    /// <summary>How <see cref="WindowsExe"/> was found (for the log).</summary>
    public string? WindowsExeSource { get; init; }
}

/// <summary>The bridge's answer, with the reason in plain words (logged with --verbose).</summary>
public sealed record BridgeDecision(BridgeAction Action, string Reason)
{
    public string? WindowsExe { get; init; }
    /// <summary>The file as a Linux path (absolute) and as the Windows path handed to BejView.exe.</summary>
    public string? LinuxFile { get; init; }
    public string? WindowsFile { get; init; }
    /// <summary>The arguments for BejView.exe, one per item (no quoting needed).</summary>
    public IReadOnlyList<string> WindowsArgs { get; init; } = [];
    /// <summary>A problem the user should hear about even without --verbose (the bridge was wanted but couldn't run).</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// WSL bridge mode. Under WSL, reading a big file on /mnt/c through the Linux build is slow (every read crosses the
/// 9P file share), so a file on a Windows drive opens in the Windows BejView.exe instead, with the path translated by
/// wslpath -w. --wslg forces the Linux window. info, find and the other console commands always run in Linux.
/// </summary>
public static class WslBridge
{
    public const string ExeEnvironmentVariable = "BEJVIEW_WINDOWS_EXE";
    public const string ExeName = "BejView.exe";

    public static BridgeDecision Decide(CliOptions o, BridgeEnvironment env, Func<string, string> toWindowsPath)
    {
        if (!env.IsWsl)
            return Here(o.Wslg ? "Not running under WSL, so --wslg has nothing to change; showing the Linux window." : "Not running under WSL.");
        if (o.Command != Command.Gui)
            return Here($"'{o.Command.ToString().ToLowerInvariant()}' is a console command; it runs in Linux.");
        if (o.Wslg)
            return Here("--wslg given: showing the Linux window in WSLg.");
        if (o.File == null)
            return Here("No file given: showing the Linux window in WSLg (open a /mnt file from the command line to use the Windows BejView).");

        string linuxFile = Absolute(o.File, env.CurrentDirectory);
        if (!Wsl.IsOnDriveMount(linuxFile, env.MountRoot))
            return Here($"{linuxFile} is on the Linux file system, which Linux reads fastest; showing the Linux window in WSLg.")
                with { LinuxFile = linuxFile };

        const string fallback = " Showing the Linux window in WSLg instead (slower on big files).";
        if (!env.InteropEnabled)
            return Here($"{linuxFile} is on a Windows drive, but WSL can't start Windows programs here (interop is off).")
                with { LinuxFile = linuxFile, Warning = "WSL interop is off ([interop] enabled=false in /etc/wsl.conf), so the Windows BejView can't be started." + fallback };
        if (env.WindowsExe == null)
            return Here($"{linuxFile} is on a Windows drive, but no Windows {ExeName} was found.")
                with
                {
                    LinuxFile = linuxFile,
                    Warning = $"No Windows {ExeName} found (looked in ${ExeEnvironmentVariable}, next to this program and on PATH)." + fallback +
                              $" To use the Windows one, set {ExeEnvironmentVariable} to its path, for example: export {ExeEnvironmentVariable}=\"/mnt/c/Tools/BejView/{ExeName}\"",
                };

        string windowsFile = toWindowsPath(linuxFile);
        return new BridgeDecision(BridgeAction.UseWindowsExe,
            $"{linuxFile} is on a Windows drive: opening it in the Windows BejView ({env.WindowsExe}, found {env.WindowsExeSource ?? "by search"}) as {windowsFile}.")
        {
            WindowsExe = env.WindowsExe,
            LinuxFile = linuxFile,
            WindowsFile = windowsFile,
            WindowsArgs = WindowsArguments(o, windowsFile, env.CurrentDirectory, env.MountRoot, toWindowsPath),
        };
    }

    /// <summary>The window options, rebuilt for BejView.exe: the file and --log become Windows paths, --wslg is dropped.</summary>
    public static IReadOnlyList<string> WindowsArguments(CliOptions o, string windowsFile, string currentDirectory, string mountRoot,
        Func<string, string> toWindowsPath)
    {
        var a = new List<string>();
        if (o.Line is long line) { a.Add("--line"); a.Add(line.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (o.Find != null) { a.Add("--find"); a.Add(o.Find); }
        if (o.Follow) a.Add("--follow");
        if (o.CaseSensitive) a.Add("--case");
        if (o.WholeWord) a.Add("--word");
        if (o.Regex) a.Add("--regex");
        if (o.MaxHits is int max) { a.Add("--max"); a.Add(max.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (o.Encoding is { } enc) { a.Add("--encoding"); a.Add(EncodingName(enc)); }
        if (o.Verbose) a.Add("--verbose");
        if (o.LogFile != null) { a.Add("--log"); a.Add(toWindowsPath(Absolute(o.LogFile, currentDirectory))); }
        a.Add("--");
        a.Add(windowsFile);
        return a;
    }

    /// <summary>
    /// Looks for the Windows BejView.exe, in this order: $BEJVIEW_WINDOWS_EXE (a Linux or Windows path), the folder this
    /// program runs from, then each Windows folder on PATH (WSL adds the Windows PATH by default). Returns the Linux
    /// path and where it was found, or (null, null).
    /// </summary>
    public static (string? Path, string? Source) FindWindowsExe(Func<string, string?> env, Func<string, bool> fileExists,
        string? ownDirectory, string mountRoot)
    {
        string? configured = env(ExeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string p = configured.Trim().Trim('"');
            if (!p.StartsWith('/'))
            {
                try { p = Wsl.ToLinuxPath(p, mountRoot); }
                catch (ArgumentException) { return (null, null); }
            }
            // A configured path that doesn't exist isn't silently replaced by a search: that would hide the typo.
            return fileExists(p) ? (p, $"in ${ExeEnvironmentVariable}") : (null, null);
        }

        if (!string.IsNullOrEmpty(ownDirectory))
        {
            string p = ownDirectory.TrimEnd('/') + "/" + ExeName;
            if (fileExists(p)) return (p, "next to this program");
        }

        foreach (string dir in (env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Wsl.IsOnDriveMount(dir, mountRoot)) continue; // Windows folders only
            string p = dir.TrimEnd('/') + "/" + ExeName;
            if (fileExists(p)) return (p, "on PATH");
        }
        return (null, null);
    }

    /// <summary>
    /// Why FindWindowsExe returned nothing when $BEJVIEW_WINDOWS_EXE is set, so the warning can name the bad value.
    /// </summary>
    public static string? ConfiguredExeProblem(Func<string, string?> env, Func<string, bool> fileExists, string mountRoot)
    {
        string? configured = env(ExeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string p = configured.Trim().Trim('"');
        if (!p.StartsWith('/'))
        {
            try { p = Wsl.ToLinuxPath(p, mountRoot); }
            catch (ArgumentException) { return $"${ExeEnvironmentVariable} is '{configured}', which isn't a full Linux or Windows path."; }
        }
        return fileExists(p) ? null : $"${ExeEnvironmentVariable} is '{configured}', but {p} doesn't exist.";
    }

    private static BridgeDecision Here(string reason) => new(BridgeAction.RunHere, reason);

    private static string Absolute(string path, string currentDirectory) =>
        path.StartsWith('/') ? Normalize(path) : Normalize(currentDirectory.TrimEnd('/') + "/" + path);

    /// <summary>Removes . and .. and doubled slashes, the Linux way (done by hand so tests on Windows give Linux answers).</summary>
    public static string Normalize(string linuxPath)
    {
        var parts = new List<string>();
        foreach (string part in linuxPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    private static string EncodingName(Text.TextEncodingKind k) => k switch
    {
        Text.TextEncodingKind.Utf16LE => "utf16le",
        Text.TextEncodingKind.Utf16BE => "utf16be",
        Text.TextEncodingKind.Latin1 => "latin1",
        _ => "utf8",
    };
}
