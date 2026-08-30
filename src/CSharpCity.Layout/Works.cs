using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// What the team is doing to the city right now: building sites for open pull requests, and the
/// queue of people waiting on the issue backlog.
/// </summary>
/// <remarks>
/// <b>This is the overlay, and it obeys one rule above all: it never moves a building.</b> Every box
/// it adds is tagged <see cref="CityLayer.Works"/> or <see cref="CityLayer.Backlog"/> and stands
/// beside, around or above geometry that already exists. That is what makes the remote safe to
/// re-read at runtime — the city underneath is derived from source and does not care what anyone has
/// opened a pull request about — and it is why a pull request that deletes a class raises hoarding
/// around the building rather than removing it. The building is still there on the main branch. That
/// is the honest picture, and it neatly sidesteps the question of what to do with the hole.
///
/// <b>Two things deliberately not reused.</b> Cranes already mean outstanding TODOs, so a site being
/// actively worked gets floodlights and a site hut instead; a second meaning on the crane would make
/// the tallest, most visible object in the city ambiguous. And the road closure below is an
/// <em>incident</em>, in the sense the emergency services already use: rare, capped and specific. It
/// does not make traffic mean something in general, which the city goes out of its way to avoid.
/// </remarks>
internal static class Works
{
    /// <summary>Sites shown at once. Past this the city is scaffolding with a town somewhere inside.</summary>
    const int MaxSites = 300;

    /// <summary>
    /// Sites one pull request may raise.
    /// </summary>
    /// <remarks>
    /// A per-request cap as well as a total, because a total on its own is spent in whatever order
    /// the requests are enumerated. Measured on a real repository, twelve open pull requests touched
    /// 363 files between them: a single global cap was exhausted by the first few and every later
    /// request rendered nothing at all, which is much worse than showing less of each. Every open
    /// request now gets its share, and within a request the largest changes are the ones shown.
    /// </remarks>
    const int MaxSitesPerPull = 25;

    /// <summary>Road closures. An incident stops being an incident when there are twenty of them.</summary>
    const int MaxClosures = 6;

    /// <summary>People in one queue. Past this they stop being countable and are just a crowd.</summary>
    const int MaxQueue = 20;

    /// <summary>Days without a comment, a push or a review before a site reads as abandoned.</summary>
    const int StaleDays = 30;

    static readonly Vector4 Hoarding = new(0.30f, 0.42f, 0.55f, 1f);
    static readonly Vector4 WeatheredHoarding = new(0.33f, 0.34f, 0.31f, 1f);
    static readonly Vector4 Steel = new(0.62f, 0.63f, 0.66f, 1f);
    static readonly Vector4 Approved = new(0.20f, 0.78f, 0.35f, 1f);
    static readonly Vector4 Blocked = new(0.86f, 0.16f, 0.14f, 1f);
    static readonly Vector4 Inspection = new(0.95f, 0.66f, 0.10f, 1f);

    /// <param name="Dropped">
    /// Changed files that earned no site because a cap fell on them. Reported rather than swallowed:
    /// a city that quietly showed a third of the work would read as a repository with a third of the
    /// work going on in it.
    /// </param>
    public sealed record Result(int PullRequests, int Sites, int Ghosts, int Demolitions,
        int Closures, int Queueing, int Dropped);

    public static Result Apply(SceneGraph scene, CityModel model, GitHubSnapshot github)
    {
        int sites = 0, ghosts = 0, demolitions = 0, closures = 0, dropped = 0;

        // Newest first: on a busy repository the cap has to fall on the stalest work, not on
        // whatever happened to be enumerated last.
        var pulls = github.PullRequests
            .OrderBy(p => p.DaysSinceUpdate)
            .ThenBy(p => p.Number)
            .ToList();

        foreach (var pull in pulls)
        {
            // Biggest changes first, so a request that has to be trimmed keeps the work that
            // actually matters rather than whichever files happened to be listed first.
            var shown = pull.Files
                .OrderByDescending(f => f.Additions + f.Deletions)
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .Take(MaxSitesPerPull)
                .ToList();

            dropped += pull.Files.Count - shown.Count;

            foreach (var file in shown)
            {
                if (sites >= MaxSites) break;

                switch (file.Change)
                {
                    case FileChange.Added:
                        if (Propose(scene, model, pull, file)) { ghosts++; sites++; }
                        break;

                    case FileChange.Removed:
                        foreach (var site in SitesFor(scene, file))
                        {
                            Condemn(scene, pull, site);
                            demolitions++;
                            sites++;
                        }
                        break;

                    default:
                        foreach (var site in SitesFor(scene, file))
                        {
                            Renovate(scene, pull, file, site);
                            sites++;
                        }
                        break;
                }
            }

            // One closure per conflicted pull request, at the road outside the first building it
            // touches — not one per file, or a wide-reaching conflict would close half the city.
            if (pull.Conflicting && closures < MaxClosures)
            {
                var first = pull.Files.SelectMany(f => SitesFor(scene, f)).FirstOrDefault();
                if (first is not null && Close(scene, pull, first)) closures++;
            }

            if (pull.Conflicting || pull.Review == ReviewState.ChangesRequested)
                Flag(scene, pull);
        }

        int queueing = Queues.Build(scene, model, github);

        return new Result(pulls.Count, sites, ghosts, demolitions, closures, queueing, dropped);
    }

    static IEnumerable<BuildingSite> SitesFor(SceneGraph scene, ChangedFile file) =>
        file.TypeIds.Select(id => scene.Sites.TryGetValue(id, out var site) ? site : null)
            .Where(s => s is not null)!;

    // --- a building being changed ---

    /// <summary>
    /// Scaffolding and a hoarding around a building an open pull request modifies.
    /// </summary>
    /// <remarks>
    /// How far the scaffolding climbs is the size of the change, not the size of the building: a
    /// one-line fix to a tower gets a hoarding and a lift or two, a rewrite gets scaffolding to the
    /// roof. That keeps the massing meaning what it always means and puts the new information in the
    /// dressing, which is the rule the whole city is built on.
    /// </remarks>
    static void Renovate(SceneGraph scene, PullRequestInfo pull, ChangedFile file, BuildingSite site)
    {
        float reach = site.Side * 0.5f + 1.1f;
        Enclose(scene, site.Center, reach, pull);

        // A draft is a fenced site with nobody on it. Putting scaffolding up would claim work is
        // happening that its author has explicitly said isn't ready.
        if (pull.IsDraft) return;

        int touched = file.Additions + file.Deletions;
        float storeys = Math.Clamp(1f + touched / 45f, 1f, 9f);
        float height = MathF.Min(storeys * 3.4f, site.Side * 2.6f);

        Scaffold(scene, site.Center, reach, height, pull);

        if (pull.Review == ReviewState.Approved) Ribbon(scene, site.Center, reach, height);
        if (pull.ChecksFailing) Placard(scene, site.Center, reach, Inspection);
        if (pull.Review == ReviewState.ChangesRequested) Placard(scene, site.Center, reach, Blocked);
    }

    /// <summary>Hoarding around a building whose file this pull request deletes.</summary>
    static void Condemn(SceneGraph scene, PullRequestInfo pull, BuildingSite site)
    {
        float reach = site.Side * 0.5f + 1.1f;
        Enclose(scene, site.Center, reach, pull);
        Placard(scene, site.Center, reach, Blocked);

        // Hazard tape at head height all the way round: the one dressing that reads as "this is
        // coming down" rather than "this is being worked on".
        for (int i = 0; i < 4; i++)
        {
            bool alongX = i % 2 == 0;
            float offset = i < 2 ? -reach : reach;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = site.Center + new Vector3(alongX ? 0f : offset, 2.2f,
                    alongX ? offset : 0f),
                Size = new Vector3(alongX ? reach * 2f : 0.1f, 0.16f, alongX ? 0.1f : reach * 2f),
                Color = Inspection,
                PickId = site.PickId,
                Detail = 1f,
                Layer = CityLayer.Works,
            });
        }
    }

    /// <summary>
    /// A building that does not exist yet, drawn where it would stand.
    /// </summary>
    /// <remarks>
    /// A new file has no type in the model and therefore no lot, so this is genuinely a guess at a
    /// position — beside the work already going on in the same project. It is drawn as a survey
    /// drawing rather than a solid precisely so it cannot be mistaken for a building that exists,
    /// and its size is scaled from the diff, which is the only thing known about it. Nobody can say
    /// how big a class will be before it is written.
    /// </remarks>
    static bool Propose(SceneGraph scene, CityModel model, PullRequestInfo pull, ChangedFile file)
    {
        if (file.Project is null) return false;
        if (!scene.Districts.TryGetValue(file.Project, out var district)) return false;

        // Somewhere in the right district, deterministic per file, kept off the edges where the
        // boulevards run.
        float x = district.X + district.Width *
            (0.2f + CityLayout.StableRandom(file.Path, 11) * 0.6f);
        float z = district.Z + district.Depth *
            (0.2f + CityLayout.StableRandom(file.Path, 23) * 0.6f);

        float side = Math.Clamp(3f + file.Additions / 55f, 3f, 11f);
        float height = Math.Clamp(4f + file.Additions / 22f, 4f, 26f);
        var footing = new Vector3(x, 0f, z);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = footing,
            Size = new Vector3(side, height, side),
            Color = new Vector4(0.36f, 0.72f, 0.92f, 0.26f),
            PickId = -1,
            Detail = 1f,
            Flags = (uint)BoxFlags.Ghost,
            Layer = CityLayer.Works,
        });

        // Setting-out pegs, so the footprint reads from above even when the drawing above it is
        // edge-on and nearly invisible.
        for (int corner = 0; corner < 4; corner++)
        {
            float dx = (corner is 0 or 3 ? -1f : 1f) * side * 0.5f;
            float dz = (corner < 2 ? -1f : 1f) * side * 0.5f;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = footing + new Vector3(dx, 0f, dz),
                Size = new Vector3(0.2f, 1.1f, 0.2f),
                Color = new Vector4(0.42f, 0.80f, 0.96f, 1f),
                PickId = -1,
                Detail = 1f,
                Flags = (uint)BoxFlags.Emissive,
                Layer = CityLayer.Works,
            });
        }

        scene.Labels.Add(new WorldLabel
        {
            Position = footing with { Y = height + 2.4f },
            Text = System.IO.Path.GetFileNameWithoutExtension(file.Path),
            Subtitle = $"proposed · PR #{pull.Number}",
            Size = 0.9f,
            Color = new Vector4(0.62f, 0.88f, 1f, 1f),
            FadeDistance = 150f,
            Layer = CityLayer.Works,
        });

        return true;
    }

    // --- the pieces every site is made of ---

    /// <summary>Site hoarding: a continuous boarded fence at the lot boundary.</summary>
    static void Enclose(SceneGraph scene, Vector3 centre, float reach, PullRequestInfo pull)
    {
        var colour = pull.DaysSinceUpdate >= StaleDays ? WeatheredHoarding : Hoarding;

        for (int i = 0; i < 4; i++)
        {
            bool alongX = i % 2 == 0;
            float offset = i < 2 ? -reach : reach;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = centre + new Vector3(alongX ? 0f : offset, 0f, alongX ? offset : 0f),
                Size = new Vector3(alongX ? reach * 2f : 0.14f, 2.0f, alongX ? 0.14f : reach * 2f),
                Color = colour,
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Works,
            });
        }
    }

    /// <summary>Tube-and-board scaffolding: uprights at the corners, lifts every 3.4 m.</summary>
    static void Scaffold(SceneGraph scene, Vector3 centre, float reach, float height,
        PullRequestInfo pull)
    {
        for (int corner = 0; corner < 4; corner++)
        {
            float dx = (corner is 0 or 3 ? -1f : 1f) * reach;
            float dz = (corner < 2 ? -1f : 1f) * reach;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = centre + new Vector3(dx, 0f, dz),
                Size = new Vector3(0.18f, height, 0.18f),
                Color = Steel,
                PickId = -1,
                Detail = 1f,
                Layer = CityLayer.Works,
            });
        }

        for (float y = 3.4f; y <= height; y += 3.4f)
        {
            for (int i = 0; i < 4; i++)
            {
                bool alongX = i % 2 == 0;
                float offset = i < 2 ? -reach : reach;

                scene.Boxes.Add(new BoxInstance
                {
                    BasePosition = centre + new Vector3(alongX ? 0f : offset, y,
                        alongX ? offset : 0f),
                    Size = new Vector3(alongX ? reach * 2f : 0.5f, 0.12f,
                        alongX ? 0.5f : reach * 2f),
                    Color = Steel,
                    PickId = -1,
                    Detail = 1f,
                    Layer = CityLayer.Works,
                });
            }
        }

        // Floodlights only where work is actually happening. A stale site keeps its scaffolding and
        // loses its lighting, which is what an abandoned site looks like.
        if (pull.DaysSinceUpdate < StaleDays)
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = centre + new Vector3(reach, height, reach),
                Size = new Vector3(0.5f, 0.4f, 0.5f),
                Color = new Vector4(1f, 0.94f, 0.72f, 1f),
                PickId = -1,
                Detail = 1f,
                Flags = (uint)BoxFlags.Emissive,
                Layer = CityLayer.Works,
            });
    }

    /// <summary>A ribbon across the frontage: reviewed, approved, waiting to open.</summary>
    static void Ribbon(SceneGraph scene, Vector3 centre, float reach, float height)
    {
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = centre + new Vector3(0f, MathF.Min(height * 0.5f, 6f), -reach - 0.2f),
            Size = new Vector3(reach * 2f, 0.55f, 0.12f),
            Color = Approved,
            PickId = -1,
            Detail = 1f,
            Flags = (uint)BoxFlags.Emissive,
            Layer = CityLayer.Works,
        });
    }

    /// <summary>A notice board on the hoarding. Colour is the notice.</summary>
    static void Placard(SceneGraph scene, Vector3 centre, float reach, Vector4 colour)
    {
        var post = centre + new Vector3(reach * 0.55f, 0f, -reach - 0.5f);

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = post,
            Size = new Vector3(0.12f, 2.6f, 0.12f),
            Color = Steel,
            PickId = -1,
            Detail = 1f,
            Layer = CityLayer.Works,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = post with { Y = 2.0f },
            Size = new Vector3(1.5f, 1.0f, 0.1f),
            Color = colour,
            PickId = -1,
            Detail = 1f,
            Flags = (uint)BoxFlags.Emissive,
            Layer = CityLayer.Works,
        });
    }

    /// <summary>
    /// A closed road outside a building whose pull request cannot merge.
    /// </summary>
    /// <remarks>
    /// A conflict is the one pull-request state that is nobody's fault and everybody's problem, and
    /// it is invisible from a building — you cannot see a merge conflict by looking at a class. A
    /// blocked road is the one piece of city vocabulary that reads instantly at a distance and says
    /// "you cannot get through here". Capped hard, for the same reason the fires are.
    /// </remarks>
    static bool Close(SceneGraph scene, PullRequestInfo pull, BuildingSite site)
    {
        var graph = scene.RoadNetwork;
        if (graph.IsEmpty) return false;
        if (!graph.TryNearestEdge(site.Center, site.Side + 40f, out int edge, out float along))
            return false;

        var centre = graph.PointOn(edge, along);
        float width = graph.Edges[edge].Width;

        // Across the carriageway, not along it: the barrier has to read as a wall from a car's
        // point of view or it is just another kerb.
        var ahead = graph.PointOn(edge, along + 1f) - centre;
        if (ahead.LengthSquared() < 1e-4f) return false;
        var direction = Vector3.Normalize(ahead with { Y = 0f });
        var across = new Vector3(-direction.Z, 0f, direction.X);

        for (int i = -1; i <= 1; i++)
        {
            var at = centre + across * (i * width * 0.3f);

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at with { Y = CityLayout.StreetSurfaceY },
                Size = new Vector3(
                    MathF.Abs(across.X) * width * 0.32f + MathF.Abs(direction.X) * 0.22f + 0.22f,
                    1.1f,
                    MathF.Abs(across.Z) * width * 0.32f + MathF.Abs(direction.Z) * 0.22f + 0.22f),
                Color = Inspection,
                PickId = -1,
                Detail = 1f,
                Flags = (uint)BoxFlags.Emissive,
                Layer = CityLayer.Works,
            });
        }

        scene.Interest.Add(new PointOfInterest
        {
            Focus = centre with { Y = 6f },
            Distance = 42f,
            Headline = "ROAD CLOSED",
            Detail = $"PR #{pull.Number} conflicts with the base branch · {pull.Title}",
            Layer = CityLayer.Works,
        });

        return true;
    }

    /// <summary>A pull request worth flying to: it is stuck and somebody has to do something.</summary>
    static void Flag(SceneGraph scene, PullRequestInfo pull)
    {
        var first = pull.Files.SelectMany(f =>
            f.TypeIds.Where(scene.Sites.ContainsKey).Select(id => scene.Sites[id])).FirstOrDefault();
        if (first is null) return;

        scene.Interest.Add(new PointOfInterest
        {
            Focus = first.Center with { Y = first.Side },
            Distance = MathF.Max(first.Side * 2f, 34f),
            Headline = pull.Conflicting ? "BLOCKED WORKS" : "CHANGES REQUESTED",
            Detail = $"PR #{pull.Number} · {pull.Title} · {pull.Author}",
            Layer = CityLayer.Works,
        });
    }
}
