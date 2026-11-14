namespace BejView.Core.Platform;

/// <summary>
/// Where BejView keeps its files. Every path can be worked out for either OS from an environment lookup,
/// so tests check the Linux rules on Windows and the other way round.
/// </summary>
public sealed class AppPaths
{
    public const string SettingsFileName = "settings.json";
    public const string LogFileName = "bejview.log";

    /// <summary>1.0's recent-files list, in the same Windows folder. Imported once on first run.</summary>
    public const string LegacyRecentFileName = "recent_files.txt";

    /// <summary>%APPDATA%\BejView on Windows (the folder 1.0 used), $XDG_CONFIG_HOME/BejView or ~/.config/BejView on Linux.</summary>
    public string ConfigDir { get; }

    /// <summary>Directory separator for the OS these paths belong to (not necessarily the OS running the code).</summary>
    public char Separator { get; }

    public string SettingsFile => ConfigDir + Separator + SettingsFileName;
    public string DefaultLogFile => ConfigDir + Separator + LogFileName;
    public string LegacyRecentFile => ConfigDir + Separator + LegacyRecentFileName;

    public AppPaths(string configDir, char separator)
    {
        ConfigDir = configDir;
        Separator = separator;
    }

    public static AppPaths ForCurrentOs() => OperatingSystem.IsWindows()
        ? ForWindows(Environment.GetEnvironmentVariable)
        : ForLinux(Environment.GetEnvironmentVariable);

    public static AppPaths ForWindows(Func<string, string?> env)
    {
        // Joined by hand with '\' so the Windows rules give the same answer when tests run on Linux.
        string appData = (NonEmpty(env("APPDATA"))
            ?? (NonEmpty(env("USERPROFILE")) ?? @"C:\Users\Default") + @"\AppData\Roaming").TrimEnd('\\');
        return new AppPaths(appData + @"\BejView", '\\');
    }

    public static AppPaths ForLinux(Func<string, string?> env)
    {
        string home = (NonEmpty(env("HOME")) ?? "/tmp").TrimEnd('/');
        string config = (NonEmpty(env("XDG_CONFIG_HOME")) ?? home + "/.config").TrimEnd('/');
        return new AppPaths(config + "/BejView", '/');
    }

    /// <summary>
    /// Where a file BejView wrote really is, for showing and opening it from outside BejView (Explorer, Notepad).
    /// The Microsoft Store package may redirect writes under %APPDATA% to a private copy,
    /// %LOCALAPPDATA%\Packages\&lt;family name&gt;\LocalCache\Roaming\... (the PLAN.md fallback, used when the Store
    /// doesn't allow turning that off). BejView itself sees the merged view, but other programs only see the private
    /// copy. So when the private copy exists, this returns it; otherwise (zip, WinGet, Linux, or redirection turned
    /// off) the path is returned unchanged.
    /// </summary>
    public static string RealLocation(string path) => RealLocation(path, PackageIdentity.FamilyName,
        Environment.GetEnvironmentVariable, p => File.Exists(p) || Directory.Exists(p));

    public static string RealLocation(string path, string? packageFamilyName, Func<string, string?> env, Func<string, bool> exists)
    {
        if (packageFamilyName == null) return path;
        string? appData = NonEmpty(env("APPDATA"))?.TrimEnd('\\');
        string? localAppData = NonEmpty(env("LOCALAPPDATA"))?.TrimEnd('\\');
        if (appData == null || localAppData == null) return path;
        if (!path.StartsWith(appData + @"\", StringComparison.OrdinalIgnoreCase)) return path;
        string copy = $@"{localAppData}\Packages\{packageFamilyName}\LocalCache\Roaming{path[appData.Length..]}";
        return exists(copy) ? copy : path;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
