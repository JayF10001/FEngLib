using System;
using System.Drawing;
using System.IO;
using SharpGL;

namespace FEngRender.GL;

// A helper class, much like Shader, meant to simplify loading textures.
public class Texture
{
    public readonly SharpGL.SceneGraph.Assets.Texture GLTexture;
    public readonly int Width;
    public readonly int Height;

    public Texture(OpenGL gl, string path)
    {
        GLTexture = new SharpGL.SceneGraph.Assets.Texture();
        using var image = new Bitmap(path);

        Width = image.Width;
        Height = image.Height;
        GLTexture.Create(gl, image);
    }

    /// <summary>Creates a GL texture from PNG bytes supplied by an asset resolver.</summary>
    public Texture(OpenGL gl, byte[] pngData)
    {
        GLTexture = new SharpGL.SceneGraph.Assets.Texture();
        using var stream = new MemoryStream(pngData ?? Array.Empty<byte>(), writable: false);
        using var image = new Bitmap(stream);

        Width = image.Width;
        Height = image.Height;
        GLTexture.Create(gl, image);
    }
}