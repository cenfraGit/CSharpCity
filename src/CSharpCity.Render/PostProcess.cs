using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Renders the world into an HDR buffer, then composites it with bloom, tone mapping, a vignette
/// and a little grain.
/// </summary>
/// <remarks>
/// This is what sells scale. Two effects do nearly all of the work:
///
/// <b>Bloom.</b> Lit windows, fires, beacons and the security beams already overdrive well past 1.0
/// — they were simply being clipped. Letting that energy bleed into neighbouring pixels is how a
/// night city reads as vast rather than as a dark grid with bright dots on it, and it costs nothing
/// extra in the scene because the brightness was already there.
///
/// <b>Tone mapping.</b> Clipping to white flattens exactly the range that carries depth. A filmic
/// curve keeps highlight detail, so a distant lit district still has structure instead of being a
/// solid glare.
///
/// The HUD is drawn afterwards, straight to the back buffer, so text stays crisp and doesn't glow.
/// </remarks>
public sealed unsafe class PostProcess : IDisposable
{
    readonly GL _gl;
    readonly Shader _bright, _blur, _composite;
    readonly uint _vao, _vbo;

    uint _sceneFbo, _sceneColor, _sceneDepth;
    readonly uint[] _blurFbo = new uint[2];
    readonly uint[] _blurColor = new uint[2];

    int _width, _height;
    /// <summary>Bloom runs at half resolution: invisible at this blur radius, four times cheaper.</summary>
    int BloomWidth => Math.Max(1, _width / 2);
    int BloomHeight => Math.Max(1, _height / 2);

    static readonly float[] Triangle = { -1f, -1f, 3f, -1f, -1f, 3f };

    public PostProcess(GL gl, int width, int height)
    {
        _gl = gl;
        _bright = new Shader(gl, VertexSource, BrightSource);
        _blur = new Shader(gl, VertexSource, BlurSource);
        _composite = new Shader(gl, VertexSource, CompositeSource);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = Triangle)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Triangle.Length * sizeof(float)), p,
                BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);

        Resize(width, height);
    }

    public float Time { get; set; }
    public float Night { get; set; }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == _width && height == _height)) return;
        _width = width;
        _height = height;

        Release();

        // Half-float, so highlights can exceed 1.0 and survive to the bloom pass.
        _sceneColor = NewTexture(_width, _height, InternalFormat.Rgba16f);
        _sceneFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _sceneColor, 0);

        _sceneDepth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)_width, (uint)_height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _sceneDepth);

        for (int i = 0; i < 2; i++)
        {
            _blurColor[i] = NewTexture(BloomWidth, BloomHeight, InternalFormat.Rgba16f);
            _blurFbo[i] = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFbo[i]);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _blurColor[i], 0);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    uint NewTexture(int width, int height, InternalFormat format)
    {
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.HalfFloat, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }

    /// <summary>Point the world pass at the offscreen buffer.</summary>
    public void BeginScene()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>Extract, blur, and composite back to the screen.</summary>
    public void Resolve()
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(_vao);

        // Bright pass into the first bloom target.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFbo[0]);
        _gl.Viewport(0, 0, (uint)BloomWidth, (uint)BloomHeight);
        _bright.Use();
        _bright.SetInt("uScene", 0);
        Bind(0, _sceneColor);
        Draw();

        // Separable gaussian, ping-ponged. Four passes widens the halo without a huge kernel.
        for (int i = 0; i < 4; i++)
        {
            int source = i % 2;
            int target = 1 - source;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFbo[target]);
            _blur.Use();
            _blur.SetInt("uSource", 0);
            _blur.SetVector2("uDirection", i % 2 == 0
                ? new System.Numerics.Vector2(1f / BloomWidth, 0f)
                : new System.Numerics.Vector2(0f, 1f / BloomHeight));
            Bind(0, _blurColor[source]);
            Draw();
        }

        // Composite to the back buffer.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _composite.Use();
        _composite.SetInt("uScene", 0);
        _composite.SetInt("uBloom", 1);
        _composite.SetFloat("uTime", Time);
        _composite.SetFloat("uNight", Night);
        Bind(0, _sceneColor);
        Bind(1, _blurColor[0]);
        Draw();

        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
    }

    void Bind(uint unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    void Draw() => _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

    void Release()
    {
        if (_sceneFbo != 0)
        {
            _gl.DeleteFramebuffer(_sceneFbo);
            _gl.DeleteTexture(_sceneColor);
            _gl.DeleteRenderbuffer(_sceneDepth);
        }
        for (int i = 0; i < 2; i++)
        {
            if (_blurFbo[i] == 0) continue;
            _gl.DeleteFramebuffer(_blurFbo[i]);
            _gl.DeleteTexture(_blurColor[i]);
        }
    }

    public void Dispose()
    {
        Release();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _bright.Dispose();
        _blur.Dispose();
        _composite.Dispose();
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec2 aPos;
        out vec2 vUv;
        void main() {
            vUv = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    const string BrightSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        out vec4 FragColor;

        void main() {
            vec3 c = texture(uScene, vUv).rgb;
            float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));

            // Soft knee: a hard cutoff makes bloom pop on and off as things cross the threshold.
            float knee = smoothstep(0.75, 1.35, luma);
            FragColor = vec4(c * knee, 1.0);
        }
        """;

    const string BlurSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uSource;
        uniform vec2 uDirection;
        out vec4 FragColor;

        void main() {
            // Nine-tap gaussian using linear-sampled offsets.
            float weight[5] = float[](0.2270, 0.1946, 0.1216, 0.0540, 0.0162);
            vec3 sum = texture(uSource, vUv).rgb * weight[0];

            for (int i = 1; i < 5; i++) {
                vec2 offset = uDirection * float(i) * 1.35;
                sum += texture(uSource, vUv + offset).rgb * weight[i];
                sum += texture(uSource, vUv - offset).rgb * weight[i];
            }

            FragColor = vec4(sum, 1.0);
        }
        """;

    const string CompositeSource = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uBloom;
        uniform float uTime;
        uniform float uNight;
        out vec4 FragColor;

        // Filmic curve. Keeps highlight detail instead of clipping it to flat white.
        vec3 tonemap(vec3 x) {
            const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
            return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
        }

        void main() {
            vec3 scene = texture(uScene, vUv).rgb;
            vec3 bloom = texture(uBloom, vUv).rgb;

            // Night leans on bloom harder: that's when the city is mostly light sources.
            scene += bloom * mix(0.42, 0.95, uNight);

            vec3 colour = tonemap(scene * 1.05);

            // Vignette. Subtle, but it frames the view and pushes the eye to the centre.
            vec2 d = vUv - 0.5;
            colour *= 1.0 - dot(d, d) * mix(0.55, 0.85, uNight);

            // A little grain, so flat sky gradients don't band.
            float grain = fract(sin(dot(vUv * uTime, vec2(12.9898, 78.233))) * 43758.5453);
            colour += (grain - 0.5) * 0.012;

            FragColor = vec4(colour, 1.0);
        }
        """;
}
