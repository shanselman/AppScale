namespace AppScale.Core.Scaling;

/// <summary>
/// DPI arithmetic. "Absolute" scale means DPI = 96 × percent / 100, independent of the monitor.
/// "Relative zoom" is requested DPI ÷ actual OS DPI and only describes visual magnification.
/// </summary>
public static class ScaleMath
{
    public const int BaseDpi = 96;
    public const int MinPercent = 75;
    public const int MaxPercent = 250;
    public const int MinDpi = 72;
    public const int MaxDpi = 240;

    public static bool IsValidPercent(int percent) => percent is >= MinPercent and <= MaxPercent;

    public static bool IsValidDpi(int dpi) => dpi is >= MinDpi and <= MaxDpi;

    /// <summary>Absolute DPI for a scale percentage, rounded half away from zero.</summary>
    public static int DpiForPercent(int percent)
    {
        if (!IsValidPercent(percent))
            throw new ArgumentOutOfRangeException(nameof(percent), percent, $"Scale must be {MinPercent}–{MaxPercent}%.");
        return MulDiv(BaseDpi, percent, 100);
    }

    public static double PercentForDpi(int dpi) => dpi * 100.0 / BaseDpi;

    /// <summary>Visual zoom factor needed to make a window rendered at <paramref name="actualDpi"/> look like <paramref name="requestedDpi"/>.</summary>
    public static double RelativeZoom(int requestedDpi, int actualDpi)
    {
        if (actualDpi <= 0) throw new ArgumentOutOfRangeException(nameof(actualDpi));
        if (requestedDpi <= 0) throw new ArgumentOutOfRangeException(nameof(requestedDpi));
        return (double)requestedDpi / actualDpi;
    }

    /// <summary>Win32 MulDiv semantics: (a*b)/c with 64-bit intermediate, rounded half away from zero. Returns -1 when c == 0.</summary>
    public static int MulDiv(int number, int numerator, int denominator)
    {
        if (denominator == 0) return -1;
        long product = (long)number * numerator;
        long den = Math.Abs((long)denominator);
        bool negative = (product < 0) ^ (denominator < 0);
        long q = (Math.Abs(product) + den / 2) / den;
        long result = negative ? -q : q;
        return result is > int.MaxValue or < int.MinValue ? -1 : (int)result;
    }

    /// <summary>Scale a 96-DPI logical length to <paramref name="dpi"/>. Always derive from the logical value to avoid cumulative drift.</summary>
    public static int LogicalToPhysical(int logical, int dpi) => MulDiv(logical, dpi, BaseDpi);

    public static int PhysicalToLogical(int physical, int dpi) => MulDiv(physical, BaseDpi, dpi);

    /// <summary>Map a point from one DPI coordinate space to another.</summary>
    public static (int X, int Y) ConvertPoint(int x, int y, int fromDpi, int toDpi) =>
        (MulDiv(x, toDpi, fromDpi), MulDiv(y, toDpi, fromDpi));

    /// <summary>
    /// Map a point inside an aspect-preserving (letterboxed) preview of a source surface back to
    /// source pixel coordinates. Returns null when the point falls outside the source image.
    /// </summary>
    public static (int X, int Y)? PreviewToSource(double px, double py, double previewWidth, double previewHeight, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || previewWidth <= 0 || previewHeight <= 0) return null;
        double zoom = Math.Min(previewWidth / sourceWidth, previewHeight / sourceHeight);
        double offX = (previewWidth - sourceWidth * zoom) / 2;
        double offY = (previewHeight - sourceHeight * zoom) / 2;
        double sx = (px - offX) / zoom;
        double sy = (py - offY) / zoom;
        if (sx < 0 || sy < 0 || sx >= sourceWidth || sy >= sourceHeight) return null;
        return ((int)Math.Floor(sx), (int)Math.Floor(sy));
    }
}
