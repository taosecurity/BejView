using System.Globalization;
using System.Text;

namespace BejView.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Small thread-safe logger. Without --verbose it writes warnings and errors only to the log file, so a failure
/// always leaves a trace. With --verbose it also writes Debug and Info lines, and echoes everything to stderr.
/// The file is cut back to its newest half when it passes <see cref="MaxBytes"/>.
/// </summary>
public sealed class Logger : IDisposable
{
    public const long MaxBytes = 1_000_000;

    private readonly object _lock = new();
    private readonly TextWriter? _console;
    private readonly Func<DateTime> _clock;
    private StreamWriter? _file;

    public string? FilePath { get; }
    public bool Verbose { get; }

    /// <summary>A logger that discards everything (for tests and tools that don't want a log).</summary>
    public static Logger Null { get; } = new(null, verbose: false, console: null);

    public Logger(string? filePath, bool verbose, TextWriter? console, Func<DateTime>? clock = null)
    {
        FilePath = filePath;
        Verbose = verbose;
        _console = verbose ? console : null;
        _clock = clock ?? (() => DateTime.Now);
        if (filePath != null) OpenFile(filePath);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warning(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message} ({ex.GetType().Name}: {ex.Message})" + (Verbose ? Environment.NewLine + ex : ""));

    public void Write(LogLevel level, string message)
    {
        if (!Verbose && level < LogLevel.Warning) return;
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{_clock():yyyy-MM-dd HH:mm:ss.fff} {Label(level)} {message}");
        lock (_lock)
        {
            try
            {
                _file?.WriteLine(line);
            }
            catch (IOException)
            {
                // A full disk or a removed drive must not stop the viewer; stderr still gets the line in verbose mode.
            }
            _console?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private static string Label(LogLevel l) => l switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "WARN ",
        _ => "ERROR",
    };

    private void OpenFile(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            TrimIfLarge(path);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _file = null;
            // Report on stderr even without --verbose: the user asked for a log and isn't getting one.
            Console.Error.WriteLine($"BejView: can't write the log file {path} ({ex.Message}). Pick another path with --log <file>.");
        }
    }

    /// <summary>Keeps the newest half of a log that has grown past <see cref="MaxBytes"/>.</summary>
    internal static void TrimIfLarge(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;
        string text = File.ReadAllText(path);
        int cut = text.IndexOf('\n', text.Length / 2);
        File.WriteAllText(path, "(older lines removed)" + Environment.NewLine + (cut >= 0 ? text[(cut + 1)..] : ""));
    }
}
