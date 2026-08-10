using System.Numerics;
using CSharpCity.Layout;

namespace CSharpCity.Render;

/// <summary>
/// Aeroplanes and helicopters, built in the aircraft's own frame.
/// </summary>
/// <remarks>
/// These exist because there was nothing else for them to be. The traveller shapes only knew about
/// wheeled vehicles and waterfowl, so a plane was declared a truck and a helicopter was too — which
/// is how the city ended up with lorries circling every airport and one more hanging over the worst
/// building in it.
///
/// Like cars, they go through the rotating renderer rather than the axis-snapped one, for the
/// obvious reason: an aircraft on a curved circuit is pointing somewhere other than due north.
/// </remarks>
internal static class AircraftShapes
{
    /// <summary>Boxes per aircraft. The instance buffer is sized from this.</summary>
    public const int MaxParts = 6;

    public static int Emit(Span<CarRenderer.Instance> into, in Traveller traveller, Vector3 at,
        Vector3 heading, float time)
    {
        float yaw = MathF.Atan2(heading.Z, heading.X);
        float pitch = MathF.Asin(Math.Clamp(heading.Y, -1f, 1f));

        return traveller.Kind == TravellerKind.Helicopter
            ? Helicopter(into, at, yaw, traveller.Color, time)
            : Plane(into, at, yaw, pitch, traveller.Color);
    }

    static int Plane(Span<CarRenderer.Instance> into, Vector3 at, float yaw, float pitch,
        Vector4 colour)
    {
        var trim = new Vector4(0.30f, 0.34f, 0.44f, 1f);
        int n = 0;

        // Local frame: +x forward, +y up, +z right.
        into[n++] = Part(at, yaw, pitch, new Vector3(0f, 0f, 0f), new Vector3(9f, 1.5f, 1.5f), colour);
        // Wings, swept back a little by sitting behind the nose.
        into[n++] = Part(at, yaw, pitch, new Vector3(-0.6f, -0.1f, 0f),
            new Vector3(2.6f, 0.25f, 11f), colour);
        // Tailplane and fin.
        into[n++] = Part(at, yaw, pitch, new Vector3(-4.0f, 0.3f, 0f),
            new Vector3(1.4f, 0.2f, 4.2f), colour);
        into[n++] = Part(at, yaw, pitch, new Vector3(-4.0f, 1.2f, 0f),
            new Vector3(1.4f, 1.8f, 0.2f), trim);
        // Engines under the wings.
        for (int side = -1; side <= 1; side += 2)
            into[n++] = Part(at, yaw, pitch, new Vector3(-0.2f, -0.7f, 3.2f * side),
                new Vector3(2.2f, 0.8f, 0.8f), trim);

        return n;
    }

    static int Helicopter(Span<CarRenderer.Instance> into, Vector3 at, float yaw, Vector4 colour,
        float time)
    {
        var glass = new Vector4(0.16f, 0.20f, 0.26f, 1f);
        int n = 0;

        into[n++] = Part(at, yaw, 0f, new Vector3(0f, 0f, 0f), new Vector3(4.2f, 1.8f, 1.8f), colour);
        into[n++] = Part(at, yaw, 0f, new Vector3(1.9f, 0.1f, 0f), new Vector3(1.4f, 1.3f, 1.5f), glass);
        // Tail boom and fin.
        into[n++] = Part(at, yaw, 0f, new Vector3(-3.4f, 0.3f, 0f), new Vector3(3.6f, 0.45f, 0.45f), colour);
        into[n++] = Part(at, yaw, 0f, new Vector3(-5.0f, 0.9f, 0f), new Vector3(0.5f, 1.2f, 0.15f), colour);

        // The rotor carries its own yaw, so it spins independently of the airframe. Fast enough to
        // read as turning, slow enough not to strobe against the frame rate.
        into[n++] = Part(at, yaw + time * 11f, 0f, new Vector3(0f, 1.2f, 0f),
            new Vector3(9.5f, 0.08f, 0.5f), new Vector4(0.18f, 0.19f, 0.21f, 1f));
        into[n++] = Part(at, yaw, time * 9f, new Vector3(-5.0f, 0.9f, 0.28f),
            new Vector3(1.8f, 0.06f, 0.2f), new Vector4(0.18f, 0.19f, 0.21f, 1f));

        return n;
    }

    static CarRenderer.Instance Part(Vector3 at, float yaw, float pitch, Vector3 offset,
        Vector3 size, Vector4 colour) => new()
    {
        Center = at + Rotate(offset, yaw, pitch),
        Size = size,
        Yaw = yaw,
        Pitch = pitch,
        Color = colour,
        Flags = 0u,
    };

    static Vector3 Rotate(Vector3 v, float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        var tilted = new Vector3(v.X * cp - v.Y * sp, v.X * sp + v.Y * cp, v.Z);
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new Vector3(tilted.X * c - tilted.Z * s, tilted.Y, tilted.X * s + tilted.Z * c);
    }
}
