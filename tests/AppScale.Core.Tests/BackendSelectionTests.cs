using AppScale.Core.Backends;
using AppScale.Core.Profiles;
using AppScale.Core.Safety;
using AppScale.Core.Targets;

namespace AppScale.Core.Tests;

internal sealed class FakeBackend(string id, BackendCapabilities caps, bool supported) : IScaleBackend
{
    public int Applies;
    public int Resets;
    public string Id => id;
    public string DisplayName => id;
    public BackendCapabilities Capabilities => caps;
    public ProbeResult Probe(TargetInfo target) => supported ? ProbeResult.Yes(caps, id) : ProbeResult.No(ScaleFailureCode.NotSupported, id + " no");
    public ScaleResult Apply(TargetInfo target, ScaleRequest request) { Applies++; return ScaleResult.Ok(id, request.RequestedDpi); }
    public ScaleResult Reset(TargetInfo target) { Resets++; return ScaleResult.Ok("reset " + id); }
    public BackendStatus GetStatus(TargetInfo target) => new(id, Applies > Resets, null, null, "");
}

internal static class Targets
{
    public static TargetInfo Allowed(nint hwnd = 0x1234) => new()
    {
        Hwnd = hwnd, ProcessId = 424242, Title = "Test",
        Executable = new ExecutableIdentity(@"C:\Tools\Harness\App.exe"),
        WindowDpi = 144, MonitorDpi = 144,
    };
}

public class BackendSelectorTests
{
    private static readonly BackendCapabilities Native = BackendCapabilities.NativeRendering | BackendCapabilities.LiveChange | BackendCapabilities.InputSupported;
    private static readonly BackendCapabilities Visual = BackendCapabilities.VisualOnly | BackendCapabilities.LiveChange;

    [Fact]
    public void NativeBackendWins()
    {
        var c = BackendSelector.Select([new FakeBackend("visual", Visual, true), new FakeBackend("native", Native, true)], Targets.Allowed(), allowVisualFallback: true);
        Assert.Equal("native", c.Backend?.Id);
    }

    [Fact]
    public void NeverSilentlyFallsBackToVisual()
    {
        var c = BackendSelector.Select([new FakeBackend("native", Native, false), new FakeBackend("visual", Visual, true)], Targets.Allowed(), allowVisualFallback: false);
        Assert.Null(c.Backend);
        Assert.Contains("explicitly", c.Explanation);
        Assert.DoesNotContain(c.Considered, x => x.BackendId == "visual");
    }

    [Fact]
    public void VisualOnlyWhenExplicitlyAllowed()
    {
        var c = BackendSelector.Select([new FakeBackend("native", Native, false), new FakeBackend("visual", Visual, true)], Targets.Allowed(), allowVisualFallback: true);
        Assert.Equal("visual", c.Backend?.Id);
        Assert.Contains("VISUAL", c.Explanation);
    }

    [Fact]
    public void ExperimentalNeverAutoSelected()
    {
        var exp = new FakeBackend("detours", Native | BackendCapabilities.Experimental, true);
        Assert.Null(BackendSelector.Select([exp], Targets.Allowed(), allowVisualFallback: true).Backend);
        Assert.Equal("detours", BackendSelector.Select([exp], Targets.Allowed(), false, "detours").Backend?.Id);
    }

    [Fact]
    public void PreferredVisualStillRequiresAllow()
    {
        var c = BackendSelector.Select([new FakeBackend("visual", Visual, true)], Targets.Allowed(), allowVisualFallback: false, "visual");
        Assert.Null(c.Backend);
    }

    [Fact]
    public void UnknownPreferredBackendFails() =>
        Assert.Null(BackendSelector.Select([new FakeBackend("native", Native, true)], Targets.Allowed(), false, "nope").Backend);

    [Fact]
    public void NoBackendsFailsClosed() =>
        Assert.Null(BackendSelector.Select([], Targets.Allowed(), true).Backend);
}

public class SafetyPolicyTests
{
    [Fact]
    public void AllowsOrdinaryApp() => Assert.Equal(ScaleFailureCode.None, SafetyPolicy.Check(Targets.Allowed(), out _));

    [Fact]
    public void ExcludesElevated() => Assert.Equal(ScaleFailureCode.Elevated, SafetyPolicy.Check(Targets.Allowed() with { IsElevated = true }, out _));

    [Fact]
    public void ExcludesUninspectable()
    {
        Assert.Equal(ScaleFailureCode.AccessDenied, SafetyPolicy.Check(Targets.Allowed() with { AccessDenied = true }, out _));
        Assert.Equal(ScaleFailureCode.AccessDenied, SafetyPolicy.Check(Targets.Allowed() with { Executable = null }, out _));
    }

    [Theory]
    [InlineData(@"C:\Program Files\KeePass\KeePass.exe")]
    [InlineData(@"C:\Somewhere\EXPLORER.EXE")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    public void ExcludesSecuritySensitiveAndSystem(string path) =>
        Assert.Equal(ScaleFailureCode.Excluded, SafetyPolicy.Check(Targets.Allowed() with { Executable = new ExecutableIdentity(path) }, out _));

    [Fact]
    public void ExcludesSelf() =>
        Assert.Equal(ScaleFailureCode.Excluded, SafetyPolicy.Check(Targets.Allowed() with { ProcessId = Environment.ProcessId }, out _));
}

public sealed class ScaleControllerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("appscale-ctl-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private (ScaleController, FakeBackend native, FakeBackend visual) Make(bool optIn = true, bool emergency = false, bool nativeSupported = true)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppScaleSettings { OptedIn = optIn, EmergencyDisabled = emergency });
        var n = new FakeBackend("native", BackendCapabilities.NativeRendering, nativeSupported);
        var v = new FakeBackend("visual", BackendCapabilities.VisualOnly, true);
        return (new ScaleController(store, [n, v]), n, v);
    }

    [Fact]
    public void RequiresOptIn()
    {
        var (c, n, _) = Make(optIn: false);
        Assert.Equal(ScaleFailureCode.OptInRequired, c.Apply(Targets.Allowed(), 150, false).Result.Code);
        Assert.Equal(0, n.Applies);
    }

    [Fact]
    public void EmergencyDisableBlocksApply()
    {
        var (c, n, _) = Make(emergency: true);
        Assert.Equal(ScaleFailureCode.EmergencyDisabled, c.Apply(Targets.Allowed(), 150, false).Result.Code);
        Assert.Equal(0, n.Applies);
    }

    [Fact]
    public void InvalidScaleRejected()
    {
        var (c, _, _) = Make();
        Assert.Equal(ScaleFailureCode.InvalidScale, c.Apply(Targets.Allowed(), 300, false).Result.Code);
    }

    [Fact]
    public void ExcludedTargetRejectedBeforeAnyBackend()
    {
        var (c, n, v) = Make();
        var r = c.Apply(Targets.Allowed() with { IsElevated = true }, 150, true).Result;
        Assert.Equal(ScaleFailureCode.Elevated, r.Code);
        Assert.Equal(0, n.Applies + v.Applies);
    }

    [Fact]
    public void ApplyThenResetAll()
    {
        var (c, n, _) = Make();
        Assert.True(c.Apply(Targets.Allowed(1), 150, false).Result.Succeeded);
        Assert.True(c.Apply(Targets.Allowed(2), 200, false).Result.Succeeded);
        Assert.Equal(2, c.ResetAll().Count);
        Assert.Equal(2, n.Resets);
        Assert.Empty(c.Applied);
    }

    [Fact]
    public void NoNativeBackendFailsWithoutExplicitVisual()
    {
        var (c, _, v) = Make(nativeSupported: false);
        Assert.Equal(ScaleFailureCode.NoBackend, c.Apply(Targets.Allowed(), 150, false).Result.Code);
        Assert.Equal(0, v.Applies);
        Assert.True(c.Apply(Targets.Allowed(), 150, true).Result.Succeeded);
        Assert.Equal(1, v.Applies);
    }

    [Fact]
    public void VisualFeatureFlagOffHidesVisualBackend()
    {
        var (c, _, v) = Make(nativeSupported: false);
        c.Store.Update(s => s with { Features = s.Features with { VisualPreview = false } });
        Assert.Equal(ScaleFailureCode.NoBackend, c.Apply(Targets.Allowed(), 150, true).Result.Code);
        Assert.Equal(0, v.Applies);
    }
}
