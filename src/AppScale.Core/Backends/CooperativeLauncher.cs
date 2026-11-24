using System.Diagnostics;
using AppScale.Core.Scaling;
using AppScale.Core.Targets;

namespace AppScale.Core.Backends;

/// <summary>
/// Starts a cooperative application with <c>APPSCALE_DPI</c> set (restart-required path).
/// Optionally enables the experimental process-local Detours adapter, which only the owned
/// Win32 test harness honours.
/// </summary>
public static class CooperativeLauncher
{
    public static ScaleResult Launch(string executablePath, int scalePercent, string? arguments = null, bool experimentalDetours = false, Action<Process>? onStarted = null)
    {
        if (!ScaleMath.IsValidPercent(scalePercent))
            return ScaleResult.Fail(ScaleFailureCode.InvalidScale, $"Scale must be {ScaleMath.MinPercent}–{ScaleMath.MaxPercent}%.");
        if (!File.Exists(executablePath))
            return ScaleResult.Fail(ScaleFailureCode.LaunchFailed, $"Not found: {executablePath}");

        int dpi = ScaleMath.DpiForPercent(scalePercent);
        var psi = new ProcessStartInfo(executablePath, arguments ?? "") { UseShellExecute = false };
        psi.Environment[CooperativeProtocol.StartupDpiEnvironmentVariable] = dpi.ToString();
        if (experimentalDetours) psi.Environment[CooperativeProtocol.ExperimentalDetoursEnvironmentVariable] = "1";
        try
        {
            var p = Process.Start(psi);
            if (p is null) return ScaleResult.Fail(ScaleFailureCode.LaunchFailed, "Process.Start returned null.");
            onStarted?.Invoke(p);
            return ScaleResult.Ok($"Launched {Path.GetFileName(executablePath)} (pid {p.Id}) with APPSCALE_DPI={dpi}. Only cooperative applications honour it.", dpi, restartRequired: true);
        }
        catch (Exception ex)
        {
            return ScaleResult.Fail(ScaleFailureCode.LaunchFailed, ex.Message);
        }
    }
}

/// <summary>Host that can display a magnified, read-only view of a window (implemented by the UI with DWM thumbnails).</summary>
public interface IVisualPreviewHost
{
    bool Show(TargetInfo target, double zoom, out string error);
    void Close(nint hwnd);
    bool IsShowing(nint hwnd);
}

/// <summary>
/// Visual magnification fallback: a separate, clearly labelled, read-only window that shows a
/// scaled DWM thumbnail of the target. This is NOT native rendering and does not forward input.
/// </summary>
public sealed class VisualPreviewBackend(IVisualPreviewHost? host) : IScaleBackend
{
    public string Id => "visual-preview";
    public string DisplayName => "Visual magnification (bitmap, read-only)";
    public BackendCapabilities Capabilities => BackendCapabilities.VisualOnly | BackendCapabilities.LiveChange;

    public ProbeResult Probe(TargetInfo target)
    {
        if (host is null) return ProbeResult.No(ScaleFailureCode.NotSupported, "Visual preview needs the AppScale UI (no host in this process).");
        if (target.IsMinimized) return ProbeResult.No(ScaleFailureCode.NotSupported, "Window is minimized; DWM provides no live thumbnail content.");
        if (target.MonitorDpi <= 0) return ProbeResult.No(ScaleFailureCode.NotSupported, "Unknown monitor DPI.");
        return ProbeResult.Yes(Capabilities, "DWM thumbnail preview (bitmap scaling, no input).");
    }

    public ScaleResult Apply(TargetInfo target, ScaleRequest request)
    {
        var probe = Probe(target);
        if (!probe.Supported) return ScaleResult.Fail(probe.Code, probe.Detail);
        int actual = target.WindowDpi > 0 ? target.WindowDpi : target.MonitorDpi;
        double zoom = ScaleMath.RelativeZoom(request.RequestedDpi, actual);
        return host!.Show(target, zoom, out var error)
            ? ScaleResult.Ok($"Visual magnification ×{zoom:0.00} (requested {request.RequestedDpi} DPI ÷ actual {actual} DPI). Not native rendering; read-only.")
            : ScaleResult.Fail(ScaleFailureCode.NotSupported, error);
    }

    public ScaleResult Reset(TargetInfo target)
    {
        host?.Close(target.Hwnd);
        return ScaleResult.Ok("Preview closed.");
    }

    public BackendStatus GetStatus(TargetInfo target) =>
        new(Id, host?.IsShowing(target.Hwnd) == true, null, target.WindowDpi, "Visual only");
}
