using System.Buffers;
using System.Diagnostics;
using BejView.Core.Index;
using BejView.Core.IO;
using BejView.Core.Logging;
using BejView.Core.Text;

namespace BejView.Core.Documents;

/// <summary>One line as shown on screen.</summary>
/// <param name="Number">0-based line number.</param>
/// <param name="Text">The line without its line break (CR and LF stripped), cut at the display limit.</param>
/// <param name="ByteStart">Where the line starts in the file.</param>
/// <param name="ByteLength">The whole line's length in bytes, line break included, even when <see cref="Text"/> was cut.</param>
/// <param name="Truncated">True when the line is longer than the display limit and <see cref="Text"/> holds only its start.</param>
public sealed record LineText(long Number, string Text, long ByteStart, long ByteLength, bool Truncated);

public enum RefreshResult
{
    Unchanged,
    Grew,
    /// <summary>The file got shorter or its start changed (a rotated or rewritten log): it was indexed again from the start.</summary>
    Reindexed,
}

/// <summary>
/// An open text file: its encoding, its line index and line reading. Read-only. Thread-safe for reading while one
/// indexing pass runs (<see cref="IndexAsync"/> and <see cref="RefreshAsync"/> take turns).
/// </summary>
public sealed class TextDocument : IDisposable
{
    /// <summary>Default cap on how much of one line is shown (64 KB). Longer lines show their start and a "continues" mark.</summary>
    public const int DefaultMaxLineBytes = 64 * 1024;

    private const int HeadBytes = 64 * 1024;
    private const int FingerprintBytes = 256;

    private readonly Logger _log;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private byte[] _fingerprint;

    /// <summary>True when the user picked the encoding (it's kept when the file is indexed again).</summary>
    public bool IsEncodingForced { get; private init; }

    private FileSource _source;
    private readonly List<FileSource> _retired = [];

    /// <summary>
    /// The open file. In follow mode it can change: when the log was rotated (renamed away and a new file made under its
    /// name), the document switches to the new file. The old one stays open until the document closes, because the
    /// screen may still be reading from it.
    /// </summary>
    public FileSource Source { get => Volatile.Read(ref _source); private set => Volatile.Write(ref _source, value); }
    public TextEncoding Encoding { get; private set; }
    public LineIndex Index { get; private set; }
    public string Path => Source.Path;

    /// <summary>The longest part of one line returned by <see cref="ReadLines"/>, in bytes.</summary>
    public int MaxLineBytes { get; }

    private TextDocument(FileSource source, TextEncoding encoding, int maxLineBytes, Logger log, byte[] fingerprint)
    {
        _source = source;
        Encoding = encoding;
        MaxLineBytes = maxLineBytes;
        _log = log;
        _fingerprint = fingerprint;
        Index = new LineIndex(source, encoding);
    }

    /// <summary>
    /// Opens a file and detects its encoding (or uses <paramref name="encoding"/>). Doesn't index yet: call
    /// <see cref="IndexAsync"/>. Throws <see cref="FileOpenException"/> with a message and a fix.
    /// </summary>
    public static TextDocument Open(string path, Logger? log = null, TextEncodingKind? encoding = null,
        int maxLineBytes = DefaultMaxLineBytes)
    {
        log ??= Logger.Null;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 256);
        var source = FileSource.Open(path);
        try
        {
            byte[] head = new byte[(int)Math.Min(HeadBytes, source.Length)];
            int n = source.Read(0, head);
            var span = head.AsSpan(0, n);
            var enc = encoding is { } k ? TextEncoding.FromKind(k, span) : TextEncoding.Detect(span);
            log.Info($"Opened {source.Path}: {source.Length:N0} bytes, {enc.DisplayName} ({enc.Reason}).");
            return new TextDocument(source, enc, maxLineBytes, log, span[..Math.Min(n, FingerprintBytes)].ToArray())
            {
                IsEncodingForced = encoding.HasValue,
            };
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Tests: replace the (still empty) index with one that has small checkpoint spacing.</summary>
    internal void UseIndexSpacing(int linesPerCheckpoint, int bytesPerCheckpoint, int readBlock)
    {
        if (Index.ScannedTo > Index.ContentStart) throw new InvalidOperationException("The index is already built.");
        Index = new LineIndex(Source, Encoding, linesPerCheckpoint, bytesPerCheckpoint, readBlock);
        _spacing = (linesPerCheckpoint, bytesPerCheckpoint, readBlock);
    }

    private (int Lines, int Bytes, int Block)? _spacing;

    private LineIndex NewIndex() => _spacing is { } s
        ? new LineIndex(Source, Encoding, s.Lines, s.Bytes, s.Block)
        : new LineIndex(Source, Encoding);

    public long LineCount => Index.LineCount;
    public long Length => Source.Length;

    /// <summary>True once everything up to the length seen at the last open or refresh is indexed.</summary>
    public bool IsFullyIndexed => Index.ScannedTo >= AlignedLength(Source.Length);

    /// <summary>Indexes the file up to its current length on a background thread. Progress is 0..1.</summary>
    public async Task IndexAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => ScanToEnd(progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Checks the file again (for follow mode): indexes new lines when it grew, or indexes it again from the start when
    /// it got shorter or its first bytes changed (a rotated or rewritten log).
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RefreshCore(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private RefreshResult RefreshCore(CancellationToken ct)
    {
        long before = Index.ScannedTo;
        bool rotated = SwitchIfRotated();
        long len = Source.RefreshLength();
        bool rewritten = rotated || len < before || !StartMatchesFingerprint();
        if (rewritten)
        {
            _log.Info(rotated
                ? $"{Path} now names a new file (the log was rotated); following the new one from the start."
                : $"{Path} got shorter or was rewritten ({before:N0} -> {len:N0} bytes); indexing it again.");
            byte[] head = new byte[(int)Math.Min(HeadBytes, len)];
            int n = Source.Read(0, head);
            _fingerprint = head.AsSpan(0, Math.Min(n, FingerprintBytes)).ToArray();
            Encoding = IsEncodingForced
                ? TextEncoding.FromKind(Encoding.Kind, head.AsSpan(0, n))
                : TextEncoding.Detect(head.AsSpan(0, n));
            Index = NewIndex();
            ScanToEnd(null, ct);
            return RefreshResult.Reindexed;
        }
        if (AlignedLength(len) <= before) return RefreshResult.Unchanged;
        ScanToEnd(null, ct);
        return RefreshResult.Grew;
    }

    /// <summary>
    /// Logs are rotated by renaming the old file away and starting a new one under the same name (the game turns
    /// Papyrus.0.log into Papyrus.1.log at each start). The open handle follows the renamed file, so open the name again
    /// and compare the first bytes: a different start, or a file shorter than what we hold, means the name now holds
    /// another file, and the document switches to it. While the name is missing (between the rename and the new file),
    /// it keeps showing the old one. The start of a log never changes while it grows, so a writer appending can't
    /// cause a false switch.
    /// </summary>
    private bool SwitchIfRotated()
    {
        FileSource fresh;
        try
        {
            fresh = FileSource.Open(Path);
        }
        catch (FileOpenException)
        {
            return false;
        }

        var current = Source;
        Span<byte> ours = stackalloc byte[FingerprintBytes];
        Span<byte> theirs = stackalloc byte[FingerprintBytes];
        int n = current.Read(0, ours);
        int m = fresh.Read(0, theirs);
        if (m >= n && theirs[..n].SequenceEqual(ours[..n]))
        {
            fresh.Dispose();
            return false;
        }
        lock (_retired) _retired.Add(current);
        Source = fresh;
        return true;
    }

    private bool StartMatchesFingerprint()
    {
        if (_fingerprint.Length == 0) return true;
        Span<byte> now = stackalloc byte[FingerprintBytes];
        int n = Source.Read(0, now[.._fingerprint.Length]);
        return n == _fingerprint.Length && now[..n].SequenceEqual(_fingerprint);
    }

    private void ScanToEnd(IProgress<double>? progress, CancellationToken ct)
    {
        long end = Source.Length;
        long start = Index.ScannedTo;
        var sw = Stopwatch.StartNew();
        IProgress<long>? bytes = progress == null || end <= 0 ? null
            : new InlineProgress<long>(p => progress.Report((double)p / end));
        try
        {
            Index.Scan(end, bytes, ct);
        }
        catch (OperationCanceledException)
        {
            _log.Info($"Indexing {Path} cancelled at {Index.ScannedTo:N0} of {end:N0} bytes.");
            throw;
        }
        progress?.Report(1.0);
        double secs = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
        _log.Info($"Indexed {Path}: {end - start:N0} bytes in {sw.Elapsed.TotalSeconds:F2} s ({(end - start) / secs / 1e6:F0} MB/s); " +
                  $"{Index.LineCount:N0} lines, {Index.CheckpointCount:N0} checkpoints ({Index.MemoryBytes / 1024:N0} KB).");
    }

    private long AlignedLength(long len) =>
        Encoding.UnitSize == 2 ? Encoding.BomLength + ((len - Encoding.BomLength) & ~1L) : len;

    public LineText ReadLine(long line) => ReadLines(line, 1)[0];

    /// <summary>
    /// Reads up to <paramref name="count"/> lines from <paramref name="first"/> (fewer near the end). Each line holds at
    /// most <see cref="MaxLineBytes"/> bytes of text; a longer line is marked <see cref="LineText.Truncated"/>.
    /// </summary>
    public IReadOnlyList<LineText> ReadLines(long first, int count)
    {
        var index = Index;
        long lines = index.LineCount;
        if (first < 0 || count <= 0 || first >= lines) return [];
        count = (int)Math.Min(count, lines - first);

        var result = new List<LineText>(count);
        int nlLen = Encoding.NewlineLength;
        int cap = MaxLineBytes + nlLen;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(cap);
        // Read through the index's own file: after a log rotation Source is already the new file while this index
        // (and its offsets) can still be the old one's.
        var src = index.Source;
        try
        {
            long start;
            try
            {
                start = index.LineStart(first);
            }
            catch (FileChangedUnderIndexException)
            {
                return []; // the file was cut short under the screen; the next refresh reads it again
            }
            long scanned = index.ScannedTo;
            for (long line = first; line < first + count; line++)
            {
                int n = src.Read(start, buffer.AsSpan(0, (int)Math.Min(cap, Math.Max(0, scanned - start))));
                if (Encoding.UnitSize == 2) n &= ~1;
                var span = buffer.AsSpan(0, n);
                int nl = Encoding.IndexOfNewline(span);
                long next;
                int textBytes;
                bool truncated;
                if (nl >= 0)
                {
                    textBytes = nl;
                    next = start + nl + nlLen;
                    truncated = false;
                }
                else if (start + n >= scanned)
                {
                    textBytes = n;          // last line, no line break (yet)
                    next = scanned;
                    truncated = false;
                }
                else
                {
                    textBytes = Math.Min(n, MaxLineBytes);
                    next = line + 1 < lines ? index.LineStart(line + 1) : scanned;
                    truncated = true;
                }

                result.Add(new LineText(line, Decode(span[..textBytes], truncated), start, next - start, truncated));
                start = next;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return result;
    }

    /// <summary>Decodes line bytes, dropping a final CR (CRLF files) and a character cut short at the display limit.</summary>
    private string Decode(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (truncated && Encoding.Kind == TextEncodingKind.Utf8)
            bytes = bytes[..TextEncoding.Utf8CompleteLength(bytes)];
        string s = Encoding.Encoding.GetString(bytes);
        if (!truncated && s.EndsWith('\r')) s = s[..^1];
        return s;
    }

    /// <summary>The character column in <paramref name="line"/>'s text of byte <paramref name="byteOffset"/>, or -1 when it's past the shown part.</summary>
    public int CharColumn(LineText line, long byteOffset)
    {
        long rel = byteOffset - line.ByteStart;
        if (rel < 0 || rel > MaxLineBytes) return -1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(rel, 1));
        try
        {
            int n = Source.Read(line.ByteStart, buffer.AsSpan(0, (int)rel));
            int col = Encoding.Encoding.GetCharCount(buffer, 0, n);
            return col <= line.Text.Length ? col : -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The byte offset of character <paramref name="column"/> in <paramref name="line"/>'s shown text (the inverse of
    /// <see cref="CharColumn"/>). Exact for every encoding, including UTF-8 with invalid bytes (each shown U+FFFD is
    /// matched to the bytes it stands for). A column past the text gives the end of the text, before the line break.
    /// </summary>
    public long ByteOffsetOfColumn(LineText line, int column)
    {
        column = Math.Clamp(column, 0, line.Text.Length);
        if (column == 0) return line.ByteStart;
        switch (Encoding.Kind)
        {
            case TextEncodingKind.Utf16LE or TextEncodingKind.Utf16BE: return line.ByteStart + 2L * column;
            case TextEncodingKind.Latin1: return line.ByteStart + column;
        }

        int max = (int)Math.Min(line.ByteLength, MaxLineBytes + 4L);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(max, 1));
        try
        {
            int n = Source.Read(line.ByteStart, buffer.AsSpan(0, max));
            var bytes = buffer.AsSpan(0, n);
            int pos = 0, chars = 0;
            while (chars < column && pos < bytes.Length)
            {
                _ = System.Text.Rune.DecodeFromUtf8(bytes[pos..], out var rune, out int used);
                chars += rune.Utf16SequenceLength;
                pos += used;
            }
            return line.ByteStart + pos;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies bytes [<paramref name="start"/>, <paramref name="end"/>) of the file to <paramref name="destination"/>
    /// unchanged (for "Save selection"). Progress reports bytes copied. Returns the number of bytes copied.
    /// </summary>
    public long CopyBytes(long start, long end, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, Source.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long copied = 0;
        try
        {
            for (long pos = start; pos < end;)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, end - pos);
                int n = Source.Read(pos, buffer.AsSpan(0, want));
                if (n <= 0) break;
                destination.Write(buffer, 0, n);
                pos += n;
                copied += n;
                progress?.Report(copied);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return copied;
    }

    public void Dispose()
    {
        Source.Dispose();
        lock (_retired)
        {
            foreach (var old in _retired) old.Dispose();
            _retired.Clear();
        }
        _scanGate.Dispose();
    }

    /// <summary>Calls the handler on the reporting thread (Progress&lt;T&gt; would post to a UI context and pile up).</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
