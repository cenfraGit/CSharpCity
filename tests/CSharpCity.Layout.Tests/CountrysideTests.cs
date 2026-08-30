using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// Laying each project out as its own town, with open country between them.
/// </summary>
public class CountrysideTests
{
    static CityModel Model(int projects = 5) =>
        Fixture.Connect(Fixture.Solution(projects, 12, 2));

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void EveryTypeStillGetsABuilding(int projects, int typesPer, int depth)
    {
        // The failure this guards is the quiet one: a town squeezed to nothing by the countryside
        // inset would drop a whole project out of the world without saying so.
        var model = Fixture.Connect(Fixture.Solution(projects, typesPer, depth));

        var scene = CityLayout.Build(model, separateCities: true);

        Assert.Equal(model.Projects.Sum(p => p.Types.Count), scene.PickInfos.Count);
    }

    [Fact]
    public void TownsDoNotTouch()
    {
        var scene = CityLayout.Build(Model(), separateCities: true);
        var towns = scene.Districts.Values.ToList();

        Assert.True(towns.Count >= 2);

        // Every pair must be separated on at least one axis, or they are not separate towns —
        // they are one city with a gap drawn down the middle of it.
        for (int a = 0; a < towns.Count; a++)
        for (int b = a + 1; b < towns.Count; b++)
        {
            bool apart =
                towns[a].X + towns[a].Width < towns[b].X
                || towns[b].X + towns[b].Width < towns[a].X
                || towns[a].Z + towns[a].Depth < towns[b].Z
                || towns[b].Z + towns[b].Depth < towns[a].Z;

            Assert.True(apart, $"towns {a} and {b} overlap or abut");
        }
    }

    [Fact]
    public void TheDefaultLayoutIsUntouched()
    {
        // Separate towns are opt-in precisely so the packed layout stays the known-good one. If
        // this ever diverges, the flag has leaked into the default path.
        var packed = CityLayout.Build(Model());
        var also = CityLayout.Build(Model());

        Assert.Equal(BaselineTests.Signature(packed), BaselineTests.Signature(also));
        Assert.Equal(1, packed.Districts.Count > 1 ? 1 : 0);
    }

    [Fact]
    public void TheWorldGrowsByAConstantFactorRatherThanWithTheProjectCount()
    {
        // The scaling property the whole approach depends on. Towns are packed by the same treemap
        // rather than scattered, so the world is a constant multiple of the packed city however
        // many projects there are — which is what keeps the heightfield and the walkable-ground
        // grid affordable. Scattering towns to arbitrary distances is what would have been costly.
        float Ratio(int projects)
        {
            var model = Model(projects);
            return CityLayout.Build(model, separateCities: true).CityBounds.Width
                   / CityLayout.Build(model).CityBounds.Width;
        }

        float few = Ratio(3);
        float many = Ratio(24);

        Assert.InRange(few, 1.2f, 2.0f);
        Assert.InRange(many, 1.2f, 2.0f);
        Assert.True(MathF.Abs(many - few) < 0.05f,
            $"the spread factor drifted with project count: {few:F2} against {many:F2}");
    }

    [Fact]
    public void CountryGroundRisesBetweenTownsAndStaysFlatInsideThem()
    {
        var scene = CityLayout.Build(Model(), separateCities: true);
        Assert.NotNull(scene.Terrain);

        var towns = scene.Districts.Values.ToList();
        var vertices = scene.Terrain!.Vertices;
        int inTown = 0, raised = 0;

        for (int i = 0; i < vertices.Length; i += 6)
        {
            float x = vertices[i], y = vertices[i + 1], z = vertices[i + 2];
            bool inside = towns.Any(t => x >= t.X && x <= t.X + t.Width
                                      && z >= t.Z && z <= t.Z + t.Depth);

            if (inside)
            {
                inTown++;
                // Flat inside a town is load-bearing: the whole surface-height stack, the footpaths
                // and the walkable-ground grid assume the city floor is exactly level.
                Assert.Equal(-Terrain.Sink, y, 3);
            }
            else if (y > -Terrain.Sink + 1f) raised++;
        }

        Assert.True(inTown > 0, "no terrain vertex landed inside a town");
        Assert.True(raised > 0, "the ground never rises anywhere outside the towns");
    }

    [Fact]
    public void TheSeaSurroundsTheWholeMapAndOnlyInTownMode()
    {
        // One plane over everything rather than water fitted to a shape: the coast is carved into
        // the ground, so where the land is above the waterline it stands out of the sea and where it
        // is below, the sea covers it. Nothing has to agree about where the shore is.
        var towns = CityLayout.Build(Model(), separateCities: true);
        var sea = Assert.Single(towns.Roads, r => (r.Flags & (uint)RoadFlags.Sea) != 0);

        Assert.True(sea.Length >= towns.CityBounds.Width,
            "the sea has to reach past the far side of the world, or it ends in mid-water");
        Assert.True(sea.Color.W < 1f, "the seabed must show through, or there are no shallows");

        Assert.DoesNotContain(CityLayout.Build(Model()).Roads,
            r => (r.Flags & (uint)RoadFlags.Sea) != 0);
    }

    [Fact]
    public void TheLandRunsOutBeforeTheSeaDoes()
    {
        // The map has to end somewhere, and the ring of mountains it used to end with was a wall
        // around a place for no reason anyone could see. A coastline explains itself.
        var scene = CityLayout.Build(Model(), separateCities: true);
        var world = scene.CityBounds;
        var vertices = scene.Terrain!.Vertices;

        int drowned = 0, dry = 0;

        for (int i = 0; i < vertices.Length; i += 6)
        {
            float x = vertices[i], y = vertices[i + 1], z = vertices[i + 2];

            bool wellOutside = x < world.X - 350f || x > world.X + world.Width + 350f
                            || z < world.Z - 350f || z > world.Z + world.Depth + 350f;

            if (wellOutside && y < Terrain.SeaLevel) drowned++;
            if (!wellOutside && y > Terrain.SeaLevel) dry++;
        }

        Assert.True(drowned > 0, "the ground never drops below the waterline out past the map");
        Assert.True(dry > 0, "there is no dry land inside the map at all");
    }

    [Fact]
    public void TheCountryBetweenTownsIsWalkableLandWithWoodsOnIt()
    {
        // Separate projects are parts of one solution, not separate worlds. An earlier version made
        // each town an island and said the wrong thing entirely; the ground between them is country
        // you could walk across.
        var scene = CityLayout.Build(Model(6), separateCities: true);
        var towns = scene.Districts.Values.ToList();

        // Trees stand on the terrain, so anything planted between the towns is on dry land there.
        int betweenTowns = scene.Boxes.Count(b =>
            b.PickId == -1 && b.BasePosition.Y > Terrain.SeaLevel
            && b.BasePosition.X > scene.CityBounds.X
            && b.BasePosition.X < scene.CityBounds.X + scene.CityBounds.Width
            && b.BasePosition.Z > scene.CityBounds.Z
            && b.BasePosition.Z < scene.CityBounds.Z + scene.CityBounds.Depth
            && !towns.Any(t => b.BasePosition.X >= t.X && b.BasePosition.X <= t.X + t.Width
                            && b.BasePosition.Z >= t.Z && b.BasePosition.Z <= t.Z + t.Depth));

        Assert.True(betweenTowns > 0, "nothing at all stands in the country between the towns");
    }

    [Fact]
    public void NoRoadLeavesItsOwnTown()
    {
        // Deliberate, and the reason the road network is in pieces here. Roads are a town's internal
        // business; what connects two projects is a project reference, and that is what the rail
        // shows. An inter-town road would be a second channel saying the same thing less well.
        var scene = CityLayout.Build(Model(5), separateCities: true);
        var towns = scene.Districts.Values.ToList();

        foreach (var node in scene.RoadNetwork.Nodes)
        {
            if (node.IncidentCount == 0) continue;

            bool inSomeTown = towns.Any(t =>
                node.Position.X >= t.X - 40f && node.Position.X <= t.X + t.Width + 40f
                && node.Position.Z >= t.Z - 40f && node.Position.Z <= t.Z + t.Depth + 40f);

            Assert.True(inSomeTown,
                $"a junction at {node.Position} is out in open country, so a road has left its town");
        }
    }
}
