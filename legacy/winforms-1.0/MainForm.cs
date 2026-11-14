using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms.VisualStyles;

namespace BejView;

/// <summary>
/// Custom color table for dark mode menu items
/// </summary>
public class DarkModeColorTable : ProfessionalColorTable
{
    public override Color MenuItemSelected => Color.FromArgb(80, 80, 80);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(80, 80, 80);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(80, 80, 80);
    public override Color MenuItemBorder => Color.FromArgb(100, 100, 100);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(100, 100, 100);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(100, 100, 100);
    public override Color MenuBorder => Color.FromArgb(100, 100, 100);
}

public partial class MainForm : Form
{
    // Constants
    private const int CONTEXT_LINES = 50; // Number of lines to show before/after search results
    private const int MAX_DISPLAY_LINES = 5000; // Maximum lines to display at once

    // File handling
    private LargeFileHandler fileHandler = new();
    private VirtualizedTextDisplay? virtualTextDisplay;

    // Theme handling
    private bool isDarkMode = false;
    private Color darkBackColor = Color.FromArgb(30, 30, 30);
    private Color darkForeColor = Color.FromArgb(220, 220, 220);
    private Color lightBackColor = Color.White;
    private Color lightForeColor = Color.Black;

    // Recent files
    private List<string> recentFiles = new(5); // Store up to 5 recent files

    // Search handling
    private List<SearchResult> searchResults = new();
    private int currentSearchResultIndex = -1;
    private bool isSearching = false;

    // Progress tracking
    private ProgressBar progressBar = new();
    
    // RAM usage tracking
    private System.Windows.Forms.Timer ramUsageTimer = new();
    private ToolStripStatusLabel ramUsageLabel = new();

    public MainForm()
    {
        InitializeComponent();
        
        // Initialize file handler
        fileHandler = new LargeFileHandler();
        fileHandler.LineIndexingProgress += FileHandler_LineIndexingProgress;
        fileHandler.LineIndexingComplete += FileHandler_LineIndexingComplete;
        fileHandler.SearchProgress += FileHandler_SearchProgress;
        fileHandler.SearchComplete += FileHandler_SearchComplete;
        
        // Replace the RichTextBox with our custom VirtualizedTextDisplay
        InitializeVirtualTextDisplay();
        
        // Add progress bar to status strip
        progressBar = new ProgressBar
        {
            Visible = false,
            Width = 100
        };
        statusStrip.Items.Add(new ToolStripControlHost(progressBar));
        
        // Add RAM usage label to status strip
        ramUsageLabel = new ToolStripStatusLabel("RAM: 0 MB");
        ramUsageLabel.Alignment = ToolStripItemAlignment.Right;
        statusStrip.Items.Add(ramUsageLabel);
        
        // Set up RAM usage timer
        ramUsageTimer.Interval = 1000; // Update every second
        ramUsageTimer.Tick += RamUsageTimer_Tick;
        ramUsageTimer.Start();
    }

    #region Initialization

    private void InitializeVirtualTextDisplay()
    {
        // Create the virtualized text display
        virtualTextDisplay = new VirtualizedTextDisplay
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ForeColor = Color.Black
        };
        
        // Add event handlers
        virtualTextDisplay.Scrolled += VirtualTextDisplay_Scrolled;
        virtualTextDisplay.LineSelected += VirtualTextDisplay_LineSelected;
        
        // Replace the RichTextBox with our custom control
        textDisplayPanel.Controls.Remove(textDisplay);
        textDisplayPanel.Controls.Add(virtualTextDisplay);
    }

    #endregion

    #region Form Events

    private void MainForm_Load(object sender, EventArgs e)
    {
        // Set up the form
        UpdateStatusBar();
        LoadRecentFiles();
        UpdateRecentFilesMenu();
        ApplyTheme();
        
        // Set up keyboard shortcuts
        SetupKeyboardShortcuts();
        
        // Update RAM usage initially
        UpdateRamUsage();
    }
    
    private void RamUsageTimer_Tick(object? sender, EventArgs e)
    {
        // Update RAM usage
        UpdateRamUsage();
    }
    
    private void UpdateRamUsage()
    {
        // Get current process
        using (Process currentProcess = Process.GetCurrentProcess())
        {
            // Refresh process info
            currentProcess.Refresh();
            
            // Get memory usage in MB
            long memoryUsageMB = currentProcess.WorkingSet64 / (1024 * 1024);
            
            // Update label
            ramUsageLabel.Text = $"RAM: {memoryUsageMB} MB";
        }
    }
    
    private void SetupKeyboardShortcuts()
    {
        // Add keyboard shortcuts
        KeyPreview = true;
        KeyDown += MainForm_KeyDown;
    }
    
    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        // Handle keyboard shortcuts
        if (e.Control)
        {
            switch (e.KeyCode)
            {
                case Keys.N: // Ctrl+N - Next search result
                    if (searchResults.Count > 0)
                    {
                        // Move to the next search result
                        currentSearchResultIndex = (currentSearchResultIndex + 1) % searchResults.Count;
                        searchResultsListBox.SelectedIndex = currentSearchResultIndex;
                        e.Handled = true;
                    }
                    break;
                    
                case Keys.P: // Ctrl+P - Previous search result
                    if (searchResults.Count > 0)
                    {
                        // Move to the previous search result
                        currentSearchResultIndex = (currentSearchResultIndex - 1 + searchResults.Count) % searchResults.Count;
                        searchResultsListBox.SelectedIndex = currentSearchResultIndex;
                        e.Handled = true;
                    }
                    break;
                    
                case Keys.D: // Ctrl+D - Toggle dark mode
                    darkModeToolStripMenuItem.Checked = !darkModeToolStripMenuItem.Checked;
                    isDarkMode = darkModeToolStripMenuItem.Checked;
                    ApplyTheme();
                    e.Handled = true;
                    break;
                    
                case Keys.B: // Ctrl+B - Go to beginning of file
                    if (virtualTextDisplay != null && fileHandler.TotalLines > 0)
                    {
                        // Temporarily disable scrolling events to prevent interference
                        virtualTextDisplay.Scrolled -= VirtualTextDisplay_Scrolled;
                        
                        try
                        {
                            // Scroll to the beginning of the file
                            virtualTextDisplay.ScrollToLine(0);
                            
                            // Force a refresh of the display
                            virtualTextDisplay.Invalidate();
                            Application.DoEvents();
                            
                            // Update status bar
                            statusLabel.Text = "Beginning of file";
                            
                            // Set focus to the text display
                            virtualTextDisplay.Focus();
                        }
                        finally
                        {
                            // Re-enable scrolling events
                            virtualTextDisplay.Scrolled += VirtualTextDisplay_Scrolled;
                        }
                        
                        e.Handled = true;
                    }
                    break;
                    
                case Keys.E: // Ctrl+E - Go to end of file
                    if (virtualTextDisplay != null && fileHandler.TotalLines > 0)
                    {
                        // Temporarily disable scrolling events to prevent interference
                        virtualTextDisplay.Scrolled -= VirtualTextDisplay_Scrolled;
                        
                        try
                        {
                            // Scroll to the end of the file
                            int maxLine = Math.Max(0, fileHandler.TotalLines - virtualTextDisplay.GetVisibleLineCount());
                            virtualTextDisplay.ScrollToLine(maxLine);
                            
                            // Force a refresh of the display
                            virtualTextDisplay.Invalidate();
                            Application.DoEvents();
                            
                            // Update status bar
                            statusLabel.Text = "End of file";
                            
                            // Set focus to the text display
                            virtualTextDisplay.Focus();
                        }
                        finally
                        {
                            // Re-enable scrolling events
                            virtualTextDisplay.Scrolled += VirtualTextDisplay_Scrolled;
                        }
                        
                        e.Handled = true;
                    }
                    break;
            }
        }
    }

    private void VirtualTextDisplay_Scrolled(object? sender, ScrollEventArgs e)
    {
        // This event is triggered when the user scrolls the virtualized text display
        // We can use it to load more content if needed
        if (fileHandler.FilePath == string.Empty || virtualTextDisplay == null)
            return;
        
        int firstVisibleLine = virtualTextDisplay.GetFirstVisibleLine();
        int visibleLineCount = virtualTextDisplay.GetVisibleLineCount();
        
        // If we're near the beginning or end of the loaded content, load more
        if (firstVisibleLine < 100 || firstVisibleLine + visibleLineCount > fileHandler.TotalLines - 100)
        {
            LoadVisibleContent();
        }
    }

    private void VirtualTextDisplay_LineSelected(object? sender, int lineIndex)
    {
        // This event is triggered when a line is selected in the virtualized text display
        // Update the status bar with the current line number
        statusLabel.Text = $"Line: {lineIndex + 1}";
    }

    #endregion

    #region Menu Events

    private void OpenToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            OpenFile(openFileDialog.FileName);
        }
    }

    private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
    {
        Close();
    }

    private void FindToolStripMenuItem_Click(object sender, EventArgs e)
    {
        // Toggle search panel visibility
        searchPanel.Visible = !searchPanel.Visible;
        
        if (searchPanel.Visible)
        {
            // Adjust the text display width
            textDisplayPanel.Width = ClientSize.Width - searchPanel.Width;
            searchTextBox.Focus();
        }
        else
        {
            // Restore the text display width
            textDisplayPanel.Width = ClientSize.Width;
        }
    }

    private void DarkModeToolStripMenuItem_Click(object sender, EventArgs e)
    {
        isDarkMode = darkModeToolStripMenuItem.Checked;
        ApplyTheme();
    }
    
    private void ShortcutsToolStripMenuItem_Click(object sender, EventArgs e)
    {
        // Create a message with all keyboard shortcuts
        string shortcuts = "Keyboard Shortcuts:\n\n" +
            "Ctrl+O: Open file\n" +
            "Ctrl+F: Find text\n" +
            "Ctrl+D: Toggle dark mode\n" +
            "Ctrl+N: Go to next search result\n" +
            "Ctrl+P: Go to previous search result\n" +
            "Ctrl+B: Go to beginning of file\n" +
            "Ctrl+E: Go to end of file\n" +
            "Ctrl++: Increase font size\n" +
            "Ctrl+-: Decrease font size\n" +
            "Alt+F4: Exit application";
        
        // Show the shortcuts in a message box
        MessageBox.Show(shortcuts, "Keyboard Shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    
    private void AboutToolStripMenuItem_Click(object sender, EventArgs e)
    {
        // Create a message with about information
        string aboutText = "Developed by Richard Bejtlich using Visual Studio Code, Cline, OpenRouter.ai, and Claude 3.7.\n\n" +
            "Version 1.0";
        
        // Show the about information in a message box
        MessageBox.Show(aboutText, "About BejView", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    
    private void IncreaseFontSizeToolStripMenuItem_Click(object sender, EventArgs e)
    {
        // Increase font size
        if (virtualTextDisplay != null)
        {
            // Get current font
            Font currentFont = virtualTextDisplay.Font;
            
            // Calculate new size (max 24pt)
            float newSize = Math.Min(currentFont.Size + 2, 24);
            
            // Create new font with increased size
            Font newFont = new Font(currentFont.FontFamily, newSize, currentFont.Style);
            
            // Set the new font
            virtualTextDisplay.Font = newFont;
            
            // Update status
            statusLabel.Text = $"Font size: {newSize}pt";
        }
    }
    
    private void DecreaseFontSizeToolStripMenuItem_Click(object sender, EventArgs e)
    {
        // Decrease font size
        if (virtualTextDisplay != null)
        {
            // Get current font
            Font currentFont = virtualTextDisplay.Font;
            
            // Calculate new size (min 8pt)
            float newSize = Math.Max(currentFont.Size - 2, 8);
            
            // Create new font with decreased size
            Font newFont = new Font(currentFont.FontFamily, newSize, currentFont.Style);
            
            // Set the new font
            virtualTextDisplay.Font = newFont;
            
            // Update status
            statusLabel.Text = $"Font size: {newSize}pt";
        }
    }

    #endregion

    #region Search Events

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            SearchButton_Click(sender, e);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void SearchButton_Click(object sender, EventArgs e)
    {
        if (fileHandler.FilePath == string.Empty || string.IsNullOrWhiteSpace(searchTextBox.Text))
            return;
        
        if (isSearching)
            return;
        
        isSearching = true;
        statusLabel.Text = "Searching...";
        Cursor = Cursors.WaitCursor;
        progressBar.Visible = true;
        progressBar.Value = 0;
        
        // Clear previous results
        searchResults.Clear();
        searchResultsListBox.Items.Clear();
        
        // Perform the search in a background task
        _ = PerformSearchAsync();
    }

    private async Task PerformSearchAsync()
    {
        try
        {
            // Search for the text
            var results = await fileHandler.SearchAsync(
                searchTextBox.Text, 
                false, // Case insensitive by default
                false, // Not regex by default
                CONTEXT_LINES);
            
            // Update the search results
            searchResults = results;
            
            // Update UI on the main thread
            BeginInvoke(() => 
            {
                UpdateSearchResults();
                
                // If we have search results, navigate to the first one
                if (searchResults.Count > 0)
                {
                    currentSearchResultIndex = 0;
                    searchResultsListBox.SelectedIndex = currentSearchResultIndex;
                    NavigateToSearchResult(currentSearchResultIndex);
                }
                
                statusLabel.Text = $"Found {searchResults.Count} matches";
                progressBar.Visible = false;
                Cursor = Cursors.Default;
                isSearching = false;
            });
        }
        catch (Exception ex)
        {
            BeginInvoke(() => 
            {
                MessageBox.Show($"Error searching: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error searching";
                progressBar.Visible = false;
                Cursor = Cursors.Default;
                isSearching = false;
            });
        }
    }

    private void SearchResultsListBox_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (searchResultsListBox.SelectedIndex >= 0 && searchResultsListBox.SelectedIndex < searchResults.Count)
        {
            currentSearchResultIndex = searchResultsListBox.SelectedIndex;
            NavigateToSearchResult(currentSearchResultIndex);
        }
    }

    #endregion

    #region File Handling Methods

    private void OpenFile(string filePath)
    {
        try
        {
            // Update status
            statusLabel.Text = "Opening file...";
            Cursor = Cursors.WaitCursor;
            progressBar.Visible = true;
            progressBar.Value = 0;
            
            // Reset the display
            if (virtualTextDisplay != null)
                virtualTextDisplay.SetLineProvider(0, _ => string.Empty);
            
            // Open the file
            if (fileHandler.OpenFile(filePath))
            {
                // Add to recent files
                AddToRecentFiles(filePath);
                UpdateRecentFilesMenu();
                
                // Update status bar
                UpdateStatusBar();
            }
            else
            {
                MessageBox.Show($"Error opening file: {filePath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error opening file";
                progressBar.Visible = false;
                Cursor = Cursors.Default;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            statusLabel.Text = "Error opening file";
            progressBar.Visible = false;
            Cursor = Cursors.Default;
        }
    }

    private void LoadVisibleContent()
    {
        if (fileHandler.FilePath == string.Empty || virtualTextDisplay == null)
            return;
        
        // Set the line provider function
        virtualTextDisplay.SetLineProvider(fileHandler.TotalLines, GetLineContent);
    }
    
    /// <summary>
    /// Line provider function for the virtualized text display
    /// </summary>
    private string GetLineContent(int lineIndex)
    {
        // Read a single line from the file handler
        return fileHandler.ReadLine(lineIndex);
    }

    private void FileHandler_LineIndexingProgress(object? sender, int progressPercentage)
    {
        BeginInvoke(() => 
        {
            progressBar.Value = progressPercentage;
        });
    }

    private void FileHandler_LineIndexingComplete(object? sender, EventArgs e)
    {
        BeginInvoke(() => 
        {
            // Set up the virtualized text display with the line provider
            LoadVisibleContent();
            
            // Update status
            UpdateStatusBar();
            statusLabel.Text = "Ready";
            progressBar.Visible = false;
            Cursor = Cursors.Default;
        });
    }

    private void FileHandler_SearchProgress(object? sender, int progressPercentage)
    {
        BeginInvoke(() => 
        {
            progressBar.Value = progressPercentage;
        });
    }

    private void FileHandler_SearchComplete(object? sender, EventArgs e)
    {
        // This is handled in the PerformSearchAsync method
    }

    #endregion

    #region Search Methods

    private void UpdateSearchResults()
    {
        searchResultsListBox.Items.Clear();
        
        foreach (var result in searchResults)
        {
            searchResultsListBox.Items.Add($"Line {result.LineNumber + 1}");
        }
        
        searchContextLabel.Text = $"Search Results ({searchResults.Count}):";
    }

    
    private void NavigateToSearchResult(int index)
    {
        if (index < 0 || index >= searchResults.Count || virtualTextDisplay == null)
            return;
        
        var result = searchResults[index];
        
        try
        {
            // Temporarily disable scrolling events to prevent interference
            virtualTextDisplay.Scrolled -= VirtualTextDisplay_Scrolled;
            
            // First, ensure we have the context lines loaded in the cache
            for (int i = result.ContextStartLine; i <= result.ContextEndLine; i++)
            {
                // This will populate the cache with all context lines
                fileHandler.ReadLine(i);
            }
            
            // Scroll to the line containing the match, positioning it in the middle of the view if possible
            int visibleLineCount = virtualTextDisplay.GetVisibleLineCount();
            int scrollToLine = Math.Max(0, result.LineNumber - (visibleLineCount / 3));
            virtualTextDisplay.ScrollToLine(scrollToLine);
            
            // Force a refresh of the display to ensure the line is visible
            Application.DoEvents();
            
            // Get the line text
            string line = fileHandler.ReadLine(result.LineNumber);
            
            // Select the entire line
            virtualTextDisplay.SelectText(result.LineNumber, 0, line.Length);
            
            virtualTextDisplay.Focus();
            
            // Update status bar
            statusLabel.Text = $"Line {result.LineNumber + 1}: {result.LineContent.Trim()}";
        }
        finally
        {
            // Re-enable scrolling events
            virtualTextDisplay.Scrolled += VirtualTextDisplay_Scrolled;
        }
    }

    #endregion

    #region Theme Methods

    private void ApplyTheme()
    {
        if (isDarkMode)
        {
            // Apply dark theme
            BackColor = darkBackColor;
            ForeColor = darkForeColor;
            if (virtualTextDisplay != null)
            {
                virtualTextDisplay.BackColor = darkBackColor;
                virtualTextDisplay.ForeColor = darkForeColor;
                virtualTextDisplay.IsDarkMode = true;
            }
            searchPanel.BackColor = darkBackColor;
            searchPanel.ForeColor = darkForeColor;
            searchTextBox.BackColor = Color.FromArgb(50, 50, 50);
            searchTextBox.ForeColor = darkForeColor;
            searchResultsListBox.BackColor = Color.FromArgb(50, 50, 50);
            searchResultsListBox.ForeColor = darkForeColor;
            
            // Make sure search button is visible in dark mode
            searchButton.BackColor = Color.FromArgb(60, 60, 60);
            searchButton.ForeColor = Color.White;
            searchButton.FlatStyle = FlatStyle.Flat;
            searchButton.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
            
            // Fix menu text color in dark mode
            menuStrip.BackColor = Color.FromArgb(45, 45, 45); // Slightly lighter than the main background
            menuStrip.ForeColor = Color.FromArgb(240, 240, 240); // Bright white for menu text
            
            // Make sure top-level menu items are visible against dark background
            foreach (ToolStripMenuItem item in menuStrip.Items)
            {
                // Set the menu item text color to a bright white for better visibility
                item.ForeColor = Color.FromArgb(240, 240, 240); // Bright white
                
                // Set custom renderer for the menu strip to handle highlighted items
                menuStrip.Renderer = new ToolStripProfessionalRenderer(new DarkModeColorTable());
                
                // Ensure dropdown menu items have black text on light background
                item.DropDownOpening += (s, e) => 
                {
                    if (s is ToolStripMenuItem menuItem)
                    {
                        foreach (ToolStripItem dropDownItem in menuItem.DropDownItems)
                        {
                            dropDownItem.ForeColor = Color.Black;
                        }
                    }
                };
            }
        }
        else
        {
            // Apply light theme
            BackColor = lightBackColor;
            ForeColor = lightForeColor;
            menuStrip.BackColor = SystemColors.Control;
            
            // Reset menu item colors in light mode
            foreach (ToolStripMenuItem item in menuStrip.Items)
            {
                item.ForeColor = SystemColors.ControlText;
            }
            if (virtualTextDisplay != null)
            {
                virtualTextDisplay.BackColor = lightBackColor;
                virtualTextDisplay.ForeColor = lightForeColor;
                virtualTextDisplay.IsDarkMode = false;
            }
            searchPanel.BackColor = lightBackColor;
            searchPanel.ForeColor = lightForeColor;
            searchTextBox.BackColor = SystemColors.Window;
            searchTextBox.ForeColor = lightForeColor;
            searchResultsListBox.BackColor = SystemColors.Window;
            searchResultsListBox.ForeColor = lightForeColor;
            
            // Reset search button appearance in light mode
            searchButton.UseVisualStyleBackColor = true;
            searchButton.FlatStyle = FlatStyle.Standard;
        }
    }

    #endregion

    #region Recent Files Methods

    private void LoadRecentFiles()
    {
        recentFiles.Clear();
        
        try
        {
            // Get the path to the recent files list
            string recentFilesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BejView",
                "recent_files.txt");
            
            // Create directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(recentFilesPath)!);
            
            // Check if the file exists
            if (File.Exists(recentFilesPath))
            {
                // Read all lines from the file
                string[] lines = File.ReadAllLines(recentFilesPath);
                
                // Add each line to the recent files list
                foreach (string line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                    {
                        recentFiles.Add(line);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log the error but don't show a message box
            Console.WriteLine($"Error loading recent files: {ex.Message}");
        }
    }

    private void AddToRecentFiles(string filePath)
    {
        // Remove if already exists
        recentFiles.Remove(filePath);
        
        // Add to the beginning
        recentFiles.Insert(0, filePath);
        
        // Keep only the most recent 5 files
        while (recentFiles.Count > 5)
        {
            recentFiles.RemoveAt(recentFiles.Count - 1);
        }
        
        // Save the recent files list
        SaveRecentFiles();
    }
    
    private void SaveRecentFiles()
    {
        try
        {
            // Get the path to the recent files list
            string recentFilesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BejView",
                "recent_files.txt");
            
            // Create directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(recentFilesPath)!);
            
            // Write all lines to the file
            File.WriteAllLines(recentFilesPath, recentFiles);
        }
        catch (Exception ex)
        {
            // Log the error but don't show a message box
            Console.WriteLine($"Error saving recent files: {ex.Message}");
        }
    }
    
    private void ClearRecentFiles()
    {
        // Clear the recent files list
        recentFiles.Clear();
        
        // Update the menu
        UpdateRecentFilesMenu();
        
        // Save the empty list
        SaveRecentFiles();
    }

    private void UpdateRecentFilesMenu()
    {
        // Clear existing items
        recentFilesToolStripMenuItem.DropDownItems.Clear();
        
        if (recentFiles.Count == 0)
        {
            var noRecentItem = new ToolStripMenuItem("No recent files");
            noRecentItem.Enabled = false;
            recentFilesToolStripMenuItem.DropDownItems.Add(noRecentItem);
        }
        else
        {
            // Add recent files
            foreach (string filePath in recentFiles)
            {
                var item = new ToolStripMenuItem(Path.GetFileName(filePath));
                item.ToolTipText = filePath;
                item.Tag = filePath;
                item.Click += RecentFile_Click;
                recentFilesToolStripMenuItem.DropDownItems.Add(item);
            }
            
            // Add separator
            recentFilesToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            
            // Add clear recent files option
            var clearItem = new ToolStripMenuItem("Clear Recent Files");
            clearItem.Click += (s, e) => ClearRecentFiles();
            recentFilesToolStripMenuItem.DropDownItems.Add(clearItem);
        }
    }

    private void RecentFile_Click(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem item && item.Tag is string filePath)
        {
            if (File.Exists(filePath))
            {
                OpenFile(filePath);
            }
            else
            {
                MessageBox.Show($"File not found: {filePath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                recentFiles.Remove(filePath);
                UpdateRecentFilesMenu();
            }
        }
    }

    #endregion

    #region Helper Methods

    private void UpdateStatusBar()
    {
        if (fileHandler.FilePath == string.Empty)
        {
            Text = "BejView";
            lineCountLabel.Text = "Lines: 0";
            lineCountLabel.Alignment = ToolStripItemAlignment.Right;
            fileSizeLabel.Text = "Size: 0";
            fileSizeLabel.Alignment = ToolStripItemAlignment.Right;
        }
        else
        {
            Text = $"BejView - {Path.GetFileName(fileHandler.FilePath)}";
            lineCountLabel.Text = $"Lines: {fileHandler.TotalLines:N0}";
            lineCountLabel.Alignment = ToolStripItemAlignment.Right;
            fileSizeLabel.Text = $"Size: {FormatFileSize(fileHandler.FileSize)}";
            fileSizeLabel.Alignment = ToolStripItemAlignment.Right;
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int suffixIndex = 0;
        double size = bytes;
        
        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }
        
        return $"{size:N2} {suffixes[suffixIndex]}";
    }

    #endregion

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        
        // Dispose of resources
        fileHandler.Dispose();
    }
}
