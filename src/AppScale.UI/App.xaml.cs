using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppScale.Core.Profiles;
using AppScale.UI.Interop;
using AppScale.UI.Preview;
using AppScale.UI.ViewModels;

namespace AppScale.UI;

public partial class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _main;
    private Mutex? _singleInstance;
    private bool _trayHintShown, _exiting;
    private StreamWriter? _out;

    public MainViewModel? ViewModel => _main?.ViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Arg("--out") is { } outPath) _out = new StreamWriter(outPath, append: false) { AutoFlush = true };
        if (args.Contains("--smoke") || args.Contains("--screenshot") || args.Contains("--selftest-apply")) Native.AttachConsole(-1);

        DispatcherUnhandledException += (_, ex) =>
        {
            WriteLine($"Unhandled: {ex.Exception}");
            try { ViewModel?.Controller.ResetAll(); } catch { /* best effort safe reset */ }
            if (args.Length > 0) { ex.Handled = true; Shutdown(3); }
        };

        try
        {
            if (args.Contains("--smoke")) RunSmoke();
            else if (args.Contains("--dummy-target")) RunDummyTarget();
            else if (Arg("--screenshot") is { } shot) RunScreenshot(shot, Arg("--tab"));
            else if (Arg("--selftest-apply") is { } exe) _ = new SelfTest(this, exe, WriteLine).RunAsync();
            else RunInteractive();
        }
        catch (Exception ex)
        {
            WriteLine($"FAILED: {ex}");
            Shutdown(1);
        }
    }

    internal void WriteLine(string text)
    {
        try { Console.WriteLine(text); } catch { /* no console */ }
        _out?.WriteLine(text);
        Debug.WriteLine(text);
    }

    internal MainWindow CreateMainWindow(bool redact, bool allowClose, bool runWatcher = false)
    {
        var window = new MainWindow { AllowClose = allowClose };
        var vm = new MainViewModel(SettingsStore.Default(), new DwmPreviewHost(), window, redact, runWatcher);
        window.DataContext = vm;
        _main = window;
        return window;
    }

    private void RunInteractive()
    {
        _singleInstance = new Mutex(true, @"Local\AppScale.UI.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("AppScale is already running (see the notification area).", "AppScale", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        var w = CreateMainWindow(redact: false, allowClose: false, runWatcher: true);
        var vm = w.ViewModel;
        _tray = new TrayIcon(OpenMain, () => vm.ResetAll(), () => { OpenMain(); vm.EmergencyCommand.Execute(null); }, ExitApp);
        _tray.SetStatus(vm.WatchingText);
        vm.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(MainViewModel.WatchingText)) _tray?.SetStatus(vm.WatchingText); };
        vm.AutoApplied += row => _tray?.ShowBalloon(
            row.Succeeded ? $"AppScale applied {row.ExecutableName}" : $"AppScale could not scale {row.ExecutableName}",
            row.Succeeded ? $"{row.DpiText} via {row.Backend}" : row.Detail, !row.Succeeded);
        SessionEnding += (_, _) => SafeReset("session ending");
        w.ViewModel.AddLog($"AppScale started. Settings: {w.ViewModel.SettingsPath}", false);
        w.ViewModel.RefreshWindows();
        w.Show();
    }

    private void OpenMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowTrayHintOnce()
    {
        if (_trayHintShown || _tray is null) return;
        _trayHintShown = true;
        _tray.ShowBalloon("AppScale is still running", "Use the tray icon to reopen, reset all, emergency disable or exit.");
    }

    private void SafeReset(string why)
    {
        if (ViewModel is not { } vm) return;
        vm.Watcher.Stop();
        var results = vm.Watcher.ResetAll().Concat(vm.Controller.ResetAll()).ToList();
        vm.PreviewHost.CloseAll();
        WriteLine($"Safe reset ({why}): {results.Count} override(s) reset. " + string.Join("; ", results));
    }

    /// <summary>Tray "Exit": always resets everything we applied before quitting.</summary>
    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        SafeReset("exit");
        _tray?.Dispose();
        _tray = null;
        if (_main is not null) { _main.AllowClose = true; _main.Close(); }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting) SafeReset("process exit");
        _tray?.Dispose();
        _singleInstance?.Dispose();
        _out?.Dispose();
        base.OnExit(e);
    }

    // ---------- automation switches ----------

    private void RunSmoke()
    {
        var sw = Stopwatch.StartNew();
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        watchdog.Tick += (_, _) => { WriteLine("SMOKE FAILED: timeout"); Shutdown(2); };
        watchdog.Start();

        var w = CreateMainWindow(redact: true, allowClose: true);
        var vm = w.ViewModel;
        vm.RefreshWindows();
        // Build the visual tree and bindings without showing anything.
        w.Measure(new Size(1280, 860));
        w.Arrange(new Rect(0, 0, 1280, 860));
        w.UpdateLayout();
        using (new TrayIcon(() => { }, () => { }, () => { }, () => { })) { }
        vm.UpdateStatus();
        WriteLine($"SMOKE OK: settings={vm.SettingsPath} optedIn={vm.OptedIn} windows={vm.Windows.Count} " +
                  $"cooperative={vm.Windows.Count(r => r.Target.IsCooperative)} excluded={vm.Windows.Count(r => r.IsExcluded)} " +
                  $"backends=[{string.Join(", ", vm.Controller.Backends.Select(b => b.Id))}] elapsed={sw.ElapsedMilliseconds}ms");
        w.Close();
        Shutdown(0);
    }

    /// <summary>A plain, NON-cooperative window owned by AppScale, used only as a self-test target.</summary>
    private void RunDummyTarget()
    {
        var w = new Window
        {
            Title = "AppScale self-test non-cooperative target", Width = 480, Height = 300,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Non-cooperative test window\n(does not implement the AppScale protocol)", FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            },
        };
        w.Closed += (_, _) => Shutdown(0);
        w.Show();
    }

    private void RunScreenshot(string path, string? tab)
    {
        bool watch = Environment.GetCommandLineArgs().Contains("--watch");
        var w = CreateMainWindow(redact: true, allowClose: true, runWatcher: watch);
        var vm = w.ViewModel;
        vm.RefreshWindows();
        vm.SelectedWindow = vm.Windows.FirstOrDefault(r => r.Target.IsCooperative) ?? vm.Windows.FirstOrDefault(r => !r.IsExcluded);
        vm.ScalePercent = 200;
        w.Height = 1060; // tall enough that the Actions card is visible in the capture
        vm.AddLog("Screenshot mode: titles and paths of non-cooperative windows are hidden.", false);
        if (int.TryParse(tab, out int tabIndex)) w.Tabs.SelectedIndex = tabIndex;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ContentRendered += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (watch)
            {
                for (int i = 0; i < 30 && !vm.AppliedRows.Any(); i++) await System.Threading.Tasks.Task.Delay(200);
                await System.Threading.Tasks.Task.Delay(300);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            try
            {
                SaveScreenshot(w, path);
                WriteLine($"SCREENSHOT OK: {Path.GetFullPath(path)}");
                w.Close();
                Shutdown(0);
            }
            catch (Exception ex) { WriteLine($"SCREENSHOT FAILED: {ex.Message}"); Shutdown(1); }
        };
        w.Show();
    }

    internal static void SaveScreenshot(MainWindow w, string path)
    {
        var root = w.Root;
        var dpi = VisualTreeHelper.GetDpi(root);
        int pw = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), ph = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
