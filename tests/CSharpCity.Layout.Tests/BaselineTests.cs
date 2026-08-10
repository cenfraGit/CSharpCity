using System.Numerics;
using System.Text;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// Pins the parts of the city the street rework is not allowed to touch.
/// </summary>
/// <remarks>
/// The street network is being rebuilt from scratch, and the one thing that must not happen is a
/// silent change to something else — a footpath that moved, a walker that vanished, a building that
/// shifted a metre. Those are exactly the changes nobody notices in a screenshot.
///
/// So each shape of solution gets a signature: counts, plus an order-independent hash of the
/// geometry that matters. Order-independence is deliberate. Reordering the emission of footpaths is
/// a legitimate thing for a refactor to do; moving one of them is not. A hash over a sorted list
/// would fail on the first, an XOR-fold fails only on the second.
///
/// If a signature is missing from <see cref="Expected"/> the test prints the line to paste in. That
/// is the intended way to add a shape, and the intended way to re-baseline after a change that is
/// genuinely meant to move these numbers.
/// </remarks>
public class BaselineTests
{
    /// <summary>
    /// Recorded before the street rework began, against the shapes in
    /// <see cref="LayoutInvariantTests.Shapes"/>.
    /// </summary>
    static readonly Dictionary<string, string> Expected = new()
    {
        ["1-1-1"] =
            "walkers=0 paths=0000000000000000 footpaths=0:0000000000000000 rail=0 air=0 " +
            "buildings=1:3a244b3e2ed46857",
        // Re-baselined when footpaths were dropped from the top of the layer stack onto the ground
        // and broken where roads cross them, which is what stopped the walkers hovering at head
        // height. Note what did *not* move when it was: walker counts, rail, air and every
        // building are byte-identical, which is the evidence that the change stayed where it was
        // meant to.
        ["1-40-1"] =
            "walkers=308 paths=233763505d20e39d footpaths=169:442163ef7b8700fd rail=0 air=1 " +
            "buildings=121:3398f42f14e6d8c2",
        ["3-25-4"] =
            "walkers=660 paths=de90c066eabf8d94 footpaths=377:64638466e783a208 rail=14 air=3 " +
            "buildings=219:6329ea5db3434432",
        ["40-30-3"] =
            "walkers=11881 paths=a0f5b79a0a94b30d footpaths=8552:23c9512574a478d1 rail=273 air=40 " +
            "buildings=3600:da3f51e47dcb8128",
        ["2-1-5"] =
            "walkers=1 paths=880f6f1fd6784aec footpaths=1:c253821166ef49b6 rail=2 air=2 " +
            "buildings=2:b52a3e6b6a6bcbe4",
    };

    [Theory]
    [MemberData(nameof(LayoutInvariantTests.Shapes), MemberType = typeof(LayoutInvariantTests))]
    public void NothingOutsideTheStreetsHasMoved(int projects, int typesPer, int depth)
    {
        string key = $"{projects}-{typesPer}-{depth}";
        var scene = CityLayout.Build(Fixture.Connect(Fixture.Solution(projects, typesPer, depth)));
        string actual = Signature(scene);

        Assert.True(Expected.TryGetValue(key, out var expected),
            $"No baseline for shape {key}. Add:  [\"{key}\"] = \"{actual}\",");
        Assert.True(expected == actual,
            $"Shape {key} changed outside the street system.\n  was: {expected}\n  now: {actual}");
    }

    /// <summary>
    /// Everything the street rework must leave alone, in one line: walkers and their paths, the
    /// footpaths themselves, rail and air, and the buildings.
    /// </summary>
    internal static string Signature(SceneGraph scene)
    {
        var walkers = scene.Travellers.Where(t => t.Kind == TravellerKind.Pedestrian).ToList();
        var rail = scene.Travellers.Count(t => t.Layer == CityLayer.Rail);
        var air = scene.Travellers.Count(t => t.Layer == CityLayer.Air);

        // Endpoints rather than whole polylines: a footpath is a straight desire line, so its two
        // ends are its entire geometry, and a walker's path is the thing that would move if the
        // layout shifted underneath it.
        ulong walkerPaths = Fold(walkers.Select(w =>
        {
            var path = scene.Paths[w.PathIndex];
            return Hash(path.Points[0]) ^ Rotate(Hash(path.Points[^1]));
        }));

        var footpaths = scene.Roads.Where(r => (r.Flags & (uint)RoadFlags.Footpath) != 0).ToList();
        ulong footpathGeometry = Fold(footpaths.Select(r =>
            Hash(r.Center) ^ Rotate(Hash(new Vector3(r.Length, r.Width, r.Yaw)))));

        // Buildings only — loose scenery carries PickId -1, and the street rework is allowed to add
        // scenery. It is not allowed to move a building.
        //
        // Anything attached to a building keeps that building's PickId and so is covered here
        // already: cranes, skips and bicycles from the repository's history all land in this fold.
        // They do not show up in the recorded signatures purely because the fixture has no git
        // history to give them; HistoryTests drives those channels directly instead.
        var buildings = scene.Boxes.Where(b => b.PickId >= 0).ToList();
        ulong buildingGeometry = Fold(buildings.Select(b =>
            (ulong)b.PickId * 1099511628211ul ^ Hash(b.BasePosition) ^ Rotate(Hash(b.Size))));

        return new StringBuilder()
            .Append("walkers=").Append(walkers.Count)
            .Append(" paths=").Append(walkerPaths.ToString("x16"))
            .Append(" footpaths=").Append(footpaths.Count)
            .Append(':').Append(footpathGeometry.ToString("x16"))
            .Append(" rail=").Append(rail)
            .Append(" air=").Append(air)
            .Append(" buildings=").Append(buildings.Count)
            .Append(':').Append(buildingGeometry.ToString("x16"))
            .ToString();
    }

    /// <summary>XOR-fold: insensitive to order, sensitive to every member.</summary>
    static ulong Fold(IEnumerable<ulong> values)
    {
        ulong folded = 0;
        foreach (ulong value in values) folded ^= value;
        return folded;
    }

    /// <summary>
    /// FNV-1a over the coordinates rounded to a millimetre, so the signature survives a change in
    /// the order of floating-point operations but not a real move.
    /// </summary>
    static ulong Hash(Vector3 v)
    {
        ulong h = 14695981039346656037ul;
        foreach (float component in new[] { v.X, v.Y, v.Z })
        {
            long fixedPoint = (long)MathF.Round(component * 1000f);
            for (int b = 0; b < 8; b++)
            {
                h ^= (ulong)((fixedPoint >> (b * 8)) & 0xFF);
                h *= 1099511628211ul;
            }
        }
        return h;
    }

    /// <summary>
    /// Rotating one half of a pair stops <c>a ^ b</c> collapsing when the two happen to be equal —
    /// which they are for any zero-length or symmetric piece of geometry.
    /// </summary>
    static ulong Rotate(ulong value) => (value << 27) | (value >> 37);
}
