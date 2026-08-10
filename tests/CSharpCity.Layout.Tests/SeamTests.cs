using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The architectural seam: the split of the projects that the fewest dependencies cross.
/// </summary>
public class SeamTests
{
    /// <summary>Two clusters that talk among themselves and barely to each other.</summary>
    static CityModel Layered(int perCluster, int bridgeEdges)
    {
        var model = new CityModel { SolutionName = "Layered" };

        for (int c = 0; c < 2; c++)
        for (int p = 0; p < perCluster; p++)
        {
            var project = new ProjectNode { Name = $"C{c}.P{p}" };
            project.Types.Add(Fixture.Type(project.Name, $"C{c}.P{p}", "T"));
            model.Projects.Add(project);
        }

        string Id(int c, int p) => $"C{c}.P{p}!global::C{c}.P{p}.T";

        // Dense inside each cluster.
        for (int c = 0; c < 2; c++)
        for (int a = 0; a < perCluster; a++)
        for (int b = 0; b < perCluster; b++)
            if (a != b)
                model.Edges.Add(new DependencyEdge
                {
                    FromId = Id(c, a), ToId = Id(c, b), Weight = 20,
                });

        // Thin between them.
        for (int i = 0; i < bridgeEdges; i++)
            model.Edges.Add(new DependencyEdge
            {
                FromId = Id(0, i % perCluster), ToId = Id(1, i % perCluster), Weight = 1,
            });

        return model;
    }

    static Dictionary<string, float> EqualWeights(CityModel model) =>
        model.Projects.ToDictionary(p => p.Name, _ => 1f, StringComparer.Ordinal);

    [Fact]
    public void FindsTheBoundaryWhenThereIsOne()
    {
        var model = Layered(perCluster: 4, bridgeEdges: 2);

        var seam = Seam.Find(model, EqualWeights(model))!;

        // Every project on one side belongs to the same cluster, which is the whole claim.
        Assert.All(seam.Left, p => Assert.Equal(seam.Left[0][..2], p[..2]));
        Assert.All(seam.Right, p => Assert.Equal(seam.Right[0][..2], p[..2]));
        Assert.NotEqual(seam.Left[0][..2], seam.Right[0][..2]);
    }

    [Fact]
    public void EveryProjectLandsOnExactlyOneBank()
    {
        var model = Layered(4, 3);

        var seam = Seam.Find(model, EqualWeights(model))!;

        Assert.Empty(seam.Left.Intersect(seam.Right));
        Assert.Equal(model.Projects.Count, seam.Left.Count + seam.Right.Count);
    }

    [Fact]
    public void NeitherBankIsAllowedToBeATokenOne()
    {
        // Left free, the minimum cut of almost any real graph is one leaf project on its own:
        // technically minimal, architecturally meaningless.
        var model = Layered(5, 4);

        var seam = Seam.Find(model, EqualWeights(model))!;

        Assert.True(seam.Left.Count >= 2 && seam.Right.Count >= 2,
            $"split came out {seam.Left.Count} against {seam.Right.Count}");
    }

    [Fact]
    public void TheSameSolutionAlwaysGivesTheSameSeam()
    {
        var a = Seam.Find(Layered(4, 3), EqualWeights(Layered(4, 3)))!;
        var b = Seam.Find(Layered(4, 3), EqualWeights(Layered(4, 3)))!;

        Assert.Equal(a.Left, b.Left);
        Assert.Equal(a.CrossingWeight, b.CrossingWeight);
    }

    [Fact]
    public void ATightlyCoupledSolutionLeaksMoreThanALayeredOne()
    {
        // The measure has to be able to tell the two apart, or it says nothing about a design.
        var layered = Seam.Find(Layered(4, 2), EqualWeights(Layered(4, 2)))!;

        var tangled = Layered(4, 2);
        string Id(int c, int p) => $"C{c}.P{p}!global::C{c}.P{p}.T";
        for (int a = 0; a < 4; a++)
        for (int b = 0; b < 4; b++)
            tangled.Edges.Add(new DependencyEdge { FromId = Id(0, a), ToId = Id(1, b), Weight = 20 });

        var tangledSeam = Seam.Find(tangled, EqualWeights(tangled))!;

        Assert.True(tangledSeam.Leakage > layered.Leakage * 3f,
            $"tangled leaked {tangledSeam.Leakage:P0} against {layered.Leakage:P0} for layered");
    }

    [Fact]
    public void AHandfulOfProjectsIsNotWorthSplitting()
    {
        var model = Layered(1, 1);   // two projects

        Assert.Null(Seam.Find(model, EqualWeights(model)));
    }

    [Fact]
    public void CrossingsAreRankedHeaviestFirst()
    {
        var model = Layered(4, 3);

        var seam = Seam.Find(model, EqualWeights(model))!;

        var weights = seam.Crossings.Select(c => c.Weight).ToList();
        Assert.Equal(weights.OrderByDescending(w => w), weights);
        Assert.Equal(seam.CrossingWeight, weights.Sum());
    }
}
