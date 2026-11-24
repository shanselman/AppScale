using AppScale.Core.Scaling;
using AppScale.Core.Targets;
using static AppScale.Core.Interop.NativeMethods;

namespace AppScale.Core.Backends;

/// <summary>Constants and helpers for the cooperative protocol (docs/adr/0002-cooperative-protocol.md).</summary>
public static class CooperativeProtocol
{
    public const string PropertyName = "AppScale.Cooperative.v1";
    public const string SetDpiMessageName = "AppScale.SetDpi.v1";
    public const string GetDpiMessageName = "AppScale.GetDpi.v1";
    public const string StartupDpiEnvironmentVariable = "APPSCALE_DPI";
    public const string ExperimentalDetoursEnvironmentVariable = "APPSCALE_EXPERIMENTAL_DETOURS";
    public const uint TimeoutMs = 2000;

    private static readonly Lazy<uint> SetMsg = new(() => RegisterWindowMessage(SetDpiMessageName));
    private static readonly Lazy<uint> GetMsg = new(() => RegisterWindowMessage(GetDpiMessageName));

    public enum SendOutcome { Ok, Timeout, Gone }

    public static SendOutcome TrySetDpi(nint hwnd, int dpi, out bool accepted)
    {
        accepted = false;
        if (!IsWindow(hwnd)) return SendOutcome.Gone;
        if (SendMessageTimeout(hwnd, SetMsg.Value, dpi, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, TimeoutMs, out nint r) == 0)
            return IsWindow(hwnd) ? SendOutcome.Timeout : SendOutcome.Gone;
        accepted = r == 1;
        return SendOutcome.Ok;
    }

    public static SendOutcome TryGetDpi(nint hwnd, out int effectiveDpi, out int osDpi)
    {
        effectiveDpi = osDpi = 0;
        if (!IsWindow(hwnd)) return SendOutcome.Gone;
        if (SendMessageTimeout(hwnd, GetMsg.Value, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, TimeoutMs, out nint r) == 0)
            return IsWindow(hwnd) ? SendOutcome.Timeout : SendOutcome.Gone;
        effectiveDpi = (int)(r & 0xFFFF);
        osDpi = (int)((r >> 16) & 0xFFFF);
        return SendOutcome.Ok;
    }
}

/// <summary>
/// Native re-rendering through the cooperative protocol. Only works for windows that advertise
/// <see cref="CooperativeProtocol.PropertyName"/>; everything else fails closed.
/// </summary>
public sealed class CooperativeBackend : IScaleBackend
{
    public string Id => "cooperative";
    public string DisplayName => "Cooperative (native re-render)";
    public BackendCapabilities Capabilities => BackendCapabilities.NativeRendering | BackendCapabilities.LiveChange | BackendCapabilities.InputSupported;

    public ProbeResult Probe(TargetInfo target)
    {
        if (!IsWindow(target.Hwnd)) return ProbeResult.No(ScaleFailureCode.TargetGone, "Window no longer exists.");
        if (!target.IsCooperative)
            return ProbeResult.No(ScaleFailureCode.NotCooperative, "Window does not implement the AppScale cooperative protocol.");
        return CooperativeProtocol.TryGetDpi(target.Hwnd, out int eff, out int os) switch
        {
            CooperativeProtocol.SendOutcome.Ok when eff > 0 => ProbeResult.Yes(Capabilities, $"Cooperative v1 (effective {eff} DPI, OS {os} DPI)."),
            CooperativeProtocol.SendOutcome.Ok => ProbeResult.No(ScaleFailureCode.NotCooperative, "Window advertises the protocol but did not answer GetDpi."),
            CooperativeProtocol.SendOutcome.Timeout => ProbeResult.No(ScaleFailureCode.Timeout, "Window did not respond in time."),
            _ => ProbeResult.No(ScaleFailureCode.TargetGone, "Window closed during probe."),
        };
    }

    public ScaleResult Apply(TargetInfo target, ScaleRequest request) => Send(target, request.RequestedDpi);

    public ScaleResult Reset(TargetInfo target) => Send(target, 0);

    private static ScaleResult Send(TargetInfo target, int dpi)
    {
        if (!target.IsCooperative) return ScaleResult.Fail(ScaleFailureCode.NotCooperative, "Not a cooperative window.");
        var outcome = CooperativeProtocol.TrySetDpi(target.Hwnd, dpi, out bool accepted);
        if (outcome == CooperativeProtocol.SendOutcome.Gone) return ScaleResult.Fail(ScaleFailureCode.TargetGone, "Window closed.");
        if (outcome == CooperativeProtocol.SendOutcome.Timeout) return ScaleResult.Fail(ScaleFailureCode.Timeout, "Window did not respond within 2 s.");
        if (!accepted) return ScaleResult.Fail(ScaleFailureCode.Rejected, $"Application rejected DPI {dpi}.");

        // Verify instead of trusting the acknowledgement.
        if (CooperativeProtocol.TryGetDpi(target.Hwnd, out int eff, out int os) != CooperativeProtocol.SendOutcome.Ok)
            return ScaleResult.Fail(ScaleFailureCode.Timeout, "Applied, but could not verify the effective DPI.");
        int expected = dpi == 0 ? os : dpi;
        return eff == expected
            ? ScaleResult.Ok(dpi == 0 ? $"Reset to OS DPI {os}." : $"Effective DPI {eff} ({ScaleMath.PercentForDpi(eff):0}%), OS DPI {os}.", eff)
            : ScaleResult.Fail(ScaleFailureCode.Rejected, $"Application reports {eff} DPI, expected {expected}.");
    }

    public BackendStatus GetStatus(TargetInfo target) =>
        CooperativeProtocol.TryGetDpi(target.Hwnd, out int eff, out int os) == CooperativeProtocol.SendOutcome.Ok
            ? new BackendStatus(Id, eff != os, eff, os, eff != os ? "Override active" : "Following OS DPI")
            : new BackendStatus(Id, false, null, null, "Unavailable");
}
