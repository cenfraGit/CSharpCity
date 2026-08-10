using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CSharpCity.Layout;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Draws <see cref="WorldLabel"/>s as camera-facing billboards: one instanced quad per glyph, plus a
/// dark plaque behind each label so text stays readable against any facade.
/// </summary>
/// <remarks>
/// Two details keep this legible. First, the whole pass leaves depth writes off — a plaque and its
/// own glyphs sit at exactly the same depth, so if the plaque wrote depth the glyphs would fail the
/// depth test and flicker in and out as the camera turned. Depth *testing* stays on, so signs are
/// still correctly hidden behind buildings in front of them. Second, labels are decluttered in
/// screen space every frame: overlapping signs are unreadable, so lower-priority ones drop out.
/// </remarks>
[SupportedOSPlatform("windows6.1")]
public sealed unsafe class TextRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Glyph
    {
        public Vector3 Anchor;      // label origin in world space
        public Vector2 Offset;      // position within the label, in world units, right/up from anchor
        public Vector2 Size;        // quad size in world units
        public Vector4 Uv;          // u0, v0, u1, v1
        public Vector4 Color;
        public float FadeDistance;
    }

    /// <summary>A label's baked geometry, kept CPU-side so the visible set can change every frame.</summary>
    sealed class LabelGeometry
    {
        public Vector3 Anchor;
        public Glyph Plaque;
        public Glyph[] Glyphs = Array.Empty<Glyph>();
        public float HalfWidth, Bottom, Top;   // local extents in world units
        public float FadeDistance;
        public int Priority;                   // higher wins a fight for screen space
        public float FaceRadius;               // 0 = floats in place; >0 = rides the near facade
    }

    readonly GL _gl;
    readonly FontAtlas _atlas;
    readonly Shader _shader;
    readonly Batch _plaques;
    readonly Batch _glyphs;
    readonly List<LabelGeometry> _labels = new();

    /// <summary>Screen divided this many ways per axis for the overlap broad-phase.</summary>
    const int BucketCount = 16;
    /// <summary>
    /// Ceiling on labels drawn in one frame. Past this the screen is unreadable anyway, and the cost
    /// is real: every label is a plaque plus one quad per glyph, rebuilt every frame.
    /// </summary>
    const int MaxVisibleLabels = 400;

    // Reused across frames so the declutter pass doesn't allocate.
    readonly List<(LabelGeometry Label, Vector4 Rect, float Distance)> _candidates = new();
    readonly List<Vector4>[] _buckets =
        Enumerable.Range(0, BucketCount * BucketCount).Select(_ => new List<Vector4>()).ToArray();
    readonly List<Glyph> _plaqueScratch = new();
    readonly List<Glyph> _glyphScratch = new();

    /// <param name="atlas">Shared with the HUD; this renderer does not own or dispose it.</param>
    public TextRenderer(GL gl, FontAtlas atlas)
    {
        _gl = gl;
        _atlas = atlas;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _plaques = new Batch(gl);
        _glyphs = new Batch(gl);
    }

    public void Build(IEnumerable<WorldLabel> labels)
    {
        _labels.Clear();

        foreach (var label in labels)
        {
            float titleSize = label.Size;
            float subSize = label.Size * 0.62f;
            float lineGap = titleSize * 0.30f;

            float titleWidth = _atlas.Measure(label.Text) * titleSize;
            float subWidth = label.Subtitle is null ? 0f : _atlas.Measure(label.Subtitle) * subSize;
            float widest = MathF.Max(titleWidth, subWidth);

            // Title sits on the top line; the subtitle hangs below it.
            float titleBaseline = label.Subtitle is null ? 0f : subSize + lineGap;

            float padX = titleSize * 0.35f, padY = titleSize * 0.28f;
            float bottom = -padY;
            float top = titleBaseline + titleSize + padY;
            float halfWidth = widest * 0.5f + padX;

            var geometry = new LabelGeometry
            {
                Anchor = label.Position,
                HalfWidth = halfWidth,
                Bottom = bottom,
                Top = top,
                FadeDistance = label.FadeDistance,
                FaceRadius = label.FaceRadius,
                Priority = label.Priority,
            };

            var (su0, sv0, su1, sv1) = _atlas.SolidUv();
            geometry.Plaque = new Glyph
            {
                Anchor = label.Position,
                Offset = new Vector2(-halfWidth, bottom),
                Size = new Vector2(halfWidth * 2f, top - bottom),
                Uv = new Vector4(su0, sv0, su1, sv1),
                Color = new Vector4(0.05f, 0.06f, 0.08f, 0.82f),
                FadeDistance = label.FadeDistance,
            };

            var glyphs = new List<Glyph>();
            EmitLine(glyphs, label.Text, label.Position, titleBaseline, titleSize, label.Color,
                label.FadeDistance);
            if (label.Subtitle is not null)
                EmitLine(glyphs, label.Subtitle, label.Position, 0f, subSize,
                    new Vector4(0.74f, 0.78f, 0.82f, label.Color.W), label.FadeDistance);

            geometry.Glyphs = glyphs.ToArray();
            _labels.Add(geometry);
        }
    }

    void EmitLine(List<Glyph> output, string text, Vector3 anchor, float baseline, float size,
        Vector4 color, float fadeDistance)
    {
        float quad = _atlas.CellEm * size;
        float pen = -_atlas.Measure(text) * size * 0.5f;   // centred on the anchor

        foreach (char c in text)
        {
            float advance = _atlas.Advance(c) * size;
            if (c != ' ')
            {
                var (u0, v0, u1, v1) = _atlas.Uv(c);
                output.Add(new Glyph
                {
                    Anchor = anchor,
                    Offset = new Vector2(pen, baseline),
                    Size = new Vector2(quad, quad),
                    Uv = new Vector4(u0, v0, u1, v1),
                    Color = color,
                    FadeDistance = fadeDistance,
                });
            }
            pen += advance;
        }
    }

    /// <summary>Labels that survived declutter last frame. Diagnostics only.</summary>
    public int VisibleCount { get; private set; }

    public void Draw(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 cameraRight,
        Vector3 cameraUp)
    {
        SelectVisible(viewProjection, cameraPosition, cameraRight, cameraUp);
        VisibleCount = _plaqueScratch.Count;
        if (_plaqueScratch.Count == 0) return;

        _plaques.Upload(CollectionsMarshal.AsSpan(_plaqueScratch));
        _glyphs.Upload(CollectionsMarshal.AsSpan(_glyphScratch));

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProjection);
        _shader.SetVector3("uCameraPos", cameraPosition);
        _shader.SetVector3("uCameraRight", cameraRight);
        _shader.SetVector3("uCameraUp", cameraUp);
        _shader.SetInt("uAtlas", 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _atlas.Texture);
        // Billboards are built facing the camera, so back-face culling would be a coin flip.
        _gl.Disable(EnableCap.CullFace);
        // Never write depth: plaque and glyphs share a depth, and a write would reject the glyphs.
        _gl.DepthMask(false);

        _plaques.Draw();
        _glyphs.Draw();

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);
    }

    /// <summary>
    /// Projects every label to screen space and greedily keeps the ones that don't collide, biggest
    /// and nearest first. Overlapping signs are worse than missing ones — you can walk closer to a
    /// missing label, but you can't unscramble two drawn on top of each other.
    /// </summary>
    void SelectVisible(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 cameraRight,
        Vector3 cameraUp)
    {
        _candidates.Clear();
        foreach (var bucket in _buckets) bucket.Clear();
        _plaqueScratch.Clear();
        _glyphScratch.Clear();

        foreach (var label in _labels)
        {
            var anchor = FacadeAnchor(label, cameraPosition);
            float distance = Vector3.Distance(anchor, cameraPosition);
            if (distance > label.FadeDistance) continue;

            // Project the plaque's own corners, so the rect tracks perspective exactly.
            var low = anchor - cameraRight * label.HalfWidth + cameraUp * label.Bottom;
            var high = anchor + cameraRight * label.HalfWidth + cameraUp * label.Top;
            if (!TryProject(low, viewProjection, out var a)) continue;
            if (!TryProject(high, viewProjection, out var b)) continue;

            var rect = new Vector4(
                MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));

            // Fully offscreen: no point spending instances or blocking other labels.
            if (rect.Z < -1.05f || rect.X > 1.05f || rect.W < -1.05f || rect.Y > 1.05f) continue;

            _candidates.Add((label, rect, distance));
        }

        // Floor signs share a building's footprint, so they need the near-facade anchor at draw time
        // too — not just for the collision rect.

        _candidates.Sort((x, y) =>
        {
            int byPriority = y.Label.Priority.CompareTo(x.Label.Priority);
            return byPriority != 0 ? byPriority : x.Distance.CompareTo(y.Distance);
        });

        foreach (var (label, rect, _) in _candidates)
        {
            if (_plaqueScratch.Count >= MaxVisibleLabels) break;
            if (Collides(rect)) continue;

            Occupy(rect);

            var anchor = FacadeAnchor(label, cameraPosition);
            var plaque = label.Plaque;
            plaque.Anchor = anchor;
            _plaqueScratch.Add(plaque);

            foreach (var glyph in label.Glyphs)
            {
                var moved = glyph;
                moved.Anchor = anchor;
                _glyphScratch.Add(moved);
            }
        }
    }

    /// <summary>
    /// Slides a wall-mounted label out to the facade nearest the viewer. Kept horizontal so signs
    /// stay flat against the building instead of tilting when you look up a tall stack.
    /// </summary>
    static Vector3 FacadeAnchor(LabelGeometry label, Vector3 cameraPosition)
    {
        if (label.FaceRadius <= 0f) return label.Anchor;

        var flat = new Vector3(cameraPosition.X - label.Anchor.X, 0f, cameraPosition.Z - label.Anchor.Z);
        float length = flat.Length();
        if (length < 0.001f) return label.Anchor;   // directly overhead: leave it centred

        return label.Anchor + flat / length * label.FaceRadius;
    }

    /// <summary>
    /// Screen-space overlap test against already-placed labels, bucketed into a uniform grid.
    /// </summary>
    /// <remarks>
    /// The obvious implementation — compare each candidate against every accepted label — is
    /// quadratic, which is invisible at 130 labels and ruinous at several thousand: a large solution
    /// would spend millions of rectangle tests per frame. Bucketing makes each test look at only the
    /// handful of labels sharing its patch of screen.
    /// </remarks>
    bool Collides(Vector4 rect)
    {
        var (minX, minY, maxX, maxY) = BucketRange(rect);

        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            foreach (var taken in _buckets[y * BucketCount + x])
            {
                if (rect.X < taken.Z && rect.Z > taken.X && rect.Y < taken.W && rect.W > taken.Y)
                    return true;
            }
        }
        return false;
    }

    void Occupy(Vector4 rect)
    {
        var (minX, minY, maxX, maxY) = BucketRange(rect);

        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
            _buckets[y * BucketCount + x].Add(rect);
    }

    /// <summary>Clamped grid cells an NDC rectangle touches.</summary>
    static (int MinX, int MinY, int MaxX, int MaxY) BucketRange(Vector4 rect)
    {
        static int Cell(float ndc) =>
            Math.Clamp((int)((ndc + 1f) * 0.5f * BucketCount), 0, BucketCount - 1);

        return (Cell(rect.X), Cell(rect.Y), Cell(rect.Z), Cell(rect.W));
    }

    static bool TryProject(Vector3 world, Matrix4x4 viewProjection, out Vector2 ndc)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (clip.W <= 0.001f)   // at or behind the eye; no meaningful screen rect
        {
            ndc = default;
            return false;
        }
        ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        return true;
    }

    public void Dispose()
    {
        _plaques.Dispose();
        _glyphs.Dispose();
        _shader.Dispose();
    }

    /// <summary>A VAO plus instance buffer for one draw call's worth of quads.</summary>
    sealed class Batch : IDisposable
    {
        readonly GL _gl;
        readonly uint _vao, _quadVbo, _instanceVbo;
        int _capacity;
        public int Count { get; private set; }

        static readonly float[] QuadCorners = { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 };

        public Batch(GL gl)
        {
            _gl = gl;
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
            uint stride = (uint)sizeof(Glyph);
            uint offset = 0;
            Attrib(gl, 1, 3, stride, ref offset);   // anchor
            Attrib(gl, 2, 2, stride, ref offset);   // offset
            Attrib(gl, 3, 2, stride, ref offset);   // size
            Attrib(gl, 4, 4, stride, ref offset);   // uv
            Attrib(gl, 5, 4, stride, ref offset);   // color
            Attrib(gl, 6, 1, stride, ref offset);   // fade distance

            gl.BindVertexArray(0);
        }

        static void Attrib(GL gl, uint index, int components, uint stride, ref uint offset)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, components, VertexAttribPointerType.Float, false, stride,
                (void*)offset);
            gl.VertexAttribDivisor(index, 1);
            offset += (uint)(components * sizeof(float));
        }

        public void Upload(ReadOnlySpan<Glyph> instances)
        {
            Count = instances.Length;
            if (Count == 0) return;

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
            if (Count > _capacity)
            {
                // Grow in steps so a busy view doesn't reallocate the buffer every frame.
                _capacity = Math.Max(Count * 2, 1024);
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_capacity * sizeof(Glyph)),
                    null, BufferUsageARB.DynamicDraw);
            }
            fixed (Glyph* p = instances)
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                    (nuint)(instances.Length * sizeof(Glyph)), p);
        }

        public void Draw()
        {
            if (Count == 0) return;
            _gl.BindVertexArray(_vao);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)Count);
            _gl.BindVertexArray(0);
        }

        public void Dispose()
        {
            _gl.DeleteBuffer(_quadVbo);
            _gl.DeleteBuffer(_instanceVbo);
            _gl.DeleteVertexArray(_vao);
        }
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec2 aCorner;
        layout(location=1) in vec3 iAnchor;
        layout(location=2) in vec2 iOffset;
        layout(location=3) in vec2 iSize;
        layout(location=4) in vec4 iUv;
        layout(location=5) in vec4 iColor;
        layout(location=6) in float iFade;

        uniform mat4 uViewProj;
        uniform vec3 uCameraPos;
        uniform vec3 uCameraRight;
        uniform vec3 uCameraUp;

        out vec2 vUv;
        out vec4 vColor;
        out float vFade;

        void main() {
            vec2 local = iOffset + aCorner * iSize;
            vec3 world = iAnchor + uCameraRight * local.x + uCameraUp * local.y;
            gl_Position = uViewProj * vec4(world, 1.0);

            vUv = mix(iUv.xy, iUv.zw, vec2(aCorner.x, 1.0 - aCorner.y));
            vColor = iColor;

            // Fade out near the cull distance so labels dissolve instead of popping.
            float dist = length(iAnchor - uCameraPos);
            vFade = 1.0 - smoothstep(iFade * 0.80, iFade, dist);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColor;
        in float vFade;
        uniform sampler2D uAtlas;
        out vec4 FragColor;

        void main() {
            float coverage = texture(uAtlas, vUv).a;
            float alpha = coverage * vColor.a * vFade;
            if (alpha < 0.01) discard;
            FragColor = vec4(vColor.rgb, alpha);
        }
        """;
}
