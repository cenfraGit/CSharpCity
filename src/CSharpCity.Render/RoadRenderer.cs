using System.Numerics;
using System.Runtime.InteropServices;
using CSharpCity.Layout;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Draws every road as one instanced, yaw-rotated quad. Lane markings â€” edge lines, dashed centre
/// lines, hazard stripes â€” are generated in the fragment shader from the road's local UVs, so a
/// street costs exactly one instance no matter how much paint is on it.
/// </summary>
public sealed unsafe class RoadRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 Center;
        public float Length;
        public float Width;
        public float Yaw;
        public float Pitch;
        public Vector4 Color;
        public uint Flags;
    }

    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _vbo, _instanceVbo;
    int _instanceCount;
    /// <summary>Instances before this index write depth; footpaths after it do not.</summary>
    int _opaqueCount;

    /// <summary>Elapsed seconds, driving the pond ripple. Set once per frame by the window.</summary>
    public float Time { get; set; }

    // A unit quad in the XZ plane, centred on the origin: position(3) + uv(2).
    static readonly float[] QuadVertices =
    {
        -0.5f, 0f, -0.5f,  0f, 0f,
         0.5f, 0f, -0.5f,  1f, 0f,
         0.5f, 0f,  0.5f,  1f, 1f,
        -0.5f, 0f, -0.5f,  0f, 0f,
         0.5f, 0f,  0.5f,  1f, 1f,
        -0.5f, 0f,  0.5f,  0f, 1f,
    };

    public RoadRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = QuadVertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(QuadVertices.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);

        const uint vertexStride = 5 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, vertexStride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, vertexStride,
            (void*)(3 * sizeof(float)));

        _instanceVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        PointInstanceAttributes(0);

        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Aims the per-instance attributes at <paramref name="firstInstance"/>, standing in for
    /// <c>glDrawArraysInstancedBaseInstance</c> which needs GL 4.2 (we target 3.3).
    /// </summary>
    void PointInstanceAttributes(int firstInstance)
    {
        uint stride = (uint)sizeof(Instance);
        nint origin = firstInstance * sizeof(Instance);

        Attrib(2, 3, origin);                        // centre
        Attrib(3, 1, origin + 3 * sizeof(float));    // length
        Attrib(4, 1, origin + 4 * sizeof(float));    // width
        Attrib(5, 1, origin + 5 * sizeof(float));    // yaw
        Attrib(6, 1, origin + 6 * sizeof(float));    // pitch
        Attrib(7, 4, origin + 7 * sizeof(float));    // colour

        _gl.EnableVertexAttribArray(8);
        _gl.VertexAttribIPointer(8, 1, VertexAttribIType.UnsignedInt, stride,
            (void*)(origin + 11 * sizeof(float)));
        _gl.VertexAttribDivisor(8, 1);

        void Attrib(uint index, int components, nint offset)
        {
            _gl.EnableVertexAttribArray(index);
            _gl.VertexAttribPointer(index, components, VertexAttribPointerType.Float, false, stride,
                (void*)offset);
            _gl.VertexAttribDivisor(index, 1);
        }
    }

    /// <summary>
    /// Uploads all roads, opaque first and translucent footpaths last.
    /// </summary>
    /// <remarks>
    /// The split exists so footpaths can be drawn without writing depth. Thousands of dependency
    /// paths cross one another at exactly the same height, and coplanar surfaces that both write
    /// depth flicker against each other â€” the same failure the labels and junction patches had.
    /// Translucent geometry shouldn't write depth anyway: it needs to blend with whatever it
    /// overlaps, not win a depth contest against it.
    /// </remarks>
    /// <param name="visible">
    /// Layers to include. A quad tagged <see cref="CityLayer.Always"/> is never filtered out.
    /// </param>
    public void Upload(IReadOnlyList<RoadQuad> roads, CityLayer visible)
    {
        roads = roads.Where(r => r.Layer == CityLayer.Always || (r.Layer & visible) != 0).ToList();

        static bool IsTranslucent(RoadQuad r) =>
            (r.Flags & (uint)RoadFlags.Footpath) != 0 || r.Color.W < 0.999f;

        var ordered = roads
            .Select((road, index) => (road, index))
            .OrderBy(e => IsTranslucent(e.road) ? 1 : 0)
            .ThenBy(e => e.index)
            .Select(e => e.road)
            .ToList();

        _opaqueCount = ordered.Count(r => !IsTranslucent(r));

        var instances = new Instance[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            var road = ordered[i];
            instances[i] = new Instance
            {
                Center = road.Center,
                Length = road.Length,
                Width = road.Width,
                Yaw = road.Yaw,
                Pitch = road.Pitch,
                Color = road.Color,
                Flags = road.Flags,
            };
        }

        _instanceCount = instances.Length;
        if (_instanceCount == 0) return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (Instance* p = instances)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(instances.Length * sizeof(Instance)),
                p, BufferUsageARB.StaticDraw);
    }

    public void Draw(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection,
        float nightAmount)
    {
        if (_instanceCount == 0) return;

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProjection);
        _shader.SetVector3("uCameraPos", cameraPosition);
        _shader.SetVector3("uSunDir", Vector3.Normalize(sunDirection));
        _shader.SetFloat("uNight", nightAmount);
        _shader.SetFloat("uTime", Time);

        // Roads are single-sided quads with no consistent winding once yaw-rotated.
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);

        if (_opaqueCount > 0)
        {
            PointInstanceAttributes(0);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)_opaqueCount);
        }

        int translucent = _instanceCount - _opaqueCount;
        if (translucent > 0)
        {
            // Depth testing stays on so paths are still hidden by buildings; only the write is off.
            _gl.DepthMask(false);
            PointInstanceAttributes(_opaqueCount);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)translucent);
            _gl.DepthMask(true);
        }

        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.CullFace);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_instanceVbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec2 aUv;
        layout(location=2) in vec3 iCenter;
        layout(location=3) in float iLength;
        layout(location=4) in float iWidth;
        layout(location=5) in float iYaw;
        layout(location=6) in float iPitch;
        layout(location=7) in vec4 iColor;
        layout(location=8) in uint iFlags;

        uniform mat4 uViewProj;

        out vec2 vUv;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec4 vColor;
        flat out uint vFlags;
        out float vAlong;   // metres travelled along the road, for dash phase
        out float vWidth;

        void main() {
            vec2 local = vec2(aPos.x * iLength, aPos.z * iWidth);

            // Tilt along the road's length first, then swing the whole thing round to its heading.
            float cp = cos(iPitch), sp = sin(iPitch);
            vec3 tilted = vec3(local.x * cp, local.x * sp, local.y);

            float c = cos(iYaw), s = sin(iYaw);
            vec2 rotated = vec2(tilted.x * c - tilted.z * s, tilted.x * s + tilted.z * c);

            vWorld = iCenter + vec3(rotated.x, tilted.y, rotated.y);
            gl_Position = uViewProj * vec4(vWorld, 1.0);

            // A ramp's surface faces partly along its climb, so it can't use a constant up-normal.
            vec3 up = vec3(-sp, cp, 0.0);
            vNormal = vec3(up.x * c, up.y, up.x * s);

            vUv = aUv;
            vColor = iColor;
            vFlags = iFlags;
            vAlong = local.x;
            vWidth = iWidth;
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec3 vWorld;
        in vec3 vNormal;
        in vec4 vColor;
        flat in uint vFlags;
        in float vAlong;
        in float vWidth;

        uniform vec3 uCameraPos;
        uniform vec3 uSunDir;
        uniform float uNight;
        uniform float uTime;

        out vec4 FragColor;

        const uint FLAG_EDGE_LINES   = 1u << 0;
        const uint FLAG_DASHED_CENTER= 1u << 1;
        const uint FLAG_HAZARD       = 1u << 2;
        const uint FLAG_GLOW         = 1u << 3;
        const uint FLAG_FOOTPATH     = 1u << 4;
        const uint FLAG_RAIL         = 1u << 5;
        const uint FLAG_POND         = 1u << 6;
        const uint FLAG_SEA          = 1u << 10;
        const uint FLAG_COURT        = 1u << 7;
        const uint FLAG_PARKING      = 1u << 8;
        const uint FLAG_LIGHT_POOL   = 1u << 9;

        // Cheap value noise, for scuffing a dirt path so it doesn't read as a painted stripe.
        float hash(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        const vec3 PAINT = vec3(0.86, 0.86, 0.82);

        void main() {
            vec3 albedo = vColor.rgb;
            vec3 emission = vec3(0.0);
            float alpha = vColor.a;

            // 0 at the centre line, 1 at the kerb.
            float across = abs(vUv.y - 0.5) * 2.0;
            // Keep paint a constant real-world width instead of a fraction of the road, so a
            // four-lane boulevard and a one-lane alley get the same size stripes.
            float paintWidth = clamp(0.16 / max(vWidth, 0.001) * 2.0, 0.02, 0.30);

            if ((vFlags & FLAG_FOOTPATH) != 0u) {
                // A desire line, kept deliberately faint. There is one of these per dependency and
                // thousands of dependencies, so at full opacity they carpet the ground and drown out
                // the city. The people walking them carry the signal; the path only has to hint at
                // where they go.
                float wear = 1.0 - smoothstep(0.1, 1.0, across);
                if (wear < 0.02) discard;   // soft, ragged edge instead of a hard rectangle

                float grit = mix(0.92, 1.08, hash(floor(vec2(vAlong * 1.7, vUv.y * 6.0))));
                albedo = vColor.rgb * grit * 1.25;
                alpha = wear * 0.22;
            }

            if ((vFlags & FLAG_COURT) != 0u) {
                // Standard pitch markings: touchlines, halfway line, centre circle.
                vec2 p = vUv;
                float paint = step(p.x, 0.025) + step(0.975, p.x)
                            + step(p.y, 0.035) + step(0.965, p.y)
                            + step(abs(p.x - 0.5), 0.010);

                // Squashed on one axis so the circle stays round on a non-square pitch.
                float ring = length((p - 0.5) * vec2(1.0, 0.62));
                paint += step(abs(ring - 0.135), 0.010);

                albedo = mix(albedo, vec3(0.93, 0.94, 0.92), clamp(paint, 0.0, 1.0));
            }

            if ((vFlags & FLAG_LIGHT_POOL) != 0u) {
                // The lit ground under a lamp. A cone of light is really only visible where it
                // lands, so this is the landing rather than the cone: a disc that falls off toward
                // its edge, brightest at the foot of the post, and absent in daylight.
                float radius = length(vUv - 0.5) * 2.0;
                float fall = 1.0 - smoothstep(0.15, 1.0, radius);
                float night = smoothstep(0.15, 0.75, uNight);

                // Emitted, not reflected: the pavement is being lit, not painted.
                FragColor = vec4(vColor.rgb * fall * 1.35, vColor.a * fall * fall * night);
                return;
            }

            if ((vFlags & FLAG_PARKING) != 0u) {
                // Bays down both sides with an aisle between them, marked out in real metres so
                // the spaces stay car-sized however big the car park is.
                float bay = 2.6;
                float line = fract(vAlong / bay);
                float stripe = 1.0 - smoothstep(0.0, 0.055, min(line, 1.0 - line));

                // The middle third is the aisle; bays are only painted either side of it.
                float fromCentre = abs(vUv.y - 0.5) * 2.0;
                float inBay = step(0.34, fromCentre) * step(fromCentre, 0.94);

                float paint = stripe * inBay;
                // A kerb line right round the edge, so the lot has an outline from the air.
                paint += step(0.965, fromCentre);
                paint += 1.0 - smoothstep(0.0, 0.006, min(vUv.x, 1.0 - vUv.x));

                albedo = mix(albedo, vec3(0.86, 0.86, 0.82), clamp(paint, 0.0, 1.0) * 0.85);
            }

            // Open sea. No outline to carve: it runs to the edge of its quad, and what shapes the
            // coast is the land rising through it, not the water stopping.
            if ((vFlags & FLAG_SEA) != 0u) {
                // Three wave trains at different scales and bearings. Two is enough to see the
                // pattern repeat once the horizon is a kilometre away; three is not.
                float swell = sin(vWorld.x * 0.021 + uTime * 0.55)
                            + sin(vWorld.z * 0.017 - uTime * 0.42)
                            + 0.6 * sin((vWorld.x + vWorld.z) * 0.045 + uTime * 0.9);

                vec3 deep    = vec3(0.04, 0.11, 0.20);
                vec3 crest   = vec3(0.16, 0.38, 0.50);
                albedo = mix(deep, crest, clamp(0.5 + 0.22 * swell, 0.0, 1.0));

                // A specular streak where the sun would sit, which is most of what makes a flat
                // plane read as water rather than as blue floor.
                float glint = pow(clamp(0.5 + 0.5 * swell, 0.0, 1.0), 14.0);
                albedo += vec3(0.55, 0.62, 0.60) * glint * (0.7 - 0.45 * uNight);

                // Moonlight instead of sun after dark.
                albedo *= 1.0 - 0.45 * uNight;
            }

            if ((vFlags & FLAG_POND) != 0u) {
                // Carve an irregular outline out of the quad rather than shipping blob geometry:
                // three harmonics of the bearing give a natural, non-repeating shoreline.
                vec2 p = (vUv - 0.5) * 2.0;
                float bearing = atan(p.y, p.x);
                float shore = 0.74
                            + 0.15 * sin(bearing * 3.0 + 1.3)
                            + 0.08 * sin(bearing * 5.0 - 0.7)
                            + 0.05 * sin(bearing * 8.0 + 2.4);

                float r = length(p);
                if (r > shore) discard;

                // Two crossing wave trains, so the surface moves without ever repeating obviously.
                float ripple = sin(vAlong * 0.85 + uTime * 1.15)
                             + sin(vUv.y * 13.0 - uTime * 0.8);
                albedo = mix(vec3(0.07, 0.19, 0.28), vec3(0.30, 0.55, 0.64), 0.5 + 0.25 * ripple);

                // Shallows: the water lightens and muddies as it meets the bank.
                float shallow = smoothstep(shore, shore * 0.72, r);
                albedo = mix(vec3(0.30, 0.31, 0.22), albedo, shallow);
            }

            if ((vFlags & FLAG_RAIL) != 0u) {
                // Ballast with sleepers banded across it, every 2.4 m of track.
                float sleeper = step(0.45, fract(vAlong / 2.4));
                float shoulder = smoothstep(0.55, 1.0, across);   // ballast falls away at the edges
                albedo = mix(vColor.rgb * 0.72, vColor.rgb * 1.25, sleeper);
                albedo *= 1.0 - shoulder * 0.45;
            }

            if ((vFlags & FLAG_HAZARD) != 0u) {
                // Diagonal stripes: the one road marking that means "do not proceed as normal".
                float stripe = step(0.5, fract((vAlong + vUv.y * 4.0) / 2.2));
                albedo = mix(vec3(0.16, 0.14, 0.10), vec3(0.92, 0.68, 0.06), stripe);
                emission = albedo * uNight * 0.45;
            }

            if ((vFlags & FLAG_EDGE_LINES) != 0u) {
                float edge = smoothstep(1.0 - paintWidth * 2.4, 1.0 - paintWidth * 1.2, across)
                           * (1.0 - smoothstep(1.0 - paintWidth * 0.4, 1.0, across));
                albedo = mix(albedo, PAINT, edge);
                emission += PAINT * edge * uNight * 0.35;
            }

            if ((vFlags & FLAG_DASHED_CENTER) != 0u) {
                // 3 m of paint, 3 m of gap â€” close enough to a real road to read instantly.
                float dash = step(fract(vAlong / 6.0), 0.5);
                float centre = 1.0 - smoothstep(paintWidth * 0.6, paintWidth * 1.4, across);
                albedo = mix(albedo, PAINT, centre * dash);
                emission += PAINT * centre * dash * uNight * 0.35;
            }

            if ((vFlags & FLAG_GLOW) != 0u) {
                // Dependency routes carry a lit spine so they're legible at night and from the air.
                float spine = 1.0 - smoothstep(0.0, 0.45, across);
                vec3 tint = normalize(vColor.rgb + vec3(0.08)) * 1.6;
                emission += tint * spine * (0.28 + 0.55 * uNight);
            }

            vec3 skyAmbient = mix(vec3(0.30, 0.33, 0.40), vec3(0.09, 0.10, 0.15), uNight);
            vec3 sunColor   = mix(vec3(1.00, 0.96, 0.88), vec3(0.18, 0.20, 0.30), uNight);
            float diffuse = max(dot(normalize(vNormal), uSunDir), 0.0);
            // Bounce from the city's own lights; see BoxRenderer for why this stands in for them.
            // Road surfaces get the most of it — they are what the street lamps are pointed at.
            vec3 cityGlow = vec3(0.34, 0.26, 0.17) * uNight
                          * (1.0 - clamp((vWorld.y - 3.0) / 70.0, 0.0, 1.0));
            vec3 lit = albedo * (skyAmbient + cityGlow + sunColor * diffuse) + emission;

            float dist = length(vWorld - uCameraPos);
            vec3 fogColor = mix(vec3(0.62, 0.68, 0.76), vec3(0.03, 0.04, 0.07), uNight);
            // Tuned for a 1.4 km city: the original 150m/900m ramp was set when the map was 283m
            // across, and saturated barely two-thirds of the way over the modern one. Distant
            // districts should still read as districts.
            lit = mix(lit, fogColor, clamp((dist - 420.0) / 2600.0, 0.0, 0.55));

            FragColor = vec4(lit, alpha);
        }
        """;
}

