using System.Diagnostics;
using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Platform;

namespace BejView.App;

/// <summary>
/// The side-effect half of the WSL bridge: gathers the facts (WSL, interop, mount root, where BejView.exe is),
/// asks <see cref="WslBridge.Decide"/>, and starts the Windows exe when that's the answer. Every step is logged
/// (Info, so it shows with --verbose); a bridge that was wanted but can't run is a Warning, printed even without it.
/// </summary>
internal static class WslBridgeRunner
{
    /// <summary>Returns an exit code when the Windows exe took over, or null to carry on in Linux.</summary>
    public static int? TryBridge(CliOptions o, Logger log)
    {
        if (!OperatingSystem.IsLinux()) return null;

        var env = Gather(log);
        var decision = WslBridge.Decide(o, env, p => ToWindowsPath(p, log));
        log.Info($"WSL bridge: {decision.Reason}");
        if (decision.Warning != null)
        {
            log.Warning($"WSL bridge: {decision.Warning}");
            if (!log.Verbose) Console.Error.WriteLine($"BejView: {decision.Warning}");
        }
        if (decision.Action != BridgeAction.UseWindowsExe) return null;
        return Launch(decision, o.Verbose, log);
    }

    private static BridgeEnvironment Gather(Logger log)
    {
        bool isWsl = Wsl.IsWslNow();
        if (!isWsl) return new BridgeEnvironment { IsWsl = false };

        string? conf = ReadOrNull("/etc/wsl.conf");
        string root = Wsl.MountRootFromConf(conf);
        bool interop = Wsl.InteropEnabled(f => ReadOrNull(f)?.Split('\n')[0]);
        string? ownDir = Path.GetDirectoryName(Environment.ProcessPath);
        var (exe, source) = WslBridge.FindWindowsExe(Environment.GetEnvironmentVariable, File.Exists, ownDir, root);
        string? problem = WslBridge.ConfiguredExeProblem(Environment.GetEnvironmentVariable, File.Exists, root);
        if (problem != null) log.Warning($"WSL bridge: {problem}");

        log.Info($"WSL bridge: WSL {Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") ?? "(distro unknown)"}, interop {(interop ? "on" : "off")}, " +
                 $"drive mounts under {root}{(conf == null ? " (no /etc/wsl.conf)" : "")}, Windows {WslBridge.ExeName}: {(exe == null ? "not found" : $"{exe} ({source})")}.");
        return new BridgeEnvironment
        {
            IsWsl = true,
            InteropEnabled = interop,
            MountRoot = root,
            CurrentDirectory = Environment.CurrentDirectory,
            WindowsExe = exe,
            WindowsExeSource = source,
        };
    }

    /// <summary>wslpath -w knows the real mounts; the tested rules in <see cref="Wsl.ToWindowsPath"/> are the fallback.</summary>
    private static string ToWindowsPath(string linuxPath, Logger log)
    {
        try
        {
            var psi = new ProcessStartInfo("wslpath") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(linuxPath);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            string error = p.StandardError.ReadToEnd().Trim();
            if (p.WaitForExit(5000) && p.ExitCode == 0 && output.Length > 0)
            {
                log.Debug($"WSL bridge: wslpath -w {linuxPath} gave {output}.");
                return output;
            }
            log.Info($"WSL bridge: wslpath -w {linuxPath} failed (exit {(p.HasExited ? p.ExitCode : -1)}{(error.Length > 0 ? ": " + error : "")}); using BejView's own rules.");
        }
        catch (Exception ex)
        {
            log.Info($"WSL bridge: couldn't run wslpath ({ex.Message}); using BejView's own rules.");
        }
        return Wsl.ToWindowsPath(linuxPath, Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") ?? "",
            Wsl.MountRootFromConf(ReadOrNull("/etc/wsl.conf")));
    }

    private static int Launch(BridgeDecision d, bool wait, Logger log)
    {
        var psi = new ProcessStartInfo(d.WindowsExe!) { UseShellExecute = false };
        foreach (string a in d.WindowsArgs) psi.ArgumentList.Add(a);
        // Start in the file's folder (a Windows drive) so the Windows program doesn't start in a \\wsl.localhost folder.
        string? dir = Path.GetDirectoryName(d.LinuxFile);
        if (dir != null && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        log.Info($"WSL bridge: starting {d.WindowsExe} {string.Join(' ', d.WindowsArgs.Select(Quote))}" +
                 (wait ? " and waiting for it (--verbose)." : "; this terminal is free again at once."));
        try
        {
            using var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("Process.Start returned nothing.");
            if (!wait) return ExitCodes.Ok;
            p.WaitForExit();
            log.Info($"WSL bridge: the Windows BejView exited with code {p.ExitCode}.");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            log.Error($"WSL bridge: couldn't start {d.WindowsExe}", ex);
            Console.Error.WriteLine($"BejView: couldn't start the Windows BejView ({d.WindowsExe}): {ex.Message}. " +
                                    "Check that the file exists and that WSL interop is on, or add --wslg to use the Linux window.");
            return ExitCodes.StartFailed;
        }
    }

    private static string Quote(string a) => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a;

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception) { return null; }
    }
}
