using System.Runtime.InteropServices;

namespace BejView.Core.Platform;

/// <summary>
/// Whether BejView runs from its Microsoft Store (MSIX) package, and the package family name if so (step 5,
/// 2026-11-13). The zip, the WinGet copy and Linux have no package, so this is null there.
/// </summary>
public static partial class PackageIdentity
{
    private const int ErrorSuccess = 0;

    /// <summary>The package family name, such as RichardBejtlich.BejView_kz4qzpv342p6y, or null when not packaged.</summary>
    public static string? FamilyName { get; } = Read();

    private static string? Read()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) return null;
        try
        {
            var buffer = new char[256];
            uint length = (uint)buffer.Length;
            // APPMODEL_ERROR_NO_PACKAGE (15700) when not packaged.
            if (GetCurrentPackageFamilyName(ref length, ref buffer[0]) != ErrorSuccess || length == 0) return null;
            return new string(buffer, 0, (int)length - 1); // length counts the closing null
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFamilyName(ref uint length, ref char name);
}
