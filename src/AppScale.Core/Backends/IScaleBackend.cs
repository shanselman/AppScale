using AppScale.Core.Targets;

namespace AppScale.Core.Backends;

[Flags]
public enum BackendCapabilities
{
    None = 0,
    /// <summary>The application re-lays out and re-renders at the requested DPI (crisp text).</summary>
    NativeRendering = 1 << 0,
    /// <summary>Scale can change while the application runs.</summary>
    LiveChange = 1 << 1,
    /// <summary>The application must be (re)started for the scale to take effect.</summary>
    RequiresRestart = 1 << 2,
    /// <summary>Mouse/keyboard input works normally at the requested scale.</summary>
    InputSupported = 1 << 3,
    /// <summary>Bitmap magnification only; not native rendering.</summary>
    VisualOnly = 1 << 4,
    /// <summary>Research code behind a feature flag. Never selected automatically.</summary>
    Experimental = 1 << 5,
}

public enum ScaleFailureCode
{
    None = 0,
    InvalidScale,
    OptInRequired,
    EmergencyDisabled,
    FeatureDisabled,
    Excluded,
    Elevated,
    AccessDenied,
    TargetGone,
    NotCooperative,
    NotSupported,
    Timeout,
    Rejected,
    NoBackend,
    LaunchFailed,
}

public sealed record ScaleResult(bool Succeeded, ScaleFailureCode Code, string Message, int? EffectiveDpi = null, bool RestartRequired = false)
{
    public static ScaleResult Ok(string message, int? effectiveDpi = null, bool restartRequired = false) =>
        new(true, ScaleFailureCode.None, message, effectiveDpi, restartRequired);

    public static ScaleResult Fail(ScaleFailureCode code, string message) => new(false, code, message);

    public override string ToString() => Succeeded ? $"OK: {Message}" : $"FAILED ({Code}): {Message}";
}

public sealed record ProbeResult(bool Supported, BackendCapabilities Capabilities, ScaleFailureCode Code, string Detail)
{
    public static ProbeResult Yes(BackendCapabilities caps, string detail) => new(true, caps, ScaleFailureCode.None, detail);
    public static ProbeResult No(ScaleFailureCode code, string detail) => new(false, BackendCapabilities.None, code, detail);
}

public sealed record BackendStatus(string BackendId, bool Active, int? EffectiveDpi, int? OsDpi, string Detail);

public sealed record ScaleRequest(int ScalePercent)
{
    public int RequestedDpi => Scaling.ScaleMath.DpiForPercent(ScalePercent);
}

/// <summary>
/// A way of making one application appear at a different scale. Implementations must return
/// structured failures instead of claiming success they cannot verify.
/// </summary>
public interface IScaleBackend
{
    string Id { get; }
    string DisplayName { get; }
    BackendCapabilities Capabilities { get; }
    ProbeResult Probe(TargetInfo target);
    ScaleResult Apply(TargetInfo target, ScaleRequest request);
    ScaleResult Reset(TargetInfo target);
    BackendStatus GetStatus(TargetInfo target);
}
