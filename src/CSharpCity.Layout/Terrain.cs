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

    // --- coastal: the shape the ground takes when the projects are separate towns ---

    /// <summary>Apron kept level beyond a town before the countryside starts rolling.</summary>
    const float GreenBelt = 40f;

    /// <summary>
    /// How high the country between towns gets.
    /// </summary>
    /// <remarks>
    /// Low, and deliberately so. This is downland between neighbouring towns, not a range dividing
    /// them: anything tall enough to hide the next town turns a map into a maze, and the point of
    /// laying the projects out separately is being able to see how they sit relative to each other.
    /// </remarks>
    const float HillHeight = 26f;

    /// <summary>Level ground kept just inside the coast, so the shore is a shore and not a cliff.</summary>
    const float ShoreMargin = 90f;

    /// <summary>Distance over which the shore drops from land to sea level.</summary>
    const float ShoreWidth = 70f;

    /// <summary>Where the water sits, in the same space <see cref="Height"/> works in.</summary>
    internal const float SeaLevel = -3f;

    const float SeaFloor = -30f;

    /// <summary>How the ground behaves outside the towns.</summary>
    public enum Ground
    {
        /// <summary>Rises into a mountain range enclosing one city.</summary>
        Continental,
        /// <summary>
        /// Open country between the towns, and sea around the lot.
        /// </summary>
        /// <remarks>
        /// Two separate ideas, and it is worth saying why each one is where it is.
        ///
        /// <b>Between the towns: land.</b> They are parts of one solution and belong to one place,
        /// so the ground between them is country you could walk across — low hills, plains and
        /// woods. An earlier version made every town its own island and it said the wrong thing
        /// entirely: separate projects are not separate worlds.
        ///
        /// <b>Around the whole thing: water.</b> The map has to end somewhere, and every boundary
        /// that is not explained looks arbitrary — which the ring of mountains always did, being a
        /// wall around a place for no reason anyone could see. A coastline explains itself. The land
        /// stops because the sea starts, and nothing about it invites the question.
        /// </remarks>
        Coastal,
    }

    /// <param name="world">Everything the mesh must cover: one city, or the union of several.</param>
    /// <param name="cities">
    /// The rectangles that stay flat. One for a single city; one per city when the projects are laid
    /// out as separate towns, in which case the ground rises in the countryside between them.
    /// </param>
    public static void Build(SceneGraph scene, Bounds2 world, IReadOnlyList<Bounds2> cities,
        Ground ground = Ground.Continental)
    {
        float minX = world.X - Extent, minZ = world.Z - Extent;
        float maxX = world.X + world.Width + Extent, maxZ = world.Z + world.Depth + Extent;

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
            float wy = Height(wx, wz, cities, world, ground);
            raw[z * columns + x] = wy;

            // Central differences against the same function give exact normals for free.
            float dx = Height(wx + CellSize, wz, cities, world, ground) - Height(wx - CellSize, wz, cities, world, ground);
            float dz = Height(wx, wz + CellSize, cities, world, ground) - Height(wx, wz - CellSize, cities, world, ground);
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

        float lowest = 0f;
        foreach (float height in raw) lowest = MathF.Min(lowest, height);
        scene.LowestGround = lowest - Sink;

        PlantTrees(scene, cities, world, minX, minZ, maxX, maxZ, ground);
    }

    /// <summary>
    /// Ground height at a point. Zero across the whole city, climbing outside it.
    /// </summary>
    static float Height(float x, float z, IReadOnlyList<Bounds2> cities, Bounds2 world,
        Ground ground)
    {
        float outside = DistanceOutside(cities, x, z);

        if (ground == Ground.Coastal) return Country(x, z, outside, world);

        if (outside <= FlatMargin) return 0f;

        float ramp = Smoothstep(FlatMargin, FlatMargin + RampWidth, outside);

        // Two noise fields: broad massing, then a ridged term that carves the crests and gullies.
        float massing = Fbm(x * 0.0021f, z * 0.0021f, 4, 11);
        float ridged = 1f - MathF.Abs(Fbm(x * 0.0043f, z * 0.0043f, 3, 29) * 2f - 1f);

        float shape = 0.30f + massing * 0.80f + ridged * 0.45f;
        return ramp * ramp * PeakHeight * shape;
    }

    /// <summary>
    /// The country between the towns, and the coast around the lot.
    /// </summary>
    /// <remarks>
    /// Two independent shapes multiplied together.
    ///
    /// <b>Inland</b>, height comes from how far you are from the nearest town: level over the town
    /// and its apron, then rolling downland rising to <see cref="HillHeight"/>. Low enough to see
    /// over, because the reason to lay projects out separately is to see how they sit relative to
    /// one another, and a range between two towns hides exactly that.
    ///
    /// <b>At the edge</b>, everything is faded down into the sea by distance from the world
    /// rectangle instead. Multiplying rather than choosing is what keeps the two from meeting at a
    /// seam: a hill that happens to sit near the coast is drowned smoothly rather than sheared off
    /// where the rules change.
    /// </remarks>
    static float Country(float x, float z, float fromTown, Bounds2 world)
    {
        // Inland: flat over the town and its apron, rolling beyond it.
        float rise = Smoothstep(FlatMargin, FlatMargin + GreenBelt, fromTown);
        float relief = Fbm(x * 0.0034f, z * 0.0034f, 4, 23);
        float detail = Fbm(x * 0.0115f, z * 0.0115f, 2, 61);
        float land = rise * HillHeight * (relief * 0.78f + detail * 0.22f);

        // The coast, measured from the world rather than from any town. A wobble on the margin so
        // the shoreline is a shoreline and not a rounded rectangle.
        float fromEdge = DistanceOutside(world, x, z);
        float wobble = Fbm(x * 0.0026f, z * 0.0026f, 3, 97) * 2f - 1f;
        float margin = MathF.Max(20f, ShoreMargin * (1f + wobble * 0.6f));

        if (fromEdge <= margin) return land;

        // Down through the waterline, then out across the shelf to the floor.
        const float ShoreDepth = SeaLevel - 6f;

        float down = Smoothstep(margin, margin + ShoreWidth, fromEdge);
        float shelf = Smoothstep(margin + ShoreWidth, margin + ShoreWidth + 320f, fromEdge);

        return land * (1f - down) + down * ShoreDepth + shelf * (SeaFloor - ShoreDepth);
    }

    /// <summary>How far a point lies outside one rectangle; zero within it.</summary>
    static float DistanceOutside(Bounds2 rect, float x, float z)
    {
        float dx = MathF.Max(MathF.Max(rect.X - x, x - (rect.X + rect.Width)), 0f);
        float dz = MathF.Max(MathF.Max(rect.Z - z, z - (rect.Z + rect.Depth)), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// The sea itself: one quad over the whole world, at the waterline.
    /// </summary>
    /// <remarks>
    /// A single plane rather than water fitted to each island, because the coast is already carved
    /// into the ground. Where the land is above the line it stands out of the water; where it is
    /// below, the water covers it. That is how it works outdoors, and it means the shoreline
    /// follows the terrain exactly with nothing having to agree about where it is.
    /// </remarks>
    public static void Flood(SceneGraph scene, Bounds2 world)
    {
        float span = MathF.Max(world.Width, world.Depth) + Extent * 2f;

        scene.Roads.Add(new RoadQuad
        {
            Center = new Vector3(world.CenterX, SeaLevel - Sink, world.CenterZ),
            Length = span,
            Width = span,
            // Translucent so the seabed reads through it: shallows come out pale over the shore and
            // the deep goes dark, with no depth information needed in the shader.
            Color = new Vector4(0.10f, 0.26f, 0.36f, 0.80f),
            Flags = (uint)RoadFlags.Sea,
        });
    }

    /// <summary>
    /// How far a point lies outside the nearest city; zero within any of them.
    /// </summary>
    /// <remarks>
    /// The minimum over every city, which is the whole of what separate towns cost the heightfield:
    /// each one keeps its own flat plate and its own skirt, and the ground climbs only where a point
    /// is well clear of all of them. Everything downstream — normals, tree placement, the treeline —
    /// samples this same function and so follows for free.
    /// </remarks>
    static float DistanceOutside(IReadOnlyList<Bounds2> cities, float x, float z)
    {
        float nearest = float.MaxValue;

        foreach (var city in cities)
        {
            float dx = MathF.Max(MathF.Max(city.X - x, x - (city.X + city.Width)), 0f);
            float dz = MathF.Max(MathF.Max(city.Z - z, z - (city.Z + city.Depth)), 0f);
            float distance = MathF.Sqrt(dx * dx + dz * dz);

            if (distance <= 0f) return 0f;
            nearest = MathF.Min(nearest, distance);
        }

        return nearest == float.MaxValue ? 0f : nearest;
    }

    /// <summary>
    /// Conifers scattered on the slopes, each sitting exactly on the surface.
    /// </summary>
    /// <remarks>
    /// The previous version placed trees at a guessed radius and buried most of them inside the rock.
    /// Sampling the same height function the mesh is built from makes that impossible by
    /// construction. Steep faces and the ground above the treeline are left bare.
    /// </remarks>
    static void PlantTrees(SceneGraph scene, IReadOnlyList<Bounds2> cities, Bounds2 world,
        float minX, float minZ, float maxX, float maxZ, Ground ground)
    {
        // Continental ground is flat for a long way out, and a tree standing on that skirt is a tree
        // standing on hidden bedrock, so nothing is planted below the foothills. An island has no
        // such skirt: its land is the strip between the town and the water, which is precisely
        // where its woods belong.
        float floorY = ground == Ground.Coastal ? 0.6f : 8f;

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

            float y = Height(jx, jz, cities, world, ground);
            // Well clear of the skirt, so no tree stands on ground hidden inside the bedrock.
            if (y < floorY || y > TreeLine) continue;

            // Slope test: nothing takes root on a cliff.
            float dx = Height(jx + 6f, jz, cities, world, ground) - Height(jx - 6f, jz, cities, world, ground);
            float dz = Height(jx, jz + 6f, cities, world, ground) - Height(jx, jz - 6f, cities, world, ground);
            float slope = MathF.Sqrt(dx * dx + dz * dz) / 12f;
            if (slope > 1.15f) continue;

            // Thin out toward the treeline so the forest fades rather than stopping dead.
            if (ground == Ground.Continental && Hash(seed * 13 + 5) < y / TreeLine * 0.75f) continue;

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

