using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Safety;
using AppScale.Core.Scaling;
using AppScale.Core.Targets;

namespace AppScale.Core;

/// <summary>
/// The single entry point UI and CLI use to apply scale. Enforces opt-in, emergency disable,
/// safety exclusions and backend selection, and tracks what it changed so Reset/ResetAll work.
/// </summary>
public sealed class ScaleController(SettingsStore store, IReadOnlyList<IScaleBackend> backends)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (TargetInfo Target, IScaleBackend Backend)> _applied = new();

    public SettingsStore Store => store;
    public IReadOnlyList<IScaleBackend> Backends => backends;
    public IReadOnlyCollection<(TargetInfo Target, IScaleBackend Backend)> Applied => _applied.Values.ToList();

    public static IReadOnlyList<IScaleBackend> DefaultBackends(IVisualPreviewHost? previewHost) =>
        [new CooperativeBackend(), new InjectionBackend(), new VisualPreviewBackend(previewHost)];

    public ScaleResult CheckPreconditions(TargetInfo target, int scalePercent)
    {
        var s = store.Load();
        if (!s.OptedIn) return ScaleResult.Fail(ScaleFailureCode.OptInRequired, "AppScale is not enabled. Opt in first (UI: Enable, CLI: appscale optin).");
        if (s.EmergencyDisabled) return ScaleResult.Fail(ScaleFailureCode.EmergencyDisabled, "Emergency disable is on.");
        if (!ScaleMath.IsValidPercent(scalePercent)) return ScaleResult.Fail(ScaleFailureCode.InvalidScale, $"Scale must be {ScaleMath.MinPercent}–{ScaleMath.MaxPercent}%.");
        var code = SafetyPolicy.Check(target, out var reason);
        return code == ScaleFailureCode.None ? ScaleResult.Ok("ok") : ScaleResult.Fail(code, reason);
    }

    public BackendChoice Choose(TargetInfo target, bool allowVisualFallback, string? preferredBackend = null)
    {
        var s = store.Load();
        var usable = backends.Where(b =>
            (!b.Capabilities.HasFlag(BackendCapabilities.VisualOnly) || s.Features.VisualPreview) &&
            (b.Id != InjectionBackend.BackendId || s.Features.Injection) &&
            (!b.Capabilities.HasFlag(BackendCapabilities.Experimental) || s.Features.ExperimentalDetours));
        return BackendSelector.Select(usable, target, allowVisualFallback, preferredBackend);
    }

    public (ScaleResult Result, BackendChoice? Choice) Apply(TargetInfo target, int scalePercent, bool allowVisualFallback, string? preferredBackend = null)
    {
        var pre = CheckPreconditions(target, scalePercent);
        if (!pre.Succeeded) return (pre, null);
        var choice = Choose(target, allowVisualFallback, preferredBackend);
        if (choice.Backend is null) return (ScaleResult.Fail(ScaleFailureCode.NoBackend, choice.Explanation), choice);
        var result = choice.Backend.Apply(target, new ScaleRequest(scalePercent));
        if (result.Succeeded) _applied[target.Hwnd] = (target, choice.Backend);
        return (result, choice);
    }

    public ScaleResult Reset(TargetInfo target)
    {
        if (_applied.TryRemove(target.Hwnd, out var entry)) return entry.Backend.Reset(target);
        // Not applied by this controller instance (e.g. CLI): reset any cooperative override anyway.
        return target.IsCooperative ? new CooperativeBackend().Reset(target) : ScaleResult.Ok("Nothing to reset.");
    }

    public BackendStatus GetStatus(TargetInfo target)
    {
        if (_applied.TryGetValue(target.Hwnd, out var entry)) return entry.Backend.GetStatus(target);
        return target.IsCooperative ? new CooperativeBackend().GetStatus(target) : new BackendStatus("none", false, null, target.WindowDpi, "No override");
    }

    /// <summary>Resets every override this controller applied. Used on exit and by emergency disable.</summary>
    public IReadOnlyList<ScaleResult> ResetAll()
    {
        var results = new List<ScaleResult>();
        foreach (var hwnd in _applied.Keys.ToList()) if (_applied.TryRemove(hwnd, out var e)) results.Add(e.Backend.Reset(e.Target));
        return results;
    }

    public IReadOnlyList<ScaleResult> EmergencyDisable()
    {
        store.Update(s => s with { EmergencyDisabled = true });
        var results = ResetAll().ToList();
        // Also reset any cooperative window still holding an override from an earlier session.
        foreach (var t in WindowInspector.EnumerateTopLevelWindows(includeUntitled: true).Where(t => t.IsCooperative))
            results.Add(new CooperativeBackend().Reset(t));
        return results;
    }
}
