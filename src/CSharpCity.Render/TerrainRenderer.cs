using System.Numerics;
using CSharpCity.Layout;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Draws the mountain ring as a lit heightfield.
/// </summary>
/// <remarks>
/// The only non-instanced geometry in the renderer, and the only thing with smooth normals. Rock is
/// shaded by height and steepness rather than by a per-vertex colour: grass gives way to scree,
/// scree to bare rock on the steep faces, and snow settles on the high ground â€” but only where it's
/// flat enough to lie, which is what stops the peaks looking dipped in paint.
/// </remarks>
public sealed unsafe class TerrainRenderer : IDisposable
{
    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _vbo, _ebo;
    readonly int _indexCount;

    public TerrainRenderer(GL gl, TerrainMesh mesh)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _indexCount = mesh.Indices.Length;

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = mesh.Vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Vertices.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = mesh.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(mesh.Indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const uint stride = 6 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));

        gl.BindVertexArray(0);
    }

    public void Draw(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection,
        float nightAmount)
    {
        if (_indexCount == 0) return;

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProjection);
        _shader.SetVector3("uCameraPos", cameraPosition);
        _shader.SetVector3("uSunDir", Vector3.Normalize(sunDirection));
        _shader.SetFloat("uNight", nightAmount);

        // The mesh has consistent winding but the range is viewed from inside and out.
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedInt,
            (void*)0);
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.CullFace);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;

        uniform mat4 uViewProj;

        out vec3 vWorld;
        out vec3 vNormal;

        void main() {
            vWorld = aPos;
            vNormal = aNormal;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;

        uniform vec3 uCameraPos;
        uniform vec3 uSunDir;
        uniform float uNight;

        out vec4 FragColor;

        float hash(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        void main() {
            vec3 n = normalize(vNormal);
            // 1 on a cliff, 0 on the flat.
            float steep = clamp(1.0 - n.y, 0.0, 1.0);
            float height = vWorld.y;

            vec3 grass = vec3(0.16, 0.26, 0.14);
            vec3 scree = vec3(0.34, 0.31, 0.26);
            vec3 rock  = vec3(0.27, 0.26, 0.26);
            vec3 snow  = vec3(0.90, 0.92, 0.96);

            vec3 albedo = mix(grass, scree, smoothstep(20.0, 110.0, height));
            // Steep ground sheds soil, so cliffs stay bare whatever their altitude.
            albedo = mix(albedo, rock, smoothstep(0.35, 0.75, steep));
            // Snow lies on the high ground, but not on faces too steep to hold it.
            float snowLine = smoothstep(150.0, 215.0, height) * (1.0 - smoothstep(0.35, 0.7, steep));
            albedo = mix(albedo, snow, snowLine);

            // Break up the large flat facets a heightfield produces at this cell size.
            albedo *= 0.92 + 0.16 * hash(floor(vWorld.xz * 0.35));

            vec3 skyAmbient = mix(vec3(0.30, 0.33, 0.40), vec3(0.08, 0.09, 0.14), uNight);
            vec3 sunColor   = mix(vec3(1.00, 0.96, 0.88), vec3(0.16, 0.18, 0.28), uNight);
            float diffuse = max(dot(n, uSunDir), 0.0);
            // The city's glow reaches the foothills and fades out up the slopes, which is what
            // keeps the mountains reading as a horizon rather than as a black wall.
            vec3 cityGlow = vec3(0.20, 0.15, 0.10) * uNight
                          * (1.0 - clamp(vWorld.y / 90.0, 0.0, 1.0));
            vec3 lit = albedo * (skyAmbient + cityGlow + sunColor * diffuse);

            float dist = length(vWorld - uCameraPos);
            vec3 fogColor = mix(vec3(0.62, 0.68, 0.76), vec3(0.03, 0.04, 0.07), uNight);
            // Tuned for a 1.4 km city: the original 150m/900m ramp was set when the map was 283m
            // across, and saturated barely two-thirds of the way over the modern one. Distant
            // districts should still read as districts.
            lit = mix(lit, fogColor, clamp((dist - 420.0) / 2600.0, 0.0, 0.55));

            FragColor = vec4(lit, 1.0);
        }
        """;
}

