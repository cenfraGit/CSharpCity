using CSharpCity.Analysis;
using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// Reading a Cobertura report and joining it to methods by line span.
/// </summary>
public class CoverageTests
{
    /// <summary>A report in the shape coverlet emits, for one file.</summary>
    static string Report(string filename, params (int Line, int Hits)[] lines)
    {
        string rows = string.Join("", lines.Select(l =>
            $"""<line number="{l.Line}" hits="{l.Hits}" />"""));

        return $"""
            <coverage>
              <packages><package><classes>
                <class filename="{filename}"><lines>{rows}</lines></class>
              </classes></package></packages>
            </coverage>
            """;
    }

    static CityModel WithMethod(string file, int start, int end)
    {
        var model = new CityModel { SolutionName = "T" };
        var project = new ProjectNode { Name = "P" };
        var type = Fixture.Type("P", "P.N", "T0", methods: 0);
        type.FilePath = file;
        type.Methods.Add(new MethodNode { Name = "M", StartLine = start, EndLine = end });
        project.Types.Add(type);
        model.Projects.Add(project);
        return model;
    }

    [Fact]
    public void CoverageIsJoinedToTheMethodTheLinesFallInside()
    {
        var model = WithMethod("/repo/src/A.cs", 10, 14);
        string xml = Report("/repo/src/A.cs", (10, 3), (11, 3), (12, 0), (13, 0));

        var result = Coverage.Apply(model, Write(xml));

        Assert.True(result.Available);
        Assert.Equal(0.5, model.Projects[0].Types[0].Methods[0].Coverage, 3);
    }

    [Fact]
    public void UnmeasuredIsNotTheSameAsUncovered()
    {
        // The distinction the whole channel rests on. A method no report mentions keeps -1, so the
        // city shows nothing; a method measured and never executed is 0, which is a real finding.
        // Collapsing the two would paint every floor of every building damp whenever somebody
        // forgot the flag, and the city would read as untested rather than unmeasured.
        var model = WithMethod("/repo/src/A.cs", 10, 14);
        model.Projects[0].Types[0].Methods.Add(
            new MethodNode { Name = "Elsewhere", StartLine = 90, EndLine = 95 });

        Coverage.Apply(model, Write(Report("/repo/src/A.cs", (10, 0), (11, 0))));

        Assert.Equal(0d, model.Projects[0].Types[0].Methods[0].Coverage);
        Assert.Equal(-1d, model.Projects[0].Types[0].Methods[1].Coverage);
    }

    [Fact]
    public void AMethodWithNoMeasurableStatementsStaysUnmeasured()
    {
        // An abstract or extern declaration is not uncovered, it is uncoverable. Marking it damp
        // would blame a method for not being tested when no test could ever reach it.
        var model = WithMethod("/repo/src/A.cs", 10, 11);

        Coverage.Apply(model, Write(Report("/repo/src/A.cs", (40, 1))));

        Assert.Equal(-1d, model.Projects[0].Types[0].Methods[0].Coverage);
    }

    [Fact]
    public void PathsAreMatchedByTailSoABuildAgentsRootDoesNotMatter()
    {
        // The report is written by whichever machine ran the tests, often with an entirely
        // different root. Insisting on equal absolute paths would match nothing at all — which
        // looks exactly like a solution with no coverage.
        var model = WithMethod(@"C:\dev\repo\src\A.cs", 10, 12);

        var result = Coverage.Apply(model, Write(Report("/agent/work/1/s/src/A.cs", (10, 5), (11, 5))));

        Assert.True(result.Available);
        Assert.Equal(1d, model.Projects[0].Types[0].Methods[0].Coverage);
    }

    [Fact]
    public void SeveralClassesInOneFileAreMerged()
    {
        // Cobertura emits one <class> per type plus one per compiler-generated closure, all naming
        // the same file. Letting the last one win would discard most of the file's coverage.
        var parsed = Coverage.Parse("""
            <coverage><packages><package><classes>
              <class filename="src/A.cs"><lines><line number="10" hits="0" /></lines></class>
              <class filename="src/A.cs"><lines><line number="11" hits="7" /></lines></class>
            </classes></package></packages></coverage>
            """);

        var lines = Assert.Single(parsed).Value;
        Assert.Equal(2, lines.Count);
        Assert.Equal(7, lines[11]);
    }

    [Fact]
    public void ALineCoveredByOneClassAndMissedByAnotherIsCovered()
    {
        var parsed = Coverage.Parse("""
            <coverage><packages><package><classes>
              <class filename="src/A.cs"><lines><line number="10" hits="0" /></lines></class>
              <class filename="src/A.cs"><lines><line number="10" hits="4" /></lines></class>
            </classes></package></packages></coverage>
            """);

        Assert.Equal(4, parsed["src/A.cs"][10]);
    }

    [Fact]
    public void AMissingOrBrokenReportIsSaidRatherThanThrown()
    {
        var model = WithMethod("/repo/src/A.cs", 10, 12);

        var missing = Coverage.Apply(model, Path.Combine(Path.GetTempPath(), "no-such.xml"));
        Assert.False(missing.Available);
        Assert.NotNull(missing.Reason);

        var broken = Coverage.Apply(model, Write("<not-xml"));
        Assert.False(broken.Available);
        Assert.NotNull(broken.Reason);

        // And in neither case did anything get marked.
        Assert.Equal(-1d, model.Projects[0].Types[0].Methods[0].Coverage);
    }

    [Fact]
    public void UntestedFloorsGrowDampAndTestedOnesDoNot()
    {
        static int Damp(double coverage)
        {
            var model = Fixture.Connect(Fixture.Solution(2, 8, 2));
            foreach (var method in model.Projects.SelectMany(p => p.Types).SelectMany(t => t.Methods))
                method.Coverage = coverage;

            return CityLayout.Build(model).Boxes
                .Count(b => (b.Flags & (uint)BoxFlags.Damp) != 0);
        }

        Assert.True(Damp(0d) > 0, "a wholly untested method should show on its own storey");
        Assert.Equal(0, Damp(1d));
        // The default. Without a report nothing is claimed either way.
        Assert.Equal(0, Damp(-1d));
    }

    static string Write(string xml)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cov-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }
}
