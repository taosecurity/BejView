using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using BejView.Core;
using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Platform;
using BejView.Core.Settings;

namespace BejView.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        var o = parsed.Options;

        // BejView is a windowed app; it borrows the terminal that started it when there is something to print.
        if (parsed.UsesTerminal) NativeConsole.AttachIfNeeded();

        if (!parsed.Ok)
        {
            Console.Error.WriteLine($"BejView: {parsed.Error}");
            Console.Error.WriteLine("Run 'bejview --help' to see the options.");
            return ExitCodes.BadArguments;
        }

        var paths = AppPaths.ForCurrentOs();
        switch (o!.Command)
        {
            case Command.Help:
                Console.WriteLine(CommandLine.HelpText);
                return ExitCodes.Ok;
            case Command.Version:
                Console.WriteLine($"{AppInfo.Name} {AppInfo.Version}");
                return ExitCodes.Ok;
            case Command.Paths:
                // Shown where other programs find them (the Store package may keep a private copy: AppPaths.RealLocation).
                Console.WriteLine($"Settings file: {AppPaths.RealLocation(paths.SettingsFile)}");
                Console.WriteLine($"Log file:      {AppPaths.RealLocation(o.LogFile ?? paths.DefaultLogFile)}");
                Console.WriteLine($"1.0 recent files (imported once): {paths.LegacyRecentFile}");
                if (PackageIdentity.FamilyName is string family)
                    Console.WriteLine($"Installed from the Microsoft Store (package {family}).");
                return ExitCodes.Ok;
        }

        using var log = new Logger(o.LogFile ?? paths.DefaultLogFile, o.Verbose, Console.Error);
        log.Info($"{AppInfo.Name} {AppInfo.Version} starting ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, " +
                 $".NET {Environment.Version}). Arguments: {(args.Length == 0 ? "none" : string.Join(' ', args))}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error($"Unexpected error: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { log.Error("Unexpected background error", e.Exception); e.SetObserved(); };

        // Under WSL, a file on a Windows drive opens in the Windows BejView.exe (the bridge); everything else runs here.
        if (WslBridgeRunner.TryBridge(o, log) is int bridged)
            return bridged;

        if (o.Command is Command.Info or Command.Find)
            return RunConsoleCommand(o, log);

        var store = new SettingsStore(paths, log);
        var session = new AppSession(o, log, store, store.Load());
        AppSession.Current = session;
        try
        {
            bool wayland = UseWayland(Environment.GetEnvironmentVariable, log);
            // AppId matches bejview.desktop, so KDE Plasma (KWin) and the portals show BejView's name and icon.
            int code = (wayland
                    ? BuildAvaloniaApp().UseWaylandWithFallback().With(new WaylandPlatformOptions { AppId = DisplayChoice.AppId })
                    : BuildAvaloniaApp())
                .StartWithClassicDesktopLifetime(args);
            log.Info($"Exiting with code {code}.");
            return code;
        }
        catch (Exception ex) when (MissingLibrary.Explain(ex) is string missing)
        {
            log.Error(missing);
            log.Debug($"Details: {ex}");
            if (!log.Verbose) Console.Error.WriteLine(missing); // --verbose already echoed it
            return ExitCodes.StartFailed;
        }
        catch (Exception ex)
        {
            log.Error("BejView stopped with an unexpected error", ex);
            Console.Error.WriteLine($"BejView stopped with an unexpected error: {ex.Message}. Details are in {log.FilePath}.");
            return ExitCodes.FileError;
        }
    }

    private static int RunConsoleCommand(CliOptions o, Logger log)
    {
        // Results go to stdout as UTF-8 (files and pipes get every character right); messages go to stderr.
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16) { AutoFlush = false };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            int code = o.Command == Command.Info
                ? CliCommands.InfoAsync(o, stdout, Console.Error, log, cts.Token).GetAwaiter().GetResult()
                : CliCommands.FindAsync(o, stdout, Console.Error, log, cts.Token).GetAwaiter().GetResult();
            stdout.Flush();
            log.Info($"{o.Command} finished with exit code {code}.");
            return code;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("BejView: stopped (Ctrl+C).");
            return ExitCodes.FileError;
        }
        catch (IOException ex) when (ex.Message.Contains("pipe", StringComparison.OrdinalIgnoreCase))
        {
            return ExitCodes.Ok; // the reader went away (for example | Select-Object -First 5)
        }
    }

    /// <summary>
    /// Linux: native Wayland when the desktop offers it, with X11 (XWayland or Xorg) as Avalonia's own fallback.
    /// Under WSL it uses X11: on WSLg's Weston the native Wayland start hung before the first window (2026-11-13,
    /// session 0), and Avalonia's fallback only covers a compositor it can't reach, not one that doesn't answer.
    /// BEJVIEW_X11=1 forces X11 anywhere; BEJVIEW_WAYLAND=1 forces native Wayland under WSL. Logged with --verbose.
    /// </summary>
    private static bool UseWayland(Func<string, string?> env, Logger log)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var choice = DisplayChoice.Decide(env, Wsl.IsWslNow());
        log.Info($"Display: WAYLAND_DISPLAY={env("WAYLAND_DISPLAY") ?? "not set"}, DISPLAY={env("DISPLAY") ?? "not set"}. {choice.Reason}");
        return choice.Wayland;
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
