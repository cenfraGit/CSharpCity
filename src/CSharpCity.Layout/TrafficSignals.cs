using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>One signal head facing one approach. The lamps are coloured per frame, not baked.</summary>
public readonly record struct SignalHead(Vector3 Lamps, float Yaw, int SignalIndex,
    bool ApproachRunsAlongX);

/// <summary>
/// The hardware: signal heads where two through-routes cross, give-way signs everywhere else.
/// </summary>
/// <remarks>
/// The rules already existed and were doing their job — cars stopped, queued and gave way — but
/// nothing in the city said why. A car halted at an empty crossroads for no visible reason reads
/// as a bug rather than as a red light, and there was no way to tell a junction that yields from
/// one that doesn't without watching it for a full cycle.
///
/// Signals are deliberately sparse. They go only where a road that matters crosses another road
/// that matters, which is both what a real network does and what keeps a thousand blinking lamps
/// out of the alleys. Every other junction that needs an order of precedence gets a sign on the
/// minor approach instead, which costs nothing per frame and says the same thing.
/// </remarks>
internal static class TrafficSignals
{
    const float PostHeight = 3.6f;
    /// <summary>How far back from the crossing the head stands, past the stop line.</summary>
    const float Setback = 1.4f;

    static readonly Vector4 Steel = new(0.20f, 0.21f, 0.23f, 1f);
    static readonly Vector4 SignRed = new(0.68f, 0.11f, 0.10f, 1f);

    public sealed record Result(int Signals, int Heads, int Signs);

    public static Result Build(SceneGraph scene, RoadGraph graph)
    {
        if (graph.IsEmpty) return new Result(0, 0, 0);

        int heads = 0, signs = 0;

        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].IncidentCount < 3) continue;
            if (graph.Nodes[node].Kind is RoadNodeKind.RampTop or RoadNodeKind.DeckJoint) continue;

            int signal = graph.Nodes[node].SignalIndex;
            if (signal >= 0) heads += AddHeads(scene, graph, node, signal);
            else signs += AddGiveWaySigns(scene, graph, node);
        }

        return new Result(graph.Signals.Length, heads, signs);
    }

    /// <summary>A head on the near right of every approach, where a driver would look for one.</summary>
    static int AddHeads(SceneGraph scene, RoadGraph graph, int node, int signal)
    {
        var at = graph.Nodes[node].Position;
        int added = 0;

        foreach (int edge in graph.IncidentEdges(node))
        {
            var road = graph.Edges[edge];
            if (road.Kind is not (RoadKind.Boulevard or RoadKind.Street)) continue;

            // Facing back down the approach, so a car arriving sees the lamps.
            var inbound = Vector3.Normalize(at - graph.Nodes[graph.Other(edge, node)].Position);
            var right = Vector3.Cross(inbound, Vector3.UnitY);

            float back = CrossingHalfWidth(graph, node, graph.RunsAlongX(edge)) + Setback;
            var foot = at - inbound * back + right * (road.Width * 0.5f + 1.0f);
            if (!Clear(graph, foot)) continue;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = foot with { Y = CityLayout.StreetSurfaceY },
                Size = new Vector3(0.18f, PostHeight, 0.18f),
                Color = Steel,
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Sidewalks,
            });

            // The housing is baked; only the three lamps inside it change, and those are emitted
            // per frame by the renderer from the signal's phase.
            var lamps = foot with { Y = CityLayout.StreetSurfaceY + PostHeight };
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = lamps,
                Size = new Vector3(0.46f, 1.35f, 0.46f),
                Color = new Vector4(0.12f, 0.13f, 0.14f, 1f),
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Sidewalks,
            });

            scene.SignalHeads.Add(new SignalHead(lamps,
                MathF.Atan2(-inbound.Z, -inbound.X), signal, graph.RunsAlongX(edge)));
            added++;
        }

        return added;
    }

    /// <summary>
    /// A give-way sign on each minor approach to an unsignalised crossing.
    /// </summary>
    /// <remarks>
    /// This mirrors the rule the simulation already follows: yield to anything on a more important
    /// road. Putting a sign only on the approaches that actually have to yield means the signs are
    /// readable as a statement about the junction rather than decoration — where there is no sign,
    /// nothing gives way.
    /// </remarks>
    static int AddGiveWaySigns(SceneGraph scene, RoadGraph graph, int node)
    {
        var at = graph.Nodes[node].Position;

        // The most important road through here; anything less important yields to it.
        var best = RoadKind.Connector;
        foreach (int edge in graph.IncidentEdges(node))
            if (graph.Edges[edge].Kind < best) best = graph.Edges[edge].Kind;

        int added = 0;
        foreach (int edge in graph.IncidentEdges(node))
        {
            var road = graph.Edges[edge];
            if (road.Kind <= best) continue;                       // this one has priority
            if (road.Kind is RoadKind.HighwayRamp or RoadKind.HighwayDeck) continue;
            if (road.Length < 10f) continue;                       // no room to see it

            var inbound = Vector3.Normalize(at - graph.Nodes[graph.Other(edge, node)].Position);
            var right = Vector3.Cross(inbound, Vector3.UnitY);

            float back = CrossingHalfWidth(graph, node, graph.RunsAlongX(edge)) + Setback;
            var foot = at - inbound * back + right * (road.Width * 0.5f + 0.7f);
            if (!Clear(graph, foot)) continue;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = foot with { Y = CityLayout.StreetSurfaceY },
                Size = new Vector3(0.12f, 2.2f, 0.12f),
                Color = Steel,
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Sidewalks,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = foot with { Y = CityLayout.StreetSurfaceY + 2.2f },
                Size = new Vector3(0.7f, 0.7f, 0.08f),
                Color = SignRed,
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Sidewalks,
            });
            added++;
        }

        return added;
    }

    /// <summary>
    /// Whether a post can stand here without being in a carriageway.
    /// </summary>
    /// <remarks>
    /// Offsetting from the road being signalled is not enough on its own. A junction has several
    /// roads meeting at once, and a head placed clear of its own approach can still land in the
    /// mouth of a third one arriving from the side. Where there is genuinely no room, the approach
    /// simply goes unsignposted — a missing head reads as unremarkable, a head in the middle of the
    /// road does not.
    /// </remarks>
    static bool Clear(RoadGraph graph, Vector3 at)
    {
        if (!graph.TryNearestEdge(at with { Y = CityLayout.StreetSurfaceY }, 30f,
                out int edge, out float along)) return true;

        var centreline = graph.PointOn(edge, along);
        float gap = MathF.Sqrt(MathF.Pow(at.X - centreline.X, 2)
                             + MathF.Pow(at.Z - centreline.Z, 2));
        return gap >= graph.Edges[edge].Width * 0.5f + 0.35f;
    }

    static float CrossingHalfWidth(RoadGraph graph, int node, bool alongX)
    {
        float widest = 0f;
        foreach (int edge in graph.IncidentEdges(node))
            if (graph.RunsAlongX(edge) != alongX)
                widest = MathF.Max(widest, graph.Edges[edge].Width);
        return widest * 0.5f;
    }
}
