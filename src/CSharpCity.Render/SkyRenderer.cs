using System.Numerics;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// The sky: a graded dome by day, a star field by night, drawn as a single full-screen pass.
/// </summary>
/// <remarks>
/// Replaces a flat clear colour. Rather than build a skybox, the shader reconstructs a world-space
/// ray per pixel from the inverse view-projection, which gives a real dome — the gradient sits on
/// the horizon and the stars stay fixed in the sky as you turn, instead of sliding with the camera.
///
/// Stars are generated, not textured: a 3D cell hash over the view direction places one star per
/// cell, with a magnitude distribution skewed so most are faint and a few are bright. That's what
/// makes a real night sky read — a uniform sprinkle of equal dots looks like noise.
/// </remarks>
public sealed unsafe class SkyRenderer : IDisposable
{
    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _vbo;

    // A single oversized triangle covering the screen: cheaper than a quad and no seam down the middle.
    static readonly float[] Triangle = { -1f, -1f, 3f, -1f, -1f, 3f };

    public SkyRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

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
    }

    public float Time { get; set; }

    public void Draw(Matrix4x4 viewProjection, Vector3 cameraPosition, float nightAmount)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;

        _shader.Use();
        _shader.SetMatrix("uInvViewProj", inverse);
        _shader.SetVector3("uCameraPos", cameraPosition);
        _shader.SetFloat("uNight", nightAmount);
        _shader.SetFloat("uTime", Time);

        // Fills the frame, so it neither reads nor writes depth.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec2 aPos;
        out vec2 vNdc;

        void main() {
            vNdc = aPos;
            gl_Position = vec4(aPos, 1.0, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vNdc;

        uniform mat4 uInvViewProj;
        uniform vec3 uCameraPos;
        uniform float uNight;
        uniform float uTime;

        out vec4 FragColor;

        float hash13(vec3 p) {
            p = fract(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return fract((p.x + p.y) * p.z);
        }

        vec3 hash33(vec3 p) {
            return vec3(hash13(p), hash13(p + 17.1), hash13(p + 43.7));
        }

        /// Stars in a shell of cells around the view direction.
        vec3 starField(vec3 dir) {
            vec3 total = vec3(0.0);

            // Two shells at different densities: a sparse layer of bright stars over a fine dusting.
            for (int layer = 0; layer < 2; layer++) {
                // Density is per-axis, so a 0.866 factor lands three quarters of the stars.
                float density = layer == 0 ? 165.0 : 277.0;
                vec3 p = dir * density;
                vec3 cell = floor(p);
                vec3 f = fract(p);

                vec3 rnd = hash33(cell + float(layer) * 91.0);
                float magnitude = hash13(cell + 7.3);

                // Skew hard toward faint: a sky of equally bright dots reads as static.
                float brightness = pow(magnitude, 9.0);
                if (brightness < 0.0015) continue;

                float d = length(f - rnd);
                float core = smoothstep(0.20, 0.0, d);

                // Slow, per-star scintillation.
                float twinkle = 0.75 + 0.25 * sin(uTime * (1.4 + rnd.x * 2.6) + rnd.y * 40.0);

                // Stellar colour: mostly white, drifting blue or amber at the extremes.
                vec3 tint = mix(vec3(1.0, 0.82, 0.66), vec3(0.72, 0.84, 1.0), rnd.z);
                tint = mix(vec3(1.0), tint, 0.55);

                total += tint * core * brightness * twinkle * 9.0;
            }

            return total;
        }

        void main() {
            // Reconstruct the world-space ray for this pixel.
            vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(far.xyz / far.w - uCameraPos);

            float up = clamp(dir.y, -1.0, 1.0);

            vec3 dayZenith  = vec3(0.35, 0.53, 0.80);
            vec3 dayHorizon = vec3(0.72, 0.78, 0.84);
            vec3 nightZenith  = vec3(0.010, 0.016, 0.045);
            vec3 nightHorizon = vec3(0.055, 0.065, 0.105);

            float lift = pow(clamp(up * 0.5 + 0.5, 0.0, 1.0), 1.6);
            vec3 day = mix(dayHorizon, dayZenith, lift);
            vec3 night = mix(nightHorizon, nightZenith, lift);
            vec3 sky = mix(day, night, uNight);

            if (uNight > 0.01 && up > -0.05) {
                // A soft band of unresolved stars, standing in for the galactic plane.
                float band = exp(-pow((dot(dir, normalize(vec3(0.55, 0.36, -0.75)))) * 2.6, 2.0));
                sky += vec3(0.055, 0.060, 0.085) * band * uNight;

                // Fade out into the horizon haze, where real stars are lost too.
                float horizon = smoothstep(-0.05, 0.28, up);
                sky += starField(dir) * uNight * horizon;
            }

            FragColor = vec4(sky, 1.0);
        }
        """;
}
