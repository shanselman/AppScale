using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Safety;
using AppScale.Core.Targets;

namespace AppScale.UI.ViewModels;

public sealed class WindowRow
{
    private readonly bool redact;

    public WindowRow(TargetInfo target, bool redact)
    {
        Target = target;
        this.redact = redact;
        ExclusionCode = SafetyPolicy.Check(target, out var reason);
        ExclusionReason = ExclusionCode == ScaleFailureCode.None ? "" : reason;
    }

    public TargetInfo Target { get; }
    public ScaleFailureCode ExclusionCode { get; }
    public string ExclusionReason { get; }
    public bool IsExcluded => ExclusionCode != ScaleFailureCode.None;
    public bool IsSelectable => !IsExcluded;

    // Redaction is only used for evidence screenshots: other apps' titles and paths are not recorded.
    private bool Hide => redact && !Target.IsCooperative;
    public string ExecutableName => Target.ExecutableName;
    public string ExecutablePath => Hide ? "(path hidden for screenshot)" : Target.Executable?.FullPath ?? "(unavailable)";
    public string Title => Hide ? "(title hidden for screenshot)" : Target.Title;
    public string Hwnd => $"0x{Target.Hwnd:X}";
    public int WindowDpi => Target.WindowDpi;
    public int MonitorDpi => Target.MonitorDpi;
    public string Awareness => Target.Awareness switch
    {
        DpiAwareness.PerMonitorAwareV2 => "PMv2",
        DpiAwareness.PerMonitorAware => "PMv1",
        DpiAwareness.SystemAware => "System",
        DpiAwareness.Unaware => "Unaware",
        DpiAwareness.UnawareGdiScaled => "GDI-scaled",
        _ => "Unknown",
    };
    public string Cooperative => Target.IsCooperative ? "Yes" : "No";
    public string Status => IsExcluded ? $"Excluded: {ExclusionReason}" : Target.IsCooperative ? "Cooperative (native)" : "Compatibility unknown";
}

public sealed class ProfileRow(ScaleProfile profile, Func<ProfileRow, bool, bool> onEnabledChanged) : ObservableObject
{
    private bool _enabled = profile.Enabled;

    public ScaleProfile Profile { get; private set; } = profile;
    public string ExecutableName => System.IO.Path.GetFileName(Profile.ExecutablePath is { Length: > 0 } p ? p : Profile.ExecutableKey);
    public string ExecutablePath => Profile.ExecutablePath is { Length: > 0 } p ? p : Profile.ExecutableKey;
    public int ScalePercent => Profile.ScalePercent;
    public string Backend => Profile.PreferredBackend ?? "automatic (native only)";
    public string Updated => Profile.UpdatedUtc.ToLocalTime().ToString("g");
    public string AutoText => _enabled ? "Auto-apply on start" : "Off";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            // The callback may decline (e.g. the user did not confirm consent); then the checkbox snaps back.
            if (onEnabledChanged(this, value))
            {
                _enabled = value;
                Profile = Profile with { Enabled = value };
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoText));
        }
    }
}

/// <summary>One "Applied" notification row: what was applied to which app, how, and whether it worked.</summary>
public sealed record AppliedRow(DateTime Time, string Source, string ExecutableName, int ProcessId, string Backend, int? EffectiveDpi, int? OsDpi, bool Succeeded, string Detail)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string DpiText => EffectiveDpi is int e ? $"{e} DPI (OS {(OsDpi is int o ? o.ToString() : "?")})" : "—";
    public string Outcome => Succeeded ? "Applied" : "Failed";
}

public sealed record LogEntry(DateTime Time, string Text, bool IsError)
{
    public string Display => $"{Time:HH:mm:ss}  {Text}";
}
