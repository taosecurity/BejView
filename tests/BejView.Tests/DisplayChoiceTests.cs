using BejView.Core.Platform;

namespace BejView.Tests;

/// <summary>Native Wayland or X11 on Linux (step 4): the user's desktops are KDE Plasma 6 on Wayland.</summary>
public class DisplayChoiceTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        n => vars.FirstOrDefault(v => v.Name == n).Value;

    [Fact]
    public void A_wayland_desktop_gets_native_wayland()
    {
        var c = DisplayChoice.Decide(Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")), isWsl: false);
        Assert.True(c.Wayland);
        Assert.Contains("falls back to X11", c.Reason);
    }

    [Fact]
    public void Without_a_wayland_compositor_it_uses_x11() =>
        Assert.False(DisplayChoice.Decide(Env(("DISPLAY", ":0")), isWsl: false).Wayland);

    [Fact]
    public void Wsl_uses_x11_unless_wayland_is_asked_for()
    {
        var wslg = Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"));
        var c = DisplayChoice.Decide(wslg, isWsl: true);
        Assert.False(c.Wayland);
        Assert.Contains(DisplayChoice.ForceWaylandVariable, c.Reason);

        Assert.True(DisplayChoice.Decide(Env(("WAYLAND_DISPLAY", "wayland-0"), (DisplayChoice.ForceWaylandVariable, "1")), isWsl: true).Wayland);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(" Yes ")]
    public void Bejview_x11_forces_x11_everywhere(string value)
    {
        var env = Env(("WAYLAND_DISPLAY", "wayland-0"), (DisplayChoice.ForceX11Variable, value), (DisplayChoice.ForceWaylandVariable, "1"));
        Assert.False(DisplayChoice.Decide(env, isWsl: false).Wayland);
        Assert.False(DisplayChoice.Decide(env, isWsl: true).Wayland);
    }

    [Fact]
    public void The_app_id_matches_the_desktop_file() =>
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "packaging", "linux", DisplayChoice.AppId + ".desktop")));
}
