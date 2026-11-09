using System.Drawing;

namespace FEngViewer;

/// <summary>Backdrop shown in the work area around the fixed-ratio FNG canvas.</summary>
public enum WorkspaceBackdrop
{
    DeepGray,
    Black,
    Checkerboard
}

/// <summary>
/// Colours and labels for the canvas/work-area split. The game canvas itself stays black so the
/// preview remains colour-accurate; only the surrounding work area follows the selected backdrop.
/// </summary>
internal static class CanvasBackdrop
{
    /// <summary>Default work-area colour, deliberately quiet enough not to compete with the preview.</summary>
    public static readonly Color Workspace = Color.FromArgb(0x2E, 0x2E, 0x2E);

    /// <summary>Light 1-DIP seam that separates the black canvas from the work area.</summary>
    public static readonly Color Border = Color.FromArgb(0xC8, 0xC8, 0xC8);

    public static readonly Color CheckerAlternate = Color.FromArgb(0x3A, 0x3A, 0x3A);

    public static string DisplayName(WorkspaceBackdrop backdrop) => backdrop switch
    {
        WorkspaceBackdrop.Black => "黑色",
        WorkspaceBackdrop.Checkerboard => "棋盘格",
        _ => "深灰"
    };
}
