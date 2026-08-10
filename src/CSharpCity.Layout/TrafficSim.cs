using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>One car, as the renderer needs to see it.</summary>
public struct CarAgent
{
    public int Id;
    /// <summary>Already offset into its lane and sitting on the road surface.</summary>
    public Vector3 Position;
    public float Yaw;
    /// <summary>Radians of climb. Non-zero only on a highway ramp.</summary>
    public float Pitch;
    public float Speed;
    public bool IsTruck;
    /// <summary>The car the camera is riding in, if any.</summary>
    public bool IsPov;
    public Vector4 Color;
}

public readonly record struct SimStats(int Population, int Arrived, int Despawned, int Waiting,
    int Unroutable);

/// <summary>
/// Live traffic: cars that start at a building, drive somewhere else, and stop existing when they
/// get there.
/// </summary>
/// <remarks>
/// What was here before was not a simulation at all. Every car's position was a closed-form
/// function of the clock — phase, times speed, along a two-point path — so cars slid back and forth
/// along one street forever, passed through each other, and reversed direction every time they
/// reached an end, because a non-looping path was played as a triangle wave. Nothing could turn a
/// corner, and nothing had anywhere to be.
///
/// This lives in the layout assembly rather than the renderer on purpose. It touches no OpenGL and
/// no frame state, takes a plain <c>dt</c>, and is therefore something a test can drive for five
/// simulated minutes and make assertions about — which is the only practical way to know that cars
/// do not overlap, do not deadlock at a red light, and do actually arrive.
/// </remarks>
public sealed class TrafficSim
{
    /// <summary>Fixed step. Car-following is only unconditionally stable if the step is bounded.</summary>
    const float Tick = 1f / 60f;
    const int MaxTicksPerCall = 8;

    // Intelligent Driver Model. These are the standard parameters for urban traffic; between them
    // they give a car that keeps a sane gap, brakes smoothly, and queues nose to tail at a red
    // light without any of that being written down anywhere as a special case.
    const float Acceleration = 1.6f;
    const float Comfortable = 2.2f;
    const float StandstillGap = 2.2f;
    const float HeadwaySeconds = 1.1f;
    const float CarLength = 2.3f;
    const float TruckLength = 3.6f;

    /// <summary>How far ahead a car looks past the end of its current road.</summary>
    const float LookAhead = 40f;
    /// <summary>Route requests served per tick. Demand is a fraction of this; see Step.</summary>
    const int RoutesPerTick = 4;

    readonly RoadGraph _graph;
    readonly IReadOnlyList<CarSpawn> _spawns;
    readonly Pathfinder _pathfinder;
    readonly int _seed;

    // Structure-of-arrays: a couple of hundred cars stepped sixty times a second, with no
    // allocation once the city is up.
    int[] _edge = Array.Empty<int>();
    int[] _direction = Array.Empty<int>();
    float[] _along = Array.Empty<float>();
    float[] _speed = Array.Empty<float>();
    bool[] _alive = Array.Empty<bool>();
    bool[] _truck = Array.Empty<bool>();
    bool[] _pov = Array.Empty<bool>();
    bool[] _cruise = Array.Empty<bool>();
    /// <summary>Queued for a route and not driving yet. Sitting at the kerb, not blocking anything.</summary>
    bool[] _waiting = Array.Empty<bool>();
    int[] _destination = Array.Empty<int>();
    Vector4[] _color = Array.Empty<Vector4>();
    List<int>[] _route = Array.Empty<List<int>>();
    int[] _leg = Array.Empty<int>();

    /// <summary>Intrusive lane lists: the car ahead, found without searching for it.</summary>
    int[] _laneHead = Array.Empty<int>();
    int[] _next = Array.Empty<int>();

    readonly Stack<int> _free = new();
    readonly Queue<int> _awaitingRoute = new();
    readonly List<CarAgent> _visible = new();
    float _carry;
    int _spawnCounter;
    int _arrived, _despawned, _unroutable;

    public TrafficSim(RoadGraph graph, IReadOnlyList<CarSpawn> spawns, int seed = 20250607)
    {
        _graph = graph;
        _spawns = spawns;
        _seed = seed;
        _pathfinder = new Pathfinder(graph);
        Resize(Math.Max(16, TargetPopulation * 2));
        _laneHead = new int[Math.Max(1, graph.Edges.Length) * 2];
        Array.Fill(_laneHead, -1);
    }

    /// <summary>How many cars the city tries to keep on the road. Tune by measurement, not taste.</summary>
    public int TargetPopulation { get; set; } = 220;

    /// <summary>1, 2, 4, 8 — the fast-forward. Implemented as more ticks, never a bigger one.</summary>
    public float TimeScale { get; set; } = 1f;

    public float Now { get; private set; }

    public bool CanDrive => _graph.Edges.Length > 0 && _spawns.Count > 1;

    public SimStats Stats => new(_alive.Count(a => a), _arrived, _despawned, _awaitingRoute.Count,
        _unroutable);

    public void Step(float deltaTime)
    {
        if (!CanDrive) return;

        _carry += Math.Clamp(deltaTime, 0f, 0.25f) * MathF.Max(TimeScale, 0f);
        int ticks = 0;
        while (_carry >= Tick && ticks < MaxTicksPerCall * (int)MathF.Max(1f, TimeScale))
        {
            _carry -= Tick;
            ticks++;
            Tock();
        }
        // Never let the backlog grow without bound; a stalled frame must not become slow motion.
        if (_carry > Tick * 4f) _carry = 0f;
    }

    void Tock()
    {
        Now += Tick;
        ServeRouteRequests();
        TopUpPopulation();

        for (int car = 0; car < _alive.Length; car++)
        {
            // A car still waiting for a route sits at the kerb; one that has been given an empty
            // route was already where it was going.
            if (!_alive[car] || _waiting[car]) continue;
            if (_route[car].Count == 0) { Despawn(car, arrived: true); continue; }
            Advance(car);
        }
    }

    // ---- population -------------------------------------------------------------------------

    void TopUpPopulation()
    {
        int living = 0;
        for (int i = 0; i < _alive.Length; i++) if (_alive[i]) living++;
        // A couple per tick: a city that refills instantly after a jam looks like a spawner.
        int wanted = Math.Min(2, TargetPopulation - living - _awaitingRoute.Count);

        for (int i = 0; i < wanted; i++)
        {
            int from = NextRandom(_spawns.Count);
            int to = NextRandom(_spawns.Count);
            if (from == to) continue;
            TrySpawn(_spawns[from], to, pov: false, cruise: false, out _);
        }
    }

    int NextRandom(int count) =>
        count <= 0 ? -1 : (int)(StableHash.Unit(_seed, _spawnCounter++) * count) % count;

    bool TrySpawn(CarSpawn spawn, int destination, bool pov, bool cruise, out int id)
    {
        id = -1;
        int direction = StableHash.Unit(_seed + 7, _spawnCounter++) < 0.5f ? 1 : -1;
        // Both lanes, not just the one it starts in. A car whose route turns out to lead back the
        // way it was pointing performs a U-turn as soon as it is routed, so the lane it ends up in
        // is not known yet — and checking only the near one lets two cars spawn in opposite lanes,
        // both turn round, and end up sharing a spot.
        if (!LaneIsClear(spawn.Edge, direction, spawn.Along)) return false;
        if (!LaneIsClear(spawn.Edge, -direction, spawn.Along)) return false;

        id = Take();
        _edge[id] = spawn.Edge;
        _direction[id] = direction;
        _along[id] = spawn.Along;
        _speed[id] = 0f;
        _alive[id] = true;
        _pov[id] = pov;
        _cruise[id] = cruise;
        _waiting[id] = true;
        _destination[id] = destination;
        float noise = StableHash.Unit(_seed + 13, id, _spawnCounter);
        _truck[id] = !pov && noise > 0.86f;
        _color[id] = pov ? new Vector4(0.92f, 0.86f, 0.42f, 1f) : CarColour(noise);
        _route[id].Clear();
        _leg[id] = 0;

        LaneInsert(id);
        _awaitingRoute.Enqueue(id);
        return true;
    }

    /// <summary>
    /// Turns queued cars into routed cars, a few at a time.
    /// </summary>
    /// <remarks>
    /// A car with no route yet is already on the road but stationary, which is fine and brief:
    /// at a couple of hundred cars on trips of a minute or two, demand is around three routes a
    /// second against a ceiling of two hundred and forty.
    /// </remarks>
    void ServeRouteRequests()
    {
        for (int served = 0; served < RoutesPerTick && _awaitingRoute.Count > 0; served++)
        {
            int car = _awaitingRoute.Dequeue();
            if (!_alive[car]) continue;
            _waiting[car] = false;
            if (!Route(car)) Despawn(car, arrived: false);
        }
    }

    bool Route(int car)
    {
        var target = _spawns[_destination[car]];
        int from = Ahead(car);
        int to = _graph.Edges[target.Edge].A;

        if (!_pathfinder.TryFind(from, to, _route[car]))
        {
            _unroutable++;
            return false;
        }
        _leg[car] = 0;

        // The search starts at the node the car is already heading for, so normally the route
        // simply continues from there and nothing about the car changes. The one exception is a
        // route whose first step is back down the road the car is on: that is a U-turn, and the
        // car has to be moved into the opposite lane for it.
        if (_route[car].Count > 0 && _route[car][0] == _edge[car])
        {
            LaneRemove(car);
            _direction[car] = -_direction[car];
            LaneInsert(car);
            _leg[car] = 1;
        }
        return true;
    }

    /// <summary>The node this car is driving towards.</summary>
    int Ahead(int car)
    {
        var edge = _graph.Edges[_edge[car]];
        return _direction[car] > 0 ? edge.B : edge.A;
    }

    int Behind(int car)
    {
        var edge = _graph.Edges[_edge[car]];
        return _direction[car] > 0 ? edge.A : edge.B;
    }

    // ---- driving ----------------------------------------------------------------------------

    void Advance(int car)
    {
        var edge = _graph.Edges[_edge[car]];
        float limit = edge.SpeedLimit;
        float travelled = _direction[car] > 0 ? _along[car] : edge.Length - _along[car];
        float remaining = edge.Length - travelled;

        float gap = float.MaxValue;
        float leaderSpeed = 0f;

        int leader = LaneLeader(car);
        if (leader >= 0)
        {
            float leaderTravelled = _direction[leader] > 0
                ? _along[leader] : edge.Length - _along[leader];
            gap = leaderTravelled - travelled - Length(leader);
            leaderSpeed = _speed[leader];
        }
        else if (remaining < LookAhead && _leg[car] < _route[car].Count)
        {
            // Look past the junction. Without this a car sees an empty road ahead right up to the
            // moment it turns onto one with stationary traffic on it, and drives into the back of
            // it — the queue at a red light is on the *far* side of the junction from everyone
            // still approaching.
            int nextEdge = _route[car][_leg[car]];
            int node = Ahead(car);
            int nextDirection = _graph.Edges[nextEdge].A == node ? 1 : -1;

            int tail = LaneTail(nextEdge, nextDirection);
            if (tail >= 0)
            {
                float across = remaining + Progress(tail) - Length(tail);
                if (across < gap) { gap = MathF.Max(across, 0.05f); leaderSpeed = _speed[tail]; }
            }
        }

        // A red light is just a stationary car parked on the stop line, which is why there is no
        // separate "stopping" state to get stuck in.
        //
        // The stop line is the near edge of the crossing road, not the junction node. A node sits
        // at the *centre* of the road it is on, so waiting at it means waiting eight metres inside
        // a sixteen-metre boulevard — which is why cars queueing off a side street were sitting in
        // the middle of the main carriageway, and why it only showed on the widest streets.
        float toStopLine = remaining - CrossingHalfWidth(Ahead(car), graph: _graph, alongX:
            _graph.RunsAlongX(_edge[car]));

        if (toStopLine < LookAhead && MustStopAt(car, toStopLine) && toStopLine < gap)
        {
            gap = MathF.Max(toStopLine, 0.05f);
            leaderSpeed = 0f;
        }

        _speed[car] = MathF.Max(0f,
            _speed[car] + Idm(_speed[car], limit, gap, leaderSpeed, Length(car)) * Tick);

        float step = _speed[car] * Tick;
        // Never step past the leader, whatever the numbers say.
        if (leader >= 0 && step > gap - 0.05f) step = MathF.Max(0f, gap - 0.05f);

        if (step < remaining)
        {
            _along[car] += _direction[car] > 0 ? step : -step;
            return;
        }

        EnterNextRoad(car, step - remaining);
    }

    float Idm(float speed, float limit, float gap, float leaderSpeed, float length)
    {
        float free = 1f - MathF.Pow(speed / MathF.Max(limit, 0.5f), 4f);
        if (gap >= float.MaxValue * 0.5f) return Acceleration * free;

        float closing = speed - leaderSpeed;
        float desired = StandstillGap + MathF.Max(0f,
            speed * HeadwaySeconds + speed * closing / (2f * MathF.Sqrt(Acceleration * Comfortable)));
        float interaction = desired / MathF.Max(gap, 0.15f);
        return Acceleration * (free - interaction * interaction);
    }

    /// <summary>
    /// Whether the junction ahead is closed to this car — a red light, or a bigger road with
    /// traffic on it, or simply nowhere to go.
    /// </summary>
    /// <summary>
    /// Half the width of the widest road crossing at a node — how far past the node the junction
    /// reaches back toward an approaching car.
    /// </summary>
    static float CrossingHalfWidth(int node, RoadGraph graph, bool alongX)
    {
        float widest = 0f;
        foreach (int edge in graph.IncidentEdges(node))
            if (graph.RunsAlongX(edge) != alongX)
                widest = MathF.Max(widest, graph.Edges[edge].Width);
        return widest * 0.5f;
    }

    bool MustStopAt(int car, float toStopLine)
    {
        // Past the point of stopping: clear the junction rather than parking in the middle of it.
        // Without this a light turning red just as a car reaches it, or a give-way decision taken a
        // fraction too late, leaves the car standing across the crossing — where the traffic that
        // has right of way then drives through it.
        //
        // Committed means "cannot now stop", not "nearly there": a car still has to be able to
        // pull up at the line, and one that is already crawling has no excuse to enter on a red.
        float braking = _speed[car] * _speed[car] / (2f * Comfortable);
        if (toStopLine < MathF.Max(0.4f, braking)) return false;

        int node = Ahead(car);
        // On the last leg there is nothing to give way to: the car drives to the end of the road
        // and stops existing. Braking here instead would leave it parked a couple of metres short
        // of its destination for the rest of the run, blocking the lane behind it — which is how
        // two hundred cars can be moving happily at first and gridlocked five minutes later.
        if (_leg[car] >= _route[car].Count) return false;

        int nextEdge = _route[car][_leg[car]];
        // Keep clear: never enter a junction you cannot leave, which is the one rule that stops a
        // grid gridlocking itself.
        int nextDirection = _graph.Edges[nextEdge].A == node ? 1 : -1;
        if (!LaneIsClear(nextEdge, nextDirection, nextDirection > 0 ? 0f : _graph.Edges[nextEdge].Length,
                CarLength + StandstillGap))
            return true;

        // A "don't enter an occupied junction" rule lived here briefly and had to come out. The
        // reasoning for it was that a car already inside a junction is not itself waiting for
        // anybody, so waiting on one cannot cycle — but that is simply untrue. A car in a junction
        // can be queued because its own exit is blocked, and then everyone waiting on it waits
        // forever. Measured: arrivals stopped dead within a minute. It is the reservation rule this
        // design refuses on purpose, and the cost of refusing it is the rare clip below.
        int signal = _graph.Nodes[node].SignalIndex;
        if (signal < 0) return MustGiveWay(car, node, toStopLine);

        bool alongX = _graph.RunsAlongX(_edge[car]);
        if (_graph.Signals[signal].IsGreen(Now, alongX)) return false;
        // Amber with no room to stop: carrying on is safer than standing on the brakes.
        return !(_graph.Signals[signal].IsAmber(Now, alongX) && toStopLine < _speed[car] * 1.2f);
    }

    /// <summary>
    /// Give way at an unsignalised crossing: yield to anything arriving on a more important road.
    /// </summary>
    /// <remarks>
    /// Not every junction gets a signal — most alley crossings do not — and without a rule here two
    /// cars simply drive through each other in the middle of one.
    ///
    /// Priority is a strict total order: road class first, then edge index to break ties. That is
    /// what makes this safe to reason about. A car only ever yields to something strictly above it,
    /// so a cycle of cars each waiting for the next cannot form, and no amount of traffic can
    /// produce a standoff nobody backs out of.
    /// </remarks>
    bool MustGiveWay(int car, int node, float toStopLine)
    {
        if (_graph.Nodes[node].IncidentCount < 2) return false;
        // Far enough out that yielding would be guesswork; IDM slows for the junction anyway.
        if (toStopLine > LookAhead * 0.5f) return false;

        var mine = Priority(_edge[car]);
        bool mineAlongX = _graph.RunsAlongX(_edge[car]);

        // Gap acceptance: how long this car needs to get across and be clear. A fixed window is
        // wrong because junctions are not all the same size — three seconds is ample at an alley
        // crossing and not nearly enough to clear a sixteen-metre boulevard from a standstill,
        // which is where the near misses were.
        float span = 2f * CrossingHalfWidth(node, _graph, mineAlongX) + Length(car);
        float needed = span / MathF.Max(_speed[car], 2.5f) + 1.5f;

        foreach (int edge in _graph.IncidentEdges(node))
        {
            if (edge == _edge[car]) continue;
            // Only a crossing road can be in the way. Traffic arriving along the same axis is
            // either behind this car or coming the other way in its own lane, and stopping for it
            // would mean halting at every bend in a straight road.
            if (_graph.RunsAlongX(edge) == mineAlongX) continue;
            if (Priority(edge).CompareTo(mine) >= 0) continue;

            // Cars on that road heading into this junction. Judged by when they will arrive rather
            // than by how far away they are: a car doing thirteen metres a second covers the width
            // of a junction in the time it takes to look at it, and one sitting still just short
            // of the crossing is about to pull into it.
            int direction = _graph.Edges[edge].B == node ? 1 : -1;
            for (int other = _laneHead[Lane(edge, direction)]; other >= 0; other = _next[other])
            {
                float toJunction = _graph.Edges[edge].Length - Progress(other);
                if (toJunction > LookAhead * 1.5f) continue;
                if (toJunction < 10f) return true;
                if (toJunction / MathF.Max(_speed[other], 1f) < needed) return true;
            }

            // And traffic that has just left it, still clearing the mouth of the crossing. Watching
            // only what is arriving misses the commonest near miss of all: two cars leaving the
            // same junction at once on roads at right angles to each other, which never conflicted
            // on the way in and are half a metre apart on the way out.
            //
            // Safe to wait for, because this only ever yields to a road of strictly higher
            // priority. A standoff needs both parties to be waiting on the other, and the ordering
            // makes that impossible.
            for (int other = _laneHead[Lane(edge, -direction)]; other >= 0; other = _next[other])
                if (Progress(other) < 6f) return true;
        }

        return false;
    }

    (int Class, int Edge) Priority(int edge) => ((int)_graph.Edges[edge].Kind, edge);


    void EnterNextRoad(int car, float overshoot)
    {
        int node = Ahead(car);

        if (_leg[car] >= _route[car].Count)
        {
            if (_cruise[car] && Retarget(car)) return;
            Despawn(car, arrived: true);
            return;
        }

        int nextEdge = _route[car][_leg[car]++];
        var next = _graph.Edges[nextEdge];
        int direction = next.A == node ? 1 : -1;

        LaneRemove(car);
        _edge[car] = nextEdge;
        _direction[car] = direction;
        _along[car] = direction > 0
            ? MathF.Min(overshoot, next.Length)
            : next.Length - MathF.Min(overshoot, next.Length);
        LaneInsert(car);
    }

    bool Retarget(int car)
    {
        _destination[car] = NextRandom(_spawns.Count);
        if (!Route(car)) return false;
        _arrived++;
        return true;
    }

    void Despawn(int car, bool arrived)
    {
        LaneRemove(car);
        _alive[car] = false;
        _route[car].Clear();
        _free.Push(car);
        if (arrived) _arrived++; else _despawned++;
    }

    float Length(int car) => _truck[car] ? TruckLength : CarLength;

    // ---- lanes ------------------------------------------------------------------------------
    //
    // Each (edge, direction) keeps its cars in a singly-linked list ordered by how far along they
    // are, leader first. Cars only move forward and the model forbids overtaking, so that order
    // never changes while a car stays on a road — which means finding the car in front is a single
    // pointer hop rather than a search, and the whole thing stays linear in the number of cars
    // rather than quadratic.

    int Lane(int edge, int direction) => edge * 2 + (direction > 0 ? 0 : 1);

    void LaneInsert(int car)
    {
        int lane = Lane(_edge[car], _direction[car]);
        float mine = Progress(car);

        int previous = -1;
        int at = _laneHead[lane];
        while (at >= 0 && Progress(at) > mine)
        {
            previous = at;
            at = _next[at];
        }

        _next[car] = at;
        if (previous < 0) _laneHead[lane] = car;
        else _next[previous] = car;
    }

    void LaneRemove(int car)
    {
        int lane = Lane(_edge[car], _direction[car]);
        int at = _laneHead[lane];
        if (at == car) { _laneHead[lane] = _next[car]; _next[car] = -1; return; }

        while (at >= 0 && _next[at] != car) at = _next[at];
        if (at >= 0) _next[at] = _next[car];
        _next[car] = -1;
    }

    /// <summary>Distance travelled along this road in this car's own direction of travel.</summary>
    float Progress(int car)
    {
        var edge = _graph.Edges[_edge[car]];
        return _direction[car] > 0 ? _along[car] : edge.Length - _along[car];
    }

    int LaneLeader(int car)
    {
        int lane = Lane(_edge[car], _direction[car]);
        int at = _laneHead[lane];
        int leader = -1;
        while (at >= 0 && at != car)
        {
            leader = at;
            at = _next[at];
        }
        return at == car ? leader : -1;
    }

    /// <summary>The car furthest back in a lane — the one anything joining it will meet first.</summary>
    int LaneTail(int edge, int direction)
    {
        int at = _laneHead[Lane(edge, direction)];
        if (at < 0) return -1;
        while (_next[at] >= 0) at = _next[at];
        return at;
    }

    bool LaneIsClear(int edge, int direction, float along, float clearance = CarLength * 2f)
    {
        float progress = direction > 0 ? along : _graph.Edges[edge].Length - along;
        for (int at = _laneHead[Lane(edge, direction)]; at >= 0; at = _next[at])
            if (MathF.Abs(Progress(at) - progress) < clearance) return false;
        return true;
    }

    /// <summary>Debug hook: where a car thinks it is, in the simulation's own terms.</summary>
    internal string DebugCar(int id) =>
        $"car {id} edge={_edge[id]} dir={_direction[id]} along={_along[id]:F2} " +
        $"len={_graph.Edges[_edge[id]].Length:F2} w={_graph.Edges[_edge[id]].Width:F1} " +
        $"legs={_route[id].Count}/{_leg[id]} waiting={_waiting[id]} alive={_alive[id]}";

    /// <summary>
    /// Debug hook: every living car appears in exactly one lane list, exactly once. Everything the
    /// simulation knows about who is in front of whom depends on it.
    /// </summary>
    internal string? LaneBookkeepingFault()
    {
        var seen = new int[_alive.Length];
        for (int lane = 0; lane < _laneHead.Length; lane++)
        {
            int steps = 0;
            for (int at = _laneHead[lane]; at >= 0; at = _next[at])
            {
                if (++steps > _alive.Length) return $"lane {lane} is a cycle";
                seen[at]++;
                if (Lane(_edge[at], _direction[at]) != lane)
                    return $"car {at} is filed under lane {lane} but drives on " +
                           $"{Lane(_edge[at], _direction[at])}";
            }
        }

        for (int car = 0; car < _alive.Length; car++)
        {
            if (_alive[car] && seen[car] != 1) return $"living car {car} is in {seen[car]} lanes";
            if (!_alive[car] && seen[car] != 0) return $"dead car {car} is still in {seen[car]} lanes";
        }
        return null;
    }

    // ---- rides ------------------------------------------------------------------------------

    /// <summary>
    /// Puts a car on the road for the camera to sit in. It is an ordinary agent in every other
    /// respect, so it queues at lights and behind traffic exactly like the rest.
    /// </summary>
    public int RequestRide(Vector3 from, int destinationSpawn, bool cruise)
    {
        if (!CanDrive) return -1;
        if (!_graph.TryNearestEdge(from, 90f, out int edge, out float along)) return -1;

        var start = new CarSpawn(edge, along, _graph.PointOn(edge, along), -1, "");
        int destination = cruise ? NextRandom(_spawns.Count) : destinationSpawn;
        if (destination < 0 || destination >= _spawns.Count) return -1;

        if (!TrySpawn(start, destination, pov: true, cruise: cruise, out int id)) return -1;
        // The passenger should not wait in a queue behind two hundred anonymous cars.
        _waiting[id] = false;
        if (!Route(id)) { Despawn(id, arrived: false); return -1; }
        return id;
    }

    public bool TryGetCar(int id, out CarAgent car)
    {
        car = default;
        if (id < 0 || id >= _alive.Length || !_alive[id]) return false;
        car = Describe(id);
        return true;
    }

    public void EndRide(int id)
    {
        if (id < 0 || id >= _alive.Length || !_alive[id]) return;
        _pov[id] = false;
        _cruise[id] = false;
    }

    /// <summary>Every living car, for the renderer. Reused list; do not hold on to it.</summary>
    public IReadOnlyList<CarAgent> Cars()
    {
        _visible.Clear();
        for (int car = 0; car < _alive.Length; car++)
            if (_alive[car]) _visible.Add(Describe(car));
        return _visible;
    }

    CarAgent Describe(int car)
    {
        var edge = _graph.Edges[_edge[car]];
        var heading = _graph.DirectionOf(_edge[car], _direction[car]);

        // Right-hand traffic. With Y up and a right-handed world, the right of a heading is
        // cross(heading, up) — the old ambient cars sat on their own left, which no one noticed
        // because they drove backwards half the time anyway.
        var right = Vector3.Cross(heading, Vector3.UnitY);
        if (right.LengthSquared() > 1e-6f) right = Vector3.Normalize(right);

        var on = _graph.PointOn(_edge[car], _along[car]);
        return new CarAgent
        {
            Id = car,
            Position = on + right * (edge.Width * 0.25f) + new Vector3(0f, 0.02f, 0f),
            Yaw = MathF.Atan2(heading.Z, heading.X),
            Pitch = edge.Pitch * _direction[car],
            Speed = _speed[car],
            IsTruck = _truck[car],
            IsPov = _pov[car],
            Color = _color[car],
        };
    }

    // ---- storage ----------------------------------------------------------------------------

    int Take()
    {
        if (_free.Count == 0) Resize(Math.Max(16, _alive.Length * 2));
        return _free.Pop();
    }

    void Resize(int capacity)
    {
        int previous = _alive.Length;
        if (capacity <= previous) return;
        Array.Resize(ref _edge, capacity);
        Array.Resize(ref _direction, capacity);
        Array.Resize(ref _along, capacity);
        Array.Resize(ref _speed, capacity);
        Array.Resize(ref _alive, capacity);
        Array.Resize(ref _truck, capacity);
        Array.Resize(ref _pov, capacity);
        Array.Resize(ref _cruise, capacity);
        Array.Resize(ref _waiting, capacity);
        Array.Resize(ref _destination, capacity);
        Array.Resize(ref _color, capacity);
        Array.Resize(ref _route, capacity);
        Array.Resize(ref _leg, capacity);
        Array.Resize(ref _next, capacity);

        // Highest index first, so ids are handed out in ascending order and a city's cars are the
        // same cars from one run to the next.
        for (int i = capacity - 1; i >= previous; i--)
        {
            _route[i] = new List<int>(32);
            _next[i] = -1;
            _free.Push(i);
        }
    }

    static Vector4 CarColour(float noise) => noise switch
    {
        < 0.25f => new Vector4(0.72f, 0.74f, 0.78f, 1f),
        < 0.50f => new Vector4(0.30f, 0.34f, 0.42f, 1f),
        < 0.72f => new Vector4(0.58f, 0.20f, 0.18f, 1f),
        < 0.88f => new Vector4(0.18f, 0.36f, 0.30f, 1f),
        _ => new Vector4(0.86f, 0.80f, 0.52f, 1f),
    };
}
