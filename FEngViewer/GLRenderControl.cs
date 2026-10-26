using System;
using System.Drawing;
using System.Windows.Forms;
using FEngLib.Structures;
using FEngRender.Data;
using FEngRender.GL;
using JetBrains.Annotations;
using SharpGL.Enumerations;

namespace FEngViewer;

public partial class GLRenderControl : UserControl, IRenderControl
{
    private GLRenderTreeRenderer _renderer;

    [CanBeNull] private RenderTree _renderTree;
    private PreviewAspectRatio _previewAspect = PreviewAspectRatio.Standard4x3;
    private Rectangle _previewViewport = new(0, 0, PreviewLayout.LogicalWidth, PreviewLayout.LogicalHeight);

    public GLRenderControl()
    {
        InitializeComponent();

        // Let the GL surface line up exactly with this control so mouse coordinates map 1:1.
        openglControl1.Margin = Padding.Empty;
        Resize += (_, _) => UpdatePreviewViewport();
        UpdatePreviewViewport();
    }

    /// <summary>Raised when the letterbox rectangle changes, so the status strip can show the scale.</summary>
    public event EventHandler PreviewViewportChanged;

    /// <summary>Rectangle (control pixels) occupied by the selected fixed-ratio canvas.</summary>
    public Rectangle PreviewViewport => _previewViewport;

    public PreviewAspectRatio PreviewAspect
    {
        get => _previewAspect;
        set
        {
            if (!Enum.IsDefined(typeof(PreviewAspectRatio), value)) value = PreviewAspectRatio.Standard4x3;
            if (_previewAspect == value) return;
            _previewAspect = value;
            UpdatePreviewViewport();
            Invalidate();
        }
    }

    /// <summary>Maps a control-space point into the logical 640x480 HUD space.</summary>
    public PointF ScreenToLogical(Point point) => PreviewLayout.ScreenToLogical(point, _previewViewport, _previewAspect);

    public Color4 BackgroundColor
    {
        set => _renderer?.SetBackgroundColor(value);
    }

    public RenderTreeNode SelectedNode
    {
        get => _renderer?.SelectedNode;
        set { if (_renderer != null) _renderer.SelectedNode = value; }
    }

    public void Init(string textureDir)
    {
        _renderer?.LoadTextures(textureDir);
    }

    public void Render(RenderTree renderTree)
    {
        _renderTree = renderTree;
    }

    private void UpdatePreviewViewport()
    {
        var viewport = PreviewLayout.ComputeViewport(
            new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)), _previewAspect);
        if (viewport == _previewViewport) return;
        _previewViewport = viewport;
        PreviewViewportChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void openglControl1_OpenGLInitialized(object sender, EventArgs e)
    {
        _renderer = new GLRenderTreeRenderer(openglControl1.OpenGL);
        _renderer.PrepareRender();
    }

    private void openglControl1_OpenGLDraw(object sender, SharpGL.RenderEventArgs args)
    {
        var gl = openglControl1.OpenGL;
        if (_renderer == null)
        {
            gl.Clear(SharpGL.OpenGL.GL_COLOR_BUFFER_BIT | SharpGL.OpenGL.GL_DEPTH_BUFFER_BIT);
            return;
        }

        ApplyViewport(gl);
        if (_renderTree != null)
            _renderer.Render(_renderTree);
        else
            gl.Clear(SharpGL.OpenGL.GL_COLOR_BUFFER_BIT | SharpGL.OpenGL.GL_DEPTH_BUFFER_BIT);
    }

    /// <summary>
    /// Quad and GLGlyphRenderer already convert the 640x480 logical canvas to normalized -1..1
    /// vertices. Wider ratios compress only normalized X, expanding the visible logical width
    /// without stretching elements; the viewport then scales both pixel axes uniformly.
    /// </summary>
    private void ApplyViewport(SharpGL.OpenGL gl)
    {
        var client = openglControl1.ClientSize;
        var width = Math.Max(1, client.Width);
        var height = Math.Max(1, client.Height);
        var viewport = PreviewLayout.ComputeViewport(new Size(width, height), _previewAspect);
        _previewViewport = viewport;

        gl.MatrixMode(MatrixMode.Projection);
        gl.LoadIdentity();
        gl.Scale(PreviewLayout.LogicalWidth / PreviewLayout.CanvasSize(_previewAspect).Width, 1f, 1f);
        gl.Viewport(viewport.X, height - (viewport.Y + viewport.Height),
            Math.Max(1, viewport.Width), Math.Max(1, viewport.Height));
        gl.MatrixMode(MatrixMode.Modelview);
        gl.LoadIdentity();
    }

    private void openglControl1_MouseMove(object sender, MouseEventArgs e)
    {
        // The GL surface margin is zero; adding its offset keeps the mapping correct if that changes.
        OnMouseMove(new MouseEventArgs(e.Button, e.Clicks, e.X + openglControl1.Left, e.Y + openglControl1.Top, e.Delta));
    }

    private void openglControl1_MouseClick(object sender, MouseEventArgs e)
    {
        OnMouseClick(new MouseEventArgs(e.Button, e.Clicks, e.X + openglControl1.Left, e.Y + openglControl1.Top, e.Delta));
    }
}
