using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Draws cars as instanced boxes that can actually be rotated.
/// </summary>
/// <remarks>
/// Every other box in the city is axis-aligned, and <see cref="BoxRenderer"/> is built around that:
/// no rotation in the vertex layout, and chunk bounds computed from unrotated extents. Cars broke
/// that assumption the moment they started turning corners, and the old traveller shapes coped by
/// snapping each vehicle to whichever axis it was closest to — which is invisible while cars only
/// ever slide along one straight street, and obvious the instant one takes a bend or climbs a ramp.
///
/// Rather than add a rotation to the twenty-five thousand static boxes that will never use it, cars
/// get their own small buffer: the cube from the box renderer, the yaw-and-pitch transform from the
/// road renderer. It is also cheaper than what it replaces — a couple of hundred simulated cars is
/// a fraction of the fifteen hundred travellers the old buffer was sized for.
/// </remarks>
public sealed unsafe class CarRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        /// <summary>Centre of the box in world space.</summary>
        public Vector3 Center;
        /// <summary>Extent along the car's forward, up and right axes.</summary>
        public Vector3 Size;
        public float Yaw;
        public float Pitch;
        public Vector4 Color;
        /// <summary>Reuses <see cref="BoxFlags"/>; only the emissive bit matters here.</summary>
        public uint Flags;
    }

    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _vbo, _instanceVbo;
    int _capacity;

    public CarRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = CubeVertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(CubeVertices.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);

        const uint vertexStride = 6 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, vertexStride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, vertexStride,
            (void*)(3 * sizeof(float)));

        _instanceVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);

        uint stride = (uint)sizeof(Instance);
        Attrib(2, 3, 0);                        // centre
        Attrib(3, 3, 3 * sizeof(float));        // size
        Attrib(4, 1, 6 * sizeof(float));        // yaw
        Attrib(5, 1, 7 * sizeof(float));        // pitch
        Attrib(6, 4, 8 * sizeof(float));        // colour

        gl.EnableVertexAttribArray(7);
        gl.VertexAttribIPointer(7, 1, VertexAttribIType.UnsignedInt, stride,
            (void*)(12 * sizeof(float)));
        gl.VertexAttribDivisor(7, 1);

        gl.BindVertexArray(0);

        void Attrib(uint index, int components, nint offset)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, components, VertexAttribPointerType.Float, false, stride,
                (void*)offset);
            gl.VertexAttribDivisor(index, 1);
        }
    }

    public void Draw(ReadOnlySpan<Instance> instances, Matrix4x4 viewProjection, Vector3 cameraPos,
        Vector3 sunDirection, float night)
    {
        if (instances.Length == 0) return;

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);

        // Grown, never shrunk, so a busy moment doesn't cause a reallocation every frame after.
        if (instances.Length > _capacity)
        {
            _capacity = Math.Max(instances.Length, _capacity * 2);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_capacity * sizeof(Instance)),
                null, BufferUsageARB.StreamDraw);
        }

        fixed (Instance* p = instances)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                (nuint)(instances.Length * sizeof(Instance)), p);

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProjection);
        _shader.SetVector3("uCameraPos", cameraPos);
        _shader.SetVector3("uSunDir", sunDirection);
        _shader.SetFloat("uNight", night);

        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 36, (uint)instances.Length);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_instanceVbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }

    /// <summary>A unit cube centred on the origin: position(3) + normal(3).</summary>
    static readonly float[] CubeVertices = BuildCube();

    static float[] BuildCube()
    {
        var faces = new (Vector3 Normal, Vector3 A, Vector3 B, Vector3 C, Vector3 D)[]
        {
            (new(0, 0, 1), new(-.5f, -.5f, .5f), new(.5f, -.5f, .5f), new(.5f, .5f, .5f), new(-.5f, .5f, .5f)),
            (new(0, 0, -1), new(.5f, -.5f, -.5f), new(-.5f, -.5f, -.5f), new(-.5f, .5f, -.5f), new(.5f, .5f, -.5f)),
            (new(1, 0, 0), new(.5f, -.5f, .5f), new(.5f, -.5f, -.5f), new(.5f, .5f, -.5f), new(.5f, .5f, .5f)),
            (new(-1, 0, 0), new(-.5f, -.5f, -.5f), new(-.5f, -.5f, .5f), new(-.5f, .5f, .5f), new(-.5f, .5f, -.5f)),
            (new(0, 1, 0), new(-.5f, .5f, .5f), new(.5f, .5f, .5f), new(.5f, .5f, -.5f), new(-.5f, .5f, -.5f)),
            (new(0, -1, 0), new(-.5f, -.5f, -.5f), new(.5f, -.5f, -.5f), new(.5f, -.5f, .5f), new(-.5f, -.5f, .5f)),
        };

        var data = new List<float>(36 * 6);
        foreach (var (normal, a, b, c, d) in faces)
            foreach (var vertex in new[] { a, b, c, a, c, d })
            {
                data.Add(vertex.X); data.Add(vertex.Y); data.Add(vertex.Z);
                data.Add(normal.X); data.Add(normal.Y); data.Add(normal.Z);
            }
        return data.ToArray();
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;
        layout(location=2) in vec3 iCenter;
        layout(location=3) in vec3 iSize;
        layout(location=4) in float iYaw;
        layout(location=5) in float iPitch;
        layout(location=6) in vec4 iColor;
        layout(location=7) in uint iFlags;

        uniform mat4 uViewProj;

        out vec3 vWorld;
        out vec3 vNormal;
        out vec4 vColor;
        flat out uint vFlags;

        // Local axes: x forward, y up, z right. Pitch tilts about the right axis, yaw swings the
        // whole car about world up — the same order the road surfaces use, so a car on a ramp lies
        // flat against it instead of hovering nose-up over the slope.
        vec3 orient(vec3 v, float yaw, float pitch) {
            float cp = cos(pitch), sp = sin(pitch);
            vec3 tilted = vec3(v.x * cp - v.y * sp, v.x * sp + v.y * cp, v.z);
            float c = cos(yaw), s = sin(yaw);
            return vec3(tilted.x * c - tilted.z * s, tilted.y, tilted.x * s + tilted.z * c);
        }

        void main() {
            vec3 local = aPos * iSize;
            vWorld = iCenter + orient(local, iYaw, iPitch);
            vNormal = normalize(orient(aNormal, iYaw, iPitch));
            gl_Position = uViewProj * vec4(vWorld, 1.0);
            vColor = iColor;
            vFlags = iFlags;
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        in vec4 vColor;
        flat in uint vFlags;

        uniform vec3 uCameraPos;
        uniform vec3 uSunDir;
        uniform float uNight;

        out vec4 FragColor;

        const uint FLAG_EMISSIVE = 1u << 6;   // BoxFlags.Emissive

        void main() {
            if ((vFlags & FLAG_EMISSIVE) != 0u) {
                FragColor = vec4(vColor.rgb * (1.4 + 0.6 * uNight), vColor.a);
                return;
            }

            vec3 normal = normalize(vNormal);
            float diffuse = max(dot(normal, normalize(uSunDir)), 0.0);
            vec3 sky = mix(vec3(0.42, 0.46, 0.54), vec3(0.10, 0.11, 0.16), uNight);
            vec3 sun = mix(vec3(1.0, 0.96, 0.88), vec3(0.16, 0.18, 0.26), uNight);
            // Bounce from the city's own lights; see BoxRenderer. A car is always down in it.
            vec3 cityGlow = vec3(0.34, 0.26, 0.17) * uNight;

            vec3 lit = vColor.rgb * (sky * 0.55 + cityGlow + sun * diffuse);

            // A little specular so paintwork reads as paintwork rather than matte plastic.
            vec3 view = normalize(uCameraPos - vWorld);
            vec3 halfway = normalize(view + normalize(uSunDir));
            lit += sun * pow(max(dot(normal, halfway), 0.0), 48.0) * 0.35 * (1.0 - uNight);

            float distance = length(uCameraPos - vWorld);
            vec3 fog = mix(vec3(0.62, 0.68, 0.78), vec3(0.04, 0.05, 0.09), uNight);
            FragColor = vec4(mix(lit, fog, clamp((distance - 420.0) / 2600.0, 0.0, 0.55)), vColor.a);
        }
        """;
}
