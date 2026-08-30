namespace CSharpCity.Layout;

/// <summary>
/// How far apart the towns stand, when each project is laid out as an island of its own.
/// </summary>
/// <remarks>
/// One project as one town says something the single-square layout cannot: that these are separate
/// deliverables which happen to ship together. The distance between them is itself informative, and
/// crossing it is what a project reference costs.
///
/// <b>Towns stay flat.</b> The whole surface-height stack, the footpath system and
/// <see cref="GroundHeights"/> all rest on the city floor being exactly level, and lifting the ground
/// inside a town would ripple through every one of them. Putting the coast, the woods and the water
/// strictly outside the towns gets the entire visual payoff and touches almost nothing: the
/// heightfield already takes a list of flat rectangles and shapes everything else around them.
/// </remarks>
internal static class Countryside
{
    /// <summary>
    /// How much bigger the world is than the same content packed into one square.
    /// </summary>
    /// <remarks>
    /// Modest on purpose, and the reason the memory worry about this layout turned out to be
    /// unfounded. Because the towns are packed by the same treemap rather than scattered, the world
    /// grows by a constant factor rather than with the number of projects — so the heightfield and
    /// the walkable-ground grid grow by that factor squared and no further. Spreading towns to
    /// arbitrary distances would have been the expensive idea.
    /// </remarks>
    const float Spread = 1.55f;

    /// <summary>Countryside between two neighbouring towns, as a share of the packed city size.</summary>
    const float GapShare = 0.16f;

    /// <summary>Enough open ground that leaving a town reads as leaving it.</summary>
    const float MinimumGap = 90f;

    public static Bounds2 World(float packedSide)
    {
        float side = packedSide * Spread;
        return new Bounds2(0, 0, side, side);
    }

    /// <summary>
    /// The buildable part of one treemap cell: the cell, less half a gap on every side.
    /// </summary>
    /// <remarks>
    /// Half on each side, so two neighbouring towns are separated by a whole gap rather than half of
    /// one. A cell too small to give up that much keeps a fixed fraction instead — a town squeezed
    /// to nothing would drop its project out of the world entirely, which is the one outcome the
    /// layout must never produce quietly.
    /// </remarks>
    public static Bounds2 Town(Bounds2 cell, float packedSide)
    {
        float gap = MathF.Max(MinimumGap, packedSide * GapShare);
        float inset = gap * 0.5f;

        if (cell.Width - inset * 2f < cell.Width * 0.35f
            || cell.Depth - inset * 2f < cell.Depth * 0.35f)
            inset = MathF.Min(cell.Width, cell.Depth) * 0.14f;

        return new Bounds2(cell.X + inset, cell.Z + inset,
            MathF.Max(cell.Width - inset * 2f, 1f), MathF.Max(cell.Depth - inset * 2f, 1f));
    }

}
