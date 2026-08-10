using System.Numerics;

namespace CSharpCity.Render;

/// <summary>
/// The six clipping planes of a view-projection matrix, for rejecting whole chunks of city that
/// can't possibly be on screen.
/// </summary>
public readonly struct Frustum
{
    // Each plane stored as (nx, ny, nz, d), pointing inward: a point is inside when dot >= 0.
    readonly Vector4 _left, _right, _bottom, _top, _near, _far;

    Frustum(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near, Vector4 far)
    {
        _left = left; _right = right; _bottom = bottom; _top = top; _near = near; _far = far;
    }

    /// <summary>
    /// Gribb–Hartmann extraction: each plane is a sum or difference of two rows of the combined
    /// matrix, which works for any projection without needing the camera parameters separately.
    /// </summary>
    public static Frustum FromViewProjection(Matrix4x4 m) => new(
        Normalize(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
        Normalize(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
        Normalize(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
        Normalize(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
        Normalize(new Vector4(m.M13, m.M23, m.M33, m.M43)),
        Normalize(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)));

    static Vector4 Normalize(Vector4 plane)
    {
        float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        return length > 1e-6f ? plane / length : plane;
    }

    /// <summary>
    /// True if any part of the box might be visible. Conservative: it can say yes for a box that
    /// turns out to be hidden, which only costs a draw call, never a missing building.
    /// </summary>
    public bool Intersects(Vector3 min, Vector3 max)
    {
        return Inside(_left) && Inside(_right) && Inside(_bottom)
               && Inside(_top) && Inside(_near) && Inside(_far);

        bool Inside(Vector4 plane)
        {
            // Test the corner furthest along the plane normal: if even that is behind, all are.
            var positive = new Vector3(
                plane.X >= 0 ? max.X : min.X,
                plane.Y >= 0 ? max.Y : min.Y,
                plane.Z >= 0 ? max.Z : min.Z);
            return plane.X * positive.X + plane.Y * positive.Y + plane.Z * positive.Z + plane.W >= 0f;
        }
    }
}
