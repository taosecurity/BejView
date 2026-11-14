using BejView.Core.Platform;

namespace BejView.Tests;

/// <summary>
/// The Microsoft Store package (step 5): when the package keeps a private copy of %APPDATA%\BejView, paths and
/// Help > Open the log file show the private copy, because that's where Explorer and Notepad find the files.
/// </summary>
public class PackageTests
{
    private const string Family = "RichardBejtlich.BejView_kz4qzpv342p6y";
    private const string Copy = @"C:\Users\rb\AppData\Local\Packages\" + Family + @"\LocalCache\Roaming\BejView\bejview.log";
    private const string Log = @"C:\Users\rb\AppData\Roaming\BejView\bejview.log";

    private static string? Env(string name) => name switch
    {
        "APPDATA" => @"C:\Users\rb\AppData\Roaming",
        "LOCALAPPDATA" => @"C:\Users\rb\AppData\Local\",
        _ => null,
    };

    [Fact]
    public void Packaged_with_a_private_copy_shows_the_copy()
        => Assert.Equal(Copy, AppPaths.RealLocation(Log, Family, Env, p => p == Copy));

    [Fact]
    public void Packaged_without_a_private_copy_keeps_the_real_path()
        => Assert.Equal(Log, AppPaths.RealLocation(Log, Family, Env, _ => false)); // redirection turned off

    [Fact]
    public void Not_packaged_keeps_the_path()
        => Assert.Equal(Log, AppPaths.RealLocation(Log, null, Env, _ => true)); // zip, WinGet, Linux

    [Fact]
    public void A_log_outside_AppData_is_never_moved()
    {
        const string mine = @"D:\Logs\bejview.log";
        Assert.Equal(mine, AppPaths.RealLocation(mine, Family, Env, _ => true));
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_AppData_is_never_moved()
    {
        const string other = @"C:\Users\rb\AppData\RoamingX\BejView\bejview.log";
        Assert.Equal(other, AppPaths.RealLocation(other, Family, Env, _ => true));
    }

    [Fact]
    public void Matching_ignores_case_like_Windows()
    {
        const string upper = @"C:\USERS\RB\APPDATA\ROAMING\BejView\bejview.log";
        Assert.Equal(@"C:\Users\rb\AppData\Local\Packages\" + Family + @"\LocalCache\Roaming\BejView\bejview.log",
            AppPaths.RealLocation(upper, Family, Env, _ => true));
    }

    [Fact]
    public void About_credits_use_the_users_wording()
        => Assert.Equal("Developed by Richard Bejtlich. Version 1.0 programmed by Claude Sonnet 3.7. Version 2.0 programmed by Claude Opus 5.5.",
            BejView.App.MainWindow.AboutCredits);

    [Fact]
    public void The_test_process_is_not_packaged()
        => Assert.Null(PackageIdentity.FamilyName);
}
