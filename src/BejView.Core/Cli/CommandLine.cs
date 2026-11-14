using System.Globalization;
using BejView.Core.Text;

namespace BejView.Core.Cli;

public enum Command
{
    /// <summary>No command: open the window (with FILE if given).</summary>
    Gui,
    /// <summary>Print a file's size, encoding, line count and index timing, then exit.</summary>
    Info,
    /// <summary>Search a file and print matching lines (like grep), then exit.</summary>
    Find,
    /// <summary>Print where the settings and log are, then exit.</summary>
    Paths,
    Help,
    Version,
}

/// <summary>Process exit codes, listed in the README and AGENTS.md.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>find: the search ran and found nothing (the same as grep).</summary>
    public const int NoMatches = 1;
    public const int BadArguments = 2;
    /// <summary>The file couldn't be opened or read.</summary>
    public const int FileError = 3;
    /// <summary>find: the search text can't be used (an invalid regular expression, for example).</summary>
    public const int SearchError = 4;
    /// <summary>BejView.com (the terminal helper) couldn't find or start BejView.exe; on Linux, a system library the
    /// window needs is missing (since beta 9); or the WSL bridge couldn't start the Windows BejView.exe.</summary>
    public const int StartFailed = 5;
}

/// <summary>Parsed command line.</summary>
public sealed record CliOptions
{
    public Command Command { get; init; } = Command.Gui;
    public string? File { get; init; }
    /// <summary>1-based line to show (window) or nothing.</summary>
    public long? Line { get; init; }
    /// <summary>Window: search for this after opening. find: the search text.</summary>
    public string? Find { get; init; }
    public bool Follow { get; init; }
    public TextEncodingKind? Encoding { get; init; }
    public bool CaseSensitive { get; init; }
    public bool Regex { get; init; }
    public bool WholeWord { get; init; }
    public int? MaxHits { get; init; }
    /// <summary>find: print only the number of matches.</summary>
    public bool CountOnly { get; init; }
    public bool Verbose { get; init; }
    public string? LogFile { get; init; }
    /// <summary>Under WSL: show the Linux window in WSLg instead of handing /mnt files to the Windows exe.</summary>
    public bool Wslg { get; init; }
}

public sealed record ParseResult(CliOptions? Options, string? Error)
{
    public bool Ok => Error == null;

    /// <summary>
    /// True when this run prints to the terminal: an error, --verbose, or any command other than opening the window.
    /// BejView.exe borrows the terminal then, and BejView.com (the terminal helper) waits for it; opening a file in the
    /// window does neither, so the terminal isn't held while the window is open.
    /// </summary>
    public bool UsesTerminal => !Ok || Options!.Verbose || Options.Command is not Command.Gui;
}

/// <summary>A small, dependency-free parser for BejView's few options. Accepts --opt value and --opt=value.</summary>
public static class CommandLine
{
    /// <summary>Options that take the next argument as their value (unless written --opt=value).</summary>
    private static readonly HashSet<string> TakesValue = ["--log", "--line", "-n", "--find", "--max", "--encoding"];

    private static Command? CommandWord(string arg) => arg.ToLowerInvariant() switch
    {
        "info" => Command.Info,
        "find" => Command.Find,
        "paths" => Command.Paths,
        "help" => Command.Help,
        "version" => Command.Version,
        _ => null,
    };

    /// <summary>
    /// The command is the first plain argument, so options may come before it ("bejview --verbose info app.log",
    /// found in the beta 8 test, 2026-11-13). Option values are skipped, and after "--" everything is a file.
    /// </summary>
    private static int CommandIndex(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--") return -1;
            if (a.StartsWith('-') && a != "-")
            {
                if (TakesValue.Contains(a)) i++; // its value is the next argument
                continue;
            }
            return CommandWord(a) != null ? i : -1;
        }
        return -1;
    }

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions();
        var positional = new List<string>();

        int commandAt = CommandIndex(args);
        if (commandAt >= 0)
        {
            o = o with { Command = CommandWord(args[commandAt])!.Value };
            args = args.Where((_, k) => k != commandAt).ToList();
        }

        bool endOfOptions = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (endOfOptions || !arg.StartsWith('-') || arg == "-")
            {
                positional.Add(arg);
                continue;
            }
            if (arg == "--") { endOfOptions = true; continue; }

            string name = arg;
            string? inline = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith("--") && eq > 2) { name = arg[..eq]; inline = arg[(eq + 1)..]; }

            string? Value()
            {
                if (inline != null) return inline;
                if (i + 1 < args.Count) return args[++i];
                return null;
            }

            switch (name)
            {
                case "-h" or "--help": o = o with { Command = Command.Help }; break;
                case "--version": o = o with { Command = Command.Version }; break;
                case "-v" or "--verbose": o = o with { Verbose = true }; break;
                case "--wslg": o = o with { Wslg = true }; break;
                case "--follow" or "-f": o = o with { Follow = true }; break;
                case "--case" or "-c": o = o with { CaseSensitive = true }; break;
                case "--regex" or "-e": o = o with { Regex = true }; break;
                case "--word" or "-w": o = o with { WholeWord = true }; break;
                case "--count": o = o with { CountOnly = true }; break;

                case "--log":
                {
                    string? v = Value();
                    if (string.IsNullOrWhiteSpace(v)) return Fail("--log needs a file path, as in --log bejview.log.");
                    o = o with { LogFile = v };
                    break;
                }
                case "--line" or "-n":
                {
                    string? v = Value();
                    if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 1)
                        return Fail($"--line needs a line number from 1 up (got '{v}').");
                    o = o with { Line = n };
                    break;
                }
                case "--find":
                {
                    string? v = Value();
                    if (string.IsNullOrEmpty(v)) return Fail("--find needs the text to search for, as in --find ERROR.");
                    o = o with { Find = v };
                    break;
                }
                case "--max":
                {
                    string? v = Value();
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int m) || m < 1)
                        return Fail($"--max needs a number of matches from 1 up (got '{v}').");
                    o = o with { MaxHits = m };
                    break;
                }
                case "--encoding":
                {
                    string? v = Value();
                    if (!TryParseEncoding(v, out var k))
                        return Fail($"--encoding must be utf8, utf16le, utf16be or latin1 (got '{v}').");
                    o = o with { Encoding = k };
                    break;
                }
                default:
                    return Fail($"Unknown option '{arg}'.");  // Program adds the --help hint to every error
            }
        }

        switch (o.Command)
        {
            case Command.Gui:
                if (positional.Count > 1) return Fail($"Give one file to open (got {positional.Count}: {string.Join(", ", positional)}).");
                o = o with { File = positional.FirstOrDefault() };
                if ((o.Line != null || o.Find != null || o.Follow) && o.File == null)
                    return Fail("--line, --find and --follow need a file, as in: bejview app.log --follow");
                break;
            case Command.Info:
                if (positional.Count != 1) return Fail("info needs one file, as in: bejview info app.log");
                o = o with { File = positional[0] };
                break;
            case Command.Find:
                if (positional.Count != 2 && !(positional.Count == 1 && o.Find != null))
                    return Fail("find needs a file and the text to search for, as in: bejview find app.log ERROR");
                o = o with { File = positional[0], Find = positional.Count == 2 ? positional[1] : o.Find };
                break;
            default:
                if (positional.Count > 0) return Fail($"'{positional[0]}' isn't used by this command.");
                break;
        }

        bool searchOptions = o.CaseSensitive || o.Regex || o.WholeWord || o.MaxHits != null || o.CountOnly;
        if (searchOptions && o.Command != Command.Find && o.Find == null)
            return Fail("--case, --regex, --word, --max and --count go with find (or --find), as in: bejview find app.log error --word");
        if (o.CountOnly && o.Command != Command.Find)
            return Fail("--count goes with the find command.");

        return new ParseResult(o, null);
    }

    public static bool TryParseEncoding(string? s, out TextEncodingKind kind)
    {
        kind = TextEncodingKind.Utf8;
        switch (s?.Trim().ToLowerInvariant().Replace("-", "").Replace("_", ""))
        {
            case "utf8": kind = TextEncodingKind.Utf8; return true;
            case "utf16le" or "utf16" or "unicode": kind = TextEncodingKind.Utf16LE; return true;
            case "utf16be": kind = TextEncodingKind.Utf16BE; return true;
            case "latin1" or "iso88591": kind = TextEncodingKind.Latin1; return true;
            default: return false;
        }
    }

    private static ParseResult Fail(string message) => new(null, message);

    public const string HelpText = """
        BejView: view and search very large text files. Read-only: it never changes a file.

        Usage:
          bejview [FILE] [--line N] [--find TEXT] [--follow]   open the window
          bejview info FILE                 print size, encoding, line count and indexing speed
          bejview find FILE TEXT [options]  print matching lines as LINE:COLUMN: text (like grep)
          bejview paths                     show where the settings and the log are
          bejview --version | --help

        Window options:
          -n, --line N        go to line N (1 = first line)
          --find TEXT         search for TEXT once the file is open
          -f, --follow        follow the end of the file as it grows (like tail -f)

        Search options (find, or the window with --find):
          -c, --case          match upper and lower case exactly
          -w, --word          match whole words only
          -e, --regex         TEXT is a .NET regular expression
          --max N             list at most N matches (default 10000; all are still counted)
          --count             find: print only the number of matches

        File options:
          --encoding utf8|utf16le|utf16be|latin1   read the file this way instead of detecting it

        Logging and troubleshooting:
          -v, --verbose       log every step, and also print it to the terminal
          --log FILE          write the log to FILE instead of the default log file

        WSL:
          Under WSL, a file on /mnt/<drive> opens in the Windows BejView.exe (much faster on big files).
          BejView looks for it in $BEJVIEW_WINDOWS_EXE, next to this program, then on PATH.
          info, find and paths always run in Linux.
          --wslg              show the Linux window in WSLg instead

        Windows terminals:
          Type "bejview" without ".exe": that runs BejView.com, which waits for info, find, paths,
          --help, --version and --verbose and passes the text and exit code back to the terminal.

        Exit codes:
          0  ok (find: matches found)
          1  find found nothing
          2  bad arguments
          3  the file couldn't be opened or read
          4  the search text can't be used
          5  BejView.com couldn't find or start BejView.exe (keep the two files together),
             or on Linux a system library is missing (the message names the package)

        Enjoy BejView!
        """;
}
