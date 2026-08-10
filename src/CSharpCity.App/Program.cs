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
    SolutionAnalyzer.ReadHistory = options.Git;
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

// Always lay the city out, even when not rendering: it's the cheapest way to catch a layout
// that silently drops buildings.
var scene = CityLayout.Build(model);
Console.WriteLine($"Scene: {scene.PickInfos.Count} buildings, {scene.Boxes.Count} boxes, " +
                  $"{scene.Ground.Count} districts, {scene.CityBounds.Width:0}m across");

if (options.NoRender) return 0;

using var window = new CityWindow(scene, $"CSharpCity â€” {model.SolutionName}");
window.Run();
return 0;

sealed record CommandLineOptions(
    string? SolutionPath, string? DumpJson, string? FromJson, bool NoRender, bool Demo,
    bool Analyzers, bool Git)
{
    public static CommandLineOptions? Parse(string[] args)
    {
        string? solution = null, dump = null, from = null;
        bool noRender = false, demo = false, analyzers = false, git = false;

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
                case "-h" or "--help": return null;
                default:
                    if (args[i].StartsWith('-')) return null;
                    solution = args[i];
                    break;
            }
        }

        if (solution is null && from is null && !demo) return null;
        return new CommandLineOptions(solution, dump, from, noRender, demo, analyzers, git);
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
          --git               Read the repository's history: what is changing, and who changes it.
        """);
}

