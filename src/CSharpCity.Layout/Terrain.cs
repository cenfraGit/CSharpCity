using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>A heightfield mesh: interleaved position and normal, ready to upload.</summary>
public sealed class TerrainMesh
{
    /// <summary>Six floats per vertex â€” position(3), normal(3).</summary>
    public float[] Vertices = Array.Empty<float>();
    public uint[] Indices = Array.Empty<uint>();
}

/// <summary>
/// A mountain range enclosing the city, generated as a real heightfield rather than stacked boxes.
/// </summary>
/// <remarks>
/// The first attempt built peaks from axis-aligned cubes, like everything else in the city, and it
/// looked exactly like what it was: terraced ziggurats. Terrain is the one thing here that genuinely
/// needs smooth geometry â€” a mountain is defined by its slopes, and a box has none. So this emits an
/// actual triangle mesh with per-vertex normals, lit as a surface.
///
/// The ground stays perfectly flat across the city itself and only lifts beyond its edge, so the
/// range never intrudes on the layout. Height comes from fractal noise multiplied by that ramp,
/// which is what stops the border reading as a uniform wall.
/// </remarks>
internal static class Terrain
{
    /// <summary>How far the mesh extends past the city.</summary>
    const float Extent = 520f;
    /// <summary>Flat ground is preserved this far beyond the last building.</summary>
    const float FlatMargin = 18f;
    /// <summary>Distance over which the range climbs to full height. Short, so the peaks sit close.</summary>
    const float RampWidth = 300f;
    const float PeakHeight = 250f;
    const float CellSize = 9f;

    /// <summary>
    /// How far the whole mesh is dropped below the city floor.
    /// </summary>
    /// <remarks>
    /// The flat skirt of the heightfield would otherwise sit at exactly y=0, precisely coplanar with
    /// the top face of the bedrock slab, and two coplanar surfaces flicker no matter how good the
    /// depth buffer is. Sinking the terrain means the foothills emerge <em>through</em> the ground
    /// rather than lying on it â€” an intersection instead of a tie.
    /// </remarks>
    internal const float Sink = 1.1f;

    public static void Build(SceneGraph scene, Bounds2 city)
    {
        float minX = city.X - Extent, minZ = city.Z - Extent;
        float maxX = city.X + city.Width + Extent, maxZ = city.Z + city.Depth + Extent;

        int columns = (int)MathF.Ceiling((maxX - minX) / CellSize) + 1;
        int rows = (int)MathF.Ceiling((maxZ - minZ) / CellSize) + 1;

        var vertices = new float[columns * rows * 6];
        // Raw heights, kept separate from the sunk vertex positions so "is this the flat skirt?"
        // stays an exact test rather than a comparison against a shifted value.
        var raw = new float[columns * rows];

        for (int z = 0; z < rows; z++)
        for (int x = 0; x < columns; x++)
        {
            float wx = minX + x * CellSize;
            float wz = minZ + z * CellSize;
            float wy = Height(wx, wz, city);
            raw[z * columns + x] = wy;

            // Central differences against the same function give exact normals for free.
            float dx = Height(wx + CellSize, wz, city) - Height(wx - CellSize, wz, city);
            float dz = Height(wx, wz + CellSize, city) - Height(wx, wz - CellSize, city);
            var normal = Vector3.Normalize(new Vector3(-dx, 2f * CellSize, -dz));

            int at = (z * columns + x) * 6;
            vertices[at] = wx;
            vertices[at + 1] = wy - Sink;
            vertices[at + 2] = wz;
            vertices[at + 3] = normal.X;
            vertices[at + 4] = normal.Y;
            vertices[at + 5] = normal.Z;
        }

        // The flat skirt is emitted too, rather than skipped. Skipping it left the enormous bedrock
        // slab as the ground beneath and beside the city, a hand's width below the district plates —
        // and two surfaces that close cannot be told apart from a kilometre up, whatever the depth
        // buffer. The terrain now *is* the ground, a clear metre below everything built on it.
        var indices = new List<uint>(columns * rows * 6);
        for (int z = 0; z < rows - 1; z++)
        for (int x = 0; x < columns - 1; x++)
        {
            uint a = (uint)(z * columns + x);
            uint b = a + 1;
            uint c = (uint)((z + 1) * columns + x);
            uint d = c + 1;

            indices.Add(a); indices.Add(c); indices.Add(b);
            indices.Add(b); indices.Add(c); indices.Add(d);
        }

        scene.Terrain = new TerrainMesh { Vertices = vertices, Indices = indices.ToArray() };
        PlantTrees(scene, city, minX, minZ, maxX, maxZ);
    }

    /// <summary>
    /// Ground height at a point. Zero across the whole city, climbing outside it.
    /// </summary>
    static float Height(float x, float z, Bounds2 city)
    {
        float outside = DistanceOutside(city, x, z);
        if (outside <= FlatMargin) return 0f;

        float ramp = Smoothstep(FlatMargin, FlatMargin + RampWidth, outside);

        // Two noise fields: broad massing, then a ridged term that carves the crests and gullies.
        float massing = Fbm(x * 0.0021f, z * 0.0021f, 4, 11);
        float ridged = 1f - MathF.Abs(Fbm(x * 0.0043f, z * 0.0043f, 3, 29) * 2f - 1f);

        float shape = 0.30f + massing * 0.80f + ridged * 0.45f;
        return ramp * ramp * PeakHeight * shape;
    }

    /// <summary>How far a point lies outside the city rectangle; zero within it.</summary>
    static float DistanceOutside(Bounds2 city, float x, float z)
    {
        float dx = MathF.Max(MathF.Max(city.X - x, x - (city.X + city.Width)), 0f);
        float dz = MathF.Max(MathF.Max(city.Z - z, z - (city.Z + city.Depth)), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Conifers scattered on the slopes, each sitting exactly on the surface.
    /// </summary>
    /// <remarks>
    /// The previous version placed trees at a guessed radius and buried most of them inside the rock.
    /// Sampling the same height function the mesh is built from makes that impossible by
    /// construction. Steep faces and the ground above the treeline are left bare.
    /// </remarks>
    static void PlantTrees(SceneGraph scene, Bounds2 city, float minX, float minZ,
        float maxX, float maxZ)
    {
        const float Step = 26f;
        const float TreeLine = 165f;

        var trunk = new Vector4(0.20f, 0.14f, 0.09f, 1f);
        var canopy = new Vector4(0.11f, 0.26f, 0.13f, 1f);
        int seed = 0;

        for (float z = minZ; z < maxZ; z += Step)
        for (float x = minX; x < maxX; x += Step)
        {
            seed++;
            float jx = x + (Hash(seed * 3) - 0.5f) * Step * 0.9f;
            float jz = z + (Hash(seed * 7 + 1) - 0.5f) * Step * 0.9f;

            float y = Height(jx, jz, city);
            // Well clear of the skirt, so no tree stands on ground hidden inside the bedrock.
            if (y < 8f || y > TreeLine) continue;

            // Slope test: nothing takes root on a cliff.
            float dx = Height(jx + 6f, jz, city) - Height(jx - 6f, jz, city);
            float dz = Height(jx, jz + 6f, city) - Height(jx, jz - 6f, city);
            float slope = MathF.Sqrt(dx * dx + dz * dz) / 12f;
            if (slope > 1.15f) continue;

            // Thin out toward the treeline so the forest fades rather than stopping dead.
            if (Hash(seed * 13 + 5) < y / TreeLine * 0.75f) continue;

            float scale = 4.5f + Hash(seed * 17 + 3) * 4.5f;
            // Same sink as the mesh, or every tree hovers a metre above the slope.
            var at = new Vector3(jx, y - Sink, jz);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at,
                Size = new Vector3(scale * 0.18f, scale * 0.8f, scale * 0.18f),
                Color = trunk,
                PickId = -1,
                Detail = 1f,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at with { Y = at.Y + scale * 0.55f },
                Size = new Vector3(scale * 0.95f, scale * 1.15f, scale * 0.95f),
                Color = canopy,
                PickId = -1,
                Detail = 1f,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at with { Y = at.Y + scale * 1.45f },
                Size = new Vector3(scale * 0.55f, scale * 0.85f, scale * 0.55f),
                Color = new Vector4(canopy.X * 1.25f, canopy.Y * 1.2f, canopy.Z * 1.25f, 1f),
                PickId = -1,
                Detail = 1f,
            });
        }
    }

    static float Fbm(float x, float z, int octaves, int seed)
    {
        float sum = 0f, amplitude = 0.5f, frequency = 1f, total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Noise(x * frequency, z * frequency, seed + i * 131) * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }
        return sum / total;
    }

    static float Noise(float x, float z, int seed)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float xf = x - xi, zf = z - zi;
        float u = xf * xf * (3f - 2f * xf);
        float v = zf * zf * (3f - 2f * zf);

        float a = Corner(xi, zi, seed), b = Corner(xi + 1, zi, seed);
        float c = Corner(xi, zi + 1, seed), d = Corner(xi + 1, zi + 1, seed);
        return Lerp(Lerp(a, b, u), Lerp(c, d, u), v);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    static float Corner(int x, int z, int seed) => Hash(x * 73856093 ^ z * 19349663 ^ seed * 83492791);

    static float Hash(int value)
    {
        unchecked
        {
            uint h = (uint)value * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}

