using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinFormsHarness;

/// <summary>
/// App-wide effective-DPI state. The override is absolute (72..240) and applies to every open
/// cooperative window of the process; <c>null</c> means "follow the OS DPI of each window".
/// </summary>
internal static class DpiState
{
    public static int? Override { get; private set; }
    /// <summary>False with --no-cooperative: no protocol, no override, stock WinForms PMv2 scaling only.</summary>
    public static bool Cooperative { get; set; } = true;
    public static readonly List<CooperativeForm> OpenForms = new();

    public static void SetOverride(int? dpi, string source)
    {
        Override = dpi;
        Log.Write("override", new { requestedDpi = dpi, source });
        foreach (var f in OpenForms.ToArray()) f.ApplyEffectiveDpi(source);
    }
}

/// <summary>
/// Base form implementing cooperative protocol v1 (docs/adr/0002-cooperative-protocol.md).
/// Subclasses describe their UI in 96-DPI logical units once; <see cref="ApplyLayout"/> always derives
/// physical bounds/fonts from that snapshot, so repeated changes cannot accumulate rounding drift.
/// </summary>
internal abstract class CooperativeForm : Form
{
    public static readonly uint SetDpiMsg = Native.RegisterWindowMessage(Native.SetDpiMessageName);
    public static readonly uint GetDpiMsg = Native.RegisterWindowMessage(Native.GetDpiMessageName);

    private readonly Dictionary<Control, Rectangle> _logicalBounds = new();
    private readonly Dictionary<object, (string Family, float Points, FontStyle Style)> _logicalFonts = new();
    private static readonly Dictionary<(string, float, FontStyle, int), Font> FontCache = new();

    public int LastAppliedDpi { get; private set; }
    public double LastApplyMs { get; private set; }
    public int ProtocolMessagesReceived { get; private set; }
    public int DpiChangedCount { get; private set; }

    protected CooperativeForm()
    {
        // Stock mode: subclasses set these inside their SuspendLayout block, as the designer does, so WinForms
        // auto-scales the 96-DPI design once all children exist. (Set here, the scale runs before any child exists.)
        if (!DpiState.Cooperative) return;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>Designer-equivalent autoscale setup for stock mode; call inside SuspendLayout/ResumeLayout.</summary>
    protected void StockAutoScale()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public int OsDpi => IsHandleCreated ? (int)Native.GetDpiForWindow(Handle) : (int)Native.GetDpiForSystem();
    public int EffectiveDpi => DpiState.Override ?? OsDpi;

    public IReadOnlyDictionary<Control, Rectangle> LogicalBounds => _logicalBounds;

    /// <summary>Records the ORIGINAL logical (96-DPI) bounds of a control. Called once per control.</summary>
    protected T Place<T>(T control, int x, int y, int w, int h) where T : Control
    {
        _logicalBounds[control] = new Rectangle(x, y, w, h);
        control.Bounds = new Rectangle(x, y, w, h);
        return control;
    }

    /// <summary>Records the ORIGINAL logical font (points at 96 DPI) for a control or ToolStrip dropdown.</summary>
    protected void LogicalFont(object target, string family, float points, FontStyle style = FontStyle.Regular) =>
        _logicalFonts[target] = (family, points, style);

    /// <summary>
    /// GDI+ converts point sizes to pixels with the system DPI (screen DC), so to get a font whose pixel height
    /// is points × eff / 72 we create it at points × eff / systemDpi (the same convention WinForms uses for
    /// per-monitor font scaling).
    /// </summary>
    public static Font ScaledFont(string family, float points, FontStyle style, int effectiveDpi)
    {
        var key = (family, points, style, effectiveDpi);
        if (!FontCache.TryGetValue(key, out var f))
        {
            float sys = Native.GetDpiForSystem();
            f = new Font(family, points * effectiveDpi / sys, style, GraphicsUnit.Point);
            FontCache[key] = f;
        }
        return f;
    }

    public Font FontFor(object target, int effectiveDpi)
    {
        var (fam, pt, st) = _logicalFonts[target];
        return ScaledFont(fam, pt, st, effectiveDpi);
    }

    public (string Family, float Points, FontStyle Style) LogicalFontOf(object target) => _logicalFonts[target];

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DpiState.Cooperative) Native.SetProp(Handle, Native.PropertyName, 1);
        if (!DpiState.OpenForms.Contains(this)) DpiState.OpenForms.Add(this);
        Log.Write("window.created", Snapshot("handleCreated"));
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (DpiState.Cooperative) Native.RemoveProp(Handle, Native.PropertyName);
        DpiState.OpenForms.Remove(this);
        base.OnHandleDestroyed(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e); // stock mode: WinForms AutoScaleMode.Dpi scales the 96-DPI design to DeviceDpi here
        // Lay out at the effective DPI before the first frame is shown.
        if (DpiState.Cooperative) ApplyEffectiveDpi("load");
        else Log.Write("stock.observed", Snapshot("load"));
    }

    protected override void WndProc(ref Message m)
    {
        if (!DpiState.Cooperative)
        {
            // Stock PMv2 app: no protocol messages; WinForms alone handles DPI. Log what it did.
            if (m.Msg == Native.WM_DPICHANGED)
            {
                DpiChangedCount++;
                var before = Snapshot("wm_dpichanged.before");
                var rc = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                base.WndProc(ref m);
                Log.Write("wm.dpichanged", new { wParamX = (int)(m.WParam & 0xFFFF), wParamY = (int)((m.WParam >> 16) & 0xFFFF), suggestedRect = new { rc.Left, rc.Top, rc.Width, rc.Height }, before });
                Log.Write("stock.observed", Snapshot("wm_dpichanged.after"));
                return;
            }
            if (m.Msg is Native.WM_DPICHANGED_BEFOREPARENT or Native.WM_DPICHANGED_AFTERPARENT or Native.WM_GETDPISCALEDSIZE)
                Log.Write("wm.dpi-related", new { msg = $"0x{m.Msg:X4}", wParam = (long)m.WParam, getDpiForWindow = OsDpi, deviceDpi = DeviceDpi });
            base.WndProc(ref m);
            return;
        }
        if (m.Msg == (int)SetDpiMsg && SetDpiMsg != 0)
        {
            ProtocolMessagesReceived++;
            int dpi = (int)m.WParam;
            bool ok = dpi == 0 || dpi is >= Native.MinDpi and <= Native.MaxDpi;
            Log.Write("protocol.setDpi", new { wParam = dpi, accepted = ok, osDpi = OsDpi });
            if (ok) DpiState.SetOverride(dpi == 0 ? null : dpi, "protocol");
            m.Result = ok ? 1 : 0;
            return;
        }
        if (m.Msg == (int)GetDpiMsg && GetDpiMsg != 0)
        {
            ProtocolMessagesReceived++;
            m.Result = Native.MakeLong(EffectiveDpi, OsDpi);
            Log.Write("protocol.getDpi", new { effectiveDpi = EffectiveDpi, osDpi = OsDpi });
            return;
        }
        if (m.Msg == Native.WM_DPICHANGED)
        {
            int before = LastAppliedDpi;
            DpiChangedCount++;
            // Let WinForms do its PerMonitorV2 work (it moves/sizes to the suggested RECT and rescales by
            // newOs/oldOs). We then re-derive everything from the logical snapshot: position from the
            // suggested RECT is kept, size follows the absolute effective DPI.
            base.WndProc(ref m);
            Log.Write("wm.dpichanged", new { newOsDpi = (int)(m.WParam & 0xFFFF), previousEffectiveDpi = before, overrideDpi = DpiState.Override });
            ApplyEffectiveDpi("wm_dpichanged");
            return;
        }
        base.WndProc(ref m);
    }

    public void ApplyEffectiveDpi(string reason)
    {
        if (!IsHandleCreated) return;
        int eff = EffectiveDpi;
        var sw = Stopwatch.StartNew();
        SuspendLayout();
        try
        {
            foreach (var (target, _) in _logicalFonts)
            {
                var font = FontFor(target, eff);
                switch (target)
                {
                    case Control c when !ReferenceEquals(c.Font, font): c.Font = font; break;
                    case ToolStripDropDown d when !ReferenceEquals(d.Font, font): d.Font = font; break;
                }
            }
            foreach (var (c, r) in _logicalBounds)
            {
                var target = new Rectangle(Native.MulDiv(r.X, eff, 96), Native.MulDiv(r.Y, eff, 96),
                                           Native.MulDiv(r.Width, eff, 96), Native.MulDiv(r.Height, eff, 96));
                if (c.Bounds != target) c.Bounds = target;
            }
            ApplyLayout(eff);
        }
        finally { ResumeLayout(true); }
        ClientSize = ComputeClientSize(eff);
        sw.Stop();
        LastAppliedDpi = eff;
        LastApplyMs = sw.Elapsed.TotalMilliseconds;
        Log.Write("dpi.applied", Snapshot(reason));
    }

    /// <summary>Framework-specific extras (ListView columns, strips) that are not plain bounds/fonts.</summary>
    protected abstract void ApplyLayout(int effectiveDpi);

    protected abstract Size ComputeClientSize(int effectiveDpi);

    public object Snapshot(string reason)
    {
        Native.GetWindowRect(Handle, out var wr);
        Native.GetClientRect(Handle, out var cr);
        return new
        {
            reason,
            window = GetType().Name,
            hwnd = $"0x{Handle:X}",
            osDpi = OsDpi,
            effectiveDpi = EffectiveDpi,
            overrideDpi = DpiState.Override,
            systemDpi = (int)Native.GetDpiForSystem(),
            deviceDpi = DeviceDpi,
            awareness = Native.AwarenessName(Native.GetWindowDpiAwarenessContext(Handle)),
            threadAwareness = Native.AwarenessName(Native.GetThreadDpiAwarenessContext()),
            scalePercent = Math.Round(EffectiveDpi * 100.0 / 96, 1),
            windowRect = new { wr.Left, wr.Top, wr.Width, wr.Height },
            clientRect = new { cr.Width, cr.Height },
            applyMs = Math.Round(LastApplyMs, 3),
            mode = DpiState.Cooperative ? "cooperative" : "stock",
            formFont = FontInfo(Font),
            metrics = Metrics(),
        };
    }

    /// <summary>Framework-reported font metrics: point size, pixel height, and GDI LOGFONT height.</summary>
    public static object FontInfo(Font f)
    {
        var lf = new LOGFONT();
        f.ToLogFont(lf);
        return new { f.Name, sizeInPoints = Math.Round(f.SizeInPoints, 3), unit = f.Unit.ToString(), heightPx = f.Height, lfHeight = lf.lfHeight };
    }

    /// <summary>Per-window layout metrics for comparing cooperative vs stock/injected runs.</summary>
    protected virtual object? Metrics() => null;
}
