namespace CSharpCity.Model;

/// <summary>
/// What the remote knows right now: open pull requests and the issue backlog.
/// </summary>
/// <remarks>
/// Deliberately <em>not</em> part of <see cref="CityModel"/>, and deliberately not fanned onto
/// <see cref="TypeNode"/> the way git history is.
///
/// Everything in <see cref="CityModel"/> is derived from source and is expected to be identical
/// given the same source — that is what makes the city deterministic and what makes a dumped model
/// worth caching. This is the opposite kind of fact: it changes when somebody opens a pull request,
/// with no source change at all. Keeping the two apart means the city's geometry never moves because
/// of remote state, a cached model stays valid however busy the repository gets, and this can be
/// re-fetched at runtime without re-analysing anything.
///
/// The city calls the visuals built from this the <em>overlay</em>, and it is the only part of the
/// scene that may be rebuilt after startup.
/// </remarks>
public sealed class GitHubSnapshot
{
    /// <summary>False when there was nothing to ask, or nobody to ask. <see cref="Reason"/> says why.</summary>
    public bool Available { get; set; }

    /// <summary>Non-null exactly when <see cref="Available"/> is false.</summary>
    public string? Reason { get; set; }

    /// <summary>"owner/name", for the console note and the browser's title.</summary>
    public string Repository { get; set; } = "";

    public List<PullRequestInfo> PullRequests { get; set; } = new();

    /// <summary>
    /// Open issues, one entry each.
    /// </summary>
    /// <remarks>
    /// Kept as individual tickets rather than a count because age is the interesting axis: a hundred
    /// issues opened this week and a hundred left over from two years ago are the same number and
    /// completely different situations.
    /// </remarks>
    public List<IssueInfo> Issues { get; set; } = new();

    /// <summary>Whether the project-board query was permitted. Needs an OAuth scope most tokens lack.</summary>
    public bool BoardsAvailable { get; set; }
}

/// <summary>One open pull request, and the files it touches.</summary>
public sealed class PullRequestInfo
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>Nobody is asking for this to be merged yet.</summary>
    public bool IsDraft { get; set; }

    public ReviewState Review { get; set; }

    /// <summary>Cannot merge: it collides with the base branch.</summary>
    public bool Conflicting { get; set; }

    /// <summary>At least one required check has failed.</summary>
    public bool ChecksFailing { get; set; }

    public int Additions { get; set; }
    public int Deletions { get; set; }

    /// <summary>Days since anything happened on it. High means abandoned in place.</summary>
    public int DaysSinceUpdate { get; set; }

    public List<ChangedFile> Files { get; set; } = new();

    /// <summary>Total churn, which is what the size of the works is scaled from.</summary>
    public int Churn => Additions + Deletions;
}

/// <summary>Where a pull request has got to with its reviewers.</summary>
public enum ReviewState
{
    /// <summary>Nobody has reviewed it yet, or review isn't required.</summary>
    Pending,
    Approved,
    ChangesRequested,
}

/// <summary>What a pull request does to one file.</summary>
public enum FileChange
{
    Modified,
    Added,
    Removed,
}

/// <summary>
/// One file in a pull request. <see cref="Path"/> is repository-relative with forward slashes —
/// the same form <c>GitHistory.Relativise</c> produces, which is what lets a pull request be matched
/// to the buildings it actually touches instead of being shown as a number somewhere.
/// </summary>
public sealed class ChangedFile
{
    public string Path { get; set; } = "";
    public FileChange Change { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }

    /// <summary>
    /// Ids of the types declared in this file, joined to the model after both are loaded.
    /// </summary>
    /// <remarks>
    /// Empty for three quite different reasons, and the layout treats them differently: the file is
    /// outside the analysed solution (a build script, a README), the file is new and so has no
    /// building yet, or the file was deleted. Only the middle case earns a ghost.
    /// </remarks>
    public List<string> TypeIds { get; set; } = new();

    /// <summary>
    /// The project this file belongs to, when it could be worked out from its folder.
    /// </summary>
    /// <remarks>
    /// Set for files with no types of their own — which is the only way a proposed new building can
    /// be put anywhere sensible, since a type that doesn't exist yet has no lot.
    /// </remarks>
    public string? Project { get; set; }
}

/// <summary>One open issue. No file references exist on issues, so this is never tied to a building.</summary>
public sealed class IssueInfo
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public IssueCategory Category { get; set; }

    /// <summary>Days since it was opened.</summary>
    public int DaysOpen { get; set; }
}

/// <summary>
/// Coarse issue kind, from labels.
/// </summary>
/// <remarks>
/// Three buckets rather than the repository's real label set, because labels are per-project vocabulary
/// and the city has to read the same way everywhere. Anything that isn't recognisably a defect or a
/// request is <see cref="Other"/> rather than being forced into one.
/// </remarks>
public enum IssueCategory
{
    Other,
    Bug,
    Feature,
}
