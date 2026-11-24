using AppScale.Core.Scaling;

namespace AppScale.Core.Tests;

public class ScaleMathTests
{
    [Theory]
    [InlineData(75, 72)]
    [InlineData(100, 96)]
    [InlineData(125, 120)]
    [InlineData(150, 144)]
    [InlineData(175, 168)]
    [InlineData(200, 192)]
    [InlineData(250, 240)]
    [InlineData(110, 106)] // 105.6 rounds half away from zero
    public void DpiForPercent_IsAbsolute(int percent, int dpi) => Assert.Equal(dpi, ScaleMath.DpiForPercent(percent));

    [Theory]
    [InlineData(74)]
    [InlineData(251)]
    [InlineData(0)]
    public void DpiForPercent_RejectsOutOfRange(int percent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScaleMath.DpiForPercent(percent));

    [Theory]
    [InlineData(144, 144, 1.0)]   // 150% requested on a 150% monitor: no visual zoom
    [InlineData(144, 120, 1.2)]   // 150% requested on a 125% monitor
    [InlineData(96, 192, 0.5)]    // 100% requested on a 200% monitor
    [InlineData(240, 96, 2.5)]
    public void RelativeZoom_IsRequestedOverActual(int requested, int actual, double zoom) =>
        Assert.Equal(zoom, ScaleMath.RelativeZoom(requested, actual), 6);

    [Theory]
    [InlineData(10, 3, 2, 15)]
    [InlineData(5, 1, 2, 3)]      // 2.5 → 3 (half away from zero, like Win32 MulDiv)
    [InlineData(-5, 1, 2, -3)]
    [InlineData(5, -1, 2, -3)]
    [InlineData(7, 1, 0, -1)]     // divide by zero → -1
    [InlineData(int.MaxValue, 2, 2, int.MaxValue)] // 64-bit intermediate
    public void MulDiv_MatchesWin32Semantics(int a, int b, int c, int expected) =>
        Assert.Equal(expected, ScaleMath.MulDiv(a, b, c));

    [Fact]
    public void ScalingFromLogical_HasNoCumulativeDrift()
    {
        int[] dpis = [72, 96, 120, 144, 168, 192, 240];
        int[] logical = [1, 7, 13, 23, 75, 101, 333, 1021];
        foreach (int l in logical)
        {
            int first = ScaleMath.LogicalToPhysical(l, 120);
            for (int round = 0; round < 100; round++)
                foreach (int d in dpis)
                    Assert.Equal(ScaleMath.MulDiv(l, d, 96), ScaleMath.LogicalToPhysical(l, d));
            Assert.Equal(first, ScaleMath.LogicalToPhysical(l, 120));
        }
    }

    [Fact]
    public void ScalingFromPreviousScaledValue_DriftsWhichIsWhyWeDontDoIt()
    {
        // Documents the failure mode the harnesses avoid: chaining conversions accumulates rounding error.
        int value = 10, dpi = 96;
        foreach (int next in new[] { 120, 72, 168, 96, 120, 72, 168, 96 })
        {
            value = ScaleMath.MulDiv(value, next, dpi);
            dpi = next;
        }
        Assert.Equal(11, value);
    }

    [Theory]
    [InlineData(100, 96, 144, 150)]
    [InlineData(150, 144, 96, 100)]
    [InlineData(-10, 96, 192, -20)]
    public void ConvertPoint_ScalesBothAxes(int v, int from, int to, int expected)
    {
        var (x, y) = ScaleMath.ConvertPoint(v, v * 2, from, to);
        Assert.Equal(expected, x);
        Assert.Equal(expected * 2, y);
    }

    [Fact]
    public void PreviewToSource_MapsCornersAndCenter()
    {
        // 200x100 source in a 400x200 preview: exact 2x zoom, no letterbox.
        Assert.Equal((0, 0), ScaleMath.PreviewToSource(0, 0, 400, 200, 200, 100));
        Assert.Equal((100, 50), ScaleMath.PreviewToSource(200, 100, 400, 200, 200, 100));
        Assert.Equal((199, 99), ScaleMath.PreviewToSource(399.9, 199.9, 400, 200, 200, 100));
        Assert.Null(ScaleMath.PreviewToSource(400, 0, 400, 200, 200, 100));
    }

    [Fact]
    public void PreviewToSource_ExcludesLetterbox()
    {
        // 100x100 source in a 300x200 preview: zoom 2, 50px bars left/right.
        Assert.Null(ScaleMath.PreviewToSource(25, 100, 300, 200, 100, 100));
        Assert.Equal((0, 50), ScaleMath.PreviewToSource(50, 100, 300, 200, 100, 100));
        Assert.Null(ScaleMath.PreviewToSource(10, 10, 0, 200, 100, 100));
    }

    [Fact]
    public void PhysicalToLogical_RoundTripsAtCommonScales()
    {
        foreach (int dpi in new[] { 96, 120, 144, 192, 240 })
            for (int l = 0; l < 500; l += 4)
                Assert.Equal(l, ScaleMath.PhysicalToLogical(ScaleMath.LogicalToPhysical(l, dpi), dpi));
    }
}

