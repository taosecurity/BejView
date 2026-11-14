using BejView.Core.Documents;
using BejView.Core.Highlighting;
using BejView.Core.Search;

namespace BejView.Core.Settings;

/// <summary>Everything BejView remembers between runs. Stored as JSON (see <see cref="SettingsStore"/>).</summary>
public sealed class AppSettings
{
    public const int CurrentSchema = 2;
    public const int MaxRecentLimit = 30;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public bool DarkMode { get; set; }

    /// <summary>A font list: the first one installed is used. Monospaced fonts keep columns lined up.</summary>
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace";

    /// <summary>Points, 6 to 48.</summary>
    public double FontSize { get; set; } = 10;

    public bool WordWrap { get; set; }

    /// <summary>Search results kept and listed (the rest are only counted), 100 to 1,000,000.</summary>
    public int MaxHits { get; set; } = SearchEngine.DefaultMaxHits;

    /// <summary>Longest part of one line shown, in bytes (4 KB to 16 MB). Longer lines show their start and a "continues" mark.</summary>
    public int MaxLineBytes { get; set; } = TextDocument.DefaultMaxLineBytes;

    public bool CaseSensitive { get; set; }
    public bool UseRegex { get; set; }
    public bool WholeWord { get; set; }

    /// <summary>Start follow (tail) mode for every file opened.</summary>
    public bool FollowByDefault { get; set; }

    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>Colour lines with <see cref="HighlightRules"/>.</summary>
    public bool HighlightsEnabled { get; set; } = true;

    /// <summary>Line colouring rules; the first that matches a line wins. Starts as the Papyrus-log preset.</summary>
    public List<HighlightRule> HighlightRules { get; set; } = HighlightPresets.PapyrusRules();

    /// <summary>Main window size in device-independent pixels (0 = default), and whether it was maximised.</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Most recently opened first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>How many recent files to keep (1.0 kept 5), 0 to 30.</summary>
    public int MaxRecent { get; set; } = 10;

    /// <summary>Puts a file at the top of the recent list.</summary>
    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
    }

    /// <summary>
    /// Puts out-of-range values back in range and returns a description of each change (for the log).
    /// Settings files are hand-editable, so this runs after every load.
    /// </summary>
    public List<string> Normalize()
    {
        var fixes = new List<string>();
        if (double.IsNaN(FontSize) || FontSize is < 6 or > 48) { fixes.Add($"FontSize {FontSize} is outside 6-48; using 10."); FontSize = 10; }
        if (MaxHits is < 100 or > 1_000_000) { fixes.Add($"MaxHits {MaxHits} is outside 100-1,000,000; using {SearchEngine.DefaultMaxHits}."); MaxHits = SearchEngine.DefaultMaxHits; }
        if (MaxLineBytes is < 4096 or > 16 << 20) { fixes.Add($"MaxLineBytes {MaxLineBytes} is outside 4096-16777216; using {TextDocument.DefaultMaxLineBytes}."); MaxLineBytes = TextDocument.DefaultMaxLineBytes; }
        if (MaxRecent is < 0 or > MaxRecentLimit) { fixes.Add($"MaxRecent {MaxRecent} is outside 0-{MaxRecentLimit}; using 10."); MaxRecent = 10; }
        if (string.IsNullOrWhiteSpace(FontFamily)) { fixes.Add("FontFamily is empty; using the default list."); FontFamily = new AppSettings().FontFamily; }
        if (double.IsNaN(WindowWidth) || WindowWidth is < 0 or > 20000) { fixes.Add($"WindowWidth {WindowWidth} is out of range; using the default."); WindowWidth = 0; }
        if (double.IsNaN(WindowHeight) || WindowHeight is < 0 or > 20000) { fixes.Add($"WindowHeight {WindowHeight} is out of range; using the default."); WindowHeight = 0; }
        HighlightRules ??= [];
        HighlightRules.RemoveAll(r => r == null);
        RecentFiles ??= [];
        RecentFiles = RecentFiles.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxRecent).ToList();
        SchemaVersion = CurrentSchema;
        return fixes;
    }
}
