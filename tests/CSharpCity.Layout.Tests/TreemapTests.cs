namespace CSharpCity.Layout.Tests;

/// <summary>
/// The treemap and the namespace trie underneath it — the two places where types have gone missing.
/// </summary>
public class TreemapTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public void EveryItemIsPlacedExactlyOnce(int count)
    {
        var items = Enumerable.Range(0, count).Select(i => (Item: i, Weight: 1f + i)).ToList();
        var placed = new List<int>();

        Treemap.Layout(items, new Bounds2(0, 0, 500, 400), (item, _) => placed.Add(item));

        Assert.Equal(count, placed.Count);
        Assert.Equal(count, placed.Distinct().Count());
    }

    [Fact]
    public void CellsDoNotOverlap()
    {
        var items = Enumerable.Range(0, 40).Select(i => (Item: i, Weight: 1f + i % 7)).ToList();
        var cells = new List<Bounds2>();

        Treemap.Layout(items, new Bounds2(0, 0, 600, 600), (_, cell) => cells.Add(cell));

        for (int i = 0; i < cells.Count; i++)
        for (int j = i + 1; j < cells.Count; j++)
        {
            bool apart = cells[i].X + cells[i].Width <= cells[j].X + 0.001f
                         || cells[j].X + cells[j].Width <= cells[i].X + 0.001f
                         || cells[i].Z + cells[i].Depth <= cells[j].Z + 0.001f
                         || cells[j].Z + cells[j].Depth <= cells[i].Z + 0.001f;
            Assert.True(apart, $"cell {i} and cell {j} overlap");
        }
    }

    [Fact]
    public void CellsTileTheWholeRegion()
    {
        var items = Enumerable.Range(0, 25).Select(i => (Item: i, Weight: 2f + i)).ToList();
        var region = new Bounds2(10, 20, 480, 360);
        float covered = 0f;

        Treemap.Layout(items, region, (_, cell) => covered += cell.Area);

        Assert.Equal(region.Area, covered, 1);
    }

    [Fact]
    public void EveryCutIsReportedExactlyOnce()
    {
        var items = Enumerable.Range(0, 20).Select(i => (Item: i, Weight: 1f)).ToList();
        var cuts = new List<Treemap.Cut>();

        Treemap.Layout(items, new Bounds2(0, 0, 300, 300), (_, _) => { },
            cut => cuts.Add(cut));

        // A binary split of n items makes exactly n-1 divisions. Emitting a cut twice would put two
        // roads on one boundary — the seam that made blocks look detached.
        Assert.Equal(items.Count - 1, cuts.Count);
    }

    [Fact]
    public void CutsLieInsideTheRegionTheyDivide()
    {
        var items = Enumerable.Range(0, 12).Select(i => (Item: i, Weight: 1f + i)).ToList();
        var region = new Bounds2(0, 0, 200, 160);
        var cuts = new List<Treemap.Cut>();

        Treemap.Layout(items, region, (_, _) => { }, cut => cuts.Add(cut));

        foreach (var cut in cuts)
        {
            float position = cut.Vertical ? cut.Position : cut.Position;
            float low = cut.Vertical ? region.X : region.Z;
            float high = cut.Vertical ? region.X + region.Width : region.Z + region.Depth;

            Assert.InRange(position, low, high);
            Assert.True(cut.SpanEnd > cut.SpanStart, "a cut must have positive length");
        }
    }

    [Fact]
    public void CollapseKeepsEveryLeaf()
    {
        var root = new NamespaceNode();
        var expected = new List<string>();

        // A deliberately awkward trie: pass-through chains that will be folded away.
        foreach (var path in new[]
                 {
                     "A.B.C", "A.B.D", "A.X", "Q.R.S.T", "Q.R.S.U", "Z",
                 })
        {
            var node = root;
            foreach (var segment in path.Split('.')) node = node.Descend(segment);
            node.Leaves.Add((path, 1f));
            expected.Add(path);
        }

        root.Accumulate();
        var leaves = Leaves(root.Collapse()).ToList();

        Assert.Equal(expected.OrderBy(x => x), leaves.OrderBy(x => x));
    }

    [Fact]
    public void SiblingsThatFoldToTheSameNameBothSurvive()
    {
        // The exact shape that silently drops types on a real solution: two branches whose folded
        // chains end in the same segment. Keyed on that final segment, one silently replaced the
        // other and every type beneath it vanished from the city.
        var root = new NamespaceNode();
        foreach (var path in new[] { "Alpha.Core.Common", "Beta.Util.Common" })
        {
            var node = root;
            foreach (var segment in path.Split('.')) node = node.Descend(segment);
            node.Leaves.Add((path, 1f));
        }

        root.Accumulate();
        var leaves = Leaves(root.Collapse()).ToList();

        Assert.Equal(2, leaves.Count);
        Assert.Contains("Alpha.Core.Common", leaves);
        Assert.Contains("Beta.Util.Common", leaves);
    }

    static IEnumerable<string> Leaves(NamespaceNode node)
    {
        foreach (var (id, _) in node.Leaves) yield return id;
        foreach (var child in node.Children.Values)
            foreach (var id in Leaves(child))
                yield return id;
    }
}
