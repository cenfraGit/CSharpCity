using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// Builds synthetic <see cref="CityModel"/>s with a given shape, so tests can describe the kind of
/// codebase they care about rather than hand-assembling nodes.
/// </summary>
internal static class Fixture
{
    /// <summary>
    /// A solution of <paramref name="projects"/> projects, each with <paramref name="typesPer"/>
    /// types spread over <paramref name="namespaceDepth"/> levels of nesting.
    /// </summary>
    public static CityModel Solution(int projects, int typesPer, int namespaceDepth = 2,
        bool includeTests = false)
    {
        var model = new CityModel { SolutionName = "Fixture" };

        for (int p = 0; p < projects; p++)
        {
            var project = new ProjectNode
            {
                Name = $"Proj{p}",
                IsTestProject = includeTests && p % 3 == 2,
            };

            for (int t = 0; t < typesPer; t++)
            {
                // Fan the types across a nested namespace trie rather than one flat list.
                var parts = new List<string> { $"Proj{p}" };
                for (int d = 0; d < namespaceDepth; d++) parts.Add($"N{(t + d) % 3}");

                project.Types.Add(Type($"Proj{p}", string.Join('.', parts), $"T{t}",
                    methods: t % 7, fields: t % 5));
            }

            model.Projects.Add(project);
        }

        return model;
    }

    public static TypeNode Type(string project, string ns, string name, int methods = 3,
        int fields = 2, int properties = 1)
    {
        var node = new TypeNode
        {
            Id = $"{project}!global::{ns}.{name}",
            FullName = $"{ns}.{name}",
            Name = name,
            Namespace = ns,
            FilePath = $"{name}.cs",
            Kind = TypeKind.Class,
            IsPublic = true,
            FieldCount = fields,
            PropertyCount = properties,
            Loc = 20 + methods * 12,
        };

        for (int m = 0; m < methods; m++)
            node.Methods.Add(new MethodNode
            {
                Name = $"M{m}",
                ReturnType = "void",
                Loc = 6 + m,
                ParameterCount = m % 4,
                IsPublic = m % 2 == 0,
                Complexity = 1 + m % 3,
            });

        return node;
    }

    /// <summary>
    /// Wires a solution up: type-to-type edges, and the project references implied by the
    /// cross-project ones.
    /// </summary>
    /// <remarks>
    /// <see cref="Solution"/> deliberately produces an unconnected model, and most invariants don't
    /// care. Anything about footpaths, walkers, rail or roundabouts does — against a bare solution
    /// those subsystems emit nothing at all, so a test that thinks it is guarding them is guarding
    /// zero against zero. Edges are picked by fixed arithmetic rather than at random so the shape
    /// of the graph is reproducible, and a failure is reproducible with it.
    ///
    /// Every edge points at a <em>later</em> type, which makes the graph acyclic by construction.
    /// That matters more than it sounds: <see cref="TrafficNetwork"/> drops any edge whose two ends
    /// are both inside a cycle, and a modular rule like <c>i -> (s*i + 1) mod n</c> is a permutation
    /// whenever s and n are coprime — every type lands in a cycle, every edge is dropped, and a
    /// model with thousands of dependencies produces not one footpath. A handful of deliberate back
    /// edges are added afterwards, so the cycle machinery still has something to find.
    /// </remarks>
    public static CityModel Connect(CityModel model, int stride = 7)
    {
        var ids = model.Projects
            .SelectMany(p => p.Types.Select(t => (Project: p, Type: t)))
            .ToList();
        if (ids.Count < 2) return model;

        var referenced = new HashSet<(string From, string To)>();

        for (int i = 0; i < ids.Count; i++)
        {
            var (fromProject, from) = ids[i];

            // Forward-only: a near neighbour, and one further off so some edges cross projects.
            // Every 40th type also closes a small loop back, to seed a few genuine cycles.
            var targets = new List<int> { i + 1 + i % 3, i + stride + i % 11 };
            if (i % 40 == 39) targets.Add(i - 5);

            foreach (int target in targets)
            {
                if (target < 0 || target >= ids.Count) continue;
                var (toProject, to) = ids[target];
                if (from.Id == to.Id) continue;

                bool crossProject = !ReferenceEquals(fromProject, toProject);
                model.Edges.Add(new DependencyEdge
                {
                    FromId = from.Id,
                    ToId = to.Id,
                    Weight = 1 + i % 9,
                    CrossProject = crossProject,
                });

                if (crossProject && referenced.Add((fromProject.Name, toProject.Name)))
                    fromProject.ProjectReferences.Add(toProject.Name);
            }
        }

        // A couple of packages per project, shared enough that airports differ in size.
        for (int p = 0; p < model.Projects.Count; p++)
            for (int k = 0; k <= p % 3; k++)
                model.Projects[p].PackageReferences.Add($"Pkg{(p + k) % 5}");

        return model;
    }

    /// <summary>Every type id the model contains, in declaration order.</summary>
    public static IEnumerable<string> AllTypeIds(CityModel model) =>
        model.Projects.SelectMany(p => p.Types).Select(t => t.Id);
}
