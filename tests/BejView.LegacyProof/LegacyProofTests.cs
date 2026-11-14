using System.Text;
using BejView;

namespace BejView.LegacyProof;

/// <summary>
/// Each test reproduces one problem from PLAN.md ("Problems I found by reading the code") against the unchanged
/// 1.0 <see cref="LargeFileHandler"/>. The assertions describe what 1.0 does wrong, so every test passing means
/// every problem is real. The 2.0 tests (BejView.Tests) check the same inputs give the right answers.
/// </summary>
public sealed class LegacyProofTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bejview-proof-" + Guid.NewGuid().ToString("N"));
    private readonly List<LargeFileHandler> _handlers = [];

    public LegacyProofTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var h in _handlers) h.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Opens a file with 1.0 and waits for its background line indexing to finish.</summary>
    private LargeFileHandler OpenAndIndex(string path)
    {
        var h = new LargeFileHandler();
        _handlers.Add(h);
        using var done = new ManualResetEventSlim();
        h.LineIndexingComplete += (_, _) => done.Set();
        Assert.True(h.OpenFile(path), "1.0 failed to open the test file");
        Assert.True(done.Wait(TimeSpan.FromMinutes(2)), "1.0 indexing did not finish");
        return h;
    }

    // Problem 2: one 8-byte entry per line in a growing List<long>, and int line counts.
    [Fact]
    public void P2_Index_costs_at_least_8_bytes_per_line_and_counts_lines_in_an_int()
    {
        const int lines = 5_000_000;
        byte[] data = new byte[lines * 2];
        for (int i = 0; i < data.Length; i += 2) { data[i] = (byte)'x'; data[i + 1] = (byte)'\n'; }
        string path = WriteFile("many-lines.txt", data);

        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var h = OpenAndIndex(path);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(lines + 1, h.TotalLines);
        // List<long> doubling allocates about 16 bytes per line in total (8 for the final array, 8 for the copies on the way).
        Assert.True(allocated >= 8L * lines, $"expected >= {8L * lines:N0} bytes allocated, got {allocated:N0}");
        Assert.Equal(typeof(int), typeof(LargeFileHandler).GetProperty(nameof(LargeFileHandler.TotalLines))!.PropertyType);
        TestContext.Current.TestOutputHelper?.WriteLine($"1.0 allocated {allocated:N0} bytes ({(double)allocated / lines:F1} per line) to index {lines:N0} lines.");
    }

    // Problem 3: a character position is added to a byte position, so UTF-8 text with non-ASCII characters
    // puts search hits on the wrong line.
    [Fact]
    public async Task P3_Search_reports_the_wrong_line_after_non_ASCII_text()
    {
        // Line 1 is 100 'é' (200 bytes, 100 characters). The word is on line 2 (index 1).
        string text = new string('é', 100) + "\n" + "target here\n" + "last\n";
        string path = WriteFile("utf8.txt", Encoding.UTF8.GetBytes(text));
        var h = OpenAndIndex(path);

        var hits = await h.SearchAsync("target");

        Assert.Single(hits);
        Assert.Equal(0, hits[0].LineNumber); // wrong: the word is on line index 1
    }

    // Problem 4a: UTF-16 line breaks are found by the single byte 0x0A, so lines are cut in the middle of a character.
    [Fact]
    public void P4_UTF16_lines_are_garbled()
    {
        string text = "first line\r\nsecond line\r\nthird line\r\n";
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
        string path = WriteFile("utf16.txt", bytes);
        var h = OpenAndIndex(path);

        Assert.NotEqual("second line", h.ReadLine(1));
        TestContext.Current.TestOutputHelper?.WriteLine($"1.0 line 2 of a UTF-16 file reads as: {Escape(h.ReadLine(1))}");
    }

    // Problem 4b: the UTF-8 byte order mark is shown as a character at the start of line 1.
    [Fact]
    public void P4_UTF8_BOM_shows_on_line_1()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("hello\nworld\n")];
        string path = WriteFile("bom.txt", bytes);
        var h = OpenAndIndex(path);

        Assert.Equal("﻿hello", h.ReadLine(0));
    }

    // Problem 5: every match is kept, with copies of its context lines, and there is no limit.
    [Fact]
    public async Task P5_Search_keeps_every_match_with_no_limit()
    {
        const int lines = 200_000;
        var sb = new StringBuilder(lines * 8);
        for (int i = 0; i < lines; i++) sb.Append("the cat\n");
        string path = WriteFile("common.txt", Encoding.UTF8.GetBytes(sb.ToString()));
        var h = OpenAndIndex(path);

        var hits = await h.SearchAsync("the", contextLines: 2);

        Assert.Equal(lines, hits.Count);
        Assert.All(hits.Take(100).Skip(5), r => Assert.Equal(5, r.ContextLines.Count));
    }

    // Problem 7: a file another program has open for writing (a live log) can't be opened.
    [Fact]
    public void P7_A_file_open_for_writing_by_another_program_cannot_be_opened()
    {
        string path = Path.Combine(_dir, "live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes("line 1\nline 2\n"));
        writer.Flush();

        var h = new LargeFileHandler();
        _handlers.Add(h);
        bool opened = h.OpenFile(path);
        // FileShare.Read on the writer is how most logging code opens its file; 1.0's memory map asks for no
        // write sharing, so Windows refuses. If this ever opens, record the platform in NOTES.md.
        if (OperatingSystem.IsWindows()) Assert.False(opened);
        TestContext.Current.TestOutputHelper?.WriteLine($"1.0 OpenFile on a file open for writing returned {opened}.");
    }

    // Problem 7b (found while proving 7): while 1.0 has a file open, the program that writes it can't write any more.
    // A game or Vortex log open in BejView 1.0 blocks that program's own logging.
    [Fact]
    public void P7_While_open_in_1_0_the_file_cannot_be_written_by_its_owner()
    {
        string path = WriteFile("grow.log", Encoding.UTF8.GetBytes("a\nb\n"));
        var h = OpenAndIndex(path);

        if (OperatingSystem.IsWindows())
            Assert.Throws<IOException>(() => File.AppendAllText(path, "c\nd\n"));
        else
            File.AppendAllText(path, "c\nd\n"); // Linux has no mandatory sharing locks

        // Either way the open file never shows new lines.
        Assert.Equal(3, h.TotalLines);
    }

    // Problem 8: one very long line is read into memory whole (bytes plus a string twice that size).
    [Fact]
    public void P8_A_long_line_is_read_whole()
    {
        const int size = 64 * 1024 * 1024;
        byte[] data = new byte[size + 1];
        Array.Fill(data, (byte)'a');
        data[^1] = (byte)'\n';
        string path = WriteFile("one-line.txt", data);
        var h = OpenAndIndex(path);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string line = h.ReadLine(0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(size, line.Length);
        Assert.True(allocated >= 3L * size, $"expected >= {3L * size:N0} bytes for one ReadLine, got {allocated:N0}");
    }

    // Problem 9: opening a second file while the first is still indexing shares one list between two indexing tasks.
    // Timing-dependent, so it's Explicit: run it by hand (dotnet test -- --explicit only) and record the result.
    [Fact(Explicit = true)]
    public void P9_Opening_a_second_file_during_indexing_corrupts_the_index()
    {
        byte[] big = new byte[200 * 1024 * 1024];
        for (int i = 9; i < big.Length; i += 10) big[i] = (byte)'\n';
        string bigPath = WriteFile("big.txt", big);
        string smallPath = WriteFile("small.txt", Encoding.UTF8.GetBytes("1\n2\n3\n"));

        int wrong = 0;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var h = new LargeFileHandler();
            _handlers.Add(h);
            int completions = 0;
            h.LineIndexingComplete += (_, _) => Interlocked.Increment(ref completions);
            h.OpenFile(bigPath);
            Thread.Sleep(20);
            h.OpenFile(smallPath);
            SpinWait.SpinUntil(() => Volatile.Read(ref completions) >= 2, TimeSpan.FromMinutes(1));
            if (h.TotalLines != 4) wrong++;
            TestContext.Current.TestOutputHelper?.WriteLine($"attempt {attempt}: small file shows {h.TotalLines:N0} lines (should be 4)");
        }
        Assert.True(wrong > 0, "the race did not show in 10 attempts");
    }

    // Problem 10: the unused ReplaceAsync. Reading suggested it could rewrite the file at the wrong place; running it shows
    // it can't write at all: CloseFile() empties filePath before File.Copy(temp, filePath), so it always fails, leaves the
    // original untouched, and leaves its temporary copy behind in %TEMP%. 1.0 can't damage files; 2.0 drops the code.
    [Fact]
    public async Task P10_ReplaceAsync_always_fails_and_leaves_a_temp_file()
    {
        string text = new string('é', 10) + "\n" + "old value\n";
        byte[] original = Encoding.UTF8.GetBytes(text);
        string path = WriteFile("replace.txt", original);
        var h = OpenAndIndex(path);
        var hits = await h.SearchAsync("old");
        Assert.Single(hits);

        string tempDir = Path.GetTempPath();
        var tempsBefore = Directory.GetFiles(tempDir, "tmp*.tmp").ToHashSet();
        var seen = new List<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> log =
            (_, e) => { lock (seen) seen.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}"); };
        AppDomain.CurrentDomain.FirstChanceException += log;
        bool ok;
        try { ok = await h.ReplaceAsync(hits, "new"); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= log; }

        Assert.False(ok);
        Assert.Contains(seen, s => s.Contains("destFileName"));
        Assert.Equal(original, File.ReadAllBytes(path));
        var leftover = Directory.GetFiles(tempDir, "tmp*.tmp").Where(f => !tempsBefore.Contains(f)).ToList();
        Assert.NotEmpty(leftover);
        foreach (string f in leftover) { try { File.Delete(f); } catch (IOException) { } }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString());
        return sb.ToString();
    }
}
