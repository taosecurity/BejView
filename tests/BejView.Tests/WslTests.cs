using BejView.Core.Platform;

namespace BejView.Tests;

public class WslTests
{
    [Theory]
    [InlineData("/mnt/c/Users/rb/Pictures", @"C:\Users\rb\Pictures")]
    [InlineData("/mnt/d/Starfield Dev/", @"D:\Starfield Dev")]
    [InlineData("/mnt/c", @"C:\")]
    [InlineData("/home/rb/shots", @"\\wsl.localhost\Debian\home\rb\shots")]
    [InlineData("/mnt/wsl/shared", @"\\wsl.localhost\Debian\mnt\wsl\shared")]
    public void ToWindows(string linux, string windows) =>
        Assert.Equal(windows, Wsl.ToWindowsPath(linux, "Debian"));

    [Fact]
    public void ToWindows_CustomMountRoot() =>
        Assert.Equal(@"E:\x", Wsl.ToWindowsPath("/drives/e/x", "Ubuntu", "/drives"));

    [Theory]
    [InlineData(@"C:\Users\rb\Pictures\a.png", "/mnt/c/Users/rb/Pictures/a.png")]
    [InlineData(@"d:\", "/mnt/d")]
    [InlineData("C:/temp/x", "/mnt/c/temp/x")]
    [InlineData(@"\\wsl.localhost\Debian\home\rb", "/home/rb")]
    [InlineData(@"\\wsl$\Ubuntu\tmp\a.png", "/tmp/a.png")]
    public void ToLinux(string windows, string linux) =>
        Assert.Equal(linux, Wsl.ToLinuxPath(windows));

    [Fact]
    public void RoundTrip()
    {
        string p = "/mnt/c/Users/rb/My Pictures/SquareCap-20261111-001.png";
        Assert.Equal(p, Wsl.ToLinuxPath(Wsl.ToWindowsPath(p, "Debian")));
    }

    [Fact]
    public void BadPathsAreExplained()
    {
        Assert.Throws<ArgumentException>(() => Wsl.ToWindowsPath("relative/path", "Debian"));
        Assert.Throws<ArgumentException>(() => Wsl.ToWindowsPath("/home/rb", ""));
        Assert.Throws<ArgumentException>(() => Wsl.ToLinuxPath(@"relative\path"));
    }

    [Fact]
    public void Detection()
    {
        Assert.True(Wsl.IsWsl(n => n == "WSL_DISTRO_NAME" ? "Debian" : null, _ => false));
        Assert.True(Wsl.IsWsl(_ => null, f => f == Wsl.InteropFile));
        Assert.False(Wsl.IsWsl(_ => null, _ => false));
    }
}

