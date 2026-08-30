using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpCity.Analysis;
using CSharpCity.Layout;
using CSharpCity.Model;
using CSharpCity.Render;

var options = CommandLineOptions.Parse(args);
if (options is null)
{
    CommandLineOptions.PrintUsage();
    return 1;
}

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
};

CityModel model;
if (options.FromJson is not null)
{
    Console.WriteLine($"Loading model from {options.FromJson}");
    model = JsonSerializer.Deserialize<CityModel>(File.ReadAllText(options.FromJson), json)
            ?? throw new InvalidOperationException("Model file was empty.");
}
else if (options.Demo)
{
    Console.WriteLine("Generating synthetic demo city (no analysis).");
    model = DemoCity.Build();
}
else
{
    SolutionAnalyzer.RunAnalyzers = options.Analyzers;

    // History is on by default wherever there is a history to read. The flags exist to override
    // that in either direction, not to switch on something you'd otherwise have to know to ask for.
    SolutionAnalyzer.ReadHistory = options.Git ?? GitHistory.IsRepository(options.SolutionPath);
    if (options.Analyzers)
        Console.WriteLine($"Running {AnalyzerHost.Load().Length} third-party analyzer rule sets " +
                          "(slower).");

    var analyzer = new SolutionAnalyzer { Progress = new Progress<string>(Console.WriteLine) };
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    model = await analyzer.AnalyzeAsync(options.SolutionPath!);
    Console.WriteLine($"Analyzed in {stopwatch.Elapsed.TotalSeconds:0.0}s");

    if (SolutionAnalyzer.RuleTally.Count > 0)
    {
        int total = SolutionAnalyzer.RuleTally.Values.Sum();
        Console.WriteLine($"{total} analyzer findings across " +
                          $"{SolutionAnalyzer.RuleTally.Count} distinct rules. Top 20:");
        var titles = AnalyzerHost.RuleTitles();
        foreach (var (rule, count) in SolutionAnalyzer.RuleTally
                     .OrderByDescending(r => r.Value).Take(25))
            Console.WriteLine($"  {count,6}  {rule}  {titles.GetValueOrDefault(rule, "?")}");
    }
}

Console.WriteLine($"{model.SolutionName}: {model.Projects.Count} projects, " +
                  $"{model.Projects.Sum(p => p.Types.Count)} types, {model.Edges.Count} edges");

if (options.DumpJson is not null)
{
    File.WriteAllText(options.DumpJson, JsonSerializer.Serialize(model, json));
    Console.WriteLine($"Wrote {options.DumpJson}");
}

// What a test run reached. A file rather than a test run of our own: see Coverage's remarks.
if (options.Coverage is not null) ReportCoverage(Coverage.Apply(model, options.Coverage));

// What the team is doing, as opposed to what the code says. Asked for on the same terms as the
// history: automatic where there is an answer, silent where there isn't, never fatal.
var github = options.GitHub ?? GitHistory.IsRepository(model.SolutionPath)
    ? await GitHub.FetchAsync(model.SolutionPath)
    : new GitHubSnapshot { Available = false, Reason = "not asked for" };
GitHub.Resolve(model, github);
ReportGitHub(github);

// Always lay the city out, even when not rendering: it's the cheapest way to catch a layout
// that silently drops buildings.
var scene = CityLayout.Build(model, github, options.SeparateCities);
Console.WriteLine($"Scene: {scene.PickInfos.Count} buildings, {scene.Boxes.Count} boxes, " +
                  $"{scene.Ground.Count} districts, {scene.CityBounds.Width:0}m across");

if (options.NoRender) return 0;

// The window knows how to ask for a fresh snapshot and how to hand one back to the layout, but
// nothing about gh or about how an overlay is built.
var feed = github.Available
    ? new WorksFeed(github,
        async ct =>
        {
            var fresh = await GitHub.FetchAsync(model.SolutionPath, ct);
            GitHub.Resolve(model, fresh);
            return fresh;
        },
        fresh => Overlay.Rebuild(scene, model, fresh))
    : null;

using var window = new CityWindow(scene, $"CSharpCity â€” {model.SolutionName}", feed);
window.Run();
return 0;

/// <summary>
/// Says what the remote did or didn't yield, on the same terms as the history note.
/// </summary>
/// <remarks>
/// Silence would be the worst outcome: a city with no works on it would read as a repository nobody
/// has open pull requests against, which is a far stronger claim than "gh wasn't installed".
/// </remarks>
static void ReportCoverage(Coverage.Result coverage)
{
    if (!coverage.Available)
    {
        Console.Error.WriteLine($"warning: no coverage ({coverage.Reason}); " +
            "no floor will be marked as untested.");
        return;
    }

    Console.Error.WriteLine(
        $"note: coverage read for {coverage.Files:n0} file(s); {coverage.MethodsCovered:n0} of " +
        $"{coverage.MethodsMeasured:n0} measured method(s) are reached by a test " +
        $"({coverage.Overall:P0} of statements on average).");
}

static void ReportGitHub(GitHubSnapshot github)
{
    if (!github.Available)
    {
        if (github.Reason != "not asked for")
            Console.Error.WriteLine($"warning: no GitHub data ({github.Reason}); " +
                "the city will show no works and no queues.");
        return;
    }

    int conflicted = github.PullRequests.Count(p => p.Conflicting);
    int drafts = github.PullRequests.Count(p => p.IsDraft);

    Console.Error.WriteLine(
        $"note: {github.Repository} has {github.PullRequests.Count:n0} open pull request(s) " +
        $"({drafts:n0} draft, {conflicted:n0} conflicting) touching " +
        $"{github.PullRequests.SelectMany(p => p.Files).Select(f => f.Path).Distinct().Count():n0} " +
        $"file(s), and {github.Issues.Count:n0} open issue(s).");
}

/// <param name="Git">
/// Null means "decide from the solution": on inside a working tree, off outside one. The flags are
/// an override in either direction.
/// </param>
/// <param name="GitHub">Same convention as <paramref name="Git"/>, for the remote.</param>
sealed record CommandLineOptions(
    string? SolutionPath, string? DumpJson, string? FromJson, bool NoRender, bool Demo,
    bool Analyzers, bool? Git, bool? GitHub, string? Coverage, bool SeparateCities)
{
    public static CommandLineOptions? Parse(string[] args)
    {
        string? solution = null, dump = null, from = null, coverage = null;
        bool noRender = false, demo = false, analyzers = false, cities = false;
        bool? git = null, github = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dump-json" when i + 1 < args.Length: dump = args[++i]; break;
                case "--from-json" when i + 1 < args.Length: from = args[++i]; break;
                case "--no-render": noRender = true; break;
                case "--demo": demo = true; break;
                case "--analyzers": analyzers = true; break;
                case "--git": git = true; break;
                case "--no-git": git = false; break;
                case "--github": github = true; break;
                case "--no-github": github = false; break;
                case "--coverage" when i + 1 < args.Length: coverage = args[++i]; break;
                case "--cities": cities = true; break;
                case "-h" or "--help": return null;
                default:
                    if (args[i].StartsWith('-')) return null;
                    solution = args[i];
                    break;
            }
        }

        if (solution is null && from is null && !demo) return null;
        return new CommandLineOptions(solution, dump, from, noRender, demo, analyzers, git, github,
            coverage, cities);
    }

    public static void PrintUsage() => Console.WriteLine("""
        CSharpCity â€” walk a C# solution as a city.

          csharpcity <path-to.sln> [options]
          csharpcity --demo
          csharpcity --from-json city.json

        Options:
          --demo              Render a synthetic city; skips analysis entirely.
          --dump-json <path>  Write the analyzed model to disk.
          --from-json <path>  Render a previously dumped model.
          --no-render         Analyze only, don't open a window.
          --analyzers         Run third-party analyzer rules as well as the compiler's.
          --git               Force reading the repository's history: what is changing, and who
                              changes it. On by default when the solution is inside a git repository.
          --no-git            Skip the repository history even inside a repository.
          --github            Force asking the remote for open pull requests and issues. On by
                              default in a repository, if the gh CLI is installed and signed in.
          --no-github         Skip the remote entirely.
          --coverage <path>   Read a Cobertura XML report; untested floors grow damp. Produce one
                              with: dotnet test --collect:"XPlat Code Coverage"
          --cities            Lay each project out as its own town with open country between them,
                              rather than as districts of a single city.
        """);
}

