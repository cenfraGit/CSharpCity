using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Turns a building into a civic landmark without changing its shape.
/// </summary>
/// <remarks>
/// <b>The rule this file exists to obey:</b> massing always means size. Height is method count,
/// footprint is fields + properties, floor height is that method's LOC. If a town hall got a fixed
/// civic silhouette, the single most important fact about that class — how big it is — would stop
/// being readable, and the landmark layer would destroy more information than it adds.
///
/// So everything here is <em>around</em> or <em>on top of</em> the real stack: a plaza in front,
/// roof furniture above it, and a plaque stating the numbers so nothing has to be inferred from
/// silhouette at all.
/// </remarks>
internal static class CivicDressing
{
    static readonly Vector4 Stone = new(0.62f, 0.60f, 0.55f, 1f);
    static readonly Vector4 Paving = new(0.42f, 0.41f, 0.39f, 1f);
    static readonly Vector4 Foliage = new(0.18f, 0.40f, 0.20f, 1f);
    static readonly Vector4 Trunk = new(0.26f, 0.19f, 0.13f, 1f);

    /// <summary>Extra lot area a landmark asks for, to make room for its plaza.</summary>
    public static float FootprintBonus(CivicRole role) => role == CivicRole.None ? 1f : 1.9f;

    public static void Apply(SceneGraph scene, TypeNode type, CivicRole role, Vector3 center,
        float side, float roofY, int pickId)
    {
        if (role == CivicRole.None) return;

        AddPlaza(scene, center, side, role);
        AddPortico(scene, center, side, role, pickId);
        AddRoofFurniture(scene, type, role, center, side, roofY, pickId);
        AddPlaque(scene, type, role, center, side);
    }

    /// <summary>A paved square with trees, exactly like the forecourt of a real municipal building.</summary>
    static void AddPlaza(SceneGraph scene, Vector3 center, float side, CivicRole role)
    {
        float depth = side * 0.85f;
        float width = side * 1.5f;
        float front = center.Z + side * 0.5f + depth * 0.5f;

        scene.Roads.Add(new RoadQuad
        {
            Center = new Vector3(center.X, CityLayout.PlazaSurfaceY, front),
            Length = width,
            Width = depth,
            Yaw = 0f,
            Color = Paving,
            Flags = (uint)RoadFlags.None,
        });

        // Trees down both edges. A power station gets none — it's an industrial site.
        if (role == CivicRole.PowerStation) return;

        for (int side_ = -1; side_ <= 1; side_ += 2)
        for (int i = 0; i < 2; i++)
        {
            var at = new Vector3(
                center.X + width * 0.42f * side_,
                0f,
                front - depth * 0.25f + i * depth * 0.5f);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at,
                Size = new Vector3(0.35f, 2.4f, 0.35f),
                Color = Trunk,
                PickId = -1,
                Detail = 1f,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at with { Y = 2.4f },
                Size = new Vector3(2.6f, 2.6f, 2.6f),
                Color = Foliage,
                PickId = -1,
                Detail = 1f,
            });
        }
    }

    /// <summary>Columns and steps at the entrance. A facade overlay — it never changes the stack.</summary>
    static void AddPortico(SceneGraph scene, Vector3 center, float side, CivicRole role, int pickId)
    {
        if (role is CivicRole.PowerStation or CivicRole.Factory or CivicRole.Depot) return;

        float front = center.Z + side * 0.5f;
        int columns = 4;

        for (int i = 0; i < columns; i++)
        {
            float t = (i + 0.5f) / columns - 0.5f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = new Vector3(center.X + t * side * 0.8f, 0f, front + 1.1f),
                Size = new Vector3(0.5f, 5.5f, 0.5f),
                Color = Stone,
                PickId = pickId,
                Detail = 1f,
            });
        }

        // Steps up to the entrance.
        for (int i = 0; i < 3; i++)
        {
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = new Vector3(center.X, 0f, front + 1.9f + i * 0.5f),
                Size = new Vector3(side * 0.95f, 0.55f - i * 0.16f, 0.9f),
                Color = Stone,
                PickId = -1,
                Detail = 1f,
            });
        }

        // Pediment resting on the columns.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = new Vector3(center.X, 5.5f, front + 1.1f),
            Size = new Vector3(side * 0.9f, 1.1f, 1.4f),
            Color = Stone,
            PickId = pickId,
            Detail = 1f,
        });
    }

    /// <summary>
    /// Domes, spires, chimneys — all sitting on top of the real roof, adding to the stack rather
    /// than replacing any part of it.
    /// </summary>
    static void AddRoofFurniture(SceneGraph scene, TypeNode type, CivicRole role, Vector3 center,
        float side, float roofY, int pickId)
    {
        switch (role)
        {
            case CivicRole.TownHall:
                // Stepped dome plus a flagpole.
                for (int i = 0; i < 3; i++)
                {
                    float t = i / 3f;
                    scene.Boxes.Add(Box(center, roofY + i * 1.5f, side * (0.55f - t * 0.28f), 1.5f,
                        Stone, pickId));
                }
                scene.Boxes.Add(Box(center, roofY + 4.5f, 0.18f, 4.5f, Stone, -1));
                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = new Vector3(center.X + 0.9f, roofY + 7.6f, center.Z),
                    Size = new Vector3(1.8f, 1.1f, 0.1f),
                    Color = new Vector4(0.85f, 0.25f, 0.22f, 1f),
                    PickId = -1,
                    Detail = 1f,
                });
                break;

            case CivicRole.Cathedral:
                // Spire scales with implementors: the congregation made it tall.
                float spire = Math.Clamp(6f + type.ImplementorCount * 1.6f, 6f, 34f);
                scene.Boxes.Add(Box(center, roofY, side * 0.34f, spire * 0.35f, Stone, pickId));
                scene.Boxes.Add(Box(center, roofY + spire * 0.35f, side * 0.18f, spire * 0.65f,
                    Stone, pickId));
                break;

            case CivicRole.School:
                scene.Boxes.Add(Box(center, roofY, side * 0.22f, 3.2f, Stone, pickId));
                scene.Boxes.Add(Box(center, roofY + 3.2f, side * 0.3f, 0.8f, Stone, -1));
                break;

            case CivicRole.Hospital:
                // Rooftop helipad and a red cross.
                scene.Boxes.Add(Box(center, roofY, side * 0.7f, 0.35f, new Vector4(0.9f, 0.9f, 0.9f, 1f),
                    pickId));
                scene.Boxes.Add(Box(center, roofY + 0.35f, side * 0.42f, 0.12f,
                    new Vector4(0.85f, 0.16f, 0.16f, 1f), -1));
                break;

            case CivicRole.PowerStation:
                for (int i = -1; i <= 1; i += 2)
                {
                    scene.Boxes.Add(new BoxInstance
                    {
                        BasePosition = new Vector3(center.X + i * side * 0.28f, roofY, center.Z),
                        Size = new Vector3(side * 0.3f, 9f, side * 0.3f),
                        Color = new Vector4(0.55f, 0.54f, 0.52f, 1f),
                        PickId = pickId,
                        Detail = 1f,
                    });
                }
                break;

            case CivicRole.Factory:
                // Sawtooth roof and a chimney.
                for (int i = -1; i <= 1; i++)
                {
                    scene.Boxes.Add(new BoxInstance
                    {
                        BasePosition = new Vector3(center.X, roofY, center.Z + i * side * 0.3f),
                        Size = new Vector3(side * 0.9f, 1.6f, side * 0.18f),
                        Color = new Vector4(0.40f, 0.38f, 0.36f, 1f),
                        PickId = -1,
                        Detail = 1f,
                    });
                }
                scene.Boxes.Add(Box(center, roofY, side * 0.16f, 11f,
                    new Vector4(0.48f, 0.32f, 0.26f, 1f), pickId));
                break;

            case CivicRole.Library:
                scene.Boxes.Add(Box(center, roofY, side * 0.92f, 0.7f, Stone, pickId));
                break;

            case CivicRole.Courthouse:
                scene.Boxes.Add(Box(center, roofY, side * 0.6f, 1.0f, Stone, pickId));
                scene.Boxes.Add(Box(center, roofY + 1.0f, side * 0.3f, 2.2f, Stone, pickId));
                break;

            case CivicRole.Depot:
                // Loading canopy over the frontage.
                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = new Vector3(center.X, roofY, center.Z + side * 0.35f),
                    Size = new Vector3(side * 1.1f, 0.5f, side * 0.5f),
                    Color = new Vector4(0.45f, 0.43f, 0.40f, 1f),
                    PickId = pickId,
                    Detail = 1f,
                });
                break;
        }
    }

    static BoxInstance Box(Vector3 center, float y, float side, float height, Vector4 color, int pickId) =>
        new()
        {
            BasePosition = center with { Y = y },
            Size = new Vector3(side, height, side),
            Color = color,
            PickId = pickId,
            Detail = 1f,
        };

    /// <summary>
    /// The board at the plaza edge. This is what answers "how big is this class?" directly, so the
    /// answer never has to be guessed from a silhouette the civic dressing might have obscured.
    /// </summary>
    static void AddPlaque(SceneGraph scene, TypeNode type, CivicRole role, Vector3 center, float side)
    {
        float front = center.Z + side * 0.5f + side * 0.85f;
        var post = new Vector3(center.X, 0f, front);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = post,
            Size = new Vector3(0.25f, 2.3f, 0.25f),
            Color = new Vector4(0.30f, 0.29f, 0.27f, 1f),
            PickId = -1,
            Detail = 1f,
        });

        scene.Labels.Add(new WorldLabel
        {
            Position = post with { Y = 2.7f },
            Text = CivicRoles.Title(role),
            Subtitle = $"{type.Name} · {CivicRoles.Citation(role, type)} · " +
                       $"{type.Methods.Count} methods · {type.Loc} LOC",
            Size = 1.0f,
            Color = new Vector4(1.00f, 0.94f, 0.72f, 1f),
            FadeDistance = 150f,
            // Below district banners, above ordinary street signs: a landmark is a waypoint.
            Priority = 7000,
        });
    }
}
