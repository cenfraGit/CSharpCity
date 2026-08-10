using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// Car parks in the leftover ground around a building that is much smaller than its lot.
/// </summary>
/// <remarks>
/// A lot's size comes from how much the treemap owed the type, and a building's footprint comes
/// from its fields and properties. Those are different numbers, so a type that is important enough
/// to be given a block of its own but slim enough to build small ends up as one tower marooned in
/// an acre of empty ground. Empty ground reads as unfinished — the eye takes it for something the
/// generator failed to fill rather than something it chose.
///
/// A car park is the honest thing to put there: it is what a real city does with land next to a
/// building that doesn't need it, and it says nothing about the code, which keeps it out of the way
/// of every channel that does. Small gaps are left alone deliberately; they already read as a
/// forecourt, and paving them would make the city look like a retail estate.
/// </remarks>
internal static class Parking
{
    /// <summary>Below this the leftover ground is a garden, not a car park.</summary>
    const float MinSide = 13f;
    /// <summary>A lot smaller than this across is never big enough to be worth splitting.</summary>
    const float MinLotSide = 26f;
    /// <summary>Only lots where the building leaves most of the ground unused.</summary>
    const float MaxBuildingShare = 0.42f;
    /// <summary>Clearance from the building and from the lot's edges.</summary>
    const float Margin = 2.4f;

    static readonly Vector4 Asphalt = new(0.155f, 0.155f, 0.17f, 1f);

    /// <summary>
    /// How far a car park keeps from the centre of a road, on top of the road's own half-width:
    /// enough for the pavement and its kerb.
    /// </summary>
    const float RoadClearance = 2.6f;

    public static int Build(SceneGraph scene, RoadGraph graph)
    {
        int laid = 0;
        foreach (var lot in scene.Lots)
        {
            // Landmarks keep their empty ground: that space is the plaza, and it is already doing
            // a job.
            if (lot.IsLandmark) continue;
            if (TryAdd(scene, graph, lot.Lot, lot.Centre, lot.Side, lot.Seed)) laid++;
        }
        return laid;
    }

    /// <summary>Lays a car park in the largest empty strip of a lot, if there is one worth having.</summary>
    static bool TryAdd(SceneGraph scene, RoadGraph graph, Bounds2 lot, Vector3 centre, float side,
        int seed)
    {
        if (MathF.Min(lot.Width, lot.Depth) < MinLotSide) return false;

        float footprint = side * side;
        if (footprint > lot.Width * lot.Depth * MaxBuildingShare) return false;

        // The building sits in the middle, so the leftover ground is a ring around it. Take the
        // widest of the four strips rather than trying to pave the whole ring — an L-shaped car
        // park round the back of a building is a worse read than a rectangle beside it.
        float half = side * 0.5f;
        var strips = new[]
        {
            new Bounds2(lot.X, lot.Z, centre.X - half - lot.X - Margin, lot.Depth),
            new Bounds2(centre.X + half + Margin, lot.Z,
                lot.X + lot.Width - (centre.X + half + Margin), lot.Depth),
            new Bounds2(lot.X, lot.Z, lot.Width, centre.Z - half - lot.Z - Margin),
            new Bounds2(lot.X, centre.Z + half + Margin, lot.Width,
                lot.Z + lot.Depth - (centre.Z + half + Margin)),
        };

        var best = strips
            .Select(s => s.Deflate(Margin))
            .Where(s => s.Width >= MinSide && s.Depth >= MinSide)
            .OrderByDescending(s => s.Width * s.Depth)
            .FirstOrDefault();

        if (best.Width < MinSide || best.Depth < MinSide) return false;

        // Pull back from any road that reaches into the plot.
        //
        // A lot's edge is not reliably clear of the tarmac beside it: the treemap insets each cell
        // by half the road it abuts, but the arrangement afterwards merges near-parallel roads and
        // snaps their centrelines onto one another, which can move a road by up to half its width
        // after the plots around it are already fixed. A car park is the first thing big and flat
        // enough for that to show as a slab of asphalt lying across a carriageway.
        best = ClearOfRoads(graph, best);
        if (best.Width < MinSide || best.Depth < MinSide) return false;

        // Bays run across the short side, which is what makes the aisle read as an aisle.
        bool baysAlongX = best.Width >= best.Depth;

        scene.Roads.Add(new RoadQuad
        {
            Center = new Vector3(best.CenterX, CityLayout.StreetSurfaceY, best.CenterZ),
            Length = baysAlongX ? best.Width : best.Depth,
            Width = baysAlongX ? best.Depth : best.Width,
            Yaw = baysAlongX ? 0f : MathF.PI * 0.5f,
            Color = Asphalt,
            Flags = (uint)RoadFlags.Parking,
            Layer = CityLayer.Sidewalks,
        });

        AddLighting(scene, best, seed);
        return true;
    }

    /// <summary>
    /// Trims a rectangle back until no road's corridor reaches into it.
    /// </summary>
    /// <remarks>
    /// Trimming per side rather than deflating uniformly: a car park with a road along one edge
    /// should lose ground on that edge only, not shrink away from the building it belongs to.
    /// </remarks>
    static Bounds2 ClearOfRoads(RoadGraph graph, Bounds2 area)
    {
        float minX = area.X, maxX = area.X + area.Width;
        float minZ = area.Z, maxZ = area.Z + area.Depth;

        for (int e = 0; e < graph.Edges.Length; e++)
        {
            var edge = graph.Edges[e];
            if (edge.Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

            var a = graph.Nodes[edge.A].Position;
            var b = graph.Nodes[edge.B].Position;
            float band = edge.Width * 0.5f + RoadClearance;

            // Roads are axis-aligned, so a corridor is just the segment's box grown by the band.
            float roadMinX = MathF.Min(a.X, b.X) - band, roadMaxX = MathF.Max(a.X, b.X) + band;
            float roadMinZ = MathF.Min(a.Z, b.Z) - band, roadMaxZ = MathF.Max(a.Z, b.Z) + band;

            if (roadMaxX <= minX || roadMinX >= maxX) continue;
            if (roadMaxZ <= minZ || roadMinZ >= maxZ) continue;

            // Give up the smaller bite: cut from whichever side loses the least ground.
            float fromLeft = roadMaxX - minX, fromRight = maxX - roadMinX;
            float fromNear = roadMaxZ - minZ, fromFar = maxZ - roadMinZ;
            float least = MathF.Min(MathF.Min(fromLeft, fromRight), MathF.Min(fromNear, fromFar));

            if (least == fromLeft) minX = roadMaxX;
            else if (least == fromRight) maxX = roadMinX;
            else if (least == fromNear) minZ = roadMaxZ;
            else maxZ = roadMinZ;

            if (maxX - minX < MinSide || maxZ - minZ < MinSide) return new Bounds2(minX, minZ, 0f, 0f);
        }

        return new Bounds2(minX, minZ, maxX - minX, maxZ - minZ);
    }

    /// <summary>A column or two, so the place is legible at night and has some height to it.</summary>
    static void AddLighting(SceneGraph scene, Bounds2 area, int seed)
    {
        int columns = Math.Clamp((int)(MathF.Max(area.Width, area.Depth) / 22f), 1, 3);

        for (int i = 0; i < columns; i++)
        {
            float t = (i + 0.5f) / columns;
            var at = area.Width >= area.Depth
                ? new Vector3(area.X + area.Width * t, 0f, area.CenterZ)
                : new Vector3(area.CenterX, 0f, area.Z + area.Depth * t);

            // Nudged off the centreline so the mast doesn't stand in the middle of the aisle.
            float nudge = (StableHash.Unit(seed, i) - 0.5f) * MathF.Min(area.Width, area.Depth) * 0.6f;
            at = area.Width >= area.Depth ? at with { Z = at.Z + nudge } : at with { X = at.X + nudge };

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at,
                Size = new Vector3(0.22f, 6.5f, 0.22f),
                Color = new Vector4(0.22f, 0.23f, 0.25f, 1f),
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Sidewalks,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at with { Y = 6.5f },
                Size = new Vector3(0.9f, 0.25f, 0.5f),
                Color = new Vector4(1f, 0.94f, 0.76f, 1f),
                PickId = -1,
                Detail = 1f,
                Flags = (uint)BoxFlags.Emissive,
                Layer = CityLayer.Sidewalks,
            });
        }
    }
}
