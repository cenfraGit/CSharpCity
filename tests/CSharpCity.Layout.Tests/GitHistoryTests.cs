using CSharpCity.Analysis;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The git log parser, against fixture output.
/// </summary>
/// <remarks>
/// Worth testing precisely because the failure mode is silent. The first version of this put the
/// commit marker <em>after</em> the hash in the format string, so no header line was ever recognised;
/// every commit therefore carried no timestamp, fell outside the recency window, and the whole city
/// reported zero churn. Nothing threw, nothing warned, and the city simply looked like a codebase
/// nobody had touched in three months — a strong and entirely false claim.
/// </remarks>
public class GitHistoryTests
{
    const char Marker = '\u0001';
    const char Separator = '\u0002';

    /// <summary>Builds log output in exactly the shape the real format string produces.</summary>
    static string Log(params (string Author, int DaysAgo, (int Added, int Deleted, string Path)[] Files)[] commits)
    {
        var text = new System.Text.StringBuilder();
        foreach (var (author, daysAgo, files) in commits)
        {
            long epoch = DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeSeconds();
            text.Append(Marker).Append("deadbeef").Append(Separator).Append(epoch)
                .Append(Separator).Append(author).Append('\n');
            foreach (var (added, deleted, path) in files)
                text.Append(added).Append('\t').Append(deleted).Append('\t').Append(path).Append('\n');
            text.Append('\n');
        }
        return text.ToString();
    }

    [Fact]
    public void CountsCommitsAndLinesInsideTheWindow()
    {
        var log = Log(
            ("ada@example.com", 1, new[] { (10, 2, "src/A.cs") }),
            ("ada@example.com", 5, new[] { (3, 1, "src/A.cs"), (7, 0, "src/B.cs") }));

        var files = GitHistory.Parse(log);

        Assert.Equal(2, files["src/A.cs"].Commits);
        Assert.Equal(16, files["src/A.cs"].LinesChanged);   // 10+2 then 3+1
        Assert.Equal(1, files["src/B.cs"].Commits);
    }

    [Fact]
    public void ChurnRespectsTheWindowButAuthorshipDoesNot()
    {
        var log = Log(
            ("ada@example.com", 5, new[] { (1, 0, "src/A.cs") }),
            ("grace@example.com", GitHistory.WindowDays + 60, new[] { (500, 400, "src/A.cs") }));

        var file = GitHistory.Parse(log)["src/A.cs"];

        // Ownership is a fact about a file's whole life; a window would report every long-stable
        // file as having no authors at all.
        Assert.Equal(2, file.Authors.Count);
        // Churn is about now, so the ancient rewrite must not count toward it.
        Assert.Equal(1, file.Commits);
        Assert.Equal(1, file.LinesChanged);
    }

    [Fact]
    public void OneCommitTouchingAFileCountsOnce()
    {
        // git reports one numstat row per path, but a commit that touches a file twice — which
        // happens with merges and with some rewrite tooling — is still one commit.
        var log = Log(("ada@example.com", 2,
            new[] { (5, 0, "src/A.cs"), (6, 0, "src/A.cs") }));

        Assert.Equal(1, GitHistory.Parse(log)["src/A.cs"].Commits);
    }

    [Fact]
    public void BinaryRowsDoNotCorruptTheCounts()
    {
        // A binary file reports "-" for both columns rather than a number.
        var log = Log(("ada@example.com", 2, new[] { (0, 0, "src/A.cs") }))
            .Replace("0\t0\tsrc/A.cs", "-\t-\tsrc/A.cs");

        var file = GitHistory.Parse(log)["src/A.cs"];
        Assert.Equal(1, file.Commits);
        Assert.Equal(0, file.LinesChanged);
    }

    [Fact]
    public void AbsolutePathsMapOntoTheRelativeOnesGitReports()
    {
        // Roslyn hands out rooted Windows paths with backslashes; git reports repo-relative paths
        // with forward slashes. They never agree by accident.
        string root = Path.Combine(Path.GetTempPath(), "repo");
        string file = Path.Combine(root, "src", "Widgets", "Thing.cs");

        Assert.Equal("src/Widgets/Thing.cs", GitHistory.Relativise(root, file));
    }

    [Fact]
    public void APathOutsideTheRepositoryIsNotClaimed()
    {
        string root = Path.Combine(Path.GetTempPath(), "repo");
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Thing.cs");

        Assert.Null(GitHistory.Relativise(root, outside));
        Assert.Null(GitHistory.Relativise(root, ""));
    }

    [Fact]
    public void AbsenceOfARepositoryIsReportedRatherThanThrown()
    {
        // A solution outside a working tree is an ordinary situation, not an error: the run must
        // continue and simply have no history to show.
        var model = Fixture.Solution(2, 5, 2);
        model.SolutionPath = Path.Combine(Path.GetTempPath(), "no-such-place", "x.slnx");

        var result = GitHistory.Apply(model);

        Assert.False(result.Available);
        Assert.NotNull(result.Reason);
        Assert.All(model.Projects.SelectMany(p => p.Types), t => Assert.Equal(0, t.Commits));
    }
}
