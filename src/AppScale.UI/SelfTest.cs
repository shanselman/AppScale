using System.Diagnostics;
using System.IO;
using System.Windows;
using AppScale.Core.Backends;
using AppScale.Core.Targets;
using AppScale.UI.Interop;
using AppScale.UI.Preview;
using AppScale.UI.ViewModels;

namespace AppScale.UI;

/// <summary>
/// Hidden integration test (<c>--selftest-apply &lt;harness.exe&gt;</c>). Drives the real view-model code
/// paths against owned test executables only: a cooperative harness and a non-cooperative dummy
/// window hosted by a second AppScale.UI process. Uses an isolated APPSCALE_HOME.
/// </summary>
internal sealed class SelfTest(App app, string harnessPath, Action<string> log)
{
    private readonly List<Process> _owned = [];
    private int _failures;
    private string? _tempHome;

    private void Check(bool ok, string what)
    {
        log($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) _failures++;
    }

    public async Task RunAsync()
    {
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(240) };
        watchdog.Tick += (_, _) => { log("FAIL  watchdog timeout"); Cleanup(); app.Shutdown(2); };
        watchdog.Start();
        try
        {
            await RunCore();
        }
        catch (Exception ex)
        {
            Check(false, $"exception: {ex}");
        }
        Cleanup();
        log(_failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({_failures} failure(s))");
        app.Shutdown(_failures == 0 ? 0 : 1);
    }

    private async Task RunCore()
    {
        harnessPath = Path.GetFullPath(harnessPath);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPSCALE_HOME")))
        {
            _tempHome = Path.Combine(AppContext.BaseDirectory, "selftest-home-" + Guid.NewGuid().ToString("N")[..8]);
            Environment.SetEnvironmentVariable("APPSCALE_HOME", _tempHome);
        }
        var w = app.CreateMainWindow(redact: true, allowClose: true, runWatcher: true);
        w.AutoConfirm = true;
        var vm = w.ViewModel;
        log($"settings: {vm.SettingsPath}");
        Check(!vm.OptedIn && vm.NeedsConsent, "fresh settings start opted-out (consent panel shown)");
        w.ShowActivated = false;
        w.Show();

        // ---- cooperative harness ----
        var harness = StartOwned(harnessPath, null);
        var target = await WaitForWindow(harness.Id, cooperative: true);
        Check(target is not null, $"cooperative harness window found (pid {harness.Id})");
        if (target is null) return;
        vm.RefreshWindows();
        var row = vm.Windows.FirstOrDefault(r => r.Target.Hwnd == target.Hwnd);
        Check(row is { IsExcluded: false, Target.IsCooperative: true }, "harness listed in picker as cooperative and not excluded");
        vm.SelectedWindow = row;
        // Pick scales that differ from the OS DPI so an override is observable.
        int p1 = target.WindowDpi == 192 ? 150 : 200, d1 = p1 == 200 ? 192 : 144;
        int p2 = target.WindowDpi == 120 ? 175 : 125, d2 = p2 == 125 ? 120 : 168;
        vm.ScalePercent = p1;
        Check(vm.RequestedDpi == d1, $"{p1}% → absolute {d1} DPI; relative zoom ×{vm.RelativeZoom:0.00} vs actual {target.WindowDpi} DPI");

        var r = vm.Apply(false);
        Check(r is { Succeeded: false, Code: ScaleFailureCode.OptInRequired }, $"Apply before opt-in refused: {r}");

        vm.EnableCommand.Execute(null);
        Check(vm.OptedIn && vm.Store.Load().OptedIn, "Enable persists OptedIn=true");
        vm.SelectedWindow = vm.Windows.FirstOrDefault(x => x.Target.Hwnd == target.Hwnd);
        vm.ScalePercent = p1;

        r = vm.Apply(false);
        Check(r is { Succeeded: true } && r.EffectiveDpi == d1, $"Apply {p1}% native: {r}");
        var st = new CooperativeBackend().GetStatus(target);
        Check(st.Active && st.EffectiveDpi == d1,  $"GetStatus after apply: effective={st.EffectiveDpi} os={st.OsDpi} '{st.Detail}'");
        Check(vm.BackendText.StartsWith("cooperative"), $"status panel backend: {vm.BackendText} | {vm.CapabilitiesText}");

        vm.Preview();
        await Task.Delay(800);
        if (vm.PreviewHost.Windows.TryGetValue(target.Hwnd, out var pw))
        {
            var src = pw.SourceSize;
            var d = pw.DestinationRect;
            Check(pw.IsThumbnailRegistered, $"preview DWM thumbnail registered (zoom requested ×{pw.RequestedZoom:0.00}, shown ×{pw.ShownZoom:0.00})");
            Check(Math.Abs(d.Width - src.cx * pw.ShownZoom) <= 2 && Math.Abs(d.Height - src.cy * pw.ShownZoom) <= 2,
                $"preview destination {d.Width}×{d.Height}px = source {src.cx}×{src.cy}px × {pw.ShownZoom:0.000} (preview DPI {Native.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(pw).Handle)})");
            Check(pw.Title.Contains("visual magnification") && PreviewWindow.Label.Contains("not native rendering"), $"preview labelled: \"{pw.Title}\"");
        }
        else Check(false, "preview window opened");
        vm.ClosePreviewsCommand.Execute(null);
        Check(!vm.PreviewHost.IsShowing(target.Hwnd), "preview closed");

        r = vm.ResetSelected();
        st = new CooperativeBackend().GetStatus(target);
        Check(r is { Succeeded: true } && !st.Active && st.EffectiveDpi == st.OsDpi, $"Reset: {r}; status effective={st.EffectiveDpi} os={st.OsDpi}");

        // ---- emergency disable (Core resets every cooperative window, so only run when the only one is ours) ----
        var otherCoop = WindowInspector.EnumerateTopLevelWindows(includeUntitled: true).Where(t => t.IsCooperative && t.ProcessId != harness.Id).ToList();
        if (otherCoop.Count > 0)
            log($"SKIP  emergency-disable test: {otherCoop.Count} other cooperative window(s) open (not ours to reset)");
        else
        {
        vm.ScalePercent = p2;
        r = vm.Apply(false);
        Check(r is { Succeeded: true } && r.EffectiveDpi == d2, $"Apply {p2}%: {r}");
        vm.EmergencyCommand.Execute(null);
        st = new CooperativeBackend().GetStatus(target);
        Check(vm.EmergencyDisabled && !st.Active, $"Emergency disable reset override (effective={st.EffectiveDpi} os={st.OsDpi})");
        r = vm.Apply(false);
        Check(r is { Code: ScaleFailureCode.EmergencyDisabled }, $"Apply blocked while emergency disabled: {r}");
        vm.ClearEmergencyCommand.Execute(null);
        Check(!vm.Store.Load().EmergencyDisabled, "Emergency disable cleared");
        }

        // ---- restart-required launch path ----
        vm.LaunchPath = harnessPath;
        vm.ScalePercent = 125;
        r = vm.Launch();
        Check(r is { Succeeded: true, RestartRequired: true, EffectiveDpi: 120 } && vm.RestartRequired, $"Launch at 125%: {r} | indicator: {vm.RestartText}");
        if (vm.LastLaunchedPid is int lp)
        {
            _owned.Add(Process.GetProcessById(lp));
            var launched = await WaitForWindow(lp, cooperative: true);
            var ls = launched is null ? null : new CooperativeBackend().GetStatus(launched);
            Check(ls is { EffectiveDpi: 120 }, $"launched harness reports effective {ls?.EffectiveDpi} DPI (OS {ls?.OsDpi})");
        }

        // ---- profiles ----
        vm.SelectedWindow = vm.Windows.FirstOrDefault(x => x.Target.Hwnd == target.Hwnd);
        vm.ScalePercent = 175;
        vm.SaveProfileCommand.Execute(null);
        var prof = vm.Profiles.FirstOrDefault(p => p.ExecutablePath.Equals(target.Executable!.FullPath, StringComparison.OrdinalIgnoreCase));
        Check(prof is { Enabled: false, ScalePercent: 175 }, "Save profile stores it disabled by default");
        Check(!vm.ApplyProfileCommand.CanExecute(prof), "disabled profile cannot be applied");
        Check(vm.IsWatching && vm.WatchingText == "Watching 0 apps", $"watcher running while opted in: '{vm.WatchingText}'");
        w.AutoConfirm = false;
        prof!.Enabled = true;
        Check(!prof.Enabled && !vm.Store.Load().Profiles.Single().Enabled, "enabling auto-apply requires confirmation (declined → stays off)");
        vm.DeleteProfileCommand.Execute(prof);
        Check(vm.Profiles.Count == 0 && vm.Store.Load().Profiles.Count == 0, "profile deleted");
        w.AutoConfirm = true;

        // ---- Remember + background watcher (cooperative path) ----
        vm.SelectedWindow = vm.Windows.FirstOrDefault(x => x.Target.Hwnd == target.Hwnd);
        vm.ScalePercent = p1;
        Check(vm.RememberCommand.CanExecute(null) && vm.Remember(), "Remember (confirmed) saves the app");
        prof = vm.Profiles.FirstOrDefault();
        Check(prof is { Enabled: true } && prof.ScalePercent == p1 && prof.Profile.PreferredBackend is null, $"Remember saved an ENABLED profile at {p1}% (native only)");
        Check(vm.WatchingText == "Watching 1 app", $"status shows '{vm.WatchingText}'");
        var fresh = StartOwned(harnessPath, null);
        var auto = await WaitForApplied(vm, fresh.Id);
        Check(auto is { Succeeded: true, Backend: "cooperative", Source: "auto (on start)" } && auto.EffectiveDpi == d1,
            $"new {Path.GetFileName(harnessPath)} instance auto-applied on start: {Describe(auto)}");
        var ft = WindowInspector.FindByProcess(fresh.Id).FirstOrDefault(x => x.IsCooperative);
        var fs = ft is null ? null : new CooperativeBackend().GetStatus(ft);
        Check(fs is { Active: true } && fs.EffectiveDpi == d1, $"harness confirms effective {fs?.EffectiveDpi} DPI (OS {fs?.OsDpi})");
        vm.DeleteProfileCommand.Execute(prof);
        Check(vm.WatchingText == "Watching 0 apps", $"after delete: '{vm.WatchingText}'");

        // ---- Remember + background watcher (injection path) ----
        if (vm.InjectorPath is not { } injector)
            log("SKIP  inject path: AppScale.Injector.exe not found (APPSCALE_INJECTOR / next to AppScale.UI.exe / artifacts\\bin\\AppScale.Injector\\release) — lane-native has not built it yet");
        else if (!vm.InjectionEnabled)
            log("SKIP  inject path: feature flag Injection is off");
        else
        {
            log($"injector: {injector}");
            var nc = StartOwned(harnessPath, "--no-cooperative");
            var nct = await WaitForWindow(nc.Id, cooperative: false);
            Check(nct is not null, $"non-cooperative harness window found (pid {nc.Id})");
            if (nct is not null)
            {
                vm.RefreshWindows();
                vm.SelectedWindow = vm.Windows.FirstOrDefault(x => x.Target.Hwnd == nct.Hwnd);
                vm.ScalePercent = p1;
                Check(vm.BackendText.StartsWith("inject") && vm.HasInjectConsent, $"backend selection picks inject: {vm.BackendText} | consent: {vm.InjectConsentText}");
                Check(vm.Remember(), "Remember non-cooperative app (consent: adapter loaded each time it starts)");
                var a1 = await WaitForApplied(vm, nc.Id);
                Check(a1 is { Succeeded: true, Backend: "inject" } && a1.EffectiveDpi == d1, $"running instance auto-applied: {Describe(a1)}");
                var nc2 = StartOwned(harnessPath, "--no-cooperative");
                var a2 = await WaitForApplied(vm, nc2.Id);
                Check(a2 is { Succeeded: true, Backend: "inject", Source: "auto (on start)" } && a2.EffectiveDpi == d1 && a2.OsDpi != d1,
                    $"newly started --no-cooperative instance auto-applied via inject: {Describe(a2)}");
                var it = WindowInspector.FindByProcess(nc2.Id).FirstOrDefault(x => x.IsCooperative);
                var ist = it is null ? null : new CooperativeBackend().GetStatus(it);
                Check(ist is { Active: true } && ist.EffectiveDpi == d1, $"injected adapter reports effective {ist?.EffectiveDpi} DPI vs OS {ist?.OsDpi}");
                foreach (var res in vm.Watcher.ResetAll()) log($"      watcher reset: {res}");
                ist = it is null ? null : new CooperativeBackend().GetStatus(it);
                Check(ist is { Active: false }, $"reset restores OS DPI (effective {ist?.EffectiveDpi}, OS {ist?.OsDpi})");
                if (vm.Profiles.FirstOrDefault() is { } ip) vm.DeleteProfileCommand.Execute(ip);

                // Launch with scale (injector --launch).
                vm.LaunchPath = harnessPath;
                vm.ScalePercent = p2;
                var lr = await vm.LaunchWithInjectorAsync();
                Check(lr.Succeeded, $"Launch with scale {p2}% via injector: {lr}");
                Check(vm.LastLaunchedPid is int, $"injector reported launched pid {vm.LastLaunchedPid}");
                if (vm.LastLaunchedPid is int ilp) try { _owned.Add(Process.GetProcessById(ilp)); } catch { }
            }
        }

        // ---- non-cooperative owned target: explicit visual fallback ----
        // The dummy is a WPF PMv2 window; with injection on it would be injectable, so disable it to exercise NoBackend.
        bool injectWas = vm.InjectionEnabled;
        vm.InjectionEnabled = false;
        var dummy = StartOwned(Environment.ProcessPath!, "--dummy-target");
        var dt = await WaitForWindow(dummy.Id, cooperative: false);
        Check(dt is not null, "non-cooperative dummy target found");
        if (dt is null) return;
        vm.RefreshWindows();
        vm.SelectedWindow = vm.Windows.FirstOrDefault(x => x.Target.Hwnd == dt.Hwnd);
        Check(vm.CompatibilityUnknown, "Compatibility unknown warning shown for non-cooperative target");
        vm.ScalePercent = p1; // non-unity zoom so the magnification is observable
        r = vm.Apply(false);
        Check(r is { Succeeded: false, Code: ScaleFailureCode.NoBackend } && vm.ShowVisualFallback && !vm.PreviewHost.IsShowing(dt.Hwnd),
            $"native Apply fails with NoBackend, no silent bitmap fallback, explicit button offered: {r}");
        r = vm.Apply(true);
        Check(r is { Succeeded: true } && vm.PreviewHost.IsShowing(dt.Hwnd), $"explicit visual magnification: {r}");
        await Task.Delay(600);
        Native.ShowWindow(dt.Hwnd, 6 /* SW_MINIMIZE */);
        await Task.Delay(1200);
        Check(vm.PreviewHost.Windows.TryGetValue(dt.Hwnd, out var dpw) && dpw.IsTargetMinimized, "minimized target handled (preview shows message, no crash)");
        Native.ShowWindow(dt.Hwnd, 9 /* SW_RESTORE */);
        await Task.Delay(800);
        dummy.Kill();
        await Task.Delay(1500);
        Check(!vm.PreviewHost.IsShowing(dt.Hwnd), "target closed → preview unregistered and closed");
        r = vm.Controller.Reset(dt);
        Check(r.Succeeded, $"reset after target closed: {r}");
        vm.InjectionEnabled = injectWas;

        // ---- opt-out ----
        vm.OptOutCommand.Execute(null);
        Check(!vm.Store.Load().OptedIn && !vm.IsWatching, $"opt-out persisted and watcher stopped ('{vm.WatchingText}')");
        Check(vm.AppliedRows.Count > 0, $"Applied list has {vm.AppliedRows.Count} row(s)");
        w.Close();
    }

    private static string Describe(AppliedRow? a) =>
        a is null ? "no Applied row within 20 s" : $"{a.Source} {a.ExecutableName} pid {a.ProcessId} via {a.Backend}: {a.DpiText} — {a.Outcome}: {a.Detail}";

    private static async Task<AppliedRow?> WaitForApplied(MainViewModel vm, int pid)
    {
        for (int i = 0; i < 100; i++)
        {
            var row = vm.AppliedRows.FirstOrDefault(a => a.ProcessId == pid && a.Source.StartsWith("auto") && a.Succeeded);
            if (row is not null) return row;
            await Task.Delay(200);
        }
        return vm.AppliedRows.FirstOrDefault(a => a.ProcessId == pid && a.Source.StartsWith("auto"));
    }

    private Process StartOwned(string path, string? args)
    {
        var psi = new ProcessStartInfo(path, args ?? "") { UseShellExecute = false };
        psi.Environment.Remove("APPSCALE_DPI");
        var p = Process.Start(psi)!;
        _owned.Add(p);
        return p;
    }

    private static async Task<TargetInfo?> WaitForWindow(int pid, bool cooperative)
    {
        for (int i = 0; i < 100; i++)
        {
            var t = WindowInspector.FindByProcess(pid).FirstOrDefault(x => x.IsCooperative == cooperative && !string.IsNullOrEmpty(x.Title));
            if (t is not null) return t;
            await Task.Delay(200);
        }
        return null;
    }

    private void Cleanup()
    {
        try { app.ViewModel?.Watcher.Stop(); app.ViewModel?.Watcher.ResetAll(); app.ViewModel?.Controller.ResetAll(); app.ViewModel?.PreviewHost.CloseAll(); } catch { }
        foreach (var p in _owned)
        {
            try
            {
                if (p.HasExited) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) p.Kill();
            }
            catch { }
        }
        if (_tempHome is not null) try { Directory.Delete(_tempHome, true); } catch { }
    }
}
