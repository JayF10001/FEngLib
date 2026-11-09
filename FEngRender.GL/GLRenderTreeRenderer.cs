using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using FEngLib.Assets;
using FEngLib.Objects;
using FEngLib.Packages;
using FEngLib.Structures;
using FEngRender.Data;
using FEngRender.Utils;
using SharpGL;
using SharpGL.Enumerations;
using SixLabors.Fonts;

namespace FEngRender.GL;

/// <summary>
/// Make sure to initialize an OpenGL context before using this.
/// </summary>
public class GLRenderTreeRenderer
{
    private readonly OpenGL _gl;
    private readonly GLGlyphRenderer _glyphRenderer;

    private readonly Dictionary<string, Texture> _loadedTextures = new Dictionary<string, Texture>();
    private readonly Dictionary<uint, Texture> _resourceRequestToTexture = new Dictionary<uint, Texture>();
    private long _lastRenderTime;

    private Stopwatch _stopwatch;

    public GLRenderTreeRenderer(OpenGL gl)
    {
        _gl = gl;
        _glyphRenderer = new GLGlyphRenderer(_gl);
        _stopwatch = Stopwatch.StartNew();
    }

    private ITextureProvider _textureProvider;

    /// <summary>
    /// Resolves texture data from loose PNGs or game archives. Setting it drops the previous GL
    /// texture cache so a source switch cannot keep stale pixels or stale "missing" decisions.
    /// </summary>
    public ITextureProvider TextureProvider
    {
        get => _textureProvider;
        set
        {
            if (ReferenceEquals(_textureProvider, value)) return;
            _textureProvider = value;
            ClearTextureCache();
        }
    }

    private void ClearTextureCache()
    {
        _resourceRequestToTexture.Clear();
        foreach (var texture in _loadedTextures.Values)
            texture.GLTexture.Destroy(_gl);
        _loadedTextures.Clear();
    }

    public RenderTreeNode SelectedNode { get; set; }
    public IObject<ObjectData> PreviewObject { get; set; }
    public uint? PreviewScript { get; set; }
    public int PreviewTime { get; set; }
    public IReadOnlyDictionary<IObject<ObjectData>,ScriptPreviewFrame> PreviewFrames { get; set; }

    public void PrepareRender()
    {
        _gl.ClearColor(0.0f, 0f, 0.0f, 0f);

        _gl.Enable(OpenGL.GL_TEXTURE_2D);
    }

    public void LoadTextures(string directory)
    {
        // Opening a document must not create a textures folder beside the imported FNG.
        ClearTextureCache();
        if (!Directory.Exists(directory))
            return;

        foreach (var pngFile in Directory.GetFiles(directory, "*.png"))
        {
            var filename = Path.GetFileNameWithoutExtension(pngFile) ?? "";
            _loadedTextures.Add(filename.ToUpperInvariant(), new Texture(_gl, pngFile));
        }
    }

    /// <summary>
    /// Renders nodes from a <see cref="RenderTree"/> to a surface.
    /// </summary>
    /// <param name="tree">The <see cref="RenderTree"/> to render.</param>
    public void Render(RenderTree tree) => Render(tree, true);

    /// <summary>
    /// <paramref name="clear"/> is false when the caller has already painted a layered background
    /// (for example the editor's black canvas over a work-area colour).
    /// </summary>
    public void Render(RenderTree tree, bool clear)
    {
        // FNG layers use painter order (GetZ below), not the depth buffer. SharpGL enables
        // depth testing by default; its resized DIB surface can otherwise occlude every layer.
        _gl.Disable(OpenGL.GL_DEPTH_TEST);
        if (clear) _gl.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT);
        _gl.LoadIdentity();

        var dt = (int)(_stopwatch.ElapsedMilliseconds - _lastRenderTime);
        RenderGeometry.Prepare(tree, PreviewObject, PreviewScript, PreviewTime, PreviewFrames);
        RenderTree(tree);
        // _stopwatch.Restart();

        if (SelectedNode != null)
        {
            RenderNode(SelectedNode, true);
        }

        // The render control owns the projection and viewport. All geometry above is already
        // normalized; a pixel-space projection here corrupts subsequent draws in this context.
        _gl.Flush();
        _lastRenderTime = _stopwatch.ElapsedMilliseconds;
    }

    public void SetBackgroundColor(Color4 color)
    {
        Vector4 colorV = color;
        _gl.ClearColor(colorV.X, colorV.Y, colorV.Z, colorV.W);
    }

    private void PrepareNodes(IEnumerable<RenderTreeNode> nodes,
        Matrix4x4 viewMatrix, int timeDelta, RenderTreeNode parent = null)
    {
        var renderContext = new RenderContext(viewMatrix, parent);
        var nodeList = nodes.ToList();

        foreach (var node in nodeList)
        {
            node.Update(renderContext, timeDelta);
        }

        foreach (var renderTreeGroup in nodeList.OfType<RenderTreeGroup>())
        {
            PrepareNodes(renderTreeGroup, renderTreeGroup.Transform, timeDelta, renderTreeGroup);
        }
    }

    private void RenderTree(IEnumerable<RenderTreeNode> nodes)
    {
        foreach (var renderTreeNode in Data.RenderTree.GetAllTreeNodesForRendering(nodes)
                     .OrderByDescending(n => n.GetZ()))
        {
            RenderNode(renderTreeNode);
        }
    }

    private void RenderNode(RenderTreeNode node, bool doBoundingBox = false)
    {
        switch (node)
        {
            case RenderTreeGroup:
                // TODO: render group bounding box
                break;
            case RenderTreeMovie:
                break;
            case RenderTreeSimpleImage si:
                RenderSimpleImage(si, doBoundingBox);
                break;
            case RenderTreeColoredImage ci:
                RenderColoredImage(ci, doBoundingBox);
                break;
            case RenderTreeImage img:
                RenderRegularImage(img, doBoundingBox);
                break;
            case RenderTreeMultiImage mi:
                RenderMultiImage(mi, doBoundingBox);
                break;
            case RenderTreeText text:
                RenderString(text);
                break;
            default:
                Debug.Assert(false, "Unsupported node", "Type: {0}", node.GetType());
                break;
        }
        //switch (node.FrontendObject)
        //{
        //    // there are some things we just don't need to handle
        //    case Group _:
        //        RenderGroupBB(node, doBoundingBox);
        //        break;
        //    case Movie _:
        //        break;
        //    case SimpleImage _:
        //        RenderSimpleImage(node, doBoundingBox);
        //        break;
        //    case IImage<ImageData> image:
        //        RenderImage(node, image, doBoundingBox);
        //        break;
        //    case Text str:
        //        RenderString(node, str);
        //        break;
        //    default:
        //        Debug.Assert(false, "Unsupported object", "Type: {0}", node.FrontendObject.GetType());
        //        break;
        //}
    }

    private void RenderString(RenderTreeText node)
    {
        var font = TextHelpers.GetFont(18);
        var str = node.FrontendObject;
        var (_, _, width, height) = TextHelpers.MeasureText(str.Value, new RendererOptions(font)
        {
            WrappingWidth = str.MaxWidth
        });
        var xOffset = TextHelpers.CalculateXOffset((uint)str.Formatting,
            width);
        var yOffset = TextHelpers.CalculateYOffset((uint)str.Formatting,
            height);

        _glyphRenderer.Transform = node.Transform * Matrix4x4.CreateTranslation(320, 240, 0);
        _glyphRenderer.Color = node.BlendedColor;
        _glyphRenderer.Formatting = str.Formatting;

        TextRenderer.RenderTextTo(_glyphRenderer, str.Value,
            new RendererOptions(font, new Vector2(xOffset, yOffset))
            {
                WrappingWidth = str.MaxWidth,
            }
        );
    }

    private void RenderSimpleImage(RenderTreeSimpleImage node, bool doBoundingBox = false)
    {
        // top left, top right, bottom right, bottom left
        Vector4[] colors = new Vector4[4];
        colors[0] = colors[1] = colors[2] = colors[3] = node.BlendedColor;

        var otkMat4 = node.Transform * Matrix4x4.CreateTranslation(320, 240, 0);
        var q = new Quad(-0.5f, -0.5f, 0.5f, 0.5f,
            1.0f,
            otkMat4,
            Vector2.Zero,
            Vector2.Zero,
            colors);

        if (doBoundingBox)
            q.DrawBoundingBox(_gl);
        else
            q.Render(_gl);
    }

    private void RenderColoredImage(RenderTreeColoredImage coloredImage, bool doBoundingBox = false)
    {
        var texture = GetTexture(coloredImage.FrontendObject.ResourceRequest);

        if (texture == null) { RenderMissing(coloredImage, doBoundingBox); return; }

        var otkMat4 = coloredImage.Transform * Matrix4x4.CreateTranslation(320, 240, 0);

        // top left, top right, bottom right, bottom left
        Vector4[] colors = new Vector4[4];

        colors[0] = coloredImage.TopLeft;
        colors[1] = coloredImage.TopRight;
        colors[2] = coloredImage.BottomRight;
        colors[3] = coloredImage.BottomLeft;

        var q = new Quad(-0.5f, -0.5f, 0.5f, 0.5f,
            1.0f,
            otkMat4,
            coloredImage.UpperLeft,
            coloredImage.LowerRight,
            colors);

        if (doBoundingBox)
            q.DrawBoundingBox(_gl);
        else
            q.Render(_gl, texture);
    }

    private void RenderRegularImage(RenderTreeImage image, bool doBoundingBox = false)
    {
        var texture = GetTexture(image.FrontendObject.ResourceRequest);

        if (texture == null) { RenderMissing(image, doBoundingBox); return; }

        var otkMat4 = image.Transform * Matrix4x4.CreateTranslation(320, 240, 0);

        // top left, top right, bottom right, bottom left
        Vector4[] colors = new Vector4[4];

        colors[0] = image.BlendedColor;
        colors[1] = image.BlendedColor;
        colors[2] = image.BlendedColor;
        colors[3] = image.BlendedColor;

        var q = new Quad(-0.5f, -0.5f, 0.5f, 0.5f,
            1.0f,
            otkMat4,
            image.UpperLeft,
            image.LowerRight,
            colors);

        if (doBoundingBox)
            q.DrawBoundingBox(_gl);
        else
            q.Render(_gl, texture);
    }

    private void RenderMultiImage(RenderTreeMultiImage image, bool doBoundingBox = false)
    {
        var texture = GetTexture(image.FrontendObject.ResourceRequest);

        if (texture == null) { RenderMissing(image, doBoundingBox); return; }

        var otkMat4 = image.Transform * Matrix4x4.CreateTranslation(320, 240, 0);

        // top left, top right, bottom right, bottom left
        Vector4[] colors = new Vector4[4];

        colors[0] = image.BlendedColor;
        colors[1] = image.BlendedColor;
        colors[2] = image.BlendedColor;
        colors[3] = image.BlendedColor;

        var q = new Quad(-0.5f, -0.5f, 0.5f, 0.5f,
            1.0f,
            otkMat4,
            image.UpperLeft,
            image.LowerRight,
            colors);

        if (doBoundingBox)
            q.DrawBoundingBox(_gl);
        else
            q.Render(_gl, texture);
    }

    private void RenderGroupBB(RenderTreeNode node, bool doBoundingBox = false)
    {
        var otkMat4 = node.Transform * Matrix4x4.CreateTranslation(320, 240, 0);

        var q = new Quad(-0.5f, -0.5f, 0.5f, 0.5f,
            1.0f,
            otkMat4,
            Vector2.Zero,
            Vector2.One,
            new Vector4[4]);

        if (doBoundingBox)
            q.DrawBoundingBox(_gl);
    }

    private Texture GetTexture(ResourceRequest resource)
    {
        if (resource == null || (resource.Type != ResourceType.Image && resource.Type != ResourceType.MultiImage))
        {
            return null;
        }

        if (_resourceRequestToTexture.TryGetValue(resource.ID, out var texture)) return texture;

        if (_textureProvider != null)
        {
            try
            {
                var resolution = _textureProvider.Resolve(resource);
                if (resolution?.IsFound == true)
                {
                    try
                    {
                        texture = new Texture(_gl, resolution.PngData);
                        _resourceRequestToTexture[resource.ID] = texture;
                        return texture;
                    }
                    catch (Exception error)
                    {
                        Debug.WriteLine("Texture decode failed: {0} ({1})", new object[] { resolution.SourceTexture, error.Message });
                    }
                }
                else if (resolution != null)
                {
                    Debug.WriteLine("Texture unresolved: {0} [{1}] {2}", new object[]
                        { resolution.RequestedName, resolution.Status, resolution.Message });
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine("Texture provider failed: {0}", new object[] { error.Message });
            }
        }

        // Fallback for callers that only configured a loose texture directory.
        var key = CleanResourcePath(resource.Name);
        if (!_loadedTextures.TryGetValue(key, out texture))
        {
            Debug.WriteLine("Texture not found: {0}", new object[] { key });
        }
        else
        {
            _resourceRequestToTexture[resource.ID] = texture;
        }

        return texture;
    }

    private static string CleanResourcePath(string path)
    {
        return Path.GetFileNameWithoutExtension((path ?? "").Replace('\\', '/')).ToUpperInvariant();
    }

    private void RenderMissing(RenderTreeNode node, bool bounds)
    {
        var q = new Quad(-0.5f,-0.5f,0.5f,0.5f,1,
            node.Transform * Matrix4x4.CreateTranslation(320,240,0), Vector2.Zero, Vector2.Zero,
            Enumerable.Repeat(new Vector4(0.8f,0.15f,0.6f,0.65f),4).ToArray());
        if (bounds) q.DrawBoundingBox(_gl); else q.Render(_gl);
    }
}
