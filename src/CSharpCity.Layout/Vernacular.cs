using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Ordinary building types, as distinct from the civic landmarks.
/// </summary>
/// <remarks>
/// <see cref="CivicRoles"/> awards a role to one superlative type per project — the most
/// depended-upon, the most thrown-from. These are the opposite: facts true of any number of
/// buildings at once, dressed so that walking down a street tells you what sort of place you are in
/// without reading a single nameplate.
///
/// All of it is dressing. Not one of these changes a building's massing, which always and only means
/// its size.
/// </remarks>
internal static class Vernacular
{
    /// <summary>Years untouched before a building is preserved rather than merely old.</summary>
    /// <remarks>
    /// Two years, not one. A file nobody edited last year is ordinary in a mature codebase; one
    /// nobody has edited in two is genuinely finished, abandoned, or frightening — and a museum
    /// says all three at once, which is honest, because the city cannot tell them apart.
    /// </remarks>
    const int MuseumDays = 730;

    static readonly Vector4 Stone = new(0.72f, 0.70f, 0.64f, 1f);
    static readonly Vector4 Railing = new(0.34f, 0.36f, 0.38f, 1f);

    public static void Apply(SceneGraph scene, TypeNode type, Bounds2 lot, Vector3 center,
        float side, float roofY, int pickId)
    {
        if (type.DaysSinceChange >= MuseumDays) Preserve(scene, center, side, roofY, pickId);
        if (!type.IsPublic) Enclose(scene, lot, center, side, pickId);
    }

    /// <summary>
    /// A colonnade and a roped-off forecourt: nobody has touched this in years.
    /// </summary>
    /// <remarks>
    /// The exact counterpart of the traffic cones, and the reason both are worth having. Cones say
    /// "this changed today"; a museum says "this has not changed since before anyone here started".
    /// Between them they turn the git history into something you can read at a glance from the end
    /// of a street, instead of two numbers on an inspection card.
    /// </remarks>
    static void Preserve(SceneGraph scene, Vector3 center, float side, float roofY, int pickId)
    {
        float columnHeight = MathF.Min(roofY * 0.55f, 7.5f);
        if (columnHeight < 2f) return;

        // A colonnade across the frontage. Four columns regardless of width: it reads as a portico
        // rather than as a fence, and a wide building would otherwise grow a picket line.
        for (int i = 0; i < 4; i++)
        {
            float across = (i - 1.5f) / 1.5f * side * 0.38f;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(across, 0f, -(side * 0.5f + 0.8f)),
                Size = new Vector3(0.5f, columnHeight, 0.5f),
                Color = Stone,
                PickId = pickId,
                Detail = 1f,
                Flags = (uint)BoxFlags.Round,
            });
        }

        // The entablature they carry.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center with { Y = columnHeight } + new Vector3(0f, 0f, -(side * 0.5f + 0.8f)),
            Size = new Vector3(side * 0.95f, 0.55f, 1.4f),
            Color = Stone,
            PickId = pickId,
            Detail = 1f,
        });
    }

    /// <summary>
    /// A railing round the plot: visible from the street, but not yours to walk into.
    /// </summary>
    /// <remarks>
    /// The city has always drawn doors for public constructors, so a type you cannot construct has
    /// simply had a blank wall — indistinguishable from one whose doors you merely could not see
    /// from where you were standing. A railing states the case from any angle, and states it about
    /// the type rather than about one facade.
    ///
    /// A railing rather than a wall, deliberately. Internal is not secret.
    /// </remarks>
    static void Enclose(SceneGraph scene, Bounds2 lot, Vector3 center, float side, int pickId)
    {
        // Just outside the building, just inside the plot. On a lot barely bigger than what stands
        // on it there is nowhere to put a fence, and half a fence is worse than none.
        float reach = side * 0.5f + 1.4f;
        if (reach * 2f > MathF.Min(lot.Width, lot.Depth) - 0.5f) return;

        const float Spacing = 2.2f;
        int perSide = Math.Max(2, (int)(reach * 2f / Spacing));

        for (int edge = 0; edge < 4; edge++)
        {
            bool alongX = edge % 2 == 0;
            float offset = edge < 2 ? -reach : reach;

            for (int i = 0; i <= perSide; i++)
            {
                float along = (i / (float)perSide - 0.5f) * reach * 2f;

                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = center + new Vector3(alongX ? along : offset, 0f,
                        alongX ? offset : along),
                    Size = new Vector3(0.09f, 1.15f, 0.09f),
                    Color = Railing,
                    PickId = pickId,
                    Detail = 1f,
                });
            }

            // The top rail, which is what makes a row of posts read as a boundary.
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(alongX ? 0f : offset, 1.05f,
                    alongX ? offset : 0f),
                Size = new Vector3(alongX ? reach * 2f : 0.06f, 0.08f, alongX ? 0.06f : reach * 2f),
                Color = Railing,
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <summary>
    /// A warehouse: a type that holds state and does nothing with it.
    /// </summary>
    /// <remarks>
    /// A DTO or a record used to render as an anonymous squat storey, which said only "small". A
    /// warehouse says what it actually is from across the district: no windows because there is
    /// nothing going on inside, a roller shutter because things are put in and taken out, and a
    /// shallow ridge so it does not read as an unfinished block.
    ///
    /// The height is the same formula as before, deliberately. This changes what the building looks
    /// like, never how big it is.
    /// </remarks>
    public static void Warehouse(SceneGraph scene, TypeNode type, Vector3 center, float side,
        float y, float height, int pickId)
    {
        var wall = new Vector4(0.50f, 0.49f, 0.46f, 1f);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center with { Y = y },
            Size = new Vector3(side, height, side),
            Color = wall,
            PickId = pickId,
            Detail = 1f,
        });

        // A shallow ridge along the roof.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center with { Y = y + height },
            Size = new Vector3(side * 0.98f, 0.45f, side * 0.42f),
            Color = new Vector4(0.42f, 0.42f, 0.40f, 1f),
            PickId = pickId,
            Detail = 1f,
        });

        // The roller shutter: the whole point of a warehouse is the door.
        float shutter = MathF.Min(height * 0.62f, 3.2f);
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = center with { Y = y } + new Vector3(0f, 0f, -(side * 0.5f + 0.06f)),
            Size = new Vector3(side * 0.52f, shutter, 0.12f),
            Color = new Vector4(0.36f, 0.38f, 0.41f, 1f),
            PickId = pickId,
            Detail = 1f,
        });
    }
}
