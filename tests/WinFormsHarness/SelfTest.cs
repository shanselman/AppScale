using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinFormsHarness;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal sealed class LOGFONT
{
    public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
    public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName = "";
}

/// <summary>
/// Unattended self-test. Every DPI change goes through SendMessage(AppScale.SetDpi.v1) — the same
/// WndProc path an external controller uses.
/// </summary>
internal sealed class SelfTest
{
    private static readonly int[] Dpis = { 72, 96, 120, 144, 192, 240 };
    private readonly MainForm _f;
    private readonly Options _o;
    private readonly List<string> _failures = new();
    private readonly List<double> _layoutMs = new(), _paintMs = new();
    private int _boundsChecks, _boundsFailed, _hitTotal, _hitFailCwp, _hitFailGcap, _wfpTotal, _wfpOk, _fontChecks, _fontFailed;
    private int _maxErr;
    private readonly List<string> _wfpMiss = new();
    private static uint Pid(nint h) { Native.GetWindowThreadProcessId(h, out uint p); return p; }

    public SelfTest(MainForm form, Options options) { _f = form; _o = options; }

    private Control[] KeyControls => new Control[] { _f.KnownLabel, _f.Text1, _f.Check, _f.Combo, _f.ApplyButton, _f.DialogButton, _f.List };

    private void Fail(string msg) { if (_failures.Count < 50) _failures.Add(msg); Log.Write("selftest.failure", msg); }

    private nint Send(int dpi)
    {
        var sw = Stopwatch.StartNew();
        nint r = Native.SendMessage(_f.Handle, CooperativeForm.SetDpiMsg, dpi, 0);
        double layout = sw.Elapsed.TotalMilliseconds;
        _f.Update();
        _layoutMs.Add(layout);
        _paintMs.Add(sw.Elapsed.TotalMilliseconds - layout);
        return r;
    }

    private (int eff, int os) Query()
    {
        long r = Native.SendMessage(_f.Handle, CooperativeForm.GetDpiMsg, 0, 0);
        return ((int)(r & 0xFFFF), (int)((r >> 16) & 0xFFFF));
    }

    private static Rectangle PhysicalRelativeTo(Control c, Control parent)
    {
        Native.GetWindowRect(c.Handle, out var r);
        Native.MapWindowPoints(0, parent.Handle, ref r, 2);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private Dictionary<string, Rectangle> Measure()
    {
        var d = new Dictionary<string, Rectangle>();
        foreach (var c in KeyControls) d[c.GetType().Name + ":" + c.Text] = PhysicalRelativeTo(c, _f.Content);
        Native.GetClientRect(_f.Handle, out var cr);
        d["client"] = new Rectangle(0, 0, cr.Width, cr.Height);
        Native.GetWindowRect(_f.Handle, out var wr);
        d["windowSize"] = new Rectangle(0, 0, wr.Width, wr.Height);
        return d;
    }

    private void CheckBounds(Control owner, IEnumerable<Control> controls, int eff, string ctx)
    {
        var logical = ((CooperativeForm)owner.FindForm()!).LogicalBounds;
        foreach (var c in controls)
        {
            var l = logical[c];
            var p = PhysicalRelativeTo(c, owner);
            var exp = new[] { Native.MulDiv(l.X, eff, 96), Native.MulDiv(l.Y, eff, 96), Native.MulDiv(l.Width, eff, 96), Native.MulDiv(l.Height, eff, 96) };
            var act = new[] { p.X, p.Y, p.Width, p.Height };
            int n = c is ComboBox ? 3 : 4; // DropDownList height is font-driven, not settable.
            for (int i = 0; i < n; i++)
            {
                _boundsChecks++;
                int err = Math.Abs(exp[i] - act[i]);
                _maxErr = Math.Max(_maxErr, err);
                if (err > 1) { _boundsFailed++; Fail($"{ctx} eff={eff} {c.GetType().Name} '{c.Text}' {"xywh"[i]} expected {exp[i]} got {act[i]}"); }
            }
        }
    }

    private void CheckFont(int eff)
    {
        var lf = new LOGFONT();
        _f.KnownLabel.Font.ToLogFont(lf);
        int expected = -Native.MulDiv(9, eff, 72);
        _fontChecks++;
        if (Math.Abs(lf.lfHeight - expected) > 1) { _fontFailed++; Fail($"font eff={eff} lfHeight expected {expected} got {lf.lfHeight}"); }
    }

    private static Control DeepestChildAt(Control root, Point screen)
    {
        Control cur = root;
        while (true)
        {
            var ch = cur.GetChildAtPoint(cur.PointToClient(screen), GetChildAtPointSkip.Invisible);
            if (ch is null) return cur;
            cur = ch;
        }
    }

    private static nint DeepestHwndAt(nint top, Point screen)
    {
        nint cur = top;
        while (true)
        {
            var p = new Native.POINT(screen.X, screen.Y);
            Native.ScreenToClient(cur, ref p);
            nint child = Native.ChildWindowFromPointEx(cur, p, Native.CWP_SKIPINVISIBLE | Native.CWP_SKIPTRANSPARENT);
            if (child == 0 || child == cur) return cur;
            cur = child;
        }
    }

    private void CheckHits(int eff)
    {
        foreach (var c in KeyControls)
        {
            Native.GetWindowRect(c.Handle, out var r);
            var centre = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            _hitTotal++;
            nint h = DeepestHwndAt(_f.Handle, centre);
            if (h != c.Handle && !Native.IsChild(c.Handle, h)) { _hitFailCwp++; Fail($"hit(ChildWindowFromPointEx) eff={eff} {c.GetType().Name} -> {Native.ClassName(h)}"); }
            var gc = DeepestChildAt(_f, centre);
            if (gc != c && !c.Contains(gc)) { _hitFailGcap++; Fail($"hit(GetChildAtPoint) eff={eff} {c.GetType().Name} -> {gc.GetType().Name}"); }
            if (Screen.AllScreens.Any(s => s.Bounds.Contains(centre)))
            {
                _wfpTotal++;
                nint w = Native.WindowFromPoint(new Native.POINT(centre.X, centre.Y));
                if (w == c.Handle || Native.IsChild(c.Handle, w)) _wfpOk++;
                else if (_wfpMiss.Count < 5) _wfpMiss.Add($"{c.GetType().Name} -> {Native.ClassName(w)} pid={Pid(w)}");
            }
        }
    }

    private nint FindVisibleTooltip()
    {
        nint found = 0;
        Native.EnumThreadWindows(Native.GetCurrentThreadId(), (h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.ClassName(h).Contains("tooltips_class32")) { found = h; return false; }
            return true;
        }, 0);
        return found;
    }

    private static void Pump(int ms = 60)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private object MeasurePopups(int dpi)
    {
        Send(dpi);
        var (eff, os) = Query();
        // Context menu (ToolStripDropDown, its own top-level HWND).
        _f.Context.Show(_f.Content, new Point(10, 10));
        Pump();
        Native.GetWindowRect(_f.Context.Handle, out var cm);
        int cmItem = _f.Context.Items[0].Height;
        int cmDeviceDpi = _f.Context.DeviceDpi;
        _f.Context.Close();
        // Menu dropdown.
        _f.FileMenu.ShowDropDown();
        Pump();
        Native.GetWindowRect(_f.FileMenu.DropDown.Handle, out var dd);
        int ddItem = _f.FileMenu.DropDownItems[0].Height;
        _f.FileMenu.HideDropDown();
        Pump(20);
        // Owner-drawn tooltip vs native tooltip.
        _f.Tip.Show("Tooltip drawn with the effective-DPI font", _f.ApplyButton, 0, _f.ApplyButton.Height, 3000);
        Pump();
        nint tipH = FindVisibleTooltip();
        Native.RECT tip = default; if (tipH != 0) Native.GetWindowRect(tipH, out tip);
        _f.Tip.Hide(_f.ApplyButton);
        Pump(20);
        _f.NativeTip.Show("Native tooltip (OS tooltip font)", _f.DialogButton, 0, _f.DialogButton.Height, 3000);
        Pump();
        nint ntipH = FindVisibleTooltip();
        Native.RECT ntip = default; if (ntipH != 0) Native.GetWindowRect(ntipH, out ntip);
        _f.NativeTip.Hide(_f.DialogButton);
        Pump(20);
        // ListView vertical scrollbar (OS-drawn, sized from system metrics).
        var sbi = new Native.SCROLLBARINFO { cbSize = Marshal.SizeOf<Native.SCROLLBARINFO>() };
        int sbw = Native.GetScrollBarInfo(_f.List.Handle, Native.OBJID_VSCROLL, ref sbi) ? sbi.rcScrollBar.Width : -1;
        return new
        {
            effectiveDpi = eff,
            osDpi = os,
            contextMenu = new { width = cm.Width, height = cm.Height, itemHeight = cmItem, deviceDpi = cmDeviceDpi },
            fileDropDown = new { width = dd.Width, height = dd.Height, itemHeight = ddItem },
            ownerDrawTooltip = tipH == 0 ? null : new { width = tip.Width, height = tip.Height },
            nativeTooltip = ntipH == 0 ? null : new { width = ntip.Width, height = ntip.Height },
            listViewRowHeight = _f.List.GetItemRect(0).Height,
            listViewVScrollWidth = sbw,
            systemVScrollWidthAtOsDpi = Native.GetSystemMetricsForDpi(Native.SM_CXVSCROLL, (uint)os),
            systemVScrollWidthAtEffDpi = Native.GetSystemMetricsForDpi(Native.SM_CXVSCROLL, (uint)eff),
            menuStripHeight = _f.MenuBar.Height,
            toolStripHeight = _f.Tools.Height,
            comboHeight = _f.Combo.Height,
        };
    }

    private object MeasureDialog(int dpi)
    {
        Send(dpi);
        object? result = null;
        using var dlg = new DialogForm();
        dlg.Shown += (_, _) => dlg.BeginInvoke(() =>
        {
            Pump(30);
            int before = _boundsFailed;
            CheckBounds(dlg, new Control[] { dlg.Message, dlg.Ok, dlg.Cancel }, dpi, "dialog");
            Native.GetClientRect(dlg.Handle, out var cr);
            bool clientOk = Math.Abs(cr.Width - Native.MulDiv(DialogForm.LogicalClient.Width, dpi, 96)) <= 1 &&
                            Math.Abs(cr.Height - Native.MulDiv(DialogForm.LogicalClient.Height, dpi, 96)) <= 1;
            if (!clientOk) Fail($"dialog client {cr.Width}x{cr.Height} at {dpi}");
            long q = Native.SendMessage(dlg.Handle, CooperativeForm.GetDpiMsg, 0, 0);
            result = new
            {
                requestedDpi = dpi,
                dialogEffectiveDpi = (int)(q & 0xFFFF),
                dialogOsDpi = (int)((q >> 16) & 0xFFFF),
                clientSize = new { cr.Width, cr.Height },
                clientOk,
                boundsOk = _boundsFailed == before,
                okButton = PhysicalRelativeTo(dlg.Ok, dlg).ToString(),
            };
            if ((int)(q & 0xFFFF) != dpi) Fail($"dialog effective {(q & 0xFFFF)} != {dpi}");
            dlg.Close();
        });
        dlg.ShowDialog(_f);
        return result ?? new { error = "dialog not measured" };
    }

    /// <summary>
    /// Investigation: the "ratio" mechanism (Control.Scale(eff/current)) applied repeatedly to the same logical
    /// layout. Measures accumulated drift versus the absolute-from-snapshot mechanism used by the harness.
    /// </summary>
    private object InvestigateScaleRatio()
    {
        using var panel = new Panel { Bounds = new Rectangle(0, 0, 582, 196) };
        var logical = _f.LogicalBounds.Values.ToArray();
        var probes = logical.Select(r => new Control { Bounds = r }).ToArray();
        panel.Controls.AddRange(probes);
        int cur = 96, maxDrift = 0, roundsWithDrift = 0;
        for (int round = 0; round < _o.Rounds; round++)
        {
            foreach (int d in Dpis.Append(96))
            {
                float k = d / (float)cur;
                panel.Scale(new SizeF(k, k));
                cur = d;
            }
            int drift = 0;
            for (int i = 0; i < probes.Length; i++)
            {
                var a = probes[i].Bounds; var l = logical[i];
                drift = Math.Max(drift, new[] { Math.Abs(a.X - l.X), Math.Abs(a.Y - l.Y), Math.Abs(a.Width - l.Width), Math.Abs(a.Height - l.Height) }.Max());
            }
            if (drift > 0) roundsWithDrift++;
            maxDrift = Math.Max(maxDrift, drift);
        }
        return new { mechanism = "Control.Scale(SizeF eff/current), repeated", rounds = _o.Rounds, roundsWithDrift, maxDriftPxAt96 = maxDrift, fontsScaled = false };
    }

    /// <summary>
    /// Genuine WM_DPICHANGED: move our own window to a monitor with a different DPI (never touches other
    /// processes or display settings). With an override active the absolute effective DPI must be kept.
    /// </summary>
    private object MoveTest(int startOs)
    {
        var target = Screen.AllScreens
            .Select(s => (s, dpi: Native.MonitorDpi(new Point(s.WorkingArea.Left + 50, s.WorkingArea.Top + 50))))
            .FirstOrDefault(t => t.dpi != 0 && t.dpi != startOs);
        if (target.s is null) return new { skipped = "no monitor with a DPI different from the start monitor" };
        var home = _f.Location;
        var steps = new List<object>();
        void Step(string name, int? overrideDpi, Point where, int expectOs)
        {
            Send(overrideDpi ?? 0);
            int before = _boundsFailed;
            int dpiChangedBefore = _f.DpiChangedCount;
            _f.Location = where;
            Pump(400);
            var (eff, os) = Query();
            int expectEff = overrideDpi ?? expectOs;
            if (os != expectOs) Fail($"move {name}: os {os} expected {expectOs}");
            if (eff != expectEff) Fail($"move {name}: effective {eff} expected {expectEff}");
            CheckBounds(_f.Content, KeyControls, expectEff, $"move {name}");
            Native.GetClientRect(_f.Handle, out var cr);
            if (Math.Abs(cr.Width - Native.MulDiv(MainForm.LogicalContent.Width, expectEff, 96)) > 1) Fail($"move {name}: client width {cr.Width}");
            steps.Add(new { name, overrideDpi, osDpi = os, effectiveDpi = eff, wmDpiChangedReceived = _f.DpiChangedCount - dpiChangedBefore, clientWidth = cr.Width, boundsOk = before == _boundsFailed });
        }
        var away = new Point(target.s.WorkingArea.Left + 40, target.s.WorkingArea.Top + 40);
        Step("override192->other", 192, away, target.dpi);
        Step("override192->home", 192, home, startOs);
        Step("noOverride->other", null, away, target.dpi);
        Step("noOverride->home", null, home, startOs);
        return new { targetMonitor = target.s.DeviceName, targetDpi = target.dpi, startOs, steps };
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
        _f.TopMost = true;
        var wa = Screen.FromControl(_f).WorkingArea;
        _f.Location = new Point(wa.Left + 16, wa.Top + 16);
        Pump(100);

        // Validation of the protocol itself.
        nint rejectedLow = Native.SendMessage(_f.Handle, CooperativeForm.SetDpiMsg, 50, 0);
        nint rejectedHigh = Native.SendMessage(_f.Handle, CooperativeForm.SetDpiMsg, 300, 0);
        if (rejectedLow != 0 || rejectedHigh != 0) Fail("out-of-range DPI was not rejected");

        var (startEff, startOs) = Query();
        int startDpi = startEff;
        bool startedWithOverride = DpiState.Override.HasValue;
        CheckBounds(_f.Content, KeyControls, startDpi, "start");
        var baseline = Measure();

        int steps = 0, driftRounds = 0, driftMismatches = 0;
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < _o.Rounds; round++)
        {
            foreach (int dpi in Dpis)
            {
                if (Send(dpi) != 1) Fail($"SetDpi({dpi}) not accepted");
                var (eff, _) = Query();
                if (eff != dpi) Fail($"GetDpi reports {eff} after SetDpi({dpi})");
                CheckBounds(_f.Content, KeyControls, dpi, $"round {round}");
                Native.GetClientRect(_f.Handle, out var cr);
                if (Math.Abs(cr.Width - Native.MulDiv(MainForm.LogicalContent.Width, dpi, 96)) > 1) Fail($"client width {cr.Width} at {dpi}");
                CheckFont(dpi);
                CheckHits(dpi);
                steps++;
            }
            // Back to the starting DPI: must be pixel-identical to the baseline.
            Send(startedWithOverride ? startDpi : 0);
            var now = Measure();
            driftRounds++;
            foreach (var (k, v) in baseline)
                if (now[k] != v) { driftMismatches++; Fail($"drift round {round}: {k} baseline {v} now {now[k]}"); }
        }
        double cycleSeconds = sw.Elapsed.TotalSeconds;

        var popups = new[] { 96, 144, 192 }.Select(MeasurePopups).ToArray();
        var dialog = MeasureDialog(192);
        var ratio = InvestigateScaleRatio();
        var move = MoveTest(startOs);

        // Reset must return to OS behaviour.
        Send(0);
        var (resetEff, resetOs) = Query();
        if (resetEff != resetOs) Fail($"reset: effective {resetEff} != os {resetOs}");
        CheckBounds(_f.Content, KeyControls, resetOs, "reset");
        if (startedWithOverride) Send(startDpi);

        bool pass = _failures.Count == 0;
        var result = new
        {
            harness = "winforms",
            mode = "selftest",
            pass,
            failures = _failures,
            environment = new
            {
                os = Environment.OSVersion.VersionString,
                runtime = RuntimeInformation.FrameworkDescription,
                highDpiMode = Application.HighDpiMode.ToString(),
                awareness = Native.AwarenessName(Native.GetWindowDpiAwarenessContext(_f.Handle)),
                systemDpi = (int)Native.GetDpiForSystem(),
                osDpi = startOs,
                envAppScaleDpi = Environment.GetEnvironmentVariable(Native.StartupDpiEnvironmentVariable),
                monitors = Screen.AllScreens.Select(s => new { s.DeviceName, s.Bounds, s.Primary }).ToArray(),
            },
            startDpi,
            startedWithOverride,
            rounds = _o.Rounds,
            dpis = Dpis,
            steps,
            bounds = new { checks = _boundsChecks, failed = _boundsFailed, maxAbsErrorPx = _maxErr, tolerancePx = 1 },
            fonts = new { checks = _fontChecks, failed = _fontFailed, rule = "LOGFONT.lfHeight == -MulDiv(9pt, eff, 72) ±1" },
            hitTest = new { total = _hitTotal, failedChildWindowFromPointEx = _hitFailCwp, failedGetChildAtPoint = _hitFailGcap, windowFromPointOnScreen = _wfpTotal, windowFromPointMatched = _wfpOk, windowFromPointMissSamples = _wfpMiss },
            drift = new { roundsChecked = driftRounds, mismatches = driftMismatches, comparedKeys = baseline.Keys },
            timing = new { layoutMs = Stats(_layoutMs), paintMs = Stats(_paintMs), totalCycleSeconds = Math.Round(cycleSeconds, 2) },
            popups,
            dialog,
            scaleRatioInvestigation = ratio,
            monitorMove = move,
            reset = new { effectiveDpi = resetEff, osDpi = resetOs },
            protocolValidation = new { setDpi50 = (long)rejectedLow, setDpi300 = (long)rejectedHigh },
        };
        Program.WriteResult(_o.ResultPath, result);
        Log.Write("selftest.done", new { pass, failures = _failures.Count });
        return pass ? 0 : 1;
    }
}
