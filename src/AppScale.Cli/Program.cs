using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using AppScale.Core;
using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Safety;
using AppScale.Core.Scaling;
using AppScale.Core.Targets;

namespace AppScale.Cli;

/// <summary>Diagnostic CLI. Read-only commands never require opt-in; anything that changes a window does.</summary>
public static class Program
{
    private const string Usage = """
        appscale — experimental per-application scaling (diagnostic CLI)

        Read-only:
          list [--all] [--json]            Top-level windows with DPI, awareness, cooperative and exclusion status
          inspect <target>                 Probe every backend for a window
          status <target>                  Current backend status
          monitors                         Monitors and their effective DPI
          dpi <percent>                    Absolute DPI and relative zoom on each monitor

        Changes (require 'optin'):
          optin | optout                   Explicit consent switch (default: opted out)
          apply <target> <percent> [--backend cooperative|inject]
                                           Ordinary PMv2 apps use 'inject': AppScale.Native is loaded into that
                                           one process, lies about DPI and triggers the app's own WM_DPICHANGED.
          reset <target>
          launch <exe> <percent> [--inject] [--experimental-detours] [-- <args>]
          watch [--once]                   Background: auto-apply ENABLED profiles when their app starts
          emergency-disable | emergency-clear

        Profiles (local JSON, disabled by default):
          profiles list | save <exe> <percent> | enable <exe> | disable <exe> | delete <exe> | export <path> | delete-all

        <target> = 0xHWND (hex window handle) or a process id (uses its cooperative or first top-level window).
        Settings: %LOCALAPPDATA%\AppScale\settings.json (override with APPSCALE_HOME).
        """;

    public static int Main(string[] args)
    {
        try { return Run(args, Console.Out); }
        catch (CliException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 2; }
    }

    public static int Run(string[] args, TextWriter o)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { o.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
        var store = SettingsStore.Default();
        var controller = new ScaleController(store, ScaleController.DefaultBackends(previewHost: null));
        string cmd = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        switch (cmd)
        {
            case "list": return List(rest, o);
            case "monitors": return Monitors(o);
            case "dpi": return Dpi(rest, o);
            case "inspect": return Inspect(controller, Target(rest), o);
            case "status":
            {
                var t = Target(rest);
                foreach (var b in controller.Backends) o.WriteLine($"{b.Id,-16} {b.GetStatus(t)}");
                return 0;
            }
            case "optin": store.Update(s => s with { OptedIn = true }); o.WriteLine("Opted in. AppScale may now change windows you explicitly target (cooperative protocol, or by loading its DPI adapter into that one process)."); return 0;
            case "optout": store.Update(s => s with { OptedIn = false }); o.WriteLine("Opted out."); return 0;
            case "emergency-disable":
                foreach (var r in controller.EmergencyDisable()) o.WriteLine(r);
                o.WriteLine("Emergency disable ON. All cooperative overrides were reset. Run 'appscale emergency-clear' to re-enable.");
                return 0;
            case "emergency-clear": store.Update(s => s with { EmergencyDisabled = false }); o.WriteLine("Emergency disable cleared."); return 0;
            case "apply":
            {
                if (rest.Length < 2) throw new CliException("usage: apply <target> <percent> [--backend <id>]");
                var t = Target(rest);
                int pct = Percent(rest[1]);
                string? backend = Option(rest, "--backend");
                var (result, choice) = controller.Apply(t, pct, allowVisualFallback: false, backend);
                if (choice is not null) o.WriteLine($"backend: {choice.Explanation}");
                o.WriteLine(result);
                return result.Succeeded ? 0 : 1;
            }
            case "reset":
            {
                var r = controller.Reset(Target(rest));
                o.WriteLine(r);
                return r.Succeeded ? 0 : 1;
            }
            case "launch": return Launch(store, rest, o);
            case "profiles": return Profiles(store, rest, o);
            case "watch": return Watch(controller, rest, o);
            default: throw new CliException($"unknown command '{cmd}'. Run 'appscale help'.");
        }
    }

    private static int List(string[] rest, TextWriter o)
    {
        bool all = rest.Contains("--all");
        var windows = WindowInspector.EnumerateTopLevelWindows(includeUntitled: all);
        if (rest.Contains("--json"))
        {
            var rows = windows.Select(w => new
            {
                hwnd = $"0x{w.Hwnd:X}", pid = w.ProcessId, exe = w.Executable?.FullPath, w.Title, w.ClassName,
                w.WindowDpi, w.MonitorDpi, awareness = w.Awareness.ToString(), w.IsCooperative, w.IsElevated,
                excluded = SafetyPolicy.Check(w, out var reason) == ScaleFailureCode.None ? null : reason,
            });
            o.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        o.WriteLine($"{"HWND",-12} {"PID",7} {"WinDPI",6} {"MonDPI",6} {"Awareness",-18} {"Coop",4}  {"Executable",-28} Title");
        foreach (var w in windows)
        {
            bool excluded = SafetyPolicy.Check(w, out _) != ScaleFailureCode.None;
            string title = w.Title.Length > 40 ? w.Title[..40] + "…" : w.Title;
            o.WriteLine($"0x{w.Hwnd,-10:X} {w.ProcessId,7} {w.WindowDpi,6} {w.MonitorDpi,6} {w.Awareness,-18} {(w.IsCooperative ? "yes" : ""),4}  {(excluded ? "(x) " : "") + w.ExecutableName,-28} {title}");
        }
        o.WriteLine("(x) = excluded by safety policy. Only 'Coop = yes' windows can be natively rescaled.");
        return 0;
    }

    private static int Monitors(TextWriter o)
    {
        o.WriteLine($"System DPI (GetDpiForSystem): {WindowInspector.GetSystemDpi()}");
        foreach (var m in MonitorInfo.All())
            o.WriteLine($"{m.Device,-14} {m.Bounds,-32} effective DPI {m.Dpi} ({ScaleMath.PercentForDpi(m.Dpi):0}%){(m.Primary ? " primary" : "")}");
        return 0;
    }

    private static int Dpi(string[] rest, TextWriter o)
    {
        if (rest.Length < 1) throw new CliException("usage: dpi <percent>");
        int pct = Percent(rest[0]);
        int dpi = ScaleMath.DpiForPercent(pct);
        o.WriteLine($"Absolute: {pct}% = {dpi} DPI (96 × {pct}/100), independent of monitor.");
        foreach (var m in MonitorInfo.All())
            o.WriteLine($"  on {m.Device} ({m.Dpi} DPI): relative visual zoom ×{ScaleMath.RelativeZoom(dpi, m.Dpi):0.###}");
        return 0;
    }

    private static int Inspect(ScaleController controller, TargetInfo t, TextWriter o)
    {
        o.WriteLine(t);
        o.WriteLine($"  executable : {t.Executable?.FullPath ?? "(inaccessible)"}");
        o.WriteLine($"  class      : {t.ClassName}");
        o.WriteLine($"  awareness  : {t.Awareness} (awareness is not scale)");
        o.WriteLine($"  window DPI : {t.WindowDpi}   monitor DPI: {t.MonitorDpi}   system DPI: {WindowInspector.GetSystemDpi()}");
        var safety = SafetyPolicy.Check(t, out var reason);
        o.WriteLine($"  safety     : {(safety == ScaleFailureCode.None ? "allowed" : $"EXCLUDED ({safety}): {reason}")}");
        foreach (var b in controller.Backends)
        {
            var p = b.Probe(t);
            o.WriteLine($"  {b.Id,-16} {(p.Supported ? "supported" : $"no ({p.Code})"),-22} [{b.Capabilities}] {p.Detail}");
        }
        var choice = controller.Choose(t, allowVisualFallback: false);
        o.WriteLine($"  selection  : {choice.Explanation}");
        if (!t.IsCooperative) o.WriteLine("  compatibility: UNKNOWN — not cooperative. Applying would load AppScale.Native into this process only; results depend on the framework (see docs/compatibility.md).");
        return 0;
    }

    private static int Launch(SettingsStore store, string[] rest, TextWriter o)
    {
        if (rest.Length < 2) throw new CliException("usage: launch <exe> <percent> [--experimental-detours] [-- <args>]");
        var s = store.Load();
        if (!s.OptedIn) { o.WriteLine(ScaleResult.Fail(ScaleFailureCode.OptInRequired, "Run 'appscale optin' first.")); return 1; }
        if (s.EmergencyDisabled) { o.WriteLine(ScaleResult.Fail(ScaleFailureCode.EmergencyDisabled, "Emergency disable is on.")); return 1; }
        bool detours = rest.Contains("--experimental-detours");
        if (detours && !s.Features.ExperimentalDetours)
        {
            o.WriteLine(ScaleResult.Fail(ScaleFailureCode.FeatureDisabled, "Experimental Detours research is off. Set features.experimentalDetours=true in settings.json to opt in."));
            return 1;
        }
        int sep = Array.IndexOf(rest, "--");
        string? extra = sep >= 0 ? string.Join(' ', rest[(sep + 1)..].Select(a => a.Contains(' ') ? $"\"{a}\"" : a)) : null;
        if (rest.Contains("--inject"))
        {
            string? injector = InjectionBackend.FindInjector();
            if (injector is null) { o.WriteLine(ScaleResult.Fail(ScaleFailureCode.NotSupported, "AppScale.Injector.exe not found.")); return 1; }
            var psi = new System.Diagnostics.ProcessStartInfo(injector) { UseShellExecute = false };
            foreach (var a in new[] { "--launch", Path.GetFullPath(rest[0]), "--dpi", ScaleMath.DpiForPercent(Percent(rest[1])).ToString() }) psi.ArgumentList.Add(a);
            if (sep >= 0) { psi.ArgumentList.Add("--"); foreach (var a in rest[(sep + 1)..]) psi.ArgumentList.Add(a); }
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        var r = CooperativeLauncher.Launch(Path.GetFullPath(rest[0]), Percent(rest[1]), extra, detours);
        o.WriteLine(r);
        return r.Succeeded ? 0 : 1;
    }

    private static int Watch(ScaleController controller, string[] rest, TextWriter o)
    {
        var s = controller.Store.Load();
        if (!s.OptedIn) { o.WriteLine(ScaleResult.Fail(ScaleFailureCode.OptInRequired, "Run 'appscale optin' first.")); return 1; }
        var enabled = s.Profiles.Where(p => p.Enabled).ToList();
        o.WriteLine($"Watching {enabled.Count} enabled profile(s): {string.Join(", ", enabled.Select(p => $"{Path.GetFileName(p.ExecutablePath)}@{p.ScalePercent}%"))}");
        using var watcher = new ProfileWatcher(controller);
        if (rest.Contains("--once"))
        {
            foreach (var (t, r) in watcher.Tick()) o.WriteLine($"{t.Executable?.FileName} pid {t.ProcessId}: {r}");
            return 0;
        }
        watcher.Applied += (t, r) => o.WriteLine($"{DateTime.Now:T} {t.Executable?.FileName} pid {t.ProcessId}: {r}");
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        watcher.Start();
        o.WriteLine("Press Ctrl+C to stop (overrides applied by this watcher are reset on exit).");
        done.Wait();
        watcher.Stop();
        foreach (var r in controller.ResetAll()) o.WriteLine(r);
        return 0;
    }

    private static int Profiles(SettingsStore store, string[] rest, TextWriter o)
    {
        string sub = rest.Length > 0 ? rest[0].ToLowerInvariant() : "list";
        string Key(int i) => rest.Length > i ? ExecutableIdentity.FromPath(rest[i]).Key : throw new CliException("missing <exe>");
        switch (sub)
        {
            case "list":
                var s = store.Load();
                o.WriteLine($"opted in: {s.OptedIn}   emergency disabled: {s.EmergencyDisabled}   features: {s.Features}");
                foreach (var p in s.Profiles) o.WriteLine($"{(p.Enabled ? "[on] " : "[off]")} {p.ScalePercent,3}%  {p.PreferredBackend ?? "auto",-12} {p.ExecutablePath}");
                if (s.Profiles.Count == 0) o.WriteLine("(no profiles)");
                return 0;
            case "save":
                if (rest.Length < 3) throw new CliException("usage: profiles save <exe> <percent>");
                var id = ExecutableIdentity.FromPath(rest[1]);
                store.UpsertProfile(new ScaleProfile { ExecutableKey = id.Key, ExecutablePath = id.FullPath, ScalePercent = Percent(rest[2]) });
                o.WriteLine($"Saved (disabled) profile for {id.FullPath}.");
                return 0;
            case "enable" or "disable":
                bool on = sub == "enable";
                string key = Key(1);
                bool found = store.Load().Profiles.Any(p => p.ExecutableKey == key);
                if (found)
                    store.Update(st => st with { Profiles = st.Profiles.Select(p => p.ExecutableKey == key ? p with { Enabled = on } : p).ToList() });
                o.WriteLine(found ? $"Profile {(on ? "enabled" : "disabled")}." : "No such profile.");
                return found ? 0 : 1;
            case "delete":
                bool removed = store.DeleteProfile(Key(1));
                o.WriteLine(removed ? "Deleted." : "No such profile.");
                return removed ? 0 : 1;
            case "export":
                if (rest.Length < 2) throw new CliException("usage: profiles export <path>");
                store.Export(rest[1]);
                o.WriteLine($"Exported to {Path.GetFullPath(rest[1])}.");
                return 0;
            case "delete-all":
                store.DeleteAll();
                o.WriteLine("All AppScale settings and profiles deleted.");
                return 0;
            default: throw new CliException($"unknown profiles command '{sub}'");
        }
    }

    private static int Percent(string s)
    {
        s = s.TrimEnd('%');
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) || !ScaleMath.IsValidPercent(p))
            throw new CliException($"scale must be an integer {ScaleMath.MinPercent}–{ScaleMath.MaxPercent} (got '{s}')");
        return p;
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static TargetInfo Target(string[] rest)
    {
        if (rest.Length < 1) throw new CliException("missing <target> (0xHWND or pid)");
        string s = rest[0];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) throw new CliException($"bad hwnd '{s}'");
            return WindowInspector.Inspect((nint)h) ?? throw new CliException($"window {s} does not exist");
        }
        if (!int.TryParse(s, out int pid)) throw new CliException($"bad target '{s}'");
        var windows = WindowInspector.FindByProcess(pid);
        return windows.FirstOrDefault(w => w.IsCooperative) ?? windows.FirstOrDefault() ?? throw new CliException($"no visible top-level window for pid {pid}");
    }
}

internal sealed class CliException(string message) : Exception(message);

internal static partial class MonitorInfo
{
    public sealed record Monitor(string Device, string Bounds, int Dpi, bool Primary);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint rect, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int l, t, r, b;
        public int wl, wt, wr, wb;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public static List<Monitor> All()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(0, 0, (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            GetMonitorInfo(h, ref mi);
            GetDpiForMonitor(h, 0, out uint dx, out _);
            list.Add(new Monitor(mi.szDevice, $"({mi.l},{mi.t})-({mi.r},{mi.b}) {mi.r - mi.l}x{mi.b - mi.t}", (int)dx, (mi.dwFlags & 1) != 0));
            return true;
        }, 0);
        return list;
    }
}
