using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CSharpCity.Analysis;

/// <summary>
/// Loads third-party Roslyn analyzers shipped beside the app and runs them against a target
/// compilation.
/// </summary>
/// <remarks>
/// This is what lets the city react to rules the analyzed solution has never heard of.
/// <c>Compilation.GetDiagnostics</c> only returns the compiler's own diagnostics; analyzer rules
/// need <c>WithAnalyzers</c> and an explicit run. Doing it here rather than adding packages to the
/// target repo means the solution under inspection is never modified.
/// </remarks>
public static class AnalyzerHost
{
    /// <summary>Assembly.LoadFrom is enough here: the analyzers ship as self-contained assemblies.</summary>
    sealed class Loader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    static ImmutableArray<DiagnosticAnalyzer>? _cached;

    /// <summary>Every C# analyzer found in the app's <c>analyzers/</c> folder.</summary>
    public static ImmutableArray<DiagnosticAnalyzer> Load()
    {
        if (_cached is { } cached) return cached;

        var folder = Path.Combine(AppContext.BaseDirectory, "analyzers");
        if (!Directory.Exists(folder))
        {
            _cached = ImmutableArray<DiagnosticAnalyzer>.Empty;
            return _cached.Value;
        }

        var loader = new Loader();
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try
            {
                var reference = new AnalyzerFileReference(path, loader);
                builder.AddRange(reference.GetAnalyzers(LanguageNames.CSharp));
            }
            catch (Exception ex)
            {
                // A rule pack that won't load costs us its rules, not the run.
                Console.Error.WriteLine($"warning: could not load analyzers from " +
                                        $"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _cached = builder.ToImmutable();
        return _cached.Value;
    }

    /// <summary>
    /// Rule id to human title, read from the analyzers' own descriptors.
    /// </summary>
    /// <remarks>
    /// The package is the authority on what its rules mean, and it's already loaded — far better
    /// than guessing from an id. This is what identified S8949 and S8969, which fired 77 times each
    /// on the first run and were sitting in the "other" bucket purely because nobody knew what they
    /// were.
    /// </remarks>
    public static Dictionary<string, string> RuleTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var analyzer in Load())
        foreach (var descriptor in analyzer.SupportedDiagnostics)
            titles.TryAdd(descriptor.Id, descriptor.Title.ToString());

        return titles;
    }

    /// <summary>
    /// Runs the analyzers over one compilation. Returns an empty list if none are installed, so the
    /// caller works unchanged when the feature is switched off.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(Compilation compilation,
        CancellationToken ct)
    {
        var analyzers = Load();
        if (analyzers.IsEmpty) return ImmutableArray<Diagnostic>.Empty;

        // Analyzer exceptions are swallowed rather than thrown: a single misbehaving rule shouldn't
        // abort analysis of a 42-project solution.
        var options = new CompilationWithAnalyzersOptions(
            options: null,
            onAnalyzerException: (ex, analyzer, _) =>
                Console.Error.WriteLine($"warning: {analyzer} threw: {ex.Message}"),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false);

        return await compilation.WithAnalyzers(analyzers, options)
            .GetAnalyzerDiagnosticsAsync(ct);
    }
}
