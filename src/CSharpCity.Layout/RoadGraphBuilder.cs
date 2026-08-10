using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>One road as the treemap reported it, before crossings are worked out.</summary>
internal readonly record struct RoadCut(
    bool Vertical, float Position, float SpanStart, float SpanEnd,
    RoadKind Kind, float Width, float SurfaceY);

/// <summary>
/// Turns the treemap's cut lines into an exact planar arrangement: one node per crossing, one edge
/// per stretch between crossings.
/// </summary>
/// <remarks>
/// The two things this replaces are worth naming, because both were guesses that worked at small
/// scale and quietly failed at large.
///
/// The first was a constant 12 m overshoot on every recorded centreline. A block is inset by half
/// the width of the road it abuts, so a child street genuinely does stop short of its parent's
/// centreline, and without something the graph comes out in fragments. But twelve metres is more
/// than three times the half-width of an alley, so the fix pushed endpoints deep inside neighbouring
/// lots — which is how a car ended up driving through a building. The reach here is derived from the
/// widths involved instead: exactly the gap the inset opened, plus a metre of slack.
///
/// The second was matching crossings by comparing floats within an epsilon, then snapping junctions
/// to a half-metre lattice to paper over the disagreements. Here every coordinate is clustered once
/// into a canonical value up front, so "these two roads are on the same line" becomes an integer
/// comparison and cannot be answered inconsistently by two different callers.
/// </remarks>
internal static class RoadGraphBuilder
{
    /// <summary>Coordinates within this are the same line. Cuts are emitted from exact arithmetic.</summary>
    const float CoordTolerance = 0.05f;

    /// <summary>Slack beyond the geometric gap, so a road that just barely reaches still joins.</summary>
    const float JoinSlack = 1.0f;

    /// <summary>Below this a height difference is a surface; above it, a bridge.</summary>
    const float GradeSeparation = 0.5f;

    public static float SpeedFor(RoadKind kind) => kind switch
    {
        RoadKind.Boulevard => 13.9f,
        RoadKind.Street => 8.3f,
        RoadKind.Alley => 4.2f,
        RoadKind.HighwayDeck => 25f,
        RoadKind.HighwayRamp => 11f,
        _ => 5f,
    };

    static byte LanesFor(RoadKind kind) => kind switch
    {
        RoadKind.Boulevard => 2,
        RoadKind.HighwayDeck => 2,
        _ => 1,
    };

    /// <summary>
    /// A working graph, still open for highways to append to before it is sealed by
    /// <see cref="Finish"/>.
    /// </summary>
    internal sealed class Draft
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<RoadNodeKind> Kinds = new();
        public readonly List<(int A, int B, float Width, RoadKind Kind)> Edges = new();
        readonly Dictionary<(int X, int Z), int> _byCell = new();

        public float[] CanonicalX = Array.Empty<float>();
        public float[] CanonicalZ = Array.Empty<float>();
        /// <summary>Which canonical coordinate each one folds onto; see SnapCoordinates.</summary>
        public int[] RepresentativeX = Array.Empty<int>();
        public int[] RepresentativeZ = Array.Empty<int>();

        /// <summary>
        /// Finds or creates the node at a canonical coordinate pair. Identity is the pair of
        /// indices, not the floats, so two roads meeting at the same place always agree that they do.
        /// </summary>
        public int Node(int xIndex, int zIndex, float y, RoadNodeKind kind)
        {
            xIndex = RepresentativeX[xIndex];
            zIndex = RepresentativeZ[zIndex];
            if (_byCell.TryGetValue((xIndex, zIndex), out int existing)) return existing;
            int index = Positions.Count;
            Positions.Add(new Vector3(CanonicalX[xIndex], y, CanonicalZ[zIndex]));
            Kinds.Add(kind);
            _byCell[(xIndex, zIndex)] = index;
            return index;
        }

        /// <summary>A node at an arbitrary position, off the street lattice — ramps and decks.</summary>
        public int FreeNode(Vector3 position, RoadNodeKind kind)
        {
            Positions.Add(position);
            Kinds.Add(kind);
            return Positions.Count - 1;
        }

        public void Connect(int a, int b, float width, RoadKind kind)
        {
            if (a != b) Edges.Add((a, b, width, kind));
        }

        /// <summary>The widest road arriving at each node. Sized to <see cref="Positions"/>.</summary>
        public float[] WidestAtNode()
        {
            var widest = new float[Positions.Count];
            foreach (var (a, b, width, _) in Edges)
            {
                widest[a] = MathF.Max(widest[a], width);
                widest[b] = MathF.Max(widest[b], width);
            }
            return widest;
        }

        /// <summary>Nodes sitting on one straight line, in order along it.</summary>
        public int[] NodesOnLine(bool vertical, float position, float tolerance = 0.5f)
        {
            var onLine = new List<int>();
            for (int i = 0; i < Positions.Count; i++)
            {
                float across = vertical ? Positions[i].X : Positions[i].Z;
                if (MathF.Abs(across - position) > tolerance) continue;
                onLine.Add(i);
            }
            return onLine.OrderBy(i => vertical ? Positions[i].Z : Positions[i].X).ToArray();
        }

        /// <summary>
        /// Inserts a node into the road that already runs along a line, so a ramp arrives at a real
        /// T-junction rather than at a stub hanging off the nearest crossroads.
        /// </summary>
        public bool SpliceOntoLine(bool vertical, float position, int node)
        {
            float at = vertical ? Positions[node].Z : Positions[node].X;

            for (int e = 0; e < Edges.Count; e++)
            {
                var (a, b, width, kind) = Edges[e];
                if (kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

                var (pa, pb) = (Positions[a], Positions[b]);
                float acrossA = vertical ? pa.X : pa.Z;
                float acrossB = vertical ? pb.X : pb.Z;
                if (MathF.Abs(acrossA - position) > 0.5f || MathF.Abs(acrossB - position) > 0.5f)
                    continue;

                float alongA = vertical ? pa.Z : pa.X;
                float alongB = vertical ? pb.Z : pb.X;
                if (at <= MathF.Min(alongA, alongB) || at >= MathF.Max(alongA, alongB)) continue;

                Edges[e] = (a, node, width, kind);
                Edges.Add((node, b, width, kind));
                return true;
            }

            return false;
        }
    }

    public static Draft Arrange(IReadOnlyList<RoadCut> cuts)
    {
        var draft = new Draft();
        if (cuts.Count == 0) return draft;

        var vertical = new List<RoadCut>();
        var horizontal = new List<RoadCut>();
        foreach (var cut in cuts)
        {
            if (cut.SpanEnd - cut.SpanStart < 0.5f) continue;
            (cut.Vertical ? vertical : horizontal).Add(cut);
        }

        // To a fixed point: absorbing one road extends the survivor's span, which can bring it into
        // contact with a third road that did not overlap anything on the previous pass.
        vertical = MergeToFixedPoint(vertical);
        horizontal = MergeToFixedPoint(horizontal);

        // A vertical cut lives at an X and spans a range of Z; a horizontal one is the reverse. So
        // the canonical X values are the vertical positions plus the horizontal endpoints, and the
        // canonical Z values the mirror image.
        draft.CanonicalX = Canonicalise(vertical.Select(c => c.Position)
            .Concat(horizontal.SelectMany(c => new[] { c.SpanStart, c.SpanEnd })));
        draft.CanonicalZ = Canonicalise(horizontal.Select(c => c.Position)
            .Concat(vertical.SelectMany(c => new[] { c.SpanStart, c.SpanEnd })));

        // A road's own centreline earns a snapping radius; a coordinate that is merely where some
        // other road happens to stop gets none, so road ends fold onto crossings and never the
        // other way round.
        draft.RepresentativeX = SnapCoordinates(draft.CanonicalX, WidthsAt(draft.CanonicalX, vertical));
        draft.RepresentativeZ = SnapCoordinates(draft.CanonicalZ,
            WidthsAt(draft.CanonicalZ, horizontal));

        // Horizontals bucketed by their canonical Z, so a vertical only looks at the ones whose
        // line actually falls inside the range it spans.
        // Buckets hold indices, not the cuts themselves: RoadCut is a record struct, so two
        // identical cuts compare equal, and a dictionary keyed by the value would merge them.
        var byZ = new Dictionary<int, List<int>>();
        for (int h = 0; h < horizontal.Count; h++)
        {
            int z = IndexOf(draft.CanonicalZ, horizontal[h].Position);
            if (!byZ.TryGetValue(z, out var bucket)) byZ[z] = bucket = new List<int>();
            bucket.Add(h);
        }
        var zKeys = byZ.Keys.OrderBy(k => draft.CanonicalZ[k]).ToArray();
        var zValues = zKeys.Select(k => draft.CanonicalZ[k]).ToArray();

        // Crossing parameters per cut, as canonical indices along the cut's own axis.
        var verticalStops = new List<SortedSet<int>>(vertical.Count);
        var horizontalStops = new List<SortedSet<int>>(horizontal.Count);
        for (int i = 0; i < vertical.Count; i++) verticalStops.Add(new SortedSet<int>(ByZ(draft)));
        for (int i = 0; i < horizontal.Count; i++) horizontalStops.Add(new SortedSet<int>(ByX(draft)));

        // The furthest any pairing could possibly reach, used only to bound the search range.
        float widestReach = MaxHalfWidth(horizontal) + JoinSlack;

        for (int v = 0; v < vertical.Count; v++)
        {
            var cut = vertical[v];
            float outerReach = MathF.Max(0.5f * cut.Width, widestReach) + JoinSlack;

            int from = LowerBound(zValues, cut.SpanStart - outerReach);
            for (int k = from; k < zValues.Length; k++)
            {
                if (zValues[k] > cut.SpanEnd + outerReach) break;

                foreach (int h in byZ[zKeys[k]])
                {
                    var other = horizontal[h];
                    // The gap the inset opened is half the wider of the two roads. Reaching exactly
                    // that far closes it; reaching further would put a junction inside a lot.
                    float join = 0.5f * MathF.Max(cut.Width, other.Width) + JoinSlack;
                    if (MathF.Abs(cut.SurfaceY - other.SurfaceY) >= GradeSeparation) continue;
                    if (other.Position < cut.SpanStart - join || other.Position > cut.SpanEnd + join)
                        continue;
                    if (cut.Position < other.SpanStart - join || cut.Position > other.SpanEnd + join)
                        continue;

                    verticalStops[v].Add(IndexOf(draft.CanonicalZ, other.Position));
                    horizontalStops[h].Add(IndexOf(draft.CanonicalX, cut.Position));
                }
            }
        }

        // A cut that ends without meeting anything still has to stop somewhere, so its own ends
        // become junctions too — but only if no crossing already claimed that end.
        //
        // Getting this wrong is subtle and looks like a rendering bug. A block is inset from the
        // road it abuts, so an alley's recorded end sits a couple of metres short of the street it
        // joins; the crossing test above correctly reaches across that gap and puts a junction on
        // the street. Adding the raw end as well then produces a *second* junction two metres away,
        // joined by a stub of road going nowhere — and two junction patches that close together
        // overlap, which is exactly the flickering patch at a road connection this was meant to fix.
        for (int v = 0; v < vertical.Count; v++)
        {
            float slack = 0.5f * vertical[v].Width + JoinSlack + MaxHalfWidth(horizontal);
            AddEndIfClear(verticalStops[v], draft.CanonicalZ, vertical[v].SpanStart, slack);
            AddEndIfClear(verticalStops[v], draft.CanonicalZ, vertical[v].SpanEnd, slack);
        }
        for (int h = 0; h < horizontal.Count; h++)
        {
            float slack = 0.5f * horizontal[h].Width + JoinSlack + MaxHalfWidth(vertical);
            AddEndIfClear(horizontalStops[h], draft.CanonicalX, horizontal[h].SpanStart, slack);
            AddEndIfClear(horizontalStops[h], draft.CanonicalX, horizontal[h].SpanEnd, slack);
        }

        for (int v = 0; v < vertical.Count; v++)
            EmitChain(draft, vertical[v], verticalStops[v], isVertical: true);
        for (int h = 0; h < horizontal.Count; h++)
            EmitChain(draft, horizontal[h], horizontalStops[h], isVertical: false);

        return draft;
    }

    /// <summary>
    /// Folds together roads that run parallel close enough to be the same road.
    /// </summary>
    /// <remarks>
    /// The treemap divides recursively, so a district boulevard and a block street can easily land
    /// three metres apart — and two seven-metre roads three metres apart are not two roads, they are
    /// one road drawn twice. On the ground that showed up as the flickering seams and mismatched
    /// patches where roads meet: two slabs of tarmac at identical height, fighting for the same
    /// pixels along their whole length.
    ///
    /// The wider road wins, and inherits the other's span so nothing it was connecting gets cut off.
    /// Widest first, so a boulevard always absorbs the alley rather than the other way round.
    /// </remarks>
    static List<RoadCut> MergeToFixedPoint(List<RoadCut> cuts)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            var merged = MergeParallel(cuts);
            if (merged.Count == cuts.Count) return merged;
            cuts = merged;
        }
        return cuts;
    }

    static List<RoadCut> MergeParallel(List<RoadCut> cuts)
    {
        var kept = new List<RoadCut>(cuts.Count);

        foreach (var cut in cuts.OrderByDescending(c => c.Width)
                     .ThenBy(c => c.Position)
                     .ThenBy(c => c.SpanStart))
        {
            int absorbed = -1;
            for (int i = 0; i < kept.Count; i++)
            {
                var other = kept[i];
                if (MathF.Abs(other.SurfaceY - cut.SurfaceY) >= GradeSeparation) continue;
                // Overlapping tarmac, not merely nearby: the two slabs would share ground.
                if (MathF.Abs(other.Position - cut.Position) >= (other.Width + cut.Width) * 0.5f)
                    continue;

                // And genuinely overlapping *along* their length, not merely touching end to end.
                // This distinction matters more than it looks. Two alleys in neighbouring blocks
                // routinely meet end to end three metres apart in Z, which is inside the width
                // test — folding those together invents a road running down the middle of the gap
                // between them, along ground neither one covered, and that phantom then reaches out
                // and grabs a junction fifty metres away.
                float shared = MathF.Min(cut.SpanEnd, other.SpanEnd)
                             - MathF.Max(cut.SpanStart, other.SpanStart);
                float shorter = MathF.Min(cut.SpanEnd - cut.SpanStart, other.SpanEnd - other.SpanStart);
                if (shared < MathF.Max(2f, shorter * 0.25f)) continue;

                absorbed = i;
                break;
            }

            if (absorbed < 0)
            {
                kept.Add(cut);
                continue;
            }

            var winner = kept[absorbed];
            kept[absorbed] = winner with
            {
                SpanStart = MathF.Min(winner.SpanStart, cut.SpanStart),
                SpanEnd = MathF.Max(winner.SpanEnd, cut.SpanEnd),
            };
        }

        return kept;
    }

    /// <summary>
    /// Adds a cut's own end as a junction, unless a crossing already sits close enough to be that
    /// junction. A stop within the slack <em>is</em> the end of the road, just measured from the
    /// road it meets rather than from the block that stops short of it.
    /// </summary>
    static void AddEndIfClear(SortedSet<int> stops, float[] canonical, float position, float slack)
    {
        foreach (int stop in stops)
            if (MathF.Abs(canonical[stop] - position) <= slack) return;

        int index = TryIndexOf(canonical, position);
        if (index >= 0) stops.Add(index);
    }

    static float MaxHalfWidth(List<RoadCut> cuts)
    {
        float widest = 0f;
        foreach (var cut in cuts) widest = MathF.Max(widest, cut.Width);
        return widest * 0.5f;
    }

    static void EmitChain(Draft draft, RoadCut cut, SortedSet<int> stops, bool isVertical)
    {
        int fixedIndex = isVertical
            ? IndexOf(draft.CanonicalX, cut.Position)
            : IndexOf(draft.CanonicalZ, cut.Position);

        int previous = -1;
        foreach (int stop in stops)
        {
            int node = isVertical
                ? draft.Node(fixedIndex, stop, cut.SurfaceY, RoadNodeKind.Junction)
                : draft.Node(stop, fixedIndex, cut.SurfaceY, RoadNodeKind.Junction);

            if (previous >= 0) draft.Connect(previous, node, cut.Width, cut.Kind);
            previous = node;
        }
    }

    /// <summary>
    /// Seals a draft into a queryable graph: adjacency, components, signals and the spatial index.
    /// </summary>
    public static RoadGraph Finish(Draft draft)
    {
        var graph = new RoadGraph();
        if (draft.Edges.Count == 0) return graph;


        // Deduplicate: two cuts can legitimately describe the same stretch of tarmac when a parent
        // and a child divide at the same place, and a doubled edge would double the traffic on it.
        var seen = new HashSet<(int, int)>();
        var edges = new List<RoadEdge>(draft.Edges.Count);
        foreach (var (a, b, width, kind) in draft.Edges)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!seen.Add(key)) continue;

            var from = draft.Positions[a];
            var to = draft.Positions[b];
            float length = Vector3.Distance(from, to);
            if (length < 0.25f) continue;

            float pitch = MathF.Asin(Math.Clamp((to.Y - from.Y) / length, -1f, 1f));
            byte lanes = LanesFor(kind);
            edges.Add(new RoadEdge(a, b, length, width, kind, lanes, lanes, SpeedFor(kind), pitch));
        }

        graph.Edges = edges.ToArray();
        if (graph.Edges.Length == 0) return graph;
        graph.MaxSpeedLimit = graph.Edges.Max(e => e.SpeedLimit);

        int nodeCount = draft.Positions.Count;
        var degree = new int[nodeCount];
        foreach (var edge in graph.Edges)
        {
            degree[edge.A]++;
            degree[edge.B]++;
        }

        var firstIncident = new int[nodeCount + 1];
        for (int i = 0; i < nodeCount; i++) firstIncident[i + 1] = firstIncident[i] + degree[i];
        var incident = new int[firstIncident[nodeCount]];
        var cursor = (int[])firstIncident.Clone();
        for (int e = 0; e < graph.Edges.Length; e++)
        {
            incident[cursor[graph.Edges[e].A]++] = e;
            incident[cursor[graph.Edges[e].B]++] = e;
        }
        graph.Incident = incident;

        var component = Components(nodeCount, graph.Edges);
        graph.MainComponent = LargestComponent(component, graph.Edges, nodeCount);

        var signals = new List<Signal>();
        var nodes = new RoadNode[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var kind = draft.Kinds[i];
            if (kind == RoadNodeKind.Junction && degree[i] <= 1) kind = RoadNodeKind.Terminal;

            int signalIndex = -1;
            if (degree[i] >= 3)
            {
                // Signals only where two roads that both matter actually cross.
                //
                // Anything with a major road touching it used to qualify, which put lights on
                // every alley mouth in the city — over a thousand of them. A real network signals
                // the junctions where two through-routes conflict and leaves the rest to give way,
                // and the count falls by an order of magnitude.
                bool majorAlongX = false, majorAlongZ = false, boulevard = false;
                for (int k = firstIncident[i]; k < firstIncident[i + 1]; k++)
                {
                    var edge = graph.Edges[incident[k]];
                    if (edge.Kind is not (RoadKind.Boulevard or RoadKind.Street)) continue;
                    if (edge.Kind == RoadKind.Boulevard) boulevard = true;

                    var from = draft.Positions[edge.A];
                    var to = draft.Positions[edge.B];
                    if (MathF.Abs(to.X - from.X) >= MathF.Abs(to.Z - from.Z)) majorAlongX = true;
                    else majorAlongZ = true;
                }

                if (majorAlongX && majorAlongZ)
                {
                    // Amber doubles as the clearance interval: the gap in which a car that has
                    // already entered a sixteen-metre junction gets out of it before the other
                    // direction is released. Two seconds was not enough to cross the widest ones.
                    float green = boulevard ? 13f : 9f;
                    float amber = 3f;
                    float cycle = (green + amber) * 2f;
                    var at = draft.Positions[i];
                    float offset = StableHash.Unit((int)(at.X * 10f), (int)(at.Z * 10f)) * cycle;
                    signalIndex = signals.Count;
                    signals.Add(new Signal(i, green, amber, offset));
                }
            }

            nodes[i] = new RoadNode(draft.Positions[i], kind, firstIncident[i], degree[i],
                signalIndex, component[i]);
        }

        graph.Nodes = nodes;
        graph.Signals = signals.ToArray();
        BuildSpatialIndex(graph);
        BuildLines(graph, draft);
        return graph;
    }

    /// <summary>
    /// Folds coordinates that are closer together than the road at them is wide.
    /// </summary>
    /// <remarks>
    /// Two side roads entering a sixteen-metre boulevard from opposite sides half a metre apart in
    /// Z are not two junctions with a road between them: the "road" is shorter than the crossing is
    /// wide, so it lies entirely inside the junction. Left alone it gives a stub of tarmac no car
    /// can occupy, two junction patches overlapping each other, and a car that stops twice in the
    /// same place.
    ///
    /// This has to happen here, on the coordinates, rather than later by merging the nodes
    /// themselves. Merging a node moves it in both X and Z at once, and every axis-aligned road
    /// still attached to it comes out diagonal. Snapping one axis at a time cannot do that.
    ///
    /// The wider road always wins, so a boulevard keeps its own centreline and it is the side road
    /// that gets nudged onto it.
    /// </remarks>
    static int[] SnapCoordinates(float[] canonical, float[] widthAt)
    {
        var representative = new int[canonical.Length];
        for (int i = 0; i < canonical.Length; i++) representative[i] = -1;

        // Widest first, and each road simply claims the ground it covers: anything within half its
        // width of its centreline is inside that road, so a junction there is that road's junction.
        //
        // Sweeping in coordinate order and comparing each entry with the one before it looks
        // equivalent and is not. A coordinate four metres from a boulevard would be passed over
        // because its immediate neighbour was two metres away and narrow, and by the time the
        // boulevard absorbed that neighbour the sweep had moved on — leaving two junctions four
        // metres apart on a sixteen-metre road.
        foreach (int i in Enumerable.Range(0, canonical.Length)
                     .OrderByDescending(i => widthAt[i])
                     .ThenBy(i => i))
        {
            if (representative[i] >= 0) continue;
            representative[i] = i;

            float radius = 0.5f * widthAt[i];
            if (radius <= 0f) continue;

            for (int j = i - 1; j >= 0 && canonical[i] - canonical[j] < radius; j--)
                if (representative[j] < 0) representative[j] = i;
            for (int j = i + 1; j < canonical.Length && canonical[j] - canonical[i] < radius; j++)
                if (representative[j] < 0) representative[j] = i;
        }

        return representative;
    }

    static float[] WidthsAt(float[] canonical, IEnumerable<RoadCut> cutsOnThisAxis)
    {
        var widths = new float[canonical.Length];
        foreach (var cut in cutsOnThisAxis)
        {
            int index = TryIndexOf(canonical, cut.Position);
            if (index >= 0) widths[index] = MathF.Max(widths[index], cut.Width);
        }
        return widths;
    }

    static int[] Components(int nodeCount, RoadEdge[] edges)
    {
        var parent = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        foreach (var edge in edges)
        {
            int a = Find(edge.A), b = Find(edge.B);
            if (a != b) parent[a] = b;
        }

        var label = new Dictionary<int, int>();
        var component = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            int root = Find(i);
            if (!label.TryGetValue(root, out int id)) label[root] = id = label.Count;
            component[i] = id;
        }
        return component;
    }

    /// <summary>By total road length, not node count — a fragment of long stubs is still a fragment.</summary>
    static int LargestComponent(int[] component, RoadEdge[] edges, int nodeCount)
    {
        var length = new Dictionary<int, float>();
        foreach (var edge in edges)
        {
            int id = component[edge.A];
            length.TryGetValue(id, out float sum);
            length[id] = sum + edge.Length;
        }
        int best = 0;
        float bestLength = -1f;
        foreach (var (id, sum) in length)
        {
            if (sum <= bestLength) continue;
            bestLength = sum;
            best = id;
        }
        return best;
    }

    static void BuildSpatialIndex(RoadGraph graph)
    {
        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var node in graph.Nodes)
        {
            minX = MathF.Min(minX, node.Position.X);
            maxX = MathF.Max(maxX, node.Position.X);
            minZ = MathF.Min(minZ, node.Position.Z);
            maxZ = MathF.Max(maxZ, node.Position.Z);
        }

        graph.GridOriginX = minX - 1f;
        graph.GridOriginZ = minZ - 1f;
        graph.GridColumns = Math.Max(1, (int)((maxX - minX + 2f) / graph.GridCell) + 1);
        graph.GridRows = Math.Max(1, (int)((maxZ - minZ + 2f) / graph.GridCell) + 1);

        int cells = graph.GridColumns * graph.GridRows;
        var counts = new int[cells + 1];

        void ForEachCell(int edge, Action<int> visit)
        {
            var a = graph.Nodes[graph.Edges[edge].A].Position;
            var b = graph.Nodes[graph.Edges[edge].B].Position;
            int x0 = Cell(MathF.Min(a.X, b.X), graph.GridOriginX, graph.GridColumns, graph.GridCell);
            int x1 = Cell(MathF.Max(a.X, b.X), graph.GridOriginX, graph.GridColumns, graph.GridCell);
            int z0 = Cell(MathF.Min(a.Z, b.Z), graph.GridOriginZ, graph.GridRows, graph.GridCell);
            int z1 = Cell(MathF.Max(a.Z, b.Z), graph.GridOriginZ, graph.GridRows, graph.GridCell);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    visit(z * graph.GridColumns + x);
        }

        for (int e = 0; e < graph.Edges.Length; e++) ForEachCell(e, cell => counts[cell + 1]++);
        for (int i = 0; i < cells; i++) counts[i + 1] += counts[i];

        var items = new int[counts[cells]];
        var cursor = (int[])counts.Clone();
        for (int e = 0; e < graph.Edges.Length; e++)
        {
            int edge = e;
            ForEachCell(e, cell => items[cursor[cell]++] = edge);
        }

        graph.CellStart = counts;
        graph.CellItems = items;
    }

    static int Cell(float value, float origin, int count, float size) =>
        Math.Clamp((int)((value - origin) / size), 0, count - 1);

    static void BuildLines(RoadGraph graph, Draft draft)
    {
        var alongX = new Dictionary<int, List<int>>();
        var alongZ = new Dictionary<int, List<int>>();

        for (int i = 0; i < graph.Nodes.Length; i++)
        {
            if (graph.Nodes[i].IncidentCount == 0) continue;
            var at = graph.Nodes[i].Position;

            int xIndex = TryIndexOf(draft.CanonicalX, at.X);
            if (xIndex >= 0)
            {
                if (!alongX.TryGetValue(xIndex, out var list)) alongX[xIndex] = list = new List<int>();
                list.Add(i);
            }

            int zIndex = TryIndexOf(draft.CanonicalZ, at.Z);
            if (zIndex >= 0)
            {
                if (!alongZ.TryGetValue(zIndex, out var list)) alongZ[zIndex] = list = new List<int>();
                list.Add(i);
            }
        }

        var xKeys = alongX.Keys.OrderBy(k => draft.CanonicalX[k]).ToArray();
        graph.LineX = xKeys.Select(k => draft.CanonicalX[k]).ToArray();
        graph.NodesOnLineX = xKeys
            .Select(k => alongX[k].OrderBy(n => graph.Nodes[n].Position.Z).ToArray())
            .ToArray();

        var zKeys = alongZ.Keys.OrderBy(k => draft.CanonicalZ[k]).ToArray();
        graph.LineZ = zKeys.Select(k => draft.CanonicalZ[k]).ToArray();
        graph.NodesOnLineZ = zKeys
            .Select(k => alongZ[k].OrderBy(n => graph.Nodes[n].Position.X).ToArray())
            .ToArray();
    }

    /// <summary>
    /// Collapses near-equal coordinates into one representative each, sorted.
    /// </summary>
    static float[] Canonicalise(IEnumerable<float> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var canonical = new List<float>();
        foreach (float value in sorted)
        {
            if (canonical.Count > 0 && value - canonical[^1] <= CoordTolerance) continue;
            canonical.Add(value);
        }
        return canonical.ToArray();
    }

    static int IndexOf(float[] canonical, float value)
    {
        int index = TryIndexOf(canonical, value);
        if (index >= 0) return index;
        // Every value fed to this was in the set Canonicalise was built from, so a miss means the
        // two disagree — better to fail loudly here than to silently place a junction elsewhere.
        throw new InvalidOperationException(
            $"Coordinate {value} has no canonical representative; the arrangement is inconsistent.");
    }

    static int TryIndexOf(float[] canonical, float value)
    {
        if (canonical.Length == 0) return -1;
        int index = Array.BinarySearch(canonical, value);
        if (index >= 0) return index;

        index = ~index;
        for (int probe = index - 1; probe <= index; probe++)
        {
            if (probe < 0 || probe >= canonical.Length) continue;
            if (MathF.Abs(canonical[probe] - value) <= CoordTolerance * 1.5f) return probe;
        }
        return -1;
    }

    static int LowerBound(float[] sorted, float value)
    {
        int index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index;
    }

    static IComparer<int> ByZ(Draft draft) =>
        Comparer<int>.Create((a, b) => draft.CanonicalZ[a].CompareTo(draft.CanonicalZ[b]));

    static IComparer<int> ByX(Draft draft) =>
        Comparer<int>.Create((a, b) => draft.CanonicalX[a].CompareTo(draft.CanonicalX[b]));
}
