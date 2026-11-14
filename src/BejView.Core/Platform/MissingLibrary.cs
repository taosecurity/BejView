using System.Text.RegularExpressions;

namespace BejView.Core.Platform;

/// <summary>
/// Linux: turns "Unable to load shared library … libfontconfig.so.1: cannot open shared object file" into a message
/// that names the missing system library and the package to install. Found in the beta 8 test (2026-11-13): a
/// minimal WSL Debian had no libfontconfig1, which the bundled SkiaSharp needs, and BejView stopped with a stack trace.
/// </summary>
public static partial class MissingLibrary
{
    /// <summary>The package that holds each library: Debian/Ubuntu, Fedora, Arch (and CachyOS).</summary>
    private static readonly Dictionary<string, (string Debian, string Fedora, string Arch)> Packages = new(StringComparer.Ordinal)
    {
        ["libfontconfig.so.1"] = ("libfontconfig1", "fontconfig", "fontconfig"),
        ["libfreetype.so.6"] = ("libfreetype6", "freetype", "freetype2"),
        ["libX11.so.6"] = ("libx11-6", "libX11", "libx11"),
        ["libICE.so.6"] = ("libice6", "libICE", "libice"),
        ["libSM.so.6"] = ("libsm6", "libSM", "libsm"),
        ["libXrandr.so.2"] = ("libxrandr2", "libXrandr", "libxrandr"),
        ["libXi.so.6"] = ("libxi6", "libXi", "libxi"),
        ["libXcursor.so.1"] = ("libxcursor1", "libXcursor", "libxcursor"),
        ["libXext.so.6"] = ("libxext6", "libXext", "libxext"),
        ["libxkbcommon.so.0"] = ("libxkbcommon0", "libxkbcommon", "libxkbcommon"),
        ["libwayland-client.so.0"] = ("libwayland-client0", "libwayland-client", "wayland"),
        ["libwayland-cursor.so.0"] = ("libwayland-cursor0", "libwayland-cursor", "wayland"),
        ["libwayland-egl.so.1"] = ("libwayland-egl1", "libwayland-egl", "wayland"),
        ["libEGL.so.1"] = ("libegl1", "libglvnd-egl", "libglvnd"),
    };

    /// <summary>BejView's own bundled libraries: their "not found" lines are just the places .NET tried.</summary>
    private static readonly string[] Bundled = ["libSkiaSharp", "libHarfBuzzSharp", "liblibSkiaSharp", "liblibHarfBuzzSharp"];

    // The loader's usual complaints: not there, or there but unusable (empty, damaged, or built for another CPU).
    [GeneratedRegex(@"(lib[A-Za-z0-9_+\-.]*?\.so(?:\.\d+)*): (?:cannot open shared object file|file too short|invalid ELF header|wrong ELF class)")]
    private static partial Regex CannotOpen();

    /// <summary>The missing system libraries named anywhere in the exception chain, in order, without repeats.</summary>
    public static IReadOnlyList<string> Find(Exception ex)
    {
        var found = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            foreach (Match m in CannotOpen().Matches(e.Message))
            {
                string lib = m.Groups[1].Value;
                if (Bundled.Any(b => lib.StartsWith(b, StringComparison.Ordinal))) continue;
                if (!found.Contains(lib)) found.Add(lib);
            }
        }
        return found;
    }

    /// <summary>A message naming the libraries and the install commands, or null when no missing library is named.</summary>
    public static string? Explain(Exception ex)
    {
        var libs = Find(ex);
        if (libs.Count == 0) return null;
        var known = libs.Where(Packages.ContainsKey).Select(l => Packages[l]).ToList();
        string what = libs.Count == 1
            ? $"the system library {libs[0]} is missing (or can't be loaded)"
            : $"these system libraries are missing (or can't be loaded): {string.Join(", ", libs)}";
        string fix = known.Count == 0
            ? $"Install the package that provides {string.Join(", ", libs)} with your package manager."
            : $"Install it with: sudo apt install {string.Join(' ', known.Select(k => k.Debian).Distinct())} (Debian, Ubuntu, WSL); " +
              $"sudo dnf install {string.Join(' ', known.Select(k => k.Fedora).Distinct())} (Fedora); " +
              $"sudo pacman -S {string.Join(' ', known.Select(k => k.Arch).Distinct())} (Arch, CachyOS).";
        return $"BejView can't open its window: {what}. {fix} A desktop install has it already; a minimal system such as a new WSL distro may not.";
    }
}
