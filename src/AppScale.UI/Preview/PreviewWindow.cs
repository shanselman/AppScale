using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AppScale.Core.Targets;
using AppScale.UI.Interop;

namespace AppScale.UI.Preview;

/// <summary>
/// AppScale-owned, read-only window that shows a magnified DWM thumbnail of another window.
/// This is bitmap magnification composed by DWM: no native re-rendering, no input forwarding,
/// and no pixels ever pass through (or are stored by) this process.
/// </summary>
public sealed class PreviewWindow : Window
{
    public const string Label = "Visual magnification preview — bitmap, not native rendering, read-only";

    private readonly TargetInfo _target;
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detailText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 12 };
    private readonly Border _thumbHost = new() { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) };
    private readonly TextBlock _overlay = new()
    {
        Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center, FontSize = 14, Visibility = Visibility.Collapsed,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private nint _thumb;
    private Native.SIZE _sourceSize;
    private bool _minimized;

    public double RequestedZoom { get; private set; }
    /// <summary>Zoom actually displayed after clamping to the work area.</summary>
    public double ShownZoom { get; private set; }
    public Native.RECT DestinationRect { get; private set; }
    public bool IsThumbnailRegistered => _thumb != 0;
    public nint SourceHwnd => _target.Hwnd;
    public string? LastError { get; private set; }
    public bool IsTargetMinimized => _minimized;
    public Native.SIZE SourceSize => _sourceSize;

    /// <summary>Raised (once) when the source window disappears; the preview closes itself.</summary>
    public event Action<PreviewWindow, string>? Notice;

    public PreviewWindow(TargetInfo target, double zoom)
    {
        _target = target;
        RequestedZoom = zoom;
        Title = $"AppScale — visual magnification ×{zoom:0.00} (bitmap, read-only)";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        ShowActivated = true;

        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x30)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8),
            Child = new StackPanel { Children = { _bannerText, _detailText } },
        };
        DockPanel.SetDock(banner, Dock.Top);
        var grid = new Grid { Children = { _thumbHost, _overlay } };
        Content = new DockPanel { LastChildFill = true, Children = { banner, grid } };
        _bannerText.Text = Label;
        UpdateDetail();

        _timer.Tick += (_, _) => Poll();
        Closed += (_, _) => Unregister();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Relayout, DispatcherPriority.Loaded);
        _thumbHost.LayoutUpdated += (_, _) => PushDestination();
    }

    /// <summary>Shows the window and registers the DWM thumbnail. Returns false (and closes) on failure.</summary>
    public bool Start(out string error)
    {
        _thumbHost.Width = 320; _thumbHost.Height = 200;
        Show();
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!Native.IsWindow(_target.Hwnd)) { error = "Target window no longer exists."; Close(); return false; }
        int hr = Native.DwmRegisterThumbnail(hwnd, _target.Hwnd, out _thumb);
        if (hr != 0) { error = $"DwmRegisterThumbnail failed: {Native.Hr(hr)}"; LastError = error; _thumb = 0; Close(); return false; }
        hr = Native.DwmQueryThumbnailSourceSize(_thumb, out _sourceSize);
        if (hr != 0 || _sourceSize.cx <= 0 || _sourceSize.cy <= 0)
        {
            error = hr != 0 ? $"DwmQueryThumbnailSourceSize failed: {Native.Hr(hr)}" : "DWM reported an empty source size.";
            LastError = error; Close(); return false;
        }
        _minimized = Native.IsIconic(_target.Hwnd);
        Relayout();
        _timer.Start();
        error = "";
        return true;
    }

    public void SetZoom(double zoom)
    {
        RequestedZoom = zoom;
        Title = $"AppScale — visual magnification ×{zoom:0.00} (bitmap, read-only)";
        Relayout();
        Activate();
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Destination size = source size × zoom in physical pixels, clamped (aspect preserved) to the preview monitor's work area.</summary>
    private void Relayout()
    {
        if (_thumb == 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        double scale = DpiScale;
        double w = _sourceSize.cx * RequestedZoom, h = _sourceSize.cy * RequestedZoom;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        double fit = 1;
        if (Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref mi))
        {
            // Leave room for caption, banner and borders (DIPs → physical via the preview's own DPI).
            double maxW = mi.rcWork.Width - 32 * scale;
            double maxH = mi.rcWork.Height - 150 * scale;
            fit = Math.Min(1, Math.Min(maxW / w, maxH / h));
        }
        ShownZoom = RequestedZoom * Math.Max(0.05, fit);
        int pw = Math.Max(1, (int)Math.Round(_sourceSize.cx * ShownZoom));
        int ph = Math.Max(1, (int)Math.Round(_sourceSize.cy * ShownZoom));
        _thumbHost.Width = pw / scale;
        _thumbHost.Height = ph / scale;
        UpdateDetail();
        UpdateLayout();
        PushDestination();
    }

    private void PushDestination()
    {
        if (_thumb == 0 || !IsLoaded) return;
        double scale = DpiScale;
        // DWM destination rectangles are in physical pixels relative to the destination window's client area.
        var origin = _thumbHost.TransformToAncestor(this).Transform(new Point(0, 0));
        int x = (int)Math.Round(origin.X * scale), y = (int)Math.Round(origin.Y * scale);
        int pw = (int)Math.Round(_thumbHost.ActualWidth * scale), ph = (int)Math.Round(_thumbHost.ActualHeight * scale);
        var dest = new Native.RECT(x, y, x + pw, y + ph);
        bool visible = !_minimized;
        if (dest.Equals(DestinationRect) && _lastVisible == visible) return;
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = dest,
            opacity = 255,
            fVisible = visible ? 1 : 0,
            fSourceClientAreaOnly = 0,
        };
        int hr = Native.DwmUpdateThumbnailProperties(_thumb, props);
        if (hr != 0) { ShowOverlay($"DWM could not update the thumbnail: {Native.Hr(hr)}"); LastError = $"DwmUpdateThumbnailProperties failed: {Native.Hr(hr)}"; return; }
        DestinationRect = dest;
        _lastVisible = visible;
    }

    private bool _lastVisible;

    private void Poll()
    {
        if (!Native.IsWindow(_target.Hwnd))
        {
            Notice?.Invoke(this, $"Target window 0x{_target.Hwnd:X} closed; preview unregistered and closed.");
            Close();
            return;
        }
        bool min = Native.IsIconic(_target.Hwnd);
        if (min != _minimized)
        {
            _minimized = min;
            DestinationRect = default;
            PushDestination();
        }
        if (_minimized)
        {
            ShowOverlay("The target window is minimized. DWM provides no live content for minimized windows — restore it to see the preview.");
            return;
        }
        HideOverlay();
        if (Native.DwmQueryThumbnailSourceSize(_thumb, out var size) == 0 && size.cx > 0 && size.cy > 0 &&
            (size.cx != _sourceSize.cx || size.cy != _sourceSize.cy))
        {
            _sourceSize = size;
            Relayout();
        }
    }

    private void ShowOverlay(string text) { _overlay.Text = text; _overlay.Visibility = Visibility.Visible; }
    private void HideOverlay() => _overlay.Visibility = Visibility.Collapsed;

    private void UpdateDetail()
    {
        string clamp = ShownZoom > 0 && Math.Abs(ShownZoom - RequestedZoom) > 0.005
            ? $" — shown at ×{ShownZoom:0.00} (clamped to fit the monitor work area)" : "";
        _detailText.Text = $"Zoom ×{RequestedZoom:0.00}{clamp}. Source: {_target.ExecutableName} \"{_target.Title}\" " +
                           $"({_sourceSize.cx}×{_sourceSize.cy} px at {_target.WindowDpi} DPI). Input is not forwarded; interact with the original window.";
    }

    private void Unregister()
    {
        _timer.Stop();
        if (_thumb != 0) { Native.DwmUnregisterThumbnail(_thumb); _thumb = 0; }
    }
}
