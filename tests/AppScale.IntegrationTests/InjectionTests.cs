using System.Diagnostics;
using AppScale.Core;
using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Targets;

namespace AppScale.IntegrationTests;

/// <summary>
/// The main feature: an ordinary PMv2 app that knows nothing about AppScale is made to re-render at
/// a DPI it is lied to about. The harnesses run with --no-cooperative (stock framework behaviour).
/// </summary>
public sealed class InjectionTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("appscale-inj-").FullName;
    private readonly List<Process> _started = new();

    public static TheoryData<string> StockHarnesses => new() { "WinFormsHarness", "WpfHarness", "Win32Harness" };

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
            p.Dispose();
        }
        try { Directory.Delete(_home, true); } catch { }
    }

    private static void RequireOrSkip(bool present, string what)
    {
        TestHostDpi.SkipIfUnsuitableDesktop();
        if (present) return;
        Assert.False(Environment.GetEnvironmentVariable("APPSCALE_REQUIRE_HARNESSES") == "1", $"{what} not built");
        Skip.If(true, $"{what} not built; run scripts\\build.ps1 first.");
    }

    private ScaleController Controller(Action<SettingsStore>? setup = null)
    {
        var store = new SettingsStore(Path.Combine(_home, "settings.json"));
        store.Save(new AppScaleSettings { OptedIn = true });
        setup?.Invoke(store);
        return new ScaleController(store, ScaleController.DefaultBackends(null));
    }

    private Process StartStock(string exe)
    {
        var psi = new ProcessStartInfo(exe, "--no-cooperative") { UseShellExecute = false };
        psi.Environment.Remove("APPSCALE_DPI");
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    private static TargetInfo WaitForWindow(Process p, Func<TargetInfo, bool>? predicate = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            var t = WindowInspector.FindByProcess(p.Id).FirstOrDefault(w => predicate?.Invoke(w) ?? true);
            if (t is not null) return t;
            Assert.False(p.HasExited, "target exited");
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException("window never appeared");
    }

    [SkippableTheory]
    [MemberData(nameof(StockHarnesses))]
    public void StockAppIsInjectedScaledAndReset(string harness)
    {
        RequireOrSkip(InjectionBackend.FindInjector() is not null, "AppScale.Injector");
        string? exe = HarnessTests.HarnessPath(harness);
        RequireOrSkip(exe is not null, harness);

        var p = StartStock(exe!);
        var target = WaitForWindow(p);
        Thread.Sleep(500);
        Assert.False(target.IsCooperative, "stock mode must not speak the protocol before injection");
        int os = target.WindowDpi;
        NativeSize(target.Hwnd, out int w0, out int h0);

        int systemBefore = WindowInspector.GetSystemDpi();
        var ctl = Controller();
        var (r, choice) = ctl.Apply(target, 200, allowVisualFallback: false);
        Assert.True(r.Succeeded, r.ToString());
        Assert.Equal(InjectionBackend.BackendId, choice!.Backend!.Id);
        Assert.Equal(192, r.EffectiveDpi);

        // The app re-laid itself out through its own WM_DPICHANGED handling.
        Thread.Sleep(500);
        NativeSize(target.Hwnd, out int w1, out int h1);
        double expected = 192.0 / os;
        Assert.InRange(w1 / (double)w0, expected * 0.9, expected * 1.1);
        Assert.InRange(h1 / (double)h0, expected * 0.85, expected * 1.15);

        // Unrelated: global/system DPI as seen by us is unchanged.
        Assert.Equal(systemBefore, WindowInspector.GetSystemDpi());

        Assert.True(ctl.Reset(target).Succeeded);
        Thread.Sleep(500);
        NativeSize(target.Hwnd, out int w2, out int h2);
        Assert.InRange(w2, w0 - 2, w0 + 2);
        Assert.InRange(h2, h0 - 2, h0 + 2);
    }

    [SkippableFact]
    public void WatcherAutoAppliesEnabledProfileWhenAppStarts()
    {
        RequireOrSkip(InjectionBackend.FindInjector() is not null, "AppScale.Injector");
        string? exe = HarnessTests.HarnessPath("WinFormsHarness");
        RequireOrSkip(exe is not null, "WinFormsHarness");

        var id = ExecutableIdentity.FromPath(exe!);
        var ctl = Controller(s => s.UpsertProfile(new ScaleProfile { ExecutableKey = id.Key, ExecutablePath = id.FullPath, ScalePercent = 175, Enabled = true }));
        using var watcher = new ProfileWatcher(ctl);

        var p = StartStock(exe!);
        WaitForWindow(p);
        var sw = Stopwatch.StartNew();
        ScaleResult? result = null;
        while (result is null && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            result = watcher.Tick().Where(x => x.Target.ProcessId == p.Id).Select(x => x.Result).FirstOrDefault(x => x.Succeeded);
            if (result is null) Thread.Sleep(250);
        }
        Assert.NotNull(result);
        Assert.Equal(168, result!.EffectiveDpi);
        Assert.DoesNotContain(watcher.Tick(), x => x.Target.ProcessId == p.Id); // applied once per process
        ctl.ResetAll();
    }

    [SkippableFact]
    public void DisabledProfileIsNeverApplied()
    {
        string? exe = HarnessTests.HarnessPath("WinFormsHarness");
        RequireOrSkip(exe is not null, "WinFormsHarness");
        var id = ExecutableIdentity.FromPath(exe!);
        var ctl = Controller(s => s.UpsertProfile(new ScaleProfile { ExecutableKey = id.Key, ExecutablePath = id.FullPath, ScalePercent = 175, Enabled = false }));
        var p = StartStock(exe!);
        var t = WaitForWindow(p);
        Assert.DoesNotContain(new ProfileWatcher(ctl).Tick(), x => x.Target.ProcessId == p.Id);
        Thread.Sleep(300);
        Assert.False(WindowInspector.Inspect(t.Hwnd)!.IsCooperative, "nothing may be injected without an enabled profile");
    }

    private static void NativeSize(nint hwnd, out int w, out int h)
    {
        Win32.GetClientRect(hwnd, out var c);
        w = c.Right; h = c.Bottom;
    }
}
