using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// Paints the road network: one slab per stretch of road, one patch per junction, all at the same
/// height.
/// </summary>
/// <remarks>
/// The tarmac used to be emitted cut by cut, as the treemap produced them, and two things went
/// wrong with that.
///
/// Junction patches were laid half a metre <em>above</em> the roads they covered, to stop lane
/// markings running into a crossing. From a pavement that reads as a step; from the air it read as
/// a shimmering grid, and a car crossing a junction was driving under its own road surface.
///
/// Worse, patches were deduplicated first-come-first-served on a decimetre grid. Blocks are laid
/// before districts, so a three-and-a-half-metre alley routinely claimed a corner before the
/// eighteen-metre boulevard that shared it — and the boulevard got an alley-sized patch, leaving
/// the unpaved notches at exactly the junctions you would look at first.
///
/// Deriving both from the finished graph fixes both at once. A junction patch is sized from the
/// widest road that actually arrives at it, so it cannot be too small; and each road is trimmed by
/// exactly the half-width its junctions occupy, so slab and patch meet without ever overlapping.
/// No dedup table, no priority, no coplanar surfaces to fight.
/// </remarks>
internal static class RoadSurfaces
{
    static readonly Vector4 Asphalt = new(0.13f, 0.13f, 0.145f, 1f);

    public static void Emit(SceneGraph scene, RoadGraph graph)
    {
        if (graph.IsEmpty) return;

        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].IncidentCount < 2) continue;
            // A ramp foot is where a pitched deck meets the ground; paving it flat would cut a
            // step across the slope.
            if (graph.Nodes[node].Kind is RoadNodeKind.RampTop or RoadNodeKind.DeckJoint) continue;

            // A crossing is paved over exactly where the two corridors overlap: as wide in X as the
            // road running along Z, and as deep in Z as the road running along X. A square of the
            // widest road would be simpler and wrong — where two narrow side roads meet a wide one
            // a few metres apart, their square patches overlap each other and flicker, which is the
            // mismatched tarmac you see at a junction.
            var (alongX, alongZ) = Corridors(graph, node);
            if (alongX < 0.4f || alongZ < 0.4f) continue;

            scene.Roads.Add(new RoadQuad
            {
                Center = graph.Nodes[node].Position,
                Length = alongZ,
                Width = alongX,
                Yaw = 0f,
                Color = Asphalt,
                Flags = (uint)RoadFlags.None,
                Layer = LayerFor(RoadKindOf(graph, node)),
            });
        }

        for (int e = 0; e < graph.Edges.Length; e++)
        {
            var edge = graph.Edges[e];
            if (edge.Kind == RoadKind.Connector) continue;   // a topological stub, not tarmac
            // Decks and ramps come with parapets, pillars and portals, so Highways draws its own
            // concrete; painting it a second time here would stack two slabs in the same place.
            if (edge.Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;
            if (edge.Width < 0.4f) continue;

            // Give back exactly what the junctions at each end already cover.
            bool runsAlongX = graph.RunsAlongX(e);
            float trimA = HalfPatch(graph, edge.A, runsAlongX);
            float trimB = HalfPatch(graph, edge.B, runsAlongX);
            float length = edge.Length - trimA - trimB;
            if (length < 0.6f) continue;

            var a = graph.Nodes[edge.A].Position;
            var b = graph.Nodes[edge.B].Position;
            var direction = Vector3.Normalize(b - a);
            var centre = a + direction * (trimA + length * 0.5f);

            var flags = RoadFlags.EdgeLines;
            // Alleys are single-lane; anything wider gets a centre line.
            if (edge.Kind != RoadKind.Alley) flags |= RoadFlags.DashedCenter;

            scene.Roads.Add(new RoadQuad
            {
                Center = centre,
                Length = length,
                Width = edge.Width,
                Yaw = MathF.Atan2(direction.Z, direction.X),
                Pitch = edge.Pitch,
                Color = Asphalt,
                Flags = (uint)flags,
                Layer = LayerFor(edge.Kind),
            });
        }
    }

    /// <summary>
    /// How far the junction at a node reaches along a road arriving on a given axis — that is, half
    /// the width of the roads crossing it.
    /// </summary>
    static float HalfPatch(RoadGraph graph, int node, bool runsAlongX)
    {
        if (graph.Nodes[node].IncidentCount < 2) return 0f;
        if (graph.Nodes[node].Kind is RoadNodeKind.RampTop or RoadNodeKind.DeckJoint) return 0f;

        var (alongX, alongZ) = Corridors(graph, node);
        if (alongX < 0.4f || alongZ < 0.4f) return 0f;
        // A road running along X is trimmed by the depth of the patch in X, which is set by the
        // road running along Z.
        return (runsAlongX ? alongZ : alongX) * 0.5f;
    }

    /// <summary>
    /// The widest road at this node running along each axis. Zero on an axis nothing arrives on,
    /// which is how a road that simply passes straight through avoids being given a patch at all.
    /// </summary>
    static (float AlongX, float AlongZ) Corridors(RoadGraph graph, int node)
    {
        float alongX = 0f, alongZ = 0f;
        foreach (int edge in graph.IncidentEdges(node))
        {
            float width = graph.Edges[edge].Width;
            if (graph.RunsAlongX(edge)) alongX = MathF.Max(alongX, width);
            else alongZ = MathF.Max(alongZ, width);
        }
        return (alongX, alongZ);
    }

    /// <summary>The most important road at a junction decides which layer its patch belongs to.</summary>
    static RoadKind RoadKindOf(RoadGraph graph, int node)
    {
        var best = RoadKind.Alley;
        foreach (int edge in graph.IncidentEdges(node))
            if (graph.Edges[edge].Kind < best) best = graph.Edges[edge].Kind;
        return best;
    }

    static CityLayer LayerFor(RoadKind kind) =>
        kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp ? CityLayer.Highways : CityLayer.Always;
}
