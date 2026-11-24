using AppScale.Core.Targets;

namespace AppScale.Core.Backends;

public sealed record BackendChoice(IScaleBackend? Backend, ProbeResult? Probe, IReadOnlyList<(string BackendId, ProbeResult Probe)> Considered, string Explanation);

/// <summary>
/// Picks a backend. Native backends always win. A visual (bitmap) backend is only returned when the
/// caller explicitly allows it — AppScale never silently switches from native to bitmap scaling.
/// Experimental backends are only returned when requested by id.
/// </summary>
public static class BackendSelector
{
    public static BackendChoice Select(IEnumerable<IScaleBackend> backends, TargetInfo target, bool allowVisualFallback, string? preferredBackendId = null)
    {
        var considered = new List<(string, ProbeResult)>();
        var all = backends.ToList();

        if (preferredBackendId is not null)
        {
            var preferred = all.FirstOrDefault(b => b.Id == preferredBackendId);
            if (preferred is null) return new(null, null, considered, $"Backend '{preferredBackendId}' is not available.");
            var p = preferred.Probe(target);
            considered.Add((preferred.Id, p));
            if (p.Supported && (!p.Capabilities.HasFlag(BackendCapabilities.VisualOnly) || allowVisualFallback))
                return new(preferred, p, considered, $"Using requested backend '{preferred.Id}'.");
            if (p.Supported) return new(null, p, considered, "Requested backend is visual-only; visual fallback was not allowed.");
            return new(null, p, considered, $"Requested backend '{preferred.Id}' unsupported: {p.Detail}");
        }

        foreach (var b in all.Where(b => b.Capabilities.HasFlag(BackendCapabilities.NativeRendering) && !b.Capabilities.HasFlag(BackendCapabilities.Experimental)))
        {
            var p = b.Probe(target);
            considered.Add((b.Id, p));
            if (p.Supported) return new(b, p, considered, $"Native backend '{b.Id}': {p.Detail}");
        }

        if (allowVisualFallback)
        {
            foreach (var b in all.Where(b => b.Capabilities.HasFlag(BackendCapabilities.VisualOnly)))
            {
                var p = b.Probe(target);
                considered.Add((b.Id, p));
                if (p.Supported) return new(b, p, considered, $"No native backend; using VISUAL magnification '{b.Id}' because it was explicitly allowed.");
            }
        }

        return new(null, null, considered, allowVisualFallback
            ? "No backend supports this window."
            : "No native backend supports this window. Visual magnification is available only if you explicitly choose it.");
    }
}
