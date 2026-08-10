using System.Diagnostics;
using CSharpCity.Model;

namespace CSharpCity.Analysis;

/// <summary>
/// Reads the repository's own memory: how often each file changes, and how many people change it.
/// </summary>
/// <remarks>
/// Everything else in this city comes from one snapshot of the source, which means it can describe
/// the shape of the code and nothing at all about its life. A file churning weekly and a file
/// untouched for a year are indistinguishable; so is a file twelve people fight over from one only
/// its author has ever opened. Those are among the first things anyone actually wants to know.
///
/// One <c>git log --numstat</c> answers all of it. Measured on a repository with a few thousand
/// commits over a year of history, the whole call takes well under a second and returns tens of
/// thousands of lines, which is nothing beside the Roslyn pass it sits next to. There is
/// deliberately no libgit2 dependency: the porcelain output of this one command is stable, and
/// shelling out costs less than
/// the ceremony of avoiding it.
/// </remarks>
public static class GitHistory
{
    /// <summary>
    /// How far back "recently" reaches, in days.
    /// </summary>
    /// <remarks>
    /// A week, so "under active work" means this week and not this quarter. Ninety days was the first
    /// choice and marked a quarter of the city as changing, which is true and useless. Authors are
    /// counted over all history instead — ownership is a fact about a file's whole life, and a
    /// window would report every long-stable file as having a bus factor of zero.
    /// </remarks>
    public const int WindowDays = 7;

    /// <summary>
    /// Delimiters for the log format. Control characters rather than punctuation, because neither
    /// can occur in a path or an email address — so a line is never ambiguous about which kind of
    /// line it is, however odd the repository's contents.
    /// </summary>
    const char Marker = (char)1;
    const char Separator = (char)2;

    public sealed record Result(bool Available, int FilesWithHistory, int TypesTouched,
        string BusiestFile, int BusiestCommits, string? Reason);

    /// <summary>Fans file-level history onto every type, in place.</summary>
    public static Result Apply(CityModel model)
    {
        string? root = FindRepositoryRoot(model.SolutionPath);
        if (root is null)
            return new Result(false, 0, 0, "", 0, "no .git directory above the solution");

        if (!TryRunGit(root, out string log, out string? failure))
            return new Result(false, 0, 0, "", 0, failure);

        var history = Parse(log);
        if (history.Count == 0)
            return new Result(false, 0, 0, "", 0, "the repository reported no history for C# files");

        int touched = 0;
        var today = DateTimeOffset.UtcNow;

        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            string? key = Relativise(root, type.FilePath);
            if (key is null || !history.TryGetValue(key, out var file)) continue;

            type.Commits = file.Commits;
            type.Authors = file.Authors.Count;
            type.LinesChanged = file.LinesChanged;
            type.DaysSinceChange = (int)(today - file.LastChange).TotalDays;
            touched++;
        }

        var busiest = history.OrderByDescending(e => e.Value.Commits).First();
        return new Result(true, history.Count, touched, busiest.Key, busiest.Value.Commits, null);
    }

    internal sealed class FileHistory
    {
        public int Commits;
        public int LinesChanged;
        public DateTimeOffset LastChange = DateTimeOffset.MinValue;
        public readonly HashSet<string> Authors = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the log. Commits are marked by a leading marker line so a commit and a numstat row
    /// can never be confused for one another.
    /// </summary>
    /// <remarks>
    /// <c>--no-renames</c> is deliberate. Following renames would attribute a file's history to its
    /// former path, and since the whole point here is to key on the path Roslyn hands us, a rename
    /// that git helpfully resolves is a row that no longer matches any type.
    /// </remarks>
    internal static Dictionary<string, FileHistory> Parse(string log)
    {
        var files = new Dictionary<string, FileHistory>(StringComparer.OrdinalIgnoreCase);
        var window = DateTimeOffset.UtcNow.AddDays(-WindowDays);

        string author = "";
        var when = DateTimeOffset.MinValue;
        var seenThisCommit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in log.Split('\n'))
        {
            var text = line.AsSpan().TrimEnd('\r');
            if (text.IsEmpty) continue;

            if (text[0] == Marker)
            {
                // Header: marker, unix seconds, author.
                var parts = text[1..].ToString().Split(Separator);
                if (parts.Length < 3) continue;
                when = long.TryParse(parts[1], out long epoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : DateTimeOffset.MinValue;
                author = parts[2];
                seenThisCommit.Clear();
                continue;
            }

            // "<added>\t<deleted>\t<path>", where a binary file reports "-" for both.
            var columns = text.ToString().Split('\t');
            if (columns.Length < 3) continue;

            string path = columns[2].Replace('\\', '/');
            if (!files.TryGetValue(path, out var file)) files[path] = file = new FileHistory();

            file.Authors.Add(author);
            if (when > file.LastChange) file.LastChange = when;

            // Only the recent window counts toward churn; authorship counts over all of it.
            if (when < window) continue;

            // One commit touching a file is one commit, however many rows it produced.
            if (seenThisCommit.Add(path)) file.Commits++;
            if (int.TryParse(columns[0], out int added)) file.LinesChanged += added;
            if (int.TryParse(columns[1], out int deleted)) file.LinesChanged += deleted;
        }

        return files;
    }

    /// <summary>
    /// Turns Roslyn's absolute path into the repo-relative, forward-slashed form git reports.
    /// </summary>
    /// <remarks>
    /// These two never agree by accident: MSBuild hands out fully-rooted Windows paths with
    /// backslashes, and git reports paths relative to the repository root with forward slashes.
    /// </remarks>
    internal static string? Relativise(string root, string absolute)
    {
        if (string.IsNullOrEmpty(absolute)) return null;

        string full = Path.GetFullPath(absolute);
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        return full[prefix.Length..].TrimStart('\\', '/').Replace('\\', '/');
    }

    /// <summary>Walks up from the solution looking for a working tree.</summary>
    internal static string? FindRepositoryRoot(string? solutionPath)
    {
        if (string.IsNullOrEmpty(solutionPath)) return null;

        var directory = Directory.Exists(solutionPath)
            ? new DirectoryInfo(solutionPath)
            : new FileInfo(solutionPath).Directory;

        for (; directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))   // a worktree or submodule
                return directory.FullName;

        return null;
    }

    /// <summary>
    /// Runs the log. A repository that cannot be read is a fact to report, never a reason to abort
    /// a run that was going to succeed anyway — the same treatment a missing analyzer pack gets.
    /// </summary>
    static bool TryRunGit(string root, out string output, out string? failure)
    {
        output = "";
        failure = null;

        // U+0001 marks a header and U+0002 separates its fields: neither can occur in an email
        // address or a path, so parsing never has to guess what kind of line it is looking at.
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
                 {
                     "log", "--numstat", "--no-renames", "--no-merges",
                     // The marker must LEAD the line: the parser identifies a header by its first
                     // character, and putting it after the hash means no header is ever recognised,
                     // every commit falls outside the window, and the whole city reads as untouched.
                     $"--format={Marker}%H{Separator}%at{Separator}%ae", "--", "*.cs",
                 })
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) { failure = "git could not be started"; return false; }

            output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0) return true;
            failure = error.Split('\n').FirstOrDefault()?.Trim() ?? $"git exited {process.ExitCode}";
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }
}
