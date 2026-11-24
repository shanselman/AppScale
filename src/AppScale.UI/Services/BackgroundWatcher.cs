using AppScale.Core;
using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Targets;

namespace AppScale.UI.Services;

/// <summary>One auto-apply outcome, captured on the watcher thread.</summary>
public sealed record AutoApplyEvent(TargetInfo Target, ScaleResult Result, string? BackendId, int? EffectiveDpi, int? OsDpi);

/// <summary>
/// Runs Core's <see cref="ProfileWatcher"/> on a dedicated background loop with its own
/// <see cref="ScaleController"/> (ScaleController is not thread-safe, so the UI thread never shares it).
/// Every watcher call is serialized through a lock, including <see cref="ResetAll"/>.
/// </summary>
public sealed class BackgroundWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly ScaleController _controller;
    private readonly ProfileWatcher _watcher;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BackgroundWatcher(SettingsStore store)
    {
        // Native backends only: the watcher never applies visual magnification.
        _controller = new ScaleController(store, [new CooperativeBackend(), new InjectionBackend()]);
        _watcher = new ProfileWatcher(_controller);
        _watcher.Applied += OnApplied;
    }

    /// <summary>Raised on the watcher thread.</summary>
    public event Action<AutoApplyEvent>? Applied;

    public bool IsRunning => _cts is not null;
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(750);

    public void Start()
    {
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { lock (_gate) _watcher.Tick(); }
                catch { /* a failing scan must never kill the loop */ }
                try { await Task.Delay(Interval, cts.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    public void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(25)); } catch { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Resets every override the watcher applied.</summary>
    public IReadOnlyList<ScaleResult> ResetAll()
    {
        lock (_gate) return _controller.ResetAll();
    }

    private void OnApplied(TargetInfo target, ScaleResult result)
    {
        // Runs inside Tick, with the gate held.
        string? backend = null; int? eff = result.EffectiveDpi, os = target.WindowDpi;
        if (result.Succeeded)
        {
            backend = _controller.Applied.FirstOrDefault(a => a.Target.Hwnd == target.Hwnd).Backend?.Id;
            try
            {
                var st = _controller.GetStatus(target);
                eff = st.EffectiveDpi ?? eff;
                os = st.OsDpi ?? os;
            }
            catch { }
        }
        Applied?.Invoke(new AutoApplyEvent(target, result, backend, eff, os));
    }

    public void Dispose() => Stop();
}
