namespace BejView;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        menuStrip = new MenuStrip();
        fileToolStripMenuItem = new ToolStripMenuItem();
        openToolStripMenuItem = new ToolStripMenuItem();
        recentFilesToolStripMenuItem = new ToolStripMenuItem();
        toolStripSeparator1 = new ToolStripSeparator();
        exitToolStripMenuItem = new ToolStripMenuItem();
        editToolStripMenuItem = new ToolStripMenuItem();
        findToolStripMenuItem = new ToolStripMenuItem();
        viewToolStripMenuItem = new ToolStripMenuItem();
        darkModeToolStripMenuItem = new ToolStripMenuItem();
        increaseFontSizeToolStripMenuItem = new ToolStripMenuItem();
        decreaseFontSizeToolStripMenuItem = new ToolStripMenuItem();
        helpToolStripMenuItem = new ToolStripMenuItem();
        shortcutsToolStripMenuItem = new ToolStripMenuItem();
        aboutToolStripMenuItem = new ToolStripMenuItem();
        statusStrip = new StatusStrip();
        statusLabel = new ToolStripStatusLabel();
        lineCountLabel = new ToolStripStatusLabel();
        fileSizeLabel = new ToolStripStatusLabel();
        textDisplayPanel = new Panel();
        textDisplay = new RichTextBox();
        searchPanel = new Panel();
        searchContextLabel = new Label();
        searchResultsListBox = new ListBox();
        searchButton = new Button();
        searchTextBox = new TextBox();
        searchLabel = new Label();
        openFileDialog = new OpenFileDialog();
        menuStrip.SuspendLayout();
        statusStrip.SuspendLayout();
        textDisplayPanel.SuspendLayout();
        searchPanel.SuspendLayout();
        SuspendLayout();
        
        // menuStrip
        menuStrip.ImageScalingSize = new Size(20, 20);
        menuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, editToolStripMenuItem, viewToolStripMenuItem, helpToolStripMenuItem });
        menuStrip.Location = new Point(0, 0);
        menuStrip.Name = "menuStrip";
        menuStrip.Size = new Size(1000, 28);
        menuStrip.TabIndex = 0;
        menuStrip.Text = "menuStrip1";
        
        // fileToolStripMenuItem
        fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { openToolStripMenuItem, recentFilesToolStripMenuItem, toolStripSeparator1, exitToolStripMenuItem });
        fileToolStripMenuItem.Name = "fileToolStripMenuItem";
        fileToolStripMenuItem.Size = new Size(46, 24);
        fileToolStripMenuItem.Text = "&File";
        
        // openToolStripMenuItem
        openToolStripMenuItem.Name = "openToolStripMenuItem";
        openToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.O;
        openToolStripMenuItem.Size = new Size(224, 26);
        openToolStripMenuItem.Text = "&Open";
        openToolStripMenuItem.Click += OpenToolStripMenuItem_Click;
        
        // recentFilesToolStripMenuItem
        recentFilesToolStripMenuItem.Name = "recentFilesToolStripMenuItem";
        recentFilesToolStripMenuItem.Size = new Size(224, 26);
        recentFilesToolStripMenuItem.Text = "&Recent Files";
        
        // toolStripSeparator1
        toolStripSeparator1.Name = "toolStripSeparator1";
        toolStripSeparator1.Size = new Size(221, 6);
        
        // exitToolStripMenuItem
        exitToolStripMenuItem.Name = "exitToolStripMenuItem";
        exitToolStripMenuItem.ShortcutKeys = Keys.Alt | Keys.F4;
        exitToolStripMenuItem.Size = new Size(224, 26);
        exitToolStripMenuItem.Text = "E&xit";
        exitToolStripMenuItem.Click += ExitToolStripMenuItem_Click;
        
        // editToolStripMenuItem
        editToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { findToolStripMenuItem });
        editToolStripMenuItem.Name = "editToolStripMenuItem";
        editToolStripMenuItem.Size = new Size(49, 24);
        editToolStripMenuItem.Text = "&Edit";
        
        // findToolStripMenuItem
        findToolStripMenuItem.Name = "findToolStripMenuItem";
        findToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.F;
        findToolStripMenuItem.Size = new Size(224, 26);
        findToolStripMenuItem.Text = "&Find";
        findToolStripMenuItem.Click += FindToolStripMenuItem_Click;
        
        // viewToolStripMenuItem
        viewToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { darkModeToolStripMenuItem, new ToolStripSeparator(), increaseFontSizeToolStripMenuItem, decreaseFontSizeToolStripMenuItem });
        viewToolStripMenuItem.Name = "viewToolStripMenuItem";
        viewToolStripMenuItem.Size = new Size(55, 24);
        viewToolStripMenuItem.Text = "&View";
        
        // darkModeToolStripMenuItem
        darkModeToolStripMenuItem.CheckOnClick = true;
        darkModeToolStripMenuItem.Name = "darkModeToolStripMenuItem";
        darkModeToolStripMenuItem.Size = new Size(224, 26);
        darkModeToolStripMenuItem.Text = "&Dark Mode";
        darkModeToolStripMenuItem.Click += DarkModeToolStripMenuItem_Click;
        
        // increaseFontSizeToolStripMenuItem
        increaseFontSizeToolStripMenuItem.Name = "increaseFontSizeToolStripMenuItem";
        increaseFontSizeToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.Oemplus;
        increaseFontSizeToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl++";
        increaseFontSizeToolStripMenuItem.Size = new Size(224, 26);
        increaseFontSizeToolStripMenuItem.Text = "&Increase Font Size";
        increaseFontSizeToolStripMenuItem.Click += IncreaseFontSizeToolStripMenuItem_Click;
        
        // decreaseFontSizeToolStripMenuItem
        decreaseFontSizeToolStripMenuItem.Name = "decreaseFontSizeToolStripMenuItem";
        decreaseFontSizeToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.OemMinus;
        decreaseFontSizeToolStripMenuItem.ShortcutKeyDisplayString = "Ctrl+-";
        decreaseFontSizeToolStripMenuItem.Size = new Size(224, 26);
        decreaseFontSizeToolStripMenuItem.Text = "&Decrease Font Size";
        decreaseFontSizeToolStripMenuItem.Click += DecreaseFontSizeToolStripMenuItem_Click;
        
        // helpToolStripMenuItem
        helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { shortcutsToolStripMenuItem, new ToolStripSeparator(), aboutToolStripMenuItem });
        helpToolStripMenuItem.Name = "helpToolStripMenuItem";
        helpToolStripMenuItem.Size = new Size(55, 24);
        helpToolStripMenuItem.Text = "&Help";
        
        // shortcutsToolStripMenuItem
        shortcutsToolStripMenuItem.Name = "shortcutsToolStripMenuItem";
        shortcutsToolStripMenuItem.Size = new Size(224, 26);
        shortcutsToolStripMenuItem.Text = "&Keyboard Shortcuts";
        shortcutsToolStripMenuItem.Click += ShortcutsToolStripMenuItem_Click;
        
        // aboutToolStripMenuItem
        aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
        aboutToolStripMenuItem.Size = new Size(224, 26);
        aboutToolStripMenuItem.Text = "&About";
        aboutToolStripMenuItem.Click += AboutToolStripMenuItem_Click;
        
        // statusStrip
        statusStrip.ImageScalingSize = new Size(20, 20);
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel, lineCountLabel, fileSizeLabel });
        statusStrip.Location = new Point(0, 578);
        statusStrip.Name = "statusStrip";
        statusStrip.Size = new Size(1000, 26);
        statusStrip.TabIndex = 1;
        statusStrip.Text = "statusStrip1";
        
        // statusLabel
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(50, 20);
        statusLabel.Text = "Ready";
        
        // lineCountLabel
        lineCountLabel.Name = "lineCountLabel";
        lineCountLabel.Size = new Size(62, 20);
        lineCountLabel.Text = "Lines: 0";
        
        // fileSizeLabel
        fileSizeLabel.Name = "fileSizeLabel";
        fileSizeLabel.Size = new Size(56, 20);
        fileSizeLabel.Text = "Size: 0";
        
        // textDisplayPanel
        textDisplayPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        textDisplayPanel.Controls.Add(textDisplay);
        textDisplayPanel.Location = new Point(0, 31);
        textDisplayPanel.Name = "textDisplayPanel";
        textDisplayPanel.Size = new Size(1000, 547);
        textDisplayPanel.TabIndex = 2;
        
        // textDisplay
        textDisplay.Dock = DockStyle.Fill;
        textDisplay.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        textDisplay.Location = new Point(0, 0);
        textDisplay.Name = "textDisplay";
        textDisplay.ReadOnly = true;
        textDisplay.Size = new Size(1000, 547);
        textDisplay.TabIndex = 0;
        textDisplay.Text = "";
        textDisplay.WordWrap = false;
        
        // searchPanel
        searchPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
        searchPanel.BorderStyle = BorderStyle.FixedSingle;
        searchPanel.Controls.Add(searchContextLabel);
        searchPanel.Controls.Add(searchResultsListBox);
        searchPanel.Controls.Add(searchButton);
        searchPanel.Controls.Add(searchTextBox);
        searchPanel.Controls.Add(searchLabel);
        searchPanel.Location = new Point(700, 31);
        searchPanel.Name = "searchPanel";
        searchPanel.Size = new Size(300, 547);
        searchPanel.TabIndex = 3;
        searchPanel.Visible = false;
        
        // searchContextLabel
        searchContextLabel.AutoSize = true;
        searchContextLabel.Location = new Point(3, 80);
        searchContextLabel.Name = "searchContextLabel";
        searchContextLabel.Size = new Size(133, 20);
        searchContextLabel.TabIndex = 4;
        searchContextLabel.Text = "Search Results (0):";
        
        // searchResultsListBox
        searchResultsListBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        searchResultsListBox.FormattingEnabled = true;
        searchResultsListBox.ItemHeight = 20;
        searchResultsListBox.Location = new Point(3, 103);
        searchResultsListBox.Name = "searchResultsListBox";
        searchResultsListBox.Size = new Size(292, 424);
        searchResultsListBox.TabIndex = 3;
        searchResultsListBox.SelectedIndexChanged += SearchResultsListBox_SelectedIndexChanged;
        
        // searchButton
        searchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        searchButton.Location = new Point(220, 40);
        searchButton.Name = "searchButton";
        searchButton.Size = new Size(75, 30);
        searchButton.TabIndex = 2;
        searchButton.Text = "Search";
        searchButton.UseVisualStyleBackColor = true;
        searchButton.Click += SearchButton_Click;
        
        // searchTextBox
        searchTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        searchTextBox.Location = new Point(3, 40);
        searchTextBox.Name = "searchTextBox";
        searchTextBox.Size = new Size(211, 27);
        searchTextBox.TabIndex = 1;
        searchTextBox.KeyDown += SearchTextBox_KeyDown;
        
        // searchLabel
        searchLabel.AutoSize = true;
        searchLabel.Location = new Point(3, 17);
        searchLabel.Name = "searchLabel";
        searchLabel.Size = new Size(56, 20);
        searchLabel.TabIndex = 0;
        searchLabel.Text = "Search:";
        
        // openFileDialog
        openFileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
        openFileDialog.Title = "Open Text File";
        
        // MainForm
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1000, 600);
        Controls.Add(searchPanel);
        Controls.Add(textDisplayPanel);
        Controls.Add(statusStrip);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;
        Name = "MainForm";
        Text = "BejView";
        Load += MainForm_Load;
        menuStrip.ResumeLayout(false);
        menuStrip.PerformLayout();
        statusStrip.ResumeLayout(false);
        statusStrip.PerformLayout();
        textDisplayPanel.ResumeLayout(false);
        searchPanel.ResumeLayout(false);
        searchPanel.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private MenuStrip menuStrip;
    private ToolStripMenuItem fileToolStripMenuItem;
    private ToolStripMenuItem openToolStripMenuItem;
    private ToolStripMenuItem recentFilesToolStripMenuItem;
    private ToolStripSeparator toolStripSeparator1;
    private ToolStripMenuItem exitToolStripMenuItem;
    private ToolStripMenuItem editToolStripMenuItem;
    private ToolStripMenuItem findToolStripMenuItem;
    private ToolStripMenuItem viewToolStripMenuItem;
    private ToolStripMenuItem darkModeToolStripMenuItem;
    private ToolStripMenuItem increaseFontSizeToolStripMenuItem;
    private ToolStripMenuItem decreaseFontSizeToolStripMenuItem;
    private ToolStripMenuItem helpToolStripMenuItem;
    private ToolStripMenuItem shortcutsToolStripMenuItem;
    private ToolStripMenuItem aboutToolStripMenuItem;
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel;
    private ToolStripStatusLabel lineCountLabel;
    private ToolStripStatusLabel fileSizeLabel;
    private Panel textDisplayPanel;
    private RichTextBox textDisplay;
    private Panel searchPanel;
    private Label searchContextLabel;
    private ListBox searchResultsListBox;
    private Button searchButton;
    private TextBox searchTextBox;
    private Label searchLabel;
    private OpenFileDialog openFileDialog;
}
