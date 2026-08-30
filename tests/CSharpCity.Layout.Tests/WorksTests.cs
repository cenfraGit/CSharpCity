using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The overlay: building sites for open pull requests, and the queue on the issue backlog.
/// </summary>
public class WorksTests
{
    static CityModel Model() => Fixture.Connect(Fixture.Solution(3, 14, 2));

    /// <summary>A snapshot whose pull request touches the first type in the model.</summary>
    static (CityModel Model, GitHubSnapshot Snapshot, string TypeId) Touching(
        Action<PullRequestInfo>? adjust = null, FileChange change = FileChange.Modified)
    {
        var model = Model();
        string id = Fixture.AllTypeIds(model).First();

        var pull = new PullRequestInfo
        {
            Number = 7,
            Title = "Something",
            Author = "ada",
            Additions = 120,
            Deletions = 40,
            Files = { new ChangedFile { Path = "src/A.cs", Change = change, Additions = 120 } },
        };
        // The join normally done by GitHub.Resolve; done directly so the layout is tested without
        // dragging a real repository into it.
        pull.Files[0].TypeIds.Add(id);
        pull.Files[0].Project = model.Projects[0].Name;
        adjust?.Invoke(pull);

        var snapshot = new GitHubSnapshot { Available = true, Repository = "owner/name" };
        snapshot.PullRequests.Add(pull);
        return (model, snapshot, id);
    }

    static IEnumerable<BoxInstance> Overlay(SceneGraph scene) =>
        scene.Boxes.Where(b => (b.Layer & (CityLayer.Works | CityLayer.Backlog)) != 0);

    [Fact]
    public void TheOverlayNeverMovesABuilding()
    {
        // The invariant the whole design rests on. The city is derived from source and must be
        // identical whether or not anybody has a pull request open — that is what makes the remote
        // safe to re-read at runtime, and what means a deleted class raises hoarding rather than
        // leaving a hole nobody knows how to fill.
        var (model, snapshot, _) = Touching();

        var plain = CityLayout.Build(model);
        var dressed = CityLayout.Build(model, snapshot);

        var before = plain.Boxes.Where(b => b.Layer != CityLayer.Works
                                         && b.Layer != CityLayer.Backlog).ToList();
        var after = dressed.Boxes.Where(b => b.Layer != CityLayer.Works
                                          && b.Layer != CityLayer.Backlog).ToList();

        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].BasePosition, after[i].BasePosition);
            Assert.Equal(before[i].Size, after[i].Size);
        }

        // And the overlay did actually put something there, or this proves nothing.
        Assert.NotEmpty(Overlay(dressed));
    }

    [Fact]
    public void EverythingTheOverlayAddsIsTaggedSoItCanBeSwitchedOff()
    {
        var (model, snapshot, _) = Touching();
        snapshot.Issues.Add(new IssueInfo { Number = 1, Category = IssueCategory.Bug, DaysOpen = 3 });

        var plain = CityLayout.Build(model);
        var dressed = CityLayout.Build(model, snapshot);

        // Every box the snapshot caused must carry a layer, or it cannot be hidden and cannot be
        // rebuilt when the remote is re-read.
        Assert.Equal(dressed.Boxes.Count - plain.Boxes.Count, Overlay(dressed).Count());
    }

    [Fact]
    public void WorksAppearOnTheBuildingThePullRequestTouches()
    {
        var (model, snapshot, id) = Touching();
        var scene = CityLayout.Build(model, snapshot);
        var site = scene.Sites[id];

        // Hoarding sits at the lot boundary, so everything the site adds is within a short reach of
        // the building it belongs to. A pull request landing somewhere else in the city would be
        // worse than useless.
        Assert.All(Overlay(scene), box =>
            Assert.True(
                MathF.Abs(box.BasePosition.X - site.Center.X) < site.Side * 2f + 12f
                && MathF.Abs(box.BasePosition.Z - site.Center.Z) < site.Side * 2f + 12f,
                $"a works box at {box.BasePosition} is nowhere near its building at {site.Center}"));
    }

    [Fact]
    public void ADraftIsFencedButNothingIsHappeningOnIt()
    {
        // A draft's author has explicitly said it isn't ready. Raising scaffolding would claim work
        // is under way that they have said isn't.
        var (model, ready, _) = Touching();
        var (draftModel, draft, _) = Touching(p => p.IsDraft = true);

        int active = Overlay(CityLayout.Build(model, ready)).Count();
        int fenced = Overlay(CityLayout.Build(draftModel, draft)).Count();

        Assert.True(fenced > 0, "a draft should still be fenced off");
        Assert.True(fenced < active,
            $"a draft raised {fenced} pieces against {active} for a live one — it should be fewer");
    }

    [Fact]
    public void OnlyAProposedFileRaisesAGhost()
    {
        static int Ghosts(SceneGraph scene) =>
            scene.Boxes.Count(b => (b.Flags & (uint)BoxFlags.Ghost) != 0);

        var (a, modified, _) = Touching(change: FileChange.Modified);
        var (b, added, _) = Touching(change: FileChange.Added);

        Assert.Equal(0, Ghosts(CityLayout.Build(a, modified)));
        Assert.True(Ghosts(CityLayout.Build(b, added)) > 0,
            "a file the pull request adds has no building yet, so it should be drawn as one");
    }

    [Fact]
    public void AGhostStandsInsideItsOwnProjectsDistrict()
    {
        // A proposed building has no lot, so its position is a guess — but it must at least be a
        // guess in the right district, or it says nothing at all.
        var (model, snapshot, _) = Touching(change: FileChange.Added);
        var scene = CityLayout.Build(model, snapshot);
        var district = scene.Districts[model.Projects[0].Name];

        var ghost = Assert.Single(scene.Boxes, b => (b.Flags & (uint)BoxFlags.Ghost) != 0);
        Assert.InRange(ghost.BasePosition.X, district.X, district.X + district.Width);
        Assert.InRange(ghost.BasePosition.Z, district.Z, district.Z + district.Depth);
    }

    [Fact]
    public void AConflictClosesARoadAndRaisesIt()
    {
        var (model, snapshot, _) = Touching(p => p.Conflicting = true);
        var scene = CityLayout.Build(model, snapshot);

        Assert.Contains(scene.Interest, i => i.Headline == "ROAD CLOSED");
    }

    [Fact]
    public void ClosuresAreCappedSoTheyStayIncidents()
    {
        // The city already draws the line between rare incidents and aggregate conditions. Twenty
        // simultaneous road closures would turn a signal into wallpaper.
        var model = Model();
        var snapshot = new GitHubSnapshot { Available = true };
        string[] ids = Fixture.AllTypeIds(model).Take(20).ToArray();

        for (int i = 0; i < ids.Length; i++)
        {
            var pull = new PullRequestInfo { Number = i, Conflicting = true, Title = $"P{i}" };
            pull.Files.Add(new ChangedFile { Path = $"src/{i}.cs" });
            pull.Files[0].TypeIds.Add(ids[i]);
            snapshot.PullRequests.Add(pull);
        }

        var scene = CityLayout.Build(model, snapshot);
        Assert.InRange(scene.Interest.Count(i => i.Headline == "ROAD CLOSED"), 1, 6);
    }

    [Fact]
    public void DefectsAndRequestsQueueAtDifferentBuildings()
    {
        var model = Model();
        var snapshot = new GitHubSnapshot { Available = true };
        for (int i = 0; i < 6; i++)
            snapshot.Issues.Add(new IssueInfo
            {
                Number = i,
                Category = i % 2 == 0 ? IssueCategory.Bug : IssueCategory.Feature,
                DaysOpen = 2,
            });

        var scene = CityLayout.Build(model, snapshot);
        var queues = scene.Interest.Where(i =>
            i.Headline is "REPORTED DEFECTS" or "OPEN REQUESTS").ToList();

        Assert.NotEmpty(queues);
        // Wherever the landmarks ended up, the two queues are not the same queue.
        if (queues.Count == 2) Assert.NotEqual(queues[0].Focus, queues[1].Focus);
    }

    [Fact]
    public void AnOldIssuePitchesATentAndARecentOneStands()
    {
        static int Backlog(int daysOpen)
        {
            var model = Model();
            var snapshot = new GitHubSnapshot { Available = true };
            snapshot.Issues.Add(new IssueInfo
            {
                Number = 1, Category = IssueCategory.Bug, DaysOpen = daysOpen,
            });
            return CityLayout.Build(model, snapshot)
                .Boxes.Count(b => b.Layer == CityLayer.Backlog);
        }

        // Age is the axis that matters: a hundred issues from this week and a hundred from two years
        // ago are the same number and completely different situations. A tent is more pieces than a
        // person, which is how the difference reads from across the plaza.
        Assert.True(Backlog(400) > Backlog(2),
            "an issue nobody has touched in a year should read as more settled than a new one");
    }

    [Fact]
    public void NoSnapshotMeansNoOverlayAtAll()
    {
        var scene = CityLayout.Build(Model());
        Assert.Empty(Overlay(scene));

        // An unavailable snapshot is the same as none: a repository we could not reach must not
        // look like a repository with nothing happening in it.
        var unreachable = new GitHubSnapshot { Available = false, Reason = "gh not installed" };
        Assert.Empty(Overlay(CityLayout.Build(Model(), unreachable)));
    }
}
