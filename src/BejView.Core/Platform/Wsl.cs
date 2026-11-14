namespace BejView.Core.Platform;

/// <summary>
/// WSL detection and path translation. Under WSL the Linux build opens /mnt files in the Windows exe
/// (bridge mode), so paths must cross between /mnt/c/... and C:\....
/// At runtime the app prefers the real <c>wslpath</c> tool; these functions are the fallback and the tested rules.
/// </summary>
public static class Wsl
{
    public const string InteropFile = "/proc/sys/fs/binfmt_misc/WSLInterop";

    /// <summary>True when running under WSL 1 or 2: WSL sets WSL_DISTRO_NAME / WSL_INTEROP, and registers the interop handler.</summary>
    public static bool IsWsl(Func<string, string?> env, Func<string, bool> fileExists) =>
        !string.IsNullOrEmpty(env("WSL_DISTRO_NAME")) ||
        !string.IsNullOrEmpty(env("WSL_INTEROP")) ||
        fileExists(InteropFile) ||
        fileExists("/proc/sys/fs/binfmt_misc/WSLInterop-late");

    public static bool IsWslNow() =>
        OperatingSystem.IsLinux() && IsWsl(Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>True for a path on a Windows drive mount: &lt;root&gt;c or &lt;root&gt;c/... (one drive letter).</summary>
    public static bool IsOnDriveMount(string linuxPath, string mountRoot = "/mnt/")
    {
        if (!mountRoot.EndsWith('/')) mountRoot += "/";
        if (!linuxPath.StartsWith(mountRoot, StringComparison.Ordinal)) return false;
        string rest = linuxPath[mountRoot.Length..];
        return rest.Length >= 1 && char.IsAsciiLetter(rest[0]) && (rest.Length == 1 || rest[1] == '/');
    }

    /// <summary>
    /// The drive mount root from /etc/wsl.conf ([automount] root = /x/), or /mnt/ when the file or the key is missing.
    /// </summary>
    public static string MountRootFromConf(string? wslConf)
    {
        const string standard = "/mnt/";
        if (string.IsNullOrEmpty(wslConf)) return standard;
        string section = "";
        foreach (string raw in wslConf.Split('\n'))
        {
            string line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim().ToLowerInvariant(); continue; }
            int eq = line.IndexOf('=');
            if (section != "automount" || eq < 0) continue;
            if (!line[..eq].Trim().Equals("root", StringComparison.OrdinalIgnoreCase)) continue;
            string v = line[(eq + 1)..].Trim().Trim('"');
            if (!v.StartsWith('/')) return standard;
            return v.EndsWith('/') ? v : v + "/";
        }
        return standard;
    }

    /// <summary>
    /// True when WSL can start Windows programs: the WSLInterop binfmt handler exists and its first line is "enabled".
    /// <paramref name="readFirstLine"/> returns null for a missing file.
    /// </summary>
    public static bool InteropEnabled(Func<string, string?> readFirstLine)
    {
        foreach (string f in new[] { InteropFile, "/proc/sys/fs/binfmt_misc/WSLInterop-late" })
        {
            string? first = readFirstLine(f);
            if (first != null) return first.Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Linux path to Windows path: /mnt/c/Users/x becomes C:\Users\x; anything outside the drive mounts becomes
    /// \\wsl.localhost\&lt;distro&gt;\path. <paramref name="mountRoot"/> is /mnt/ unless /etc/wsl.conf changes it.
    /// </summary>
    public static string ToWindowsPath(string linuxPath, string distro, string mountRoot = "/mnt/")
    {
        if (string.IsNullOrEmpty(linuxPath)) throw new ArgumentException("The path is empty.", nameof(linuxPath));
        if (!linuxPath.StartsWith('/'))
            throw new ArgumentException($"'{linuxPath}' is not an absolute Linux path (it must start with /).", nameof(linuxPath));
        if (!mountRoot.EndsWith('/')) mountRoot += "/";

        if (linuxPath.StartsWith(mountRoot, StringComparison.Ordinal))
        {
            string rest = linuxPath[mountRoot.Length..];
            int slash = rest.IndexOf('/');
            string drive = slash < 0 ? rest : rest[..slash];
            if (drive.Length == 1 && char.IsAsciiLetter(drive[0]))
            {
                string tail = slash < 0 ? "" : rest[(slash + 1)..].TrimEnd('/');
                return char.ToUpperInvariant(drive[0]) + ":\\" + tail.Replace('/', '\\');
            }
        }

        if (string.IsNullOrWhiteSpace(distro))
            throw new ArgumentException("The WSL distro name is needed for paths outside /mnt (set WSL_DISTRO_NAME).", nameof(distro));
        string inner = linuxPath.TrimEnd('/');
        return @"\\wsl.localhost\" + distro + inner.Replace('/', '\\');
    }

    /// <summary>Windows path to Linux path: C:\Users\x becomes /mnt/c/Users/x; \\wsl.localhost\distro\p and \\wsl$\distro\p become /p.</summary>
    public static string ToLinuxPath(string windowsPath, string mountRoot = "/mnt/")
    {
        if (string.IsNullOrEmpty(windowsPath)) throw new ArgumentException("The path is empty.", nameof(windowsPath));
        if (!mountRoot.EndsWith('/')) mountRoot += "/";
        string p = windowsPath.Replace('/', '\\');

        foreach (string prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = p[prefix.Length..];
                int slash = rest.IndexOf('\\');
                return slash < 0 ? "/" : "/" + rest[(slash + 1)..].TrimEnd('\\').Replace('\\', '/');
            }
        }

        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            string tail = p.Length > 2 ? p[2..].Trim('\\') : "";
            string drive = mountRoot + char.ToLowerInvariant(p[0]);
            return tail.Length == 0 ? drive : drive + "/" + tail.Replace('\\', '/');
        }

        throw new ArgumentException($"'{windowsPath}' is not an absolute Windows path (expected C:\\... or \\\\wsl.localhost\\...).", nameof(windowsPath));
    }
}
