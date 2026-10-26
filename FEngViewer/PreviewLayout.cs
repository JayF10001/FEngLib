using System;
using System.Drawing;

namespace FEngViewer;

/// <summary>Fixed display ratios; wider canvases expose additional space around the FNG centre.</summary>
public enum PreviewAspectRatio
{
    Standard4x3,
    Widescreen16x9,
    Ultrawide21x9
}

/// <summary>
/// The source FNG keeps its 640x480 coordinates and centre (320,240). Wider previews expand
/// horizontally around that centre; screen mapping and projection use the same logical width.
/// </summary>
internal static class PreviewLayout
{
    public const int LogicalWidth = 640;
    public const int LogicalHeight = 480;
    public const float MaxScale = 8f;

    public static SizeF CanvasSize(PreviewAspectRatio aspect) => new(aspect switch
    {
        PreviewAspectRatio.Widescreen16x9 => LogicalHeight * (16f / 9f),
        PreviewAspectRatio.Ultrawide21x9 => LogicalHeight * (21f / 9f),
        _ => LogicalWidth
    }, LogicalHeight);

    /// <summary>Centred fixed-ratio rectangle, uniformly scaled to the available space.</summary>
    public static Rectangle ComputeViewport(Size clientSize, PreviewAspectRatio aspect)
    {
        var width = Math.Max(1, clientSize.Width);
        var height = Math.Max(1, clientSize.Height);
        var canvas = CanvasSize(aspect);
        var scale = Math.Min(MaxScale, Math.Min(width / canvas.Width, height / canvas.Height));

        var viewWidth = Math.Clamp((int)Math.Round(canvas.Width * scale), 1, width);
        var viewHeight = Math.Clamp((int)Math.Round(canvas.Height * scale), 1, height);
        return new Rectangle((width - viewWidth) / 2, (height - viewHeight) / 2, viewWidth, viewHeight);
    }

    public static float EffectiveScale(Rectangle viewport) => viewport.Height / (float)LogicalHeight;

    /// <summary>Maps a point inside the control to logical HUD coordinates (may leave 0..640/0..480).</summary>
    public static PointF ScreenToLogical(Point point, Rectangle viewport,
        PreviewAspectRatio aspect = PreviewAspectRatio.Standard4x3)
    {
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);
        var canvas = CanvasSize(aspect);
        return new PointF(
            (point.X - viewport.X) * canvas.Width / width + (LogicalWidth - canvas.Width) / 2f,
            (point.Y - viewport.Y) * LogicalHeight / (float)height);
    }

    /// <summary>Inverse of <see cref="ScreenToLogical"/>; used by tests and hit-test diagnostics.</summary>
    public static PointF LogicalToScreen(PointF point, Rectangle viewport,
        PreviewAspectRatio aspect = PreviewAspectRatio.Standard4x3)
    {
        var width = Math.Max(1, viewport.Width);
        var height = Math.Max(1, viewport.Height);
        var canvas = CanvasSize(aspect);
        return new PointF(
            viewport.X + (point.X - (LogicalWidth - canvas.Width) / 2f) * width / canvas.Width,
            viewport.Y + point.Y * height / (float)LogicalHeight);
    }

    public static string Describe(Rectangle viewport, PreviewAspectRatio aspect) =>
        $"{CanvasSize(aspect).Width:0.#}×{LogicalHeight} @ {EffectiveScale(viewport) * 100f:0}%";

    public static string ModeName(PreviewAspectRatio aspect) => aspect switch
    {
        PreviewAspectRatio.Widescreen16x9 => "16:9",
        PreviewAspectRatio.Ultrawide21x9 => "21:9",
        _ => "4:3"
    };

    // Legacy "fit" / "one" and invalid values safely migrate to the original 4:3 canvas.
    public static PreviewAspectRatio ParseMode(string name) => name switch
    {
        "16:9" => PreviewAspectRatio.Widescreen16x9,
        "21:9" => PreviewAspectRatio.Ultrawide21x9,
        _ => PreviewAspectRatio.Standard4x3
    };
}
