using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Trees on clean lots and ponds in clean blocks: the reward side of the encoding.
/// </summary>
/// <remarks>
/// Everything else in the city shows what's <em>wrong</em> — fires, broken glass, cranes, rust. A
/// codebase with nothing wrong therefore rendered as a grey nothing, which reads as "no information"
/// rather than "this is good". Greenery fixes that asymmetry: health is a visible, positive state,
/// so a well-kept district looks like somewhere you'd want to work rather than merely somewhere that
/// isn't on fire.
///
/// Measured on a large real-world solution, roughly two-thirds of types come out clean, so this is
/// deliberately the city's default appearance — the burning quarter is the exception.
/// </remarks>
internal static class Greenery
{
    /// <summary>0 = ailing, 1 = spotless. Drives how much greenery a lot earns.</summary>
    public static float Health(TypeNode type)
    {
        int smells = type.Smells.Sum(s => s.Count);
        int warnings = type.NullWarnings + type.AnalyzerWarnings + type.UnusedWarnings
                       + type.CompileErrors * 5 + type.SecurityFindings * 5;

        float penalty = smells * 0.16f
                        + warnings * 0.09f
                        + MathF.Max(0f, (float)type.AvgComplexity - 3f) * 0.14f;

        return Math.Clamp(1f - penalty, 0f, 1f);
    }

    /// <summary>Street trees around a healthy building, kept clear of the building itself.</summary>
    /// <remarks>
    /// Two constraints stop the planting from swallowing what it's meant to decorate. Trees never
    /// grow taller than about half the building, so a one-method type isn't hidden by its own
    /// shrubbery, and they stand off from the facade far enough to leave the nameplate and the
    /// windows readable. Greenery is a reward, not a screen.
    /// </remarks>
    public static void PlantLot(SceneGraph scene, TypeNode type, Bounds2 lot, Vector3 center,
        float side, float roofY)
    {
        float health = Health(type);
        if (health < 0.55f) return;

        int trees = (int)MathF.Round(health * 4f);
        // Never more than half the building's height, so nothing gets hidden by its own trees.
        float ceiling = MathF.Max(2.2f, roofY * 0.5f);

        for (int i = 0; i < trees; i++)
        {
            float scale = MathF.Min(3.4f + CityLayout.StableRandom(type.Id, i * 13 + 5) * 2.6f,
                ceiling);

            // Stand off from the facade, further for a bigger canopy.
            float radius = side * 0.5f + 2.6f + scale * 0.5f
                           + CityLayout.StableRandom(type.Id, i * 11 + 3) * 1.6f;
            float angle = MathF.Tau * i / trees + CityLayout.StableRandom(type.Id, i * 7 + 61) * 1.1f;
            var at = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);

            // Stay on the lot; a tree in the middle of the road helps nobody.
            if (at.X < lot.X || at.X > lot.X + lot.Width) continue;
            if (at.Z < lot.Z || at.Z > lot.Z + lot.Depth) continue;

            AddTree(scene, at, scale, health);
        }
    }

    /// <summary>
    /// Turns a test project's block into actual parkland: a pitch, benches, and a pond.
    /// </summary>
    /// <remarks>
    /// Test districts were only ever green ground — the colour said "not production" but nothing
    /// else did. Recreation ground is the read: a place where activity happens that isn't the
    /// business of the city. It's also the one context where a pond belongs, which is why ponds live
    /// here rather than on production concrete, where open water looked like a flooded car park.
    /// </remarks>
    public static void AddParkland(SceneGraph scene, Bounds2 block, float health, int seed)
    {
        float span = MathF.Min(block.Width, block.Depth);
        if (span < 12f) return;

        // Plant first and everywhere, so the plot reads as parkland whatever else fits in it.
        ScatterTrees(scene, block, seed);

        if (span >= 20f) AddPitch(scene, block, seed);
        if (span >= 34f) AddPond(scene, block, health, seed);
    }

    /// <summary>Trees across the whole plot, at a density that doesn't depend on how big it is.</summary>
    static void ScatterTrees(SceneGraph scene, Bounds2 block, int seed)
    {
        // Roughly one tree per 90 m², so a small park is sparse and a large one is properly wooded
        // rather than a court marooned in an empty field.
        int count = Math.Clamp((int)(block.Width * block.Depth / 90f), 4, 40);

        for (int i = 0; i < count; i++)
        {
            var at = new Vector3(
                block.X + Hash(seed * 61 + i * 7) * block.Width,
                0f,
                block.Z + Hash(seed * 67 + i * 11) * block.Depth);

            // Leave the middle clear for the court.
            var toCentre = new Vector2(at.X - (block.X + block.Width * 0.5f),
                at.Z - (block.Z + block.Depth * 0.5f));
            if (toCentre.Length() < MathF.Min(block.Width, block.Depth) * 0.28f) continue;

            AddTree(scene, at, 3.8f + Hash(seed * 71 + i) * 3.4f, 1f);
        }
    }

    /// <summary>A clay basketball court with a hoop at each end.</summary>
    static void AddPitch(SceneGraph scene, Bounds2 block, int seed)
    {
        float length = MathF.Min(block.Width * 0.55f, 26f);
        float width = MathF.Min(block.Depth * 0.55f, 15f);
        if (length < 12f || width < 8f) return;

        var centre = new Vector3(
            block.X + block.Width * 0.5f,
            CityLayout.PlazaSurfaceY,
            block.Z + block.Depth * (0.30f + Hash(seed * 53) * 0.14f));

        scene.Roads.Add(new RoadQuad
        {
            Center = centre,
            Length = length,
            Width = width,
            Yaw = 0f,
            Color = new Vector4(0.42f, 0.26f, 0.19f, 1f),   // clay
            Flags = (uint)RoadFlags.Court,
        });

        // Post, backboard and ring at each end.
        for (int side = -1; side <= 1; side += 2)
        {
            var post = centre with { Y = 0f } + new Vector3(length * 0.5f * side, 0f, 0f);

            scene.Boxes.Add(Box(post, new Vector3(0.28f, 3.4f, 0.28f),
                new Vector4(0.30f, 0.31f, 0.33f, 1f)));
            scene.Boxes.Add(Box(post with { Y = 3.4f } - new Vector3(0.6f * side, 0f, 0f),
                new Vector3(0.16f, 1.1f, 1.8f), new Vector4(0.90f, 0.90f, 0.88f, 1f)));
            scene.Boxes.Add(Box(post with { Y = 3.15f } - new Vector3(1.1f * side, 0f, 0f),
                new Vector3(0.7f, 0.1f, 0.7f), new Vector4(0.92f, 0.45f, 0.12f, 1f)));
        }

        // Substitutes' benches, sitting just inside the touchline. Previously they were placed
        // beyond it, onto whatever happened to be there — which in a block with internal streets
        // meant the occasional bench stranded in the middle of a road.
        for (int i = 0; i < 3; i++)
        {
            var at = centre with { Y = 0f }
                     + new Vector3((i - 1) * length * 0.28f, 0f, width * 0.5f - 1.1f);
            scene.Boxes.Add(Box(at, new Vector3(2.2f, 0.45f, 0.6f),
                new Vector4(0.42f, 0.30f, 0.18f, 1f)));
            // Backrest on the outside edge, so the bench faces the court.
            scene.Boxes.Add(Box(at with { Y = 0.45f } + new Vector3(0f, 0f, 0.25f),
                new Vector3(2.2f, 0.6f, 0.12f), new Vector4(0.42f, 0.30f, 0.18f, 1f)));
        }
    }

    static BoxInstance Box(Vector3 at, Vector3 size, Vector4 colour) => new()
    {
        BasePosition = at,
        Size = size,
        Color = colour,
        PickId = -1,
        Detail = 1f,
    };

    /// <summary>
    /// A pond with ducks, for a block whose types are collectively healthy.
    /// </summary>
    /// <remarks>
    /// Placed per block rather than per lot because a pond squeezed onto one building's plot reads
    /// as a puddle. At block scale it becomes the neighbourhood's park, which is the point: it
    /// rewards a whole namespace being in good order, not one lucky class.
    /// </remarks>
    public static void AddPond(SceneGraph scene, Bounds2 block, float health, int seed)
    {
        float span = MathF.Min(block.Width, block.Depth);
        if (span < 26f) return;

        float radius = MathF.Min(span * 0.20f, 15f);
        var centre = new Vector3(
            block.X + block.Width * (0.28f + Hash(seed) * 0.44f),
            CityLayout.PondSurfaceY,
            block.Z + block.Depth * (0.28f + Hash(seed * 3 + 1) * 0.44f));

        scene.Roads.Add(new RoadQuad
        {
            Center = centre,
            Length = radius * 2f,
            Width = radius * 1.6f,
            Yaw = Hash(seed * 5) * MathF.PI,
            Color = new Vector4(0.16f, 0.34f, 0.44f, 1f),
            Flags = (uint)RoadFlags.Pond,
        });

        // Reeds and a few boulders around the margin.
        for (int i = 0; i < 10; i++)
        {
            float angle = MathF.Tau * i / 10f;
            var at = centre with { Y = 0f } + new Vector3(
                MathF.Cos(angle) * radius * 1.05f, 0f, MathF.Sin(angle) * radius * 0.85f);

            bool rock = Hash(seed * 17 + i) < 0.35f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at,
                Size = rock
                    ? new Vector3(1.1f, 0.6f, 1.0f)
                    : new Vector3(0.3f, 1.5f + Hash(seed * 19 + i) * 0.9f, 0.3f),
                Color = rock
                    ? new Vector4(0.40f, 0.39f, 0.37f, 1f)
                    : new Vector4(0.30f, 0.44f, 0.22f, 1f),
                PickId = -1,
                Detail = 1f,
            });
        }

        // Trees clustered at one end, the way a real park does.
        for (int i = 0; i < 5; i++)
        {
            float angle = MathF.Tau * Hash(seed * 23 + i);
            var at = centre with { Y = 0f } + new Vector3(
                MathF.Cos(angle) * radius * 1.7f, 0f, MathF.Sin(angle) * radius * 1.5f);
            AddTree(scene, at, 4.5f + Hash(seed * 29 + i) * 3f, health);
        }

        AddDucks(scene, centre, radius, seed);
    }

    /// <summary>Ducks paddling a slow circuit. Pure ornament, and the point of a pond.</summary>
    static void AddDucks(SceneGraph scene, Vector3 centre, float radius, int seed)
    {
        const int Segments = 14;
        float ring = radius * 0.55f;

        var loop = new Vector3[Segments + 1];
        for (int i = 0; i <= Segments; i++)
        {
            float angle = MathF.Tau * (i % Segments) / Segments;
            loop[i] = centre with { Y = CityLayout.PondSurfaceY + 0.12f }
                      + new Vector3(MathF.Cos(angle) * ring, 0f, MathF.Sin(angle) * ring * 0.8f);
        }

        int path = TrafficNetwork.AddPath(scene, loop, loop: true);
        int ducks = 2 + (int)(Hash(seed * 31) * 3);

        for (int i = 0; i < ducks; i++)
        {
            scene.Travellers.Add(new Traveller
            {
                PathIndex = path,
                Phase = (float)i / ducks,
                Speed = 0.55f + Hash(seed * 37 + i) * 0.35f,
                Color = Hash(seed * 41 + i) < 0.3f
                    ? new Vector4(0.88f, 0.86f, 0.80f, 1f)
                    : new Vector4(0.34f, 0.27f, 0.18f, 1f),
                Kind = TravellerKind.Duck,
            });
        }
    }

    /// <summary>Also planted along kerbs; see <see cref="Sidewalks"/>.</summary>
    internal static void AddTree(SceneGraph scene, Vector3 at, float scale, float health,
        CityLayer layer = CityLayer.Always)
    {
        // Healthier ground grows greener leaves; a struggling lot yellows off.
        var leaf = Vector4.Lerp(
            new Vector4(0.34f, 0.32f, 0.14f, 1f),
            new Vector4(0.15f, 0.36f, 0.17f, 1f), health);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = at,
            Size = new Vector3(scale * 0.16f, scale * 0.6f, scale * 0.16f),
            Color = new Vector4(0.24f, 0.17f, 0.11f, 1f),
            PickId = -1,
            Detail = 1f,
            Layer = layer,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = at with { Y = at.Y + scale * 0.5f },
            Size = new Vector3(scale * 0.95f, scale * 0.85f, scale * 0.95f),
            Color = leaf,
            PickId = -1,
            Detail = 1f,
            Layer = layer,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = at with { Y = at.Y + scale * 1.05f },
            Size = new Vector3(scale * 0.62f, scale * 0.6f, scale * 0.62f),
            Color = new Vector4(leaf.X * 1.2f, leaf.Y * 1.18f, leaf.Z * 1.2f, 1f),
            PickId = -1,
            Detail = 1f,
            Layer = layer,
        });
    }

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
