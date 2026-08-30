using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace CSharpCity.Render;

/// <summary>
/// Draws the entire city as instanced unit cubes: one VBO, one draw call for every building
/// and every slab of pavement. Facade detail (windows, grime, glass) is done in the fragment
/// shader from the per-instance size and flag bits rather than with real geometry.
/// </summary>
public sealed unsafe class BoxRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 BasePosition;
        public Vector3 Size;
        public Vector4 Color;
        public uint Flags;
        /// <summary>Windows across the facade of this box.</summary>
        public float Detail;
        /// <summary>Fraction of panes smashed, 0..1.</summary>
        public float Damage;
    }

    /// <summary>A spatially contiguous run of instances, culled as a unit.</summary>
    public readonly struct Chunk
    {
        public readonly int Start, Count;
        public readonly Vector3 Min, Max;
        /// <summary>Drawn in a second pass without depth writes, so it can blend with what's behind.</summary>
        public readonly bool Translucent;

        public Chunk(int start, int count, Vector3 min, Vector3 max, bool translucent)
        {
            Start = start; Count = count; Min = min; Max = max; Translucent = translucent;
        }
    }

    readonly GL _gl;
    readonly Shader _shader;
    readonly uint _vao, _vbo, _ebo, _instanceVbo;
    int _instanceCount;

    public BoxRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = CubeVertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(CubeVertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = CubeIndices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(CubeIndices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        // Per-vertex: position(3), normal(3), face uv(2)
        const uint vertexStride = 8 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, vertexStride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, vertexStride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, vertexStride, (void*)(6 * sizeof(float)));

        // Per-instance: basePos(3), size(3), color(4), flags(uint), detail(1)
        _instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        PointInstanceAttributes(0);

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Aims the per-instance attributes at <paramref name="firstInstance"/>.
    /// </summary>
    /// <remarks>
    /// This stands in for <c>glDrawElementsInstancedBaseInstance</c>, which needs GL 4.2 â€” we target
    /// 3.3, where an instanced draw always starts at instance zero. Re-pointing the attributes is
    /// the portable way to draw a slice of the instance buffer, and it's what makes per-chunk
    /// frustum culling possible without re-uploading geometry every frame.
    /// </remarks>
    void PointInstanceAttributes(int firstInstance)
    {
        uint stride = (uint)sizeof(Instance);
        nint origin = firstInstance * sizeof(Instance);

        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)origin);
        _gl.VertexAttribDivisor(3, 1);
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, stride,
            (void*)(origin + 3 * sizeof(float)));
        _gl.VertexAttribDivisor(4, 1);
        _gl.EnableVertexAttribArray(5);
        _gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride,
            (void*)(origin + 6 * sizeof(float)));
        _gl.VertexAttribDivisor(5, 1);
        _gl.EnableVertexAttribArray(6);
        _gl.VertexAttribIPointer(6, 1, VertexAttribIType.UnsignedInt, stride,
            (void*)(origin + 10 * sizeof(float)));
        _gl.VertexAttribDivisor(6, 1);
        _gl.EnableVertexAttribArray(7);
        _gl.VertexAttribPointer(7, 1, VertexAttribPointerType.Float, false, stride,
            (void*)(origin + 11 * sizeof(float)));
        _gl.VertexAttribDivisor(7, 1);
        _gl.EnableVertexAttribArray(8);
        _gl.VertexAttribPointer(8, 1, VertexAttribPointerType.Float, false, stride,
            (void*)(origin + 12 * sizeof(float)));
        _gl.VertexAttribDivisor(8, 1);
    }

    public void Upload(ReadOnlySpan<Instance> instances)
    {
        _instanceCount = instances.Length;
        if (_instanceCount == 0) return;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (Instance* p = instances)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(instances.Length * sizeof(Instance)), p, BufferUsageARB.StaticDraw);
    }

    public void Draw(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection, float nightAmount)
    {
        if (_instanceCount == 0) return;
        Bind(viewProjection, cameraPosition, sunDirection, nightAmount);
        PointInstanceAttributes(0);
        _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)CubeIndices.Length,
            DrawElementsType.UnsignedInt, (void*)0, (uint)_instanceCount);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws only the chunks the camera can see. Returns how many instances were actually issued,
    /// so the caller can report the culling ratio.
    /// </summary>
    public int DrawChunks(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection,
        float nightAmount, IReadOnlyList<Chunk> chunks, in Frustum frustum)
    {
        if (_instanceCount == 0 || chunks.Count == 0) return 0;

        Bind(viewProjection, cameraPosition, sunDirection, nightAmount);

        int drawn = 0;

        // Opaque first, then translucent with depth writes off. Without the split, a tall
        // translucent object â€” a searchlight beam, a smog slab, a glass pavilion â€” writes depth
        // across everything behind it and punches a hole in the city.
        for (int pass = 0; pass < 2; pass++)
        {
            bool translucentPass = pass == 1;
            if (translucentPass) _gl.DepthMask(false);

            foreach (var chunk in chunks)
            {
                if (chunk.Translucent != translucentPass) continue;
                if (chunk.Count == 0 || !frustum.Intersects(chunk.Min, chunk.Max)) continue;

                PointInstanceAttributes(chunk.Start);
                _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)CubeIndices.Length,
                    DrawElementsType.UnsignedInt, (void*)0, (uint)chunk.Count);
                drawn += chunk.Count;
            }

            if (translucentPass) _gl.DepthMask(true);
        }

        _gl.BindVertexArray(0);
        return drawn;
    }

    /// <summary>Elapsed seconds, driving the flame flicker. Set once per frame by the window.</summary>
    public float Time { get; set; }

    void Bind(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection, float nightAmount)
    {
        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProjection);
        _shader.SetVector3("uCameraPos", cameraPosition);
        _shader.SetVector3("uSunDir", Vector3.Normalize(sunDirection));
        _shader.SetFloat("uNight", nightAmount);
        _shader.SetFloat("uTime", Time);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_instanceVbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }

    // Unit cube spanning X/Z in [-0.5, 0.5] and Y in [0, 1] â€” so an instance's position is the
    // centre of its base and buildings sit on the ground without any offset maths.
    static readonly float[] CubeVertices =
    {
        // pos                  normal            uv
        // +X
        0.5f,0f,0.5f,           1,0,0,            0,0,
        0.5f,0f,-0.5f,          1,0,0,            1,0,
        0.5f,1f,-0.5f,          1,0,0,            1,1,
        0.5f,1f,0.5f,           1,0,0,            0,1,
        // -X
        -0.5f,0f,-0.5f,        -1,0,0,            0,0,
        -0.5f,0f,0.5f,         -1,0,0,            1,0,
        -0.5f,1f,0.5f,         -1,0,0,            1,1,
        -0.5f,1f,-0.5f,        -1,0,0,            0,1,
        // +Z
        -0.5f,0f,0.5f,          0,0,1,            0,0,
        0.5f,0f,0.5f,           0,0,1,            1,0,
        0.5f,1f,0.5f,           0,0,1,            1,1,
        -0.5f,1f,0.5f,          0,0,1,            0,1,
        // -Z
        0.5f,0f,-0.5f,          0,0,-1,           0,0,
        -0.5f,0f,-0.5f,         0,0,-1,           1,0,
        -0.5f,1f,-0.5f,         0,0,-1,           1,1,
        0.5f,1f,-0.5f,          0,0,-1,           0,1,
        // +Y (roof)
        -0.5f,1f,0.5f,          0,1,0,            0,0,
        0.5f,1f,0.5f,           0,1,0,            1,0,
        0.5f,1f,-0.5f,          0,1,0,            1,1,
        -0.5f,1f,-0.5f,         0,1,0,            0,1,
        // -Y
        -0.5f,0f,-0.5f,         0,-1,0,           0,0,
        0.5f,0f,-0.5f,          0,-1,0,           1,0,
        0.5f,0f,0.5f,           0,-1,0,           1,1,
        -0.5f,0f,0.5f,          0,-1,0,           0,1,
    };

    static readonly uint[] CubeIndices = BuildIndices();

    static uint[] BuildIndices()
    {
        var indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint v = face * 4, i = face * 6;
            indices[i + 0] = v + 0; indices[i + 1] = v + 1; indices[i + 2] = v + 2;
            indices[i + 3] = v + 2; indices[i + 4] = v + 3; indices[i + 5] = v + 0;
        }
        return indices;
    }

    const string VertexSource = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;
        layout(location=2) in vec2 aUv;
        layout(location=3) in vec3 iBase;
        layout(location=4) in vec3 iSize;
        layout(location=5) in vec4 iColor;
        layout(location=6) in uint iFlags;
        layout(location=7) in float iDetail;
        layout(location=8) in float iDamage;

        uniform mat4 uViewProj;
        uniform float uTime;

        out vec3 vNormal;
        out vec4 vColor;
        out vec3 vWorld;
        out vec2 vFacadeUv;
        flat out uint vFlags;
        out float vHeightFrac;
        out float vStoreyHeight;
        out float vDamage;
        // Local position within the unit cube, and the instance's own size. Between them the
        // fragment stage can work out where it is inside the box and carve a cylinder out of it.
        out vec3 vLocal;
        out vec3 vSize;

        float vhash(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        void main() {
            vec3 local = aPos;

            // Cones taper toward the top. The cube runs from y=0 at its base to y=1, so scaling the
            // horizontal axes by the height fraction turns four flat sides into a tapered one, and
            // leaves a small flat top the way a real traffic cone has.
            if ((iFlags & 2048u) != 0u) local.xz *= 1.0 - aPos.y * 0.86;

            vec3 world = local * iSize + iBase;

            // Flame and smoke are the only geometry in the city that moves under its own power.
            // Displacing in the vertex shader is far cheaper than re-uploading their instances every
            // frame, and it scales with height so each box's base stays anchored while its top sways.
            if ((iFlags & (128u | 256u | 1024u)) != 0u) {
                float seed = vhash(floor(iBase.xz * 1.7));
                bool smoke = (iFlags & 256u) != 0u;
                bool water = (iFlags & 1024u) != 0u;

                // A jet under pressure wobbles fast and narrow; smoke rolls slow and wide.
                float rate = water ? 9.0 : (smoke ? 0.9 : 5.5);
                float reach = water ? 0.35 : (smoke ? 1.5 : 0.5);
                float phase = uTime * rate + seed * 51.0;

                world.x += sin(phase) * reach * aPos.y;
                world.z += cos(phase * 0.73) * reach * 0.7 * aPos.y;
                // Flames also breathe vertically; smoke swells as it rises.
                world.y += (smoke ? 0.5 : 0.28) * sin(phase * (smoke ? 0.6 : 1.9)) * aPos.y;
            }

            gl_Position = uViewProj * vec4(world, 1.0);
            vNormal = aNormal;
            vColor = iColor;
            vWorld = world;
            vFlags = iFlags;
            vHeightFrac = aPos.y;
            vStoreyHeight = iSize.y;
            vDamage = iDamage;
            vLocal = aPos;
            vSize = iSize;

            // One box is one storey, so the facade carries exactly one row of iDetail windows.
            // A method's parameter count therefore reads directly as windows across.
            vFacadeUv = vec2(aUv.x * max(iDetail, 1.0), aUv.y);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec3 vNormal;
        in vec4 vColor;
        in vec3 vWorld;
        in vec2 vFacadeUv;
        flat in uint vFlags;
        in float vHeightFrac;
        in float vStoreyHeight;
        in float vDamage;
        in vec3 vLocal;
        in vec3 vSize;

        uniform vec3 uCameraPos;
        uniform vec3 uSunDir;
        uniform float uNight;
        uniform float uTime;

        out vec4 FragColor;

        const uint FLAG_WINDOWS   = 1u;
        const uint FLAG_LIT       = 2u;
        const uint FLAG_GRIMY     = 4u;
        const uint FLAG_GLASS     = 8u;
        const uint FLAG_ABANDONED = 16u;
        const uint FLAG_SCAFFOLD  = 32u;
        const uint FLAG_EMISSIVE  = 64u;
        const uint FLAG_FIRE      = 128u;
        const uint FLAG_BEACON    = 512u;
        const uint FLAG_WATER     = 1024u;
        const uint FLAG_CONE      = 2048u;
        const uint FLAG_ROUND     = 4096u;
        const uint FLAG_GHOST     = 8192u;
        const uint FLAG_DAMP      = 16384u;

        // Cheap value noise for grime.
        float hash(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        void main() {
            vec3 albedo = vColor.rgb;
            float alpha = vColor.a;
            bool isSide = abs(vNormal.y) < 0.5;

            // Live flame. Flickers, runs cool-to-hot up its own height, and burns much brighter after
            // dark â€” a swallowed exception should be the thing you see first at night.
            if ((vFlags & FLAG_FIRE) != 0u) {
                float seed = hash(floor(vWorld.xz * 2.3));
                float flicker = 0.66
                              + 0.46 * sin(uTime * 11.0 + seed * 43.0)
                              + 0.26 * sin(uTime * 27.0 + seed * 17.0 + vWorld.y * 2.4)
                              + 0.14 * sin(uTime * 41.0 + seed * 7.0);

                // Hot yellow core at the top of each box, deep red where it meets the wall.
                vec3 hot = mix(albedo, vec3(1.0, 0.95, 0.62), pow(vHeightFrac, 1.6) * 0.85);
                FragColor = vec4(hot * flicker * (2.1 + 1.9 * uNight), alpha);
                return;
            }

            // Water under pressure: shimmering, self-lit, brighter where it's densest.
            if ((vFlags & FLAG_WATER) != 0u) {
                float seed = hash(floor(vWorld.xz * 3.0));
                float shimmer = 0.72 + 0.30 * sin(uTime * 16.0 + vWorld.y * 2.6 + seed * 30.0)
                                     + 0.16 * sin(uTime * 31.0 + seed * 11.0);
                vec3 spray = mix(vec3(0.42, 0.66, 0.95), vec3(0.95, 0.99, 1.0), shimmer * 0.6);
                FragColor = vec4(spray * shimmer * (1.35 + 1.1 * uNight), alpha);
                return;
            }

            // Emergency beacon: hard strobe between its own colour and white, offset per vehicle so
            // a row of them doesn't pulse in unison.
            if ((vFlags & FLAG_BEACON) != 0u) {
                float offset = hash(floor(vWorld.xz * 4.1)) * 6.28;
                float strobe = step(0.55, fract(uTime * 2.6 + offset));
                float pop = 0.35 + 0.65 * step(0.82, fract(uTime * 2.6 + offset));
                vec3 lamp = mix(albedo, vec3(1.0), strobe * 0.55);
                FragColor = vec4(lamp * (1.6 + 2.4 * uNight) * pop, alpha);
                return;
            }

            // Hazard lights ignore the sun entirely and stay readable at night.
            // Carved before anything else is decided, so a discarded pixel costs nothing further.
            if ((vFlags & FLAG_ROUND) != 0u) {
                // The axle is whichever axis the shape is thinnest on — a wheel is a thin disc, so
                // this needs no extra data per instance and works whichever way the bike is facing.
                // Local x and z run -0.5..0.5 and y runs 0..1, hence the offset on y.
                vec3 centred = vec3(vLocal.x, vLocal.y - 0.5, vLocal.z);
                vec2 across = vSize.x <= min(vSize.y, vSize.z) ? centred.yz
                            : (vSize.y <= vSize.z ? centred.xz : centred.xy);

                if (length(across) > 0.5) discard;
            }

            if ((vFlags & FLAG_CONE) != 0u) {
                // The reflective band, which is most of what makes a cone read as a cone from any
                // distance at which you cannot see its shape.
                float band = step(0.42, vHeightFrac) * step(vHeightFrac, 0.63);
                vec3 sleeve = mix(vColor.rgb, vec3(0.94, 0.94, 0.92), band);
                FragColor = vec4(sleeve * (1.15 + 0.5 * uNight), vColor.a);
                return;
            }

            // A proposal, not a building: drawn rather than built, so it reads as a survey drawing
            // standing up in the air. Bright edges give it a silhouette that survives being seen
            // against a solid facade, and the setting-out lines climbing it say "this is a drawing"
            // in a way that a merely faint box never did â€” the city already has eight kinds of
            // translucent scenery, and one more would just read as smog.
            if ((vFlags & FLAG_GHOST) != 0u) {
                vec2 face = abs(vLocal.xz);
                float edge = max(smoothstep(0.42, 0.5, face.x), smoothstep(0.42, 0.5, face.y));
                edge = max(edge, smoothstep(0.94, 1.0, vLocal.y));

                float lines = smoothstep(0.72, 1.0, fract(vLocal.y * vSize.y * 0.34));
                float glow = clamp(edge + lines * 0.55, 0.0, 1.0);

                vec3 drawn = albedo * (0.7 + 1.7 * glow);
                FragColor = vec4(drawn * (1.1 + 0.5 * uNight),
                                 clamp(alpha + glow * 0.55, 0.0, 1.0));
                return;
            }

            if ((vFlags & FLAG_EMISSIVE) != 0u) {
                FragColor = vec4(albedo * (1.4 + 0.6 * uNight), alpha);
                return;
            }

            // Light a window emits is added after shading, not multiplied by it â€” otherwise night
            // ambient scales the glow down to nothing and the city looks unlit.
            vec3 emission = vec3(0.0);

            // --- one row of windows per storey; count across = the method's parameter count ---
            if (isSide && (vFlags & FLAG_WINDOWS) != 0u && vStoreyHeight > 2.0) {
                vec2 cell = vec2(fract(vFacadeUv.x), vFacadeUv.y);
                // Tall storeys (long methods) get a proportionally tall window: the stretch is the point.
                bool inWindow = cell.x > 0.28 && cell.x < 0.72
                             && cell.y > 0.22 && cell.y < 0.82;
                if (inWindow) {
                    // A possible-null dereference is a hole where a value should be. Enough of them
                    // and the facade is a smashed shell â€” and a null-safe type keeps all its glass.
                    float paneId = floor(vFacadeUv.x) * 13.0 + floor(vStoreyHeight * 7.0);
                    bool smashed = hash(vec2(paneId, vStoreyHeight * 3.1)) < vDamage;

                    if (smashed) {
                        // Dark void behind, with a rim of jagged glass teeth catching the light.
                        float rim = max(
                            smoothstep(0.34, 0.28, cell.x) + smoothstep(0.66, 0.72, cell.x),
                            smoothstep(0.28, 0.23, cell.y) + smoothstep(0.76, 0.81, cell.y));
                        float shard = step(0.55, hash(floor(cell * 11.0) + paneId));
                        albedo = mix(vec3(0.025, 0.025, 0.03),
                                     vec3(0.62, 0.68, 0.70), rim * shard * 0.8);
                    }
                    else if ((vFlags & FLAG_ABANDONED) != 0u) {
                        // Boarded up: horizontal planks, never lit.
                        float plank = step(0.5, fract(cell.y * 3.0));
                        albedo = mix(vec3(0.30, 0.22, 0.15), vec3(0.22, 0.16, 0.11), plank);
                    } else {
                        bool lit = (vFlags & FLAG_LIT) != 0u;
                        // At night a public facade glows; a private one stays dark.
                        vec3 glass = vec3(0.10, 0.12, 0.16);
                        vec3 glow  = vec3(1.00, 0.86, 0.55);
                        albedo = lit ? mix(glass, glow, uNight) : glass * (1.0 - 0.6 * uNight);
                        if (lit) {
                            // Hot core, softer edge, so the pane reads as a light source rather
                            // than a flat yellow rectangle. Kept just over 1.0 so the pane still
                            // clips warm-white, but under the top of the bloom knee's ramp
                            // (smoothstep(0.75, 1.35) in PostProcess) — past that the halo grows
                            // faster than the light and a lit district turns into a smear.
                            vec2 fromCentre = abs(cell - vec2(0.5, 0.52)) / vec2(0.22, 0.30);
                            float falloff = 1.0 - smoothstep(0.35, 1.0, max(fromCentre.x, fromCentre.y));
                            emission = glow * uNight * mix(0.85, 1.55, falloff);
                        }
                    }
                }
            }

            // --- scaffolding stripes for abstract classes ---
            if (isSide && (vFlags & FLAG_SCAFFOLD) != 0u) {
                float band = step(0.5, fract(vFacadeUv.y / 2.5));
                albedo = mix(albedo, vec3(0.85, 0.60, 0.10), band * 0.35);
            }

            // --- grime: complexity made visible as soot streaking down the facade ---
            if (isSide && (vFlags & FLAG_GRIMY) != 0u) {
                float streak = hash(floor(vec2(vFacadeUv.x * 3.0, 0.0)));
                float soot = smoothstep(0.2, 1.0, streak) * (1.0 - vHeightFrac * 0.5);
                albedo = mix(albedo, vec3(0.12, 0.10, 0.09), soot * 0.55);
            }

            // --- damp: no test reaches the method this storey stands for ---
            // Green, and rising from the slab rather than running down from the top, so it can
            // never be confused with the soot above it. Two kinds of neglect on one wall have to
            // differ in more than intensity to stay readable.
            if (isSide && (vFlags & FLAG_DAMP) != 0u) {
                float tide = 1.0 - smoothstep(0.0, 0.62, vHeightFrac);
                float mottle = hash(floor(vec2(vFacadeUv.x * 5.0, vHeightFrac * 6.0)));
                float moss = tide * (0.45 + 0.55 * mottle);
                albedo = mix(albedo, vec3(0.16, 0.30, 0.14), moss * 0.62);
            }

            // --- lighting: one directional sun, hemispheric ambient, vertical fake AO ---
            vec3 n = normalize(vNormal);
            float diffuse = max(dot(n, uSunDir), 0.0);
            vec3 skyAmbient = mix(vec3(0.30, 0.33, 0.40), vec3(0.09, 0.10, 0.15), uNight);
            vec3 sunColor   = mix(vec3(1.00, 0.96, 0.88), vec3(0.18, 0.20, 0.30), uNight);
            float ao = mix(0.55, 1.0, clamp(vHeightFrac * 2.5, 0.0, 1.0));

            // The light the city throws back on itself after dark. Every lamp, window and headlamp
            // in the place is emissive-only: it glows, but it lights nothing, so the night was lit
            // by moonlight alone and everything below the rooftops went black. Rather than pay for
            // real point lights, this is the bounce they would produce — warm, and strongest near
            // the ground where the sources are.
            vec3 cityGlow = vec3(0.30, 0.23, 0.15) * uNight
                          * (1.0 - clamp((vWorld.y - 3.0) / 70.0, 0.0, 1.0));

            // Emission is added outside the AO/diffuse term: a lit pane doesn't care about the sun.
            vec3 lit = albedo * (skyAmbient + cityGlow + sunColor * diffuse) * ao + emission;

            // Distance fog doubles as the district smog layer once Phase 4 lands.
            float dist = length(vWorld - uCameraPos);
            vec3 fogColor = mix(vec3(0.62, 0.68, 0.76), vec3(0.03, 0.04, 0.07), uNight);
            // Tuned for a 1.4 km city: the original 150m/900m ramp was set when the map was 283m
            // across, and saturated barely two-thirds of the way over the modern one. Distant
            // districts should still read as districts.
            lit = mix(lit, fogColor, clamp((dist - 420.0) / 2600.0, 0.0, 0.55));

            if ((vFlags & FLAG_GLASS) != 0u) {
                // Fresnel rim so hollow interface pavilions still read as solid shapes.
                vec3 viewDir = normalize(uCameraPos - vWorld);
                float fresnel = pow(1.0 - max(dot(n, viewDir), 0.0), 2.0);
                lit += vec3(0.35, 0.55, 0.70) * fresnel * 0.6;
                alpha = clamp(alpha + fresnel * 0.4, 0.0, 1.0);
            }

            FragColor = vec4(lit, alpha);
        }
        """;
}

