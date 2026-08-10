using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The two channels the repository's history drives: construction, and the people on site.
/// </summary>
public class HistoryTests
{
    /// <summary>
    /// A solution where exactly one type carries whatever history the test needs.
    /// </summary>
    /// <remarks>
    /// Exactly one matters: every project in the fixture names its types the same way, so matching
    /// on a name alone would quietly apply the history three times over and make every count a
    /// multiple of the project count.
    /// </remarks>
    static SceneGraph City(string subject, Action<TypeNode> history)
    {
        var model = Fixture.Solution(3, 12, 2);
        var target = model.Projects.SelectMany(p => p.Types).First(t => t.Name == subject);
        history(target);
        return CityLayout.Build(model);
    }

    /// <summary>Crane masts are the one tall yellow thing in the city.</summary>
    static IEnumerable<BoxInstance> Cranes(SceneGraph scene) =>
        scene.Boxes.Where(b => b.Size.Y > 6f
                            && MathF.Abs(b.Color.X - 0.86f) < 0.01f
                            && MathF.Abs(b.Color.Y - 0.62f) < 0.01f);

    /// <summary>Cones are the only tapered thing in the city.</summary>
    static IEnumerable<BoxInstance> Cones(SceneGraph scene) =>
        scene.Boxes.Where(b => (b.Flags & (uint)BoxFlags.Cone) != 0);

    [Fact]
    public void AQuietCodebaseHasNothingHappeningOnIt()
    {
        // Fixture types carry no history at all, which is exactly the "no git" case.
        var scene = CityLayout.Build(Fixture.Solution(3, 12, 2));

        Assert.Empty(Cones(scene));
        Assert.Empty(Cranes(scene));
    }

    [Fact]
    public void ATodoRaisesACraneAndNoCones()
    {
        // The two channels were briefly the other way round and competed. A crane is a site that
        // has stood for months, which is what an unfinished TODO is; cones are this week's work.
        var scene = City("T3", t => t.Smells.Add(new Smell { Kind = SmellKind.TodoComment, Count = 9 }));

        Assert.NotEmpty(Cranes(scene));
        Assert.Empty(Cones(scene));
    }

    [Fact]
    public void CommittedToThisWeekPutsOutConesAndNoCrane()
    {
        var scene = City("T3", t => t.Commits = 5);

        Assert.NotEmpty(Cones(scene));
        Assert.Empty(Cranes(scene));
    }

    [Fact]
    public void MoreTodosMakeATallerCrane()
    {
        float Mast(int todos) => Cranes(City("T3",
            t => t.Smells.Add(new Smell { Kind = SmellKind.TodoComment, Count = todos })))
            .Max(b => b.Size.Y);

        Assert.True(Mast(9) > Mast(1),
            "nine outstanding TODOs should show a taller crane than one");
    }

    [Fact]
    public void ACraneAlwaysClearsItsOwnBuilding()
    {
        // Sizing the mast from a guess at the roof rather than from the finished floor stack is
        // what put jibs through the upper storeys of anything taller than the guess.
        var model = Fixture.Solution(2, 14, 2);
        foreach (var type in model.Projects.SelectMany(p => p.Types))
            type.Smells.Add(new Smell { Kind = SmellKind.TodoComment, Count = 3 });

        var scene = CityLayout.Build(model);
        Assert.NotEmpty(Cranes(scene));

        foreach (var mast in Cranes(scene))
        {
            // The tallest thing standing on the nearest building to this crane.
            float roof = scene.Boxes
                .Where(b => b.PickId >= 0
                         && MathF.Abs(b.BasePosition.X - mast.BasePosition.X) < 30f
                         && MathF.Abs(b.BasePosition.Z - mast.BasePosition.Z) < 30f
                         && (b.Flags & (uint)BoxFlags.Windows) != 0)
                .Select(b => b.BasePosition.Y + b.Size.Y)
                .DefaultIfEmpty(0f)
                .Max();

            Assert.True(mast.BasePosition.Y + mast.Size.Y > roof,
                $"a crane tops out at {mast.BasePosition.Y + mast.Size.Y:F1}m beside a " +
                $"{roof:F1}m building, so its jib swings through it");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void NothingTheHistoryLayerPutsDownEndsUpInTheRoad(int projects, int typesPer, int depth)
    {
        var model = Fixture.Solution(projects, typesPer, depth);
        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            type.Commits = 6;
            type.LinesChanged = 2000;
            type.Authors = 4;
        }

        var scene = CityLayout.Build(model);
        var graph = scene.RoadNetwork;
        if (graph.IsEmpty) return;

        // Skips once walked a fixed distance out from the building on a random bearing, checking
        // nothing at all, and stood in the carriageway. They are gone; this guards whatever else
        // the layer puts on the ground from repeating it.
        //
        // Only the substantial props are held to this. A traffic cone that strays into an alley is
        // where a traffic cone belongs, and the lot boundary cannot be made exact anyway: the road
        // arrangement merges and snaps centrelines *after* the plots either side of them are fixed,
        // so a road can move a metre once the lot it borders is already decided.
        foreach (var prop in scene.Boxes.Where(b => b.PickId >= 0 && b.BasePosition.Y < 2f
                                                 && MathF.Max(b.Size.X, b.Size.Z) >= 1f))
        {
            if (!graph.TryNearestEdge(prop.BasePosition, 25f, out int edge, out float along))
                continue;
            if (graph.Edges[edge].Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

            var centreline = graph.PointOn(edge, along);
            float across = MathF.Sqrt(MathF.Pow(prop.BasePosition.X - centreline.X, 2)
                                    + MathF.Pow(prop.BasePosition.Z - centreline.Z, 2));

            // Measured to the prop's near edge, not its centre: half a skip in the road is still
            // a skip in the road.
            float reach = MathF.Max(prop.Size.X, prop.Size.Z) * 0.5f;
            Assert.True(across + reach >= graph.Edges[edge].Width * 0.5f - 0.05f,
                $"a {prop.Size.X:F1}x{prop.Size.Z:F1}m prop at {prop.BasePosition} is " +
                $"{across:F2}m from the centre of a {graph.Edges[edge].Width:F1}m road");
        }
    }

    [Fact]
    public void MoreCommitsPutOutMoreCones()
    {
        int Count(int commits) => Cones(City("T3", t => t.Commits = commits)).Count();

        Assert.True(Count(6) > Count(1), "six commits should show more cones than one");
        // Capped, or a file touched thirty times this week buries its own lot.
        Assert.Equal(Count(8), Count(40));
    }

    [Fact]
    public void OneBicycleForEachAuthor()
    {
        // Bicycles are the only thing on the frontage, so counting the small dark wheels is enough
        // to tell how many were racked.
        int Wheels(int authors) => City("T3", t => t.Authors = authors)
            .Boxes.Count(b => MathF.Abs(b.Size.Y - 0.74f) < 0.001f
                           && MathF.Abs(b.Color.X - 0.10f) < 0.01f);

        int one = Wheels(1);
        int six = Wheels(6);

        Assert.True(six > one, $"six authors gave {six} wheels against {one} for a single author");
        // Capped, or a heavily-shared file grows a bicycle car park.
        Assert.Equal(Wheels(10), Wheels(40));
    }

    [Fact]
    public void SoleOwnershipIsRaisedOnlyWhenTheOwnerIsStillWorking()
    {
        static int Stops(SceneGraph scene) =>
            scene.Interest.Count(i => i.Headline == "SOLE OWNERSHIP");

        // Half the types in a real codebase have one author simply because nobody has needed to
        // touch them; that is not a finding.
        Assert.Equal(0, Stops(City("T3", t => { t.Authors = 1; t.Commits = 0; })));
        // Nor is a busy file that several people share.
        Assert.Equal(0, Stops(City("T3", t => { t.Authors = 5; t.Commits = 20; })));
        // Both together is the bus factor.
        Assert.Equal(1, Stops(City("T3", t => { t.Authors = 1; t.Commits = 20; })));
    }

    [Fact]
    public void TheTourDoesNotFillUpWithOwnershipStops()
    {
        // Measured on a real repository: dozens of files are sole-authored and still changing. Flying
        // to all of them would nearly double a 42-stop tour and drown out the fires and crime
        // scenes, so the busiest are visited and the rest are reported as a count.
        var model = Fixture.Solution(4, 25, 2);
        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            type.Authors = 1;
            type.Commits = 5;
        }

        var scene = CityLayout.Build(model);

        Assert.InRange(scene.Interest.Count(i => i.Headline == "SOLE OWNERSHIP"), 1, 8);
    }
}
