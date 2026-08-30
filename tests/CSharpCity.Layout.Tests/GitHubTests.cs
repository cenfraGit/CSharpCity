using CSharpCity.Analysis;
using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The gh output parser, against fixture JSON in the shape the real CLI emits.
/// </summary>
/// <remarks>
/// Tested the same way the git log parser is, and for the same reason: the parse is a pure function
/// of a string, so the whole thing can be checked without a network, an account, or a repository.
/// The failure mode is also the same — a payload that parses to nothing produces a city with no
/// works on it, which looks exactly like a repository nobody has open pull requests against.
/// </remarks>
public class GitHubTests
{
    /// <summary>One pull request, in the field shape <c>gh pr list --json</c> actually returns.</summary>
    static string PullRequests(string body) => $"[{body}]";

    const string Ordinary = """
        {
          "number": 42,
          "title": "Tidy the parser",
          "author": { "login": "ada", "is_bot": false },
          "isDraft": false,
          "reviewDecision": "APPROVED",
          "mergeable": "MERGEABLE",
          "additions": 30,
          "deletions": 12,
          "updatedAt": "2026-08-28T10:00:00Z",
          "files": [
            { "path": "src/A.cs", "additions": 20, "deletions": 2, "changeType": "MODIFIED" },
            { "path": "src/New.cs", "additions": 10, "deletions": 0, "changeType": "ADDED" }
          ],
          "statusCheckRollup": [
            { "conclusion": "SUCCESS" }, { "conclusion": "SKIPPED" }
          ]
        }
        """;

    [Fact]
    public void ReadsAPullRequestAndTheFilesItTouches()
    {
        var pulls = GitHub.ParsePullRequests(PullRequests(Ordinary), DateTimeOffset.UtcNow);

        var pull = Assert.Single(pulls);
        Assert.Equal(42, pull.Number);
        Assert.Equal("ada", pull.Author);
        Assert.Equal(ReviewState.Approved, pull.Review);
        Assert.False(pull.IsDraft);
        Assert.Equal(42, pull.Churn);

        // The file list is the whole reason this is worth fetching: it is what lets a pull request
        // be drawn on the buildings it actually changes.
        Assert.Equal(2, pull.Files.Count);
        Assert.Equal(FileChange.Modified, pull.Files[0].Change);
        Assert.Equal(FileChange.Added, pull.Files[1].Change);
    }

    [Fact]
    public void OnlyAnExplicitConflictCounts()
    {
        // UNKNOWN means GitHub has not finished working it out, which is not the same as a clash.
        // Treating it as one would close roads all over the city seconds after any push.
        string unknown = Ordinary.Replace("\"MERGEABLE\"", "\"UNKNOWN\"");
        string clash = Ordinary.Replace("\"MERGEABLE\"", "\"CONFLICTING\"");

        Assert.False(GitHub.ParsePullRequests(PullRequests(unknown), DateTimeOffset.UtcNow)[0].Conflicting);
        Assert.True(GitHub.ParsePullRequests(PullRequests(clash), DateTimeOffset.UtcNow)[0].Conflicting);
    }

    [Fact]
    public void SkippedChecksAreNotFailures()
    {
        // Most repositories skip a great many checks on any given pull request. Counting those as
        // red would put a failed-inspection notice on nearly every site in the city.
        Assert.False(GitHub.ParsePullRequests(PullRequests(Ordinary), DateTimeOffset.UtcNow)[0].ChecksFailing);

        string failed = Ordinary.Replace("\"conclusion\": \"SUCCESS\"", "\"conclusion\": \"FAILURE\"");
        Assert.True(GitHub.ParsePullRequests(PullRequests(failed), DateTimeOffset.UtcNow)[0].ChecksFailing);
    }

    [Theory]
    [InlineData("bug", IssueCategory.Bug)]
    [InlineData("kind/bug", IssueCategory.Bug)]
    [InlineData("regression", IssueCategory.Bug)]
    [InlineData("enhancement", IssueCategory.Feature)]
    [InlineData("api-suggestion", IssueCategory.Feature)]
    [InlineData("area-System.Text", IssueCategory.Other)]
    [InlineData("untriaged", IssueCategory.Other)]
    public void LabelsSortIssuesIntoTheRightQueue(string label, IssueCategory expected)
    {
        // Substring matching on a small vocabulary: no two repositories name these the same way,
        // and anything unrecognised has to stay Other rather than being guessed into a queue.
        string json = $$"""
            [{ "number": 1, "title": "x", "createdAt": "2026-08-01T00:00:00Z",
               "labels": [{ "name": "{{label}}" }] }]
            """;

        Assert.Equal(expected, GitHub.ParseIssues(json, DateTimeOffset.UtcNow)[0].Category);
    }

    [Fact]
    public void AgeIsMeasuredFromWhenTheIssueWasOpened()
    {
        var now = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        string json = """
            [{ "number": 1, "title": "x", "createdAt": "2026-06-29T00:00:00Z", "labels": [] }]
            """;

        Assert.Equal(61, GitHub.ParseIssues(json, now)[0].DaysOpen);
    }

    [Fact]
    public void AMalformedPayloadCostsTheOverlayAndNotTheRun()
    {
        // gh is another program on somebody else's machine. Whatever it hands back, the city still
        // has to render — the same bargain the analyzer pack and the git log already make.
        Assert.Empty(GitHub.ParsePullRequests("not json at all", DateTimeOffset.UtcNow));
        Assert.Empty(GitHub.ParsePullRequests("{\"unexpected\":\"object\"}", DateTimeOffset.UtcNow));
        Assert.Empty(GitHub.ParseIssues("", DateTimeOffset.UtcNow));
        Assert.Equal("", GitHub.ParseRepositoryName("["));
    }

    [Fact]
    public void ReadsTheRepositoryName()
    {
        Assert.Equal("owner/name",
            GitHub.ParseRepositoryName("""{"nameWithOwner":"owner/name"}"""));
    }

    [Fact]
    public void FilesAreJoinedToTheBuildingsTheyDeclare()
    {
        // The join that makes the whole feature work: gh reports repo-relative forward-slashed
        // paths, Roslyn reports rooted Windows ones, and neither knows about the other.
        string root = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");
        var model = new CityModel { SolutionPath = Path.Combine(root, "S.slnx") };
        var project = new ProjectNode { Name = "App" };
        project.Types.Add(Fixture.Type("App", "App.Core", "Widget"));
        project.Types[0].FilePath = Path.Combine(root, "src", "Core", "Widget.cs");
        model.Projects.Add(project);

        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            var snapshot = new GitHubSnapshot { Available = true };
            snapshot.PullRequests.Add(new PullRequestInfo
            {
                Number = 1,
                Files =
                {
                    new ChangedFile { Path = "src/Core/Widget.cs", Change = FileChange.Modified },
                    new ChangedFile { Path = "src/Core/Later.cs", Change = FileChange.Added },
                    new ChangedFile { Path = "README.md", Change = FileChange.Modified },
                },
            });

            GitHub.Resolve(model, snapshot);
            var files = snapshot.PullRequests[0].Files;

            Assert.Equal(project.Types[0].Id, Assert.Single(files[0].TypeIds));

            // A file that does not exist yet has no building, but its folder still says which
            // project's ground it would stand on — which is the only way to place a proposal.
            Assert.Empty(files[1].TypeIds);
            Assert.Equal("App", files[1].Project);

            // And something outside the solution belongs to nobody.
            Assert.Empty(files[2].TypeIds);
            Assert.Null(files[2].Project);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
