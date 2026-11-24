using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AppScale.IntegrationTests;

internal static partial class TestHostDpi
{
    // testhost.exe has no PMv2 manifest; without this, Windows virtualizes every DPI query to 96.
    [ModuleInitializer]
    internal static void Init() => SetProcessDpiAwarenessContext(-4);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    /// <summary>
    /// Integration tests need a non-elevated interactive desktop large enough for a 250 % window.
    /// Hosted CI runners are elevated (AppScale refuses elevated targets by design, and children inherit
    /// elevation) and use a 1024×768 display (Windows clamps the scaled-up harness windows).
    /// </summary>
    internal static void SkipIfUnsuitableDesktop()
    {
        if (Environment.IsPrivilegedProcess)
            Skip.If(true, "Test host is elevated; AppScale refuses elevated targets by design. Run from a non-elevated desktop.");
        int width = GetSystemMetrics(0 /* SM_CXSCREEN */), height = GetSystemMetrics(1 /* SM_CYSCREEN */);
        Skip.If(width < 1920 || height < 1080, $"Primary display {width}×{height} is too small for 250 % harness windows (Windows clamps them).");
    }
}
