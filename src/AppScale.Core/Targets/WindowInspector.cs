using AppScale.Core.Backends;
using static AppScale.Core.Interop.NativeMethods;

namespace AppScale.Core.Targets;

/// <summary>Read-only inspection of top-level windows. Never writes to other processes.</summary>
public static unsafe class WindowInspector
{
    public static IReadOnlyList<TargetInfo> EnumerateTopLevelWindows(bool includeUntitled = false)
    {
        var handles = new List<nint>();
        EnumWindows((h, _) => { handles.Add(h); return true; }, 0);
        int self = Environment.ProcessId;
        var list = new List<TargetInfo>();
        foreach (var h in handles)
        {
            if (!IsCandidate(h)) continue;
            var info = Inspect(h);
            if (info is null || info.ProcessId == self) continue;
            if (!includeUntitled && string.IsNullOrWhiteSpace(info.Title)) continue;
            list.Add(info);
        }
        return list;
    }

    private static bool IsCandidate(nint h)
    {
        if (!IsWindowVisible(h)) return false;
        if (GetWindow(h, GW_OWNER) != 0) return false;
        if ((GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return true;
    }

    public static TargetInfo? Inspect(nint hwnd)
    {
        if (!IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        var (exe, elevated, denied) = InspectProcess(pid);
        GetWindowRect(hwnd, out var rect);
        return new TargetInfo
        {
            Hwnd = hwnd,
            ProcessId = (int)pid,
            Title = GetText(hwnd),
            ClassName = GetClass(hwnd),
            Executable = exe,
            WindowDpi = (int)GetDpiForWindow(hwnd),
            MonitorDpi = GetMonitorDpi(hwnd),
            Awareness = GetAwareness(hwnd),
            IsElevated = elevated,
            AccessDenied = denied,
            IsCooperative = GetProp(hwnd, CooperativeProtocol.PropertyName) != 0,
            IsMinimized = IsIconic(hwnd),
            WindowWidth = rect.Width,
            WindowHeight = rect.Height,
        };
    }

    public static IReadOnlyList<TargetInfo> FindByProcess(int pid) =>
        EnumerateTopLevelWindows(includeUntitled: true).Where(t => t.ProcessId == pid).ToList();

    public static int GetMonitorDpi(nint hwnd)
    {
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return mon != 0 && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint x, out _) == 0 ? (int)x : 0;
    }

    public static int GetSystemDpi() => (int)GetDpiForSystem();

    private static DpiAwareness GetAwareness(nint hwnd)
    {
        var ctx = GetWindowDpiAwarenessContext(hwnd);
        if (ctx == 0) return DpiAwareness.Unknown;
        // DPI_AWARENESS_CONTEXT pseudo-handles: -1 unaware, -2 system, -3 PMv1, -4 PMv2, -5 unaware GDI-scaled.
        if (AreDpiAwarenessContextsEqual(ctx, -4)) return DpiAwareness.PerMonitorAwareV2;
        if (AreDpiAwarenessContextsEqual(ctx, -5)) return DpiAwareness.UnawareGdiScaled;
        return GetAwarenessFromDpiAwarenessContext(ctx) switch
        {
            0 => DpiAwareness.Unaware,
            1 => DpiAwareness.SystemAware,
            2 => DpiAwareness.PerMonitorAware,
            _ => DpiAwareness.Unknown,
        };
    }

    internal static (ExecutableIdentity? exe, bool elevated, bool denied) InspectProcess(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return (null, false, true);
        try
        {
            ExecutableIdentity? exe = null;
            var buf = new char[1024];
            int size = buf.Length;
            bool ok; fixed (char* pb = buf) ok = QueryFullProcessImageName(h, 0, pb, ref size);
            if (ok)
                exe = new ExecutableIdentity(new string(buf, 0, size));

            bool elevated = false, denied = false;
            if (OpenProcessToken(h, TOKEN_QUERY, out var token))
            {
                try
                {
                    if (GetTokenInformation(token, TokenElevation, out int e, sizeof(int), out _)) elevated = e != 0;
                }
                finally { CloseHandle(token); }
            }
            else denied = true;
            return (exe, elevated, denied);
        }
        finally { CloseHandle(h); }
    }

    private static string GetText(nint h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        int n; fixed (char* p = buf) n = GetWindowText(h, p, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    private static string GetClass(nint h)
    {
        var buf = new char[256];
        int n; fixed (char* p = buf) n = GetClassName(h, p, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }
}
