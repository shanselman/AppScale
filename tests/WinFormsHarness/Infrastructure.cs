using System.Text.Json;

namespace WinFormsHarness;

internal sealed class Options
{
    public bool SelfTest { get; private set; }
    public string? ResultPath { get; private set; }
    public string? ScreenshotPath { get; private set; }
    public int? ExitAfterMs { get; private set; }
    public string? LogPath { get; private set; }
    public int Rounds { get; private set; } = 50;
    /// <summary>Stock PMv2 mode: no protocol, no APPSCALE_DPI; only real OS DPI / WM_DPICHANGED (injection target).</summary>
    public bool NoCooperative { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--selftest": o.SelfTest = true; break;
                case "--result": o.ResultPath = Next(); break;
                case "--screenshot": o.ScreenshotPath = Next(); break;
                case "--exit-after": o.ExitAfterMs = int.Parse(Next()); break;
                case "--log": o.LogPath = Next(); break;
                case "--rounds": o.Rounds = int.Parse(Next()); break;
                case "--no-cooperative": o.NoCooperative = true; break;
                default: throw new ArgumentException($"Unknown argument {a}");
            }
        }
        o.LogPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppScale", "logs", $"winforms-{Environment.ProcessId}.jsonl");
        if (o.SelfTest && o.NoCooperative) throw new ArgumentException("--selftest drives the cooperative protocol and cannot be combined with --no-cooperative");
        return o;
    }
}

/// <summary>JSON Lines diagnostics log. Awareness and scale are always separate fields.</summary>
internal static class Log
{
    private static StreamWriter? _writer;
    private static readonly object Gate = new();

    public static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex) { Console.Error.WriteLine($"log disabled: {ex.Message}"); }
    }

    public static void Write(string evt, object? data = null)
    {
        var line = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.Now.ToString("O"),
            ["harness"] = "winforms",
            ["pid"] = Environment.ProcessId,
            ["event"] = evt,
            ["data"] = data,
        };
        lock (Gate) _writer?.WriteLine(JsonSerializer.Serialize(line));
    }

    public static void Close() { lock (Gate) { _writer?.Dispose(); _writer = null; } }
}
