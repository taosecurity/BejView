using System.Text.Json;
using System.Text.Json.Serialization;
using BejView.Core.Logging;
using BejView.Core.Platform;

namespace BejView.Core.Settings;

/// <summary>Source-generated JSON (works with trimming and single-file builds).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Never throws on a bad file: it logs what was wrong, keeps a copy of the
/// bad file next to it, and carries on with defaults.
/// <para>First run imports BejView 1.0's recent-files list (recent_files.txt in the same folder) once. 1.0 saved
/// nothing else.</para>
/// </summary>
public sealed class SettingsStore(AppPaths paths, Logger log)
{
    public AppPaths Paths { get; } = paths;

    public AppSettings Load()
    {
        string file = Paths.SettingsFile;
        AppSettings settings;
        if (File.Exists(file))
        {
            settings = ReadFile(file) ?? new AppSettings();
        }
        else
        {
            log.Info($"No settings file at {file}; starting with defaults.");
            settings = new AppSettings();
            if (ImportV1RecentFiles(settings)) Save(settings);
        }

        foreach (string fix in settings.Normalize())
            log.Warning($"Settings ({file}): {fix}");
        return settings;
    }

    /// <summary>Writes the settings atomically (temporary file, then rename), so a crash can't leave half a file.</summary>
    public bool Save(AppSettings settings)
    {
        string file = Paths.SettingsFile;
        try
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, file, overwrite: true);
            log.Debug($"Settings saved to {file}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Can't save settings to {file}. Check that the folder exists and isn't read-only", ex);
            return false;
        }
    }

    private AppSettings? ReadFile(string file)
    {
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"Can't read settings file {file}; using defaults this time", ex);
            return null;
        }

        try
        {
            var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                    ?? throw new JsonException("The file holds 'null'.");
            log.Debug($"Settings read from {file}.");
            return s;
        }
        catch (JsonException ex)
        {
            string bad = file + ".bad";
            try { File.Copy(file, bad, overwrite: true); } catch (IOException) { /* keep going with defaults */ }
            log.Warning($"Settings file {file} is not valid JSON ({ex.Message}). Using defaults; the old file was copied to {bad}. " +
                        "Fix or delete it to stop this warning.");
            return null;
        }
    }

    /// <summary>1.0 kept up to 5 recent files, one path per line, in %APPDATA%\BejView\recent_files.txt.</summary>
    private bool ImportV1RecentFiles(AppSettings s)
    {
        string old = Paths.LegacyRecentFile;
        if (!File.Exists(old)) return false;
        try
        {
            var paths = File.ReadAllLines(old).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            foreach (string p in Enumerable.Reverse(paths)) s.AddRecent(p);
            log.Info($"Imported {paths.Count} recent file(s) from BejView 1.0 ({old}). The 1.0 file is left as it was.");
            return paths.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"Found BejView 1.0's recent files at {old} but couldn't read them ({ex.Message}); skipping the import.");
            return false;
        }
    }
}
