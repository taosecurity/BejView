using Avalonia.Media;
using Avalonia.Media.Immutable;
using BejView.Core.Highlighting;

namespace BejView.App.Views;

/// <summary>The text view's colours for the light or the dark theme.</summary>
internal sealed class Palette
{
    public required IBrush Background { get; init; }
    public required IBrush Text { get; init; }
    public required IBrush GutterBackground { get; init; }
    public required IBrush GutterText { get; init; }
    public required IBrush CurrentLine { get; init; }
    public required IBrush Selection { get; init; }
    public required IBrush Match { get; init; }
    public required IBrush CurrentMatch { get; init; }
    public required IBrush Marker { get; init; }
    public required IBrush Bookmark { get; init; }
    public required IBrush Caret { get; init; }
    public required IReadOnlyDictionary<HighlightColor, IBrush> Highlights { get; init; }

    private static ImmutableSolidColorBrush B(uint argb) => new(Color.FromUInt32(argb));

    public static Palette Light { get; } = new()
    {
        Background = B(0xFFFFFFFF),
        Text = B(0xFF1F2328),
        GutterBackground = B(0xFFF3F4F6),
        GutterText = B(0xFF8C959F),
        CurrentLine = B(0xFFF0F4FA),
        Selection = B(0xFFB6D4FB),
        Match = B(0xFFFFE066),
        CurrentMatch = B(0xFFFF9F1C),
        Marker = B(0xFF8250DF),
        Bookmark = B(0xFF0969DA),
        Caret = B(0xFF1F2328),
        Highlights = new Dictionary<HighlightColor, IBrush>
        {
            [HighlightColor.Red] = B(0xFFCF222E),
            [HighlightColor.Amber] = B(0xFF9A6700),
            [HighlightColor.Green] = B(0xFF1A7F37),
            [HighlightColor.Blue] = B(0xFF0969DA),
            [HighlightColor.Purple] = B(0xFF8250DF),
            [HighlightColor.Gray] = B(0xFF8C959F),
        },
    };

    public static Palette Dark { get; } = new()
    {
        Background = B(0xFF1E1F22),
        Text = B(0xFFD4D4D4),
        GutterBackground = B(0xFF26282C),
        GutterText = B(0xFF6E7681),
        CurrentLine = B(0xFF2A2D32),
        Selection = B(0xFF264F78),
        Match = B(0xFF6B5A12),
        CurrentMatch = B(0xFFB8641A),
        Marker = B(0xFFB392F0),
        Bookmark = B(0xFF58A6FF),
        Caret = B(0xFFE6E6E6),
        Highlights = new Dictionary<HighlightColor, IBrush>
        {
            [HighlightColor.Red] = B(0xFFFF7B72),
            [HighlightColor.Amber] = B(0xFFE3B341),
            [HighlightColor.Green] = B(0xFF7EE787),
            [HighlightColor.Blue] = B(0xFF79C0FF),
            [HighlightColor.Purple] = B(0xFFD2A8FF),
            [HighlightColor.Gray] = B(0xFF8B949E),
        },
    };
}
