using CSharpCity.Model;

/// <summary>
/// A synthetic solution used to exercise the layout and renderer without running Roslyn.
/// Deliberately includes a god class, a dead type and a test project so the visual encodings
/// have something to show before the analyzer exists.
/// </summary>
static class DemoCity
{
    public static CityModel Build()
    {
        var rng = new Random(1337);

        var model = new CityModel { SolutionName = "DemoSolution", SolutionPath = "(synthetic)" };
        var projects = new[]
        {
            ("Demo.Core", 46, false),
            ("Demo.Infrastructure", 30, false),
            ("Demo.Api", 18, false),
            ("Demo.Tests", 22, true),
        };

        foreach (var (name, typeCount, isTest) in projects)
        {
            var project = new ProjectNode { Name = name, Path = $"{name}/{name}.csproj", IsTestProject = isTest };

            for (int i = 0; i < typeCount; i++)
            {
                var kind = (TypeKind)rng.Next(0, 8);
                var methodCount = kind is TypeKind.Enum or TypeKind.Delegate ? 0 : rng.Next(0, 22);

                var type = new TypeNode
                {
                    Id = $"{name}.Generated.Type{i:00}",
                    Name = $"Type{i:00}",
                    Namespace = $"{name}.Generated",
                    FilePath = $"{name}/Generated/Type{i:00}.cs",
                    Line = 1,
                    Kind = kind,
                    IsPublic = rng.NextDouble() < 0.6,
                    Loc = rng.Next(20, 400),
                    FieldCount = rng.Next(0, 14),
                    PropertyCount = rng.Next(0, 10),
                    AvgComplexity = rng.NextDouble() * 8,
                };

                for (int m = 0; m < methodCount; m++)
                {
                    type.Methods.Add(new MethodNode
                    {
                        Name = $"Method{m}",
                        Loc = rng.Next(2, 40),
                        ParameterCount = rng.Next(0, 6),
                        IsPublic = rng.NextDouble() < 0.5,
                        Complexity = rng.Next(1, 12),
                    });
                }

                project.Types.Add(type);
            }

            // One deliberate landmark per production district so the skyline is legible.
            if (!isTest)
            {
                var god = new TypeNode
                {
                    Id = $"{name}.{name.Split('.')[^1]}Manager",
                    Name = $"{name.Split('.')[^1]}Manager",
                    Namespace = name,
                    FilePath = $"{name}/{name.Split('.')[^1]}Manager.cs",
                    Kind = TypeKind.Class,
                    IsPublic = true,
                    Loc = 1840,
                    FieldCount = 31,
                    PropertyCount = 18,
                    AvgComplexity = 14.2,
                };
                for (int m = 0; m < 96; m++)
                    god.Methods.Add(new MethodNode { Name = $"Do{m}", Loc = 24, ParameterCount = 4, IsPublic = true, Complexity = 12 });
                god.Smells.Add(new Smell { Kind = SmellKind.GodClass, Count = 1, Detail = "96 methods, 1840 LOC" });
                project.Types.Add(god);
            }

            model.Projects.Add(project);
        }

        return model;
    }
}
