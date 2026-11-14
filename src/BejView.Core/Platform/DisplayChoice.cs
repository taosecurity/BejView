namespace BejView.Core.Platform;

/// <summary>
/// Linux only: whether the window tries native Wayland (Avalonia.Wayland, X11 as Avalonia's fallback) or goes
/// straight to X11. The user's desktops are KDE Plasma 6 on Wayland (2026-11-13), so native Wayland is the default.
/// Under WSL it's X11: WSLg's Weston didn't answer a native Wayland start (step 4, 2026-11-13).
/// </summary>
public sealed record DisplayChoice(bool Wayland, string Reason)
{
    /// <summary>The Wayland app_id and the .desktop file's name (bejview.desktop); KWin matches name and icon by it.</summary>
    public const string AppId = "bejview";
    public const string ForceX11Variable = "BEJVIEW_X11";
    public const string ForceWaylandVariable = "BEJVIEW_WAYLAND";

    public static DisplayChoice Decide(Func<string, string?> env, bool isWsl)
    {
        if (IsOn(env(ForceX11Variable)))
            return new(false, $"{ForceX11Variable} is set, so using X11.");
        if (string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
            return new(false, "No Wayland compositor named (WAYLAND_DISPLAY isn't set), so using X11.");
        if (isWsl && !IsOn(env(ForceWaylandVariable)))
            return new(false, $"Under WSL, so using X11 through WSLg (native Wayland hung on WSLg's Weston; set {ForceWaylandVariable}=1 to try it).");
        return new(true, isWsl
            ? $"{ForceWaylandVariable} is set: trying native Wayland under WSL, with X11 as the fallback."
            : "Trying native Wayland first; Avalonia falls back to X11 if the compositor can't be reached.");
    }

    private static bool IsOn(string? v) => v?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
