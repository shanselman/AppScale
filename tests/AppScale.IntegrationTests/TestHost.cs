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
}
