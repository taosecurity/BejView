using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BejView.Core.Highlighting;
using BejView.Core.Navigation;

namespace BejView.App;

/// <summary>Small dialogs built in code: a message, a yes/no question, "Go to", and the highlight-rule editor.</summary>
internal static class Dialogs
{
    private static Window Shell(string title, Control content, double width = 460)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(18), Child = content },
        };
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static async Task ShowMessage(Window owner, string title, string text, bool monospace = false)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (monospace) body.FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
        var panel = new StackPanel();
        panel.Children.Add(body);
        panel.Children.Add(Buttons(ok));
        var w = Shell(title, panel, monospace ? 620 : 460);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string text, string yes, string no = "Cancel")
    {
        var y = new Button { Content = yes, IsDefault = true, MinWidth = 80 };
        var n = new Button { Content = no, IsCancel = true, MinWidth = 80 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Buttons(y, n));
        var w = Shell(title, panel);
        bool result = false;
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Asks where to go. Returns null on Cancel.</summary>
    public static async Task<GoToTarget?> AskGoTo(Window owner, string hint)
    {
        var box = new TextBox { PlaceholderText = "12345, 50% or @1048576" };
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
        var go = new Button { Content = "Go", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = GoToTarget.Help, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(Buttons(go, cancel));
        var w = Shell("Go to", panel);
        GoToTarget? result = null;
        go.Click += (_, _) =>
        {
            if (GoToTarget.TryParse(box.Text, out var t, out string message)) { result = t; w.Close(); }
            else { error.Text = message; error.IsVisible = true; }
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => box.Focus();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Edits a copy of the highlight rules. Returns the new list, or null on Cancel.</summary>
    public static async Task<List<HighlightRule>?> EditRules(Window owner, IReadOnlyList<HighlightRule> current)
    {
        var rules = current.Select(r => r.Clone()).ToList();
        var rows = new StackPanel { Spacing = 4 };
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        string[] colours = Enum.GetNames<HighlightColor>();

        void Rebuild()
        {
            rows.Children.Clear();
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,70,60,36") };
            AddCell(head, new TextBlock { Text = "Lines containing", FontWeight = FontWeight.SemiBold }, 0);
            AddCell(head, new TextBlock { Text = "Colour", FontWeight = FontWeight.SemiBold }, 1);
            AddCell(head, new TextBlock { Text = "Regex", FontWeight = FontWeight.SemiBold }, 2);
            AddCell(head, new TextBlock { Text = "Case", FontWeight = FontWeight.SemiBold }, 3);
            rows.Children.Add(head);
            foreach (var r in rules)
            {
                var rule = r;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110,70,60,36") };
                var text = new TextBox { Text = rule.Pattern, Margin = new Thickness(0, 0, 6, 0) };
                text.TextChanged += (_, _) => rule.Pattern = text.Text ?? "";
                var colour = new ComboBox { ItemsSource = colours, SelectedItem = colours.FirstOrDefault(c => c.Equals(rule.Color, StringComparison.OrdinalIgnoreCase)) ?? colours[0], HorizontalAlignment = HorizontalAlignment.Stretch };
                colour.SelectionChanged += (_, _) => rule.Color = colour.SelectedItem as string ?? "Red";
                var regex = new CheckBox { IsChecked = rule.IsRegex, HorizontalAlignment = HorizontalAlignment.Center };
                regex.IsCheckedChanged += (_, _) => rule.IsRegex = regex.IsChecked == true;
                var cs = new CheckBox { IsChecked = rule.CaseSensitive, HorizontalAlignment = HorizontalAlignment.Center };
                cs.IsCheckedChanged += (_, _) => rule.CaseSensitive = cs.IsChecked == true;
                var remove = new Button { Content = "✕", Padding = new Thickness(6, 2) };
                ToolTip.SetTip(remove, "Remove this rule");
                remove.Click += (_, _) => { rules.Remove(rule); Rebuild(); };
                AddCell(g, text, 0);
                AddCell(g, colour, 1);
                AddCell(g, regex, 2);
                AddCell(g, cs, 3);
                AddCell(g, remove, 4);
                rows.Children.Add(g);
            }
        }

        static void AddCell(Grid g, Control c, int col)
        {
            Grid.SetColumn(c, col);
            c.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(c);
        }

        Rebuild();
        var add = new Button { Content = "Add a rule" };
        add.Click += (_, _) => { rules.Add(new HighlightRule("", HighlightColor.Red)); Rebuild(); };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Lines that contain the text are drawn in the colour. The first rule that matches a line wins. " +
                   "Tick Regex for a .NET regular expression, Case to match upper and lower case exactly.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 380 });
        panel.Children.Add(add);
        panel.Children.Add(error);
        panel.Children.Add(Buttons(ok, cancel));
        var w = Shell("Highlight rules", panel, 640);
        List<HighlightRule>? result = null;
        ok.Click += (_, _) =>
        {
            rules.RemoveAll(r => string.IsNullOrEmpty(r.Pattern));
            Highlighter.Compile(rules, out var problems);
            if (problems.Count > 0) { error.Text = string.Join(Environment.NewLine, problems); error.IsVisible = true; return; }
            result = rules;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    public const string KeysText = """
        Open a file          Ctrl+O     (or drag files onto the window)
        Close the tab        Ctrl+W     Next / previous tab: Ctrl+Tab / Ctrl+Shift+Tab
        Reload               F5         (looks for new lines once)
        Follow (tail)        Ctrl+L     scroll up to pause, Ctrl+E to resume

        Find                 Ctrl+F     then Enter; Shift+Enter goes back
        Next / previous      Ctrl+N / Ctrl+P   (also F3 / Shift+F3)
        Matching lines       Ctrl+Shift+M      (filter pane)
        Other lines          Ctrl+Shift+O      (filter pane, like grep -v)
        Cancel               Esc

        Go to line, %, byte  Ctrl+G
        Beginning / end      Ctrl+B / Ctrl+E   (also Ctrl+Home / Ctrl+End)
        Bookmark             Ctrl+F2    next F2, previous Shift+F2

        Copy                 Ctrl+C     (no selection: the current line)
        Copy with numbers    Ctrl+Shift+C
        Select all           Ctrl+A
        Save selection       Ctrl+S     (writes a new file; never the one shown)

        Dark mode            Ctrl+D
        Text size            Ctrl++ / Ctrl+- / Ctrl+0   (or Ctrl+mouse wheel)
        Word wrap            Alt+Z
        """;
}
