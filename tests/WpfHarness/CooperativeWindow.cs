using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfHarness;

/// <summary>App-wide absolute effective-DPI override (null = follow each window's OS DPI).</summary>
internal static class DpiState
{
    public static int? Override { get; private set; }
    /// <summary>False with --no-cooperative: no protocol, no override, no transform; stock WPF PMv2 only.</summary>
    public static bool Cooperative { get; set; } = true;
    public static bool PopupScaling { get; set; } = true;
    public static readonly List<CooperativeWindow> OpenWindows = new();
    private static readonly List<WeakReference<FrameworkElement>> PopupRoots = new();

    public static void SetOverride(int? dpi, string source)
    {
        Override = dpi;
        Log.Write("override", new { requestedDpi = dpi, source });
        foreach (var w in OpenWindows.ToArray()) w.ApplyEffectiveDpi(source);
        foreach (var r in PopupRoots.ToArray())
            if (r.TryGetTarget(out var root)) ScalePopupRoot(root); else PopupRoots.Remove(r);
    }

    /// <summary>
    /// ContextMenu and ToolTip live in separate popup HWNDs whose visual root is the internal PopupRoot and are
    /// NOT descendants of the window's scaled <c>Inner</c> grid, so they do not inherit its LayoutTransform.
    /// (Menu submenus and the ComboBox dropdown use a Popup declared inside the control template; WPF's Popup
    /// copies the placement target's transform onto the PopupRoot, so those already follow — measured.)
    /// We put a ScaleTransform(eff / popupOs) on the ContextMenu / ToolTip element itself when it opens.
    /// Note: a class handler on FrameworkElement.LoadedEvent never fires for popup content (WPF only broadcasts
    /// Loaded into subtrees that have instance Loaded handlers), so the routed Opened events are used instead.
    /// </summary>
    public static void HookPopups()
    {
        var h = new RoutedEventHandler((s, _) =>
        {
            if (s is not FrameworkElement fe) return;
            if (!PopupRoots.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, fe)))
                PopupRoots.Add(new WeakReference<FrameworkElement>(fe));
            ScalePopupRoot(fe);
        });
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, h);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, h);
    }

    public static void ScalePopupRoot(FrameworkElement root)
    {
        if (PresentationSource.FromVisual(root) is not HwndSource src) return;
        int os = (int)Native.GetDpiForWindow(src.Handle);
        double s = PopupScaling && Override is int eff && os > 0 ? (double)eff / os : 1.0;
        if (root.LayoutTransform is ScaleTransform st && st.ScaleX == s) return;
        root.LayoutTransform = s == 1.0 ? Transform.Identity : new ScaleTransform(s, s);
        Log.Write("popup.scaled", new { element = root.GetType().Name, popupOsDpi = os, scale = s });
    }
}

/// <summary>
/// Window implementing cooperative protocol v1. Content is hosted in <see cref="Outer"/> (no transform) →
/// <see cref="Inner"/> (LayoutTransform = ScaleTransform(eff / osDpi)). WPF re-lays out and re-rasterises text and
/// vectors under the transform, so this is native rendering at the effective DPI, not bitmap scaling.
/// The ratio is always recomputed from the absolute effective DPI and the current OS DPI (no accumulation).
/// </summary>
internal class CooperativeWindow : Window
{
    public static readonly uint SetDpiMsg = Native.RegisterWindowMessage(Native.SetDpiMessageName);
    public static readonly uint GetDpiMsg = Native.RegisterWindowMessage(Native.GetDpiMessageName);

    public readonly Border Outer = new();
    public readonly Grid Inner = new();
    private readonly ScaleTransform _scale = new(1, 1);
    public nint Hwnd { get; private set; }
    public double LastApplyMs { get; private set; }
    public int DpiChangedCount { get; private set; }

    public CooperativeWindow()
    {
        if (DpiState.Cooperative) Inner.LayoutTransform = _scale;
        Outer.Child = Inner;
        Content = Outer;
        UseLayoutRounding = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanMinimize;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            if (Hwnd != 0 && DpiState.Cooperative) Native.RemoveProp(Hwnd, Native.PropertyName);
            DpiState.OpenWindows.Remove(this);
        };
    }

    public int OsDpi => Hwnd != 0 ? (int)Native.GetDpiForWindow(Hwnd) : (int)Native.GetDpiForSystem();
    public int EffectiveDpi => DpiState.Override ?? OsDpi;
    public double Scale => _scale.ScaleX;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(Hwnd).AddHook(WndProc);
        DpiState.OpenWindows.Add(this);
        if (!DpiState.Cooperative)
        {
            Log.Write("window.created", Snapshot("sourceInitialized"));
            ContentRendered += (_, _) => Log.Write("stock.observed", Snapshot("contentRendered"));
            return;
        }
        Native.SetProp(Hwnd, Native.PropertyName, 1);
        Log.Write("window.created", Snapshot("sourceInitialized"));
        ApplyEffectiveDpi("startup");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (!DpiState.Cooperative)
        {
            if (msg == Native.WM_DPICHANGED)
            {
                DpiChangedCount++;
                var rc = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(lParam);
                Log.Write("wm.dpichanged", new { wParamX = (int)(wParam & 0xFFFF), wParamY = (int)((wParam >> 16) & 0xFFFF), suggestedRect = new { rc.Left, rc.Top, rc.Width, rc.Height }, before = Snapshot("wm_dpichanged.before") });
            }
            else if (msg == Native.WM_GETDPISCALEDSIZE)
                Log.Write("wm.dpi-related", new { msg = $"0x{msg:X4}", wParam = (long)wParam, getDpiForWindow = OsDpi });
            return 0; // never handle anything: WPF's own processing runs
        }
        if ((uint)msg == SetDpiMsg && SetDpiMsg != 0)
        {
            handled = true;
            int dpi = (int)wParam;
            bool ok = dpi == 0 || dpi is >= Native.MinDpi and <= Native.MaxDpi;
            Log.Write("protocol.setDpi", new { wParam = dpi, accepted = ok, osDpi = OsDpi });
            if (ok) DpiState.SetOverride(dpi == 0 ? null : dpi, "protocol");
            return ok ? 1 : 0;
        }
        if ((uint)msg == GetDpiMsg && GetDpiMsg != 0)
        {
            handled = true;
            Log.Write("protocol.getDpi", new { effectiveDpi = EffectiveDpi, osDpi = OsDpi });
            return Native.MakeLong(EffectiveDpi, OsDpi);
        }
        if (msg == Native.WM_DPICHANGED)
        {
            DpiChangedCount++;
            Log.Write("wm.dpichanged", new { newOsDpi = (int)(wParam & 0xFFFF), overrideDpi = DpiState.Override });
        }
        return 0;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        // WPF has already rescaled DIP→pixels for the new monitor; keep the absolute effective DPI by
        // recomputing eff / newOs. (Position from the suggested RECT is applied by WPF.)
        base.OnDpiChanged(oldDpi, newDpi);
        if (!DpiState.Cooperative)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                Log.Write("stock.observed", new { reason = "Window.OnDpiChanged", oldPixelsPerDip = oldDpi.PixelsPerDip, newPixelsPerDip = newDpi.PixelsPerDip, state = Snapshot("dpichanged.after") }));
            return;
        }
        ApplyEffectiveDpi("wm_dpichanged");
    }

    public void ApplyEffectiveDpi(string reason)
    {
        if (Hwnd == 0) return;
        var sw = Stopwatch.StartNew();
        double s = (double)EffectiveDpi / OsDpi;
        if (_scale.ScaleX != s) { _scale.ScaleX = s; _scale.ScaleY = s; }
        UpdateLayout();
        LastApplyMs = sw.Elapsed.TotalMilliseconds;
        Log.Write("dpi.applied", Snapshot(reason));
    }

    public object Snapshot(string reason)
    {
        Native.GetWindowRect(Hwnd, out var wr);
        Native.GetClientRect(Hwnd, out var cr);
        var vdpi = VisualTreeHelper.GetDpi(this);
        return new
        {
            reason,
            window = GetType().Name,
            hwnd = $"0x{Hwnd:X}",
            osDpi = OsDpi,
            effectiveDpi = EffectiveDpi,
            overrideDpi = DpiState.Override,
            wpfPixelsPerDip = vdpi.PixelsPerDip,
            systemDpi = (int)Native.GetDpiForSystem(),
            awareness = Native.AwarenessName(Native.GetWindowDpiAwarenessContext(Hwnd)),
            threadAwareness = Native.AwarenessName(Native.GetThreadDpiAwarenessContext()),
            scalePercent = Math.Round(EffectiveDpi * 100.0 / 96, 1),
            layoutTransformScale = Math.Round(_scale.ScaleX, 6),
            windowRect = new { wr.Left, wr.Top, wr.Width, wr.Height },
            clientRect = new { cr.Width, cr.Height },
            applyMs = Math.Round(LastApplyMs, 3),
            mode = DpiState.Cooperative ? "cooperative" : "stock",
            metrics = Metrics(),
        };
    }

    /// <summary>Per-window layout/font metrics for comparing cooperative vs stock/injected runs.</summary>
    protected virtual object? Metrics() => null;

    /// <summary>Physical-pixel bounds of <paramref name="e"/> relative to <paramref name="origin"/>, measured above the scale transform.</summary>
    public Rect PhysicalBounds(FrameworkElement e, FrameworkElement origin)
    {
        var r = e.TransformToAncestor(Outer).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        var o = origin.TransformToAncestor(Outer).Transform(new Point(0, 0));
        double k = OsDpi / 96.0;
        return new Rect(Math.Round((r.X - o.X) * k), Math.Round((r.Y - o.Y) * k), Math.Round(r.Width * k), Math.Round(r.Height * k));
    }

    protected static T Place<T>(Canvas c, T e, double x, double y, double w, double h) where T : FrameworkElement
    {
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y); e.Width = w; e.Height = h;
        c.Children.Add(e);
        return e;
    }
}
