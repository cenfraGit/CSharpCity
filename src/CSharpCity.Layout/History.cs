using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// What the repository remembers, made visible: construction where the code is moving, and bicycles
/// for the people who move it.
/// </summary>
/// <remarks>
/// Two channels from one data source, kept separate on purpose.
///
/// <b>Churn is disruption.</b> A file committed to this week gets cones on its lot — today's work,
/// put out and taken in again. It briefly had the crane instead, and that was wrong twice over: a
/// crane is a site that has been standing for months, which is what an unfinished TODO is, and two
/// hundred and eighty thirty-metre cranes turned the skyline into scaffolding. Skips for the volume
/// of lines moved were tried alongside and removed: nobody could tell what they were, and an object
/// that has to be explained is noise that still costs attention. The pairing with grime is where
/// this earns its place — grime already means complexity, so a filthy building surrounded by cones
/// is the churn-times-complexity hotspot, falling out of two independent channels rather than being
/// computed as a third.
///
/// <b>Authorship is not monotonic</b>, and pretending otherwise would be dishonest. One author on a
/// file is a bus factor; a dozen is a file with no owner. Both ends are interesting and the middle
/// is fine, so the count gets a neutral, countable visual — bicycles at the frontage, "how many
/// people work here" — and the genuinely risky combination is raised as an incident instead.
/// </remarks>
internal static class History
{
    /// <summary>Commits this week for a building to count as under active work.</summary>
    const int ActiveThreshold = 1;

    /// <summary>
    /// Commits this week before sole authorship becomes a finding rather than a fact.
    /// </summary>
    /// <remarks>
    /// Deliberately a higher bar than the cones. A single commit by a file's only author is the most
    /// ordinary thing in a repository — sharing the cones' threshold of one raised dozens of
    /// incidents on a real solution, which is not a shortlist anybody would read. Three in a week is
    /// somebody actively working alone on something.
    /// </remarks>
    const int SoleOwnershipCommits = 3;
    /// <summary>Cones on one lot. Past a handful they stop being countable anyway.</summary>
    const int MaxCones = 8;
    const int MaxBicycles = 10;
    /// <summary>Gap between bikes on the rack. Wide enough that they read as separate.</summary>
    const float Spacing = 0.95f;
    /// <summary>How many sole-ownership cases earn a place on the tour. The rest are a number.</summary>
    const int MaxSoleOwnershipStops = 8;

    public sealed record Result(int Active, int Bicycles, int SoleOwnership);

    public static Result Apply(SceneGraph scene, CityModel model)
    {
        int active = 0, bicycles = 0;
        var owned = new List<(TypeNode Type, BuildingSite Site)>();

        // The real plot each building stands on. BuildingSite records only a centre and a footprint,
        // so without this every lot prop has to guess where the boundary is, and a guess is how
        // props end up in the road.
        var lots = scene.Lots.ToDictionary(l => l.Seed, l => l.Lot);

        foreach (var project in model.Projects)
        foreach (var type in project.Types)
        {
            if (!scene.Sites.TryGetValue(type.Id, out var site)) continue;

            // Only used to aim the tour camera; nothing is built at this height.
            float roofY = site.Side * 1.6f;

            if (type.Commits >= ActiveThreshold)
            {
                // A building whose lot was never recorded is one that overran its cell; leave its
                // ground alone rather than scattering props onto a boundary nobody knows.
                var lot = lots.TryGetValue(site.PickId, out var recorded)
                    ? recorded
                    : new Bounds2(site.Center.X - site.Side, site.Center.Z - site.Side,
                        site.Side * 2f, site.Side * 2f);

                AddCones(scene, type, site, lot);
                active++;

                scene.Interest.Add(new PointOfInterest
                {
                    Focus = site.Center with { Y = roofY * 0.6f },
                    Distance = MathF.Max(site.Side * 2f, 38f),
                    Headline = "WORK IN PROGRESS",
                    Detail = $"{type.Name} · {type.Commits} commit(s) in " +
                             $"{GitWindowDays} days · {type.Authors} author(s)",
                });
            }

            bicycles += AddBicycles(scene, scene.RoadNetwork, type, site);

            // The bus factor: the only person who has ever touched this file is still actively
            // changing it. A sole author on its own is unremarkable — half the types here have one,
            // because nobody has needed to touch them in years.
            if (type.Authors == 1 && type.Commits >= SoleOwnershipCommits)
                owned.Add((type, site));
        }

        // Incidents are rare on purpose; conditions read in aggregate. Thirty-odd tour stops would
        // nearly double the itinerary and drown out the fires and the crime scenes, so only the
        // busiest are flown to and the rest are reported as a number.
        foreach (var (type, site) in owned.OrderByDescending(o => o.Type.Commits)
                     .ThenBy(o => o.Type.Id, StringComparer.Ordinal)
                     .Take(MaxSoleOwnershipStops))
        {
            scene.Interest.Add(new PointOfInterest
            {
                Focus = site.Center with { Y = site.Side * 0.8f },
                Distance = MathF.Max(site.Side * 2f, 34f),
                Headline = "SOLE OWNERSHIP",
                Detail = $"{type.Name} · one author · {type.Commits} commit(s) in " +
                         $"{GitWindowDays} days",
            });
        }

        return new Result(active, bicycles, owned.Count);
    }

    /// <summary>Mirrors <c>GitHistory.WindowDays</c>; the layout does not reference the analyzer.</summary>
    const int GitWindowDays = 7;

    /// <summary>
    /// Traffic cones on the lot, one per commit this week.
    /// </summary>
    /// <remarks>
    /// Cones rather than a crane, because the two say different things and were competing. A crane
    /// is a site that has been standing for months; cones are today's disruption, put out and taken
    /// in again. Recent commits are the second of those. It also fixes the density problem — a
    /// crane is thirty metres tall and two hundred and eighty of them turned the skyline into a
    /// forest of scaffold, whereas cones read at street level and disappear from the air.
    /// </remarks>
    static void AddCones(SceneGraph scene, TypeNode type, BuildingSite site, Bounds2 lot)
    {
        int cones = Math.Clamp(type.Commits, 1, MaxCones);

        for (int i = 0; i < cones; i++)
        {
            var spot = CityLayout.ScatterOnLot(type.Id, lot, site.Side, i * 7 + 3,
                clearance: 0.3f);
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = spot,
                Size = new Vector3(0.52f, 0.78f, 0.52f),
                Color = new Vector4(0.95f, 0.36f, 0.06f, 1f),
                PickId = site.PickId,
                Detail = 1f,
                Flags = (uint)BoxFlags.Cone,
            });
        }
    }

    /// <summary>
    /// One bicycle per author, racked at the frontage.
    /// </summary>
    /// <remarks>
    /// The frontage is the last piece of a building not already carrying a meaning: all four facades
    /// have a tenant — posters, patched render, pipework, fire escape — and the roof carries the
    /// interface antennae. Bicycles are also the right weight for the signal. It is a small number
    /// worth counting up close, not something that should shout across the city.
    /// </remarks>
    static int AddBicycles(SceneGraph scene, RoadGraph graph, TypeNode type, BuildingSite site)
    {
        int bikes = Math.Clamp(type.Authors, 0, MaxBicycles);
        if (bikes == 0) return 0;

        var frame = new Vector4(0.22f, 0.24f, 0.30f, 1f);
        var wheel = new Vector4(0.10f, 0.10f, 0.11f, 1f);

        // Out front by the pavement, facing the nearest street — which is where a rack goes and
        // where anyone looking for one would look.
        //
        // They used to stand on a deterministic but arbitrary bearing, so a third of them ended up
        // round the back against a blank wall. Deterministic is not the same as sensible: the seed
        // made them stable between runs and did nothing at all to make them findable.
        var outward = new Vector3(0f, 0f, 1f);
        float standoff = site.Side * 0.5f + 1.8f;

        if (graph.TryNearestEdge(site.Center, site.Side + 60f, out int edge, out float along2))
        {
            var kerbside = graph.PointOn(edge, along2) - site.Center;
            if (kerbside.LengthSquared() > 1e-4f)
            {
                outward = Vector3.Normalize(kerbside with { Y = 0f });

                // Just inside the plot: past the building, short of the pavement, never in the road.
                //
                // The ceiling has to win over the floor. On a cramped lot there is no distance that
                // both clears the building and stays off the carriageway, and pushing out to a
                // minimum standoff regardless is how a rack of bicycles ends up parked in an alley.
                // Against the wall is merely odd; in the traffic is wrong.
                float toRoad = new Vector2(kerbside.X, kerbside.Z).Length();
                float pavement = graph.Edges[edge].Width * 0.5f + 2.2f;
                float furthest = toRoad - pavement;

                standoff = MathF.Max(0.4f,
                    MathF.Min(site.Side * 0.5f + 2.6f, furthest));
            }
        }

        var alongRack = new Vector3(-outward.Z, 0f, outward.X);
        var origin = site.Center + outward * standoff;

        // The rack itself, so a row of bicycles reads as a rack from across the street rather than
        // as scattered clutter you only notice standing on top of it.
        float run = MathF.Max(1.4f, bikes * Spacing);
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = origin with { Y = 0.5f },
            Size = new Vector3(0.12f + MathF.Abs(alongRack.X) * run, 0.1f,
                               0.12f + MathF.Abs(alongRack.Z) * run),
            Color = new Vector4(0.34f, 0.36f, 0.40f, 1f),
            PickId = site.PickId,
            Detail = 1f,
        });

        for (int i = 0; i < bikes; i++)
        {
            var at = origin + alongRack * ((i - (bikes - 1) * 0.5f) * Spacing);

            // Frame: a bar along the bike, standing about waist high.
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at with { Y = 0.52f },
                Size = new Vector3(0.10f + MathF.Abs(outward.X) * 1.25f, 0.5f,
                                   0.10f + MathF.Abs(outward.Z) * 1.25f),
                Color = frame,
                PickId = site.PickId,
                Detail = 1f,
            });
            // Handlebars, which is what actually makes the silhouette read as a bicycle.
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = (at + outward * 0.5f) with { Y = 1.02f },
                Size = new Vector3(0.08f + MathF.Abs(alongRack.X) * 0.55f, 0.08f,
                                   0.08f + MathF.Abs(alongRack.Z) * 0.55f),
                Color = frame,
                PickId = site.PickId,
                Detail = 1f,
            });
            // Wheels, genuinely round. A square wheel is the one part of a bicycle nobody will
            // forgive, and it was why these read as a rack of small crates rather than as bikes.
            // The thin axis is the axle, which the shader works out for itself.
            for (int end = -1; end <= 1; end += 2)
                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = at + outward * (0.55f * end),
                    Size = new Vector3(MathF.Abs(alongRack.X) * 0.08f + MathF.Abs(outward.X) * 0.74f,
                                       0.74f,
                                       MathF.Abs(alongRack.Z) * 0.08f + MathF.Abs(outward.Z) * 0.74f),
                    Color = wheel,
                    PickId = site.PickId,
                    Detail = 1f,
                    Flags = (uint)BoxFlags.Round,
                });
        }

        // A shelter once there are enough of them to need one.
        if (bikes >= 8)
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = origin with { Y = 2.1f },
                Size = new Vector3(0.4f + MathF.Abs(alongRack.X) * bikes * Spacing, 0.12f,
                                   0.4f + MathF.Abs(alongRack.Z) * bikes * Spacing),
                Color = new Vector4(0.30f, 0.33f, 0.36f, 0.75f),
                PickId = site.PickId,
                Detail = 1f,
            });

        return bikes;
    }
}
