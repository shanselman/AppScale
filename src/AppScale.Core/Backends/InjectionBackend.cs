using System.Diagnostics;
using AppScale.Core.Scaling;
using AppScale.Core.Targets;

namespace AppScale.Core.Backends;

/// <summary>
/// Native re-rendering for ordinary (non-cooperative) applications. AppScale.Injector loads
/// AppScale.Native.dll into the chosen process only. The DLL hooks that process's DPI queries
/// (GetDpiForWindow, GetDpiForSystem, GetDpiForMonitor, GetDeviceCaps LOGPIXELS, metrics) to
/// return the requested DPI, then sends the app its own WM_DPICHANGED so the app relayouts itself
/// through the same code path it uses when it is dragged to another monitor. Once injected, the
/// process speaks the cooperative protocol, so apply/verify/reset reuse <see cref="CooperativeBackend"/>.
/// Nothing persists: the DLL unloads when the process exits.
/// </summary>
public sealed class InjectionBackend(string? injectorPath = null) : IScaleBackend
{
    public const string BackendId = "inject";
    private readonly CooperativeBackend _protocol = new();

    public string Id => BackendId;
    public string DisplayName => "Injected DPI virtualization (native re-render)";
    public BackendCapabilities Capabilities => BackendCapabilities.NativeRendering | BackendCapabilities.LiveChange | BackendCapabilities.InputSupported;

    public string? InjectorPath { get; } = injectorPath ?? FindInjector();

    public static string? FindInjector()
    {
        if (Environment.GetEnvironmentVariable("APPSCALE_INJECTOR") is { Length: > 0 } env && File.Exists(env)) return env;
        string local = Path.Combine(AppContext.BaseDirectory, "AppScale.Injector.exe");
        if (File.Exists(local)) return local;
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            foreach (string sub in new[] { "AppScale.Injector", "Win32Harness" })
            {
                string candidate = Path.Combine(dir, "artifacts", "bin", sub, "release", "AppScale.Injector.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public ProbeResult Probe(TargetInfo target)
    {
        if (InjectorPath is null) return ProbeResult.No(ScaleFailureCode.NotSupported, "AppScale.Injector.exe not found (build native components).");
        if (target.Awareness is DpiAwareness.Unaware or DpiAwareness.SystemAware or DpiAwareness.UnawareGdiScaled)
            return ProbeResult.No(ScaleFailureCode.NotSupported, $"Window is {target.Awareness}; Windows bitmap-scales it, so a DPI lie cannot make it re-render. Use visual magnification.");
        if (!Environment.Is64BitOperatingSystem || !IsSameBitness(target.ProcessId))
            return ProbeResult.No(ScaleFailureCode.NotSupported, "Only x64 targets are supported so far.");
        return ProbeResult.Yes(Capabilities, target.IsCooperative ? "Already injected / cooperative." : "Will inject AppScale.Native into this process.");
    }

    public ScaleResult Apply(TargetInfo target, ScaleRequest request)
    {
        var ready = EnsureInjected(target, request.RequestedDpi);
        return ready.Target is null ? ready.Failure! : _protocol.Apply(ready.Target, request);
    }

    public ScaleResult Reset(TargetInfo target)
    {
        var t = WindowInspector.Inspect(target.Hwnd) ?? target;
        return t.IsCooperative ? _protocol.Reset(t) : ScaleResult.Ok("Not injected; nothing to reset.");
    }

    public BackendStatus GetStatus(TargetInfo target) => _protocol.GetStatus(target) with { BackendId = Id };

    private (TargetInfo? Target, ScaleResult? Failure) EnsureInjected(TargetInfo target, int dpi)
    {
        if (target.IsCooperative) return (target, null);
        if (InjectorPath is null) return (null, ScaleResult.Fail(ScaleFailureCode.NotSupported, "Injector not found."));

        var psi = new ProcessStartInfo(InjectorPath, $"--inject {target.ProcessId} --dpi {dpi}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        string output;
        try
        {
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { p.Kill(); return (null, ScaleResult.Fail(ScaleFailureCode.Timeout, "Injector timed out.")); }
            if (p.ExitCode != 0) return (null, ScaleResult.Fail(ScaleFailureCode.Rejected, $"Injector failed ({p.ExitCode}): {output.Trim()}"));
        }
        catch (Exception ex)
        {
            return (null, ScaleResult.Fail(ScaleFailureCode.LaunchFailed, ex.Message));
        }

        // The adapter marks the process's top-level windows as cooperative once its hooks are live.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (WindowInspector.Inspect(target.Hwnd) is { IsCooperative: true } t) return (t, null);
            Thread.Sleep(50);
        }
        return (null, ScaleResult.Fail(ScaleFailureCode.NotCooperative, $"Injected, but the window never exposed the AppScale protocol. {output.Trim()}"));
    }

    private static bool IsSameBitness(int pid) => !Interop.NativeMethods.IsWow64(pid);
}

