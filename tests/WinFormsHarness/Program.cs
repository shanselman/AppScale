using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinFormsHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options o;
        try { o = Options.Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

        Log.Open(o.LogPath!);
        // Must run before any window is created. (The .NET-recommended alternative is the
        // ApplicationHighDpiMode project property; both end in SetProcessDpiAwarenessContext.)
        bool dpiOk = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        DpiState.Cooperative = !o.NoCooperative;
        // Stock mode must not read APPSCALE_DPI at all.
        string? rawEnv = DpiState.Cooperative ? Environment.GetEnvironmentVariable(Native.StartupDpiEnvironmentVariable) : null;
        int? startup = DpiState.Cooperative ? Native.StartupDpi() : null;
        Log.Write("startup", new
        {
            args,
            mode = DpiState.Cooperative ? "cooperative" : "stock (--no-cooperative: no protocol, APPSCALE_DPI not read)",
            setHighDpiModeResult = dpiOk,
            highDpiMode = Application.HighDpiMode.ToString(),
            threadAwareness = Native.AwarenessName(Native.GetThreadDpiAwarenessContext()),
            systemDpi = (int)Native.GetDpiForSystem(),
            envAppScaleDpi = rawEnv,
            startupOverrideDpi = startup,
            os = Environment.OSVersion.VersionString,
            runtime = RuntimeInformation.FrameworkDescription,
        });
        if (startup is int s) DpiState.SetOverride(s, "env");

        int exitCode = 0;
        var form = new MainForm();
        if (o.ExitAfterMs is int ms)
        {
            var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, ms) };
            t.Tick += (_, _) => { t.Stop(); form.Close(); };
            t.Start();
        }
        if (o.SelfTest)
        {
            form.Shown += (_, _) => form.BeginInvoke(() =>
            {
                try { exitCode = new SelfTest(form, o).Run(); }
                catch (Exception ex) { Log.Write("selftest.crash", ex.ToString()); WriteResult(o.ResultPath, new { harness = "winforms", pass = false, error = ex.ToString() }); exitCode = 3; }
                form.Close();
            });
        }
        else if (o.ScreenshotPath is string shot)
        {
            form.Shown += (_, _) =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 400 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    try { exitCode = Screenshot.Capture(form, shot, o.ResultPath); }
                    catch (Exception ex) { Log.Write("screenshot.crash", ex.ToString()); exitCode = 3; }
                    form.Close();
                };
                t.Start();
            };
        }
        Application.Run(form);
        Log.Write("exit", new { exitCode });
        Log.Close();
        return exitCode;
    }

    public static void WriteResult(string? path, object result)
    {
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        if (path is null) { Console.WriteLine(json); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
    }
}

internal static class Screenshot
{
    /// <summary>Captures the whole top-level window (including the OS-drawn caption) with PrintWindow.</summary>
    public static int Capture(MainForm form, string path, string? resultPath)
    {
        form.Refresh();
        Application.DoEvents();
        Native.GetWindowRect(form.Handle, out var wr);
        using var full = new Bitmap(wr.Width, wr.Height);
        using (var g = Graphics.FromImage(full))
        {
            nint hdc = g.GetHdc();
            try { Native.PrintWindow(form.Handle, hdc, Native.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        // Crop the invisible resize borders: keep only the DWM extended frame bounds.
        Rectangle crop = new(0, 0, wr.Width, wr.Height);
        if (Native.DwmGetWindowAttribute(form.Handle, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var fb, Marshal.SizeOf<Native.RECT>()) == 0)
            crop = Rectangle.Intersect(crop, new Rectangle(fb.Left - wr.Left, fb.Top - wr.Top, fb.Width, fb.Height));
        using var cropped = full.Clone(crop, full.PixelFormat);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        cropped.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        var info = new
        {
            harness = "winforms",
            mode = "screenshot",
            path = Path.GetFullPath(path),
            method = "PrintWindow(PW_RENDERFULLCONTENT), cropped to DWMWA_EXTENDED_FRAME_BOUNDS",
            imageSize = new { cropped.Width, cropped.Height },
            state = form.Snapshot("screenshot"),
            knownLabelPx = new { form.KnownLabel.Width, form.KnownLabel.Height },
        };
        Log.Write("screenshot", info);
        if (resultPath != null) Program.WriteResult(resultPath, info);
        return 0;
    }
}
