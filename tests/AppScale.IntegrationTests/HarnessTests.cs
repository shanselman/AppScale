using System.Diagnostics;
using AppScale.Core;
using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Targets;

namespace AppScale.IntegrationTests;

/// <summary>
/// Launches the owned harness executables (built into artifacts\bin) and drives them through the real
/// cooperative protocol via ScaleController. Skips when a harness has not been built, unless
/// APPSCALE_REQUIRE_HARNESSES=1 (CI sets this after building them).
/// </summary>
public sealed class HarnessTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("appscale-it-").FullName;
    private readonly List<Process> _started = new();

    public static TheoryData<string> Harnesses => new() { "Win32Harness", "WinFormsHarness", "WpfHarness" };

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
            p.Dispose();
        }
        try { Directory.Delete(_home, true); } catch { }
    }

    internal static string? HarnessPath(string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return null;
        string exe = Path.Combine(dir, "artifacts", "bin", name, "release", name + ".exe");
        return File.Exists(exe) ? exe : null;
    }

    private string RequireHarness(string name)
    {
        string? exe = HarnessPath(name);
        if (exe is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("APPSCALE_REQUIRE_HARNESSES") == "1", $"{name} not built");
            Skip.If(true, $"{name}.exe not built; run scripts\\build.ps1 first.");
        }
        return exe!;
    }

    private (Process, TargetInfo) Launch(string exe, int? startupDpi = null, string args = "")
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        if (startupDpi is int d) psi.Environment["APPSCALE_DPI"] = d.ToString();
        psi.Environment.Remove("APPSCALE_EXPERIMENTAL_DETOURS");
        var p = Process.Start(psi)!;
        _started.Add(p);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            var t = WindowInspector.FindByProcess(p.Id).FirstOrDefault(w => w.IsCooperative);
            if (t is not null) return (p, t);
            if (p.HasExited) break;
            Thread.Sleep(100);
        }
        throw new Xunit.Sdk.XunitException($"{Path.GetFileName(exe)} never exposed a cooperative window (exited={p.HasExited}).");
    }

    private ScaleController Controller()
    {
        var store = new SettingsStore(Path.Combine(_home, "settings.json"));
        store.Save(new AppScaleSettings { OptedIn = true });
        return new ScaleController(store, ScaleController.DefaultBackends(null));
    }

    [SkippableTheory]
    [MemberData(nameof(Harnesses))]
    public void LiveApplyVerifyAndReset(string harness)
    {
        var (_, target) = Launch(RequireHarness(harness));
        var ctl = Controller();
        int systemDpiBefore = WindowInspector.GetSystemDpi();

        Assert.True(CooperativeProtocol.TryGetDpi(target.Hwnd, out int initial, out int os) == CooperativeProtocol.SendOutcome.Ok);
        Assert.Equal(target.WindowDpi, os);
        Assert.Equal(os, initial);

        foreach (int pct in new[] { 75, 100, 125, 150, 200, 250 })
        {
            var (r, choice) = ctl.Apply(target, pct, allowVisualFallback: false);
            Assert.True(r.Succeeded, r.ToString());
            Assert.Equal("cooperative", choice!.Backend!.Id);
            Assert.True(CooperativeProtocol.TryGetDpi(target.Hwnd, out int eff, out int osNow) == CooperativeProtocol.SendOutcome.Ok);
            Assert.Equal(96 * pct / 100, eff);
            Assert.Equal(os, osNow); // the OS DPI of the window never changes: we only change the app's effective DPI
        }

        Assert.True(ctl.Reset(target).Succeeded);
        CooperativeProtocol.TryGetDpi(target.Hwnd, out int after, out _);
        Assert.Equal(os, after);
        Assert.Equal(systemDpiBefore, WindowInspector.GetSystemDpi());
    }

    [SkippableTheory]
    [MemberData(nameof(Harnesses))]
    public void StartupOverrideViaEnvironment(string harness)
    {
        var (_, target) = Launch(RequireHarness(harness), startupDpi: 168);
        Assert.True(CooperativeProtocol.TryGetDpi(target.Hwnd, out int eff, out _) == CooperativeProtocol.SendOutcome.Ok);
        Assert.Equal(168, eff);
    }

    [SkippableTheory]
    [MemberData(nameof(Harnesses))]
    public void RepeatedChangesDoNotDrift(string harness)
    {
        var (_, target) = Launch(RequireHarness(harness));
        var ctl = Controller();
        Assert.True(ctl.Apply(target, 150, false).Result.Succeeded);
        Thread.Sleep(300);
        NativeRects(target.Hwnd, out var w1, out var c1);
        for (int i = 0; i < 20; i++)
            foreach (int pct in new[] { 75, 125, 175, 250, 100 })
                Assert.True(ctl.Apply(target, pct, false).Result.Succeeded);
        Assert.True(ctl.Apply(target, 150, false).Result.Succeeded);
        Thread.Sleep(300);
        NativeRects(target.Hwnd, out var w2, out var c2);
        Assert.Equal(c1, c2);
        Assert.Equal(w1, w2);
    }

    [SkippableFact]
    public void UnrelatedProcessIsUntouched()
    {
        string exe = RequireHarness("Win32Harness");
        var (_, a) = Launch(exe);
        var (_, b) = Launch(exe);
        var ctl = Controller();
        CooperativeProtocol.TryGetDpi(b.Hwnd, out int bBefore, out _);
        Assert.True(ctl.Apply(a, 200, false).Result.Succeeded);
        CooperativeProtocol.TryGetDpi(b.Hwnd, out int bAfter, out _);
        Assert.Equal(bBefore, bAfter);
        Assert.Single(ctl.ResetAll());
    }

    [SkippableFact]
    public void OptOutAndEmergencyDisableFailClosed()
    {
        var (_, target) = Launch(RequireHarness("Win32Harness"));
        var ctl = Controller();
        ctl.Store.Update(s => s with { OptedIn = false });
        Assert.Equal(ScaleFailureCode.OptInRequired, ctl.Apply(target, 150, false).Result.Code);
        ctl.Store.Update(s => s with { OptedIn = true });
        Assert.True(ctl.Apply(target, 150, false).Result.Succeeded);
        ctl.EmergencyDisable();
        CooperativeProtocol.TryGetDpi(target.Hwnd, out int eff, out int os);
        Assert.Equal(os, eff);
        Assert.Equal(ScaleFailureCode.EmergencyDisabled, ctl.Apply(target, 150, false).Result.Code);
    }

    [SkippableFact]
    public void NonCooperativeWindowGetsNoNativeBackend()
    {
        // A harness launched normally is cooperative; WindowInspector of a non-cooperative process
        // (this test host has no windows) is covered by probing a fake target instead.
        var ctl = Controller();
        var fake = new TargetInfo { Hwnd = 0x1, ProcessId = 999999, Executable = new ExecutableIdentity(@"C:\Apps\x.exe"), WindowDpi = 96, MonitorDpi = 96 };
        var (r, _) = ctl.Apply(fake, 150, false);
        Assert.Equal(ScaleFailureCode.NoBackend, r.Code);
    }

    private static void NativeRects(nint hwnd, out (int, int, int, int) window, out (int, int) client)
    {
        Win32.GetWindowRect(hwnd, out var w);
        Win32.GetClientRect(hwnd, out var c);
        window = (w.Left, w.Top, w.Right, w.Bottom);
        client = (c.Right, c.Bottom);
    }
}

public sealed class SelfTestHarnessTests
{
    public static TheoryData<string, string> SelfTests => new()
    {
        { "Win32Harness", "--selftest-drift" },
        { "Win32Harness", "--selftest-synthetic" },
        { "Win32Harness", "--selftest-monitors" },
        { "WinFormsHarness", "--selftest" },
        { "WpfHarness", "--selftest" },
    };

    [SkippableTheory]
    [MemberData(nameof(SelfTests))]
    public void HarnessSelfTestPasses(string harness, string mode)
    {
        string? exe = HarnessTests.HarnessPath(harness);
        if (exe is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("APPSCALE_REQUIRE_HARNESSES") == "1", $"{harness} not built");
            Skip.If(true, $"{harness} not built");
        }
        string result = Path.Combine(Path.GetTempPath(), $"appscale-{harness}-{mode.TrimStart('-')}-{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo(exe!, $"{mode} --result \"{result}\"") { UseShellExecute = false };
        psi.Environment.Remove("APPSCALE_DPI");
        using var p = Process.Start(psi)!;
        bool exited = p.WaitForExit(120_000);
        if (!exited) p.Kill(true);
        Assert.True(exited, "self-test timed out");
        string text = File.Exists(result) ? File.ReadAllText(result) : "(no result file)";
        try { File.Delete(result); } catch { }
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {text}");
    }
}

internal static partial class Win32
{
    public struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out RECT rect);
}


