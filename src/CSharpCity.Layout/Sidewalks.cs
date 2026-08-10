using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// Continuous pavement along every street, round every corner.
/// </summary>
/// <remarks>
/// A kerb is a box four tenths of a metre tall whose top face is exactly the road surface. That is
/// what explains the height the roads sit at: instead of a slab of tarmac hovering over the district
/// floor for reasons of depth precision, there is a pavement, and the road is the gap between two
/// of them.
///
/// Continuity is the whole requirement, and the first attempt broke it in two ways. Kerbs were set
/// back from junctions and nothing filled the corner, so every crossroads had four empty squares in
/// it; and only boulevards and streets were paved, so any block bounded by an alley simply stopped
/// having a pavement partway round. Both are fixed by deriving the setbacks from the junction
/// geometry rather than from a constant, and by paving every road at ground level.
/// </remarks>
internal static class Sidewalks
{
    const float Kerb = 0.4f;
    const float PropSpacing = 16f;

    /// <summary>
    /// A ceiling on the whole layer. A large real-world solution can carry tens of thousands of
    /// boxes before any of this, and the startup sort is superlinear in the total.
    /// </summary>
    const int MaxBoxes = 30_000;

    static readonly Vector4 Paving = new(0.52f, 0.52f, 0.54f, 1f);

    public sealed record Result(int Kerbs, int Corners, int Props, int Skipped);

    /// <summary>
    /// How wide a pavement is beside a given road. An alley's is narrower because the gap between
    /// an alley and the lots it separates is narrower — a full-width pavement there would run over
    /// the front of the buildings.
    /// </summary>
    static float WidthFor(RoadKind kind) => kind switch
    {
        RoadKind.Boulevard => 2.0f,
        RoadKind.Street => 1.8f,
        _ => 1.0f,
    };

    static bool Paved(RoadKind kind) =>
        kind is RoadKind.Boulevard or RoadKind.Street or RoadKind.Alley;

    public static Result Build(SceneGraph scene, RoadGraph graph)
    {
        if (graph.IsEmpty) return new Result(0, 0, 0, 0);

        int kerbs = 0, corners = 0, props = 0, skipped = 0;
        int budget = MaxBoxes;

        // Widest first, so if the budget ever runs out it is an alley behind a warehouse that loses
        // its pavement rather than the boulevard through the middle of the city.
        var ordered = Enumerable.Range(0, graph.Edges.Length)
            .Where(e => Paved(graph.Edges[e].Kind))
            .OrderBy(e => graph.Edges[e].Kind)
            .ThenByDescending(e => graph.Edges[e].Length)
            .ToList();

        foreach (int e in ordered)
        {
            var edge = graph.Edges[e];
            bool alongX = graph.RunsAlongX(e);
            float sidewalk = WidthFor(edge.Kind);

            var a = graph.Nodes[edge.A].Position;
            var b = graph.Nodes[edge.B].Position;
            var along = Vector3.Normalize(b - a);
            var across = Vector3.Cross(along, Vector3.UnitY);

            for (int side = -1; side <= 1; side += 2)
            {
                if (budget < 1) { skipped++; continue; }

                // Absolute side of the road this pavement is on: +Z or -Z for a road running along
                // X, +X or -X for one running along Z.
                var offset = across * side;
                int facing = alongX
                    ? (offset.Z > 0f ? 1 : -1)
                    : (offset.X > 0f ? 1 : -1);

                float trimA = Setback(graph, edge.A, alongX, facing);
                float trimB = Setback(graph, edge.B, alongX, facing);
                float length = edge.Length - trimA - trimB;
                if (length < 0.6f) { skipped++; continue; }

                var centre = a + along * (trimA + length * 0.5f)
                             + offset * (edge.Width * 0.5f + sidewalk * 0.5f);

                scene.Boxes.Add(new BoxInstance
                {
                    // The top face lands exactly on the road surface, so the kerb reads as the
                    // pavement the road is cut into rather than a wall beside it.
                    BasePosition = centre with { Y = CityLayout.StreetSurfaceY - Kerb },
                    Size = Extent(alongX, length, Kerb, sidewalk),
                    Color = Paving,
                    PickId = -1,
                    Detail = 1f,
                    Layer = CityLayer.Sidewalks,
                });
                kerbs++;
                budget--;

                if (edge.Kind == RoadKind.Alley) continue;   // too narrow to furnish

                int added = Dress(scene, e, centre, along, offset, length, Math.Min(budget, 24));
                props += added;
                budget -= added;
            }
        }

        corners = FillCorners(scene, graph, ref budget);
        return new Result(kerbs, corners, props, skipped);
    }

    /// <summary>
    /// How far a pavement stops short of a junction: far enough to clear the crossing road, and
    /// nothing at all when no road crosses on that side.
    /// </summary>
    /// <remarks>
    /// The distinction matters at a T-junction, where the pavement on the far side of the main road
    /// runs straight past without a break — which is what a real one does, and what a constant
    /// setback got wrong by cutting a gap on both sides of every node.
    /// </remarks>
    static float Setback(RoadGraph graph, int node, bool alongX, int facing)
    {
        if (!HasArm(graph, node, crossing: !alongX, facing)) return 0f;

        // Clear the crossing road's own width, measured the same way the tarmac at this junction is.
        float widest = 0f;
        foreach (int edge in graph.IncidentEdges(node))
            if (graph.RunsAlongX(edge) != alongX)
                widest = MathF.Max(widest, graph.Edges[edge].Width);

        return widest * 0.5f;
    }

    /// <summary>Whether a road leaves this junction along the given axis, in the given direction.</summary>
    static bool HasArm(RoadGraph graph, int node, bool crossing, int direction)
    {
        var at = graph.Nodes[node].Position;

        foreach (int edge in graph.IncidentEdges(node))
        {
            if (!Paved(graph.Edges[edge].Kind)) continue;
            if (graph.RunsAlongX(edge) != crossing) continue;

            var other = graph.Nodes[graph.Other(edge, node)].Position;
            float delta = crossing ? other.X - at.X : other.Z - at.Z;
            if (MathF.Sign(delta) == direction) return true;
        }

        return false;
    }

    /// <summary>
    /// The square of pavement in each corner of a junction, joining the two roads' kerbs.
    /// </summary>
    /// <remarks>
    /// This is the empty square you see in the corner of every block without it. A corner belongs
    /// to a quadrant only if roads actually leave the junction in both of its directions, so a
    /// T-junction gets two corners and a crossroads four — putting one where there is no road would
    /// leave a lump of pavement standing on its own in a lot.
    /// </remarks>
    static int FillCorners(SceneGraph scene, RoadGraph graph, ref int budget)
    {
        int corners = 0;

        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].IncidentCount < 2) continue;
            if (graph.Nodes[node].Kind is RoadNodeKind.RampTop or RoadNodeKind.DeckJoint) continue;

            // The junction's own extent, and the pavement width to match it, taken across every
            // road arriving here so two different classes of road still meet flush.
            float alongX = 0f, alongZ = 0f, sidewalk = 0f;
            foreach (int edge in graph.IncidentEdges(node))
            {
                if (!Paved(graph.Edges[edge].Kind)) continue;
                float width = graph.Edges[edge].Width;
                if (graph.RunsAlongX(edge)) alongX = MathF.Max(alongX, width);
                else alongZ = MathF.Max(alongZ, width);
                sidewalk = MathF.Max(sidewalk, WidthFor(graph.Edges[edge].Kind));
            }

            if (alongX < 0.4f || alongZ < 0.4f || sidewalk <= 0f) continue;

            var at = graph.Nodes[node].Position;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                if (budget < 1) continue;
                if (!HasArm(graph, node, crossing: true, sx)) continue;
                if (!HasArm(graph, node, crossing: false, sz)) continue;

                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = new Vector3(
                        at.X + sx * (alongZ * 0.5f + sidewalk * 0.5f),
                        CityLayout.StreetSurfaceY - Kerb,
                        at.Z + sz * (alongX * 0.5f + sidewalk * 0.5f)),
                    Size = new Vector3(sidewalk, Kerb, sidewalk),
                    Color = Paving,
                    PickId = -1,
                    Detail = 1f,
                    Layer = CityLayer.Sidewalks,
                });
                corners++;
                budget--;
            }
        }

        return corners;
    }

    /// <summary>Trees, benches and lamp posts along a stretch of pavement.</summary>
    static int Dress(SceneGraph scene, int edge, Vector3 centre, Vector3 along, Vector3 outward,
        float length, int budget)
    {
        int slots = (int)(length / PropSpacing);
        if (slots <= 0 || budget <= 0) return 0;

        int added = 0;
        float top = CityLayout.StreetSurfaceY;

        for (int i = 0; i < slots && added + 3 <= budget; i++)
        {
            float offset = (i + 0.5f) / slots * length - length * 0.5f;
            var at = centre + along * offset;
            float roll = StableHash.Unit(edge, i, 17);

            if (roll < 0.42f)
            {
                Greenery.AddTree(scene, at with { Y = top }, 3.2f + roll * 2f, 1f,
                    CityLayer.Sidewalks);
                added += 3;
            }
            else if (roll < 0.68f)
            {
                added += Bench(scene, at with { Y = top }, along, outward);
            }
            else if (roll < 0.88f)
            {
                added += LampPost(scene, at with { Y = top });
            }
            else
            {
                added += Box(scene, at with { Y = top }, new Vector3(0.5f, 0.85f, 0.5f),
                    new Vector4(0.24f, 0.28f, 0.30f, 1f));
            }
        }

        return added;
    }

    static int Bench(SceneGraph scene, Vector3 at, Vector3 along, Vector3 outward)
    {
        int n = Box(scene, at, Extent(MathF.Abs(along.X) > 0.5f, 1.6f, 0.45f, 0.5f),
            new Vector4(0.35f, 0.24f, 0.15f, 1f));
        n += Box(scene, (at + outward * 0.22f) with { Y = at.Y + 0.45f },
            Extent(MathF.Abs(along.X) > 0.5f, 1.6f, 0.5f, 0.12f),
            new Vector4(0.32f, 0.22f, 0.14f, 1f));
        return n;
    }

    /// <summary>How far a lamp throws light across the pavement and the kerb.</summary>
    const float LampReach = 5.5f;

    static int LampPost(SceneGraph scene, Vector3 at)
    {
        int n = Box(scene, at, new Vector3(0.16f, 3.6f, 0.16f), new Vector4(0.20f, 0.21f, 0.23f, 1f));

        // The lamp itself is deliberately dim. A bright emissive box is pushed hard through the
        // bloom pass, and a street of them turns into a row of smeared blobs — the light spilling
        // out of the lamp rather than onto anything. Most of the brightness now lives in the pool
        // it casts, which is where a street lamp's light is supposed to be.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = at with { Y = at.Y + 3.6f },
            Size = new Vector3(0.38f, 0.2f, 0.38f),
            Color = new Vector4(0.60f, 0.55f, 0.40f, 1f),
            PickId = -1,
            Detail = 1f,
            Flags = (uint)BoxFlags.Emissive,
            Layer = CityLayer.Sidewalks,
        });

        scene.Roads.Add(new RoadQuad
        {
            // Just clear of the pavement it lies on. Translucent, so it never writes depth and
            // blends over whatever it falls across.
            Center = at with { Y = CityLayout.StreetSurfaceY + 0.06f },
            Length = LampReach * 2f,
            Width = LampReach * 2f,
            Yaw = 0f,
            Color = new Vector4(1.0f, 0.88f, 0.62f, 0.55f),
            Flags = (uint)RoadFlags.LightPool,
            Layer = CityLayer.Sidewalks,
        });

        return n + 1;
    }

    static int Box(SceneGraph scene, Vector3 at, Vector3 size, Vector4 colour)
    {
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = at,
            Size = size,
            Color = colour,
            PickId = -1,
            Detail = 1f,
            Layer = CityLayer.Sidewalks,
        });
        return 1;
    }

    /// <summary>Sizes a box that runs along a road, whichever axis that road happens to be on.</summary>
    static Vector3 Extent(bool alongX, float length, float height, float across) =>
        alongX ? new Vector3(length, height, across) : new Vector3(across, height, length);
}
