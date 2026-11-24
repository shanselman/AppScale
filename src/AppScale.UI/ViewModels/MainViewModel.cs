using System.Collections.ObjectModel;
using AppScale.Core;
using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Scaling;
using AppScale.Core.Targets;
using AppScale.UI.Preview;
using AppScale.UI.Services;

namespace AppScale.UI.ViewModels;

/// <summary>UI services the view model needs from the shell; replaced by stubs in smoke/self-test runs.</summary>
public interface IUiServices
{
    string? PickExecutable();
    string? PickExportPath();
    bool Confirm(string title, string message);
}

public sealed class MainViewModel : ObservableObject
{
    public static readonly int[] Presets = [75, 100, 125, 150, 200, 250];

    private readonly IUiServices _ui;
    private WindowRow? _selected;
    private int _scalePercent = 125;
    private bool _optedIn, _emergency, _visualFlag, _detoursFlag, _showVisualFallback, _restartRequired;
    private string _restartText = "—", _backendText = "", _capabilitiesText = "", _backendDetail = "", _launchPath = "", _lastResultText = "";
    private bool _lastResultIsError;

    private readonly bool _runWatcher;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private bool _injectionFlag, _launching;
    private string _watchingText = "Not watching", _injectConsentText = "";

    /// <param name="runWatcher">Run the background profile watcher while opted in (interactive tray and self-test only).</param>
    public MainViewModel(SettingsStore store, DwmPreviewHost previewHost, IUiServices ui, bool redact = false, bool runWatcher = false)
    {
        Store = store;
        PreviewHost = previewHost;
        Controller = new ScaleController(store, ScaleController.DefaultBackends(previewHost));
        Redact = redact;
        _ui = ui;
        _runWatcher = runWatcher;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Watcher = new BackgroundWatcher(store);
        Watcher.Applied += e => _dispatcher.BeginInvoke(() => OnAutoApplied(e));
        previewHost.Notice += msg => AddLog(msg, false);

        RefreshCommand = new RelayCommand(RefreshWindows);
        PresetCommand = new RelayCommand(p => { if (p is not null && int.TryParse(p.ToString(), out int v)) ScalePercent = v; });
        PreviewCommand = new RelayCommand(Preview, () => CanAct && _visualFlag);
        ApplyCommand = new RelayCommand(() => Apply(false), () => CanAct);
        ApplyVisualCommand = new RelayCommand(() => Apply(true), () => CanAct && _showVisualFallback && _visualFlag);
        ResetCommand = new RelayCommand(() => ResetSelected(), () => _selected is not null);
        ResetAllCommand = new RelayCommand(ResetAll);
        EmergencyCommand = new RelayCommand(EmergencyDisable);
        ClearEmergencyCommand = new RelayCommand(ClearEmergency, () => _emergency);
        EnableCommand = new RelayCommand(OptIn);
        OptOutCommand = new RelayCommand(OptOut, () => _optedIn);
        BrowseExeCommand = new RelayCommand(() => { if (_ui.PickExecutable() is { } p) LaunchPath = p; });
        LaunchCommand = new RelayCommand(() => Launch(), () => _optedIn && !_emergency && File.Exists(_launchPath));
        LaunchInjectCommand = new RelayCommand(() => _ = LaunchWithInjectorAsync(), () => _optedIn && !_emergency && !_launching && _injectionFlag && InjectorPath is not null && File.Exists(_launchPath));
        RememberCommand = new RelayCommand(() => Remember(), () => CanAct && _selected?.Target.Executable is not null);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => _selected is { IsExcluded: false, Target.Executable: not null });
        ApplyProfileCommand = new RelayCommand(p => ApplyProfile(p as ProfileRow), p => p is ProfileRow { Enabled: true } && _optedIn && !_emergency);
        DeleteProfileCommand = new RelayCommand(p => DeleteProfile(p as ProfileRow), p => p is ProfileRow);
        DeleteAllCommand = new RelayCommand(DeleteAll);
        ExportCommand = new RelayCommand(Export);
        ClosePreviewsCommand = new RelayCommand(() => PreviewHost.CloseAll());

        ReloadSettings();
    }

    public SettingsStore Store { get; }
    public ScaleController Controller { get; }
    public BackgroundWatcher Watcher { get; }
    public ObservableCollection<AppliedRow> AppliedRows { get; } = [];
    public RelayCommand LaunchInjectCommand { get; }
    public RelayCommand RememberCommand { get; }

    /// <summary>Raised on the UI thread for every background auto-apply (used by the tray balloon).</summary>
    public event Action<AppliedRow>? AutoApplied;

    // ---------- background watcher ----------

    public string WatchingText { get => _watchingText; private set => Set(ref _watchingText, value); }
    public bool IsWatching => Watcher.IsRunning;
    public int WatchedCount => Store.Load().Profiles.Count(p => p.Enabled);

    /// <summary>Starts or stops the watcher so it runs exactly while the user is opted in and not emergency-disabled.</summary>
    public void UpdateWatcher()
    {
        bool want = _runWatcher && _optedIn && !_emergency;
        if (want && !Watcher.IsRunning) Watcher.Start();
        else if (!want && Watcher.IsRunning) Watcher.Stop();
        int n = WatchedCount;
        WatchingText = Watcher.IsRunning
            ? $"Watching {n} app{(n == 1 ? "" : "s")}"
            : !_optedIn ? "Not watching (AppScale is off)" : _emergency ? "Not watching (emergency disable)" : "Not watching";
        OnPropertyChanged(nameof(IsWatching));
        OnPropertyChanged(nameof(WatchedCount));
    }

    private void OnAutoApplied(AutoApplyEvent e)
    {
        var row = new AppliedRow(DateTime.Now, "auto (on start)", e.Target.ExecutableName, e.Target.ProcessId,
            e.BackendId ?? "—", e.EffectiveDpi, e.OsDpi, e.Result.Succeeded, e.Result.Succeeded ? e.Result.Message : $"{e.Result.Code}: {e.Result.Message}");
        AddApplied(row);
        LogResult($"Auto-apply {e.Target.ExecutableName} (pid {e.Target.ProcessId}){(e.BackendId is { } b ? $" via {b}" : "")}", e.Result);
        AutoApplied?.Invoke(row);
    }

    private void AddApplied(AppliedRow row)
    {
        AppliedRows.Insert(0, row);
        while (AppliedRows.Count > 200) AppliedRows.RemoveAt(AppliedRows.Count - 1);
    }

    private void RecordApplied(string source, TargetInfo t, ScaleResult r, string? backendId)
    {
        int? eff = r.EffectiveDpi, os = t.WindowDpi;
        if (r.Succeeded)
            try { var st = Controller.GetStatus(t); eff = st.EffectiveDpi ?? eff; os = st.OsDpi ?? os; } catch { }
        AddApplied(new AppliedRow(DateTime.Now, source, t.ExecutableName, t.ProcessId, backendId ?? "—", eff, os, r.Succeeded,
            r.Succeeded ? r.Message : $"{r.Code}: {r.Message}"));
    }

    /// <summary>Stops the watcher and resets everything it applied (exit / opt-out / emergency).</summary>
    public void ShutdownWatcher()
    {
        Watcher.Stop();
        foreach (var r in Watcher.ResetAll()) LogResult("Reset (watcher)", r);
        UpdateWatcher();
    }
    public DwmPreviewHost PreviewHost { get; }
    public bool Redact { get; }

    public ObservableCollection<WindowRow> Windows { get; } = [];
    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public ObservableCollection<LogEntry> Log { get; } = [];
    public IReadOnlyList<int> PresetValues => Presets;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand PresetCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand ApplyVisualCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ResetAllCommand { get; }
    public RelayCommand EmergencyCommand { get; }
    public RelayCommand ClearEmergencyCommand { get; }
    public RelayCommand EnableCommand { get; }
    public RelayCommand OptOutCommand { get; }
    public RelayCommand BrowseExeCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand ApplyProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand DeleteAllCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ClosePreviewsCommand { get; }

    private bool CanAct => _selected is { IsExcluded: false } && _optedIn && !_emergency;

    // ---------- settings / consent ----------

    public bool OptedIn { get => _optedIn; private set { if (Set(ref _optedIn, value)) OnPropertyChanged(nameof(NeedsConsent)); } }
    public bool NeedsConsent => !_optedIn;
    public bool EmergencyDisabled { get => _emergency; private set => Set(ref _emergency, value); }
    public string SettingsPath => Store.Path;

    public bool VisualPreviewEnabled
    {
        get => _visualFlag;
        set
        {
            if (!Set(ref _visualFlag, value)) return;
            Store.Update(s => s with { Features = s.Features with { VisualPreview = value } });
            if (!value) PreviewHost.CloseAll();
            AddLog($"Feature flag VisualPreview = {value}.", false);
            UpdateStatus();
        }
    }

    public bool InjectionEnabled
    {
        get => _injectionFlag;
        set
        {
            if (!Set(ref _injectionFlag, value)) return;
            Store.Update(s => s with { Features = s.Features with { Injection = value } });
            AddLog($"Feature flag Injection = {value}{(value ? "" : " — the DPI adapter will not be loaded into any app; only cooperative apps can be scaled natively")}.", false);
            UpdateStatus();
        }
    }

    public string? InjectorPath => InjectionBackend.FindInjector();
    public string InjectorText => InjectorPath is { } p ? $"Injector: {p}" : "Injector not found (AppScale.Injector.exe) — inject / Launch with scale unavailable.";

    public bool ExperimentalDetoursEnabled
    {
        get => _detoursFlag;
        set
        {
            if (!Set(ref _detoursFlag, value)) return;
            Store.Update(s => s with { Features = s.Features with { ExperimentalDetours = value } });
            AddLog($"Feature flag ExperimentalDetours = {value} (affects only launching the owned Win32 harness).", false);
        }
    }

    public void ReloadSettings()
    {
        var s = Store.Load();
        OptedIn = s.OptedIn;
        EmergencyDisabled = s.EmergencyDisabled;
        Set(ref _visualFlag, s.Features.VisualPreview, nameof(VisualPreviewEnabled));
        Set(ref _detoursFlag, s.Features.ExperimentalDetours, nameof(ExperimentalDetoursEnabled));
        Set(ref _injectionFlag, s.Features.Injection, nameof(InjectionEnabled));
        Profiles.Clear();
        foreach (var p in s.Profiles) Profiles.Add(new ProfileRow(p, OnProfileEnabledChanged));
        UpdateWatcher();
    }

    private void OptIn()
    {
        Store.Update(s => s with { OptedIn = true });
        OptedIn = true;
        AddLog("Opted in. AppScale may now apply scale to windows you select, and auto-apply apps you choose to remember.", false);
        UpdateWatcher();
        RefreshWindows();
    }

    private void OptOut()
    {
        Watcher.Stop();
        foreach (var r in Watcher.ResetAll()) LogResult("Reset (opt-out, watcher)", r);
        foreach (var r in Controller.ResetAll()) LogResult("Reset (opt-out)", r);
        PreviewHost.CloseAll();
        Store.Update(s => s with { OptedIn = false });
        OptedIn = false;
        UpdateWatcher();
        AddLog("Opted out. Background watcher stopped and all overrides this session were reset; nothing can be applied until you enable AppScale again.", false);
    }

    private void EmergencyDisable()
    {
        PreviewHost.CloseAll();
        Watcher.Stop();
        foreach (var r in Watcher.ResetAll()) LogResult("Emergency reset (watcher)", r);
        var results = Controller.EmergencyDisable();
        foreach (var r in results) LogResult("Emergency reset", r);
        EmergencyDisabled = true;
        UpdateWatcher();
        AddLog($"EMERGENCY DISABLE: watcher stopped; {results.Count} reset(s) issued; previews closed; Apply is blocked until cleared in Settings.", true);
        UpdateStatus();
    }

    private void ClearEmergency()
    {
        Store.Update(s => s with { EmergencyDisabled = false });
        EmergencyDisabled = false;
        UpdateWatcher();
        AddLog("Emergency disable cleared.", false);
    }

    // ---------- window picker ----------

    public WindowRow? SelectedWindow
    {
        get => _selected;
        set
        {
            if (value is { IsExcluded: true }) value = null;
            if (!Set(ref _selected, value)) return;
            ShowVisualFallback = false;
            UpdateStatus();
        }
    }

    public void RefreshWindows()
    {
        nint? keep = _selected?.Target.Hwnd;
        Windows.Clear();
        foreach (var t in WindowInspector.EnumerateTopLevelWindows()
                     .OrderByDescending(t => t.IsCooperative)
                     .ThenBy(t => t.ExecutableName, StringComparer.OrdinalIgnoreCase))
            Windows.Add(new WindowRow(t, Redact));
        SelectedWindow = Windows.FirstOrDefault(w => w.Target.Hwnd == keep);
        WindowCountText = $"{Windows.Count} windows ({Windows.Count(w => w.Target.IsCooperative)} cooperative, {Windows.Count(w => w.IsExcluded)} excluded)";
        OnPropertyChanged(nameof(WindowCountText));
    }

    public string WindowCountText { get; private set; } = "";

    // ---------- scale ----------

    public int ScalePercent
    {
        get => _scalePercent;
        set
        {
            int v = Math.Clamp((int)(Math.Round(value / 5.0) * 5), ScaleMath.MinPercent, ScaleMath.MaxPercent);
            if (Set(ref _scalePercent, v)) UpdateStatus();
        }
    }

    public int RequestedDpi => ScaleMath.DpiForPercent(_scalePercent);
    public string AbsoluteText => $"{_scalePercent}%  =  {RequestedDpi} DPI absolute (96 × {_scalePercent / 100.0:0.##})";

    private int ActualDpi => _selected is null ? 0 : _selected.Target.WindowDpi > 0 ? _selected.Target.WindowDpi : _selected.Target.MonitorDpi;
    public double RelativeZoom => ActualDpi > 0 ? ScaleMath.RelativeZoom(RequestedDpi, ActualDpi) : 0;
    public string RelativeText => ActualDpi > 0
        ? $"×{RelativeZoom:0.00} relative visual zoom  ({RequestedDpi} requested ÷ {ActualDpi} actual DPI)"
        : "Relative visual zoom: select a window (requested DPI ÷ the window's actual DPI)";

    // ---------- status ----------

    public string TargetText => _selected is null ? "No window selected" : $"{_selected.ExecutableName} — \"{_selected.Title}\"  ({_selected.Hwnd}, pid {_selected.Target.ProcessId})";
    public string TargetPath => _selected?.ExecutablePath ?? "";
    public string MonitorText => _selected is null ? "—" : $"{_selected.Target.MonitorDpi} DPI ({ScaleMath.PercentForDpi(_selected.Target.MonitorDpi):0}%) monitor · window reports {_selected.Target.WindowDpi} DPI · {_selected.Awareness}";
    public string RequestedText => $"{_scalePercent}% → {RequestedDpi} DPI";
    public bool CompatibilityUnknown => _selected is { IsExcluded: false, Target.IsCooperative: false };
    public string BackendText { get => _backendText; private set => Set(ref _backendText, value); }
    public string CapabilitiesText { get => _capabilitiesText; private set => Set(ref _capabilitiesText, value); }
    public string BackendDetail { get => _backendDetail; private set => Set(ref _backendDetail, value); }
    public bool RestartRequired { get => _restartRequired; private set => Set(ref _restartRequired, value); }
    public string RestartText { get => _restartText; private set => Set(ref _restartText, value); }
    public bool ShowVisualFallback { get => _showVisualFallback; private set => Set(ref _showVisualFallback, value); }
    public string LastResultText { get => _lastResultText; private set => Set(ref _lastResultText, value); }
    public bool LastResultIsError { get => _lastResultIsError; private set => Set(ref _lastResultIsError, value); }
    public string InjectConsentText { get => _injectConsentText; private set { if (Set(ref _injectConsentText, value)) OnPropertyChanged(nameof(HasInjectConsent)); } }
    public bool HasInjectConsent => _injectConsentText.Length > 0;
    public string RememberText => _selected is null ? "Pick an app first." :
        $"Remember: auto-apply {_scalePercent}% to {_selected.ExecutableName} every time it starts.";

    public static string DescribeCaps(BackendCapabilities c) =>
        $"Native rendering {Mark(c, BackendCapabilities.NativeRendering)} · Live change {Mark(c, BackendCapabilities.LiveChange)} · " +
        $"Input {Mark(c, BackendCapabilities.InputSupported)} · Visual-only {Mark(c, BackendCapabilities.VisualOnly)}" +
        (c.HasFlag(BackendCapabilities.RequiresRestart) ? " · Requires restart" : "") +
        (c.HasFlag(BackendCapabilities.Experimental) ? " · EXPERIMENTAL" : "");

    private static string Mark(BackendCapabilities c, BackendCapabilities f) => c.HasFlag(f) ? "✓" : "✗";

    public void UpdateStatus()
    {
        foreach (var n in new[] { nameof(RequestedDpi), nameof(AbsoluteText), nameof(RelativeZoom), nameof(RelativeText), nameof(TargetText),
                     nameof(TargetPath), nameof(MonitorText), nameof(RequestedText), nameof(CompatibilityUnknown), nameof(RememberText) })
            OnPropertyChanged(n);
        InjectConsentText = "";

        if (_selected is null) { BackendText = "—"; CapabilitiesText = ""; BackendDetail = ""; return; }
        var t = _selected.Target;
        var applied = Controller.Applied.FirstOrDefault(a => a.Target.Hwnd == t.Hwnd);
        if (applied.Backend is not null)
        {
            var st = applied.Backend.GetStatus(t);
            BackendText = $"{applied.Backend.Id} — {applied.Backend.DisplayName} (active)";
            CapabilitiesText = DescribeCaps(applied.Backend.Capabilities);
            BackendDetail = $"{st.Detail}" + (st.EffectiveDpi is int e ? $"; effective {e} DPI" : "") + (st.OsDpi is int o ? $", OS {o} DPI" : "");
            return;
        }
        if (_selected.IsExcluded) { BackendText = "None (excluded)"; CapabilitiesText = ""; BackendDetail = _selected.ExclusionReason; return; }
        var choice = Controller.Choose(t, allowVisualFallback: false);
        if (choice.Backend is not null)
        {
            BackendText = $"{choice.Backend.Id} — {choice.Backend.DisplayName}";
            CapabilitiesText = DescribeCaps(choice.Probe?.Capabilities ?? choice.Backend.Capabilities);
            BackendDetail = choice.Explanation;
            if (choice.Backend.Id == InjectionBackend.BackendId)
                InjectConsentText = $"Apply now will load AppScale's DPI adapter into {_selected.ExecutableName} (this process only, until it exits). The app is told its monitor is {RequestedDpi} DPI and re-renders natively.";
        }
        else
        {
            BackendText = "No native backend";
            CapabilitiesText = _visualFlag ? "Visual magnification (bitmap, read-only) is available only if you explicitly choose it." : "Visual magnification is disabled by feature flag.";
            BackendDetail = choice.Explanation;
        }
    }

    // ---------- actions ----------

    private TargetInfo? FreshTarget()
    {
        if (_selected is null) return null;
        var fresh = WindowInspector.Inspect(_selected.Target.Hwnd);
        if (fresh is null)
        {
            LogResult("Apply", ScaleResult.Fail(ScaleFailureCode.TargetGone, "The selected window no longer exists. Refresh the list."));
            return null;
        }
        return fresh;
    }

    public void Preview()
    {
        var t = FreshTarget();
        if (t is null) return;
        var pre = Controller.CheckPreconditions(t, _scalePercent);
        if (!pre.Succeeded) { LogResult("Preview", pre); return; }
        int actual = t.WindowDpi > 0 ? t.WindowDpi : t.MonitorDpi;
        if (actual <= 0) { LogResult("Preview", ScaleResult.Fail(ScaleFailureCode.NotSupported, "Unknown window DPI.")); return; }
        if (t.IsMinimized) { LogResult("Preview", ScaleResult.Fail(ScaleFailureCode.NotSupported, "Window is minimized; DWM provides no live thumbnail content. Restore it first.")); return; }
        double zoom = ScaleMath.RelativeZoom(RequestedDpi, actual);
        bool ok = PreviewHost.Show(t, zoom, out var err);
        LogResult("Preview", ok
            ? ScaleResult.Ok($"{PreviewWindow.Label}: ×{zoom:0.00} ({RequestedDpi} ÷ {actual} DPI). Nothing was applied to the target.")
            : ScaleResult.Fail(ScaleFailureCode.NotSupported, err));
    }

    public ScaleResult? Apply(bool allowVisualFallback)
    {
        var t = FreshTarget();
        if (t is null) return null;
        var (result, choice) = Controller.Apply(t, _scalePercent, allowVisualFallback);
        string via = choice?.Backend is { } b ? $" via {b.Id}" : "";
        LogResult(allowVisualFallback ? $"Apply {_scalePercent}% (visual magnification explicitly chosen){via}" : $"Apply {_scalePercent}%{via}", result);
        if (!result.Succeeded && choice is not null && result.Code == ScaleFailureCode.NoBackend)
            foreach (var (id, probe) in choice.Considered)
                AddLog($"    considered {id}: {(probe.Supported ? "supported" : $"{probe.Code} — {probe.Detail}")}", !probe.Supported);
        ShowVisualFallback = !allowVisualFallback && result.Code == ScaleFailureCode.NoBackend;
        RecordApplied(allowVisualFallback ? "manual (visual)" : "manual", t, result, choice?.Backend?.Id);
        if (result.Succeeded)
        {
            RestartRequired = result.RestartRequired;
            RestartText = result.RestartRequired ? "Restart required" : "Applied live — no restart needed";
        }
        UpdateStatus();
        return result;
    }

    public ScaleResult? ResetSelected()
    {
        if (_selected is null) return null;
        var t = WindowInspector.Inspect(_selected.Target.Hwnd) ?? _selected.Target;
        PreviewHost.Close(t.Hwnd);
        var r = Controller.Reset(t);
        LogResult("Reset", r);
        RestartRequired = false; RestartText = "—";
        UpdateStatus();
        return r;
    }

    public void ResetAll()
    {
        var results = Watcher.ResetAll().Concat(Controller.ResetAll()).ToList();
        PreviewHost.CloseAll();
        foreach (var r in results) LogResult("Reset all", r);
        if (results.Count == 0) AddLog("Reset all: nothing was applied in this session; previews closed.", false);
        UpdateStatus();
    }

    public string LaunchPath
    {
        get => _launchPath;
        set { if (Set(ref _launchPath, value ?? "")) OnPropertyChanged(nameof(LaunchName)); }
    }

    public string LaunchName => string.IsNullOrEmpty(_launchPath) ? "(no executable chosen)" : Path.GetFileName(_launchPath);

    public int? LastLaunchedPid { get; private set; }

    public ScaleResult Launch()
    {
        var s = Store.Load();
        ScaleResult r;
        if (!s.OptedIn) r = ScaleResult.Fail(ScaleFailureCode.OptInRequired, "Enable AppScale first.");
        else if (s.EmergencyDisabled) r = ScaleResult.Fail(ScaleFailureCode.EmergencyDisabled, "Emergency disable is on.");
        else
        {
            bool detours = s.Features.ExperimentalDetours && Path.GetFileName(_launchPath).StartsWith("Win32Harness", StringComparison.OrdinalIgnoreCase);
            r = CooperativeLauncher.Launch(_launchPath, _scalePercent, experimentalDetours: detours, onStarted: p => LastLaunchedPid = p.Id);
            if (detours) AddLog("Experimental Detours adapter requested for the owned Win32 harness (research only).", false);
        }
        LogResult($"Launch {LaunchName} at {_scalePercent}%", r);
        if (r.Succeeded)
        {
            RestartRequired = true;
            RestartText = $"Restart required — launched at {r.EffectiveDpi} DPI (only cooperative apps honour APPSCALE_DPI)";
        }
        return r;
    }

    // ---------- launch with injector ----------

    public bool IsLaunching { get => _launching; private set => Set(ref _launching, value); }

    /// <summary>For apps that read DPI only at startup: start them under the injector so the adapter is in place before the first window.</summary>
    public async Task<ScaleResult> LaunchWithInjectorAsync()
    {
        var s = Store.Load();
        string name = LaunchName;
        int dpi = RequestedDpi;
        ScaleResult r;
        if (!s.OptedIn) r = ScaleResult.Fail(ScaleFailureCode.OptInRequired, "Enable AppScale first.");
        else if (s.EmergencyDisabled) r = ScaleResult.Fail(ScaleFailureCode.EmergencyDisabled, "Emergency disable is on.");
        else if (!s.Features.Injection) r = ScaleResult.Fail(ScaleFailureCode.FeatureDisabled, "Feature flag Injection is off.");
        else if (InjectorPath is not { } injector) r = ScaleResult.Fail(ScaleFailureCode.NotSupported, "AppScale.Injector.exe was not found.");
        else if (!File.Exists(_launchPath)) r = ScaleResult.Fail(ScaleFailureCode.TargetGone, "Executable not found.");
        else if (IsSystemPath(_launchPath)) r = ScaleResult.Fail(ScaleFailureCode.Excluded, "Windows system executables are excluded.");
        else
        {
            IsLaunching = true;
            LastLaunchedPid = null;
            try { r = await Task.Run(() => RunInjectorLaunch(injector, _launchPath, dpi)); }
            finally { IsLaunching = false; }
        }
        LogResult($"Launch with scale {name} at {dpi} DPI (inject)", r);
        AddApplied(new AppliedRow(DateTime.Now, "launch", name, LastLaunchedPid ?? 0, "inject", r.Succeeded ? r.EffectiveDpi ?? dpi : null, null, r.Succeeded,
            r.Succeeded ? r.Message : $"{r.Code}: {r.Message}"));
        if (r.Succeeded) { RestartRequired = false; RestartText = $"Launched with adapter at {dpi} DPI"; }
        return r;
    }

    private ScaleResult RunInjectorLaunch(string injector, string exe, int dpi)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(injector, $"--launch \"{exe}\" --dpi {dpi}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return ScaleResult.Fail(ScaleFailureCode.Timeout, "Injector --launch timed out (30 s)."); }
            string output = (outTask.Result + errTask.Result).Trim();
            var m = System.Text.RegularExpressions.Regex.Match(output, @"\bpid""?[\s=:]+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) LastLaunchedPid = int.Parse(m.Groups[1].Value);
            string tail = output.Length > 300 ? "…" + output[^300..] : output;
            return p.ExitCode == 0
                ? ScaleResult.Ok($"Started {Path.GetFileName(exe)} with the DPI adapter at {dpi} DPI. {tail}".Trim(), dpi)
                : ScaleResult.Fail(ScaleFailureCode.LaunchFailed, $"Injector exited {p.ExitCode}: {tail}");
        }
        catch (Exception ex) { return ScaleResult.Fail(ScaleFailureCode.LaunchFailed, $"Could not run the injector: {ex.Message}"); }
    }

    private static bool IsSystemPath(string path)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return win.Length > 0 && Path.GetFullPath(path).StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- profiles ----------

    /// <summary>Remember = per-app consent: saves an ENABLED profile that the background watcher applies whenever the app starts.</summary>
    public bool Remember()
    {
        if (_selected?.Target.Executable is not { } exe) return false;
        var t = _selected.Target;
        string how = t.IsCooperative
            ? $"AppScale will ask {exe.FileName} to render at {RequestedDpi} DPI ({_scalePercent}%) each time it starts (it supports the AppScale cooperative protocol)."
            : $"AppScale will load its DPI adapter into {exe.FileName} each time it starts, and tell it that its monitor is {RequestedDpi} DPI ({_scalePercent}%) so it re-renders natively.";
        if (!_ui.Confirm("Remember this app",
                how + "\n\nExecutable: " + exe.FullPath +
                "\n\nOnly this executable is affected, only while AppScale is running and enabled. Nothing system-wide changes. " +
                "You can turn this off any time in Profiles, or use Emergency disable."))
        {
            AddLog($"Remember {exe.FileName}: cancelled.", false);
            return false;
        }
        var s = Store.UpsertProfile(new ScaleProfile
        {
            ExecutableKey = exe.Key, ExecutablePath = exe.FullPath, ScalePercent = _scalePercent, PreferredBackend = null, Enabled = true,
        });
        ReloadProfiles(s);
        UpdateWatcher();
        AddLog($"Remembered {exe.FileName} at {_scalePercent}% ({RequestedDpi} DPI): auto-apply on start is ON.", false);
        return true;
    }

    private void SaveProfile()
    {
        if (_selected?.Target.Executable is not { } exe) return;
        var applied = Controller.Applied.FirstOrDefault(a => a.Target.Hwnd == _selected.Target.Hwnd);
        string? backend = applied.Backend?.Id == "visual-preview" ? "visual-preview" : null;
        var existing = Store.Load().Profiles.FirstOrDefault(p => p.ExecutableKey == exe.Key);
        var s = Store.UpsertProfile(new ScaleProfile
        {
            ExecutableKey = exe.Key, ExecutablePath = exe.FullPath, ScalePercent = _scalePercent,
            PreferredBackend = backend, Enabled = existing?.Enabled ?? false,
        });
        ReloadProfiles(s);
        AddLog($"Saved profile for {exe.FileName}: {_scalePercent}%{(backend is null ? "" : " (visual magnification)")}. {(existing?.Enabled == true ? "Auto-apply stays ON." : "Saved disabled — use Remember or tick Enabled to auto-apply on start.")}", false);
        UpdateWatcher();
    }

    private void ReloadProfiles(AppScaleSettings s)
    {
        Profiles.Clear();
        foreach (var p in s.Profiles) Profiles.Add(new ProfileRow(p, OnProfileEnabledChanged));
    }

    private bool OnProfileEnabledChanged(ProfileRow row, bool enabled)
    {
        if (enabled && row.Profile.PreferredBackend != "visual-preview" &&
            !_ui.Confirm("Auto-apply on start",
                $"AppScale will apply {row.ScalePercent}% to {row.ExecutableName} each time it starts. If the app is not cooperative, AppScale will load its DPI adapter into it.\n\n{row.ExecutablePath}"))
        {
            AddLog($"Profile {row.ExecutableName}: enabling cancelled.", false);
            return false;
        }
        Store.Update(s => s with { Profiles = s.Profiles.Select(p => p.ExecutableKey == row.Profile.ExecutableKey ? p with { Enabled = enabled } : p).ToList() });
        AddLog($"Profile {row.ExecutableName} {(enabled ? "enabled — auto-apply on start" : "disabled")}.", false);
        UpdateWatcher();
        return true;
    }

    private void ApplyProfile(ProfileRow? row)
    {
        if (row is null) return;
        if (!row.Enabled) { AddLog($"Profile {row.ExecutableName} is disabled; enable it first.", true); return; }
        var target = WindowInspector.EnumerateTopLevelWindows()
            .Where(t => t.Executable?.Key == row.Profile.ExecutableKey)
            .OrderByDescending(t => t.IsCooperative).FirstOrDefault();
        if (target is null) { LogResult($"Apply profile {row.ExecutableName}", ScaleResult.Fail(ScaleFailureCode.TargetGone, "No open window for this executable.")); return; }
        // Native backends only (cooperative / inject); bitmap magnification only if the profile was explicitly saved as visual.
        bool visual = row.Profile.PreferredBackend == "visual-preview";
        var (r, choice) = Controller.Apply(target, row.ScalePercent, allowVisualFallback: visual, row.Profile.PreferredBackend);
        LogResult($"Apply profile {row.ExecutableName} {row.ScalePercent}%{(choice?.Backend is { } b ? $" via {b.Id}" : "")}", r);
        RecordApplied("profile", target, r, choice?.Backend?.Id);
        RefreshWindows();
    }

    private void DeleteProfile(ProfileRow? row)
    {
        if (row is null) return;
        Store.DeleteProfile(row.Profile.ExecutableKey);
        ReloadProfiles(Store.Load());
        AddLog($"Deleted profile {row.ExecutableName}.", false);
        UpdateWatcher();
    }

    private void DeleteAll()
    {
        if (!_ui.Confirm("Delete all AppScale data",
                "Delete ALL AppScale settings and profiles from disk?\n\nThis also turns AppScale off (opt-in is reset). Active overrides are reset first.\n\n" + Store.Path))
            return;
        Watcher.Stop();
        foreach (var r in Watcher.ResetAll()) LogResult("Reset (delete all, watcher)", r);
        foreach (var r in Controller.ResetAll()) LogResult("Reset (delete all)", r);
        PreviewHost.CloseAll();
        Store.DeleteAll();
        ReloadSettings();
        AddLog($"Deleted all settings and profiles ({Store.Path}).", false);
    }

    private void Export()
    {
        if (_ui.PickExportPath() is not { } path) return;
        try
        {
            Store.Export(path);
            AddLog($"Exported settings and {Profiles.Count} profile(s) to {path}.", false);
        }
        catch (Exception ex) { AddLog($"Export failed: {ex.Message}", true); }
    }

    // ---------- log ----------

    public void LogResult(string action, ScaleResult r)
    {
        LastResultText = $"{action}: {r}";
        LastResultIsError = !r.Succeeded;
        AddLog($"{action}: {r}", !r.Succeeded);
    }

    public void AddLog(string text, bool isError)
    {
        Log.Insert(0, new LogEntry(DateTime.Now, text, isError));
        while (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }
}
