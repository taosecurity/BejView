using BejView.Core.Cli;
using BejView.Core.Logging;
using BejView.Core.Settings;

namespace BejView.App;

/// <summary>Everything one run of BejView shares: the command line, the log and the settings.</summary>
internal sealed class AppSession(CliOptions options, Logger log, SettingsStore store, AppSettings settings)
{
    public static AppSession Current { get; set; } = null!;

    public CliOptions Options { get; } = options;
    public Logger Log { get; } = log;
    public SettingsStore Store { get; } = store;
    public AppSettings Settings { get; } = settings;

    public void SaveSettings() => Store.Save(Settings);
}
