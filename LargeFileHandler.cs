using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace BejView;

/// <summary>
/// Handles operations on large text files using memory-mapped files for efficiency.
/// </summary>
public class LargeFileHandler : IDisposable
{
    // Constants
    private const int BUFFER_SIZE = 4 * 1024 * 1024; // 4MB buffer
    private const int MAX_CHUNK_SIZE = 100 * 1024 * 1024; // 100MB maximum chunk size

    // File properties
    private string filePath = string.Empty;
    private long fileSize;
    private MemoryMappedFile? memoryMappedFile;
    private List<long> linePositions = new();
    private int totalLines = 0;
    private Encoding encoding = Encoding.UTF8;

    // Events
    public event EventHandler<int>? LineIndexingProgress;
    public event EventHandler? LineIndexingComplete;
    public event EventHandler<int>? SearchProgress;
    public event EventHandler? SearchComplete;

    /// <summary>
    /// Gets the total number of lines in the file
    /// </summary>
    public int TotalLines => totalLines;

    /// <summary>
    /// Gets the size of the file in bytes
    /// </summary>
    public long FileSize => fileSize;

    /// <summary>
    /// Gets the path of the currently opened file
    /// </summary>
    public string FilePath => filePath;

    /// <summary>
    /// Opens a large text file using memory-mapped file access
    /// </summary>
    /// <param name="path">Path to the file</param>
    /// <returns>True if the file was opened successfully, false otherwise</returns>
    public bool OpenFile(string path)
    {
        try
        {
            // Close any previously opened file
            CloseFile();
            
            // Check if the file exists
            if (!File.Exists(path))
                return false;
            
            // Get file info
            FileInfo fileInfo = new FileInfo(path);
            fileSize = fileInfo.Length;
            filePath = path;
            
            // Create memory-mapped file
            memoryMappedFile = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            
            // Detect encoding
            DetectEncoding();
            
            // Start indexing lines in a background task
            Task.Run(() => IndexLines());
            
            return true;
        }
        catch (Exception)
        {
            CloseFile();
            return false;
        }
    }

    /// <summary>
    /// Closes the currently opened file
    /// </summary>
    public void CloseFile()
    {
        // Clear line positions
        linePositions.Clear();
        totalLines = 0;
        
        // Close the memory-mapped file
        if (memoryMappedFile != null)
        {
            memoryMappedFile.Dispose();
            memoryMappedFile = null;
        }
        
        // Reset file properties
        filePath = string.Empty;
        fileSize = 0;
    }

    /// <summary>
    /// Reads a chunk of text from the file
    /// </summary>
    /// <param name="startLine">The line index to start reading from</param>
    /// <param name="lineCount">The number of lines to read</param>
    /// <returns>The text content of the specified lines</returns>
    public string ReadLines(int startLine, int lineCount)
    {
        if (memoryMappedFile == null || linePositions.Count == 0)
            return string.Empty;
        
        // Ensure we don't go out of bounds
        startLine = Math.Max(0, Math.Min(startLine, totalLines - 1));
        lineCount = Math.Min(lineCount, totalLines - startLine);
        
        if (lineCount <= 0)
            return string.Empty;
        
        // Calculate the start and end positions in the file
        long startPosition = linePositions[startLine];
        long endPosition = (startLine + lineCount < totalLines) 
            ? linePositions[startLine + lineCount] 
            : fileSize;
        
        // Read the content
        return ReadFileContent(startPosition, endPosition - startPosition);
    }

    // Line cache for frequently accessed lines
    private readonly Dictionary<int, string> lineCache = new(1000); // Cache up to 1000 lines
    private readonly object cacheLock = new();

    /// <summary>
    /// Reads a specific line from the file
    /// </summary>
    /// <param name="lineIndex">The index of the line to read</param>
    /// <returns>The text content of the specified line</returns>
    public string ReadLine(int lineIndex)
    {
        if (memoryMappedFile == null || linePositions.Count == 0 || 
            lineIndex < 0 || lineIndex >= totalLines)
            return string.Empty;
        
        // Check if the line is in the cache
        lock (cacheLock)
        {
            if (lineCache.TryGetValue(lineIndex, out string? cachedLine))
                return cachedLine;
        }
        
        // Calculate the start and end positions in the file
        long startPosition = linePositions[lineIndex];
        long endPosition = (lineIndex + 1 < totalLines) 
            ? linePositions[lineIndex + 1] 
            : fileSize;
        
        // Read the content
        string line = ReadFileContent(startPosition, endPosition - startPosition).TrimEnd('\r', '\n');
        
        // Add to cache
        lock (cacheLock)
        {
            // Trim cache if it gets too large
            if (lineCache.Count >= 1000)
            {
                // Remove 20% of the oldest entries
                var keysToRemove = lineCache.Keys.Take(200).ToList();
                foreach (var key in keysToRemove)
                    lineCache.Remove(key);
            }
            
            lineCache[lineIndex] = line;
        }
        
        return line;
    }

    /// <summary>
    /// Searches for a text pattern in the file
    /// </summary>
    /// <param name="searchText">The text to search for</param>
    /// <param name="isCaseSensitive">Whether the search is case-sensitive</param>
    /// <param name="isRegex">Whether the search text is a regular expression</param>
    /// <param name="contextLines">Number of context lines to include before and after matches</param>
    /// <returns>A list of search results</returns>
    public async Task<List<SearchResult>> SearchAsync(string searchText, bool isCaseSensitive = false, 
        bool isRegex = false, int contextLines = 0)
    {
        List<SearchResult> results = new();
        
        if (memoryMappedFile == null || string.IsNullOrEmpty(searchText) || linePositions.Count == 0)
            return results;
        
        // Create regex options
        RegexOptions options = RegexOptions.Compiled;
        if (!isCaseSensitive)
            options |= RegexOptions.IgnoreCase;
        
        // Create the regex pattern
        Regex regex;
        try
        {
            if (isRegex)
                regex = new Regex(searchText, options);
            else
                regex = new Regex(Regex.Escape(searchText), options);
        }
        catch (ArgumentException)
        {
            // Invalid regex pattern
            return results;
        }
        
        // Search through the file in chunks
        await Task.Run(() => 
        {
            using var accessor = memoryMappedFile.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);
            
            // Process the file in chunks to avoid loading it all into memory
            long position = 0;
            int processedLines = 0;
            
            while (position < fileSize)
            {
                // Determine chunk size
                long chunkSize = Math.Min(MAX_CHUNK_SIZE, fileSize - position);
                
                // Find the end of the chunk at a line boundary
                int endLineIndex = FindLineIndex(position + chunkSize);
                if (endLineIndex < 0)
                    endLineIndex = totalLines - 1;
                
                long endPosition = (endLineIndex + 1 < totalLines) 
                    ? linePositions[endLineIndex + 1] 
                    : fileSize;
                
                chunkSize = endPosition - position;
                
                // Read the chunk
                string chunk = ReadFileContent(position, chunkSize);
                
                // Find the line index for the start of the chunk
                int startLineIndex = FindLineIndex(position);
                
                // Find matches in the chunk
                MatchCollection matches = regex.Matches(chunk);
                
                foreach (Match match in matches)
                {
                    // Find the line containing the match
                    long matchPosition = position + match.Index;
                    int lineIndex = FindLineIndex(matchPosition);
                    
                    if (lineIndex >= 0)
                    {
                        // Get the line content
                        string line = ReadLine(lineIndex);
                        
                        // Calculate match position within the line
                        long lineStart = linePositions[lineIndex];
                        int matchStart = (int)(matchPosition - lineStart);
                        
                        // Get context lines
                        int contextStartLine = Math.Max(0, lineIndex - contextLines);
                        int contextEndLine = Math.Min(totalLines - 1, lineIndex + contextLines);
                        
                        List<string> contextLinesList = new();
                        for (int i = contextStartLine; i <= contextEndLine; i++)
                        {
                            contextLinesList.Add(ReadLine(i));
                        }
                        
                        // Add to results
                        results.Add(new SearchResult
                        {
                            LineNumber = lineIndex,
                            LineContent = line,
                            MatchStart = matchStart,
                            MatchLength = match.Length,
                            ContextStartLine = contextStartLine,
                            ContextEndLine = contextEndLine,
                            ContextLines = contextLinesList
                        });
                    }
                }
                
                // Move to the next chunk
                position = endPosition;
                
                // Update progress
                processedLines = endLineIndex + 1;
                int progressPercentage = (int)((double)processedLines / totalLines * 100);
                SearchProgress?.Invoke(this, progressPercentage);
            }
            
            SearchComplete?.Invoke(this, EventArgs.Empty);
        });
        
        return results;
    }

    /// <summary>
    /// Replaces text in the file
    /// </summary>
    /// <param name="searchResults">The search results containing the text to replace</param>
    /// <param name="replaceText">The text to replace with</param>
    /// <returns>True if the replacement was successful, false otherwise</returns>
    public async Task<bool> ReplaceAsync(List<SearchResult> searchResults, string replaceText)
    {
        if (memoryMappedFile == null || searchResults.Count == 0 || string.IsNullOrEmpty(filePath))
            return false;
        
        try
        {
            // Create a temporary file for the modified content
            string tempFilePath = Path.GetTempFileName();
            
            await Task.Run(() => 
            {
                using var sourceAccessor = memoryMappedFile.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);
                using var destFile = File.Create(tempFilePath);
                
                // Sort search results by position
                searchResults.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
                
                long lastEnd = 0;
                
                // Process each match
                foreach (var result in searchResults)
                {
                    // Calculate the position of the match in the file
                    long matchStart = linePositions[result.LineNumber] + result.MatchStart;
                    long matchEnd = matchStart + result.MatchLength;
                    
                    // Copy content between the last match and this one
                    CopyContent(sourceAccessor, destFile, lastEnd, matchStart - lastEnd);
                    
                    // Write the replacement text
                    byte[] replaceBytes = encoding.GetBytes(replaceText);
                    destFile.Write(replaceBytes, 0, replaceBytes.Length);
                    
                    // Update the last end position
                    lastEnd = matchEnd;
                }
                
                // Copy the remaining content
                CopyContent(sourceAccessor, destFile, lastEnd, fileSize - lastEnd);
            });
            
            // Close the current file
            CloseFile();
            
            // Replace the original file with the modified one
            File.Copy(tempFilePath, filePath, true);
            File.Delete(tempFilePath);
            
            // Reopen the file
            OpenFile(filePath);
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Disposes of resources
    /// </summary>
    public void Dispose()
    {
        CloseFile();
        GC.SuppressFinalize(this);
    }

    #region Private Methods

    /// <summary>
    /// Detects the encoding of the file
    /// </summary>
    private void DetectEncoding()
    {
        if (memoryMappedFile == null)
            return;
        
        try
        {
            // Read the first few bytes to detect BOM
            using var accessor = memoryMappedFile.CreateViewAccessor(0, Math.Min(4, fileSize), MemoryMappedFileAccess.Read);
            byte[] buffer = new byte[4];
            accessor.ReadArray(0, buffer, 0, (int)Math.Min(4, fileSize));
            
            // Check for BOM
            if (fileSize >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                encoding = Encoding.UTF8;
            }
            else if (fileSize >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
            }
            else if (fileSize >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            {
                if (fileSize >= 4 && buffer[2] == 0 && buffer[3] == 0)
                    encoding = Encoding.UTF32;
                else
                    encoding = Encoding.Unicode;
            }
            else if (fileSize >= 4 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xFE && buffer[3] == 0xFF)
            {
                encoding = new UTF32Encoding(true, true);
            }
            else
            {
                // No BOM detected, try to detect encoding by analyzing content
                // For simplicity, we'll default to UTF-8
                encoding = Encoding.UTF8;
            }
        }
        catch (Exception)
        {
            // Default to UTF-8 if detection fails
            encoding = Encoding.UTF8;
        }
    }

    /// <summary>
    /// Indexes all line positions in the file
    /// </summary>
    private void IndexLines()
    {
        if (memoryMappedFile == null)
            return;
        
        linePositions.Clear();
        linePositions.Add(0); // First line starts at position 0
        
        using (var accessor = memoryMappedFile.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read))
        {
            byte[] buffer = new byte[BUFFER_SIZE];
            long position = 0;
            
            while (position < fileSize)
            {
                // Read a chunk of the file
                int bytesToRead = (int)Math.Min(BUFFER_SIZE, fileSize - position);
                accessor.ReadArray(position, buffer, 0, bytesToRead);
                
                // Find line breaks in the buffer
                for (int i = 0; i < bytesToRead; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        linePositions.Add(position + i + 1); // Position after the newline
                    }
                }
                
                // Update progress
                position += bytesToRead;
                int progressPercentage = (int)((double)position / fileSize * 100);
                LineIndexingProgress?.Invoke(this, progressPercentage);
            }
        }
        
        totalLines = linePositions.Count;
        LineIndexingComplete?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads content from the file at the specified position and length
    /// </summary>
    /// <param name="position">The position to start reading from</param>
    /// <param name="length">The number of bytes to read</param>
    /// <returns>The text content</returns>
    private string ReadFileContent(long position, long length)
    {
        if (memoryMappedFile == null)
            return string.Empty;
        
        try
        {
            using var accessor = memoryMappedFile.CreateViewAccessor(position, length, MemoryMappedFileAccess.Read);
            byte[] buffer = new byte[length];
            accessor.ReadArray(0, buffer, 0, (int)length);
            
            // Convert to string using the detected encoding
            return encoding.GetString(buffer);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Finds the line index containing the specified position
    /// </summary>
    /// <param name="position">The position in the file</param>
    /// <returns>The line index, or -1 if not found</returns>
    private int FindLineIndex(long position)
    {
        // Binary search to find the line containing the position
        int low = 0;
        int high = linePositions.Count - 1;
        
        while (low <= high)
        {
            int mid = (low + high) / 2;
            
            if (position < linePositions[mid])
            {
                high = mid - 1;
            }
            else if (mid + 1 < linePositions.Count && position >= linePositions[mid + 1])
            {
                low = mid + 1;
            }
            else
            {
                return mid;
            }
        }
        
        return -1;
    }

    /// <summary>
    /// Copies content from the source accessor to the destination file
    /// </summary>
    /// <param name="source">The source accessor</param>
    /// <param name="destination">The destination file</param>
    /// <param name="sourceOffset">The offset in the source</param>
    /// <param name="length">The length to copy</param>
    private void CopyContent(MemoryMappedViewAccessor source, FileStream destination, long sourceOffset, long length)
    {
        byte[] buffer = new byte[BUFFER_SIZE];
        
        long remaining = length;
        while (remaining > 0)
        {
            int bytesToCopy = (int)Math.Min(BUFFER_SIZE, remaining);
            source.ReadArray(sourceOffset, buffer, 0, bytesToCopy);
            destination.Write(buffer, 0, bytesToCopy);
            
            sourceOffset += bytesToCopy;
            remaining -= bytesToCopy;
        }
    }

    #endregion
}

/// <summary>
/// Represents a search result in a large file
/// </summary>
public class SearchResult
{
    /// <summary>
    /// The line number where the match was found (0-based)
    /// </summary>
    public int LineNumber { get; set; }
    
    /// <summary>
    /// The content of the line containing the match
    /// </summary>
    public string LineContent { get; set; } = string.Empty;
    
    /// <summary>
    /// The start position of the match within the line
    /// </summary>
    public int MatchStart { get; set; }
    
    /// <summary>
    /// The length of the match
    /// </summary>
    public int MatchLength { get; set; }
    
    /// <summary>
    /// The line number of the first context line
    /// </summary>
    public int ContextStartLine { get; set; }
    
    /// <summary>
    /// The line number of the last context line
    /// </summary>
    public int ContextEndLine { get; set; }
    
    /// <summary>
    /// The context lines surrounding the match
    /// </summary>
    public List<string> ContextLines { get; set; } = new();
}
