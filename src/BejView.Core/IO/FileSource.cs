using Microsoft.Win32.SafeHandles;

namespace BejView.Core.IO;

/// <summary>
/// Read-only access to a file by offset. The file is opened with read, write and delete sharing, so the program that
/// writes it (a game log, a Vortex log) keeps writing while BejView shows it, and a file that grows is seen at the next
/// <see cref="RefreshLength"/>. BejView never opens a file for writing.
/// <para>Thread-safe: reads at an offset don't share a file position.</para>
/// </summary>
public sealed class FileSource : IDisposable
{
    private readonly SafeFileHandle _handle;
    private long _length;

    public string Path { get; }

    /// <summary>The length seen at open or at the last <see cref="RefreshLength"/>.</summary>
    public long Length => Volatile.Read(ref _length);

    private FileSource(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
    }

    /// <summary>Opens <paramref name="path"/> for reading. Throws <see cref="FileOpenException"/> with a plain message and fix.</summary>
    public static FileSource Open(string path)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileOpenException(path, $"'{path}' is not a valid file path.", "Check the spelling of the path.", ex);
        }

        if (Directory.Exists(full))
            throw new FileOpenException(full, $"{full} is a folder, not a file.", "Pick a file inside it.");

        try
        {
            var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            return new FileSource(full, handle);
        }
        catch (FileNotFoundException ex)
        {
            throw new FileOpenException(full, $"{full} doesn't exist.", "Check the path, or pick the file with File > Open.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileOpenException(full, $"The folder of {full} doesn't exist.", "Check the path.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOpenException(full, $"This account isn't allowed to read {full}.",
                "Open it from an account that can read it, or copy it somewhere this account can read.", ex);
        }
        catch (IOException ex)
        {
            throw new FileOpenException(full, $"{full} can't be opened ({ex.Message}).",
                "Another program may hold it locked without sharing; close that program or copy the file.", ex);
        }
    }

    /// <summary>Re-reads the file length. Returns the new length (larger when the file grew, smaller when it was cut or replaced).</summary>
    public long RefreshLength()
    {
        long len = RandomAccess.GetLength(_handle);
        Volatile.Write(ref _length, len);
        return len;
    }

    /// <summary>Reads up to <c>buffer.Length</c> bytes at <paramref name="offset"/>; returns how many were read (0 at the end).</summary>
    public int Read(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>A file couldn't be opened. <see cref="Exception.Message"/> says what happened; <see cref="Fix"/> says what to do.</summary>
public sealed class FileOpenException(string path, string message, string fix, Exception? inner = null)
    : IOException(message, inner)
{
    public string FilePath { get; } = path;
    public string Fix { get; } = fix;
}
