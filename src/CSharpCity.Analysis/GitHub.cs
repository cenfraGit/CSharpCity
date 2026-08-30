using System.Diagnostics;
using System.Text.Json;
using CSharpCity.Model;

namespace CSharpCity.Analysis;

/// <summary>
/// Asks the remote what the team is doing: open pull requests and the issue backlog.
/// </summary>
/// <remarks>
/// The same bargain <see cref="GitHistory"/> makes, for the same reasons. One CLI already installed
/// on most machines that work with GitHub answers all of it; the alternative is an HTTP client, a
/// token to store, and an OAuth flow to get the token, which is a great deal of ceremony for data
/// <c>gh</c> hands over in a second.
///
/// <b>Why the file list matters.</b> <c>gh</c> reports each pull request's touched files as
/// repository-relative paths with forward slashes — byte-identical to what
/// <see cref="GitHistory.Relativise"/> already produces from Roslyn's rooted Windows paths. That one
/// coincidence is the whole reason this is worth building: a pull request can be shown as
/// scaffolding on the actual buildings it changes, rather than as a number floating over the city.
/// Issues have no such thing, which is why they are never tied to a building.
///
/// <b>Unlike git history, this is never merged into the model.</b> See <see cref="GitHubSnapshot"/>.
/// </remarks>
public static class GitHub
{
    /// <summary>
    /// How many open pull requests to ask for.
    /// </summary>
    /// <remarks>
    /// A cap on the query, not just on the display. Asking for the file list of every open pull
    /// request on a repository with hundreds is both slow and pointless — the city cannot show two
    /// hundred simultaneous construction sites legibly, and the ones worth seeing are the recent
    /// ones this returns first.
    /// </remarks>
    public const int MaxPullRequests = 40;

    /// <summary>Issues are cheap to fetch and become a queue, which the layout caps separately.</summary>
    public const int MaxIssues = 200;

    /// <summary>
    /// Fields worth the round trip. <c>statusCheckRollup</c> is by far the most expensive of these —
    /// measured at roughly triple the query time on a busy repository — but a red build is exactly
    /// the sort of thing somebody walking the city wants to spot without being told.
    /// </summary>
    const string PullRequestFields =
        "number,title,author,isDraft,reviewDecision,mergeable,additions,deletions,updatedAt," +
        "files,statusCheckRollup";

    const string IssueFields = "number,title,labels,createdAt";

    /// <summary>
    /// Asks the remote. Never throws: everything that can go wrong here is somebody else's
    /// environment, and none of it is a reason to fail a run that was going to succeed.
    /// </summary>
    public static async Task<GitHubSnapshot> FetchAsync(string? solutionPath, CancellationToken ct = default)
    {
        string? root = GitHistory.FindRepositoryRoot(solutionPath);
        if (root is null) return Unavailable("no .git directory above the solution");

        // Cheapest possible gate, and it rules out four separate failures at once: gh missing from
        // PATH, gh not authenticated, no remote at all, and a remote that isn't GitHub. Doing this
        // first means the expensive queries only ever run when they can succeed.
        var identity = await RunAsync(root, ct, "repo", "view", "--json", "nameWithOwner");
        if (!identity.Ok) return Unavailable(identity.Failure ?? "gh could not read the repository");

        string repository = ParseRepositoryName(identity.Output);
        if (repository.Length == 0) return Unavailable("gh returned no repository name");

        var snapshot = new GitHubSnapshot { Available = true, Repository = repository };

        var pulls = await RunAsync(root, ct, "pr", "list", "--state", "open",
            "--limit", MaxPullRequests.ToString(), "--json", PullRequestFields);
        if (pulls.Ok) snapshot.PullRequests.AddRange(ParsePullRequests(pulls.Output, DateTimeOffset.UtcNow));

        var issues = await RunAsync(root, ct, "issue", "list", "--state", "open",
            "--limit", MaxIssues.ToString(), "--json", IssueFields);
        if (issues.Ok) snapshot.Issues.AddRange(ParseIssues(issues.Output, DateTimeOffset.UtcNow));

        // Both queries failing is a real failure; one failing is a partial answer worth keeping.
        if (!pulls.Ok && !issues.Ok)
            return Unavailable(pulls.Failure ?? issues.Failure ?? "gh returned nothing");

        return snapshot;
    }

    static GitHubSnapshot Unavailable(string reason) =>
        new() { Available = false, Reason = reason };

    /// <summary>
    /// Joins each changed file to the buildings it declares, in place.
    /// </summary>
    /// <remarks>
    /// Separate from the fetch because the two halves arrive independently: the snapshot can be
    /// re-read at runtime against a model that was loaded once, and a cached model can be rendered
    /// against a snapshot fetched minutes later. This is also the only place that knows how to turn
    /// one path form into the other, which is why it lives here rather than in the layout.
    /// </remarks>
    public static void Resolve(CityModel model, GitHubSnapshot snapshot)
    {
        string? root = GitHistory.FindRepositoryRoot(model.SolutionPath);
        if (root is null) return;

        // Every analysed file, keyed the way gh reports paths.
        var typesByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var projectByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in model.Projects)
        foreach (var type in project.Types)
        {
            string? key = GitHistory.Relativise(root, type.FilePath);
            if (key is null) continue;

            if (!typesByPath.TryGetValue(key, out var ids))
                typesByPath[key] = ids = new List<string>();
            ids.Add(type.Id);
            projectByPath[key] = project.Name;
        }

        foreach (var file in snapshot.PullRequests.SelectMany(p => p.Files))
        {
            if (typesByPath.TryGetValue(file.Path, out var ids))
            {
                file.TypeIds.AddRange(ids);
                file.Project = projectByPath[file.Path];
                continue;
            }

            // No building of its own — a new file, a deleted one, or something outside the
            // solution. Which project's ground it would stand on is still worth knowing.
            file.Project = ProjectOfFolder(file.Path, projectByPath);
        }
    }

    /// <summary>
    /// Which project a path belongs to, by the company its folder keeps.
    /// </summary>
    /// <remarks>
    /// A file that does not exist yet cannot be looked up, so it is placed by its neighbours: the
    /// deepest folder that already contains analysed code decides. Walking up rather than matching
    /// prefixes handles the ordinary case (a new class beside its siblings) exactly, and gives up
    /// cleanly on a file that belongs to no project at all.
    /// </remarks>
    static string? ProjectOfFolder(string path, Dictionary<string, string> projectByPath)
    {
        for (int cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
        {
            string folder = path[..(cut + 1)];
            foreach (var (known, project) in projectByPath)
                if (known.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return project;
        }

        return null;
    }

    // --- parsing: pure, and separate from the process call so it can be tested without a network ---

    internal static string ParseRepositoryName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("nameWithOwner", out var name)
                ? name.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    internal static List<PullRequestInfo> ParsePullRequests(string json, DateTimeOffset now)
    {
        var results = new List<PullRequestInfo>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var pull = new PullRequestInfo
                {
                    Number = Int(element, "number"),
                    Title = String(element, "title"),
                    Author = ParseAuthor(element),
                    IsDraft = Bool(element, "isDraft"),
                    Review = ParseReview(String(element, "reviewDecision")),
                    // MERGEABLE / CONFLICTING / UNKNOWN. Only an explicit conflict counts: UNKNOWN
                    // means GitHub hasn't finished computing it, which is not the same as a clash.
                    Conflicting = String(element, "mergeable") == "CONFLICTING",
                    ChecksFailing = ParseChecksFailing(element),
                    Additions = Int(element, "additions"),
                    Deletions = Int(element, "deletions"),
                    DaysSinceUpdate = DaysSince(element, "updatedAt", now),
                };

                if (element.TryGetProperty("files", out var files)
                    && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                        pull.Files.Add(new ChangedFile
                        {
                            Path = String(file, "path"),
                            Change = ParseChangeType(String(file, "changeType")),
                            Additions = Int(file, "additions"),
                            Deletions = Int(file, "deletions"),
                        });
                }

                results.Add(pull);
            }
        }
        catch (JsonException)
        {
            // A malformed payload costs the overlay, not the run.
            return results;
        }

        return results;
    }

    internal static List<IssueInfo> ParseIssues(string json, DateTimeOffset now)
    {
        var results = new List<IssueInfo>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var element in document.RootElement.EnumerateArray())
                results.Add(new IssueInfo
                {
                    Number = Int(element, "number"),
                    Title = String(element, "title"),
                    Category = ParseCategory(element),
                    DaysOpen = DaysSince(element, "createdAt", now),
                });
        }
        catch (JsonException)
        {
            return results;
        }

        return results;
    }

    /// <summary>
    /// Sorts an issue into defect, request, or neither, from its labels.
    /// </summary>
    /// <remarks>
    /// Substring matching on a small vocabulary, because label names are house style and no two
    /// repositories agree: "bug", "type: bug", "kind/bug" and "defect" all mean the same thing.
    /// Anything unrecognised stays <see cref="IssueCategory.Other"/> — guessing would put issues in
    /// the wrong queue, and a wrong answer here is worse than an unspecific one.
    /// </remarks>
    static IssueCategory ParseCategory(JsonElement element)
    {
        if (!element.TryGetProperty("labels", out var labels)
            || labels.ValueKind != JsonValueKind.Array) return IssueCategory.Other;

        foreach (var label in labels.EnumerateArray())
        {
            string name = String(label, "name").ToLowerInvariant();

            if (name.Contains("bug") || name.Contains("defect") || name.Contains("regression"))
                return IssueCategory.Bug;

            if (name.Contains("enhancement") || name.Contains("feature")
                || name.Contains("proposal") || name.Contains("suggestion"))
                return IssueCategory.Feature;
        }

        return IssueCategory.Other;
    }

    static string ParseAuthor(JsonElement element) =>
        element.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
            ? String(author, "login")
            : "";

    static ReviewState ParseReview(string decision) => decision switch
    {
        "APPROVED" => ReviewState.Approved,
        "CHANGES_REQUESTED" => ReviewState.ChangesRequested,
        _ => ReviewState.Pending,
    };

    static FileChange ParseChangeType(string changeType) => changeType switch
    {
        "ADDED" => FileChange.Added,
        "REMOVED" => FileChange.Removed,
        _ => FileChange.Modified,
    };

    /// <summary>
    /// True when any check has actually failed.
    /// </summary>
    /// <remarks>
    /// The rollup mixes two shapes: check runs report a <c>conclusion</c>, older status contexts a
    /// <c>state</c>. Both are read. A cancelled or skipped check is deliberately not a failure —
    /// most repositories skip a great many checks on any given pull request, and treating those as
    /// red would put a failed-inspection notice on nearly every site in the city.
    /// </remarks>
    static bool ParseChecksFailing(JsonElement element)
    {
        if (!element.TryGetProperty("statusCheckRollup", out var rollup)
            || rollup.ValueKind != JsonValueKind.Array) return false;

        foreach (var check in rollup.EnumerateArray())
        {
            string conclusion = String(check, "conclusion");
            if (conclusion is "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE") return true;

            string state = String(check, "state");
            if (state is "FAILURE" or "ERROR") return true;
        }

        return false;
    }

    static int DaysSince(JsonElement element, string property, DateTimeOffset now) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var stamp)
            ? Math.Max(0, (int)(now - stamp).TotalDays)
            : 0;

    static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static int Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Runs one <c>gh</c> command.
    /// </summary>
    /// <remarks>
    /// Deliberately async, which is the one place this departs from <see cref="GitHistory"/>. That
    /// reads stdout to the end before waiting for exit, which is safe only because <c>git log</c>
    /// writes almost nothing to stderr. <c>gh</c> writes a good deal more — authentication notices,
    /// rate-limit warnings, upgrade nags — and if it fills the stderr pipe while we are still
    /// blocked on stdout, both sides wait for each other forever. Reading both concurrently costs
    /// nothing and removes the deadlock.
    /// </remarks>
    static async Task<(bool Ok, string Output, string? Failure)> RunAsync(
        string root, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return (false, "", "gh could not be started");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0) return (true, stdout.Result, null);

            string error = stderr.Result.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return (false, "", error.Length > 0 ? error : $"gh exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            // Most often: gh isn't installed. That is a fact about the machine, not an error here.
            return (false, "", ex.Message);
        }
    }
}
