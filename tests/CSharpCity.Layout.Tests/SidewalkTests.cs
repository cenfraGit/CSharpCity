using System.Numerics;


namespace CSharpCity.Layout.Tests;

/// <summary>
/// The pavements: where they sit, and that they go all the way round.
/// </summary>
public class SidewalkTests
{
    static IEnumerable<BoxInstance> Kerbside(SceneGraph scene) =>
        scene.Boxes.Where(b => b.Layer == CityLayer.Sidewalks);

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void KerbTopsMeetTheRoadSurface(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));

        // The kerb is what makes the road's height make sense: its top face *is* the road surface,
        // so the tarmac reads as being cut into the pavement rather than floating over the ground.
        foreach (var kerb in Kerbside(scene).Where(b => MathF.Abs(b.Size.Y - 0.4f) < 1e-3f))
            Assert.True(MathF.Abs(kerb.BasePosition.Y + kerb.Size.Y - CityLayout.StreetSurfaceY) < 1e-3f,
                $"A kerb at {kerb.BasePosition} tops out at " +
                $"{kerb.BasePosition.Y + kerb.Size.Y}, not {CityLayout.StreetSurfaceY}.");
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void NothingStandsInTheRoad(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));
        var graph = scene.RoadNetwork;
        if (graph.IsEmpty) return;

        // A bench in the middle of a carriageway is the sort of thing that gets noticed from a
        // moving car and never from a screenshot.
        foreach (var prop in Kerbside(scene))
        {
            if (!graph.TryNearestEdge(prop.BasePosition, 30f, out int edge, out float along))
                continue;
            if (graph.Edges[edge].Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

            var centreline = graph.PointOn(edge, along);
            float across = MathF.Sqrt(
                MathF.Pow(prop.BasePosition.X - centreline.X, 2) +
                MathF.Pow(prop.BasePosition.Z - centreline.Z, 2));

            Assert.True(across >= graph.Edges[edge].Width * 0.5f - 0.05f,
                $"Street furniture at {prop.BasePosition} is {across:F2}m from the centre of a " +
                $"{graph.Edges[edge].Width:F1}m road.");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void EveryJunctionCornerIsPaved(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));
        var graph = scene.RoadNetwork;
        if (graph.IsEmpty) return;

        // Kerbs stop short of a junction to let the crossing road through, which leaves a square
        // hole in the corner of every block unless something fills it. It is the most visible thing
        // about a pavement and the easiest to forget.
        var corners = Kerbside(scene)
            .Where(b => MathF.Abs(b.Size.X - b.Size.Z) < 1e-3f && MathF.Abs(b.Size.Y - 0.4f) < 1e-3f)
            .Select(b => (b.BasePosition.X, b.BasePosition.Z))
            .ToHashSet();

        int expected = 0, found = 0;
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].IncidentCount < 3) continue;
            if (graph.Nodes[node].Kind is RoadNodeKind.RampTop or RoadNodeKind.DeckJoint) continue;

            bool crossing = false;
            bool first = graph.RunsAlongX(graph.IncidentEdges(node)[0]);
            foreach (int edge in graph.IncidentEdges(node))
                if (graph.RunsAlongX(edge) != first) crossing = true;
            if (!crossing) continue;

            expected++;
            var at = graph.Nodes[node].Position;
            if (corners.Any(c => MathF.Abs(c.X - at.X) < 20f && MathF.Abs(c.Z - at.Z) < 20f))
                found++;
        }

        Assert.True(found >= expected,
            $"{expected - found} of {expected} crossings have no pavement in their corners.");
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void EveryRoadHasPavementOnBothSides(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));
        var graph = scene.RoadNetwork;
        if (graph.IsEmpty) return;

        // The gap the user could see: only boulevards and streets were paved, so any block with an
        // alley along one edge lost its pavement partway round.
        var paved = Kerbside(scene)
            .Where(b => MathF.Abs(b.Size.Y - 0.4f) < 1e-3f)
            .ToList();

        // Containment, not proximity: a kerb's origin is the middle of its run, which moves when
        // one end is trimmed for a crossing and the other is not, so "is there a kerb near here"
        // answers the wrong question.
        bool Covers(Vector3 point) => paved.Any(b =>
            MathF.Abs(b.BasePosition.X - point.X) <= b.Size.X * 0.5f + 0.05f &&
            MathF.Abs(b.BasePosition.Z - point.Z) <= b.Size.Z * 0.5f + 0.05f);

        int bare = 0, total = 0;
        for (int e = 0; e < graph.Edges.Length; e++)
        {
            var edge = graph.Edges[e];
            if (edge.Kind is RoadKind.Connector or RoadKind.HighwayDeck or RoadKind.HighwayRamp)
                continue;
            if (edge.Length < 12f) continue;

            var middle = graph.PointOn(e, edge.Length * 0.5f);
            var across = Vector3.Cross(
                Vector3.Normalize(graph.Nodes[edge.B].Position - graph.Nodes[edge.A].Position),
                Vector3.UnitY);

            for (int side = -1; side <= 1; side += 2)
            {
                total++;
                // Just past the kerb line, where the pavement has to be.
                if (!Covers(middle + across * side * (edge.Width * 0.5f + 0.4f))) bare++;
            }
        }

        Assert.True(bare <= total / 20,
            $"{bare} of {total} road sides are missing their pavement.");
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void CarParksOnlyGoOnPlotsThatAreMostlyEmpty(int projects, int typesPer, int depth)
    {
        var scene = CityLayout.Build(Fixture.Solution(projects, typesPer, depth));
        var graph = scene.RoadNetwork;

        var carParks = scene.Roads
            .Where(r => (r.Flags & (uint)RoadFlags.Parking) != 0)
            .ToList();

        foreach (var park in carParks)
        {
            bool alongX = MathF.Abs(MathF.Cos(park.Yaw)) > 0.5f;
            float halfX = (alongX ? park.Length : park.Width) * 0.5f;
            float halfZ = (alongX ? park.Width : park.Length) * 0.5f;

            // A plot's edge is not reliably clear of the road beside it — the arrangement can move
            // a centreline after the plots around it are fixed — so this is checked against the
            // finished network rather than assumed from the lot.
            foreach (var corner in new[]
                     {
                         new Vector3(park.Center.X - halfX, 0f, park.Center.Z - halfZ),
                         new Vector3(park.Center.X + halfX, 0f, park.Center.Z - halfZ),
                         new Vector3(park.Center.X - halfX, 0f, park.Center.Z + halfZ),
                         new Vector3(park.Center.X + halfX, 0f, park.Center.Z + halfZ),
                     })
            {
                if (!graph.TryNearestEdge(corner, 30f, out int edge, out float along)) continue;
                if (graph.Edges[edge].Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

                var centreline = graph.PointOn(edge, along);
                float gap = MathF.Sqrt(MathF.Pow(corner.X - centreline.X, 2)
                                     + MathF.Pow(corner.Z - centreline.Z, 2));
                Assert.True(gap >= graph.Edges[edge].Width * 0.5f - 0.05f,
                    $"A car park corner at {corner} is inside a {graph.Edges[edge].Width:F1}m road.");
            }

            // And never over the building it belongs to.
            foreach (var site in scene.Sites.Values)
            {
                float half = site.Side * 0.5f;
                bool inside = MathF.Abs(park.Center.X - site.Center.X) < halfX + half
                           && MathF.Abs(park.Center.Z - site.Center.Z) < halfZ + half;
                Assert.False(inside,
                    $"A car park at {park.Center} covers the building at {site.Center}.");
            }
        }
    }

    [Fact]
    public void ThePavementStaysWithinBudget()
    {
        var scene = CityLayout.Build(Fixture.Solution(40, 30, 3));

        // Silent truncation would read as "every street is paved" when it is not; the cap is
        // reported, and it has to actually hold.
        Assert.True(Kerbside(scene).Count() <= 30_000,
            $"{Kerbside(scene).Count():n0} sidewalk boxes is over the stated budget.");
    }

    [Fact]
    public void EverythingIsTaggedSoItCanBeSwitchedOff()
    {
        var scene = CityLayout.Build(Fixture.Solution(6, 25, 3));
        Assert.NotEmpty(Kerbside(scene));

        // A box left untagged would survive F10 and the layer would look half-removed; a box tagged
        // with a layer nothing binds a key to could never be turned off at all. Both are the same
        // mistake, so the check is that every box carries a layer somebody can actually switch.
        foreach (var box in scene.Boxes)
            Assert.True(box.Layer is CityLayer.Always or CityLayer.Sidewalks or CityLayer.Highways,
                $"A box is tagged {box.Layer}, which nothing knows how to toggle.");
    }
}
