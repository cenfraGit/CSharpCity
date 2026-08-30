using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Turns a <see cref="CityModel"/> into a positioned <see cref="SceneGraph"/>.
/// Deterministic: every random-looking value is derived from a hash of the type's fully-qualified
/// name, so the same solution always produces the same city.
/// </summary>
public static class CityLayout
{
    // Tuned so a person of eye height 1.7 feels like they're walking real streets.
    const float DistrictGap = 26f;
    const float StreetWidth = 7f;
    const float AlleyWidth = 3.5f;
    const float LotPadding = 1.6f;
    /// <summary>Smallest footprint a type is ever given. Below this it would be invisible anyway.</summary>
    const float MinBuildableSide = 1.6f;
    const float MinFloorHeight = 2.6f;
    const float MaxFloorHeight = 26f;

    /// <summary>
    /// Half-diagonal of a unit square. A facade sign pushed out only half a side sits inside the
    /// building when you view it corner-on; clearing the circumradius keeps it outside from every
    /// angle, because a billboard's own width only ever carries its corners further out.
    /// </summary>
    const float BoxCircumradius = 0.70711f;
    /// <summary>Extra gap so signs read as mounted on the wall rather than embedded in it.</summary>
    const float FacadeSignClearance = 0.45f;

    /// <summary>
    /// Street and lot margins are a share of the block they're carved out of, not fixed metres.
    /// </summary>
    /// <remarks>
    /// A constant margin is applied at every level of the namespace tree, so on a deep tree it
    /// compounds: five levels of 7 m street eats 70 m of a block from both sides and the lots at the
    /// bottom collapse to nothing — silently dropping types out of the city on any solution with a
    /// deeply nested namespace. Scaling with the block keeps a big district's roads wide and a small
    /// nested block's roads narrow but present.
    /// </remarks>
    static float MarginFor(Bounds2 block, float desired, float floor)
    {
        float share = MathF.Min(block.Width, block.Depth) * 0.07f;
        return Math.Clamp(share, floor, desired);
    }

    // Spacing for the flat layers, set by depth-buffer precision rather than by taste.
    //
    // Resolvable depth grows with the square of viewing distance: from a kilometre up, with a 0.4 m
    // near plane, anything closer together than roughly a third of a metre is indistinguishable and
    // flickers. The old stack ran from 0.06 to 0.36 — every layer inside that limit, which is why
    // the whole ground shimmered from the air while looking fine from the street. Each step here is
    // at least 0.25 m, which still reads as a kerb from a pavement.
    internal const float PondSurfaceY = 0.15f;
    /// <summary>
    /// Streets, boulevards, alleys <em>and</em> the junctions between them. One height, deliberately.
    /// </summary>
    /// <remarks>
    /// Junction patches used to sit half a metre higher so their blank tarmac would hide the lane
    /// markings running underneath. It worked, and it cost a visible step at every crossing plus a
    /// car that drove under the road it was crossing. <see cref="RoadSurfaces"/> gets the same
    /// result by trimming each road back to where its junction begins, so the two meet edge to edge
    /// instead of one hiding the other.
    /// </remarks>
    internal const float StreetSurfaceY = 0.40f;
    /// <summary>Civic forecourts, just clear of the street they open onto.</summary>
    internal const float PlazaSurfaceY = 0.66f;
    internal const float RoundaboutSurfaceY = 1.18f;
    /// <summary>
    /// Bare ground: a worn path is the lowest thing in the city, not the highest.
    /// </summary>
    /// <remarks>
    /// This used to sit at the top of the stack, at 1.44, on the reasoning that a desire line
    /// crosses roads rather than yielding to them and therefore had to clear everything it might
    /// overlap. The paths are thin enough that nobody noticed them floating — but the people
    /// walking on them were hovering at about their own height, which everybody noticed.
    ///
    /// A path now stops where the ground stops being bare and resumes on the far side, so it never
    /// overlaps anything and needs no clearance from it. All it has to clear is the district plate
    /// underneath, which tops out at 0.1.
    /// </remarks>
    internal const float FootpathSurfaceY = 0.36f;

    // Quality thresholds that flip visual channels on.
    const double GrimyComplexity = 6.0;

    /// <summary>
    /// Statement coverage below which a floor reads as untested.
    /// </summary>
    /// <remarks>
    /// Not zero. A method whose only covered lines are its signature and a guard clause is
    /// technically "covered" and practically is not, and a hard zero would show nothing at all on
    /// the long tail of methods a test brushes past on its way somewhere else. Half is a bar that a
    /// method genuinely under test clears easily.
    /// </remarks>
    const double UncoveredBelow = 0.5;

    /// <summary>
    /// Smog range, calibrated against real data rather than guessed. Measured across a large
    /// real-world solution's districts, average complexity runs from 1.0 to 8.0 — an earlier
    /// threshold of 4.5 left only a handful of districts hazy at all, and a divisor of 8 made even
    /// those nearly invisible.
    /// </summary>
    const double SmogFloor = 2.6;
    const double SmogCeiling = 7.5;

    /// <summary>
    /// Says out loud how connected the road network came out, because a stranded fragment is a
    /// place cars can never reach and it is invisible from the air.
    /// </summary>
    static void ReportNetwork(RoadGraph graph)
    {
        if (graph.IsEmpty) return;

        float total = graph.Edges.Sum(e => e.Length);
        float main = graph.Edges.Where(e => graph.Nodes[e.A].Component == graph.MainComponent)
            .Sum(e => e.Length);
        int fragments = graph.Nodes.Where(n => n.IncidentCount > 0)
            .Select(n => n.Component).Distinct().Count() - 1;

        Console.Error.WriteLine(
            $"note: road network has {graph.Nodes.Count(n => n.IncidentCount > 0):n0} junction(s), " +
            $"{graph.Edges.Length:n0} segment(s), {graph.Signals.Length:n0} signal(s); " +
            $"{main / MathF.Max(total, 1f) * 100f:F1}% of it is one connected network" +
            (fragments > 0 ? $", {fragments} stranded fragment(s)." : "."));
    }

    /// <summary>
    /// Says where the design's boundary is and how leaky it is.
    /// </summary>
    /// <remarks>
    /// The ranked crossings are the useful part. A clean layering shows a couple of heavy pairs —
    /// everything goes through one deliberate interface — while a tangle shows a long tail of light
    /// ones, which is the same total leakage saying something completely different about the design.
    /// </remarks>
    static void ReportSeam(Seam.Result? seam)
    {
        if (seam is null) return;

        // Concentration is the number that decides whether this is a boundary or a smear. Four
        // pairs carrying most of the traffic is a design with an interface; forty pairs each
        // carrying a little is a design without one, and the two have identical leakage.
        int topFour = seam.Crossings.Take(4).Sum(c => c.Weight);
        float concentration = seam.CrossingWeight == 0 ? 0f : (float)topFour / seam.CrossingWeight;

        Console.Error.WriteLine(
            $"note: the architectural seam splits {seam.Left.Count} project(s) from " +
            $"{seam.Right.Count}; {seam.CrossingWeight:n0} of {seam.TotalWeight:n0} references " +
            $"cross it ({seam.Leakage:P0}) over {seam.CrossingPairs} project pair(s), " +
            $"the heaviest four carrying {concentration:P0} of it.");

        foreach (var crossing in seam.Crossings.Take(4))
            Console.Error.WriteLine(
                $"        {crossing.From} -> {crossing.To}: {crossing.Weight:n0}");
    }

    /// <param name="github">
    /// What the remote is doing right now, or null. Everything built from this is the overlay: it
    /// dresses buildings that are already placed and never moves one, so the city is identical with
    /// and without it.
    /// </param>
    /// <param name="separateCities">
    /// Lay each project out as a town of its own, with open country between them, rather than as a
    /// district of one city. Opt-in: the packed layout stays the known-good default, and having both
    /// means the two can be compared on the same solution.
    /// </param>
    public static SceneGraph Build(CityModel model, GitHubSnapshot? github = null,
        bool separateCities = false)
    {
        var scene = new SceneGraph();
        var projects = model.Projects.Where(p => p.Types.Count > 0)
            .OrderByDescending(p => p.Types.Count)
            .ToList();
        if (projects.Count == 0) return scene;

        // Civic roles are decided before layout, because a landmark asks for a bigger lot to hold
        // its plaza and that has to be priced into the treemap weights.
        var roles = new Dictionary<string, CivicRole>(StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var (id, role) in CivicRoles.Assign(project))
                roles[id] = role;

        // District area follows total content, so the city grows with the codebase.
        var districts = projects
            .Select(p => (Project: p, Weight: p.Types.Sum(t => FootprintDemand(t, roles))))
            .ToList();
        float totalWeight = districts.Sum(d => d.Weight);

        // Where the architecture's boundary is, and how much crosses it. Reported rather than
        // drawn: a river was built along this line and taken out again, because on a design with
        // no real boundary it rendered as a dozen similar bridges — a much weaker way of saying
        // what the numbers below say outright.
        var seam = Seam.Find(model,
            districts.ToDictionary(d => d.Project.Name, d => d.Weight, StringComparer.Ordinal));
        ReportSeam(seam);
        // FootprintDemand already carries slack for streets; the extra factor is the ring roads
        // between districts and the breathing room that makes the city walkable rather than packed.
        float citySide = MathF.Max(160f, MathF.Sqrt(totalWeight) * 2.8f);

        // One square holding every project, or one square per project with countryside between —
        // see Countryside.Spread. Either way this ends with `cityBounds` covering everything and
        // `flatGround` listing the rectangles the terrain must keep level.
        var cityBounds = new Bounds2(0, 0, citySide, citySide);
        var flatGround = new List<Bounds2>();
        // Boulevards between districts scale with the city, so a 40-district map doesn't end up with
        // the same alleys a 5-district one had.
        float boulevardWidth = Math.Clamp(citySide * 0.011f, 7f, 18f);
        var districtCuts = new List<Treemap.Cut>();

        if (separateCities)
        {
            cityBounds = Countryside.World(citySide);

            Treemap.Layout(
                districts.Select(d => (d.Project, d.Weight)).ToList(),
                cityBounds,
                (project, cell) =>
                {
                    var town = Countryside.Town(cell, citySide);
                    flatGround.Add(town);
                    LayDistrict(scene, project, roles, town);
                },
                // The world-level divisions are countryside, not roads. A boulevard laid along one
                // would run for half a kilometre through the hills between two towns, joining
                // nothing to nothing.
                _ => { });
        }
        else
        {
            flatGround.Add(cityBounds);

            Treemap.Layout(
                districts.Select(d => (d.Project, d.Weight)).ToList(),
                cityBounds,
                (project, cell) => LayDistrict(scene, project, roles,
                    StreetNetwork.InsetFromCuts(cell, cityBounds, boulevardWidth * 0.5f)),
                cut => districtCuts.Add(cut));

            foreach (var cut in districtCuts)
                StreetNetwork.AddCut(scene, cut, boulevardWidth, RoadClass.Boulevard);
        }

        // Every road the treemap cut is now known. Work out where they actually meet, once, and
        // build the single network that everything drivable routes over.
        var draft = RoadGraphBuilder.Arrange(scene.RoadCuts);

        // Arterials ride the biggest district divisions, turning 40-odd islands into one city.
        // Highways append to the draft rather than standing apart from it, so a route can climb a
        // ramp, run the deck and come back down onto the streets.
        int highways = Highways.Build(scene, cityBounds, districtCuts, draft);

        scene.RoadNetwork = RoadGraphBuilder.Finish(draft);
        RoadSurfaces.Emit(scene, scene.RoadNetwork);
        Kerbs.BuildSpawns(scene, scene.RoadNetwork);
        var pavement = Sidewalks.Build(scene, scene.RoadNetwork);
        int carParks = Parking.Build(scene, scene.RoadNetwork);
        var control = TrafficSignals.Build(scene, scene.RoadNetwork);
        ReportNetwork(scene.RoadNetwork);

        if (control.Heads + control.Signs > 0)
            Console.Error.WriteLine(
                $"note: {control.Signals:n0} signalised junction(s) with {control.Heads:n0} head(s), " +
                $"and {control.Signs:n0} give-way sign(s) where a minor road meets a bigger one.");

        if (carParks > 0)
            Console.Error.WriteLine(
                $"note: {carParks:n0} car park(s) on plots far bigger than the building standing " +
                "on them.");

        if (pavement.Kerbs > 0)
            Console.Error.WriteLine(
                $"note: {pavement.Kerbs:n0} kerb(s), {pavement.Corners:n0} corner(s) and " +
                $"{pavement.Props:n0} piece(s) of street furniture" +
                (pavement.Skipped > 0
                    ? $"; {pavement.Skipped} stretch(es) left unpaved (too short, or over budget)."
                    : "."));

        scene.CityBounds = cityBounds;

        // Traffic is a second pass: routing a dependency needs every building already placed, both
        // to know where the endpoints are and to know what's in the way.
        var traffic = TrafficNetwork.Build(scene, model);
        Terrain.Build(scene, cityBounds, flatGround,
            separateCities ? Terrain.Ground.Coastal : Terrain.Ground.Continental);

        if (separateCities)
        {
            Terrain.Flood(scene, cityBounds);
            Console.Error.WriteLine(
                $"note: {flatGround.Count} town(s) across {cityBounds.Width:0}m of open country, " +
                "with the coast beyond. Each town keeps its own streets; rail is the only way " +
                "between them.");
        }

        // The ride used to be a precomputed random walk through the streets, played back on rails.
        // It is now an ordinary car in TrafficSim with somewhere to be, so nothing is baked here.

        // What the repository remembers. Last, because it needs every building already placed and
        // it reads the model rather than the compiler.
        var history = History.Apply(scene, model);
        if (history.Active > 0 || history.Bicycles > 0)
            Console.Error.WriteLine(
                $"note: {history.Active:n0} building(s) with work in progress (committed to this week), " +
                $"{history.Bicycles:n0} bicycle(s) for the people who work in them" +
                (history.SoleOwnership > 0
                    ? $", and {history.SoleOwnership} file(s) still being changed by the only person " +
                      "who has ever touched them."
                    : "."));

        var railAir = RailAndAir.Build(scene, model);
        var emergency = EmergencyServices.Build(scene, model);

        // The overlay, last of all: it dresses buildings, junctions and civic forecourts that all
        // have to exist first. Everything it adds is tagged CityLayer.Works or CityLayer.Backlog,
        // which is what lets it be thrown away and rebuilt without disturbing the city under it.
        if (github is { Available: true })
        {
            var works = Works.Apply(scene, model, github);
            if (works.Sites > 0 || works.Queueing > 0)
                Console.Error.WriteLine(
                    $"note: {works.Sites:n0} building(s) under works from " +
                    $"{works.PullRequests:n0} pull request(s)" +
                    (works.Ghosts > 0 ? $", {works.Ghosts:n0} not built yet" : "") +
                    (works.Demolitions > 0 ? $", {works.Demolitions:n0} for demolition" : "") +
                    (works.Closures > 0 ? $", {works.Closures:n0} road closure(s)" : "") +
                    $"; {works.Queueing:n0} person/people queueing at the civic buildings" +
                    (works.Dropped > 0
                        ? $"; {works.Dropped:n0} changed file(s) not shown (capped)."
                        : "."));
        }

        if (emergency.CrimeScenes + emergency.Fires + emergency.Leaks > 0)
            Console.Error.WriteLine(
                $"note: emergency response at {emergency.CrimeScenes} security scene(s), " +
                $"{emergency.Fires} fire(s), {emergency.Leaks} resource leak(s); " +
                $"helicopter over {emergency.WorstBuilding}.");


        if (scene.Worst.Count > 0)
            Console.Error.WriteLine(
                $"note: worst building is {scene.Worst[0].Name} ({scene.Worst[0].Reason}). " +
                "Press B in the city for the full ranking.");

        if (railAir.Lines > 0)
            Console.Error.WriteLine(
                $"note: {railAir.Lines} project reference(s) on rail, {railAir.Unused} of them carrying " +
                "no observed type usage (rusted, no trains) - worth questioning, though reflection " +
                "and DI can use a type in ways static analysis doesn't see.");
        if (railAir.Airports > 0)
            Console.Error.WriteLine($"note: {railAir.Airports} airport(s) for external packages.");

        if (highways > 0)
            Console.Error.WriteLine($"note: {highways} elevated highway(s) across the city.");

        if (roles.Count > 0)
        {
            var tally = roles.GroupBy(r => r.Value)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {CivicRoles.Title(g.Key).ToLowerInvariant()}");
            Console.Error.WriteLine($"note: {roles.Count} civic landmarks — {string.Join(", ", tally)}.");
        }

        if (scene.CrampedLots > 0)
            Console.Error.WriteLine(
                $"note: {scene.CrampedLots} building(s) overran a lot too small to hold them.");

        int dropped = model.Projects.Sum(p => p.Types.Count) - scene.PickInfos.Count;
        if (traffic.Cycles > 0)
            Console.Error.WriteLine(
                $"note: {traffic.Cycles} circular dependency group(s) - look for the roundabouts.");
        if (traffic.Skipped > 0)
            Console.Error.WriteLine(
                $"warning: {traffic.Skipped} low-weight dependency path(s) skipped past the display cap.");
        if (dropped > 0)
        {
            // Name them. "N types are missing" is unactionable; the ids point straight at the cause.
            var missing = model.Projects.SelectMany(p => p.Types)
                .Where(t => !scene.Sites.ContainsKey(t.Id))
                .Select(t => t.Id)
                .Take(4);
            Console.Error.WriteLine(
                $"warning: {dropped} type(s) never placed: {string.Join(", ", missing)}");
        }

        // Spawn just outside the city on the main axis, so the first thing you see is the skyline.
        scene.SpawnPosition = new Vector3(citySide * 0.5f, 1.7f, -35f);
        scene.SpawnYaw = 90f;
        return scene;
    }

    /// <summary>
    /// Haze layers over a district, thickening with its average complexity, so you can pick out the
    /// bad neighbourhood from across the city without reading a single label.
    /// </summary>
    static void AddSmog(SceneGraph scene, ProjectNode project, Bounds2 bounds)
    {
        if (project.Types.Count == 0 || bounds.Width < 8f || bounds.Depth < 8f) return;

        double complexity = project.Types.Average(t => t.AvgComplexity);
        // Below the floor a district is clean and gets nothing at all; the absence of haze has to
        // mean something for its presence to.
        if (complexity < SmogFloor) return;

        float density = (float)Math.Clamp((complexity - SmogFloor) / (SmogCeiling - SmogFloor),
            0.12, 1.0);
        const int Layers = 6;

        for (int i = 0; i < Layers; i++)
        {
            float t = i / (float)(Layers - 1);
            scene.Roads.Add(new RoadQuad
            {
                Center = new Vector3(bounds.CenterX, 9f + t * 30f, bounds.CenterZ),
                Length = bounds.Width * 0.98f,
                Width = bounds.Depth * 0.98f,
                Yaw = 0f,
                // Thins out with height, the way real smog sits in a bowl over a city.
                Color = new Vector4(0.48f, 0.39f, 0.27f, density * 0.30f * (1f - t * 0.55f)),
                Flags = (uint)RoadFlags.None,
                Layer = CityLayer.Smog,
            });
        }
    }

    /// <summary>Lot area a type wants: driven by its state, since that's what sets the footprint.</summary>
    static float FootprintDemand(TypeNode type, IReadOnlyDictionary<string, CivicRole> roles)
    {
        float side = FootprintSide(type);
        roles.TryGetValue(type.Id, out var role);
        // Landmarks claim extra ground for their plaza - the lot grows, never the building.
        return side * side * 2.6f * CivicDressing.FootprintBonus(role);
    }

    static float FootprintSide(TypeNode type) => type.Kind switch
    {
        TypeKind.Delegate => 2.2f,                                    // phone booth
        TypeKind.Enum => 3.0f + MathF.Min(type.EnumMemberCount, 12) * 0.25f,
        _ => 5f + MathF.Sqrt(type.FieldCount + type.PropertyCount) * 2.6f,
    };

    static void LayDistrict(SceneGraph scene, ProjectNode project, IReadOnlyDictionary<string, CivicRole> roles, Bounds2 bounds)
    {
        scene.Districts[project.Name] = bounds;
        AddSmog(scene, project, bounds);

        scene.Ground.Add(new GroundQuad
        {
            BasePosition = new Vector3(bounds.CenterX, 0f, bounds.CenterZ),
            Size = new Vector2(bounds.Width, bounds.Depth),
            // Test projects are parks: green, and obviously not production.
            Color = project.IsTestProject
                ? new Vector4(0.20f, 0.36f, 0.19f, 1f)
                : new Vector4(0.20f, 0.20f, 0.22f, 1f),
        });

        // A banner floating above the skyline: the one sign you can always find from anywhere.
        scene.Labels.Add(new WorldLabel
        {
            Position = new Vector3(bounds.CenterX, 46f, bounds.CenterZ),
            Text = project.Name,
            Subtitle = project.IsTestProject
                ? $"test project Â· {project.Types.Count} types"
                : $"{project.Types.Count} types",
            Size = 4.2f,
            Color = project.IsTestProject
                ? new Vector4(0.62f, 0.92f, 0.60f, 1f)
                : new Vector4(0.70f, 0.86f, 1.00f, 1f),
            FadeDistance = 1200f,
            // The signs you navigate by; nothing should ever elbow these out.
            Priority = 9000,
        });

        // Namespace trie -> nested blocks.
        var root = new NamespaceNode();
        var typesById = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var type in project.Types.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            typesById[type.Id] = type;
            var node = root;
            foreach (var segment in type.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries))
                node = node.Descend(segment);
            node.Leaves.Add((type.Id, FootprintDemand(type, roles)));
        }
        root.Accumulate();

        AddStreetSign(scene, bounds, project.Name, district: true);
        LayBlock(scene, root.Collapse(), bounds, typesById, project, roles, depth: 0);
    }

    /// <summary>
    /// A signpost on the corner naming whatever this block is. Without it you can see the city is
    /// organised but not what any of the organisation means.
    /// </summary>
    static void AddStreetSign(SceneGraph scene, Bounds2 block, string name, bool district)
    {
        if (name.Length == 0 || block.Width < 6f || block.Depth < 6f) return;

        float height = district ? 5.5f : 3.4f;
        // Just inside the corner, on the street side, where you'd actually read it walking past.
        var post = new Vector3(block.X + 1.4f, 0f, block.Z + 1.4f);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = post,
            Size = new Vector3(0.22f, height, 0.22f),
            Color = new Vector4(0.28f, 0.29f, 0.31f, 1f),
            PickId = -1,
            Detail = 1f,
        });

        scene.Labels.Add(new WorldLabel
        {
            Position = post with { Y = height + 0.35f },
            Text = name,
            Size = district ? 1.25f : 0.85f,
            Color = district
                ? new Vector4(0.98f, 0.92f, 0.64f, 1f)
                : new Vector4(0.86f, 0.90f, 0.96f, 1f),
            FadeDistance = district ? 190f : 95f,
            // Above building nameplates: you orient by street name before building name.
            Priority = district ? 6000 : 2000,
        });
    }


    static void LayBlock(SceneGraph scene, NamespaceNode node, Bounds2 bounds,
        Dictionary<string, TypeNode> typesById, ProjectNode project,
        IReadOnlyDictionary<string, CivicRole> roles, int depth)
    {
        // No size guard on purpose. Any threshold here deletes a whole namespace subtree from the
        // city without saying so; PlaceBuilding clamps undersized lots instead and reports them.
        // Only a truly degenerate cell is refused, since it has no centre to build on.
        if (bounds.Width < 1e-5f || bounds.Depth < 1e-5f) return;

        // Children (sub-namespaces) and leaves (types) compete for the same block.
        var entries = new List<(object Item, float Weight)>();
        foreach (var child in node.Children.Values.OrderBy(c => c.Segment, StringComparer.Ordinal))
            entries.Add((child, child.Weight));
        foreach (var (id, weight) in node.Leaves)
            entries.Add((id, weight));

        if (entries.Count == 0) return;

        // A test district's recreation ground competes for space like any other plot rather than
        // being painted over the block afterwards. Laid on top, a court inevitably straddled the
        // internal streets, because those streets are cut *inside* the block it was covering.
        // As a treemap entry it gets its own cell and the cuts go around it.
        if (project.IsTestProject && depth == 0)
        {
            // Sized against a typical lot, not against the district: a share of the total gave a
            // 40-type district a park nineteen buildings wide, mostly empty. A few lots' worth is
            // what a court and some trees actually need, and it stays sane at any district size.
            float share = entries.Average(e => e.Weight) * 3.2f;
            entries.Add((new ParkPlot(StableHash.Combine((int)bounds.X, (int)bounds.Z)), share));
        }

        // Biggest first. The binary split cuts at the halfway-weight point, so feeding it an
        // arbitrary order lets one huge item sit beside many tiny ones and carve out slivers too
        // thin to build on. Sorting by weight is what keeps cells close to square - and the tie-break
        // on name keeps the city identical across runs.
        entries.Sort((a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0
                ? byWeight
                : string.CompareOrdinal(SortKey(a.Item), SortKey(b.Item));
        });

        // Streets narrow as you go deeper: boulevards between projects, streets between namespaces,
        // alleys within one. Every cell pulls back by half, so the two sides share one road.
        float streetWidth = MarginFor(bounds, depth == 0 ? StreetWidth : AlleyWidth, 1.2f);
        float half = streetWidth * 0.5f;
        var cuts = new List<Treemap.Cut>();

        Treemap.Layout(entries, bounds, (item, cell) =>
        {
            var inner = StreetNetwork.InsetFromCuts(cell, bounds, half);
            switch (item)
            {
                case NamespaceNode child:
                    AddStreetSign(scene, inner, child.DisplayName, district: false);
                    LayBlock(scene, child, inner, typesById, project, roles, depth + 1);
                    break;
                case string id:
                    PlaceBuilding(scene, typesById[id],
                        inner.Deflate(MarginFor(inner, LotPadding, 0.3f)), project, roles);
                    break;
                case ParkPlot park:
                    Greenery.AddParkland(scene, inner, 1f, park.Seed);
                    break;
            }
        }, cut => cuts.Add(cut));

        foreach (var cut in cuts)
            StreetNetwork.AddCut(scene, cut, streetWidth,
                depth == 0 ? RoadClass.Street : RoadClass.Alley);

    }

    /// <summary>A reserved plot for a test district's recreation ground.</summary>
    sealed record ParkPlot(int Seed);

    /// <summary>Deterministic tie-break for equally-weighted blocks and types.</summary>
    static string SortKey(object item) => item switch
    {
        NamespaceNode node => node.Segment,
        string id => id,
        ParkPlot => "￿park",   // sorts last among equal weights, deterministically
        _ => "",
    };

    // ---------------------------------------------------------------------------------------
    // A building is not one box. It's a plinth (inheritance depth), a stack of floor boxes (one
    // per method, each as tall as that method is long), roof antennas (interfaces), doors (public
    // constructors) and scattered props (smells).
    // ---------------------------------------------------------------------------------------

    static void PlaceBuilding(SceneGraph scene, TypeNode type, Bounds2 lot, ProjectNode project, IReadOnlyDictionary<string, CivicRole> roles)
    {
        float available = MathF.Min(lot.Width, lot.Depth);
        float side = MathF.Min(FootprintSide(type), available);

        // A building that overruns its lot is far better than a type that silently isn't in the
        // city at all - you can see and question the first, but never notice the second.
        if (side < MinBuildableSide)
        {
            side = MinBuildableSide;
            scene.CrampedLots++;
        }

        int pickId = scene.PickInfos.Count;
        scene.PickInfos.Add(new PickInfo
        {
            // Built from the parts, not the id: the id carries a project prefix to stay unique.
            DisplayName = type.Namespace == "<global>" ? type.Name : $"{type.Namespace}.{type.Name}",
            FilePath = type.FilePath,
            Line = type.Line,
            Loc = type.Loc,
            AvgComplexity = type.AvgComplexity,
            Kind = type.Kind,
            SmellLabels = type.Smells.Select(s => s.Count > 1 ? $"{s.Kind} x{s.Count}" : s.Kind.ToString())
                .ToList(),
            DiagnosticLabels = BuildDiagnosticLabels(type),
        });

        var center = new Vector3(lot.CenterX, 0f, lot.CenterZ);
        scene.Sites[type.Id] = new BuildingSite
        {
            Center = center,
            Side = side,
            ProjectName = project.Name,
            Namespace = type.Namespace,
            PickId = pickId,
        };

        var smells = type.Smells.ToDictionary(s => s.Kind, s => s.Count);
        bool dead = smells.ContainsKey(SmellKind.DeadCode);
        bool grimy = type.AvgComplexity >= GrimyComplexity && !dead;

        // Where the windows ended up, recorded as the storeys go up so that anything which ought to
        // come out of a window can find one. The alternative is recomputing the floor stack from the
        // same formula somewhere else, which works right up until one of the two copies changes.
        var storeys = new List<Storey>();

        // Deep hierarchies literally teeter: a narrow plinth raises the building one level per
        // step below the root of its inheritance chain.
        float plinthHeight = type.InheritanceDepth * 1.4f;
        if (plinthHeight > 0.01f)
        {
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center,
                Size = new Vector3(side * 0.55f, plinthHeight, side * 0.55f),
                Color = new Vector4(0.34f, 0.32f, 0.31f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }

        float y = plinthHeight;

        switch (type.Kind)
        {
            case TypeKind.Interface:
                // Hollow glass pavilion: low, transparent, no interior.
                scene.Boxes.Add(Box(center, y, side, 4.5f, new Vector4(0.42f, 0.68f, 0.84f, 0.30f),
                    pickId, BoxFlags.Glass, 1f));
                y += 4.5f;
                break;

            case TypeKind.Enum:
                // Kiosk with one illuminated slot per member.
                scene.Boxes.Add(Box(center, y, side, 2.8f, new Vector4(0.62f, 0.56f, 0.28f, 1f),
                    pickId, BoxFlags.Windows | BoxFlags.LitWindows,
                    MathF.Max(1, MathF.Min(type.EnumMemberCount, 10))));
                y += 2.8f;
                break;

            case TypeKind.Delegate:
                // Phone booth.
                scene.Boxes.Add(Box(center, y, MathF.Min(side, 2.2f), 3.2f,
                    new Vector4(0.35f, 0.30f, 0.45f, 0.65f), pickId, BoxFlags.Glass, 1f));
                y += 3.2f;
                break;

            case TypeKind.StaticClass:
                // Windowless doorless obelisk - you cannot enter a static class.
                float obelisk = MathF.Max(6f, 3f + type.Methods.Count * 1.5f);
                scene.Boxes.Add(Box(center, y, side * 0.62f, obelisk,
                    new Vector4(0.36f, 0.35f, 0.37f, 1f), pickId,
                    grimy ? BoxFlags.Grimy : BoxFlags.None, 1f));
                y += obelisk;
                break;

            default:
                y = StackFloors(scene, type, center, side, y, pickId, dead, grimy, storeys);
                break;
        }

        AddRoofDetail(scene, type, center, side, y, pickId, smells);
        Fixtures.Apply(scene, type, center, side, y, pickId);
        Vernacular.Apply(scene, type, lot, center, side, y, pickId);
        AddDoors(scene, type, center, side, plinthHeight, pickId);
        AddSmellProps(scene, type, lot, center, side, y, pickId, smells, storeys);
        AddNameplate(scene, type, center, side, y, smells);

        // Last, and deliberately so: `y` is the finished roof height and `side` the real footprint.
        // Civic dressing only ever reads those — it never gets a chance to change them.
        bool isLandmark = roles.TryGetValue(type.Id, out var role);
        if (isLandmark) CivicDressing.Apply(scene, type, role, center, side, y, pickId);

        // Health as a positive signal: a clean lot earns its trees. Landmarks are skipped — their
        // plaza already has planting, and a second ring would bury the portico and the plaque.
        if (!isLandmark) Greenery.PlantLot(scene, type, lot, center, side, y);

        // Recorded rather than acted on: a lot much bigger than the building standing on it can
        // take a car park, but only once the road network exists to clip it against.
        scene.Lots.Add(new LotRecord(lot, center, side, isLandmark, pickId));

        Conditions.Apply(scene, type, lot, center, side, y, pickId);
    }

    /// <summary>
    /// The sign over the building: what this thing is and how bad it is. Without it the whole
    /// encoding is a private language - you can see a building is wrong but not what it is.
    /// </summary>
    static void AddNameplate(SceneGraph scene, TypeNode type, Vector3 center, float side, float roofY,
        Dictionary<SmellKind, int> smells)
    {
        var subtitle = $"{KindLabel(type.Kind)} Â· {type.Loc} LOC Â· {type.Methods.Count}m";
        if (smells.Count > 0)
            subtitle += "  ! " + string.Join(" ", smells.OrderByDescending(s => s.Value)
                .Take(2).Select(s => SmellLabel(s.Key)));

        // Nameplate size tracks the building so a skyscraper's sign is readable from across town.
        float size = Math.Clamp(side * 0.16f, 0.85f, 2.4f);

        scene.Labels.Add(new WorldLabel
        {
            // Lifted a fixed 3 m above the size-scaled offset: on a low building the old height sat
            // inside the tree canopy, and a nameplate you have to duck under is no use.
            Position = center with { Y = roofY + size * 2.2f + 3f },
            Text = type.Name,
            Subtitle = subtitle,
            Size = size,
            Color = smells.ContainsKey(SmellKind.DeadCode)
                ? new Vector4(0.62f, 0.60f, 0.58f, 1f)
                : smells.Count > 0
                    ? new Vector4(1.00f, 0.78f, 0.42f, 1f)
                    : new Vector4(1f, 1f, 1f, 1f),
            FadeDistance = 60f + roofY * 3.5f,
            // Outranks floor signs: you need to know which building you're at before which method.
            Priority = 500,
        });
    }

    static string KindLabel(TypeKind kind) => kind switch
    {
        TypeKind.StaticClass => "static class",
        TypeKind.AbstractClass => "abstract class",
        TypeKind.Interface => "interface",
        TypeKind.Struct => "struct",
        TypeKind.Record => "record",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => "class",
    };

    static string SmellLabel(SmellKind kind) => kind switch
    {
        SmellKind.GodClass => "GOD CLASS",
        SmellKind.LongMethod => "long method",
        SmellKind.LongParameterList => "long params",
        SmellKind.DeadCode => "DEAD",
        SmellKind.TodoComment => "TODO",
        SmellKind.CommentedOutCode => "dead comments",
        SmellKind.EmptyCatch => "SWALLOWED EXC",
        SmellKind.NotImplemented => "not impl",
        SmellKind.PublicMutableField => "public fields",
        SmellKind.StaticMutableState => "static state",
        SmellKind.RegionAbuse => "regions",
        SmellKind.OversizedFile => "huge file",
        SmellKind.CircularDependency => "CYCLE",
        _ => kind.ToString(),
    };

    /// <summary>
    /// One box per method, stacked. Floor height tracks the method's length, so a 200-line method
    /// becomes a single grotesquely stretched storey you can spot from the street. Each floor lights
    /// up at night only if its method is public - that's the API-surface view.
    /// </summary>
    /// <summary>
    /// One storey of a building, and where its windows are.
    /// </summary>
    /// <remarks>
    /// Mirrors what the fragment shader does with the same numbers: one row of windows per storey,
    /// <see cref="Windows"/> of them across each facade, each centred in its own column and a little
    /// above the middle of the floor. The shader only draws them at all above a certain storey
    /// height, and <see cref="HasWindows"/> repeats that bar so nothing is placed on a blank wall.
    /// </remarks>
    internal readonly record struct Storey(float BaseY, float Height, int Windows)
    {
        /// <summary>Matches the shader's <c>vStoreyHeight &gt; 2.0</c> gate.</summary>
        public bool HasWindows => Height > 2f;

        /// <summary>Height of the window row's centre, in world units.</summary>
        public float WindowY => BaseY + Height * 0.52f;

        /// <summary>
        /// Offset of one window's centre from the middle of the facade, as a fraction of the side.
        /// </summary>
        public float WindowAcross(int index) =>
            (index + 0.5f) / MathF.Max(Windows, 1) - 0.5f;
    }

    static float StackFloors(SceneGraph scene, TypeNode type, Vector3 center, float side, float y,
        int pickId, bool dead, bool grimy, List<Storey> storeys)
    {
        var baseColor = KindColor(type.Kind);
        var flags = BoxFlags.Windows;
        if (grimy) flags |= BoxFlags.Grimy;
        if (dead) flags |= BoxFlags.Abandoned;
        if (type.Kind == TypeKind.AbstractClass) flags |= BoxFlags.Scaffold;

        // A type with no methods holds state and does nothing with it, so it gets a warehouse. The
        // height is the same as when it was an anonymous squat storey: what it looks like changed,
        // how big it is did not.
        if (type.Methods.Count == 0)
        {
            float h = MathF.Max(2.8f, (type.FieldCount + type.PropertyCount) * 0.35f + 2.5f);
            Vernacular.Warehouse(scene, type, center, side, y, h, pickId);

            // No methods means no floors to label, but the data it holds is still worth naming.
            scene.Labels.Add(new WorldLabel
            {
                Position = center with { Y = y + h * 0.5f },
                Text = type.Kind == TypeKind.Enum
                    ? $"{type.EnumMemberCount} values"
                    : $"{type.FieldCount} fields Â· {type.PropertyCount} properties",
                Size = 0.42f,
                Color = new Vector4(0.78f, 0.82f, 0.88f, 1f),
                FadeDistance = 34f,
                FaceRadius = side * BoxCircumradius + FacadeSignClearance,
                Priority = 10,
            });
            return y + h;
        }

        foreach (var method in type.Methods)
        {
            float height = Math.Clamp(1.8f + method.Loc * 0.16f, MinFloorHeight, MaxFloorHeight);
            var floorFlags = flags;
            if (method.IsPublic && !dead) floorFlags |= BoxFlags.LitWindows;

            // A floor no test reaches goes damp. Coverage is per method and a floor is a method, so
            // this is the one channel in the city whose grain matches its metric exactly.
            //
            // The -1 check is the whole point: unmeasured is not the same as uncovered. Without a
            // coverage report every floor of every building would be damp, and the city would read
            // as untested when it is merely unmeasured.
            if (method.Coverage >= 0 && method.Coverage < UncoveredBelow)
                floorFlags |= BoxFlags.Damp;

            // Slight per-floor tint variation keeps a tall stack from reading as one flat slab.
            float tint = 0.94f + StableRandom(type.Id, method.Name.GetHashCode()) * 0.12f;

            // Windows across the facade = parameters. A 6-parameter method is a wall of glass.
            int windows = Math.Clamp(method.ParameterCount, 1, 8);
            storeys.Add(new Storey(y, height, windows));

            var floorBox = Box(center, y, side, height,
                new Vector4(baseColor.X * tint, baseColor.Y * tint, baseColor.Z * tint, baseColor.W),
                pickId, floorFlags, windows);
            floorBox.Damage = NullDamage(type);
            scene.Boxes.Add(floorBox);

            // An async method is the storey you wait on, so it gets its own external lift.
            if (method.IsAsync) Fixtures.AddLiftShaft(scene, center, side, y, height, pickId);

            // A sign on the floor itself: read the class from the street, read its members up close.
            scene.Labels.Add(new WorldLabel
            {
                Position = center with { Y = y + height * 0.5f },
                Text = $"{method.ReturnType} {method.Name}",
                Subtitle = $"{(method.IsPublic ? "public" : "private")} Â· {method.Loc} LOC Â· " +
                           $"{method.ParameterCount}p Â· cx{method.Complexity}",
                Size = 0.42f,
                Color = method.IsPublic
                    ? new Vector4(1.00f, 0.93f, 0.78f, 1f)
                    : new Vector4(0.66f, 0.72f, 0.80f, 1f),
                // Short range: these are for standing at the foot of a building, not for the skyline.
                FadeDistance = 34f,
                FaceRadius = side * BoxCircumradius + FacadeSignClearance,
                Priority = 10,
            });

            y += height;
        }

        // Abstract classes are never finished: the top storey is open scaffolding.
        if (type.Kind == TypeKind.AbstractClass)
        {
            scene.Boxes.Add(Box(center, y, side * 1.04f, 1.8f,
                new Vector4(0.78f, 0.56f, 0.12f, 0.55f), pickId, BoxFlags.Scaffold | BoxFlags.Glass, 1f));
            y += 1.8f;
        }

        return y;
    }

    /// <summary>One antenna per implemented interface, plus hazard lights on a god class.</summary>
    static void AddRoofDetail(SceneGraph scene, TypeNode type, Vector3 center, float side, float roofY,
        int pickId, Dictionary<SmellKind, int> smells)
    {
        int antennas = Math.Min(type.Interfaces.Count, 8);
        for (int i = 0; i < antennas; i++)
        {
            float angle = i / (float)Math.Max(antennas, 1) * MathF.Tau;
            float radius = side * 0.32f;
            float height = 2.5f + StableRandom(type.Id, i * 31) * 3f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(MathF.Cos(angle) * radius, roofY, MathF.Sin(angle) * radius),
                Size = new Vector3(0.22f, height, 0.22f),
                Color = new Vector4(0.72f, 0.74f, 0.78f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }

        if (smells.ContainsKey(SmellKind.GodClass))
        {
            // Red aviation hazard light: this thing is tall enough to be a navigation obstacle.
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(0, roofY, 0),
                Size = new Vector3(0.9f, 0.9f, 0.9f),
                Color = new Vector4(1.0f, 0.15f, 0.12f, 1f),
                PickId = pickId,
                Flags = (uint)BoxFlags.Emissive,
                Detail = 1f,
            });
        }
    }

    /// <summary>
    /// A tower crane beside the building: work started and not finished, which is exactly what a
    /// TODO is. It stands taller than the building it serves, so unfinished work is legible from
    /// across the city rather than something you have to be standing next to.
    /// </summary>
    /// <remarks>
    /// Scaffolding was the obvious choice and it failed in practice — hugging the facade, it was
    /// invisible among a thousand buildings. A crane wins because its silhouette is unlike anything
    /// else in the city: nothing else has a long horizontal arm in the sky.
    /// </remarks>
    static void AddCrane(SceneGraph scene, TypeNode type, Vector3 center, float side,
        float roofY, int todos, int pickId)
    {
        var steel = new Vector4(0.86f, 0.62f, 0.10f, 1f);
        var cable = new Vector4(0.24f, 0.23f, 0.22f, 1f);

        // More outstanding work, taller crane — and always at least eight metres clear of its own
        // roof, so the jib swings over the building rather than through it.
        float mastTop = roofY + Math.Clamp(7f + todos * 2.2f, 8f, 30f);
        float jib = side * 0.9f + 7f;

        // Stands at the lot's edge, the way a real site keeps the crane off the footprint.
        float bearing = StableRandom(type.Id, 313) * MathF.Tau;
        var foot = center + new Vector3(
            MathF.Cos(bearing) * (side * 0.5f + 2.4f), 0f,
            MathF.Sin(bearing) * (side * 0.5f + 2.4f));

        // Concrete pad and mast.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot,
            Size = new Vector3(2.6f, 0.5f, 2.6f),
            Color = new Vector4(0.42f, 0.42f, 0.43f, 1f),
            PickId = pickId,
            Detail = 1f,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot,
            Size = new Vector3(0.85f, mastTop, 0.85f),
            Color = steel,
            PickId = pickId,
            Detail = 1f,
        });

        // Operator's cab where the jib meets the mast.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot + new Vector3(0f, mastTop - 1.6f, 0f),
            Size = new Vector3(1.5f, 1.6f, 1.5f),
            Color = new Vector4(0.30f, 0.34f, 0.40f, 1f),
            PickId = pickId,
            Detail = 1f,
        });

        // Jib reaching over the building, and the short counter-jib opposite it.
        var reach = new Vector3(MathF.Cos(bearing + MathF.PI), 0f, MathF.Sin(bearing + MathF.PI));
        bool alongX = MathF.Abs(reach.X) >= MathF.Abs(reach.Z);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot + reach * (jib * 0.5f) + new Vector3(0f, mastTop, 0f),
            Size = alongX ? new Vector3(jib, 0.5f, 0.45f) : new Vector3(0.45f, 0.5f, jib),
            Color = steel,
            PickId = pickId,
            Detail = 1f,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot - reach * 2.6f + new Vector3(0f, mastTop, 0f),
            Size = alongX ? new Vector3(5.2f, 0.45f, 0.45f) : new Vector3(0.45f, 0.45f, 5.2f),
            Color = steel,
            PickId = pickId,
            Detail = 1f,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot - reach * 4.6f + new Vector3(0f, mastTop - 0.6f, 0f),
            Size = new Vector3(1.5f, 1.5f, 1.5f),
            Color = new Vector4(0.38f, 0.38f, 0.39f, 1f),   // counterweight
            PickId = pickId,
            Detail = 1f,
        });

        // Hoist cable and its load, hanging over the building.
        var hoist = foot + reach * (jib * 0.62f);
        float drop = mastTop * 0.45f;
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = hoist + new Vector3(0f, mastTop - drop, 0f),
            Size = new Vector3(0.1f, drop, 0.1f),
            Color = cable,
            PickId = pickId,
            Detail = 1f,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = hoist + new Vector3(0f, mastTop - drop - 1.1f, 0f),
            Size = new Vector3(1.3f, 1.1f, 1.3f),
            Color = new Vector4(0.55f, 0.45f, 0.30f, 1f),
            PickId = pickId,
            Detail = 1f,
        });

        // Obstruction light on the mast head — finds the crane at night too.
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = foot + new Vector3(0f, mastTop + 0.5f, 0f),
            Size = new Vector3(0.6f, 0.6f, 0.6f),
            Color = new Vector4(1.0f, 0.18f, 0.14f, 1f),
            PickId = pickId,
            Flags = (uint)BoxFlags.Emissive,
            Detail = 1f,
        });
    }

    /// <summary>
    /// The build's own complaints, phrased so the card explains what you're looking at: broken glass
    /// means nullable warnings, and the count is what makes it something you can go and fix.
    /// </summary>
    static List<string> BuildDiagnosticLabels(TypeNode type)
    {
        var labels = new List<string>();
        if (type.CompileErrors > 0) labels.Add($"{type.CompileErrors} compile error(s) - on fire");
        if (type.NullWarnings > 0) labels.Add($"{type.NullWarnings} nullable warning(s) - broken glass");
        if (type.UnusedWarnings > 0) labels.Add($"{type.UnusedWarnings} unused/unreachable - rubbish");
        if (type.ObsoleteWarnings > 0) labels.Add($"{type.ObsoleteWarnings} obsolete API use - condemned");
        if (type.OtherWarnings > 0) labels.Add($"{type.OtherWarnings} other warning(s)");
        return labels;
    }

    /// <summary>
    /// Fraction of a building's window panes smashed, from its nullable-reference warnings.
    /// </summary>
    /// <remarks>
    /// Capped below 1 on purpose: even the worst offender keeps some glass, so "totally derelict"
    /// stays available for genuinely dead code. Measured on a large real-world solution, the worst
    /// type had 18 warnings, so a divisor of 14 puts it near the cap while a single warning is one
    /// broken pane here and there.
    /// </remarks>
    static float NullDamage(TypeNode type) =>
        Math.Clamp(type.NullWarnings / 14f, 0f, 0.82f);

    /// <summary>
    /// A window fire: a bed of flame licking out of the facade, tapering tongues above it, embers,
    /// and a smoke column leaning over the district.
    /// </summary>
    /// <remarks>
    /// Built as a taper rather than one box because a single cube reads as an orange crate, not a
    /// fire. The shape does the work in daylight; the flicker and the night-boosted emission in the
    /// shader do it after dark, when a swallowed exception should be the brightest thing around.
    /// </remarks>
    /// <summary>
    /// Picks a window on the building, and which way it faces.
    /// </summary>
    /// <remarks>
    /// Fire used to be placed at a random height on a random point of the east wall, which put it in
    /// front of blank render as often as not and made it read as floating in mid-air beside the
    /// building. A fire comes <em>out</em> of something. Reading the storey list back gives the exact
    /// window the shader drew, so the flame starts where the glass is.
    ///
    /// Falls back to a point on the wall when the building has no windows to speak of — an obelisk,
    /// or a stack of one-line methods too short for the shader to draw glass on.
    /// </remarks>
    static (Vector3 At, Vector3 Outward) WindowOn(TypeNode type, IReadOnlyList<Storey> storeys,
        Vector3 center, float side, float roofY, int seed)
    {
        // Faces in a fixed order, so a building with several fires spreads them round it rather than
        // stacking them all on one wall.
        var outward = (seed & 3) switch
        {
            0 => new Vector3(1f, 0f, 0f),
            1 => new Vector3(0f, 0f, 1f),
            2 => new Vector3(-1f, 0f, 0f),
            _ => new Vector3(0f, 0f, -1f),
        };
        var across = new Vector3(-outward.Z, 0f, outward.X);

        var glazed = storeys.Where(s => s.HasWindows).ToList();
        if (glazed.Count == 0)
        {
            float loose = 2.5f + StableRandom(type.Id, seed * 23 + 3) * MathF.Max(roofY - 4f, 1f);
            return (center + outward * (side * 0.5f)
                    + across * ((StableRandom(type.Id, seed * 19) - 0.5f) * side * 0.7f)
                    + Vector3.UnitY * loose,
                outward);
        }

        var storey = glazed[(int)(StableRandom(type.Id, seed * 37 + 11) * glazed.Count) % glazed.Count];
        int window = (int)(StableRandom(type.Id, seed * 41 + 5) * storey.Windows) % storey.Windows;

        return (center
                + outward * (side * 0.5f)
                + across * (storey.WindowAcross(window) * side)
                + Vector3.UnitY * storey.WindowY,
            outward);
    }

    static void AddFire(SceneGraph scene, TypeNode type, Vector3 wall, Vector3 outward, int index,
        int pickId)
    {
        const int Tongues = 5;
        float scale = 1.15f + StableRandom(type.Id, index * 31 + 7) * 0.7f;
        var across = new Vector3(-outward.Z, 0f, outward.X);

        // Wide, hot bed at the window, narrowing and rising into tongues of flame that lean out of
        // the opening and back against the wall as they climb — which is what a window fire does,
        // and what tells you at a glance which floor it is on.
        for (int i = 0; i < Tongues; i++)
        {
            float t = i / (float)Tongues;
            float lean = (StableRandom(type.Id, index * 29 + i * 5) - 0.5f) * 1.3f * t;

            // Furthest out at the sill, drawn back towards the facade as the flame rises.
            float reach = 0.35f + (0.55f - t * 0.5f) * scale;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = wall + outward * reach + across * lean
                               + Vector3.UnitY * (t * 3.0f * scale),
                Size = new Vector3(
                    (2.1f - t * 1.25f) * scale,
                    (2.0f - t * 0.7f) * scale,
                    (2.1f - t * 1.25f) * scale),
                // Deep red at the base, yellow-white at the tips; the shader adds the gradient
                // within each box, this sets the range across the stack.
                Color = new Vector4(1.0f, 0.20f + t * 0.55f, 0.03f + t * 0.30f, 1f),
                PickId = pickId,
                Flags = (uint)BoxFlags.Fire,
                Detail = 1f,
            });
        }

        // Embers drifting off the top.
        for (int i = 0; i < 3; i++)
        {
            float spread = StableRandom(type.Id, index * 37 + i * 11) - 0.5f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = wall + new Vector3(spread * 2.2f, 2.8f + i * 1.1f, spread * 1.8f),
                Size = new Vector3(0.22f, 0.22f, 0.22f),
                Color = new Vector4(1.0f, 0.62f, 0.18f, 1f),
                PickId = -1,
                Flags = (uint)BoxFlags.Fire,
                Detail = 1f,
            });
        }

        // Smoke, leaning further downwind and thinning as it climbs.
        for (int s = 1; s <= 7; s++)
        {
            float drift = s * 0.55f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = wall + new Vector3(drift, 2.4f + s * 3.1f, drift * 0.4f),
                Size = new Vector3(1.8f + s * 0.85f, 3.2f, 1.8f + s * 0.85f),
                // The first puff still catches the firelight; the rest are soot.
                Color = s == 1
                    ? new Vector4(0.40f, 0.28f, 0.20f, 0.34f)
                    : new Vector4(0.22f, 0.21f, 0.20f, MathF.Max(0.05f, 0.30f - s * 0.035f)),
                PickId = -1,
                Flags = (uint)BoxFlags.Smoke,
                Detail = 1f,
            });
        }
    }

    /// <summary>Public constructors are the doors. No public constructor, no way in.</summary>
    static void AddDoors(SceneGraph scene, TypeNode type, Vector3 center, float side, float groundY,
        int pickId)
    {
        int doors = Math.Min(type.PublicCtorCount, 5);
        if (doors == 0 || type.Kind is TypeKind.Enum or TypeKind.Delegate or TypeKind.StaticClass) return;

        float spacing = side / (doors + 1);
        for (int i = 0; i < doors; i++)
        {
            float offsetX = -side * 0.5f + spacing * (i + 1);
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(offsetX, groundY, side * 0.5f),
                Size = new Vector3(MathF.Min(1.1f, spacing * 0.7f), 2.2f, 0.25f),
                Color = new Vector4(0.20f, 0.16f, 0.13f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <summary>The Â§1.5 smell catalogue, as physical objects on the lot.</summary>
    static void AddSmellProps(SceneGraph scene, TypeNode type, Bounds2 lot, Vector3 center, float side,
        float roofY, int pickId, Dictionary<SmellKind, int> smells, IReadOnlyList<Storey> storeys)
    {
        // A crane for work somebody wrote down and never came back to.
        //
        // The cones that used to stand here have gone to the history layer, where they mark a file
        // being committed to this week. Splitting them is what keeps either one readable: a crane is
        // a site that has been standing a long time, cones are today's disruption, and a building
        // wearing both at once is a TODO on something under active change.
        if (smells.TryGetValue(SmellKind.TodoComment, out int todos))
        {
            // roofY here is the finished height of the actual floor stack, so the mast always clears
            // the building. Sizing it from a guess at the roof is what put jibs through the upper
            // storeys of anything taller than the guess.
            AddCrane(scene, type, center, side, roofY, todos, pickId);

            scene.Interest.Add(new PointOfInterest
            {
                Focus = center with { Y = roofY * 0.6f },
                Distance = MathF.Max(side * 2f, 38f),
                Headline = "UNFINISHED WORK",
                Detail = $"{type.Name} · {todos} TODO(s)",
            });
        }

        // Unused and unreachable code: rubbish nobody has cleared off the lot.
        for (int i = 0; i < Math.Min(type.UnusedWarnings, 10); i++)
        {
            var spot = ScatterOnLot(type.Id, lot, side, i * 7 + 91);
            float bulk = 0.5f + StableRandom(type.Id, i * 5 + 3) * 0.7f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = spot,
                Size = new Vector3(bulk, 0.35f + bulk * 0.4f, bulk * 0.8f),
                Color = new Vector4(0.30f, 0.28f, 0.24f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }

        // Obsolete API usage: the building is condemned, boarded diagonally across the entrance.
        if (type.ObsoleteWarnings > 0)
        {
            for (int i = 0; i < 2; i++)
            {
                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = center + new Vector3(0f, 1.4f + i * 1.1f, side * 0.5f + 0.1f),
                    Size = new Vector3(side * 0.9f, 0.3f, 0.14f),
                    Color = new Vector4(0.44f, 0.30f, 0.16f, 1f),
                    PickId = pickId,
                    Detail = 1f,
                });
            }
        }

        // A compile error is an active emergency: the building is ablaze, not merely smouldering.
        for (int i = 0; i < Math.Min(type.CompileErrors, 4); i++)
        {
            var (at, outward) = WindowOn(type, storeys, center, side, roofY, i + 40);
            AddFire(scene, type, at, outward, i + 40, pickId);
        }

        // A swallowed exception is a fire nobody is coming to put out.
        if (smells.TryGetValue(SmellKind.EmptyCatch, out int fires))
        {
            for (int i = 0; i < Math.Min(fires, 6); i++)
            {
                var (at, outward) = WindowOn(type, storeys, center, side, roofY, i);
                AddFire(scene, type, at, outward, i, pickId);
            }
        }

        // Dead code: weeds coming up through the pavement.
        if (smells.ContainsKey(SmellKind.DeadCode))
        {
            for (int i = 0; i < 8; i++)
            {
                var spot = ScatterOnLot(type.Id, lot, side, i * 11 + 41);
                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = spot,
                    Size = new Vector3(0.35f, 0.5f + StableRandom(type.Id, i * 3) * 0.7f, 0.35f),
                    Color = new Vector4(0.29f, 0.42f, 0.18f, 1f),
                    PickId = pickId,
                    Detail = 1f,
                });
            }
        }

        // Static mutable state: a humming substation squatting on the lot.
        if (smells.ContainsKey(SmellKind.StaticMutableState))
        {
            var spot = center + new Vector3(-side * 0.5f - 1.6f, 0, side * 0.3f);
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = spot,
                Size = new Vector3(2.2f, 1.6f, 1.6f),
                Color = new Vector4(0.42f, 0.40f, 0.36f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = spot + new Vector3(0, 1.6f, 0),
                Size = new Vector3(0.18f, 3.2f, 0.18f),
                Color = new Vector4(0.60f, 0.58f, 0.52f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }

        // NotImplementedException: hazard tape around a hole in the floor.
        if (smells.ContainsKey(SmellKind.NotImplemented))
        {
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = center + new Vector3(0, 0.05f, 0),
                Size = new Vector3(side * 1.15f, 0.12f, side * 1.15f),
                Color = new Vector4(0.92f, 0.82f, 0.10f, 1f),
                PickId = pickId,
                Detail = 1f,
            });
        }
    }

    /// <param name="clearance">
    /// Half the prop's own footprint, so a large object lands wholly inside the lot.
    /// </param>
    /// <remarks>
    /// The clamp is on the prop's <em>centre</em>, which is fine for a traffic cone and not fine for
    /// anything substantial: a skip nearly three metres long, centred on the boundary, puts half of
    /// itself in the road. Only the caller knows how big its prop is, so only the caller can say.
    /// </remarks>
    internal static Vector3 ScatterOnLot(string seed, Bounds2 lot, float buildingSide, int salt,
        float clearance = 0f)
    {
        // Keep props off the building footprint but inside the lot.
        float angle = StableRandom(seed, salt) * MathF.Tau;
        float ring = buildingSide * 0.5f + 0.6f + clearance + StableRandom(seed, salt + 1) * 2.2f;

        // Never inset past the middle: on a lot barely bigger than its building there is nowhere
        // legal to stand, and the centre is a better answer than a negative-width range.
        float insetX = MathF.Min(clearance, lot.Width * 0.45f);
        float insetZ = MathF.Min(clearance, lot.Depth * 0.45f);

        float x = Math.Clamp(lot.CenterX + MathF.Cos(angle) * ring,
            lot.X + insetX, lot.X + lot.Width - insetX);
        float z = Math.Clamp(lot.CenterZ + MathF.Sin(angle) * ring,
            lot.Z + insetZ, lot.Z + lot.Depth - insetZ);
        return new Vector3(x, 0f, z);
    }

    static BoxInstance Box(Vector3 center, float y, float side, float height, Vector4 color,
        int pickId, BoxFlags flags, float detail) => new()
    {
        BasePosition = center with { Y = y },
        Size = new Vector3(side, height, side),
        Color = color,
        PickId = pickId,
        Flags = (uint)flags,
        Detail = detail,
    };

    static Vector4 KindColor(TypeKind kind) => kind switch
    {
        TypeKind.Class => new Vector4(0.60f, 0.58f, 0.55f, 1f),
        TypeKind.StaticClass => new Vector4(0.36f, 0.35f, 0.37f, 1f),
        TypeKind.AbstractClass => new Vector4(0.66f, 0.55f, 0.36f, 1f),
        TypeKind.Interface => new Vector4(0.42f, 0.68f, 0.84f, 0.30f),
        TypeKind.Struct => new Vector4(0.70f, 0.63f, 0.50f, 1f),
        TypeKind.Record => new Vector4(0.58f, 0.65f, 0.56f, 1f),
        TypeKind.Enum => new Vector4(0.62f, 0.56f, 0.28f, 1f),
        TypeKind.Delegate => new Vector4(0.48f, 0.44f, 0.56f, 1f),
        _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
    };

    /// <summary>Stable per-name pseudo-random in [0,1). Jitter that survives re-runs.</summary>
    internal static float StableRandom(string seed, int salt = 0)
    {
        unchecked
        {
            uint h = 2166136261u ^ (uint)salt;
            foreach (char c in seed)
            {
                h ^= c;
                h *= 16777619u;
            }
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}



