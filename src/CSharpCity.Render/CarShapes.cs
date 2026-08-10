using System.Numerics;
using CSharpCity.Layout;

namespace CSharpCity.Render;

/// <summary>
/// Builds a vehicle out of boxes, in the car's own frame of reference.
/// </summary>
/// <remarks>
/// The parts here are the same ones <see cref="TravellerShapes"/> assembles — four wheels, a body,
/// a cabin, head and tail lamps — but laid out along the car's forward axis rather than along
/// whichever world axis it happened to be closest to. That is the whole difference: the old vehicle
/// could only be built for a car travelling due north, south, east or west, so a car halfway round
/// a corner was drawn facing one of the two roads and not the direction it was actually going.
///
/// Pedestrians, ducks and trains keep the old shapes and the old code path untouched; only the
/// things that can now genuinely turn need this.
/// </remarks>
internal static class CarShapes
{
    /// <summary>Boxes per vehicle. The instance buffer is sized from this.</summary>
    public const int MaxParts = 11;

    public static int Emit(Span<CarRenderer.Instance> into, in CarAgent car)
    {
        bool truck = car.IsTruck;
        float length = truck ? 3.6f : 2.3f;
        float width = truck ? 1.5f : 1.25f;
        float bodyHeight = truck ? 0.85f : 0.62f;

        var paint = car.Color;
        var glass = new Vector4(0.14f, 0.18f, 0.24f, 1f);
        var rubber = new Vector4(0.07f, 0.07f, 0.08f, 1f);

        int n = 0;

        // Wheels first, so the body sits on them. Local frame: +x forward, +y up, +z right.
        for (int fore = -1; fore <= 1; fore += 2)
        for (int side = -1; side <= 1; side += 2)
            into[n++] = Part(car, new Vector3(length * 0.32f * fore, 0.34f, width * 0.46f * side),
                new Vector3(0.52f, 0.68f, 0.30f), rubber);

        into[n++] = Part(car, new Vector3(0f, 0.22f + bodyHeight * 0.5f, 0f),
            new Vector3(length, bodyHeight, width), paint);

        // Cabin: a glasshouse set back on a car, a boxy cab up front on a truck.
        float cabinOffset = truck ? length * 0.28f : -length * 0.06f;
        float cabinLength = truck ? length * 0.34f : length * 0.52f;
        float cabinHeight = truck ? 0.75f : 0.5f;
        into[n++] = Part(car,
            new Vector3(cabinOffset, 0.22f + bodyHeight + cabinHeight * 0.5f, 0f),
            new Vector3(cabinLength, cabinHeight, width * 0.9f), truck ? paint : glass);

        // Head and tail lamps, self-lit so they carry at night.
        for (int side = -1; side <= 1; side += 2)
        {
            into[n++] = Lamp(car, new Vector3(length * 0.5f, 0.45f, width * 0.30f * side),
                new Vector4(1.0f, 0.96f, 0.82f, 1f));
            into[n++] = Lamp(car, new Vector3(-length * 0.5f, 0.45f, width * 0.30f * side),
                new Vector4(0.95f, 0.12f, 0.08f, 1f));
        }

        return n;
    }

    static CarRenderer.Instance Part(in CarAgent car, Vector3 offset, Vector3 size, Vector4 colour)
        => new()
        {
            // The offset is in the car's frame; the shader applies the same yaw and pitch to it as
            // to the box itself, so a part stays bolted on however the car is angled.
            Center = car.Position + Rotate(offset, car.Yaw, car.Pitch),
            Size = size,
            Yaw = car.Yaw,
            Pitch = car.Pitch,
            Color = colour,
            Flags = 0u,
        };

    static CarRenderer.Instance Lamp(in CarAgent car, Vector3 offset, Vector4 colour)
    {
        var lamp = Part(car, offset, new Vector3(0.14f, 0.16f, 0.30f), colour);
        lamp.Flags = (uint)BoxFlags.Emissive;
        return lamp;
    }

    static Vector3 Rotate(Vector3 v, float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        var tilted = new Vector3(v.X * cp - v.Y * sp, v.X * sp + v.Y * cp, v.Z);
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new Vector3(tilted.X * c - tilted.Z * s, tilted.Y, tilted.X * s + tilted.Z * c);
    }
}
