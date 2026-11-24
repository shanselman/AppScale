namespace AppScale.Core.Targets;

public enum DpiAwareness
{
    Unknown = -1,
    Unaware = 0,
    SystemAware = 1,
    PerMonitorAware = 2,
    PerMonitorAwareV2 = 3,
    UnawareGdiScaled = 4,
}

/// <summary>Stable identity used to key profiles: normalized full executable path.</summary>
public sealed record ExecutableIdentity(string FullPath)
{
    public string Key => FullPath.Trim().ToLowerInvariant();
    public string FileName => Path.GetFileName(FullPath);

    public static ExecutableIdentity FromPath(string path) => new(Path.GetFullPath(path));
}

/// <summary>Snapshot of a candidate top-level window. All values are observations; none are guaranteed to stay valid.</summary>
public sealed record TargetInfo
{
    public required nint Hwnd { get; init; }
    public required int ProcessId { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public ExecutableIdentity? Executable { get; init; }
    /// <summary>GetDpiForWindow — DPI the OS believes the window uses (depends on awareness).</summary>
    public int WindowDpi { get; init; }
    /// <summary>Effective DPI of the monitor the window is on (MDT_EFFECTIVE_DPI).</summary>
    public int MonitorDpi { get; init; }
    public DpiAwareness Awareness { get; init; } = DpiAwareness.Unknown;
    public bool IsElevated { get; init; }
    /// <summary>True when we could not open the process for limited query (protected or elevated).</summary>
    public bool AccessDenied { get; init; }
    public bool IsCooperative { get; init; }
    public bool IsMinimized { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }

    public string ExecutableName => Executable?.FileName ?? "(unknown)";

    public override string ToString() =>
        $"0x{Hwnd:X} pid={ProcessId} {ExecutableName} \"{Title}\" dpi={WindowDpi} monitor={MonitorDpi} {Awareness}{(IsCooperative ? " [cooperative]" : "")}{(IsElevated ? " [elevated]" : "")}";
}
