using System.Text;
using BejView.Core.Documents;
using BejView.Core.IO;
using BejView.Core.Text;

namespace BejView.Tests;

/// <summary>Opening, reading, live logs and the 1.0 problems (PLAN.md items 4, 7, 9) fixed.</summary>
public sealed class DocumentTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UTF16_files_read_correctly_and_the_BOM_is_not_shown()
    {
        string text = "first line\r\nsecond line\r\nthird line\r\n";
        string p16 = _tmp.Write("u16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
        string p8 = _tmp.Write("u8.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("hello\nworld\n")]);

        using var d16 = TextDocument.Open(p16);
        using var d8 = TextDocument.Open(p8);
        await d16.IndexAsync(ct: Ct);
        await d8.IndexAsync(ct: Ct);

        Assert.Equal(["first line", "second line", "third line"], d16.ReadLines(0, 10).Select(l => l.Text));
        Assert.Equal("hello", d8.ReadLine(0).Text);
        Assert.Equal("UTF-16 LE", d16.Encoding.DisplayName);
    }

    [Fact]
    public async Task A_file_another_program_is_writing_opens_and_its_writer_keeps_writing()
    {
        string path = Path.Combine(_tmp.Path, "live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        writer.Write("line 1\nline 2\n"u8);
        writer.Flush();

        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);
        Assert.Equal(2, doc.LineCount);

        writer.Write("line 3\npartial"u8);   // 1.0 blocked this write (proof test P7b)
        writer.Flush();
        Assert.Equal(RefreshResult.Grew, await doc.RefreshAsync(Ct));
        Assert.Equal(4, doc.LineCount);
        Assert.True(doc.Index.LastLineIsOpen);
        Assert.Equal("partial", doc.ReadLine(3).Text);

        writer.Write(" now done\nline 5\n"u8);
        writer.Flush();
        Assert.Equal(RefreshResult.Grew, await doc.RefreshAsync(Ct));
        Assert.Equal(["line 1", "line 2", "line 3", "partial now done", "line 5"], doc.ReadLines(0, 10).Select(l => l.Text));
        Assert.Equal(RefreshResult.Unchanged, await doc.RefreshAsync(Ct));
    }

    [Fact]
    public async Task A_file_that_is_cut_short_or_rewritten_is_indexed_again()
    {
        string path = _tmp.Write("rotate.log", Encoding.UTF8.GetBytes("old 1\nold 2\nold 3\n"));
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);

        File.WriteAllText(path, "new\n");
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal(["new"], doc.ReadLines(0, 10).Select(l => l.Text));

        // Same start length but different bytes, and longer: caught by the start check.
        File.WriteAllText(path, "NEW\nmore lines here\nand more\n");
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal(3, doc.LineCount);
    }

    [Fact]
    public async Task A_rotated_log_is_followed_to_the_new_file()
    {
        // How the game rotates Papyrus.0.log: rename it away, then start a new file under the name. Found on beta 3:
        // the open handle followed the renamed file, so the new session never showed.
        string path = _tmp.Write("Papyrus.0.log", Encoding.UTF8.GetBytes("[10:08:24] Papyrus log opened\nold 2\nold 3\n"));
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);

        File.Move(path, Path.Combine(_tmp.Path, "Papyrus.1.log"));
        Assert.Equal(RefreshResult.Unchanged, await doc.RefreshAsync(Ct)); // no file under the name yet: keep the old one
        Assert.Equal(3, doc.LineCount);

        File.WriteAllText(path, "");                                       // the new file starts empty
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal(0, doc.Length);

        File.AppendAllText(path, "[11:00:00] Papyrus log opened\nnew 2\n");
        Assert.Equal(RefreshResult.Grew, await doc.RefreshAsync(Ct));
        Assert.Equal(["[11:00:00] Papyrus log opened", "new 2"], doc.ReadLines(0, 10).Select(l => l.Text));
        Assert.Equal(RefreshResult.Unchanged, await doc.RefreshAsync(Ct)); // same file again: no false switch

        // Deleted and made again (another way loggers start over), this time not empty.
        File.Delete(path);
        File.WriteAllText(path, "[12:00:00] Papyrus log opened\n");
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal(["[12:00:00] Papyrus log opened"], doc.ReadLines(0, 10).Select(l => l.Text));
    }

    [Fact]
    public async Task Reading_lines_of_a_file_cut_short_before_the_refresh_gives_nothing_instead_of_failing()
    {
        // Found while testing the rotation fix: the screen drew from the old index just after a log was rewritten in
        // place, and the exception reached the renderer.
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++) sb.Append($"old line {i}\n");
        string path = _tmp.Write("cut.log", Encoding.UTF8.GetBytes(sb.ToString()));
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);

        File.WriteAllText(path, "new\n");
        Assert.Empty(doc.ReadLines(1500, 10)); // between checkpoints, so the index has to scan the (now missing) bytes
        Assert.Equal(RefreshResult.Reindexed, await doc.RefreshAsync(Ct));
        Assert.Equal(["new"], doc.ReadLines(0, 10).Select(l => l.Text));
    }

    [Fact]
    public void BejView_never_holds_a_file_for_writing()
    {
        string path = _tmp.Write("ro.txt", "abc\n"u8.ToArray());
        using var doc = TextDocument.Open(path);
        // Another program can still write, rename and delete it while it's open.
        File.AppendAllText(path, "def\n");
        File.Move(path, path + ".moved");
        File.Delete(path + ".moved");
    }

    [Fact]
    public void Open_errors_say_what_happened_and_how_to_fix_it()
    {
        var ex = Assert.Throws<FileOpenException>(() => TextDocument.Open(Path.Combine(_tmp.Path, "missing.log")));
        Assert.Contains("doesn't exist", ex.Message);
        Assert.False(string.IsNullOrEmpty(ex.Fix));

        var ex2 = Assert.Throws<FileOpenException>(() => TextDocument.Open(_tmp.Path));
        Assert.Contains("folder", ex2.Message);
    }

    [Fact]
    public async Task Two_documents_index_at_the_same_time_without_mixing_up()
    {
        // 1.0 shared one list between two indexing tasks (proof test P9: a 3-line file showed up to 419,434 lines).
        byte[] big = new byte[50 * 1024 * 1024];
        for (int i = 9; i < big.Length; i += 10) big[i] = (byte)'\n';
        string bigPath = _tmp.Write("big.txt", big);
        string smallPath = _tmp.Write("small.txt", "1\n2\n3\n"u8.ToArray());

        using var a = TextDocument.Open(bigPath);
        using var b = TextDocument.Open(smallPath);
        var ta = a.IndexAsync(ct: Ct);
        await b.IndexAsync(ct: Ct);
        await ta;
        Assert.Equal(3, b.LineCount);
        Assert.Equal(big.Length / 10, a.LineCount);
    }

    [Fact]
    public async Task Indexing_can_be_cancelled_and_resumed()
    {
        byte[] big = new byte[40 * 1024 * 1024];
        for (int i = 4; i < big.Length; i += 5) big[i] = (byte)'\n';
        using var doc = TextDocument.Open(_tmp.Write("cancel.txt", big));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 0.1) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.IndexAsync(progress, cts.Token));
        Assert.False(doc.IsFullyIndexed);

        await doc.IndexAsync(ct: Ct);
        Assert.True(doc.IsFullyIndexed);
        Assert.Equal(big.Length / 5, doc.LineCount);
    }

    [Fact]
    public async Task Lines_show_without_CR_and_CharColumn_maps_bytes_to_characters()
    {
        string path = _tmp.Write("crlf.txt", Encoding.UTF8.GetBytes("ééé target\r\nnext\r\n"));
        using var doc = TextDocument.Open(path);
        await doc.IndexAsync(ct: Ct);
        var line = doc.ReadLine(0);
        Assert.Equal("ééé target", line.Text);
        Assert.Equal(4, doc.CharColumn(line, 7)); // 3 × 2-byte é + space = 7 bytes = 4 characters
    }

    [Fact]
    public async Task Empty_and_BOM_only_files_have_no_lines()
    {
        using var empty = TextDocument.Open(_tmp.Write("empty.txt", []));
        using var bomOnly = TextDocument.Open(_tmp.Write("bom.txt", [0xEF, 0xBB, 0xBF]));
        await empty.IndexAsync(ct: Ct);
        await bomOnly.IndexAsync(ct: Ct);
        Assert.Equal(0, empty.LineCount);
        Assert.Equal(0, bomOnly.LineCount);
        Assert.Empty(empty.ReadLines(0, 5));
    }

    [Fact]
    public async Task A_forced_encoding_is_used()
    {
        string path = _tmp.Write("latin.txt", Encoding.UTF8.GetBytes("café\n"));
        using var doc = TextDocument.Open(path, encoding: TextEncodingKind.Latin1);
        await doc.IndexAsync(ct: Ct);
        Assert.Equal("cafÃ©", doc.ReadLine(0).Text);
        Assert.True(doc.IsEncodingForced);
    }

    private sealed class SyncProgress(Action<double> a) : IProgress<double>
    {
        public void Report(double value) => a(value);
    }
}
