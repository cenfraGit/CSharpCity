using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// The quiet majority of analyzer findings, rendered as ordinary urban wear.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="EmergencyServices"/>. Those are incidents: rare, animated, and
/// meant to pull you across the city. These are conditions: static, unremarkable individually, and
/// only meaningful in aggregate — which is exactly right, because they cover roughly a thousand of
/// the 1,180 findings. A street where every building has bins out and posters peeling reads as
/// neglected without any single building shouting about it.
/// </remarks>
internal static class Conditions
{
    public static void Apply(SceneGraph scene, TypeNode type, Bounds2 lot, Vector3 center,
        float side, float roofY, int pickId)
    {
        AddRefuse(scene, type, lot, side, pickId);
        AddPosters(scene, type, center, side, pickId);
        AddPatches(scene, type, center, side, roofY, pickId);
        AddLettingBoard(scene, type, center, side, pickId);
        AddIdlingPlant(scene, type, center, side, pickId);
        AddZipline(scene, type, center, side, roofY, pickId);
    }

    /// <summary>Wheelie bins and refuse sacks: unused locals, fields and dead stores.</summary>
    static void AddRefuse(SceneGraph scene, TypeNode type, Bounds2 lot, float side, int pickId)
    {
        int items = Math.Min(type.ClutterFindings, 7);

        for (int i = 0; i < items; i++)
        {
            var at = CityLayout.ScatterOnLot(type.Id, lot, side, i * 9 + 131);
            bool bin = i % 3 != 0;

            if (bin)
            {
                scene.Boxes.Add(Box(at, new Vector3(0.85f, 1.15f, 0.75f),
                    new Vector4(0.20f, 0.26f, 0.21f, 1f)));
                // Lid propped open, because it's overflowing.
                scene.Boxes.Add(Box(at with { Y = 1.15f }, new Vector3(0.9f, 0.14f, 0.5f),
                    new Vector4(0.14f, 0.19f, 0.15f, 1f)));
                scene.Boxes.Add(Box(at with { Y = 1.2f }, new Vector3(0.7f, 0.4f, 0.6f),
                    new Vector4(0.32f, 0.30f, 0.26f, 1f)));
            }
            else
            {
                scene.Boxes.Add(Box(at, new Vector3(0.75f, 0.65f, 0.7f),
                    new Vector4(0.13f, 0.13f, 0.14f, 1f)));
            }
        }
    }

    /// <summary>Layered fly-posters: code commented out and left on the wall.</summary>
    static void AddPosters(SceneGraph scene, TypeNode type, Vector3 center, float side, int pickId)
    {
        int sheets = Math.Min(type.StaleFindings, 8);

        for (int i = 0; i < sheets; i++)
        {
            float along = (CityLayout.StableRandom(type.Id, i * 7 + 211) - 0.5f) * side * 0.8f;
            float height = 1.2f + CityLayout.StableRandom(type.Id, i * 11 + 17) * 3.2f;
            float fade = CityLayout.StableRandom(type.Id, i * 13 + 5);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(along, height, side * 0.5f + 0.06f),
                Size = new Vector3(0.9f + fade * 0.5f, 1.3f + fade * 0.6f, 0.05f),
                // Older bills have bleached out; newer ones are still bright.
                Color = new Vector4(0.62f - fade * 0.24f, 0.60f - fade * 0.26f, 0.54f - fade * 0.22f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <summary>Mismatched render patches: redundant code that adds nothing but surface.</summary>
    static void AddPatches(SceneGraph scene, TypeNode type, Vector3 center, float side, float roofY,
        int pickId)
    {
        int patches = Math.Min(type.RedundancyFindings / 2, 9);

        for (int i = 0; i < patches; i++)
        {
            float along = (CityLayout.StableRandom(type.Id, i * 17 + 301) - 0.5f) * side * 0.85f;
            float height = 1f + CityLayout.StableRandom(type.Id, i * 19 + 7) * MathF.Max(roofY - 2f, 1f);
            float shade = 0.34f + CityLayout.StableRandom(type.Id, i * 23 + 3) * 0.26f;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(-side * 0.5f - 0.05f, height, along),
                Size = new Vector3(0.06f, 1.1f + CityLayout.StableRandom(type.Id, i * 29) * 1.4f,
                    1.2f + CityLayout.StableRandom(type.Id, i * 31) * 1.6f),
                Color = new Vector4(shade, shade * 0.96f, shade * 0.88f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <summary>A letting board on a building with empty classes, methods or blocks.</summary>
    static void AddLettingBoard(SceneGraph scene, TypeNode type, Vector3 center, float side,
        int pickId)
    {
        if (type.EmptyFindings == 0) return;

        var post = center + new Vector3(side * 0.5f + 0.9f, 0f, -side * 0.3f);
        scene.Boxes.Add(Box(post, new Vector3(0.16f, 4.2f, 0.16f),
            new Vector4(0.34f, 0.31f, 0.26f, 1f)));
        scene.Boxes.Add(Box(post with { Y = 3.1f }, new Vector3(0.14f, 1.5f, 2.4f),
            new Vector4(0.86f, 0.84f, 0.78f, 1f)));
        // A red banner across it, the way a letting agent's board carries one.
        scene.Boxes.Add(Box(post with { Y = 4.1f }, new Vector3(0.16f, 0.45f, 2.4f),
            new Vector4(0.68f, 0.16f, 0.14f, 1f)));
    }

    /// <summary>
    /// A generator running on the lot with no way to shut it down: work that takes no
    /// CancellationToken. It idles, it smokes, and nobody can stop it.
    /// </summary>
    static void AddIdlingPlant(SceneGraph scene, TypeNode type, Vector3 center, float side,
        int pickId)
    {
        if (type.UncancellableFindings == 0) return;

        var at = center + new Vector3(-side * 0.5f - 1.8f, 0f, side * 0.3f);
        scene.Boxes.Add(Box(at, new Vector3(2.2f, 1.5f, 1.4f),
            new Vector4(0.46f, 0.44f, 0.20f, 1f)));
        scene.Boxes.Add(Box(at with { Y = 1.5f }, new Vector3(0.35f, 1.1f, 0.35f),
            new Vector4(0.24f, 0.23f, 0.22f, 1f)));

        // Exhaust, drifting on the same animation the fires' smoke uses.
        int puffs = Math.Min(2 + type.UncancellableFindings / 3, 5);
        for (int i = 1; i <= puffs; i++)
        {
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at + new Vector3(0f, 2.6f + i * 1.5f, 0f),
                Size = new Vector3(0.8f + i * 0.35f, 1.4f, 0.8f + i * 0.35f),
                Color = new Vector4(0.30f, 0.29f, 0.27f, MathF.Max(0.06f, 0.26f - i * 0.045f)),
                PickId = -1,
                Flags = (uint)BoxFlags.Smoke,
                Detail = 1f,
            });
        }
    }

    /// <summary>
    /// A zipline strung off the roof: control leaving the building by a route that isn't the stairs.
    /// </summary>
    static void AddZipline(SceneGraph scene, TypeNode type, Vector3 center, float side, float roofY,
        int pickId)
    {
        if (type.GotoFindings == 0 || roofY < 6f) return;

        var top = center + new Vector3(side * 0.5f, roofY, side * 0.5f);
        var ground = center + new Vector3(side * 0.5f + 16f, 1.4f, side * 0.5f + 16f);

        // Anchor posts at both ends.
        scene.Boxes.Add(Box(top with { Y = roofY }, new Vector3(0.3f, 1.4f, 0.3f),
            new Vector4(0.38f, 0.36f, 0.32f, 1f)));
        scene.Boxes.Add(Box(ground with { Y = 0f }, new Vector3(0.35f, 1.4f, 0.35f),
            new Vector4(0.38f, 0.36f, 0.32f, 1f)));

        // The cable, stepped along its run and sagging in the middle.
        const int Segments = 22;
        for (int i = 0; i <= Segments; i++)
        {
            float t = i / (float)Segments;
            var at = Vector3.Lerp(top with { Y = roofY + 1.2f }, ground, t);
            at.Y -= MathF.Sin(t * MathF.PI) * 1.6f;

            scene.Boxes.Add(Box(at, new Vector3(0.85f, 0.09f, 0.85f),
                new Vector4(0.16f, 0.16f, 0.17f, 1f)));
        }

        // The trolley, parked partway down.
        var trolley = Vector3.Lerp(top with { Y = roofY + 1.2f }, ground, 0.38f);
        trolley.Y -= MathF.Sin(0.38f * MathF.PI) * 1.6f;
        scene.Boxes.Add(Box(trolley with { Y = trolley.Y - 0.5f }, new Vector3(0.5f, 0.6f, 0.5f),
            new Vector4(0.82f, 0.58f, 0.10f, 1f)));
    }

    static BoxInstance Box(Vector3 at, Vector3 size, Vector4 colour) => new()
    {
        BasePosition = at,
        Size = size,
        Color = colour,
        PickId = -1,
        Detail = 1f,
    };
}
