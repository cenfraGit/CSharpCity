using CSharpCity.Model;

namespace CSharpCity.Render;

/// <summary>
/// The window's connection to the remote: what it last said, and how to ask it again.
/// </summary>
/// <remarks>
/// Two delegates rather than a reference to the analysis and layout assemblies, so the window still
/// knows nothing about Roslyn, gh, or how an overlay is built. It knows only that something can be
/// fetched and something can then be applied to the scene it is drawing.
/// </remarks>
public sealed class WorksFeed
{
    public WorksFeed(GitHubSnapshot snapshot,
        Func<CancellationToken, Task<GitHubSnapshot>> fetch,
        Action<GitHubSnapshot> apply)
    {
        Snapshot = snapshot;
        Fetch = fetch;
        Apply = apply;
    }

    /// <summary>The most recent answer. Replaced wholesale by a refresh.</summary>
    public GitHubSnapshot Snapshot { get; set; }

    /// <summary>Asks the remote again. Runs off the render thread.</summary>
    public Func<CancellationToken, Task<GitHubSnapshot>> Fetch { get; }

    /// <summary>Rebuilds the overlay in the scene from a snapshot. Must run on the render thread.</summary>
    public Action<GitHubSnapshot> Apply { get; }

    /// <summary>True while a fetch is in flight, so the panel can say so and refuse a second one.</summary>
    public bool Refreshing { get; set; }

    /// <summary>What went wrong last time, if anything.</summary>
    public string? LastError { get; set; }
}
