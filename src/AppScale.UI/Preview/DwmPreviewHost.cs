using System.Windows;
using AppScale.Core.Backends;
using AppScale.Core.Targets;

namespace AppScale.UI.Preview;

/// <summary>Core <see cref="IVisualPreviewHost"/> backed by DWM thumbnails in AppScale-owned windows.</summary>
public sealed class DwmPreviewHost : IVisualPreviewHost
{
    private readonly Dictionary<nint, PreviewWindow> _windows = new();

    public event Action<string>? Notice;

    public IReadOnlyDictionary<nint, PreviewWindow> Windows => _windows;

    public bool Show(TargetInfo target, double zoom, out string error)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            string err = "";
            bool ok = dispatcher.Invoke(() => Show(target, zoom, out err));
            error = err;
            return ok;
        }
        if (zoom <= 0 || double.IsNaN(zoom)) { error = "Invalid zoom."; return false; }
        if (_windows.TryGetValue(target.Hwnd, out var existing))
        {
            existing.SetZoom(zoom);
            error = "";
            return true;
        }
        var w = new PreviewWindow(target, zoom);
        w.Notice += (_, msg) => Notice?.Invoke(msg);
        w.Closed += (_, _) => { if (_windows.TryGetValue(target.Hwnd, out var cur) && cur == w) _windows.Remove(target.Hwnd); };
        if (!w.Start(out error)) return false;
        _windows[target.Hwnd] = w;
        return true;
    }

    public void Close(nint hwnd)
    {
        if (_windows.Remove(hwnd, out var w)) w.Close();
    }

    public bool IsShowing(nint hwnd) => _windows.ContainsKey(hwnd);

    public void CloseAll()
    {
        foreach (var w in _windows.Values.ToList()) w.Close();
        _windows.Clear();
    }
}
