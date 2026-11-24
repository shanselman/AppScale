using AppScale.Core.Backends;
using AppScale.Core.Targets;

namespace AppScale.Core;

/// <summary>
/// Background watcher: when a top-level window appears for an executable that has an <b>enabled</b>
/// profile, apply that profile's scale (native backends only; never a silent visual fallback).
/// Polls window enumeration (no admin, no WMI, no global hooks). Each process is handled once.
/// </summary>
public sealed class ProfileWatcher(ScaleController controller, TimeSpan? interval = null) : IDisposable
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(750);
    private readonly Dictionary<int, int> _attempts = new();
    private readonly HashSet<int> _done = new();
    private Timer? _timer;
    private int _busy;

    public event Action<TargetInfo, ScaleResult>? Applied;

    public bool IsRunning => _timer is not null;

    public void Start() => _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, _interval);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    /// <summary>One scan. Public so tests and the CLI can drive it deterministically.</summary>
    public IReadOnlyList<(TargetInfo Target, ScaleResult Result)> Tick()
    {
        var results = new List<(TargetInfo, ScaleResult)>();
        if (Interlocked.Exchange(ref _busy, 1) == 1) return results;
        try
        {
            var settings = controller.Store.Load();
            if (!settings.OptedIn || settings.EmergencyDisabled) return results;
            var profiles = settings.Profiles.Where(p => p.Enabled).ToDictionary(p => p.ExecutableKey, StringComparer.OrdinalIgnoreCase);
            if (profiles.Count == 0) return results;

            var windows = WindowInspector.EnumerateTopLevelWindows();
            var alive = windows.Select(w => w.ProcessId).ToHashSet();
            _done.RemoveWhere(pid => !alive.Contains(pid));

            foreach (var w in windows)
            {
                if (w.Executable is null || _done.Contains(w.ProcessId)) continue;
                if (!profiles.TryGetValue(w.Executable.Key, out var profile)) continue;
                int attempts = _attempts.GetValueOrDefault(w.ProcessId);
                if (attempts >= 3) continue;
                _attempts[w.ProcessId] = attempts + 1;

                var (r, _) = controller.Apply(w, profile.ScalePercent, allowVisualFallback: false, profile.PreferredBackend);
                if (r.Succeeded || r.Code is ScaleFailureCode.Excluded or ScaleFailureCode.Elevated or ScaleFailureCode.AccessDenied)
                    _done.Add(w.ProcessId);
                results.Add((w, r));
                Applied?.Invoke(w, r);
            }
        }
        finally { Volatile.Write(ref _busy, 0); }
        return results;
    }
}
