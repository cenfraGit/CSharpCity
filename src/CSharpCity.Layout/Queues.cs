using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// The issue backlog, as the queue outside the civic buildings.
/// </summary>
/// <remarks>
/// <b>Issues cannot be buildings, and pretending otherwise would be a lie.</b> A pull request carries
/// the list of files it touches, so it can be shown on the exact buildings it changes. An issue
/// carries nothing of the sort — no file, no type, no location — and the only honest options are to
/// invent a position or to put it somewhere that is openly an aggregate. Inventing one would be the
/// worst kind of wrong: precise, confident and meaningless.
///
/// So the backlog becomes what a backlog actually is — people waiting on the council. Defects queue
/// at the hospital, requests at the town hall, and the length of the queue is the size of the
/// backlog. Both landmarks already exist and keep their own meanings, which are architectural: the
/// hospital is still the type with the most <c>catch</c> blocks. A queue outside it is a separate
/// channel that happens to share a doorstep.
///
/// The second axis is age, and it is the one that matters. A hundred issues opened this week and a
/// hundred left from two years ago are the same number and completely different situations, so a
/// recent issue stands, an old one sits down, and one nobody has touched in a year pitches a tent.
/// A row of tents outside the town hall is a backlog nobody is ever going to clear.
/// </remarks>
internal static class Queues
{
    const int MaxQueue = 20;
    const float Spacing = 1.5f;

    /// <summary>Sitting down. Long enough that nobody expects to be seen today.</summary>
    const int SeatedDays = 30;

    /// <summary>Camping. Long enough that "open" has stopped meaning anything.</summary>
    const int CampingDays = 365;

    static readonly Vector4 Standing = new(0.82f, 0.78f, 0.70f, 1f);
    static readonly Vector4 Seated = new(0.66f, 0.62f, 0.58f, 1f);
    static readonly Vector4 Canvas = new(0.42f, 0.50f, 0.44f, 1f);

    public static int Build(SceneGraph scene, CityModel model, GitHubSnapshot github)
    {
        if (github.Issues.Count == 0) return 0;

        // Where the civic buildings ended up. Roles are decided per project and at most one of each
        // is ever awarded, so this finds the landmark of the largest project that has one.
        var landmarks = FindLandmarks(scene, model);

        int queueing = 0;
        queueing += Queue(scene, Anchor(scene, model, landmarks, CivicRole.Hospital, -1f),
            github.Issues.Where(i => i.Category == IssueCategory.Bug).ToList(), "REPORTED DEFECTS");

        // Requests and everything uncategorised go to the town hall together. Splitting "other" into
        // a third queue outside a third building would be inventing a distinction the labels do not
        // reliably make.
        queueing += Queue(scene, Anchor(scene, model, landmarks, CivicRole.TownHall, 1f),
            github.Issues.Where(i => i.Category != IssueCategory.Bug).ToList(), "OPEN REQUESTS");

        return queueing;
    }

    /// <summary>Where a queue forms, and how wide it may spread.</summary>
    readonly record struct QueuePoint(Vector3 Head, float Spread);

    /// <summary>
    /// The doorstep a queue forms on, falling back to the city centre when there is no landmark.
    /// </summary>
    /// <remarks>
    /// The fallback is the important half. Civic roles carry minimum bars — a project with one
    /// interface nobody implements gets no cathedral, and a small tidy solution may well have
    /// neither a hospital nor a town hall. Returning nothing in that case made the entire backlog
    /// invisible, so a repository with two hundred open issues rendered exactly like one with none.
    /// That is the silent absence this codebase refuses everywhere else: a queue standing in the
    /// main square is imprecise, and imprecise beats absent every time.
    ///
    /// The two queues are pushed to opposite sides of the square so they stay countable separately
    /// even when they have both fallen back to the same place.
    /// </remarks>
    static QueuePoint? Anchor(SceneGraph scene, CityModel model,
        Dictionary<CivicRole, BuildingSite> landmarks, CivicRole role, float side)
    {
        if (landmarks.TryGetValue(role, out var landmark))
            return new QueuePoint(
                landmark.Center with { Z = landmark.Center.Z - (landmark.Side * 0.5f + 2.4f) },
                landmark.Side);

        var biggest = model.Projects
            .Where(p => p.Types.Count > 0 && !p.IsTestProject)
            .OrderByDescending(p => p.Types.Count)
            .FirstOrDefault();

        if (biggest is null || !scene.Districts.TryGetValue(biggest.Name, out var district))
            return null;

        float spread = MathF.Min(district.Width, district.Depth) * 0.12f;
        return new QueuePoint(
            new Vector3(district.CenterX + side * spread * 1.4f, 0f, district.CenterZ), spread);
    }

    /// <summary>
    /// The best site for each civic role across the whole solution.
    /// </summary>
    /// <remarks>
    /// Issues belong to the repository, not to a project, so their queue has to stand in one place.
    /// The largest project's landmark is the closest thing the city has to a capital, and projects
    /// arrive here already sorted biggest-first.
    /// </remarks>
    static Dictionary<CivicRole, BuildingSite> FindLandmarks(SceneGraph scene, CityModel model)
    {
        var found = new Dictionary<CivicRole, BuildingSite>();

        foreach (var project in model.Projects
                     .Where(p => p.Types.Count > 0 && !p.IsTestProject)
                     .OrderByDescending(p => p.Types.Count))
        foreach (var (id, role) in CivicRoles.Assign(project))
        {
            if (found.ContainsKey(role)) continue;
            if (scene.Sites.TryGetValue(id, out var site)) found[role] = site;
        }

        return found;
    }

    /// <summary>A line of people out from the doorstep, newest at the front.</summary>
    static int Queue(SceneGraph scene, QueuePoint? anchor, List<IssueInfo> issues, string headline)
    {
        if (issues.Count == 0 || anchor is not { } point) return 0;

        var waiting = issues
            .OrderBy(i => i.DaysOpen)
            .ThenBy(i => i.Number)
            .Take(MaxQueue)
            .ToList();

        for (int i = 0; i < waiting.Count; i++)
        {
            var at = point.Head + new Vector3((i - (waiting.Count - 1) * 0.5f) * Spacing, 0f,
                -CityLayout.StableRandom(waiting[i].Number.ToString(), 7) * 0.7f);

            if (waiting[i].DaysOpen >= CampingDays) Camp(scene, at);
            else if (waiting[i].DaysOpen >= SeatedDays) Sit(scene, at);
            else Stand(scene, at, waiting[i].Number);
        }

        scene.Interest.Add(new PointOfInterest
        {
            Focus = point.Head with { Y = 3f },
            Distance = MathF.Max(point.Spread * 2f, 36f),
            Headline = headline,
            Detail = $"{issues.Count} open · oldest {issues.Max(i => i.DaysOpen)} days",
            Layer = CityLayer.Backlog,
        });

        return waiting.Count;
    }

    static void Stand(SceneGraph scene, Vector3 at, int seed)
    {
        float lean = CityLayout.StableRandom(seed.ToString(), 3) * 0.12f;

        Add(scene, at, new Vector3(0.42f, 1.15f, 0.3f), Standing);
        Add(scene, at with { Y = 1.15f + lean }, new Vector3(0.3f, 0.3f, 0.3f), Standing);
    }

    static void Sit(SceneGraph scene, Vector3 at)
    {
        Add(scene, at, new Vector3(0.44f, 0.62f, 0.42f), Seated);
        Add(scene, at with { Y = 0.62f }, new Vector3(0.3f, 0.28f, 0.3f), Seated);
    }

    /// <summary>A tent. Whatever this issue was, nobody is being seen about it.</summary>
    static void Camp(SceneGraph scene, Vector3 at)
    {
        Add(scene, at, new Vector3(1.5f, 0.62f, 1.15f), Canvas);
        Add(scene, at with { Y = 0.62f }, new Vector3(1.1f, 0.34f, 0.8f), Canvas);
        Add(scene, at with { Y = 0.96f }, new Vector3(0.7f, 0.22f, 0.45f), Canvas);
    }

    static void Add(SceneGraph scene, Vector3 at, Vector3 size, Vector4 colour) =>
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = at,
            Size = size,
            Color = colour,
            PickId = -1,
            Detail = 1f,
            Layer = CityLayer.Backlog,
        });
}
