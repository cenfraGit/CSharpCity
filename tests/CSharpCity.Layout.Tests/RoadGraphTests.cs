using System.Diagnostics;
using System.Numerics;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The drivable network's invariants.
/// </summary>
/// <remarks>
/// These stand in for a class of bug that only ever showed up as a sighting: a car through a wall,
/// a road that stopped a metre short of the one it was supposed to meet, a whole district no
/// vehicle ever reached. Each was found by walking the city and none of them was visible in a
/// screenshot, which is exactly the kind of thing worth spending a test on.
/// </remarks>
public class RoadGraphTests
{
    static RoadGraph Graph(int projects, int typesPer, int depth) =>
        CityLayout.Build(Fixture.Solution(projects, typesPer, depth)).RoadNetwork;

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void EveryEdgeIsAxisAligned(int projects, int typesPer, int depth)
    {
        var graph = Graph(projects, typesPer, depth);

        foreach (var edge in graph.Edges)
        {
            var a = graph.Nodes[edge.A].Position;
            var b = graph.Nodes[edge.B].Position;
            Assert.True(MathF.Abs(a.X - b.X) < 1e-3f || MathF.Abs(a.Z - b.Z) < 1e-3f,
                $"Edge from {a} to {b} runs diagonally.");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void EdgeLengthsMatchTheirEndpoints(int projects, int typesPer, int depth)
    {
        var graph = Graph(projects, typesPer, depth);

        // PointOn interpolates between the endpoints using Length as the scale, so any disagreement
        // here puts cars somewhere other than the road they are on.
        foreach (var edge in graph.Edges)
        {
            float measured = Vector3.Distance(graph.Nodes[edge.A].Position,
                graph.Nodes[edge.B].Position);
            Assert.True(MathF.Abs(measured - edge.Length) < 1e-3f,
                $"Edge claims {edge.Length}m but measures {measured}m.");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void CrossingRoadsShareAJunction(int projects, int typesPer, int depth)
    {
        var graph = Graph(projects, typesPer, depth);
        if (graph.Edges.Length > 4000) return;   // O(n^2); the smaller shapes prove the property

        for (int i = 0; i < graph.Edges.Length; i++)
        for (int j = i + 1; j < graph.Edges.Length; j++)
        {
            var (a, b) = (graph.Edges[i], graph.Edges[j]);
            if (a.A == b.A || a.A == b.B || a.B == b.A || a.B == b.B) continue;

            var a0 = graph.Nodes[a.A].Position;
            var a1 = graph.Nodes[a.B].Position;
            var b0 = graph.Nodes[b.A].Position;
            var b1 = graph.Nodes[b.B].Position;

            // Only same-level roads can genuinely cross; a deck flying over a street is fine.
            if (MathF.Abs(a0.Y - b0.Y) > 0.5f) continue;

            bool aVertical = MathF.Abs(a0.X - a1.X) < 1e-3f;
            bool bVertical = MathF.Abs(b0.X - b1.X) < 1e-3f;
            if (aVertical == bVertical) continue;

            var (v0, v1, h0, h1) = aVertical ? (a0, a1, b0, b1) : (b0, b1, a0, a1);
            const float Inside = 0.05f;
            bool crosses =
                v0.X > MathF.Min(h0.X, h1.X) + Inside && v0.X < MathF.Max(h0.X, h1.X) - Inside &&
                h0.Z > MathF.Min(v0.Z, v1.Z) + Inside && h0.Z < MathF.Max(v0.Z, v1.Z) - Inside;

            Assert.False(crosses,
                $"Roads cross at ({v0.X}, {h0.Z}) without a junction there.");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void MostOfTheNetworkIsOneConnectedPiece(int projects, int typesPer, int depth)
    {
        var graph = Graph(projects, typesPer, depth);
        if (graph.IsEmpty) return;

        float total = graph.Edges.Sum(e => e.Length);
        float main = graph.Edges
            .Where(e => graph.Nodes[e.A].Component == graph.MainComponent)
            .Sum(e => e.Length);

        Assert.True(main >= total * 0.95f,
            $"Only {main / total:P0} of the road network is reachable from the main component.");
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void NoJunctionSitsInsideABuilding(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));
        var graph = scene.RoadNetwork;

        // This is the direct guard against the twelve-metre overshoot the old centrelines used to
        // force connectivity, which is how a junction — and then a car — ended up inside a lot.
        foreach (var site in scene.Sites.Values)
        {
            float half = site.Side * 0.5f;
            foreach (var node in graph.Nodes)
            {
                if (node.IncidentCount == 0) continue;
                bool inside = MathF.Abs(node.Position.X - site.Center.X) < half * 0.75f
                           && MathF.Abs(node.Position.Z - site.Center.Z) < half * 0.75f;
                Assert.False(inside,
                    $"Junction at {node.Position} is inside the building at {site.Center} " +
                    $"(side {site.Side}).");
            }
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void StreetJunctionsSitOnTheRoadSurface(int projects, int typesPer, int depth)
    {
        var graph = Graph(projects, typesPer, depth);

        foreach (var node in graph.Nodes)
        {
            if (node.Kind is RoadNodeKind.RampTop or RoadNodeKind.DeckJoint) continue;
            Assert.True(MathF.Abs(node.Position.Y - CityLayout.StreetSurfaceY) < 1e-3f,
                $"Junction at {node.Position} is not at street level.");
        }
    }

    [Fact]
    public void SignalsNeverGiveTwoDirectionsGreenAtOnce()
    {
        var graph = Graph(6, 25, 3);
        Assert.NotEmpty(graph.Signals);

        // Green is a pure function of time, so the whole cycle can simply be enumerated.
        foreach (var signal in graph.Signals)
            for (float t = 0f; t < signal.Cycle; t += 0.1f)
                Assert.False(signal.IsGreen(t, true) && signal.IsGreen(t, false),
                    $"Signal at node {signal.NodeIndex} is green both ways at t={t}.");
    }

    [Fact]
    public void NearestEdgeFindsThePointItIsStandingOn()
    {
        var graph = Graph(6, 25, 3);

        for (int e = 0; e < graph.Edges.Length; e += 17)
        {
            var probe = graph.PointOn(e, graph.Edges[e].Length * 0.5f);
            Assert.True(graph.TryNearestEdge(probe, 40f, out int found, out float along));

            // Not necessarily the same edge — junctions have several equally close — but the point
            // it returns must be the point we asked about.
            Assert.True(Vector3.Distance(graph.PointOn(found, along), probe) < 0.6f,
                $"Nearest lookup at {probe} landed at {graph.PointOn(found, along)}.");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void RoadAndJunctionTarmacNeverOverlap(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));

        // What actually shows is a *marked* slab overlapping other tarmac: two sets of lane
        // markings at the same height contradict each other and flicker, which is why junction
        // patches used to be lifted half a metre — and why there was a step at every crossing.
        // Two blank patches overlapping are the same asphalt colour at the same height, so the
        // depth fight between them resolves to the same pixel either way; that is checked
        // separately, as a bound rather than a ban.
        var all = scene.Roads
            .Where(r => MathF.Abs(r.Center.Y - CityLayout.StreetSurfaceY) < 1e-3f
                     && MathF.Abs(r.Pitch) < 1e-3f)
            .ToList();
        var marked = all.Where(r => r.Flags != (uint)RoadFlags.None).Select(Extent).ToList();
        var everything = all.Select(Extent).ToList();

        var clash = OverlappingPairs(marked, everything, 0.05f).FirstOrDefault();
        Assert.True(clash == default,
            $"Marked road ({clash.A.MinX},{clash.A.MinZ})-({clash.A.MaxX},{clash.A.MaxZ}) " +
            $"overlaps tarmac ({clash.B.MinX},{clash.B.MinZ})-({clash.B.MaxX},{clash.B.MaxZ}).");
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void NearDuplicateJunctionsStayRare(int projects, int typesPer, int depth)
    {
        var graph = Graph(projects, typesPer, depth);
        if (graph.Edges.Length < 20) return;

        // Two junctions a couple of metres apart are one junction the arrangement failed to
        // recognise: a stub of road no car can sit on, and a vehicle that comes to a halt twice in
        // the same place. The treemap does legitimately divide at nearly the same coordinate at two
        // levels of nesting, and two side roads really can meet a boulevard a few metres apart, so
        // a tail of short segments is expected. The measure is scale-free on purpose: a stub is a
        // segment shorter than the junctions at its ends are wide, which is what "this road exists
        // entirely inside a crossing" means. A fixed metre count would call every alley in a small
        // city a stub and let a real regression through in a large one.
        int stubs = graph.Edges.Count(e =>
            e.Length < 0.5f * MathF.Max(graph.MaxIncidentWidth(e.A), graph.MaxIncidentWidth(e.B)));

        Assert.True(stubs <= graph.Edges.Length / 20,
            $"{stubs} of {graph.Edges.Length} road segments are under 4m long.");
    }

    /// <summary>The footprint of a road quad on the ground.</summary>
    internal readonly record struct Box(float MinX, float MinZ, float MaxX, float MaxZ);

    static Box Extent(RoadQuad quad)
    {
        bool alongX = MathF.Abs(MathF.Cos(quad.Yaw)) > 0.5f;
        float halfX = (alongX ? quad.Length : quad.Width) * 0.5f;
        float halfZ = (alongX ? quad.Width : quad.Length) * 0.5f;
        return new Box(quad.Center.X - halfX, quad.Center.Z - halfZ,
                       quad.Center.X + halfX, quad.Center.Z + halfZ);
    }

    /// <summary>
    /// Every overlapping pair, found by bucketing rather than by comparing everything with
    /// everything — at forty districts that is eight thousand quads and sixty million comparisons.
    /// </summary>
    static IEnumerable<(Box A, Box B)> OverlappingPairs(
        IReadOnlyList<Box> left, IReadOnlyList<Box> right, float slack)
    {
        const float Cell = 40f;
        var grid = new Dictionary<(int, int), List<Box>>();
        foreach (var box in right)
            foreach (var cell in Cells(box, Cell))
            {
                if (!grid.TryGetValue(cell, out var bucket)) grid[cell] = bucket = new List<Box>();
                bucket.Add(box);
            }

        var seen = new HashSet<(Box, Box)>();
        foreach (var a in left)
            foreach (var cell in Cells(a, Cell))
            {
                if (!grid.TryGetValue(cell, out var bucket)) continue;
                foreach (var b in bucket)
                {
                    if (a.Equals(b) || !seen.Add((a, b))) continue;
                    bool overlaps = a.MinX < b.MaxX - slack && b.MinX < a.MaxX - slack
                                 && a.MinZ < b.MaxZ - slack && b.MinZ < a.MaxZ - slack;
                    if (overlaps) yield return (a, b);
                }
            }
    }

    static IEnumerable<(int, int)> Cells(Box box, float size)
    {
        for (int z = (int)MathF.Floor(box.MinZ / size); z <= (int)MathF.Floor(box.MaxZ / size); z++)
        for (int x = (int)MathF.Floor(box.MinX / size); x <= (int)MathF.Floor(box.MaxX / size); x++)
            yield return (x, z);
    }

    [Fact]
    public void EveryHighwayDeckIsReachableFromTheStreets()
    {
        var graph = Graph(40, 30, 3);
        var decks = Enumerable.Range(0, graph.Nodes.Length)
            .Where(n => graph.Nodes[n].Kind == RoadNodeKind.DeckJoint)
            .ToList();
        Assert.NotEmpty(decks);

        // The decks used to be drawn and then left topologically stranded — a road surface with
        // parapets and pillars that nothing could ever drive onto, because the ramps joined it to
        // nothing at all.
        var street = Enumerable.Range(0, graph.Nodes.Length).First(n =>
            graph.Nodes[n].IncidentCount > 2
            && graph.Nodes[n].Kind == RoadNodeKind.Junction
            && graph.Nodes[n].Component == graph.MainComponent);

        foreach (int deck in decks)
            Assert.True(graph.Nodes[deck].Component == graph.Nodes[street].Component,
                $"Deck joint at {graph.Nodes[deck].Position} is on its own island.");
    }

    [Fact]
    public void RampsTouchDownClearOfAJunction()
    {
        var graph = Graph(40, 30, 3);
        var feet = Enumerable.Range(0, graph.Nodes.Length)
            .Where(n => graph.Nodes[n].Kind == RoadNodeKind.RampFoot)
            .ToList();
        Assert.NotEmpty(feet);

        foreach (int foot in feet)
        {
            // Landing on a crossroads is the specific defect here: the ramps used to be built at
            // the ends of a treemap cut, and for every division below the first that end *is* the
            // centreline of the boulevard the parent cut laid.
            foreach (int edge in graph.IncidentEdges(foot))
            {
                if (graph.Edges[edge].Kind == RoadKind.HighwayRamp) continue;
                int neighbour = graph.Other(edge, foot);
                float clearance = 0.5f * graph.MaxIncidentWidth(neighbour);
                Assert.True(graph.Edges[edge].Length > clearance,
                    $"Ramp foot at {graph.Nodes[foot].Position} is inside the junction at " +
                    $"{graph.Nodes[neighbour].Position}.");
            }

            // And it has to join the street at a T, or nothing can turn onto it.
            int streetLinks = graph.IncidentEdges(foot)
                .ToArray()
                .Count(e => graph.Edges[e].Kind != RoadKind.HighwayRamp);
            Assert.True(streetLinks >= 2,
                $"Ramp foot at {graph.Nodes[foot].Position} joins the street on one side only.");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void NoRoundaboutIsPaintedOverARooftop(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Connect(Fixture.Solution(projects, typesPer, depth)));

        // A roundabout is centred on the average position of the cycle's members, and when those
        // are spread out that average lands inside a block. The ring was painted over the buildings
        // there and its traffic circled a metre above the roofs — visible from the air as cars
        // flying over the city, and impossible to find on foot.
        var rings = scene.Roads
            .Where(r => (r.Flags & (uint)RoadFlags.Hazard) != 0)
            .ToList();

        foreach (var segment in rings)
            foreach (var site in scene.Sites.Values)
            {
                float half = site.Side * 0.5f;
                bool inside = MathF.Abs(segment.Center.X - site.Center.X) < half
                           && MathF.Abs(segment.Center.Z - site.Center.Z) < half;
                Assert.False(inside,
                    $"A roundabout segment at {segment.Center} sits on the building at " +
                    $"{site.Center}.");
            }
    }

    [Fact]
    public void BuildIsDeterministic()
    {
        var first = Graph(6, 25, 3);
        var second = Graph(6, 25, 3);

        Assert.Equal(first.Nodes.Length, second.Nodes.Length);
        Assert.Equal(first.Edges.Length, second.Edges.Length);
        Assert.Equal(first.MainComponent, second.MainComponent);

        for (int i = 0; i < first.Nodes.Length; i++)
            Assert.Equal(first.Nodes[i].Position, second.Nodes[i].Position);
        for (int i = 0; i < first.Edges.Length; i++)
        {
            Assert.Equal(first.Edges[i].A, second.Edges[i].A);
            Assert.Equal(first.Edges[i].B, second.Edges[i].B);
        }
    }

    [Fact]
    public void BuildIsFastEnoughAtScale()
    {
        // A large real-world solution's shape. Arranging the roads must not become the slow part
        // of startup.
        var watch = Stopwatch.StartNew();
        var graph = Graph(40, 30, 3);
        watch.Stop();

        Assert.NotEmpty(graph.Edges);
        Assert.True(watch.ElapsedMilliseconds < 4000,
            $"Building a 40-district city took {watch.ElapsedMilliseconds}ms.");
    }
}
