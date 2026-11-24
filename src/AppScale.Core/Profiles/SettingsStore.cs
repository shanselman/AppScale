using System.Text.Json;
using System.Text.Json.Serialization;
using AppScale.Core.Scaling;

namespace AppScale.Core.Profiles;

public sealed record ScaleProfile
{
    public required string ExecutableKey { get; init; }
    public string ExecutablePath { get; init; } = "";
    public int ScalePercent { get; init; } = 100;
    /// <summary>Backend id to use; null = automatic native selection.</summary>
    public string? PreferredBackend { get; init; }
    /// <summary>Profiles are disabled by default and never applied automatically unless enabled.</summary>
    public bool Enabled { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record FeatureFlags
{
    public bool VisualPreview { get; init; } = true;
    /// <summary>Injected DPI virtualization for ordinary apps. Only ever used on a window the user picked or an enabled profile.</summary>
    public bool Injection { get; init; } = true;
    public bool ExperimentalDetours { get; init; }
}

public sealed record AppScaleSettings
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Explicit opt-in. Nothing is applied until the user accepts.</summary>
    public bool OptedIn { get; init; }
    /// <summary>Emergency disable: when true every Apply fails and all overrides are reset.</summary>
    public bool EmergencyDisabled { get; init; }
    public FeatureFlags Features { get; init; } = new();
    public List<ScaleProfile> Profiles { get; init; } = [];
}

/// <summary>Local JSON settings store with atomic writes (temp file + replace). No network, no telemetry.</summary>
public sealed class SettingsStore(string path)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("APPSCALE_HOME") is { Length: > 0 } home ? home
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppScale"),
        "settings.json");

    public static SettingsStore Default() => new(DefaultPath);

    public AppScaleSettings Load()
    {
        if (!File.Exists(Path)) return new AppScaleSettings();
        try
        {
            return JsonSerializer.Deserialize<AppScaleSettings>(File.ReadAllText(Path), Json) ?? new AppScaleSettings();
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for diagnosis, fail safe to defaults (opted out, nothing enabled).
            File.Copy(Path, Path + ".corrupt", overwrite: true);
            return new AppScaleSettings();
        }
    }

    public void Save(AppScaleSettings settings)
    {
        foreach (var p in settings.Profiles)
            if (!ScaleMath.IsValidPercent(p.ScalePercent))
                throw new ArgumentOutOfRangeException(nameof(settings), $"Profile {p.ExecutableKey} has invalid scale {p.ScalePercent}%.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }

    public AppScaleSettings Update(Func<AppScaleSettings, AppScaleSettings> change)
    {
        var next = change(Load());
        Save(next);
        return next;
    }

    public AppScaleSettings UpsertProfile(ScaleProfile profile) => Update(s =>
    {
        var list = s.Profiles.Where(p => p.ExecutableKey != profile.ExecutableKey).ToList();
        list.Add(profile with { UpdatedUtc = DateTimeOffset.UtcNow });
        return s with { Profiles = list.OrderBy(p => p.ExecutableKey).ToList() };
    });

    public bool DeleteProfile(string executableKey)
    {
        bool removed = false;
        Update(s =>
        {
            var list = s.Profiles.Where(p => p.ExecutableKey != executableKey).ToList();
            removed = list.Count != s.Profiles.Count;
            return s with { Profiles = list };
        });
        return removed;
    }

    public void Export(string destination) => File.WriteAllText(destination, JsonSerializer.Serialize(Load(), Json));

    /// <summary>Deletes all AppScale settings and profiles from disk.</summary>
    public void DeleteAll()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
