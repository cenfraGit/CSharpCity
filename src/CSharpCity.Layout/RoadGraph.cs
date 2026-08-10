using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>How wide, how fast, and how important a stretch of road is.</summary>
public enum RoadKind : byte
{
    /// <summary>Between projects. The widest roads in the city.</summary>
    Boulevard,
    /// <summary>Between namespaces inside a project.</summary>
    Street,
    /// <summary>Inside a namespace, between lots.</summary>
    Alley,
    /// <summary>The pitched climb between a street and an elevated deck.</summary>
    HighwayRamp,
    /// <summary>The elevated carriageway itself.</summary>
    HighwayDeck,
    /// <summary>A short stub joining a ramp foot to the street network. Never drawn.</summary>
    Connector,
}

public enum RoadNodeKind : byte
{
    Junction,
    /// <summary>A road that simply stops. Nothing to give way to, nothing to turn into.</summary>
    Terminal,
    RampFoot,
    RampTop,
    DeckJoint,
}

/// <summary>A junction. Its Y <em>is</em> the drivable surface there — there is no second source.</summary>
public readonly struct RoadNode
{
    public readonly Vector3 Position;
    public readonly RoadNodeKind Kind;
    /// <summary>Offset into <see cref="RoadGraph.Incident"/>.</summary>
    public readonly int FirstIncident;
    public readonly int IncidentCount;
    /// <summary>Index into <see cref="RoadGraph.Signals"/>, or -1 where traffic is unsignalised.</summary>
    public readonly int SignalIndex;
    public readonly int Component;

    public RoadNode(Vector3 position, RoadNodeKind kind, int firstIncident, int incidentCount,
        int signalIndex, int component)
    {
        Position = position;
        Kind = kind;
        FirstIncident = firstIncident;
        IncidentCount = incidentCount;
        SignalIndex = signalIndex;
        Component = component;
    }
}

/// <summary>One stretch of road between two junctions. A to B is the "forward" direction.</summary>
public readonly struct RoadEdge
{
    public readonly int A, B;
    public readonly float Length;
    public readonly float Width;
    public readonly RoadKind Kind;
    /// <summary>Lanes in each direction. Zero on one side makes the road one-way.</summary>
    public readonly byte LanesAB, LanesBA;
    /// <summary>Metres per second.</summary>
    public readonly float SpeedLimit;
    /// <summary>Radians of climb from A to B. Zero everywhere except ramps.</summary>
    public readonly float Pitch;

    public RoadEdge(int a, int b, float length, float width, RoadKind kind, byte lanesAb,
        byte lanesBa, float speedLimit, float pitch)
    {
        A = a;
        B = b;
        Length = length;
        Width = width;
        Kind = kind;
        LanesAB = lanesAb;
        LanesBA = lanesBa;
        SpeedLimit = speedLimit;
        Pitch = pitch;
    }
}

/// <summary>
/// A fixed-time traffic signal.
/// </summary>
/// <remarks>
/// There is no stored state here, and that is the whole design. Green is a pure function of the
/// clock, so a light can never be waiting on a car that is waiting on the light: the phase turns
/// over on a timer whatever the traffic does. Combined with cars that only ever choose to brake —
/// never to reserve — there is no hold-and-wait, and therefore no deadlock to debug.
///
/// Phase 0 serves approaches running along X, phase 1 those running along Z. Every edge in the
/// network is axis-aligned by construction, so each approach belongs to exactly one phase and the
/// two can never both be green.
/// </remarks>
public readonly struct Signal
{
    public readonly int NodeIndex;
    public readonly float GreenSeconds;
    public readonly float AmberSeconds;
    /// <summary>Stagger, so the city doesn't blink in unison.</summary>
    public readonly float Offset;

    public Signal(int nodeIndex, float greenSeconds, float amberSeconds, float offset)
    {
        NodeIndex = nodeIndex;
        GreenSeconds = greenSeconds;
        AmberSeconds = amberSeconds;
        Offset = offset;
    }

    public float Cycle => (GreenSeconds + AmberSeconds) * 2f;

    /// <summary>0 while X-running approaches are served, 1 while Z-running ones are.</summary>
    public int PhaseAt(float time)
    {
        float cycle = Cycle;
        float t = time + Offset;
        t -= MathF.Floor(t / cycle) * cycle;
        return t < GreenSeconds + AmberSeconds ? 0 : 1;
    }

    public bool IsGreen(float time, bool approachRunsAlongX)
    {
        float cycle = Cycle;
        float t = time + Offset;
        t -= MathF.Floor(t / cycle) * cycle;
        float within = PhaseAt(time) == 0 ? t : t - (GreenSeconds + AmberSeconds);
        return PhaseAt(time) == (approachRunsAlongX ? 0 : 1) && within < GreenSeconds;
    }

    /// <summary>True during the amber that ends this approach's green.</summary>
    public bool IsAmber(float time, bool approachRunsAlongX)
    {
        float cycle = Cycle;
        float t = time + Offset;
        t -= MathF.Floor(t / cycle) * cycle;
        float within = PhaseAt(time) == 0 ? t : t - (GreenSeconds + AmberSeconds);
        return PhaseAt(time) == (approachRunsAlongX ? 0 : 1) && within >= GreenSeconds;
    }
}

/// <summary>
/// The one drivable network: every junction a node, every stretch of road an edge.
/// </summary>
/// <remarks>
/// Before this existed the city had three unconnected road graphs — the centrelines that fed the
/// ride, the ambient car loops, and the highway decks — none of which shared a single node. Nothing
/// could drive from one to another because, as far as the data was concerned, they were three
/// different cities drawn on top of each other.
/// </remarks>
public sealed class RoadGraph
{
    public RoadNode[] Nodes = Array.Empty<RoadNode>();
    public RoadEdge[] Edges = Array.Empty<RoadEdge>();
    /// <summary>Edge indices grouped by node; see <see cref="RoadNode.FirstIncident"/>.</summary>
    public int[] Incident = Array.Empty<int>();
    public Signal[] Signals = Array.Empty<Signal>();
    /// <summary>The component holding the most road. Cars only ever spawn and aim inside it.</summary>
    public int MainComponent;
    /// <summary>Highest limit anywhere, so the A* heuristic can stay admissible.</summary>
    public float MaxSpeedLimit = 1f;

    // A uniform grid over the edges, as CSR. Long boulevards are stamped into every cell they
    // cross, so a lookup in the middle of one still finds it.
    internal float GridOriginX, GridOriginZ;
    internal float GridCell = 32f;
    internal int GridColumns, GridRows;
    internal int[] CellStart = Array.Empty<int>();
    internal int[] CellItems = Array.Empty<int>();

    /// <summary>Nodes on each constant-X line and each constant-Z line, sorted along the line.</summary>
    internal float[] LineX = Array.Empty<float>();
    internal float[] LineZ = Array.Empty<float>();
    internal int[][] NodesOnLineX = Array.Empty<int[]>();
    internal int[][] NodesOnLineZ = Array.Empty<int[]>();

    public bool IsEmpty => Edges.Length == 0;

    public ReadOnlySpan<int> IncidentEdges(int node)
    {
        var n = Nodes[node];
        return Incident.AsSpan(n.FirstIncident, n.IncidentCount);
    }

    public int Other(int edge, int node)
    {
        var e = Edges[edge];
        return e.A == node ? e.B : e.A;
    }

    /// <summary>How much tarmac a junction needs: enough for the widest road arriving at it.</summary>
    public float MaxIncidentWidth(int node)
    {
        float widest = 0f;
        foreach (int edge in IncidentEdges(node)) widest = MathF.Max(widest, Edges[edge].Width);
        return widest;
    }

    /// <summary>A point a given distance along an edge from its A end. Y comes with it.</summary>
    public Vector3 PointOn(int edge, float along)
    {
        var e = Edges[edge];
        float t = e.Length > 1e-4f ? Math.Clamp(along / e.Length, 0f, 1f) : 0f;
        return Vector3.Lerp(Nodes[e.A].Position, Nodes[e.B].Position, t);
    }

    /// <summary>Unit heading along an edge; <paramref name="direction"/> +1 is A to B.</summary>
    public Vector3 DirectionOf(int edge, int direction)
    {
        var e = Edges[edge];
        var delta = Nodes[e.B].Position - Nodes[e.A].Position;
        if (delta.LengthSquared() < 1e-8f) return Vector3.UnitX;
        return Vector3.Normalize(delta) * (direction >= 0 ? 1f : -1f);
    }

    public int NearestNode(Vector3 point)
    {
        if (!TryNearestEdge(point, float.MaxValue, out int edge, out float along)) return -1;
        var e = Edges[edge];
        return along * 2f <= e.Length ? e.A : e.B;
    }

    /// <summary>
    /// The closest point on any road within <paramref name="maxDistance"/>, as an edge and a
    /// distance along it.
    /// </summary>
    public bool TryNearestEdge(Vector3 point, float maxDistance, out int edge, out float along)
    {
        edge = -1;
        along = 0f;
        if (Edges.Length == 0 || GridColumns == 0) return false;

        int cx = Math.Clamp((int)((point.X - GridOriginX) / GridCell), 0, GridColumns - 1);
        int cz = Math.Clamp((int)((point.Z - GridOriginZ) / GridCell), 0, GridRows - 1);
        int maxRing = Math.Max(GridColumns, GridRows);
        float best = maxDistance;

        for (int ring = 0; ring <= maxRing; ring++)
        {
            // Everything in a further ring is at least this far away, so once the best hit is
            // closer than the ring we are looking at, no later ring can beat it.
            if (edge >= 0 && (ring - 1) * GridCell > best) break;

            for (int z = cz - ring; z <= cz + ring; z++)
            {
                if (z < 0 || z >= GridRows) continue;
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    if (x < 0 || x >= GridColumns) continue;
                    // Only the shell of the ring is new.
                    if (ring > 0 && Math.Abs(x - cx) != ring && Math.Abs(z - cz) != ring) continue;

                    int cell = z * GridColumns + x;
                    for (int i = CellStart[cell]; i < CellStart[cell + 1]; i++)
                    {
                        int candidate = CellItems[i];
                        float distance = DistanceToEdge(candidate, point, out float at);
                        if (distance >= best) continue;
                        best = distance;
                        edge = candidate;
                        along = at;
                    }
                }
            }
        }

        return edge >= 0;
    }

    float DistanceToEdge(int edge, Vector3 point, out float along)
    {
        var e = Edges[edge];
        var a = Nodes[e.A].Position;
        var b = Nodes[e.B].Position;
        var delta = b - a;
        float lengthSquared = delta.LengthSquared();
        float t = lengthSquared > 1e-8f
            ? Math.Clamp(Vector3.Dot(point - a, delta) / lengthSquared, 0f, 1f)
            : 0f;
        along = t * e.Length;
        var closest = a + delta * t;
        // Horizontal distance: a car looking for its nearest road doesn't care that a deck is
        // fourteen metres over its head — but it must not be dragged onto one either, which is
        // what the caller's maxDistance and the component check are for.
        float dx = closest.X - point.X, dz = closest.Z - point.Z, dy = closest.Y - point.Y;
        return MathF.Sqrt(dx * dx + dz * dz + dy * dy * 4f);
    }

    /// <summary>
    /// Every node sitting on one straight line of the grid, ordered along it. Used to find where a
    /// highway can touch down without landing on a crossroads.
    /// </summary>
    public IReadOnlyList<int> NodesAlongLine(bool vertical, float position)
    {
        var coordinates = vertical ? LineX : LineZ;
        var lines = vertical ? NodesOnLineX : NodesOnLineZ;
        if (coordinates.Length == 0) return Array.Empty<int>();

        int index = Array.BinarySearch(coordinates, position);
        if (index < 0)
        {
            index = ~index;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int probe = index - 1; probe <= index; probe++)
            {
                if (probe < 0 || probe >= coordinates.Length) continue;
                float distance = MathF.Abs(coordinates[probe] - position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = probe;
            }
            if (best < 0 || bestDistance > 0.5f) return Array.Empty<int>();
            index = best;
        }

        return lines[index];
    }

    /// <summary>Whether an edge runs along X rather than Z. Signals key off this.</summary>
    public bool RunsAlongX(int edge)
    {
        var e = Edges[edge];
        return MathF.Abs(Nodes[e.B].Position.X - Nodes[e.A].Position.X)
             >= MathF.Abs(Nodes[e.B].Position.Z - Nodes[e.A].Position.Z);
    }
}
