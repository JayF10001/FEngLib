using System;
using System.Drawing;
using FEngLib.Structures;
using FEngRender.Data;

namespace FEngViewer;

public interface IRenderControl
{
    RenderTreeNode SelectedNode { get; set; }

    void Init(string textureDir);

    void Render(RenderTree renderTree);
        
    Color4 BackgroundColor { set; }

    /// <summary>Rectangle (control pixels) occupied by the selected fixed-ratio canvas.</summary>
    Rectangle PreviewViewport { get; }

    PreviewAspectRatio PreviewAspect { get; set; }

    event EventHandler PreviewViewportChanged;

    /// <summary>Maps a control-space point into the logical 640x480 HUD space.</summary>
    PointF ScreenToLogical(Point point);
}
