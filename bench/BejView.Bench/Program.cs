using System.Diagnostics;
using System.Globalization;
using System.Text;
using BejView.Core.Documents;
using BejView.Core.Search;

// Usage:
//   bench gen FILE SIZE_MB             make a log-like test file (about 100-byte lines, a rare word "ZQXJ" every 50,000 lines)
//   bench new FILE WORD                2.0: open, index, search (ignore case, then exact case), read the last screen
//   bench legacy FILE WORD             1.0: open, index, search (its only mode: ignore case)
// Prints one result line: key=value pairs.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length < 2) { Console.Error.WriteLine("usage: bench gen FILE SIZE_MB | bench new FILE WORD | bench legacy FILE WORD"); return 2; }

switch (args[0])
{
    case "gen": return Gen(args[1], long.Parse(args[2], CultureInfo.InvariantCulture));
    case "new": return await New(args[1], args[2]);
    case "legacy": return await Legacy(args[1], args[2]);
    default: Console.Error.WriteLine($"unknown mode {args[0]}"); return 2;
}

static int Gen(string path, long sizeMb)
{
    long target = sizeMb * 1024 * 1024;
    var r = new Random(12345);
    string[] levels = ["INFO ", "DEBUG", "WARN ", "ERROR"];
    string[] words = ["quest", "actor", "script", "stage", "alias", "timer", "event", "spell", "outpost", "ship", "café", "naïve"];
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    using var w = new StreamWriter(fs, new UTF8Encoding(false), 1 << 20);
    long written = 0, line = 0;
    var sb = new StringBuilder(200);
    var start = new DateTime(2026, 11, 11, 0, 0, 0);
    while (written < target)
    {
        sb.Clear();
        sb.Append(start.AddMilliseconds(line * 7).ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(levels[r.Next(levels.Length)]).Append(' ');
        int n = r.Next(6, 14);
        for (int i = 0; i < n; i++) sb.Append(words[r.Next(words.Length)]).Append('_').Append(r.Next(10000)).Append(' ');
        if (line % 50_000 == 49_999) sb.Append("ZQXJ marker ");
        sb.Append('\n');
        string s = sb.ToString();
        w.Write(s);
        written += Encoding.UTF8.GetByteCount(s);
        line++;
    }
    Console.WriteLine($"mode=gen file={path} bytes={written} lines={line}");
    return 0;
}

static async Task<int> New(string path, string word)
{
    var total = Stopwatch.StartNew();
    using var doc = TextDocument.Open(path);
    var sw = Stopwatch.StartNew();
    await doc.IndexAsync();
    double index = sw.Elapsed.TotalSeconds;

    sw.Restart();
    var screen = doc.ReadLines(doc.LineCount - 60, 60);
    double lastScreen = sw.Elapsed.TotalMilliseconds;
    sw.Restart();
    var mid = doc.ReadLines(doc.LineCount / 2, 60);
    double midScreen = sw.Elapsed.TotalMilliseconds;

    var s1 = SearchEngine.Run(doc, new SearchQuery(word));
    var s2 = SearchEngine.Run(doc, new SearchQuery(word, CaseSensitive: true));
    var s3 = SearchEngine.Run(doc, new SearchQuery(word, CaseSensitive: true), parallelism: 1);

    using var p = Process.GetCurrentProcess();
    p.Refresh();
    Console.WriteLine(string.Join(' ',
        "mode=new", $"bytes={doc.Length}", $"lines={doc.LineCount}",
        $"index_s={index:F2}", $"index_mb={doc.Index.MemoryBytes / 1048576.0:F1}",
        $"last_screen_ms={lastScreen:F1}", $"mid_screen_ms={midScreen:F1}",
        $"search_ignorecase_s={s1.Elapsed.TotalSeconds:F2}", $"hits={s1.TotalMatches}",
        $"search_case_s={s2.Elapsed.TotalSeconds:F2}", $"search_case_1thread_s={s3.Elapsed.TotalSeconds:F2}",
        $"peak_ws_mb={p.PeakWorkingSet64 / 1048576.0:F0}", $"peak_private_mb={p.PeakPagedMemorySize64 / 1048576.0:F0}",
        $"total_s={total.Elapsed.TotalSeconds:F2}", $"first_line_ok={screen.Count == 60 && mid.Count == 60}"));
    return 0;
}

static async Task<int> Legacy(string path, string word)
{
    var total = Stopwatch.StartNew();
    var h = new BejView.LargeFileHandler();
    var done = new TaskCompletionSource();
    h.LineIndexingComplete += (_, _) => done.TrySetResult();
    var sw = Stopwatch.StartNew();
    if (!h.OpenFile(path)) { Console.WriteLine("mode=legacy error=open_failed"); return 3; }
    await done.Task;
    double index = sw.Elapsed.TotalSeconds;

    sw.Restart();
    for (int i = h.TotalLines - 60; i < h.TotalLines; i++) h.ReadLine(i);
    double lastScreen = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    var hits = await h.SearchAsync(word);
    double search = sw.Elapsed.TotalSeconds;

    using var p = Process.GetCurrentProcess();
    p.Refresh();
    Console.WriteLine(string.Join(' ',
        "mode=legacy", $"bytes={h.FileSize}", $"lines={h.TotalLines}",
        $"index_s={index:F2}", $"last_screen_ms={lastScreen:F1}",
        $"search_ignorecase_s={search:F2}", $"hits={hits.Count}",
        $"peak_ws_mb={p.PeakWorkingSet64 / 1048576.0:F0}", $"peak_private_mb={p.PeakPagedMemorySize64 / 1048576.0:F0}",
        $"total_s={total.Elapsed.TotalSeconds:F2}"));
    h.Dispose();
    return 0;
}
