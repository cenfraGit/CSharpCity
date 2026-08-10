using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// A* over the road network, reused rather than reallocated.
/// </summary>
/// <remarks>
/// Cost is time, not distance — <c>length / speed limit</c>. That one choice is why cars prefer a
/// boulevard to a shortcut through alleys, and why they will climb a ramp to run a deck when the
/// trip is long enough, without any of it being special-cased.
///
/// The heuristic is straight-line distance divided by the fastest limit anywhere in the city, which
/// can never overestimate the time remaining, so A* still returns genuinely optimal routes.
///
/// Nothing is cleared between queries. A generation stamp makes the score arrays look empty at the
/// start of each search, which matters because the arrays are the size of the whole city and there
/// are a couple of hundred cars asking.
/// </remarks>
internal sealed class Pathfinder
{
    /// <summary>A ceiling, not a budget. Nothing in a connected city comes close.</summary>
    const int MaxExpansions = 20_000;

    readonly RoadGraph _graph;
    readonly float[] _cost;
    readonly int[] _cameFromEdge;
    readonly int[] _stamp;
    readonly int[] _heapNode;
    readonly float[] _heapScore;
    int _generation;
    int _heapCount;

    public int LastExpansions { get; private set; }

    public Pathfinder(RoadGraph graph)
    {
        _graph = graph;
        int nodes = Math.Max(1, graph.Nodes.Length);
        _cost = new float[nodes];
        _cameFromEdge = new int[nodes];
        _stamp = new int[nodes];
        _heapNode = new int[nodes + 1];
        _heapScore = new float[nodes + 1];
    }

    /// <summary>Fills <paramref name="route"/> with edge indices from start to goal.</summary>
    public bool TryFind(int from, int to, List<int> route)
    {
        route.Clear();
        if (from < 0 || to < 0 || from >= _graph.Nodes.Length || to >= _graph.Nodes.Length)
            return false;
        if (_graph.Nodes[from].Component != _graph.Nodes[to].Component) return false;
        if (from == to) return true;

        _generation++;
        _heapCount = 0;
        LastExpansions = 0;

        var goal = _graph.Nodes[to].Position;
        Visit(from, 0f, -1, Heuristic(_graph.Nodes[from].Position, goal));

        while (_heapCount > 0)
        {
            int node = Pop();
            if (node == to) return Unwind(from, to, route);
            if (++LastExpansions > MaxExpansions) return false;

            float here = _cost[node];
            foreach (int edge in _graph.IncidentEdges(node))
            {
                var road = _graph.Edges[edge];
                int next = road.A == node ? road.B : road.A;
                // A one-way road refuses entry from the wrong end.
                if ((road.A == node ? road.LanesAB : road.LanesBA) == 0) continue;

                float candidate = here + road.Length / MathF.Max(road.SpeedLimit, 0.5f);
                if (_stamp[next] == _generation && _cost[next] <= candidate) continue;

                Visit(next, candidate, edge,
                    candidate + Heuristic(_graph.Nodes[next].Position, goal));
            }
        }

        return false;
    }

    bool Unwind(int from, int to, List<int> route)
    {
        for (int node = to; node != from;)
        {
            int edge = _cameFromEdge[node];
            if (edge < 0) return false;
            route.Add(edge);
            node = _graph.Other(edge, node);
        }
        route.Reverse();
        return route.Count > 0;
    }

    float Heuristic(Vector3 from, Vector3 to) =>
        Vector3.Distance(from, to) / MathF.Max(_graph.MaxSpeedLimit, 0.5f);

    void Visit(int node, float cost, int cameFromEdge, float score)
    {
        _stamp[node] = _generation;
        _cost[node] = cost;
        _cameFromEdge[node] = cameFromEdge;
        Push(node, score);
    }

    void Push(int node, float score)
    {
        int at = ++_heapCount;
        if (at >= _heapNode.Length) { _heapCount--; return; }   // full: the ceiling has done its job

        _heapNode[at] = node;
        _heapScore[at] = score;
        while (at > 1 && _heapScore[at >> 1] > _heapScore[at])
        {
            Swap(at, at >> 1);
            at >>= 1;
        }
    }

    int Pop()
    {
        int best = _heapNode[1];
        _heapNode[1] = _heapNode[_heapCount];
        _heapScore[1] = _heapScore[_heapCount];
        _heapCount--;

        int at = 1;
        while (true)
        {
            int left = at << 1, right = left + 1, smallest = at;
            if (left <= _heapCount && _heapScore[left] < _heapScore[smallest]) smallest = left;
            if (right <= _heapCount && _heapScore[right] < _heapScore[smallest]) smallest = right;
            if (smallest == at) break;
            Swap(at, smallest);
            at = smallest;
        }
        return best;
    }

    void Swap(int a, int b)
    {
        (_heapNode[a], _heapNode[b]) = (_heapNode[b], _heapNode[a]);
        (_heapScore[a], _heapScore[b]) = (_heapScore[b], _heapScore[a]);
    }
}
