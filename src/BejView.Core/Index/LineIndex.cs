using System.Buffers;
using BejView.Core.IO;
using BejView.Core.Text;

namespace BejView.Core.Index;

/// <summary>A known position in the file: <see cref="Offset"/> lies in line <see cref="Line"/> (0-based).</summary>
/// <param name="IsLineStart">True when <see cref="Offset"/> is where that line starts; false for a point inside a long line.</param>
public readonly record struct Checkpoint(long Offset, long Line, bool IsLineStart);

/// <summary>
/// The file no longer matches the index (a live log cut short or rewritten in place before follow mode noticed).
/// Readers skip that moment; the next refresh indexes the file again.
/// </summary>
public sealed class FileChangedUnderIndexException(string message) : InvalidOperationException(message);

/// <summary>
/// A sparse line index. 1.0 kept one 8-byte position for every line (about 2 GB for 260 million lines); this keeps a
/// checkpoint every <see cref="LinesPerCheckpoint"/> lines or every <see cref="BytesPerCheckpoint"/> bytes, whichever
/// comes first, and finds the lines in between with a fast scan of at most about that many bytes.
/// <list type="bullet">
/// <item>Memory: 16 bytes per checkpoint. A 26 GB log of 100-byte lines needs about 1 million checkpoints (16 MB).</item>
/// <item>Very long lines get checkpoints inside them too, so the line after a 1 GB line is found without reading 1 GB.</item>
/// <item>Line numbers and offsets are <c>long</c>: no limit at 2.1 billion lines.</item>
/// <item><see cref="Scan"/> can be called again after the file grows; it carries on where it stopped.</item>
/// </list>
/// One thread scans at a time (the caller makes sure of that); any number of threads can look lines up meanwhile.
/// </summary>
public sealed class LineIndex
{
    public const int DefaultLinesPerCheckpoint = 256;
    public const int DefaultBytesPerCheckpoint = 1 << 20;
    public const int DefaultReadBlock = 4 << 20;

    private const int ChunkBits = 16;
    private const int ChunkSize = 1 << ChunkBits;

    private readonly FileSource _src;

    /// <summary>The file this index was built from (in follow mode the document can move on to a new file).</summary>
    public FileSource Source => _src;
    private readonly TextEncoding _enc;
    private readonly int _readBlock;
    private readonly object _lock = new();

    // Checkpoints in chunked arrays (no big copies as they grow). Key = (line << 1) | (inside-a-line ? 1 : 0),
    // which never decreases along the file, so lookups by line are a binary search.
    private readonly List<long[]> _offsets = [];
    private readonly List<long[]> _keys = [];
    private long _count;

    // Scan state. Written by the scanning thread, published under _lock.
    private long _scannedTo;
    private long _newlines;
    private long _lastLineStart;
    private long _lastCheckpoint;
    private long _linesSinceCheckpoint;

    public int LinesPerCheckpoint { get; }
    public int BytesPerCheckpoint { get; }

    /// <summary>Where the text starts (after the byte order mark).</summary>
    public long ContentStart { get; }

    public LineIndex(FileSource src, TextEncoding enc,
        int linesPerCheckpoint = DefaultLinesPerCheckpoint,
        int bytesPerCheckpoint = DefaultBytesPerCheckpoint,
        int readBlock = DefaultReadBlock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(linesPerCheckpoint, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerCheckpoint, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(readBlock, 16);
        _src = src;
        _enc = enc;
        LinesPerCheckpoint = linesPerCheckpoint;
        BytesPerCheckpoint = bytesPerCheckpoint;
        _readBlock = readBlock & ~1;
        ContentStart = enc.BomLength;
        Reset();
    }

    /// <summary>Bytes of the file indexed so far.</summary>
    public long ScannedTo { get { lock (_lock) return _scannedTo; } }

    /// <summary>Lines found so far. A last line without a line break counts; an empty file has 0 lines.</summary>
    public long LineCount { get { lock (_lock) return CountLinesLocked(); } }

    public long CheckpointCount { get { lock (_lock) return _count; } }

    /// <summary>Approximate memory the index holds (allocated chunks).</summary>
    public long MemoryBytes { get { lock (_lock) return (long)_offsets.Count * ChunkSize * 16; } }

    /// <summary>True when the last line seen so far has no line break after it (it may still be growing).</summary>
    public bool LastLineIsOpen { get { lock (_lock) return _scannedTo > _lastLineStart; } }

    /// <summary>Forgets everything, for a file that was cut short or replaced.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _offsets.Clear();
            _keys.Clear();
            _count = 0;
            _scannedTo = ContentStart;
            _newlines = 0;
            _lastLineStart = ContentStart;
            _lastCheckpoint = ContentStart;
            _linesSinceCheckpoint = 0;
            AddLocked(ContentStart, 0, isLineStart: true);
        }
    }

    /// <summary>
    /// Indexes from where the last scan stopped up to <paramref name="end"/> (the file length seen by the caller).
    /// Reports progress as the byte offset reached. Throws <see cref="OperationCanceledException"/> when cancelled;
    /// what was indexed before that stays valid.
    /// </summary>
    public void Scan(long end, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long pos, newlines, lastLineStart, lastCp, linesSinceCp;
        lock (_lock)
        {
            pos = _scannedTo;
            newlines = _newlines;
            lastLineStart = _lastLineStart;
            lastCp = _lastCheckpoint;
            linesSinceCp = _linesSinceCheckpoint;
        }
        if (_enc.UnitSize == 2) end = ContentStart + ((end - ContentStart) & ~1L); // ignore a stray odd byte at the end
        if (end <= pos) return;

        int nlLen = _enc.NewlineLength;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_readBlock);
        try
        {
            while (pos < end)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(_readBlock, end - pos);
                int n = _src.Read(pos, buffer.AsSpan(0, want));
                if (_enc.UnitSize == 2) n &= ~1;
                if (n <= 0) break; // the file got shorter while we read; the next refresh notices
                ReadOnlySpan<byte> span = buffer.AsSpan(0, n);

                var pending = new List<(long Offset, long Line, bool Start)>();
                int i = 0;
                while (true)
                {
                    int rel = _enc.IndexOfNewline(span[i..]);
                    int segEnd = rel < 0 ? n : i + rel;
                    MidLineCheckpoints(span, pos, segEnd, newlines, lastLineStart, ref lastCp, ref linesSinceCp, pending);
                    if (rel < 0) break;

                    long lineStart = pos + segEnd + nlLen;
                    newlines++;
                    linesSinceCp++;
                    lastLineStart = lineStart;
                    if (linesSinceCp >= LinesPerCheckpoint || lineStart - lastCp >= BytesPerCheckpoint)
                    {
                        pending.Add((lineStart, newlines, true));
                        lastCp = lineStart;
                        linesSinceCp = 0;
                    }
                    i = segEnd + nlLen;
                    if (i >= n) break;
                }

                pos += n;
                lock (_lock)
                {
                    foreach (var p in pending) AddLocked(p.Offset, p.Line, p.Start);
                    _scannedTo = pos;
                    _newlines = newlines;
                    _lastLineStart = lastLineStart;
                    _lastCheckpoint = lastCp;
                    _linesSinceCheckpoint = linesSinceCp;
                }
                progress?.Report(pos);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Adds checkpoints inside a long line, every <see cref="BytesPerCheckpoint"/> bytes, up to block index <paramref name="segEnd"/>.</summary>
    private void MidLineCheckpoints(ReadOnlySpan<byte> span, long pos, int segEnd, long line, long lastLineStart,
        ref long lastCp, ref long linesSinceCp, List<(long, long, bool)> pending)
    {
        while (lastCp + BytesPerCheckpoint < pos + segEnd)
        {
            int rel = (int)(lastCp + BytesPerCheckpoint - pos);
            if (rel < 0) rel = 0;
            rel = _enc.AlignBack(span, rel);
            long off = pos + rel;
            if (off <= lastLineStart || off <= lastCp) break;
            pending.Add((off, line, false));
            lastCp = off;
            linesSinceCp = 0;
        }
    }

    /// <summary>Byte offset where line <paramref name="line"/> (0-based) starts.</summary>
    public long LineStart(long line)
    {
        Checkpoint cp;
        lock (_lock)
        {
            long lines = CountLinesLocked();
            if (line < 0 || line >= lines)
                throw new ArgumentOutOfRangeException(nameof(line), line, $"The file has {lines:N0} lines indexed.");
            long idx = LastIndexWithKeyAtMost(line << 1);
            cp = AtLocked(idx);
        }
        if (cp.Line == line && cp.IsLineStart) return cp.Offset;
        return SkipNewlines(cp.Offset, line - cp.Line);
    }

    /// <summary>Byte offset just past the end of line <paramref name="line"/>, including its line break.</summary>
    public long LineEnd(long line)
    {
        long next = line + 1;
        long lines = LineCount;
        if (next < lines) return LineStart(next);
        if (line == lines - 1) return ScannedTo;
        throw new ArgumentOutOfRangeException(nameof(line), line, $"The file has {lines:N0} lines indexed.");
    }

    /// <summary>The 0-based line that contains byte <paramref name="offset"/> (clamped to the indexed part of the file).</summary>
    public long LineOfOffset(long offset)
    {
        Checkpoint cp;
        lock (_lock)
        {
            offset = Math.Clamp(offset, ContentStart, Math.Max(ContentStart, _scannedTo));
            cp = AtLocked(FloorIndexLocked(offset));
        }
        return cp.Line + CountNewlines(cp.Offset, offset);
    }

    /// <summary>The last checkpoint at or before <paramref name="offset"/>, and its index.</summary>
    public (long Index, Checkpoint Checkpoint) Floor(long offset)
    {
        lock (_lock)
        {
            long i = FloorIndexLocked(offset);
            return (i, AtLocked(i));
        }
    }

    public Checkpoint CheckpointAt(long index)
    {
        lock (_lock)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return AtLocked(index);
        }
    }

    // ---- helpers ----

    private long CountLinesLocked() => _newlines + (_scannedTo > _lastLineStart ? 1 : 0);

    private void AddLocked(long offset, long line, bool isLineStart)
    {
        int chunk = (int)(_count >> ChunkBits);
        if (chunk == _offsets.Count)
        {
            _offsets.Add(new long[ChunkSize]);
            _keys.Add(new long[ChunkSize]);
        }
        int slot = (int)(_count & (ChunkSize - 1));
        _offsets[chunk][slot] = offset;
        _keys[chunk][slot] = (line << 1) | (isLineStart ? 0L : 1L);
        _count++;
    }

    private Checkpoint AtLocked(long i)
    {
        long off = _offsets[(int)(i >> ChunkBits)][(int)(i & (ChunkSize - 1))];
        long key = _keys[(int)(i >> ChunkBits)][(int)(i & (ChunkSize - 1))];
        return new Checkpoint(off, key >> 1, (key & 1) == 0);
    }

    private long KeyAt(long i) => _keys[(int)(i >> ChunkBits)][(int)(i & (ChunkSize - 1))];
    private long OffsetAt(long i) => _offsets[(int)(i >> ChunkBits)][(int)(i & (ChunkSize - 1))];

    private long LastIndexWithKeyAtMost(long key)
    {
        long lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            long mid = lo + (hi - lo + 1) / 2;
            if (KeyAt(mid) <= key) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private long FloorIndexLocked(long offset)
    {
        long lo = 0, hi = _count - 1;
        while (lo < hi)
        {
            long mid = lo + (hi - lo + 1) / 2;
            if (OffsetAt(mid) <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Position just after the <paramref name="count"/>-th line break at or after <paramref name="from"/>.</summary>
    private long SkipNewlines(long from, long count)
    {
        int nlLen = _enc.NewlineLength;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long pos = from;
            long end = ScannedTo;
            while (pos < end)
            {
                int n = _src.Read(pos, buffer.AsSpan(0, (int)Math.Min(64 * 1024, end - pos)));
                if (_enc.UnitSize == 2) n &= ~1;
                if (n <= 0) break;
                var span = buffer.AsSpan(0, n);
                int i = 0;
                while (true)
                {
                    int rel = _enc.IndexOfNewline(span[i..]);
                    if (rel < 0) break;
                    i += rel + nlLen;
                    if (--count == 0) return pos + i;
                    if (i >= n) break;
                }
                pos += n;
            }
            throw new FileChangedUnderIndexException($"The file changed under the index near offset {from:N0}; reopen it.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private long CountNewlines(long from, long to)
    {
        long count = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long pos = from;
            while (pos < to)
            {
                int n = _src.Read(pos, buffer.AsSpan(0, (int)Math.Min(64 * 1024, to - pos)));
                if (n <= 0) break;
                count += _enc.CountNewlines(buffer.AsSpan(0, n));
                pos += n;
            }
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
