using System;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using FEngLib.Assets;
using FEngLib.Structures;
using FEngRender.Data;
using FEngRender.GL;
using JetBrains.Annotations;
using SharpGL.Enumerations;

namespace FEngViewer;

public partial class GLRenderControl : UserControl, IRenderControl
{
    private GLRenderTreeRenderer _renderer;
    private RenderTreeNode _selectedNode;
    private bool _frameQueued;
    private ITextureProvider _textureProvider;

    [CanBeNull] private RenderTree _renderTree;
    private PreviewAspectRatio _previewAspect = PreviewAspectRatio.Standard4x3;
    private Rectangle _previewViewport = new(0, 0, PreviewLayout.LogicalWidth, PreviewLayout.LogicalHeight);
    private WorkspaceBackdrop _backdrop = WorkspaceBackdrop.DeepGray;
    private Color4 _canvasBackground = new(0, 0, 0, 255);

    public GLRenderControl()
    {
        InitializeComponent();

        // Let the GL surface line up exactly with this control so mouse coordinates map 1:1.
        openglControl1.Margin = Padding.Empty;
        BackColor = Color.Black;
        openglControl1.BackColor = Color.Black;
        Resize += (_, _) => UpdatePreviewViewport();
        UpdatePreviewViewport();
        InitializeEditorInput();
        openglControl1.GDIDraw += (_, args) => DrawEditorOverlay(args.Graphics);
    }

    /// <summary>
    /// Work-area backdrop outside the actual FNG canvas. The canvas stays black in every mode; the
    /// light seam is drawn by the editor overlay. Changing this never touches FNG data.
    /// </summary>
    public WorkspaceBackdrop Backdrop
    {
        get => _backdrop;
        set
        {
            if (!Enum.IsDefined(typeof(WorkspaceBackdrop), value)) value = WorkspaceBackdrop.DeepGray;
            if (_backdrop == value) return;
            _backdrop = value;
            RequestFrame();
        }
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
    public PointF ScreenToLogical(Point point) => new(
        (point.X-_previewViewport.Left-_previewViewport.Width/2f-_pan.X)/Zoom+320,
        (point.Y-_previewViewport.Top-_previewViewport.Height/2f-_pan.Y)/Zoom+240);

    public Color4 BackgroundColor
    {
        set
        {
            _canvasBackground = value;
            _renderer?.SetBackgroundColor(value);
            RequestFrame();
        }
    }

    public RenderTreeNode SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            _selectedNode = value;
            RequestFrame();
        }
    }

    public ITextureProvider TextureProvider
    {
        get => _textureProvider;
        set
        {
            _textureProvider = value;
            if (_renderer != null) _renderer.TextureProvider = value;
        }
    }

    public void Init(string textureDir)
    {
        _renderer?.LoadTextures(textureDir);
    }

    public void Render(RenderTree renderTree)
    {
        if (!ReferenceEquals(_renderTree, renderTree))
        {
            HoveredNode = null;
            _lastHits.Clear();
        }
        _renderTree = renderTree;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameQueued || !IsHandleCreated || IsDisposed) return;
        _frameQueued = true;
        BeginInvoke(new Action(() =>
        {
            _frameQueued = false;
            if (!IsDisposed && !openglControl1.IsDisposed && _renderer != null)
                openglControl1.DoRender();
        }));
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
        _renderer.TextureProvider = _textureProvider;
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

        UpdatePreviewViewport();
        // Paint both layers before the FNG content. The renderer must not clear afterwards or it
        // would erase the work area; content that extends past the canvas stays visible on top.
        DrawSceneBackground(gl);
        ApplyViewport(gl);
        UpdatePreviewFrame();
        if (_renderTree != null)
            _renderer.Render(_renderTree, false);
    }

    /// <summary>
    /// Clears the whole surface with the work-area colour, then the actual canvas with its own
    /// colour through a scissor rectangle. No GDI fill is used, so FNG layers outside the canvas
    /// still render above the work area instead of being covered by editor chrome.
    /// </summary>
    private void DrawSceneBackground(SharpGL.OpenGL gl)
    {
        var client = openglControl1.ClientSize;
        var width = Math.Max(1, client.Width);
        var height = Math.Max(1, client.Height);
        gl.Viewport(0, 0, width, height);
        gl.Disable(SharpGL.OpenGL.GL_SCISSOR_TEST);

        var workspace = _backdrop == WorkspaceBackdrop.Black ? Color.Black : CanvasBackdrop.Workspace;
        gl.ClearColor(workspace.R / 255f, workspace.G / 255f, workspace.B / 255f, 1f);
        gl.Clear(SharpGL.OpenGL.GL_COLOR_BUFFER_BIT | SharpGL.OpenGL.GL_DEPTH_BUFFER_BIT);

        if (_backdrop == WorkspaceBackdrop.Checkerboard)
            DrawWorkspaceCheckerboard(gl, width, height);

        var canvas = CanvasClip;
        if (canvas.Width <= 0 || canvas.Height <= 0) return;
        gl.Enable(SharpGL.OpenGL.GL_SCISSOR_TEST);
        gl.Scissor(canvas.X, height - canvas.Bottom, canvas.Width, canvas.Height);
        var background = (Vector4)_canvasBackground;
        gl.ClearColor(background.X, background.Y, background.Z, background.W);
        gl.Clear(SharpGL.OpenGL.GL_COLOR_BUFFER_BIT);
        gl.Disable(SharpGL.OpenGL.GL_SCISSOR_TEST);
    }

    /// <summary>
    /// Alternating dark cells for the transparent-material backdrop. Drawn in screen space before
    /// content, so textures/layers remain fully visible and the board never acts as a mask.
    /// </summary>
    private void DrawWorkspaceCheckerboard(SharpGL.OpenGL gl, int width, int height)
    {
        var cell = Math.Max(6, (int)Math.Round(12 * DeviceDpi / 96f));
        gl.Disable(SharpGL.OpenGL.GL_TEXTURE_2D);
        gl.Disable(SharpGL.OpenGL.GL_BLEND);
        gl.Disable(SharpGL.OpenGL.GL_CULL_FACE);
        gl.Disable(SharpGL.OpenGL.GL_SCISSOR_TEST);
        gl.MatrixMode(MatrixMode.Projection);
        gl.LoadIdentity();
        gl.Ortho(0, width, height, 0, -1, 1);
        gl.MatrixMode(MatrixMode.Modelview);
        gl.LoadIdentity();

        var alternate = CanvasBackdrop.CheckerAlternate;
        gl.Color(alternate.R / 255f, alternate.G / 255f, alternate.B / 255f, 1f);
        gl.Begin(SharpGL.OpenGL.GL_QUADS);
        for (var y = 0; y < height; y += cell)
        for (var x = 0; x < width; x += cell)
        {
            if ((((x / cell) + (y / cell)) & 1) == 0) continue;
            gl.Vertex(x, y);
            gl.Vertex(x + cell, y);
            gl.Vertex(x + cell, y + cell);
            gl.Vertex(x, y + cell);
        }

        gl.End();
        gl.Color(1f, 1f, 1f, 1f);
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
        var fitScale=Math.Max(0.001f,viewport.Height/480f);
        gl.Translate(2f*_pan.X/viewport.Width,-2f*_pan.Y/viewport.Height,0);
        gl.Scale(PreviewLayout.LogicalWidth / PreviewLayout.CanvasSize(_previewAspect).Width*Zoom/fitScale, Zoom/fitScale, 1f);
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
