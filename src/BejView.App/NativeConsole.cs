using System.Runtime.InteropServices;

namespace BejView.App;

/// <summary>
/// BejView.exe is a windowed program, so Windows gives it no console. For the console commands it borrows the
/// terminal that started it. Output that already goes to a file or a pipe (for example <c>find ... > hits.txt</c> or
/// <c>| Select-String</c>) keeps going there; only the handles with nowhere to go are pointed at the terminal.
/// </summary>
internal static partial class NativeConsole
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint FileTypeUnknown = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int which, nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint handle);

    private static bool Usable(nint h) => h != 0 && h != -1 && GetFileType(h) != FileTypeUnknown;

    /// <summary>Attaches to the parent's console for whichever of stdout and stderr has nowhere to go.</summary>
    public static void AttachIfNeeded()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint outH = GetStdHandle(StdOutputHandle), errH = GetStdHandle(StdErrorHandle);
        bool outOk = Usable(outH), errOk = Usable(errH);
        if (outOk && errOk) return;
        if (!AttachConsole(AttachParentProcess)) return; // started from Explorer: no terminal to borrow
        // Put back the handles that were already redirected, in case attaching replaced them.
        if (outOk) SetStdHandle(StdOutputHandle, outH);
        if (errOk) SetStdHandle(StdErrorHandle, errH);
    }
}
