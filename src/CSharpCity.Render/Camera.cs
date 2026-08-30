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

    /// <summary>
    /// Cruising and sprint speeds, in metres per second, given as absolutes rather than as a base
    /// and a multiplier.
    /// </summary>
    /// <remarks>
    /// Sprint used to be a shared <c>× 3</c> on whichever base was active, which made the two
    /// speeds impossible to tune independently: slowing the walk to something you could actually
    /// read a street at also slowed the sprint that was already right. Four absolute numbers cost
    /// two extra fields and say exactly what they mean.
    ///
    /// There is no acceleration or damping anywhere — a key press is full speed on the same frame —
    /// so these four values are the entire feel of moving through the city.
    /// </remarks>
    public float WalkSpeed = 8f;
    public float WalkSprintSpeed = 42f;
    public float FlySpeed = 24f;
    public float FlySprintSpeed = 135f;

    /// <summary>
    /// Space and Ctrl, which get their own pair rather than borrowing the horizontal ones.
    /// </summary>
    /// <remarks>
    /// Roughly seventy per cent quicker than flying flat, because the two are not the same journey.
    /// Horizontal speed is set by how fast a street should go past; vertical speed is set by how
    /// long it takes to get above the rooftops and back down, and at level-flight pace that was a
    /// tedious few seconds every time.
    /// </remarks>
    public float ClimbSpeed = 41f;
    public float ClimbSprintSpeed = 230f;
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

    /// <summary>
    /// Where you are facing with the pitch thrown away: <see cref="Front"/> flattened onto the ground.
    /// </summary>
    /// <remarks>
    /// Taken from the yaw directly rather than by flattening and renormalising <see cref="Front"/>,
    /// which collapses to a zero-length vector when you look straight up or down — exactly the moment
    /// you most need a heading.
    /// </remarks>
    public Vector3 Heading
    {
        get
        {
            var yaw = Yaw * MathF.PI / 180f;
            return new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        }
    }

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
        // Normalised across the ground only, so holding Space while flying forward climbs at the
        // full rate rather than trading half of it away for the forward motion. The two axes are
        // independent controls and behave like it.
        var ground = new Vector2(move.X, move.Z);
        if (ground.LengthSquared() > 1f) ground = Vector2.Normalize(ground);

        float speed = (Flying
            ? sprinting ? FlySprintSpeed : FlySpeed
            : sprinting ? WalkSprintSpeed : WalkSpeed) * deltaTime;

        // Both modes keep WASD in the ground plane, so looking up never launches you and looking
        // down never drives you into the pavement. Flying used to follow the full look direction,
        // which made altitude something you fought with the mouse instead of something you chose:
        // any glance at a rooftop turned level flight into a climb. Height belongs to Space and
        // Ctrl alone, where it can be held steady while you look wherever you like.
        Position += (Right * ground.X + Heading * ground.Y) * speed;

        if (Flying)
            Position += Vector3.UnitY * Math.Clamp(move.Y, -1f, 1f)
                        * (sprinting ? ClimbSprintSpeed : ClimbSpeed) * deltaTime;
        else Position.Y = EyeHeight;
    }

    public void ToggleFly()
    {
        Flying = !Flying;
        if (!Flying) Position.Y = EyeHeight;
    }
}
