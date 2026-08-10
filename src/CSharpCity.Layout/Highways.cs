using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// Elevated highways connecting the districts, so the city reads as one place rather than a scatter
/// of islands.
/// </summary>
/// <remarks>
/// Highways follow the treemap's <em>longest</em> district cuts. That's not an arbitrary choice: the
/// first divisions the treemap makes are the biggest structural splits in the solution, and running
/// the arterials along them mirrors how real cities grow major roads along their principal
/// boundaries. Nothing new has to be routed, and the highway network is deterministic.
///
/// Like the street grid, highways are <b>scenery</b>. Project-level meaning belongs to rail in a
/// later phase; putting a second encoding on the roads would compete with the footpaths that carry
/// the dependencies.
/// </remarks>
internal static class Highways
{
    const float DeckWidth = 11f;
    /// <summary>Two heights so perpendicular highways stack into an interchange instead of colliding.</summary>
    const float LowDeckY = 9f;
    const float HighDeckY = 14.5f;
    const float PillarSpacing = 26f;
    const float ParapetHeight = 1.0f;
    const float RampRun = 58f;
    /// <summary>
    /// Only cuts spanning at least this share of the city become arterials. A binary treemap halves
    /// the region at each level, so the first cut spans the full city and the next tier spans about
    /// half â€” the threshold has to sit below that or only the single first cut ever qualifies.
    /// </summary>
    const float MajorCutShare = 0.34f;
    const int MaxHighways = 6;

    static readonly Vector4 Concrete = new(0.24f, 0.24f, 0.25f, 1f);
    static readonly Vector4 Structure = new(0.30f, 0.30f, 0.31f, 1f);

    /// <summary>How far along the deck the joints sit, so a route has somewhere to aim.</summary>
    const float DeckJointSpacing = 120f;

    public static int Build(SceneGraph scene, Bounds2 city, IReadOnlyList<Treemap.Cut> districtCuts,
        RoadGraphBuilder.Draft draft)
    {
        float threshold = MathF.Min(city.Width, city.Depth) * MajorCutShare;

        var arterials = districtCuts
            .Where(c => c.SpanEnd - c.SpanStart >= threshold)
            .OrderByDescending(c => c.SpanEnd - c.SpanStart)
            .Take(MaxHighways)
            .ToList();

        int laid = 0;
        foreach (var cut in arterials)
            if (Lay(scene, cut, draft)) laid++;
        return laid;
    }

    static bool Lay(SceneGraph scene, Treemap.Cut cut, RoadGraphBuilder.Draft draft)
    {
        float deckY = cut.Vertical ? LowDeckY : HighDeckY;

        // Touch down just past the outermost real junction on this road, not at the ends of the
        // treemap cut.
        //
        // Those are not the same place. A cut's span is the region being divided, and for every
        // division below the first that region's edge *is* the centreline of the boulevard the
        // parent cut laid — so a ramp built at the span end came down in the middle of a crossroads.
        // Walking the finished network instead puts the toe on a real stretch of road, clear of the
        // junction box at its end.
        var line = draft.NodesOnLine(cut.Vertical, cut.Position);
        if (line.Length < 4) return false;

        var widest = draft.WidestAtNode();
        float At(int node) => cut.Vertical ? draft.Positions[node].Z : draft.Positions[node].X;
        float Clearance(int node) => 0.5f * widest[node] + 2f;

        float start = At(line[0]) + Clearance(line[0]);
        float end = At(line[^1]) - Clearance(line[^1]);
        if (end - start < RampRun * 2f + 40f) return false;   // too short to climb up and back down

        float deckStart = start + RampRun;
        float deckEnd = end - RampRun;
        float yaw = cut.Vertical ? MathF.PI * 0.5f : 0f;

        AddDeck(scene, cut, deckStart, deckEnd, deckY, yaw);
        AddPillars(scene, cut, deckStart, deckEnd, deckY);
        AddParapets(scene, cut, deckStart, deckEnd, deckY);
        AddDeckLighting(scene, cut, start, end, deckY);

        // A ramp at each end, climbing from ground to deck. Ramps run "outward", so the pitch is
        // mirrored: the near ramp rises toward the deck, the far ramp falls away from it.
        AddRamp(scene, cut, start, deckStart, deckY, yaw, rising: true);
        AddRamp(scene, cut, deckEnd, end, deckY, yaw, rising: false);

        AddPortal(scene, cut, start, deckY);
        AddPortal(scene, cut, end, deckY);

        Connect(draft, cut, start, deckStart, deckEnd, end, deckY);
        AddDeckTraffic(scene, cut, start, deckStart, deckEnd, end, deckY);
        return true;
    }

    /// <summary>
    /// Traffic that climbs a ramp, runs the deck and comes back down the other one.
    /// </summary>
    /// <remarks>
    /// Scripted rather than simulated, and deliberately so. The deck is genuinely part of the road
    /// network — a route can climb it and the pathfinder will take it when it is worth taking — but
    /// a highway's only entrances are at its two far ends, because that is where the outermost
    /// junctions on its road are. A trip only benefits if it runs almost the full width of the city
    /// along that one line, which out of a couple of hundred random journeys essentially never
    /// happens, and an empty motorway over a busy city looks like something is broken.
    ///
    /// One closed circuit per highway: out along one carriageway, back along the other, with the
    /// ramps at each end included so there is always something visibly climbing.
    /// </remarks>
    static void AddDeckTraffic(SceneGraph scene, Treemap.Cut cut, float start, float deckStart,
        float deckEnd, float end, float deckY)
    {
        float lane = DeckWidth * 0.22f;
        float ground = CityLayout.StreetSurfaceY + 0.02f;

        var forward = cut.Vertical ? Vector3.UnitZ : Vector3.UnitX;
        var side = Vector3.Cross(forward, Vector3.UnitY) * lane;

        // Out on one side, back on the other, closing the loop across the width at each end. Being
        // a closed loop is what keeps it moving forwards: an open path is played as a triangle
        // wave, which is what used to make every car on the deck reverse at the end of it.
        var loop = new List<Vector3>
        {
            Along(cut, start, ground) + side,
            Along(cut, deckStart, deckY) + side,
            Along(cut, deckEnd, deckY) + side,
            Along(cut, end, ground) + side,
            Along(cut, end, ground) - side,
            Along(cut, deckEnd, deckY) - side,
            Along(cut, deckStart, deckY) - side,
            Along(cut, start, ground) - side,
        };
        loop.Add(loop[0]);

        int pathIndex = TrafficNetwork.AddPath(scene, loop.ToArray(), loop: true);
        int vehicles = Math.Clamp((int)((deckEnd - deckStart) / 90f), 4, 12);

        for (int i = 0; i < vehicles; i++)
        {
            bool truck = i % 4 == 0;
            scene.Travellers.Add(new Traveller
            {
                PathIndex = pathIndex,
                Phase = (float)i / vehicles,
                Speed = truck ? 15f : 22f,
                Color = truck
                    ? new Vector4(0.86f, 0.55f, 0.22f, 1f)
                    : new Vector4(0.74f, 0.78f, 0.84f, 1f),
                Kind = truck ? TravellerKind.Truck : TravellerKind.Car,
                Layer = CityLayer.Highways,
            });
        }
    }

    /// <summary>
    /// Joins the deck to the street network, which is what lets anything actually drive it.
    /// </summary>
    /// <remarks>
    /// Before this the deck was drawn but not connected to anything: its own two-point path carried
    /// cars back and forth along the top and no route ever reached it from the ground. The ramps
    /// were scenery in the most literal sense — a road surface no vehicle could enter.
    /// </remarks>
    static void Connect(RoadGraphBuilder.Draft draft, Treemap.Cut cut, float start, float deckStart,
        float deckEnd, float end, float deckY)
    {
        int footA = draft.FreeNode(Along(cut, start, CityLayout.StreetSurfaceY), RoadNodeKind.RampFoot);
        int footB = draft.FreeNode(Along(cut, end, CityLayout.StreetSurfaceY), RoadNodeKind.RampFoot);
        int topA = draft.FreeNode(Along(cut, deckStart, deckY), RoadNodeKind.RampTop);
        int topB = draft.FreeNode(Along(cut, deckEnd, deckY), RoadNodeKind.RampTop);

        draft.Connect(footA, topA, DeckWidth, RoadKind.HighwayRamp);
        draft.Connect(topB, footB, DeckWidth, RoadKind.HighwayRamp);

        // Joints along the deck give a route somewhere to aim for mid-span, and keep the segments
        // short enough that a car's position along one is a useful number.
        int previous = topA;
        for (float d = deckStart + DeckJointSpacing; d < deckEnd - DeckJointSpacing * 0.5f;
             d += DeckJointSpacing)
        {
            int joint = draft.FreeNode(Along(cut, d, deckY), RoadNodeKind.DeckJoint);
            draft.Connect(previous, joint, DeckWidth, RoadKind.HighwayDeck);
            previous = joint;
        }
        draft.Connect(previous, topB, DeckWidth, RoadKind.HighwayDeck);

        // The toe splits the street it lands on, so the ramp meets it at a T-junction and traffic
        // can turn onto it from either direction.
        foreach (int foot in new[] { footA, footB })
            if (!draft.SpliceOntoLine(cut.Vertical, cut.Position, foot))
                Console.Error.WriteLine(
                    "warning: a highway ramp found no street to join; that deck will be unreachable.");
    }

    /// <summary>Maps a distance along the cut to a world position at the given height.</summary>
    static Vector3 Along(Treemap.Cut cut, float distance, float y) => cut.Vertical
        ? new Vector3(cut.Position, y, distance)
        : new Vector3(distance, y, cut.Position);

    static void AddDeck(SceneGraph scene, Treemap.Cut cut, float from, float to, float y, float yaw)
    {
        scene.Roads.Add(new RoadQuad
        {
            Center = Along(cut, (from + to) * 0.5f, y),
            Length = to - from,
            Width = DeckWidth,
            Yaw = yaw,
            Color = Concrete,
            Flags = (uint)(RoadFlags.EdgeLines | RoadFlags.DashedCenter),
            Layer = CityLayer.Highways,
        });
    }

    static void AddRamp(SceneGraph scene, Treemap.Cut cut, float from, float to, float deckY,
        float yaw, bool rising)
    {
        float run = to - from;
        if (run < 1f) return;

        // The quad is built flat and then tilted about its centre, so its centre sits at half height.
        float pitch = MathF.Atan2(deckY - CityLayout.StreetSurfaceY, run) * (rising ? 1f : -1f);
        float midY = (deckY + CityLayout.StreetSurfaceY) * 0.5f;

        scene.Roads.Add(new RoadQuad
        {
            Center = Along(cut, (from + to) * 0.5f, midY),
            // Length is the slope, not the ground run, or the ramp falls short of the deck.
            Length = MathF.Sqrt(run * run + (deckY - CityLayout.StreetSurfaceY) *
                                            (deckY - CityLayout.StreetSurfaceY)),
            Width = DeckWidth,
            Yaw = yaw,
            Pitch = pitch,
            Color = Concrete,
            Flags = (uint)(RoadFlags.EdgeLines | RoadFlags.DashedCenter),
            Layer = CityLayer.Highways,
        });
    }

    /// <summary>Spacing of the masts along a deck. Wide, the way motorway lighting actually is.</summary>
    const float LightSpacing = 42f;

    /// <summary>
    /// Lighting along the deck, on the parapets, and the pools it throws on the carriageway.
    /// </summary>
    /// <remarks>
    /// Runs the full length including the ramps, not just the deck: an unlit ramp is exactly the
    /// piece you most want to see, because it is the only part of a highway that goes anywhere.
    /// The masts alternate sides so the deck is lit evenly without doubling the count.
    /// </remarks>
    static void AddDeckLighting(SceneGraph scene, Treemap.Cut cut, float from, float to, float deckY)
    {
        const float MastHeight = 5.5f;
        float half = DeckWidth * 0.5f;
        int index = 0;

        for (float d = from + LightSpacing * 0.5f; d < to; d += LightSpacing, index++)
        {
            // Ramps climb, so a mast standing on one has to stand on the slope, not in mid-air.
            float y = SurfaceAt(cut, d, from, to, deckY);
            int side = index % 2 == 0 ? 1 : -1;

            var offset = cut.Vertical
                ? new Vector3(half * side, 0f, 0f)
                : new Vector3(0f, 0f, half * side);
            var foot = Along(cut, d, y) + offset;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = foot,
                Size = new Vector3(0.24f, MastHeight, 0.24f),
                Color = Structure,
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Highways,
            });

            // The head leans in over the carriageway, as a real one does.
            var head = foot with { Y = foot.Y + MastHeight } - offset * 0.32f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = head,
                Size = new Vector3(0.95f, 0.22f, 0.5f),
                Color = new Vector4(0.62f, 0.57f, 0.42f, 1f),
                PickId = -1,
                Detail = 1f,
                Flags = (uint)BoxFlags.Emissive,
                Layer = CityLayer.Highways,
            });

            scene.Roads.Add(new RoadQuad
            {
                Center = Along(cut, d, y + 0.06f),
                Length = LightSpacing * 0.8f,
                Width = DeckWidth,
                Yaw = cut.Vertical ? MathF.PI * 0.5f : 0f,
                Color = new Vector4(1.0f, 0.90f, 0.66f, 0.42f),
                Flags = (uint)RoadFlags.LightPool,
                Layer = CityLayer.Highways,
            });
        }
    }

    /// <summary>Height of the carriageway at a distance along the cut — flat on the deck, sloped on a ramp.</summary>
    static float SurfaceAt(Treemap.Cut cut, float d, float from, float to, float deckY)
    {
        float deckStart = from + RampRun, deckEnd = to - RampRun;
        if (d <= deckStart)
            return CityLayout.StreetSurfaceY
                   + (deckY - CityLayout.StreetSurfaceY) * Math.Clamp((d - from) / RampRun, 0f, 1f);
        if (d >= deckEnd)
            return CityLayout.StreetSurfaceY
                   + (deckY - CityLayout.StreetSurfaceY) * Math.Clamp((to - d) / RampRun, 0f, 1f);
        return deckY;
    }

    static void AddPillars(SceneGraph scene, Treemap.Cut cut, float from, float to, float deckY)
    {
        for (float d = from + PillarSpacing * 0.5f; d < to; d += PillarSpacing)
        {
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = Along(cut, d, 0f),
                Size = new Vector3(1.7f, deckY, 1.7f),
                Color = Structure,
                PickId = -1,
                Detail = 1f,
            });
        }
    }

    static void AddParapets(SceneGraph scene, Treemap.Cut cut, float from, float to, float deckY)
    {
        float half = DeckWidth * 0.5f;
        float length = to - from;
        float middle = (from + to) * 0.5f;

        for (int side = -1; side <= 1; side += 2)
        {
            var centre = Along(cut, middle, deckY);
            var offset = cut.Vertical
                ? new Vector3(half * side, 0f, 0f)
                : new Vector3(0f, 0f, half * side);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = centre + offset,
                Size = cut.Vertical
                    ? new Vector3(0.35f, ParapetHeight, length)
                    : new Vector3(length, ParapetHeight, 0.35f),
                Color = Structure,
                PickId = -1,
                Detail = 1f,
            });
        }
    }

    /// <summary>
    /// A portal frame where the highway meets ground level, so a ramp arrives somewhere rather than
    /// just petering out.
    /// </summary>
    static void AddPortal(SceneGraph scene, Treemap.Cut cut, float at, float deckY)
    {
        const float Height = 6.5f;
        float half = DeckWidth * 0.5f + 0.9f;

        for (int side = -1; side <= 1; side += 2)
        {
            var offset = cut.Vertical
                ? new Vector3(half * side, 0f, 0f)
                : new Vector3(0f, 0f, half * side);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = Along(cut, at, 0f) + offset,
                Size = new Vector3(1.3f, Height, 1.3f),
                Color = Structure,
                PickId = -1,
                Detail = 1f,
            });
        }

        // Lintel across the top.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = Along(cut, at, Height),
            Size = cut.Vertical
                ? new Vector3(half * 2f + 1.3f, 1.2f, 1.3f)
                : new Vector3(1.3f, 1.2f, half * 2f + 1.3f),
            Color = Structure,
            PickId = -1,
            Detail = 1f,
        });
    }

}

