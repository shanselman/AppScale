using System.Runtime.InteropServices;

namespace WinFormsHarness;

/// <summary>Minimal P/Invoke copied locally; the harness must not depend on AppScale.Core.</summary>
internal static partial class Native
{
    public const string PropertyName = "AppScale.Cooperative.v1";
    public const string SetDpiMessageName = "AppScale.SetDpi.v1";
    public const string GetDpiMessageName = "AppScale.GetDpi.v1";
    public const string StartupDpiEnvironmentVariable = "APPSCALE_DPI";
    public const int MinDpi = 72, MaxDpi = 240;

    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_DPICHANGED_BEFOREPARENT = 0x02E2;
    public const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
    public const int WM_GETDPISCALEDSIZE = 0x02E4;
    public const uint CWP_SKIPINVISIBLE = 0x1, CWP_SKIPTRANSPARENT = 0x4;
    public const uint PW_RENDERFULLCONTENT = 0x2;
    public const int OBJID_VSCROLL = unchecked((int)0xFFFFFFFB);
    public const int SM_CXVSCROLL = 2;
    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCROLLBARINFO
    {
        public int cbSize;
        public RECT rcScrollBar;
        public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", EntryPoint = "SetPropW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProp(nint hwnd, string name, nint data);

    [LibraryImport("user32.dll", EntryPoint = "RemovePropW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint RemoveProp(nint hwnd, string name);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmon, int type, out uint dpiX, out uint dpiY);

    public static int MonitorDpi(Point p)
    {
        nint mon = MonitorFromPoint(new POINT(p.X, p.Y), 2);
        return GetDpiForMonitor(mon, 0, out uint x, out _) == 0 ? (int)x : 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowDpiAwarenessContext(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    public static partial int GetAwarenessFromDpiAwarenessContext(nint ctx);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    public static partial int MapWindowPoints(nint from, nint to, ref RECT rect, int count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref POINT pt);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref POINT pt);

    [LibraryImport("user32.dll")]
    public static partial nint ChildWindowFromPointEx(nint parent, POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT pt);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsChild(nint parent, nint child);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetScrollBarInfo(nint hwnd, int idObject, ref SCROLLBARINFO psbi);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc cb, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hwnd, System.Text.StringBuilder sb, int max);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    public static int MulDiv(int value, int numerator, int denominator) =>
        (int)Math.Round((double)value * numerator / denominator, MidpointRounding.AwayFromZero);

    public static nint MakeLong(int low, int high) => (nint)(((high & 0xFFFF) << 16) | (low & 0xFFFF));

    public static string AwarenessName(nint ctx)
    {
        if (ctx == 0) return "unknown";
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4
        if (AreDpiAwarenessContextsEqual(ctx, -4)) return "PerMonitorV2";
        return GetAwarenessFromDpiAwarenessContext(ctx) switch
        {
            0 => "Unaware",
            1 => "SystemAware",
            2 => "PerMonitor",
            _ => "Invalid",
        };
    }

    public static string ClassName(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Parses APPSCALE_DPI; invalid or missing values mean "follow the OS" (null).</summary>
    public static int? StartupDpi()
    {
        string? raw = Environment.GetEnvironmentVariable(StartupDpiEnvironmentVariable);
        return int.TryParse(raw, out int dpi) && dpi is >= MinDpi and <= MaxDpi ? dpi : null;
    }
}
