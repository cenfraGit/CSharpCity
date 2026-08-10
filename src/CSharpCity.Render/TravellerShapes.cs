using System.Numerics;
using CSharpCity.Layout;

namespace CSharpCity.Render;

/// <summary>
/// Builds each traveller out of several boxes — legs, torso, head, wheels, lights — and animates
/// them from the distance they've walked or driven.
/// </summary>
/// <remarks>
/// A single box per traveller reads as a sliding brick. The parts cost almost nothing, because they
/// go into the same instanced buffer that was already being rebuilt every frame, and they buy the
/// thing the city was missing at street level: something recognisably alive.
///
/// Everything is axis-aligned, so each figure is built along whichever axis it's mostly travelling.
/// That's exact for the street grid and rail, and close enough on a diagonal footpath that nobody
/// reads it as wrong.
/// </remarks>
internal static class TravellerShapes
{
    /// <summary>Upper bound on boxes one traveller can emit; callers size their buffer by this.</summary>
    public const int MaxParts = 11;

    /// <param name="identity">
    /// A value that is constant for the lifetime of this traveller.
    /// </param>
    /// <remarks>
    /// Identity must not come from the position. Deriving it from where the traveller currently
    /// stands re-rolls it every frame, so hats blink in and out and clothing changes colour as
    /// people walk — the appearance has to be stable even though the position isn't.
    /// </remarks>
    public static int Emit(Span<BoxRenderer.Instance> into, in Traveller traveller, Vector3 at,
        Vector3 direction, float distance, int identity)
    {
        bool alongX = MathF.Abs(direction.X) >= MathF.Abs(direction.Z);
        float sign = alongX ? MathF.Sign(direction.X == 0 ? 1 : direction.X)
                            : MathF.Sign(direction.Z == 0 ? 1 : direction.Z);
        float seed = Hash(identity * 0.6180339f);

        return traveller.Kind switch
        {
            TravellerKind.Pedestrian => Person(into, traveller, at, alongX, sign, distance, seed),
            TravellerKind.Duck => Duck(into, traveller, at, alongX, sign, distance),
            TravellerKind.Truck => Vehicle(into, traveller, at, alongX, sign, seed, truck: true),
            _ => Vehicle(into, traveller, at, alongX, sign, seed, truck: false),
        };
    }

    static int Person(Span<BoxRenderer.Instance> into, in Traveller traveller, Vector3 at,
        bool alongX, float sign, float distance, float seed)
    {
        // Stride from ground distance, so the legs keep pace with the actual movement.
        float gait = distance * 1.9f;
        float swing = MathF.Sin(gait) * 0.26f;
        // Two footfalls per stride, hence the doubled frequency on the bob.
        float bob = MathF.Abs(MathF.Cos(gait)) * 0.055f;

        var skin = Tint(0.86f, 0.68f, 0.54f, seed * 0.35f);
        var trousers = Tint(0.20f, 0.22f, 0.30f, seed * 0.7f);
        var shirt = traveller.Color;

        int n = 0;
        Vector3 Fore(float d) => alongX ? new Vector3(d * sign, 0, 0) : new Vector3(0, 0, d * sign);
        Vector3 Side(float d) => alongX ? new Vector3(0, 0, d) : new Vector3(d, 0, 0);

        // Legs are separated sideways as well as swinging fore/aft. Without the lateral offset they
        // land in exactly the same place twice per stride — two identical coincident boxes, which
        // flickers every time the gait crosses zero.
        into[n++] = Box(at + Fore(swing) + Side(0.115f),
            Span(alongX, 0.17f, 0.22f) with { Y = 0.80f }, trousers);
        into[n++] = Box(at + Fore(-swing) - Side(0.115f),
            Span(alongX, 0.17f, 0.22f) with { Y = 0.80f }, trousers);

        float torsoY = at.Y + 0.72f + bob;
        into[n++] = Box(at with { Y = torsoY }, Span(alongX, 0.30f, 0.46f) with { Y = 0.60f }, shirt);

        // Arms counter-swing against the legs. Parenthesised deliberately: `with` binds tighter than
        // `+`, so writing `at + Fore(..) with { Y = .. }` would add at.Y to itself.
        var armOffset = alongX ? new Vector3(0, 0, 0.30f) : new Vector3(0.30f, 0, 0);
        into[n++] = Box((at + armOffset + Fore(-swing * 0.8f)) with { Y = torsoY + 0.06f },
            Span(alongX, 0.13f, 0.13f) with { Y = 0.48f }, shirt);
        into[n++] = Box((at - armOffset + Fore(swing * 0.8f)) with { Y = torsoY + 0.06f },
            Span(alongX, 0.13f, 0.13f) with { Y = 0.48f }, shirt);

        // Head sunk into the shoulders rather than balanced on them: a base sitting exactly on the
        // torso's top face is coplanar, and coplanar faces fight for the same depth.
        float headBase = torsoY + 0.52f;
        into[n++] = Box(at with { Y = headBase }, new Vector3(0.26f, 0.28f, 0.26f), skin);

        // Roughly a third wear something, pulled down over the crown for the same reason.
        if (seed > 0.66f)
            into[n++] = Box(at with { Y = headBase + 0.21f }, new Vector3(0.33f, 0.11f, 0.33f),
                Tint(0.30f, 0.16f, 0.14f, seed));

        return n;
    }

    static int Duck(Span<BoxRenderer.Instance> into, in Traveller traveller, Vector3 at,
        bool alongX, float sign, float distance)
    {
        // Ducks bob on the water rather than stride.
        float bob = MathF.Sin(distance * 2.4f) * 0.035f;
        var body = traveller.Color;

        int n = 0;
        Vector3 Fore(float d) => alongX ? new Vector3(d * sign, 0, 0) : new Vector3(0, 0, d * sign);

        into[n++] = Box(at with { Y = at.Y + bob }, Span(alongX, 0.34f, 0.52f) with { Y = 0.26f }, body);
        into[n++] = Box((at + Fore(0.20f)) with { Y = at.Y + bob + 0.22f },
            new Vector3(0.17f, 0.22f, 0.17f), body);
        into[n++] = Box((at + Fore(0.32f)) with { Y = at.Y + bob + 0.26f },
            new Vector3(0.13f, 0.07f, 0.11f), new Vector4(0.95f, 0.66f, 0.12f, 1f));

        return n;
    }

    static int Vehicle(Span<BoxRenderer.Instance> into, in Traveller traveller, Vector3 at,
        bool alongX, float sign, float seed, bool truck)
    {
        float length = truck ? 3.6f : 2.3f;
        float width = truck ? 1.5f : 1.25f;
        float bodyHeight = truck ? 0.85f : 0.62f;

        var paint = traveller.Color;
        var glass = new Vector4(0.14f, 0.18f, 0.24f, 1f);
        var rubber = new Vector4(0.07f, 0.07f, 0.08f, 1f);

        int n = 0;
        Vector3 Fore(float d) => alongX ? new Vector3(d * sign, 0, 0) : new Vector3(0, 0, d * sign);
        Vector3 Side(float d) => alongX ? new Vector3(0, 0, d) : new Vector3(d, 0, 0);

        // Wheels first, so the body sits on them.
        for (int i = -1; i <= 1; i += 2)
        for (int j = -1; j <= 1; j += 2)
            into[n++] = Box(at + Fore(length * 0.32f * i) + Side(width * 0.46f * j),
                Span(alongX, 0.30f, 0.52f) with { Y = 0.34f }, rubber);

        into[n++] = Box(at with { Y = at.Y + 0.22f },
            Span(alongX, width, length) with { Y = bodyHeight }, paint);

        // Cabin: a glasshouse set back on a car, a boxy cab up front on a truck.
        float cabinOffset = truck ? length * 0.28f : -length * 0.06f;
        float cabinLength = truck ? length * 0.34f : length * 0.52f;
        into[n++] = Box((at + Fore(cabinOffset)) with { Y = at.Y + 0.22f + bodyHeight },
            Span(alongX, width * 0.9f, cabinLength) with { Y = truck ? 0.75f : 0.5f },
            truck ? paint : glass);

        // Head and tail lamps, self-lit so they carry at night.
        for (int j = -1; j <= 1; j += 2)
        {
            into[n++] = Lamp(
                (at + Fore(length * 0.5f) + Side(width * 0.30f * j)) with { Y = at.Y + 0.45f },
                new Vector4(1.0f, 0.96f, 0.82f, 1f));
            into[n++] = Lamp(
                (at - Fore(length * 0.5f) + Side(width * 0.30f * j)) with { Y = at.Y + 0.45f },
                new Vector4(0.95f, 0.12f, 0.08f, 1f));
        }

        return n;
    }

    /// <summary>A box sized by which axis the traveller is facing along.</summary>
    static Vector3 Span(bool alongX, float across, float along) =>
        alongX ? new Vector3(along, 0f, across) : new Vector3(across, 0f, along);

    static BoxRenderer.Instance Box(Vector3 at, Vector3 size, Vector4 colour) => new()
    {
        BasePosition = at,
        Size = size,
        Color = colour,
        Flags = 0,
        Detail = 1f,
    };

    static BoxRenderer.Instance Lamp(Vector3 at, Vector4 colour) => new()
    {
        BasePosition = at,
        Size = new Vector3(0.2f, 0.16f, 0.2f),
        Color = colour,
        Flags = (uint)BoxFlags.Emissive,
        Detail = 1f,
    };

    static Vector4 Tint(float r, float g, float b, float variation)
    {
        float k = 0.82f + Hash(variation * 13.7f) * 0.36f;
        return new Vector4(r * k, g * k, b * k, 1f);
    }

    static float Hash(float value)
    {
        float x = MathF.Sin(value * 127.1f) * 43758.5453f;
        return x - MathF.Floor(x);
    }
}
