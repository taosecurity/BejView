using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace BejView.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var session = AppSession.Current;
        RequestedThemeVariant = session.Settings.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(session);
            // Which windowing backend won (Wayland, X11 or Win32), so a Linux log shows whether the fallback was used.
            session.Log.Info($"Display: the window uses {window.PlatformImpl?.GetType().FullName ?? "an unknown backend"}.");
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            window.OpenFromCommandLine();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
