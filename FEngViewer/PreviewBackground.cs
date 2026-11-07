using System.Drawing;

namespace FEngViewer;

/// <summary>Shared checkerboard used behind transparent PNG previews.</summary>
internal static class PreviewBackground
{
    public static Bitmap Checkerboard { get; } = Create();

    private static Bitmap Create()
    {
        const int cell = 9;
        var bitmap = new Bitmap(cell * 2, cell * 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(232, 232, 232));
        using var dark = new SolidBrush(Color.FromArgb(158, 158, 158));
        graphics.FillRectangle(dark, 0, 0, cell, cell);
        graphics.FillRectangle(dark, cell, cell, cell, cell);
        return bitmap;
    }
}
