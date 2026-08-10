using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Building fixtures for language features that aren't size, kind or decay — the details you read
/// once you're standing in front of a building rather than looking at the skyline.
/// </summary>
/// <remarks>
/// Each one obeys the same rule as everything else: one metric, one visual, in an intuitive
/// direction. They attach to the outside of the finished stack and never alter its massing.
/// </remarks>
internal static class Fixtures
{
    static readonly Vector4 Metal = new(0.44f, 0.45f, 0.47f, 1f);
    static readonly Vector4 DarkMetal = new(0.28f, 0.29f, 0.31f, 1f);

    public static void Apply(SceneGraph scene, TypeNode type, Vector3 center, float side,
        float roofY, int pickId)
    {
        if (type.IsDisposable) AddFireEscape(scene, center, side, roofY, pickId);
        if (type.IsSealed) AddRoofCap(scene, center, side, roofY, pickId);
        if (type.EventCount > 0) AddLoudspeakers(scene, type, center, side, roofY, pickId);
        if (type.MaxNesting >= 4) AddPipes(scene, type, center, side, roofY, pickId);
    }

    /// <summary>
    /// A lift shaft strapped to the outside of one storey. Async is the storey you wait on, and an
    /// external lift is the most legible "this takes time" a building has.
    /// </summary>
    public static void AddLiftShaft(SceneGraph scene, Vector3 center, float side, float y,
        float height, int pickId)
    {
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center + new Vector3(-side * 0.5f - 0.35f, y, 0f),
            Size = new Vector3(0.9f, height, side * 0.34f),
            Color = new Vector4(0.36f, 0.40f, 0.46f, 1f),
            PickId = pickId,
            Detail = 1f,
        });
    }

    /// <summary>Zigzag fire escape: a disposable type has a defined way out.</summary>
    static void AddFireEscape(SceneGraph scene, Vector3 center, float side, float roofY, int pickId)
    {
        float wall = center.Z - side * 0.5f - 0.3f;
        int landings = Math.Clamp((int)(roofY / 3.4f), 1, 9);

        for (int i = 1; i <= landings; i++)
        {
            float y = i * 3.4f;
            if (y > roofY - 0.5f) break;

            // Landing, alternating side to side so the flights zigzag between them.
            float shift = (i % 2 == 0 ? 1f : -1f) * side * 0.22f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = new Vector3(center.X + shift, y, wall),
                Size = new Vector3(side * 0.42f, 0.12f, 0.85f),
                Color = DarkMetal,
                PickId = pickId,
                Detail = 1f,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = new Vector3(center.X + shift, y, wall - 0.4f),
                Size = new Vector3(side * 0.42f, 0.95f, 0.08f),
                Color = DarkMetal,
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <summary>A flat parapet cap. Sealed: finished, nothing more will be built on top of it.</summary>
    static void AddRoofCap(SceneGraph scene, Vector3 center, float side, float roofY, int pickId)
    {
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center with { Y = roofY },
            Size = new Vector3(side * 1.08f, 0.45f, side * 1.08f),
            Color = new Vector4(0.34f, 0.35f, 0.37f, 1f),
            PickId = pickId,
            Detail = 1f,
        });
    }

    /// <summary>Loudspeakers on the roof — an event is this type broadcasting to whoever subscribed.</summary>
    static void AddLoudspeakers(SceneGraph scene, TypeNode type, Vector3 center, float side,
        float roofY, int pickId)
    {
        int horns = Math.Clamp(type.EventCount, 1, 5);
        float mast = 1.6f + horns * 0.35f;

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center with { Y = roofY },
            Size = new Vector3(0.22f, mast, 0.22f),
            Color = Metal,
            PickId = pickId,
            Detail = 1f,
        });

        for (int i = 0; i < horns; i++)
        {
            float angle = MathF.Tau * i / horns;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(
                    MathF.Cos(angle) * 0.75f, roofY + mast - 0.5f, MathF.Sin(angle) * 0.75f),
                Size = new Vector3(0.7f, 0.55f, 0.7f),
                Color = new Vector4(0.72f, 0.70f, 0.62f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <summary>
    /// Exterior pipework, one run per level of nesting past the third. Spaghetti inside the methods
    /// becomes spaghetti you can see from the street without opening the file.
    /// </summary>
    static void AddPipes(SceneGraph scene, TypeNode type, Vector3 center, float side, float roofY,
        int pickId)
    {
        int runs = Math.Clamp(type.MaxNesting - 3, 1, 6);
        float wall = center.X + side * 0.5f + 0.22f;

        for (int i = 0; i < runs; i++)
        {
            float offset = (CityLayout.StableRandom(type.Id, i * 17 + 71) - 0.5f) * side * 0.8f;
            float top = MathF.Max(roofY * (0.55f + CityLayout.StableRandom(type.Id, i * 5) * 0.45f), 2f);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = new Vector3(wall, 0f, center.Z + offset),
                Size = new Vector3(0.28f, top, 0.28f),
                Color = new Vector4(0.40f, 0.36f, 0.30f, 1f),
                PickId = pickId,
                Detail = 1f,
            });

            // An elbow running across the facade, so the runs tangle instead of sitting parallel.
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = new Vector3(wall, top - 0.28f, center.Z + offset * 0.3f),
                Size = new Vector3(0.24f, 0.24f, MathF.Abs(offset) * 0.8f + 0.4f),
                Color = new Vector4(0.40f, 0.36f, 0.30f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }
    }
}
