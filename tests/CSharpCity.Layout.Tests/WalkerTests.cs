namespace CSharpCity.Layout.Tests;

/// <summary>
/// Where the people are, vertically.
/// </summary>
public class WalkerTests
{
    /// <summary>The tallest thing anyone should ever be standing on.</summary>
    const float Highest = 1.20f;

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void WalkersAreOnTheGroundNotAboveIt(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Connect(Fixture.Solution(projects, typesPer, depth)));

        var walkers = scene.Travellers
            .Where(t => t.Kind == TravellerKind.Pedestrian)
            .Select(t => scene.Paths[t.PathIndex])
            .ToList();
        if (walkers.Count == 0) return;

        int onTheGround = 0, total = 0;
        foreach (var path in walkers)
            foreach (var point in path.Points)
            {
                total++;
                // The whole path used to be a single ribbon at 1.44 — head height — so everybody
                // on it hovered. Most of a path runs over bare ground; the rest is where it steps
                // up onto a road or a forecourt to cross it.
                Assert.True(point.Y <= Highest,
                    $"A walker's route passes {point.Y:F2}m above the ground.");
                if (point.Y <= CityLayout.StreetSurfaceY + 0.01f) onTheGround++;
            }

        Assert.True(onTheGround > total / 2,
            $"Only {onTheGround} of {total} steps are at ground level; the rest are on something.");
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void FootpathsOnlyShowOnBareGround(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Connect(Fixture.Solution(projects, typesPer, depth)));
        var graph = scene.RoadNetwork;
        if (graph.IsEmpty) return;

        // A worn path stops at the kerb and resumes on the far side. Letting it run across the
        // carriageway instead is what forced it up to the top of the layer stack in the first
        // place: two flat surfaces covering the same ground cannot share a height.
        int across = 0, total = 0;
        foreach (var path in scene.Roads.Where(r => (r.Flags & (uint)RoadFlags.Footpath) != 0))
        {
            total++;
            if (!graph.TryNearestEdge(path.Center, 20f, out int edge, out float along)) continue;
            if (graph.Edges[edge].Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

            var centreline = graph.PointOn(edge, along);
            float gap = MathF.Sqrt(MathF.Pow(path.Center.X - centreline.X, 2)
                                 + MathF.Pow(path.Center.Z - centreline.Z, 2));
            if (gap < graph.Edges[edge].Width * 0.4f) across++;
        }

        // The proportional bound is the one that means anything; the constant only stops a city
        // with a single footpath in it from failing on a sample size of one.
        Assert.True(across <= 1 + total / 10,
            $"{across} of {total} footpath segments are centred on a carriageway.");
    }
}
