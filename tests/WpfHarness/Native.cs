using System.Runtime.InteropServices;

namespace WpfHarness;

/// <summary>Minimal P/Invoke copied locally; the harness must not depend on AppScale.Core.</summary>
internal static partial class Native
{
    public const string PropertyName = "AppScale.Cooperative.v1";
    public const string SetDpiMessageName = "AppScale.SetDpi.v1";
    public const string GetDpiMessageName = "AppScale.GetDpi.v1";
    public const string StartupDpiEnvironmentVariable = "APPSCALE_DPI";
    public const int MinDpi = 72, MaxDpi = 240;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_GETDPISCALEDSIZE = 0x02E4;
    public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", EntryPoint = "SetPropW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProp(nint hwnd, string name, nint data);

    [LibraryImport("user32.dll", EntryPoint = "RemovePropW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint RemoveProp(nint hwnd, string name);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    public delegate bool MonitorEnumProc(nint hmon, nint hdc, ref RECT rc, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hmon, ref MONITORINFOEX info);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmon, int type, out uint dpiX, out uint dpiY);

    public sealed record Monitor(nint Handle, string Device, RECT Bounds, RECT Work, int Dpi, bool Primary);

    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(0, 0, (nint h, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(h, ref mi);
            int dpi = GetDpiForMonitor(h, 0, out uint x, out _) == 0 ? (int)x : 0;
            list.Add(new Monitor(h, mi.szDevice, mi.rcMonitor, mi.rcWork, dpi, (mi.dwFlags & 1) != 0));
            return true;
        }, 0);
        return list;
    }

    public static int MulDiv(int value, int numerator, int denominator) =>
        (int)Math.Round((double)value * numerator / denominator, MidpointRounding.AwayFromZero);

    public static nint MakeLong(int low, int high) => (nint)(((high & 0xFFFF) << 16) | (low & 0xFFFF));

    public static string AwarenessName(nint ctx)
    {
        if (ctx == 0) return "unknown";
        if (AreDpiAwarenessContextsEqual(ctx, -4)) return "PerMonitorV2";
        return GetAwarenessFromDpiAwarenessContext(ctx) switch { 0 => "Unaware", 1 => "SystemAware", 2 => "PerMonitor", _ => "Invalid" };
    }

    /// <summary>Parses APPSCALE_DPI; invalid or missing values mean "follow the OS" (null).</summary>
    public static int? StartupDpi()
    {
        string? raw = Environment.GetEnvironmentVariable(StartupDpiEnvironmentVariable);
        return int.TryParse(raw, out int dpi) && dpi is >= MinDpi and <= MaxDpi ? dpi : null;
    }
}
