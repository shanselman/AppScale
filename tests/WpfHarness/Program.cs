using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options o;
        try { o = Options.Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        Log.Open(o.LogPath!);
        DpiState.Cooperative = !o.NoCooperative;
        DpiState.PopupScaling = !o.NoPopupScale;

        // Stock mode must not read APPSCALE_DPI at all.
        string? rawEnv = DpiState.Cooperative ? Environment.GetEnvironmentVariable(Native.StartupDpiEnvironmentVariable) : null;
        int? startup = DpiState.Cooperative ? Native.StartupDpi() : null;
        Log.Write("startup", new
        {
            args,
            mode = DpiState.Cooperative ? "cooperative" : "stock (--no-cooperative: no protocol, APPSCALE_DPI not read)",
            threadAwareness = Native.AwarenessName(Native.GetThreadDpiAwarenessContext()),
            systemDpi = (int)Native.GetDpiForSystem(),
            envAppScaleDpi = rawEnv,
            startupOverrideDpi = startup,
            popupScaling = DpiState.PopupScaling,
            os = Environment.OSVersion.VersionString,
            runtime = RuntimeInformation.FrameworkDescription,
        });

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        if (DpiState.Cooperative) DpiState.HookPopups();
        if (startup is int s) DpiState.SetOverride(s, "env");

        int exitCode = 0;
        var w = new MainWindow();
        if (o.ExitAfterMs is int ms)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
            t.Tick += (_, _) => { t.Stop(); w.Close(); };
            t.Start();
        }
        if (o.SelfTest)
        {
            w.ContentRendered += (_, _) => w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try { exitCode = new SelfTest(w, o).Run(); }
                catch (Exception ex) { Log.Write("selftest.crash", ex.ToString()); WriteResult(o.ResultPath, new { harness = "wpf", pass = false, error = ex.ToString() }); exitCode = 3; }
                w.Close();
            });
        }
        else if (o.ScreenshotPath is string shot)
        {
            w.ContentRendered += (_, _) => w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try { exitCode = Capture(w, shot, o.ResultPath); }
                catch (Exception ex) { Log.Write("screenshot.crash", ex.ToString()); exitCode = 3; }
                w.Close();
            });
        }
        app.Run(w);
        Log.Write("exit", new { exitCode });
        Log.Close();
        return exitCode;
    }

    public static void WriteResult(string? path, object result)
    {
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        if (path is null) { Console.WriteLine(json); return; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
    }

    /// <summary>RenderTargetBitmap of the root content (client area only; the OS caption is not included).</summary>
    private static int Capture(MainWindow w, string path, string? resultPath)
    {
        SelfTest.Pump(200);
        int os = w.OsDpi;
        int px = (int)Math.Ceiling(w.Outer.ActualWidth * os / 96.0), py = (int)Math.Ceiling(w.Outer.ActualHeight * os / 96.0);
        var rtb = new RenderTargetBitmap(px, py, os, os, PixelFormats.Pbgra32);
        rtb.Render(w.Outer);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using (var fs = File.Create(path)) enc.Save(fs);
        var info = new
        {
            harness = "wpf",
            mode = "screenshot",
            path = System.IO.Path.GetFullPath(path),
            method = "RenderTargetBitmap(root content, dpi = OS DPI)",
            imageSize = new { width = px, height = py },
            state = w.Snapshot("screenshot"),
            knownLabelPx = w.PhysicalBounds(w.KnownLabel, w.Surface).ToString(),
        };
        Log.Write("screenshot", info);
        if (resultPath != null) WriteResult(resultPath, info);
        return 0;
    }
}

/// <summary>Unattended self-test. Every DPI change goes through SendMessage(AppScale.SetDpi.v1).</summary>
internal sealed class SelfTest
{
    private static readonly int[] Dpis = { 72, 96, 120, 144, 192, 240 };
    private readonly MainWindow _w;
    private readonly Options _o;
    private readonly List<string> _failures = new();
    private readonly List<double> _layoutMs = new(), _renderMs = new();
    private int _boundsChecks, _boundsFailed, _maxErr, _hitTotal, _hitFailInput, _hitFailVisual, _hitFailScreen;

    public SelfTest(MainWindow w, Options o) { _w = w; _o = o; }

    private FrameworkElement[] Keys => new FrameworkElement[] { _w.KnownLabel, _w.Text1, _w.Check, _w.Combo, _w.ApplyButton, _w.DialogButton, _w.List, _w.Vector };

    private void Fail(string m) { if (_failures.Count < 50) _failures.Add(m); Log.Write("selftest.failure", m); }

    public static void Pump(int ms = 0)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            if (ms > 0) Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < ms);
    }

    private nint Send(int dpi)
    {
        var sw = Stopwatch.StartNew();
        nint r = Native.SendMessage(_w.Hwnd, CooperativeWindow.SetDpiMsg, dpi, 0);
        double layout = sw.Elapsed.TotalMilliseconds;
        Pump();
        _layoutMs.Add(layout);
        _renderMs.Add(sw.Elapsed.TotalMilliseconds - layout);
        return r;
    }

    private (int eff, int os) Query()
    {
        long r = Native.SendMessage(_w.Hwnd, CooperativeWindow.GetDpiMsg, 0, 0);
        return ((int)(r & 0xFFFF), (int)((r >> 16) & 0xFFFF));
    }

    private void CheckBounds(CooperativeWindow win, FrameworkElement origin, IDictionary<FrameworkElement, Rect> logical, int eff, string ctx)
    {
        foreach (var (e, l) in logical)
        {
            var p = win.PhysicalBounds(e, origin);
            var exp = new[] { Native.MulDiv((int)l.X, eff, 96), Native.MulDiv((int)l.Y, eff, 96), Native.MulDiv((int)l.Width, eff, 96), Native.MulDiv((int)l.Height, eff, 96) };
            var act = new[] { (int)p.X, (int)p.Y, (int)p.Width, (int)p.Height };
            for (int i = 0; i < 4; i++)
            {
                _boundsChecks++;
                int err = Math.Abs(exp[i] - act[i]);
                _maxErr = Math.Max(_maxErr, err);
                if (err > 1) { _boundsFailed++; Fail($"{ctx} eff={eff} {e.GetType().Name} {"xywh"[i]} expected {exp[i]} got {act[i]}"); }
            }
        }
    }

    private static bool IsWithin(DependencyObject? hit, DependencyObject target)
    {
        while (hit != null)
        {
            if (ReferenceEquals(hit, target)) return true;
            hit = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) ?? LogicalTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return false;
    }

    private void CheckHits(int eff)
    {
        foreach (var e in Keys)
        {
            var b = e.TransformToAncestor(_w.Outer).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            var centre = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            _hitTotal++;
            if (!IsWithin(_w.Outer.InputHitTest(centre) as DependencyObject, e)) { _hitFailInput++; Fail($"InputHitTest eff={eff} {e.GetType().Name}"); }
            if (!IsWithin(VisualTreeHelper.HitTest(_w.Outer, centre)?.VisualHit, e)) { _hitFailVisual++; Fail($"VisualTreeHelper.HitTest eff={eff} {e.GetType().Name}"); }
            // Full physical round trip: centre → screen pixels → back into the (scaled) tree → hit.
            var screen = _w.Outer.PointToScreen(centre);
            var back = _w.Outer.PointFromScreen(new Point(Math.Round(screen.X), Math.Round(screen.Y)));
            if (!IsWithin(_w.Outer.InputHitTest(back) as DependencyObject, e)) { _hitFailScreen++; Fail($"screen round-trip hit eff={eff} {e.GetType().Name}"); }
        }
    }

    private Dictionary<string, Rect> Measure()
    {
        var d = new Dictionary<string, Rect>();
        foreach (var e in Keys) d[e.GetType().Name + "#" + Array.IndexOf(Keys, e)] = _w.PhysicalBounds(e, _w.Surface);
        Native.GetClientRect(_w.Hwnd, out var cr);
        d["client"] = new Rect(0, 0, cr.Width, cr.Height);
        Native.GetWindowRect(_w.Hwnd, out var wr);
        d["windowSize"] = new Rect(0, 0, wr.Width, wr.Height);
        return d;
    }

    private static Native.RECT PopupRect(FrameworkElement? probe, out double rootScale)
    {
        rootScale = double.NaN;
        if (probe is null || PresentationSource.FromVisual(probe) is not System.Windows.Interop.HwndSource src) return default;
        if (probe.LayoutTransform is ScaleTransform st) rootScale = st.ScaleX; else rootScale = 1.0;
        Native.GetWindowRect(src.Handle, out var r);
        return r;
    }

    private readonly Dictionary<string, (int w, int h)> _popupAtOs = new();
    private int _popupChecks, _popupFailed;

    private object MeasurePopups(int dpi)
    {
        Send(dpi);
        var (eff, os) = Query();
        object One(string name, Action open, Func<FrameworkElement?> probe, Action close)
        {
            open(); Pump(150);
            var r = PopupRect(probe(), out double rs);
            close(); Pump(50);
            bool? follows = null;
            if (eff == os) _popupAtOs[name + DpiState.PopupScaling] = (r.Width, r.Height);
            else if (_popupAtOs.TryGetValue(name + DpiState.PopupScaling, out var b))
            {
                follows = Math.Abs(r.Width - Native.MulDiv(b.w, eff, os)) <= 2 && Math.Abs(r.Height - Native.MulDiv(b.h, eff, os)) <= 2;
                if (DpiState.PopupScaling)
                {
                    _popupChecks++;
                    if (follows != true) { _popupFailed++; Fail($"popup {name} at eff={eff}: {r.Width}x{r.Height}, expected ~{Native.MulDiv(b.w, eff, os)}x{Native.MulDiv(b.h, eff, os)}"); }
                }
            }
            return new { width = r.Width, height = r.Height, elementLayoutScale = double.IsNaN(rs) ? (double?)null : Math.Round(rs, 4), followsEffectiveDpi = follows };
        }
        _w.Context.PlacementTarget = _w.ApplyButton;
        _w.Context.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _w.Tip.PlacementTarget = _w.ApplyButton;
        return new
        {
            effectiveDpi = eff,
            osDpi = os,
            popupScaling = DpiState.PopupScaling,
            contextMenu = One("contextMenu", () => _w.Context.IsOpen = true, () => _w.Context, () => _w.Context.IsOpen = false),
            toolTip = One("toolTip", () => _w.Tip.IsOpen = true, () => _w.Tip, () => _w.Tip.IsOpen = false),
            fileSubmenu = One("fileSubmenu", () => _w.FileMenu.IsSubmenuOpen = true, () => _w.FileMenu.Items[0] as FrameworkElement, () => _w.FileMenu.IsSubmenuOpen = false),
            comboDropDown = One("comboDropDown", () => _w.Combo.IsDropDownOpen = true, () => _w.Combo.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement, () => _w.Combo.IsDropDownOpen = false),
        };
    }

    private object MeasureDialog(int dpi)
    {
        Send(dpi);
        object? result = null;
        var d = new DialogWindow { Owner = _w };
        d.ContentRendered += (_, _) => d.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            Pump(50);
            int before = _boundsFailed;
            CheckBounds(d, d.Surface, d.Logical, dpi, "dialog");
            Native.GetClientRect(d.Hwnd, out var cr);
            bool clientOk = Math.Abs(cr.Width - Native.MulDiv((int)DialogWindow.LogicalClient.Width, dpi, 96)) <= 1 &&
                            Math.Abs(cr.Height - Native.MulDiv((int)DialogWindow.LogicalClient.Height, dpi, 96)) <= 1;
            if (!clientOk) Fail($"dialog client {cr.Width}x{cr.Height} at {dpi}");
            long q = Native.SendMessage(d.Hwnd, CooperativeWindow.GetDpiMsg, 0, 0);
            if ((int)(q & 0xFFFF) != dpi) Fail($"dialog effective {q & 0xFFFF} != {dpi}");
            result = new { requestedDpi = dpi, dialogEffectiveDpi = (int)(q & 0xFFFF), dialogOsDpi = (int)((q >> 16) & 0xFFFF), clientSize = new { cr.Width, cr.Height }, clientOk, boundsOk = before == _boundsFailed, okButton = d.PhysicalBounds(d.Ok, d.Surface).ToString() };
            d.Close();
        });
        d.ShowDialog();
        return result ?? new { error = "dialog not measured" };
    }

    private object MoveTest(int startOs)
    {
        var target = Native.Monitors().FirstOrDefault(m => m.Dpi != 0 && m.Dpi != startOs);
        if (target is null) return new { skipped = "no monitor with a DPI different from the start monitor" };
        Native.GetWindowRect(_w.Hwnd, out var homeRect);
        var steps = new List<object>();
        void Step(string name, int? overrideDpi, int x, int y, int expectOs)
        {
            Send(overrideDpi ?? 0);
            int before = _boundsFailed, changed = _w.DpiChangedCount;
            Native.SetWindowPos(_w.Hwnd, 0, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Pump(500);
            var (eff, os) = Query();
            int expectEff = overrideDpi ?? expectOs;
            if (os != expectOs) Fail($"move {name}: os {os} expected {expectOs}");
            if (eff != expectEff) Fail($"move {name}: effective {eff} expected {expectEff}");
            CheckBounds(_w, _w.Surface, _w.Logical, expectEff, $"move {name}");
            Native.GetClientRect(_w.Hwnd, out var cr);
            if (Math.Abs(cr.Width - Native.MulDiv((int)MainWindow.LogicalSurface.Width, expectEff, 96)) > 1) Fail($"move {name}: client width {cr.Width}");
            steps.Add(new { name, overrideDpi, osDpi = os, effectiveDpi = eff, wmDpiChangedReceived = _w.DpiChangedCount - changed, scale = Math.Round(_w.Scale, 4), clientWidth = cr.Width, boundsOk = before == _boundsFailed });
        }
        Step("override192->other", 192, target.Work.Left + 40, target.Work.Top + 40, target.Dpi);
        Step("override192->home", 192, homeRect.Left, homeRect.Top, startOs);
        Step("noOverride->other", null, target.Work.Left + 40, target.Work.Top + 40, target.Dpi);
        Step("noOverride->home", null, homeRect.Left, homeRect.Top, startOs);
        return new { targetMonitor = target.Device, targetDpi = target.Dpi, startOs, steps };
    }

    private static object Stats(List<double> v)
    {
        if (v.Count == 0) return new { count = 0 };
        var s = v.OrderBy(x => x).ToArray();
        double P(double p) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(p * s.Length) - 1)];
        return new { count = s.Length, min = Math.Round(s[0], 3), median = Math.Round(P(0.5), 3), p95 = Math.Round(P(0.95), 3), max = Math.Round(s[^1], 3), mean = Math.Round(s.Average(), 3) };
    }

    public int Run()
    {
        _w.Topmost = true;
        Pump(100);
        nint rejLow = Native.SendMessage(_w.Hwnd, CooperativeWindow.SetDpiMsg, 50, 0);
        nint rejHigh = Native.SendMessage(_w.Hwnd, CooperativeWindow.SetDpiMsg, 300, 0);
        if (rejLow != 0 || rejHigh != 0) Fail("out-of-range DPI was not rejected");

        var (startEff, startOs) = Query();
        bool startedWithOverride = DpiState.Override.HasValue;
        CheckBounds(_w, _w.Surface, _w.Logical, startEff, "start");
        var baseline = Measure();

        int steps = 0, driftMismatches = 0;
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < _o.Rounds; round++)
        {
            foreach (int dpi in Dpis)
            {
                if (Send(dpi) != 1) Fail($"SetDpi({dpi}) not accepted");
                var (eff, _) = Query();
                if (eff != dpi) Fail($"GetDpi reports {eff} after SetDpi({dpi})");
                CheckBounds(_w, _w.Surface, _w.Logical, dpi, $"round {round}");
                Native.GetClientRect(_w.Hwnd, out var cr);
                if (Math.Abs(cr.Width - Native.MulDiv((int)MainWindow.LogicalSurface.Width, dpi, 96)) > 1) Fail($"client width {cr.Width} at {dpi}");
                CheckHits(dpi);
                steps++;
            }
            Send(startedWithOverride ? startEff : 0);
            var now = Measure();
            foreach (var (k, v) in baseline)
                if (now[k] != v) { driftMismatches++; Fail($"drift round {round}: {k} baseline {v} now {now[k]}"); }
        }
        double cycleSeconds = sw.Elapsed.TotalSeconds;

        var popupsScaled = new[] { startOs, 96, 192 }.Distinct().Select(MeasurePopups).ToArray();
        DpiState.PopupScaling = false;
        var popupsUnscaled = new[] { startOs, 96, 192 }.Distinct().Select(MeasurePopups).ToArray();
        DpiState.PopupScaling = !_o.NoPopupScale;
        var dialog = MeasureDialog(192);
        var move = MoveTest(startOs);

        Send(0);
        var (resetEff, resetOs) = Query();
        if (resetEff != resetOs) Fail($"reset: effective {resetEff} != os {resetOs}");
        CheckBounds(_w, _w.Surface, _w.Logical, resetOs, "reset");
        if (Math.Abs(_w.Scale - 1.0) > 1e-9) Fail($"reset: transform scale {_w.Scale}");
        if (startedWithOverride) Send(startEff);

        bool pass = _failures.Count == 0;
        Program.WriteResult(_o.ResultPath, new
        {
            harness = "wpf",
            mode = "selftest",
            pass,
            failures = _failures,
            environment = new
            {
                os = Environment.OSVersion.VersionString,
                runtime = RuntimeInformation.FrameworkDescription,
                awareness = Native.AwarenessName(Native.GetWindowDpiAwarenessContext(_w.Hwnd)),
                systemDpi = (int)Native.GetDpiForSystem(),
                osDpi = startOs,
                envAppScaleDpi = Environment.GetEnvironmentVariable(Native.StartupDpiEnvironmentVariable),
                monitors = Native.Monitors().Select(m => new { m.Device, m.Dpi, m.Primary, bounds = $"{m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height}" }).ToArray(),
            },
            startDpi = startEff,
            startedWithOverride,
            rounds = _o.Rounds,
            dpis = Dpis,
            steps,
            bounds = new { checks = _boundsChecks, failed = _boundsFailed, maxAbsErrorPx = _maxErr, tolerancePx = 1 },
            hitTest = new { total = _hitTotal, failedInputHitTest = _hitFailInput, failedVisualTreeHelperHitTest = _hitFailVisual, failedScreenRoundTrip = _hitFailScreen },
            drift = new { roundsChecked = _o.Rounds, mismatches = driftMismatches, comparedKeys = baseline.Keys },
            timing = new { layoutMs = Stats(_layoutMs), renderPumpMs = Stats(_renderMs), totalCycleSeconds = Math.Round(cycleSeconds, 2) },
            popupFollowChecks = new { checks = _popupChecks, failed = _popupFailed, tolerancePx = 2, note = "popup HWND size at eff vs MulDiv(size at OS DPI, eff, os)" },
            popupsWithPopupElementScale = popupsScaled,
            popupsWithoutPopupElementScale = popupsUnscaled,
            dialog,
            monitorMove = move,
            reset = new { effectiveDpi = resetEff, osDpi = resetOs },
            protocolValidation = new { setDpi50 = (long)rejLow, setDpi300 = (long)rejHigh },
        });
        Log.Write("selftest.done", new { pass, failures = _failures.Count });
        return pass ? 0 : 1;
    }
}
