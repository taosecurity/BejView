using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace BejView;

/// <summary>
/// A custom control for displaying large text files with virtualized rendering.
/// Only renders the visible portion of the text, making it efficient for very large files.
/// </summary>
public class VirtualizedTextDisplay : Control
{
    // Constants
    private const int DEFAULT_LINE_HEIGHT = 20;
    private const int DEFAULT_CHAR_WIDTH = 8;
    private const int SCROLL_AMOUNT = 3;
    private const int LINE_NUMBER_PADDING = 5; // Padding between line numbers and text
    private const int MIN_LINE_NUMBER_WIDTH = 40; // Minimum width of line number column

    // Text properties
    private Dictionary<int, string> lineCache = new();
    private int totalLines = 0;
    private int firstVisibleLine = 0;
    private int visibleLineCount = 0;
    private int maxLineWidth = 0;
    private int charWidth = DEFAULT_CHAR_WIDTH;
    private int lineHeight = DEFAULT_LINE_HEIGHT;
    private int horizontalScrollPosition = 0;
    private int maxCachedLines = 1000; // Maximum number of lines to keep in cache
    private int lineNumberWidth = MIN_LINE_NUMBER_WIDTH; // Width of line number column
    
    // Line provider delegate
    public delegate string LineProviderDelegate(int lineIndex);
    private LineProviderDelegate? lineProvider;

    // Selection properties
    private int selectionStart = -1;
    private int selectionEnd = -1;
    private int selectionStartLine = -1;
    private int selectionEndLine = -1;
    private bool isSelecting = false;
    private Point selectionStartPoint;

    // Scrollbars
    private VScrollBar vScrollBar;
    private HScrollBar hScrollBar;

    // Theme properties
    private Color selectionColor = Color.FromArgb(173, 214, 255);
    private Color selectionBackColor = Color.Orange; // Default for light mode
    private Color darkModeSelectionBackColor = Color.Blue;
    private bool isDarkMode = false;

    // Events
    public event EventHandler<ScrollEventArgs>? Scrolled;
    public event EventHandler<int>? LineSelected;

    /// <summary>
    /// Sets the dark mode state
    /// </summary>
    [Browsable(true)]
    [Category("Appearance")]
    [Description("Determines whether the control uses dark mode colors")]
    [DefaultValue(false)]
    public bool IsDarkMode
    {
        get => isDarkMode;
        set
        {
            isDarkMode = value;
            Invalidate(); // Redraw with new colors
        }
    }
    
    public VirtualizedTextDisplay()
    {
        // Set up the control
        SetStyle(ControlStyles.OptimizedDoubleBuffer | 
                 ControlStyles.AllPaintingInWmPaint | 
                 ControlStyles.UserPaint | 
                 ControlStyles.ResizeRedraw, true);
        
        // Make the control focusable
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        
        // Set up control properties
        BackColor = Color.White;
        ForeColor = Color.Black;
        
        // Create scrollbars but don't set properties yet
        vScrollBar = new VScrollBar();
        hScrollBar = new HScrollBar();
        
        // Add scrollbars to the control
        Controls.Add(vScrollBar);
        Controls.Add(hScrollBar);
        
        // Now configure scrollbars
        vScrollBar.Dock = DockStyle.Right;
        vScrollBar.SmallChange = 1;
        vScrollBar.LargeChange = 10;
        vScrollBar.Maximum = 0;
        
        hScrollBar.Dock = DockStyle.Bottom;
        hScrollBar.SmallChange = 5;
        hScrollBar.LargeChange = 20;
        hScrollBar.Maximum = 0;
        
        // Set up event handlers
        vScrollBar.ValueChanged += VScrollBar_ValueChanged;
        hScrollBar.ValueChanged += HScrollBar_ValueChanged;
        MouseWheel += VirtualizedTextDisplay_MouseWheel;
        MouseDown += VirtualizedTextDisplay_MouseDown;
        MouseMove += VirtualizedTextDisplay_MouseMove;
        MouseUp += VirtualizedTextDisplay_MouseUp;
        KeyDown += VirtualizedTextDisplay_KeyDown;
        
        // Set up font - do this last to avoid issues with OnFontChanged
        Font = new Font("Consolas", 10F, FontStyle.Regular);
    }

    #region Public Properties and Methods

    /// <summary>
    /// Sets the total number of lines and a line provider function
    /// </summary>
    public void SetLineProvider(int totalLineCount, LineProviderDelegate provider)
    {
        lineProvider = provider;
        totalLines = totalLineCount;
        lineCache.Clear();
        
        // Reset scroll position
        firstVisibleLine = 0;
        horizontalScrollPosition = 0;
        
        // Update scrollbars
        UpdateScrollBars();
        
        // Invalidate to trigger repaint
        Invalidate();
    }
    
    /// <summary>
    /// Gets a line from the cache or from the line provider
    /// </summary>
    private string GetLine(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= totalLines)
            return string.Empty;
            
        // Check if the line is in the cache
        if (lineCache.TryGetValue(lineIndex, out string? cachedLine))
            return cachedLine;
            
        // Get the line from the provider
        if (lineProvider != null)
        {
            string line = lineProvider(lineIndex);
            
            // Add to cache
            lineCache[lineIndex] = line;
            
            // Trim cache if it gets too large
            if (lineCache.Count > maxCachedLines)
            {
                // Remove lines that are far from the current view
                var keysToRemove = lineCache.Keys
                    .Where(k => Math.Abs(k - firstVisibleLine) > maxCachedLines / 2)
                    .Take(lineCache.Count - maxCachedLines / 2)
                    .ToList();
                    
                foreach (var key in keysToRemove)
                    lineCache.Remove(key);
            }
            
            return line;
        }
        
        return string.Empty;
    }

    /// <summary>
    /// Gets the number of visible lines that can fit in the control
    /// </summary>
    public int GetVisibleLineCount()
    {
        return visibleLineCount;
    }

    /// <summary>
    /// Gets the first visible line index
    /// </summary>
    public int GetFirstVisibleLine()
    {
        return firstVisibleLine;
    }

    /// <summary>
    /// Scrolls to the specified line
    /// </summary>
    public void ScrollToLine(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= totalLines)
            return;
        
        // Set the scroll position
        firstVisibleLine = lineIndex;
        
        // Ensure we don't scroll past the end
        int maxFirstLine = Math.Max(0, totalLines - visibleLineCount);
        if (firstVisibleLine > maxFirstLine)
            firstVisibleLine = maxFirstLine;
        
        // Update scrollbar
        vScrollBar.Value = firstVisibleLine;
        
        // Invalidate to trigger repaint
        Invalidate();
        
        // Raise scrolled event
        Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.ThumbPosition, firstVisibleLine));
    }

    /// <summary>
    /// Selects the specified line and scrolls to make it visible
    /// </summary>
    public void SelectLine(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= totalLines)
            return;
        
        // Ensure the line is visible
        if (lineIndex < firstVisibleLine || lineIndex >= firstVisibleLine + visibleLineCount)
        {
            ScrollToLine(lineIndex);
        }
        
        // Set selection to the entire line
        selectionStartLine = lineIndex;
        selectionEndLine = lineIndex;
        selectionStart = 0;
        selectionEnd = GetLine(lineIndex).Length;
        
        // Invalidate to trigger repaint
        Invalidate();
        
        // Raise line selected event
        LineSelected?.Invoke(this, lineIndex);
    }

    /// <summary>
    /// Selects text at the specified position with the given length
    /// </summary>
    public void SelectText(int lineIndex, int position, int length)
    {
        if (lineIndex < 0 || lineIndex >= totalLines)
            return;
        
        // Ensure the line is visible
        if (lineIndex < firstVisibleLine || lineIndex >= firstVisibleLine + visibleLineCount)
        {
            ScrollToLine(lineIndex);
        }
        
        // Set selection
        selectionStartLine = lineIndex;
        selectionEndLine = lineIndex;
        selectionStart = position;
        selectionEnd = position + length;
        
        // Ensure selection is within bounds
        if (selectionStart < 0)
            selectionStart = 0;
        
        string line = GetLine(lineIndex);
        if (selectionEnd > line.Length)
            selectionEnd = line.Length;
        
        // Ensure horizontal scroll position shows the selection
        EnsureSelectionVisible();
        
        // Invalidate to trigger repaint
        Invalidate();
    }
    
    /// <summary>
    /// Ensures the current selection is visible by adjusting the horizontal scroll position
    /// </summary>
    private void EnsureSelectionVisible()
    {
        if (selectionStartLine < 0 || selectionEndLine < 0)
            return;
            
        // Get the line with the selection
        string line = GetLine(selectionStartLine);
        if (string.IsNullOrEmpty(line))
            return;
            
        // Calculate the pixel position of the selection
        int selectionStartX = selectionStart * charWidth;
        int selectionEndX = selectionEnd * charWidth;
        
        // Calculate the visible area
        int visibleWidth = ClientSize.Width - vScrollBar.Width;
        
        // If selection start is before the visible area, scroll left
        if (selectionStartX < horizontalScrollPosition)
        {
            horizontalScrollPosition = Math.Max(0, selectionStartX - charWidth * 5);
            hScrollBar.Value = horizontalScrollPosition;
        }
        // If selection end is after the visible area, scroll right
        else if (selectionEndX > horizontalScrollPosition + visibleWidth)
        {
            horizontalScrollPosition = Math.Max(0, selectionEndX - visibleWidth + charWidth * 5);
            if (horizontalScrollPosition > hScrollBar.Maximum - hScrollBar.LargeChange + 1)
                horizontalScrollPosition = hScrollBar.Maximum - hScrollBar.LargeChange + 1;
            hScrollBar.Value = horizontalScrollPosition;
        }
    }

    /// <summary>
    /// Clears the current selection
    /// </summary>
    public void ClearSelection()
    {
        selectionStart = -1;
        selectionEnd = -1;
        selectionStartLine = -1;
        selectionEndLine = -1;
        Invalidate();
    }

    #endregion

    #region Event Handlers

    private void VScrollBar_ValueChanged(object? sender, EventArgs e)
    {
        firstVisibleLine = vScrollBar.Value;
        Invalidate();
        Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.ThumbPosition, firstVisibleLine));
    }

    private void HScrollBar_ValueChanged(object? sender, EventArgs e)
    {
        horizontalScrollPosition = hScrollBar.Value;
        Invalidate();
    }

    private void VirtualizedTextDisplay_MouseWheel(object? sender, MouseEventArgs e)
    {
        // Calculate the number of lines to scroll
        int linesToScroll = e.Delta > 0 ? -SCROLL_AMOUNT : SCROLL_AMOUNT;
        
        // Update the first visible line
        firstVisibleLine += linesToScroll;
        
        // Ensure we don't scroll past the beginning or end
        if (firstVisibleLine < 0)
            firstVisibleLine = 0;
        
        int maxFirstLine = Math.Max(0, totalLines - visibleLineCount);
        if (firstVisibleLine > maxFirstLine)
            firstVisibleLine = maxFirstLine;
        
        // Update scrollbar
        vScrollBar.Value = firstVisibleLine;
        
        // Invalidate to trigger repaint
        Invalidate();
        
        // Raise scrolled event
        Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.ThumbPosition, firstVisibleLine));
    }

    private void VirtualizedTextDisplay_MouseDown(object? sender, MouseEventArgs e)
    {
        // Set focus to the control
        Focus();
        
        if (e.Button == MouseButtons.Left)
        {
            // Start selection
            isSelecting = true;
            selectionStartPoint = e.Location;
            
            // Calculate the line and character position
            int line = firstVisibleLine + e.Y / lineHeight;
            int charPos = (e.X + horizontalScrollPosition) / charWidth;
            
            // Ensure we don't go out of bounds
            if (line >= totalLines)
                line = totalLines - 1;
            
            if (line < 0)
                line = 0;
            
            if (charPos < 0)
                charPos = 0;
            
            if (line < totalLines)
            {
                string lineText = GetLine(line);
                if (charPos > lineText.Length)
                    charPos = lineText.Length;
            }
            
            // Set selection start
            selectionStartLine = line;
            selectionStart = charPos;
            
            // Set selection end to the same position initially
            selectionEndLine = line;
            selectionEnd = charPos;
            
            // Invalidate to trigger repaint
            Invalidate();
        }
    }

    private void VirtualizedTextDisplay_MouseMove(object? sender, MouseEventArgs e)
    {
        if (isSelecting)
        {
            // Calculate the line and character position
            int line = firstVisibleLine + e.Y / lineHeight;
            int charPos = (e.X + horizontalScrollPosition) / charWidth;
            
            // Ensure we don't go out of bounds
            if (line >= totalLines)
                line = totalLines - 1;
            
            if (line < 0)
                line = 0;
            
            if (charPos < 0)
                charPos = 0;
            
            if (line < totalLines)
            {
                string lineText = GetLine(line);
                if (charPos > lineText.Length)
                    charPos = lineText.Length;
            }
            
            // Update selection end
            selectionEndLine = line;
            selectionEnd = charPos;
            
            // Auto-scroll if needed
            if (e.Y < 0)
            {
                // Scroll up
                if (firstVisibleLine > 0)
                {
                    firstVisibleLine--;
                    vScrollBar.Value = firstVisibleLine;
                }
            }
            else if (e.Y > Height)
            {
                // Scroll down
                int maxFirstLine = Math.Max(0, totalLines - visibleLineCount);
                if (firstVisibleLine < maxFirstLine)
                {
                    firstVisibleLine++;
                    vScrollBar.Value = firstVisibleLine;
                }
            }
            
            // Invalidate to trigger repaint
            Invalidate();
        }
    }

    private void VirtualizedTextDisplay_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // End selection
            isSelecting = false;
        }
    }

    private void VirtualizedTextDisplay_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up:
                // Scroll up one line
                if (firstVisibleLine > 0)
                {
                    firstVisibleLine--;
                    vScrollBar.Value = firstVisibleLine;
                    Invalidate();
                    Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.SmallDecrement, firstVisibleLine));
                }
                break;
            
            case Keys.Down:
                // Scroll down one line
                int maxFirstLine = Math.Max(0, totalLines - visibleLineCount);
                if (firstVisibleLine < maxFirstLine)
                {
                    firstVisibleLine++;
                    vScrollBar.Value = firstVisibleLine;
                    Invalidate();
                    Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.SmallIncrement, firstVisibleLine));
                }
                break;
            
            case Keys.PageUp:
                // Scroll up one page
                firstVisibleLine -= visibleLineCount;
                if (firstVisibleLine < 0)
                    firstVisibleLine = 0;
                vScrollBar.Value = firstVisibleLine;
                Invalidate();
                Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.LargeDecrement, firstVisibleLine));
                break;
            
            case Keys.PageDown:
                // Scroll down one page
                firstVisibleLine += visibleLineCount;
                maxFirstLine = Math.Max(0, totalLines - visibleLineCount);
                if (firstVisibleLine > maxFirstLine)
                    firstVisibleLine = maxFirstLine;
                vScrollBar.Value = firstVisibleLine;
                Invalidate();
                Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.LargeIncrement, firstVisibleLine));
                break;
            
            case Keys.Home:
                if (e.Control)
                {
                    // Scroll to the beginning of the file
                    firstVisibleLine = 0;
                    vScrollBar.Value = firstVisibleLine;
                    Invalidate();
                    Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.First, firstVisibleLine));
                }
                else
                {
                    // Scroll to the beginning of the line
                    horizontalScrollPosition = 0;
                    hScrollBar.Value = horizontalScrollPosition;
                    Invalidate();
                }
                break;
            
            case Keys.End:
                if (e.Control)
                {
                    // Scroll to the end of the file
                    maxFirstLine = Math.Max(0, totalLines - visibleLineCount);
                    firstVisibleLine = maxFirstLine;
                    vScrollBar.Value = firstVisibleLine;
                    Invalidate();
                    Scrolled?.Invoke(this, new ScrollEventArgs(ScrollEventType.Last, firstVisibleLine));
                }
                else
                {
                    // Scroll to the end of the line
                    horizontalScrollPosition = Math.Max(0, maxLineWidth - ClientSize.Width + vScrollBar.Width);
                    hScrollBar.Value = horizontalScrollPosition;
                    Invalidate();
                }
                break;
            
            case Keys.Left:
                // Scroll left
                if (horizontalScrollPosition > 0)
                {
                    horizontalScrollPosition -= charWidth * 5;
                    if (horizontalScrollPosition < 0)
                        horizontalScrollPosition = 0;
                    hScrollBar.Value = horizontalScrollPosition;
                    Invalidate();
                }
                break;
            
            case Keys.Right:
                // Scroll right
                int maxHScroll = Math.Max(0, maxLineWidth - ClientSize.Width + vScrollBar.Width);
                if (horizontalScrollPosition < maxHScroll)
                {
                    horizontalScrollPosition += charWidth * 5;
                    if (horizontalScrollPosition > maxHScroll)
                        horizontalScrollPosition = maxHScroll;
                    hScrollBar.Value = horizontalScrollPosition;
                    Invalidate();
                }
                break;
        }
    }

    #endregion

    #region Painting and Layout

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        
        // Set up graphics
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        
        // Calculate visible area
        int clientWidth = ClientSize.Width - vScrollBar.Width;
        int clientHeight = ClientSize.Height - hScrollBar.Height;
        
        // Calculate the number of visible lines
        visibleLineCount = clientHeight / lineHeight;
        
        // Calculate line number width based on total lines
        UpdateLineNumberWidth();
        
        // Draw background
        e.Graphics.FillRectangle(new SolidBrush(BackColor), 0, 0, clientWidth, clientHeight);
        
        // Draw line number background
        Color lineNumberBackColor = isDarkMode ? 
            Color.FromArgb(45, 45, 45) : // Darker background for dark mode
            Color.FromArgb(240, 240, 240); // Lighter background for light mode
        e.Graphics.FillRectangle(new SolidBrush(lineNumberBackColor), 0, 0, lineNumberWidth, clientHeight);
        
        // Draw separator line
        Color separatorColor = isDarkMode ? 
            Color.FromArgb(60, 60, 60) : // Darker line for dark mode
            Color.FromArgb(200, 200, 200); // Lighter line for light mode
        e.Graphics.DrawLine(new Pen(separatorColor), lineNumberWidth, 0, lineNumberWidth, clientHeight);
        
        // Draw visible lines
        int lastVisibleLine = Math.Min(firstVisibleLine + visibleLineCount, totalLines);
        
        for (int i = firstVisibleLine; i < lastVisibleLine; i++)
        {
            int y = (i - firstVisibleLine) * lineHeight;
            
            // Draw line background (text area only)
            e.Graphics.FillRectangle(new SolidBrush(BackColor), lineNumberWidth + 1, y, clientWidth - lineNumberWidth - 1, lineHeight);
            
            // Draw line number
            Color lineNumberColor = isDarkMode ? 
                Color.FromArgb(150, 150, 150) : // Dimmer text for dark mode
                Color.FromArgb(100, 100, 100); // Dimmer text for light mode
            e.Graphics.DrawString((i + 1).ToString(), Font, new SolidBrush(lineNumberColor), 
                lineNumberWidth - 10 - e.Graphics.MeasureString((i + 1).ToString(), Font).Width, y);
            
            // Draw selection if this line is selected
            if (i >= selectionStartLine && i <= selectionEndLine)
            {
                DrawSelection(e.Graphics, i, y, clientWidth);
            }
            
            // Draw line text
            string line = GetLine(i);
            if (!string.IsNullOrEmpty(line))
            {
                // Calculate the visible portion of the line
                int startChar = horizontalScrollPosition / charWidth;
                int visibleChars = (clientWidth - lineNumberWidth - 1) / charWidth + 1;
                
                if (startChar < line.Length)
                {
                    string visibleText = line;
                    if (startChar > 0 || startChar + visibleChars < line.Length)
                    {
                        int length = Math.Min(visibleChars, line.Length - startChar);
                        visibleText = line.Substring(startChar, length);
                    }
                    
                    e.Graphics.DrawString(visibleText, Font, new SolidBrush(ForeColor), 
                        lineNumberWidth + LINE_NUMBER_PADDING - horizontalScrollPosition % charWidth, y);
                }
            }
        }
    }
    
    /// <summary>
    /// Updates the width of the line number column based on the total number of lines
    /// </summary>
    private void UpdateLineNumberWidth()
    {
        // Calculate the width needed to display the highest line number
        int digits = totalLines > 0 ? (int)Math.Log10(totalLines) + 1 : 1;
        int calculatedWidth = (digits * charWidth) + (2 * LINE_NUMBER_PADDING);
        
        // Use the larger of the calculated width or the minimum width
        lineNumberWidth = Math.Max(calculatedWidth, MIN_LINE_NUMBER_WIDTH);
    }

    private void DrawSelection(Graphics g, int lineIndex, int y, int clientWidth)
    {
        // Get the line text
        string line = GetLine(lineIndex);
        
        // Draw the current selection if this line is selected
        if (selectionStartLine != -1 && selectionEndLine != -1 && 
            lineIndex >= selectionStartLine && lineIndex <= selectionEndLine)
        {
            // Determine if this is a multi-line selection
            bool isMultiLineSelection = selectionStartLine != selectionEndLine;
            
            // Calculate selection bounds
            int selStart = 0;
            int selEnd = line.Length;
            
            if (lineIndex == selectionStartLine)
            {
                selStart = selectionStart;
                if (!isMultiLineSelection)
                    selEnd = selectionEnd;
            }
            else if (lineIndex == selectionEndLine)
            {
                selEnd = selectionEnd;
            }
            
            // Convert character positions to pixel positions
            int startX = lineNumberWidth + LINE_NUMBER_PADDING + (selStart * charWidth) - horizontalScrollPosition;
            int endX = lineNumberWidth + LINE_NUMBER_PADDING + (selEnd * charWidth) - horizontalScrollPosition;
            
            // Ensure the selection is visible
            if (endX >= lineNumberWidth && startX <= clientWidth)
            {
                // Clip to visible area
                startX = Math.Max(lineNumberWidth + 1, startX);
                endX = Math.Min(clientWidth, endX);
                
                // Use the appropriate selection color based on dark mode
                Color highlightColor = isDarkMode ? darkModeSelectionBackColor : selectionBackColor;
                
                // Draw selection rectangle
                using (SolidBrush brush = new SolidBrush(highlightColor))
                {
                    g.FillRectangle(brush, startX, y, endX - startX, lineHeight);
                }
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        
        // Update scrollbars
        UpdateScrollBars();
        
        // Invalidate to trigger repaint
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        
        // Recalculate character dimensions
        using (Graphics g = CreateGraphics())
        {
            SizeF charSize = g.MeasureString("X", Font);
            charWidth = (int)Math.Ceiling(charSize.Width);
            lineHeight = (int)Math.Ceiling(charSize.Height);
        }
        
        // Recalculate max line width
        CalculateMaxLineWidth();
        
        // Update scrollbars
        UpdateScrollBars();
        
        // Invalidate to trigger repaint
        Invalidate();
    }

    #endregion

    #region Helper Methods

    private void CalculateMaxLineWidth()
    {
        maxLineWidth = 0;
        
        // If there are no lines, set a default width
        if (totalLines == 0)
        {
            maxLineWidth = 1000; // Default width of 1000 pixels
            return;
        }
        
        // Sample a subset of lines to estimate max width
        int sampleSize = Math.Min(1000, totalLines);
        int step = Math.Max(1, totalLines / sampleSize);
        
        for (int i = 0; i < totalLines; i += step)
        {
            string line = GetLine(i);
            int lineWidth = line.Length * charWidth;
            if (lineWidth > maxLineWidth)
                maxLineWidth = lineWidth;
        }
        
        // Add some extra space to account for lines we didn't sample
        maxLineWidth = Math.Max(1000, (int)(maxLineWidth * 1.2));
    }

    private void UpdateScrollBars()
    {
        // Check if the control is fully initialized
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            // Set default values
            visibleLineCount = 10;
            vScrollBar.Maximum = Math.Max(0, totalLines - 1);
            vScrollBar.LargeChange = 10;
            hScrollBar.Maximum = Math.Max(0, maxLineWidth - 1);
            hScrollBar.LargeChange = 20;
            return;
        }
        
        // Update vertical scrollbar
        visibleLineCount = Math.Max(1, (ClientSize.Height - hScrollBar.Height) / lineHeight);
        vScrollBar.Maximum = Math.Max(0, totalLines - 1);
        vScrollBar.LargeChange = Math.Max(1, visibleLineCount);
        
        // Update horizontal scrollbar
        int visibleWidth = Math.Max(1, ClientSize.Width - vScrollBar.Width);
        hScrollBar.Maximum = Math.Max(0, maxLineWidth - 1);
        hScrollBar.LargeChange = Math.Max(1, visibleWidth);
    }

    #endregion
}
