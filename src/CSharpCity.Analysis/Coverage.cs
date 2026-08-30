using System.Globalization;
using System.Xml.Linq;
using CSharpCity.Model;

namespace CSharpCity.Analysis;

/// <summary>
/// Reads a Cobertura coverage report and fans it onto individual methods.
/// </summary>
/// <remarks>
/// <b>A file, not a test run.</b> Running the target's tests would mean building somebody else's
/// solution, guessing its test framework, and failing whenever their suite does — a great deal of
/// machinery for a number the tooling already emits. Cobertura is what coverlet, dotCover and
/// Visual Studio all produce, so one path argument covers essentially every .NET setup:
///
/// <code>dotnet test --collect:"XPlat Code Coverage"</code>
///
/// <b>Joined by line, because that is the only key there is.</b> The report identifies code by file
/// and line number; a <see cref="MethodNode"/> has a name, and names are not unique across overloads.
/// Line spans are the one thing both sides agree on, which is why <see cref="MethodNode.StartLine"/>
/// exists at all.
///
/// A method that no line in the report falls inside keeps <see cref="MethodNode.Coverage"/> at -1,
/// meaning unmeasured — deliberately distinct from 0, which means measured and never executed.
/// </remarks>
public static class Coverage
{
    public sealed record Result(bool Available, int Files, int MethodsCovered, int MethodsMeasured,
        double Overall, string? Reason);

    public static Result Apply(CityModel model, string path)
    {
        if (!File.Exists(path))
            return new Result(false, 0, 0, 0, 0, $"no coverage file at {path}");

        Dictionary<string, Dictionary<int, int>> hits;
        try
        {
            hits = Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new Result(false, 0, 0, 0, 0, ex.Message);
        }

        if (hits.Count == 0)
            return new Result(false, 0, 0, 0, 0, "the coverage report listed no source files");

        int measured = 0, covered = 0;
        double total = 0;

        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            // The report's filenames may be absolute or relative to anywhere; matching on the tail
            // of the path is what survives that without needing to know the build's layout.
            var lines = Lookup(hits, type.FilePath);
            if (lines is null) continue;

            foreach (var method in type.Methods)
            {
                if (method.StartLine <= 0 || method.EndLine < method.StartLine) continue;

                int statements = 0, executed = 0;
                for (int line = method.StartLine; line <= method.EndLine; line++)
                {
                    if (!lines.TryGetValue(line, out int count)) continue;
                    statements++;
                    if (count > 0) executed++;
                }

                // A declaration with no measurable statements — an auto-property, an abstract or
                // extern method — is not uncovered, it is uncoverable. Leaving it unmeasured stops
                // the city painting damp on things no test could ever reach.
                if (statements == 0) continue;

                method.Coverage = executed / (double)statements;
                measured++;
                total += method.Coverage;
                if (method.Coverage > 0) covered++;
            }
        }

        return measured == 0
            ? new Result(false, hits.Count, 0, 0, 0,
                "no method in the solution matched a line in the report")
            : new Result(true, hits.Count, covered, measured, total / measured, null);
    }

    /// <summary>
    /// Line hit counts per source file, keyed by the report's own path spelling.
    /// </summary>
    /// <remarks>
    /// Cobertura nests <c>&lt;class filename&gt;</c> inside <c>&lt;package&gt;</c>, and a single
    /// file routinely appears as several classes — one per type, plus one per compiler-generated
    /// closure. Their line sets are merged rather than replacing one another, or whichever class
    /// happened to come last would be the only one counted.
    /// </remarks>
    internal static Dictionary<string, Dictionary<int, int>> Parse(string xml)
    {
        var files = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(xml);

        foreach (var element in document.Descendants("class"))
        {
            string? filename = element.Attribute("filename")?.Value;
            if (string.IsNullOrEmpty(filename)) continue;

            string key = Normalise(filename);
            if (!files.TryGetValue(key, out var lines))
                files[key] = lines = new Dictionary<int, int>();

            foreach (var line in element.Descendants("line"))
            {
                if (!int.TryParse(line.Attribute("number")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int number)) continue;
                if (!int.TryParse(line.Attribute("hits")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int count)) continue;

                // Merged with max, not overwritten: the same line covered by one class and missed by
                // another is covered.
                lines[number] = lines.TryGetValue(number, out int existing)
                    ? Math.Max(existing, count)
                    : count;
            }
        }

        return files;
    }

    /// <summary>
    /// Finds a source file's lines, tolerating the two paths being written differently.
    /// </summary>
    /// <remarks>
    /// An exact match first, then progressively shorter tails. The report is written by whatever
    /// machine ran the tests — often a build agent with an entirely different root — so insisting on
    /// equal absolute paths would silently match nothing at all, which looks exactly like a solution
    /// with no coverage.
    /// </remarks>
    internal static Dictionary<int, int>? Lookup(
        Dictionary<string, Dictionary<int, int>> files, string absolute)
    {
        if (string.IsNullOrEmpty(absolute)) return null;

        string full = Normalise(absolute);
        if (files.TryGetValue(full, out var exact)) return exact;

        // Neither path is generally a suffix of the other — they share a tail and disagree about
        // everything above it, so `C:/dev/repo/src/A.cs` and `/agent/work/1/s/src/A.cs` have only
        // `src/A.cs` in common. Longest shared tail wins, and a tail must be at least a folder and
        // a filename: matching on the filename alone would confidently join the wrong Program.cs.
        var tails = new List<string>();
        for (int i = full.Length - 1; i >= 0; i--)
            if (full[i] == '/') tails.Add(full[(i + 1)..]);

        for (int i = tails.Count - 1; i >= 1; i--)
        {
            string tail = "/" + tails[i];
            foreach (var (candidate, lines) in files)
                if (candidate.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                    || candidate.Equals(tails[i], StringComparison.OrdinalIgnoreCase))
                    return lines;
        }

        return null;
    }

    static string Normalise(string path) => path.Replace('\\', '/');
}
