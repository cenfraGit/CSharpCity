using System.Numerics;

namespace CSharpCity.Render;

/// <summary>First-person camera. Walk mode pins you to eye height; fly mode is for the skyline read.</summary>
public sealed class Camera
{
    const float EyeHeight = 1.7f;

    public Vector3 Position;
    public float Yaw = -90f;
    public float Pitch;
    public bool Flying;
    public float WalkSpeed = 14f;
    public float FlySpeed = 45f;
    /// <summary>
    /// Vertical field of view in degrees. Wide enough to feel like standing in a street rather
    /// than looking down a telescope; the scroll wheel takes it from there.
    /// </summary>
    public float Fov = 78f;

    public const float MinFov = 45f;
    public const float MaxFov = 105f;

    public Vector3 Front
    {
        get
        {
            var yaw = Yaw * MathF.PI / 180f;
            var pitch = Pitch * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(yaw) * MathF.Cos(pitch),
                MathF.Sin(pitch),
                MathF.Sin(yaw) * MathF.Cos(pitch)));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Front, Vector3.UnitY));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Front, Vector3.UnitY);

    /// <remarks>
    /// The near plane dominates depth-buffer precision: a 0.1 m near with a 4 km far spends almost
    /// the entire 24-bit range on the first few metres, leaving neighbouring flat surfaces —
    /// pavement, roads, junction patches — fighting for the same depth value further out. Eye height
    /// is 1.7 m and you can't walk closer than a step to a wall, so 0.4 m costs nothing visible and
    /// buys roughly an order of magnitude of precision.
    /// </remarks>
    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(Fov * MathF.PI / 180f, aspect, 0.4f, 2600f);

    public void Look(float deltaX, float deltaY, float sensitivity = 0.12f)
    {
        Yaw += deltaX * sensitivity;
        Pitch = Math.Clamp(Pitch - deltaY * sensitivity, -89f, 89f);
    }

    /// <summary>
    /// <paramref name="move"/> is in local axes: X = strafe right, Y = up (fly only), Z = forward.
    /// </summary>
    public void Move(Vector3 move, float deltaTime, bool sprinting)
    {
        if (move.LengthSquared() > 0) move = Vector3.Normalize(move);
        var speed = (Flying ? FlySpeed : WalkSpeed) * (sprinting ? 3f : 1f) * deltaTime;

        // Walking keeps forward motion in the ground plane so looking up doesn't launch you.
        var forward = Flying ? Front : Vector3.Normalize(new Vector3(Front.X, 0, Front.Z));
        Position += (Right * move.X + forward * move.Z) * speed;

        if (Flying) Position += Vector3.UnitY * move.Y * speed;
        else Position.Y = EyeHeight;
    }

    public void ToggleFly()
    {
        Flying = !Flying;
        if (!Flying) Position.Y = EyeHeight;
    }
}
