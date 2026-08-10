using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>How wide and how important a road is, by the level of the hierarchy it divides.</summary>
internal enum RoadClass
{
    /// <summary>Between projects. The widest roads in the city.</summary>
    Boulevard,
    /// <summary>Between namespaces inside a project.</summary>
    Street,
    /// <summary>Inside a namespace, between lots.</summary>
    Alley,
}

/// <summary>
/// Builds the street grid from the treemap's own cut lines, so neighbouring blocks share the road
/// between them instead of each owning a private ring.
/// </summary>
/// <remarks>
/// The previous approach deflated every block by a full margin and then drew a complete four-sided
/// ring around it. Two neighbours therefore left twice the intended gap and filled it with two
/// parallel roads meeting at a seam — blocks read as detached islands rather than city blocks.
///
/// Here each division reported by <see cref="Treemap.Cut"/> becomes exactly one road, centred on the
/// boundary, and the cells on either side pull back by half its width. The street belongs to neither
/// block, which is what makes it a street.
/// </remarks>
internal static class StreetNetwork
{
    static readonly Vector4 Asphalt = new(0.13f, 0.13f, 0.145f, 1f);

    /// <summary>
    /// Trims a cell back from the cuts that bound it — and only from those. A cell touching the
    /// region's outer edge is already clear of the parent's road, so insetting there too would open
    /// an unpaved gap the city never uses.
    /// </summary>
    public static Bounds2 InsetFromCuts(Bounds2 cell, Bounds2 region, float half)
    {
        const float Epsilon = 0.01f;

        // Never inset a cell out of existence. A small district — two types, say — gets a narrow
        // cell, and taking half a boulevard off each side would leave nothing to build on, silently
        // emptying the whole district. Better a road that encroaches slightly than a lost project.
        float limitX = MathF.Min(half, cell.Width * 0.35f);
        float limitZ = MathF.Min(half, cell.Depth * 0.35f);

        float left = cell.X > region.X + Epsilon ? limitX : 0f;
        float right = cell.X + cell.Width < region.X + region.Width - Epsilon ? limitX : 0f;
        float near = cell.Z > region.Z + Epsilon ? limitZ : 0f;
        float far = cell.Z + cell.Depth < region.Z + region.Depth - Epsilon ? limitZ : 0f;

        return new Bounds2(
            cell.X + left,
            cell.Z + near,
            MathF.Max(0f, cell.Width - left - right),
            MathF.Max(0f, cell.Depth - near - far));
    }

    /// <summary>
    /// Records one road along a cut, and any traffic on it.
    /// </summary>
    /// <remarks>
    /// This no longer paints anything. Tarmac is emitted in one pass by <see cref="RoadSurfaces"/>
    /// once every cut in the city is known, because a junction's size depends on the widest road
    /// arriving at it — and at the moment a block's alleys are cut, the boulevard they will meet
    /// has not been cut yet.
    /// </remarks>
    public static void AddCut(SceneGraph scene, Treemap.Cut cut, float width, RoadClass roadClass)
    {
        float length = cut.SpanEnd - cut.SpanStart;
        if (length < 1f || width < 0.4f) return;

        // Hand the raw cut to the arrangement, which works out where roads actually meet. Unlike
        // the centreline below, nothing here is extended or guessed at: the geometry is reported as
        // the treemap produced it, and the builder closes the gaps from the widths involved.
        scene.RoadCuts.Add(new RoadCut(cut.Vertical, cut.Position, cut.SpanStart, cut.SpanEnd,
            roadClass switch
            {
                RoadClass.Boulevard => RoadKind.Boulevard,
                RoadClass.Street => RoadKind.Street,
                _ => RoadKind.Alley,
            },
            width, CityLayout.StreetSurfaceY));

        // No cars are placed here any more. Ambient traffic used to be a two-point path per
        // street with a couple of vehicles sliding along it; the cars in this city are now agents
        // in TrafficSim, which route over the whole network rather than one road at a time.
        _ = length;
    }
}
