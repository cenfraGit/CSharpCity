using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Screen-space overlay: crosshair, the inspection card for whatever you're looking at, and the
/// minimap. Immediate-mode — call <see cref="Begin"/>, issue rectangles and text in pixel
/// coordinates, then <see cref="End"/> to upload and draw the frame in one instanced call.
/// </summary>
/// <remarks>
/// Shares the world labels' <see cref="FontAtlas"/> rather than rasterizing a second one: the atlas
/// is a megabyte of texture and a GDI+ pass at startup, and one is enough for both.
/// </remarks>
[SupportedOSPlatform("windows6.1")]
public sealed unsafe class HudRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Element
    {
        public Vector2 Position;   // pixels, origin top-left
        public Vector2 Size;
        public Vector4 Uv;
        public Vector4 Color;
    }

    readonly GL _gl;
    readonly FontAtlas _atlas;
    readonly Shader _shader;
    readonly uint _vao, _quadVbo, _instanceVbo;
    readonly List<Element> _elements = new();

    Vector2 _viewport = Vector2.One;
    int _capacity;

    static readonly float[] QuadCorners = { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 };

    public HudRenderer(GL gl, FontAtlas atlas)
    {
        _gl = gl;
        _atlas = atlas;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _quadVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* p = QuadCorners)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(QuadCorners.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        _instanceVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        uint stride = (uint)sizeof(Element);
        uint offset = 0;
        Attrib(1, 2, stride, ref offset);
        Attrib(2, 2, stride, ref offset);
        Attrib(3, 4, stride, ref offset);
        Attrib(4, 4, stride, ref offset);

        gl.BindVertexArray(0);

        void Attrib(uint index, int components, uint s, ref uint o)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, components, VertexAttribPointerType.Float, false, s, (void*)o);
            gl.VertexAttribDivisor(index, 1);
            o += (uint)(components * sizeof(float));
        }
    }

    public void Begin(Vector2 viewport)
    {
        _viewport = viewport;
        _elements.Clear();
    }

    public void Rect(float x, float y, float width, float height, Vector4 color)
    {
        var (u0, v0, u1, v1) = _atlas.SolidUv();
        _elements.Add(new Element
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, height),
            Uv = new Vector4(u0, v0, u1, v1),
            Color = color,
        });
    }

    /// <summary>Draws a line of text. Returns the width consumed, so callers can lay out inline.</summary>
    public float Text(float x, float y, float pixelSize, Vector4 color, string text)
    {
        float quad = _atlas.CellEm * pixelSize;
        float pen = x;

        foreach (char c in text)
        {
            float advance = _atlas.Advance(c) * pixelSize;
            if (c != ' ')
            {
                var (u0, v0, u1, v1) = _atlas.Uv(c);
                _elements.Add(new Element
                {
                    Position = new Vector2(pen, y),
                    Size = new Vector2(quad, quad),
                    Uv = new Vector4(u0, v0, u1, v1),
                    Color = color,
                });
            }
            pen += advance;
        }

        return pen - x;
    }

    public float Measure(string text, float pixelSize) => _atlas.Measure(text) * pixelSize;

    public void End()
    {
        if (_elements.Count == 0) return;

        _shader.Use();
        _shader.SetVector2("uViewport", _viewport);
        _shader.SetInt("uAtlas", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _atlas.Texture);

        // The HUD sits on top of everything by definition.
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);

        var span = CollectionsMarshal.AsSpan(_elements);
        if (_elements.Count > _capacity)
        {
            _capacity = Math.Max(_elements.Count * 2, 512);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_capacity * sizeof(Element)),
                null, BufferUsageARB.DynamicDraw);
        }
        fixed (Element* p = span)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                (nuint)(_elements.Count * sizeof(Element)), p);

        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)_elements.Count);
        _gl.BindVertexArray(0);

        _gl.Enable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_quadVbo);
        _gl.DeleteBuffer(_instanceVbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec2 aCorner;
        layout(location=1) in vec2 iPos;
        layout(location=2) in vec2 iSize;
        layout(location=3) in vec4 iUv;
        layout(location=4) in vec4 iColor;

        uniform vec2 uViewport;

        out vec2 vUv;
        out vec4 vColor;

        void main() {
            vec2 pixel = iPos + aCorner * iSize;
            // Pixels to clip space, with the origin at the top-left like every other 2D API.
            vec2 ndc = vec2(pixel.x / uViewport.x * 2.0 - 1.0, 1.0 - pixel.y / uViewport.y * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);

            vUv = mix(iUv.xy, iUv.zw, aCorner);
            vColor = iColor;
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColor;
        uniform sampler2D uAtlas;
        out vec4 FragColor;

        void main() {
            float coverage = texture(uAtlas, vUv).a;
            float alpha = coverage * vColor.a;
            if (alpha < 0.004) discard;
            FragColor = vec4(vColor.rgb, alpha);
        }
        """;
}
