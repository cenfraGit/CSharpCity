using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// How high the walkable surface is at any point in the city.
/// </summary>
/// <remarks>
/// This exists to get people back on the ground. The flat layers are stacked at fixed heights so
/// that two surfaces covering the same ground can never fight for the same depth values, and
/// footpaths were put at the top of that stack — nearly a metre and a half up — because a desire
/// line crosses everything and had to be above all of it. The paths themselves are thin enough that
/// nobody noticed, but the people walking on them were floating at roughly their own eye height.
///
/// The fix is to stop treating a footpath as one flat ribbon at one height. Sampled against this
/// map, a path lies on the ground where the ground is bare and steps up onto the tarmac where it
/// crosses a road, which is both what a real path does and what stops anyone hovering.
///
/// A coarse grid is enough: it decides which of a handful of fixed heights a point is on, and the
/// surfaces are metres across. Rotated quads are entered by their bounding box, which slightly
/// over-covers a roundabout's chords and is invisible at this resolution.
/// </remarks>
internal sealed class GroundHeights
{
    const float Cell = 2f;

    readonly float _originX, _originZ;
    readonly int _columns, _rows;
    readonly float[] _height;
    readonly float _bare;

    public GroundHeights(SceneGraph scene, Bounds2 city, float bare)
    {
        _bare = bare;
        _originX = city.X - Cell;
        _originZ = city.Z - Cell;
        _columns = Math.Max(1, (int)(city.Width / Cell) + 3);
        _rows = Math.Max(1, (int)(city.Depth / Cell) + 3);

        _height = new float[_columns * _rows];
        Array.Fill(_height, bare);

        foreach (var quad in scene.Roads)
        {
            // Footpaths are what this is being built for; they cannot also be an input to it.
            if ((quad.Flags & (uint)RoadFlags.Footpath) != 0) continue;
            // A pool of lamplight is something you can walk through, not something you stand on.
            if ((quad.Flags & (uint)RoadFlags.LightPool) != 0) continue;
            // A deck is not ground: a path passing beneath one stays where it is.
            if (quad.Center.Y > 3f) continue;
            if (quad.Center.Y <= bare) continue;

            float cos = MathF.Abs(MathF.Cos(quad.Yaw)), sin = MathF.Abs(MathF.Sin(quad.Yaw));
            float halfX = (quad.Length * cos + quad.Width * sin) * 0.5f;
            float halfZ = (quad.Length * sin + quad.Width * cos) * 0.5f;

            Stamp(quad.Center.X - halfX, quad.Center.Z - halfZ,
                  quad.Center.X + halfX, quad.Center.Z + halfZ, quad.Center.Y);
        }
    }

    void Stamp(float minX, float minZ, float maxX, float maxZ, float y)
    {
        int x0 = Column(minX), x1 = Column(maxX);
        int z0 = Row(minZ), z1 = Row(maxZ);

        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            int at = z * _columns + x;
            if (_height[at] < y) _height[at] = y;
        }
    }

    /// <summary>The surface underfoot at a point, or the bare ground where nothing is paved.</summary>
    public float At(Vector3 point) => _height[Row(point.Z) * _columns + Column(point.X)];

    /// <summary>True where nothing has been paved, so a worn path can show through.</summary>
    public bool IsBare(Vector3 point) => At(point) <= _bare;

    int Column(float x) => Math.Clamp((int)((x - _originX) / Cell), 0, _columns - 1);
    int Row(float z) => Math.Clamp((int)((z - _originZ) / Cell), 0, _rows - 1);
}
