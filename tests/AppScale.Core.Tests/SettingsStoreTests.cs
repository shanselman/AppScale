using AppScale.Core.Profiles;
using AppScale.Core.Targets;

namespace AppScale.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("appscale-store-").FullName;
    private string FilePath => Path.Combine(_dir, "sub", "settings.json");
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void DefaultsAreSafe()
    {
        var s = new SettingsStore(FilePath).Load();
        Assert.False(s.OptedIn);
        Assert.False(s.EmergencyDisabled);
        Assert.False(s.Features.ExperimentalDetours);
        Assert.Empty(s.Profiles);
        Assert.False(new ScaleProfile { ExecutableKey = "x" }.Enabled);
    }

    [Fact]
    public void RoundTripsProfiles()
    {
        var store = new SettingsStore(FilePath);
        var id = new ExecutableIdentity(@"C:\Apps\Foo.exe");
        store.Update(s => s with { OptedIn = true, Features = s.Features with { ExperimentalDetours = true } });
        store.UpsertProfile(new ScaleProfile { ExecutableKey = id.Key, ExecutablePath = id.FullPath, ScalePercent = 175, PreferredBackend = "cooperative", Enabled = true });

        var loaded = new SettingsStore(FilePath).Load();
        Assert.True(loaded.OptedIn);
        Assert.True(loaded.Features.ExperimentalDetours);
        var p = Assert.Single(loaded.Profiles);
        Assert.Equal(@"c:\apps\foo.exe", p.ExecutableKey);
        Assert.Equal(175, p.ScalePercent);
        Assert.Equal("cooperative", p.PreferredBackend);
        Assert.True(p.Enabled);
    }

    [Fact]
    public void UpsertReplacesByExecutableKey()
    {
        var store = new SettingsStore(FilePath);
        store.UpsertProfile(new ScaleProfile { ExecutableKey = "a", ScalePercent = 125 });
        store.UpsertProfile(new ScaleProfile { ExecutableKey = "a", ScalePercent = 200 });
        store.UpsertProfile(new ScaleProfile { ExecutableKey = "b", ScalePercent = 75 });
        var profiles = store.Load().Profiles;
        Assert.Equal(2, profiles.Count);
        Assert.Equal(200, profiles.Single(p => p.ExecutableKey == "a").ScalePercent);
    }

    [Fact]
    public void ExecutableKeyIsCaseInsensitive() =>
        Assert.Equal(new ExecutableIdentity(@"C:\APPS\Foo.EXE").Key, new ExecutableIdentity(@"c:\apps\foo.exe").Key);

    [Fact]
    public void SaveIsAtomicAndLeavesNoTempFile()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppScaleSettings { OptedIn = true });
        store.Save(new AppScaleSettings { OptedIn = false });
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.False(store.Load().OptedIn);
    }

    [Fact]
    public void InvalidScaleIsNotPersisted()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppScaleSettings { OptedIn = true });
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpsertProfile(new ScaleProfile { ExecutableKey = "x", ScalePercent = 500 }));
        Assert.True(store.Load().OptedIn);
        Assert.Empty(store.Load().Profiles);
    }

    [Fact]
    public void CorruptFileFailsSafeAndIsPreserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        var s = new SettingsStore(FilePath).Load();
        Assert.False(s.OptedIn);
        Assert.True(File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void DeleteExportAndDeleteAll()
    {
        var store = new SettingsStore(FilePath);
        store.UpsertProfile(new ScaleProfile { ExecutableKey = "a", ScalePercent = 125 });
        var export = Path.Combine(_dir, "export.json");
        store.Export(export);
        Assert.Contains("\"executableKey\": \"a\"", File.ReadAllText(export));
        Assert.True(store.DeleteProfile("a"));
        Assert.False(store.DeleteProfile("a"));
        store.DeleteAll();
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void SerializationUsesStableCamelCase()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppScaleSettings { OptedIn = true });
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"optedIn\": true", json);
        Assert.Contains("\"visualPreview\": true", json);
    }
}
