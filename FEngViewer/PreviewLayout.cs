using System;
using System.Drawing;

namespace FEngViewer;

/// <summary>How the 640x480 logical HUD canvas is mapped into the available control area.</summary>
public enum PreviewScaleMode
{
    /// <summary>Scale up or down so the whole 4:3 canvas is visible.</summary>
    Fit,

    /// <summary>One logical pixel per device pixel, centred and clipped when the area is smaller.</summary>
    OneToOne
}

/// <summary>
/// The single place where the logical 640x480 HUD space and the on-screen preview rectangle are
/// converted into each other. The FNG data itself is never changed by preview scaling.
/// </summary>
internal static class PreviewLayout
{
    public const int LogicalWidth = 640;
    public const int LogicalHeight = 480;
    public const float MinScale = 0.05f;

    /// <summary>Upper bound for Fit so a very large window does not magnify HUD pixels endlessly.</summary>
    public const float MaxScale = 8f;

    /// <summary>Rect (in control pixels, top-left origin) that the 640x480 canvas occupies.</summary>
    public static Rectangle ComputeViewport(Size clientSize, PreviewScaleMode mode)
    {
        var width = Math.Max(1, clientSize.Width);
        var height = Math.Max(1, clientSize.Height);
        var scale = mode == PreviewScaleMode.OneToOne
            ? 1f
            : Math.Min(width / (float)LogicalWidth, height / (float)LogicalHeight);
        scale = Math.Clamp(scale, MinScale, MaxScale);

        var viewWidth = Math.Max(1, (int)Math.Round(LogicalWidth * scale));
        var viewHeight = Math.Max(1, (int)Math.Round(LogicalHeight * scale));
        return new Rectangle((width - viewWidth) / 2, (height - viewHeight) / 2, viewWidth, viewHeight);
    }

    public static float EffectiveScale(Rectangle viewport) => viewport.Width / (float)LogicalWidth;

    /// <summary>Maps a point inside the control to logical HUD coordinates (may leave 0..640/0..480).</summary>
    public static PointF ScreenToLogical(Point point, Rectangle viewport)
    {
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);
        return new PointF(
            (point.X - viewport.X) * LogicalWidth / (float)width,
            (point.Y - viewport.Y) * LogicalHeight / (float)height);
    }

    /// <summary>Inverse of <see cref="ScreenToLogical"/>; used by tests and hit-test diagnostics.</summary>
    public static PointF LogicalToScreen(PointF point, Rectangle viewport)
    {
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);
        return new PointF(
            viewport.X + point.X * width / (float)LogicalWidth,
            viewport.Y + point.Y * height / (float)LogicalHeight);
    }

    public static string Describe(Rectangle viewport) =>
        $"{LogicalWidth}×{LogicalHeight} @ {EffectiveScale(viewport) * 100f:0}%";

    public static string ModeName(PreviewScaleMode mode) =>
        mode == PreviewScaleMode.OneToOne ? "one" : "fit";

    public static PreviewScaleMode ParseMode(string name) =>
        string.Equals(name, "one", StringComparison.OrdinalIgnoreCase)
            ? PreviewScaleMode.OneToOne
            : PreviewScaleMode.Fit;
}
