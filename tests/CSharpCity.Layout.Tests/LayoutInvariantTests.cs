using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The invariants the layout must never break, whatever shape of solution it is handed.
/// </summary>
/// <remarks>
/// Every test here corresponds to a bug that actually shipped and was found by eye rather than by
/// code — non-unique ids, namespace subtrees silently overwritten, districts inset out of existence,
/// margins compounding until lots collapsed. They all had the same signature: buildings quietly
/// absent, with the run reporting success. That's precisely the failure a test catches cheaply and
/// a person catches only by chance.
/// </remarks>
public class LayoutInvariantTests
{
    public static TheoryData<int, int, int> Shapes => new()
    {
        // projects, types per project, namespace depth
        { 1, 1, 1 },      // the degenerate single-type solution
        { 1, 40, 1 },     // one flat project
        { 3, 25, 4 },     // deep nesting, where margins used to compound away the lots
        { 40, 30, 3 },    // a large solution's shape: many districts competing for one square
        { 2, 1, 5 },      // tiny districts, which used to be inset out of existence
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryTypeGetsABuilding(int projects, int typesPer, int depth)
    {
        var model = Fixture.Solution(projects, typesPer, depth);

        var scene = CityLayout.Build(model);

        var missing = Fixture.AllTypeIds(model).Where(id => !scene.Sites.ContainsKey(id)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} type(s) were never placed, first few: " +
            string.Join(", ", missing.Take(3)));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryBuildingIsInspectable(int projects, int typesPer, int depth)
    {
        var model = Fixture.Solution(projects, typesPer, depth);

        var scene = CityLayout.Build(model);

        // PickInfo used to be registered before the size check, so a dropped building still
        // consumed a slot and corrupted the very count that reported the problem.
        Assert.Equal(Fixture.AllTypeIds(model).Count(), scene.PickInfos.Count);
    }

    [Fact]
    public void LayoutIsDeterministic()
    {
        var first = CityLayout.Build(Fixture.Solution(6, 20, 3));
        var second = CityLayout.Build(Fixture.Solution(6, 20, 3));

        Assert.Equal(first.Boxes.Count, second.Boxes.Count);
        Assert.Equal(first.Roads.Count, second.Roads.Count);
        Assert.Equal(first.Labels.Count, second.Labels.Count);
        Assert.Equal(first.Travellers.Count, second.Travellers.Count);

        // Positions, not just counts: a stable count with drifting placement would still mean the
        // mental map you build of a city is worthless between runs.
        foreach (var (id, site) in first.Sites)
        {
            Assert.True(second.Sites.TryGetValue(id, out var other), $"{id} missing on second run");
            Assert.Equal(site.Center.X, other!.Center.X, 4);
            Assert.Equal(site.Center.Z, other.Center.Z, 4);
        }
    }

    [Fact]
    public void BuildingsDoNotOverlapEachOther()
    {
        var scene = CityLayout.Build(Fixture.Solution(4, 30, 2));
        var sites = scene.Sites.Values.ToList();

        for (int i = 0; i < sites.Count; i++)
        for (int j = i + 1; j < sites.Count; j++)
        {
            float gapX = MathF.Abs(sites[i].Center.X - sites[j].Center.X);
            float gapZ = MathF.Abs(sites[i].Center.Z - sites[j].Center.Z);
            float needX = (sites[i].Side + sites[j].Side) * 0.5f;

            // Overlapping on one axis is fine; overlapping on both means two buildings share ground.
            Assert.True(gapX >= needX - 0.01f || gapZ >= needX - 0.01f,
                $"buildings at {sites[i].Center} and {sites[j].Center} overlap");
        }
    }

    [Fact]
    public void EmptySolutionProducesEmptyCity()
    {
        var scene = CityLayout.Build(new CityModel());

        Assert.Empty(scene.Sites);
        Assert.Empty(scene.PickInfos);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 14)]
    [InlineData(9, 30)]
    public void MoreMethodsMakesATallerBuilding(int fewMethods, int manyMethods)
    {
        float shortHeight = TallestBoxFor(Fixture.Type("P", "P.N", "T", methods: fewMethods));
        float tallHeight = TallestBoxFor(Fixture.Type("P", "P.N", "T", methods: manyMethods));

        Assert.True(tallHeight > shortHeight,
            $"{manyMethods} methods gave {tallHeight:F1}m, {fewMethods} gave {shortHeight:F1}m");
    }

    [Fact]
    public void MoreStateMakesAWiderFootprint()
    {
        var narrow = CityLayout.Build(Single(Fixture.Type("P", "P.N", "T", fields: 1, properties: 0)));
        var wide = CityLayout.Build(Single(Fixture.Type("P", "P.N", "T", fields: 30, properties: 20)));

        Assert.True(wide.Sites.Values.Single().Side > narrow.Sites.Values.Single().Side);
    }

    static float TallestBoxFor(TypeNode type)
    {
        var scene = CityLayout.Build(Single(type));
        // Ignore ground plates and scenery; the building is what stands up off the lot.
        return scene.Boxes.Where(b => b.PickId >= 0).Max(b => b.BasePosition.Y + b.Size.Y);
    }

    static CityModel Single(TypeNode type)
    {
        var model = new CityModel();
        var project = new ProjectNode { Name = "P" };
        project.Types.Add(type);
        model.Projects.Add(project);
        return model;
    }
}
