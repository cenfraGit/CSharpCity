using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Populates the city with movement: foot traffic that carries meaning, and road traffic that
/// carries none.
/// </summary>
/// <remarks>
/// The split is the whole design, and it exists because the two previous attempts each failed in an
/// instructive way. Drawing dependencies as asphalt roads made the graph fight the city â€” heavy
/// slabs cutting across lots at arbitrary angles. Routing dependencies through the street grid fixed
/// the look but destroyed the reading: once a route bends around three blocks you can no longer see
/// which two buildings it connects, which was the only thing it was for.
///
/// So: <b>a dependency is a desire line.</b> A straight, worn footpath from one building to another,
/// with people on it, because the shortest line between two doors is exactly what tells you those
/// two types talk. Density of people is the reference count, so a busy path is a heavily used
/// coupling. Meanwhile the street grid keeps ordinary cars driving around the blocks â€” they mean
/// nothing at all, they just make the place read as a city instead of a diagram.
/// </remarks>
internal static class TrafficNetwork
{
    /// <summary>
    /// Paths are cheap, but past this many the ground turns to spaghetti and reads as noise. Sized
    /// comfortably above the edge count of a large real-world solution so it isn't silently
    /// truncated; the renderer's distance culling, not this cap, is what keeps the frame rate.
    /// </summary>
    const int MaxFootpaths = 9000;

    public sealed record Result(int Cycles, int Footpaths, int Skipped);

    public static Result Build(SceneGraph scene, CityModel model)
    {
        var edges = model.Edges
            .Where(e => e.FromId != e.ToId
                        && scene.Sites.ContainsKey(e.FromId)
                        && scene.Sites.ContainsKey(e.ToId))
            .ToList();

        var cycles = CycleFinder.Find(edges, scene.Sites.Keys);
        var inCycle = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in cycles)
            foreach (var id in group)
                inCycle.Add(id);

        // Busiest first, so if the cap bites it drops the couplings that matter least.
        var walkable = edges
            .Where(e => !(inCycle.Contains(e.FromId) && inCycle.Contains(e.ToId)))
            .OrderByDescending(e => e.Weight)
            .ToList();

        int skipped = Math.Max(0, walkable.Count - MaxFootpaths);
        if (skipped > 0) walkable = walkable.Take(MaxFootpaths).ToList();

        // What is already paved, so a worn path can be laid on the bare ground between it.
        var ground = new GroundHeights(scene, scene.CityBounds, CityLayout.FootpathSurfaceY);

        int built = 0;
        foreach (var edge in walkable)
            if (LayFootpath(scene, edge, ground)) built++;

        foreach (var group in cycles)
            Roundabout.Lay(scene, group);

        return new Result(cycles.Count, built, skipped);
    }

    /// <summary>How often the path is sampled against the ground beneath it.</summary>
    const float SampleStep = 3f;

    /// <summary>Fills in gaps shorter than <paramref name="shortest"/> samples.</summary>
    static void Smooth(bool[] flags, int shortest)
    {
        for (int i = 0; i < flags.Length; i++)
        {
            if (flags[i]) continue;

            int run = i;
            while (run + 1 < flags.Length && !flags[run + 1]) run++;

            // Only an interruption between two stretches of path counts; a gap running off either
            // end is the path genuinely starting or finishing on paved ground.
            bool interior = i > 0 && run < flags.Length - 1;
            if (interior && run - i + 1 <= shortest)
                for (int k = i; k <= run; k++) flags[k] = true;

            i = run;
        }
    }

    /// <summary>A straight worn path between two buildings, with walkers on it.</summary>
    static bool LayFootpath(SceneGraph scene, DependencyEdge edge, GroundHeights ground)
    {
        var origin = scene.Sites[edge.FromId];
        var destination = scene.Sites[edge.ToId];

        var delta = destination.Center - origin.Center;
        float span = new Vector2(delta.X, delta.Z).Length();

        // Start and finish at the doors, not the centres, so the path doesn't vanish under a tower.
        float trimFrom = origin.Side * 0.5f;
        float trimTo = destination.Side * 0.5f;
        if (span <= trimFrom + trimTo + 2f) return false;   // adjacent buildings: a path is noise

        var direction = new Vector3(delta.X / span, 0f, delta.Z / span);
        var start = (origin.Center + direction * trimFrom) with { Y = CityLayout.FootpathSurfaceY };
        var end = (destination.Center - direction * trimTo) with { Y = CityLayout.FootpathSurfaceY };

        var boundary = Classify(origin, destination);
        float length = Vector3.Distance(start, end);
        float width = Math.Clamp(0.75f + MathF.Log(edge.Weight + 1f) * 0.30f, 0.75f, 2.1f);
        float yaw = MathF.Atan2(delta.Z, delta.X);

        // Sampled rather than drawn as one ribbon. Worn ground shows only where the ground is
        // actually bare, and the walkers rise onto whatever they are crossing — which is what
        // stops a crowd from wading through the road surface, and stops them hovering over it.
        int steps = Math.Max(2, (int)(length / SampleStep));
        float step = length / steps;

        var waypoints = new List<Vector3>(steps + 1);
        var bare = new bool[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            var point = start + direction * (step * i);
            waypoints.Add(point with { Y = ground.At(point) });
            bare[i] = ground.IsBare(point);
        }

        // Ignore short interruptions. Without this a path is chopped at every kerb, gutter and
        // three-metre alley it brushes, turning one worn line into twenty stubs — the same amount
        // of information at five times the cost, and visibly dashed rather than worn.
        Smooth(bare, (int)MathF.Ceiling(6f / step));

        for (int i = 0; i <= steps; i++)
        {
            if (!bare[i]) continue;
            int run = i;
            while (run + 1 <= steps && bare[run + 1]) run++;

            float from = step * i, to = step * run;
            if (to - from > 1.5f)
            {
                var middle = start + direction * ((from + to) * 0.5f);
                scene.Roads.Add(new RoadQuad
                {
                    Center = middle with { Y = CityLayout.FootpathSurfaceY },
                    Length = to - from,
                    // Wear, not volume: even a heavy dependency is a path, not a motorway.
                    Width = width,
                    Yaw = yaw,
                    Color = PathColor(boundary),
                    Flags = (uint)RoadFlags.Footpath,
                    Layer = CityLayer.Footpaths,
                });
            }

            i = run;
        }

        int pathIndex = AddPath(scene, waypoints.ToArray(), loop: false);

        // Density is the reference count: a busy path is a heavily used coupling.
        int walkers = Math.Clamp(edge.Weight, 1, 9);
        for (int i = 0; i < walkers; i++)
        {
            scene.Travellers.Add(new Traveller
            {
                PathIndex = pathIndex,
                Phase = (float)i / walkers,
                // Slight spread so a crowd doesn't march in lockstep.
                Speed = 1.15f + (i % 3) * 0.18f,
                Color = WalkerColor(boundary),
                Kind = TravellerKind.Pedestrian,
                Layer = CityLayer.Walkers,
            });
        }

        return true;
    }

    /// <summary>How far a dependency reaches. Colour only â€” the shape of the path is the same.</summary>
    enum Boundary { SameNamespace, CrossNamespace, CrossProject }

    static Boundary Classify(BuildingSite from, BuildingSite to)
    {
        if (!string.Equals(from.ProjectName, to.ProjectName, StringComparison.Ordinal))
            return Boundary.CrossProject;
        return string.Equals(from.Namespace, to.Namespace, StringComparison.Ordinal)
            ? Boundary.SameNamespace
            : Boundary.CrossNamespace;
    }

    static Vector4 PathColor(Boundary boundary) => boundary switch
    {
        Boundary.SameNamespace => new Vector4(0.40f, 0.38f, 0.33f, 1f),   // trodden earth
        Boundary.CrossNamespace => new Vector4(0.34f, 0.40f, 0.44f, 1f),  // worn stone
        _ => new Vector4(0.46f, 0.34f, 0.18f, 1f),                        // the long haul
    };

    static Vector4 WalkerColor(Boundary boundary) => boundary switch
    {
        Boundary.SameNamespace => new Vector4(0.88f, 0.83f, 0.70f, 1f),
        Boundary.CrossNamespace => new Vector4(0.66f, 0.86f, 0.96f, 1f),
        _ => new Vector4(1.00f, 0.64f, 0.26f, 1f),
    };

    internal static int AddPath(SceneGraph scene, Vector3[] waypoints, bool loop)
    {
        var cumulative = new float[waypoints.Length];
        float total = 0f;
        for (int i = 1; i < waypoints.Length; i++)
        {
            total += Vector3.Distance(waypoints[i - 1], waypoints[i]);
            cumulative[i] = total;
        }

        scene.Paths.Add(new TrafficPath
        {
            Points = waypoints,
            Cumulative = cumulative,
            Length = total,
            Loop = loop,
        });
        return scene.Paths.Count - 1;
    }
}

