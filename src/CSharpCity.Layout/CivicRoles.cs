using CSharpCity.Model;
using ModelTypeKind = CSharpCity.Model.TypeKind;

namespace CSharpCity.Layout;

/// <summary>What a building is to its district, beyond what its code does.</summary>
internal enum CivicRole
{
    None,
    /// <summary>Highest fan-in: everyone comes here.</summary>
    TownHall,
    /// <summary>The interface with the most implementors: many congregants, one creed.</summary>
    Cathedral,
    /// <summary>Stateless static class with the widest public surface. Everyone borrows from it.</summary>
    Library,
    /// <summary>Where judgement is passed — the most `throw` sites.</summary>
    Courthouse,
    /// <summary>Where broken things are treated — the most `catch` clauses.</summary>
    Hospital,
    /// <summary>The abstract base that teaches the most subclasses.</summary>
    School,
    /// <summary>Builds and hands out objects.</summary>
    Factory,
    /// <summary>Static mutable state: hums, and everything is wired to it.</summary>
    PowerStation,
    /// <summary>Highest cross-project fan-in: the freight terminal of its district.</summary>
    Depot,
}

/// <summary>
/// Awards civic roles, at most one of each per project, to that project's most extreme type.
/// </summary>
/// <remarks>
/// Superlative rather than threshold. A fixed bar ("fan-in above 20 is a town hall") behaves wildly
/// differently across codebases: a large project would sprout a dozen cathedrals while a small tidy
/// one got none. Awarding each role once per district guarantees a legible civic core at any size,
/// and makes the meaning exact — this is <em>the</em> most depended-upon type here.
///
/// Minimum bars still apply, so a project with one interface and two classes doesn't get a cathedral
/// for an interface nobody implements.
/// </remarks>
internal static class CivicRoles
{
    public static Dictionary<string, CivicRole> Assign(ProjectNode project)
    {
        var awarded = new Dictionary<string, CivicRole>(StringComparer.Ordinal);
        if (project.IsTestProject) return awarded;   // parkland, not a civic centre

        // Order matters: a type can hold only one role, and the earlier ones are the ones you most
        // want to be able to find.
        Award(CivicRole.TownHall, t => t.FanIn, 3);
        Award(CivicRole.Cathedral, t => t.Kind == ModelTypeKind.Interface ? t.ImplementorCount : 0, 2);
        Award(CivicRole.School, t => t.Kind == ModelTypeKind.AbstractClass ? t.DerivedCount : 0, 2);
        Award(CivicRole.PowerStation, t => Smell(t, SmellKind.StaticMutableState), 1);
        Award(CivicRole.Depot, t => t.CrossProjectFanIn, 3);
        Award(CivicRole.Library,
            t => t.Kind == ModelTypeKind.StaticClass && t.FieldCount == 0 ? t.PublicMemberCount : 0, 4);
        Award(CivicRole.Hospital, t => t.CatchCount, 3);
        Award(CivicRole.Courthouse, t => t.ThrowCount, 4);
        Award(CivicRole.Factory, t => t.FactoryReturnCount, 3);

        return awarded;

        void Award(CivicRole role, Func<TypeNode, int> score, int minimum)
        {
            TypeNode? best = null;
            int bestScore = minimum - 1;

            foreach (var type in project.Types)
            {
                if (awarded.ContainsKey(type.Id)) continue;
                if (type.Kind == ModelTypeKind.Delegate || type.Kind == ModelTypeKind.Enum) continue;

                int value = score(type);
                // Ties break on id so the same solution always crowns the same building.
                if (value > bestScore
                    || (value == bestScore && best is not null
                                           && string.CompareOrdinal(type.Id, best.Id) < 0))
                {
                    best = type;
                    bestScore = value;
                }
            }

            if (best is not null && bestScore >= minimum) awarded[best.Id] = role;
        }
    }

    static int Smell(TypeNode type, SmellKind kind) =>
        type.Smells.FirstOrDefault(s => s.Kind == kind)?.Count ?? 0;

    public static string Title(CivicRole role) => role switch
    {
        CivicRole.TownHall => "TOWN HALL",
        CivicRole.Cathedral => "CATHEDRAL",
        CivicRole.Library => "LIBRARY",
        CivicRole.Courthouse => "COURTHOUSE",
        CivicRole.Hospital => "HOSPITAL",
        CivicRole.School => "SCHOOL",
        CivicRole.Factory => "FACTORY",
        CivicRole.PowerStation => "POWER STATION",
        CivicRole.Depot => "DEPOT",
        _ => "",
    };

    /// <summary>The metric that earned the role, so the plaque can say why this building.</summary>
    public static string Citation(CivicRole role, TypeNode type) => role switch
    {
        CivicRole.TownHall => $"{type.FanIn} references in",
        CivicRole.Cathedral => $"{type.ImplementorCount} implementors",
        CivicRole.Library => $"{type.PublicMemberCount} public members, no state",
        CivicRole.Courthouse => $"{type.ThrowCount} throw sites",
        CivicRole.Hospital => $"{type.CatchCount} catch blocks",
        CivicRole.School => $"{type.DerivedCount} subclasses",
        CivicRole.Factory => $"{type.FactoryReturnCount} objects built",
        CivicRole.PowerStation => $"{Smell(type, SmellKind.StaticMutableState)} static fields",
        CivicRole.Depot => $"{type.CrossProjectFanIn} cross-project references",
        _ => "",
    };
}
