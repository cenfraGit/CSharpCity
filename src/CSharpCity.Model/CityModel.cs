namespace CSharpCity.Model;

/// <summary>
/// The complete, renderer-agnostic description of a analyzed solution.
/// This is the hard boundary between analysis and everything downstream:
/// it serializes to JSON so the renderer can be iterated on without re-running Roslyn.
/// </summary>
public sealed class CityModel
{
    public string SolutionName { get; set; } = "";
    public string SolutionPath { get; set; } = "";
    public List<ProjectNode> Projects { get; set; } = new();

    /// <summary>Type-to-type references, aggregated. Drives roads and traffic.</summary>
    public List<DependencyEdge> Edges { get; set; } = new();

    /// <summary>
    /// How many times each analyzer rule fired across the solution. Kept so the visual mapping can
    /// be designed against what a real codebase actually produces rather than guessed at.
    /// </summary>
    public Dictionary<string, int> RuleTally { get; set; } = new();
}

public sealed class ProjectNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Test projects render as park districts rather than production blocks.</summary>
    public bool IsTestProject { get; set; }
    public List<TypeNode> Types { get; set; } = new();

    /// <summary>
    /// Names of projects this one declares a &lt;ProjectReference&gt; to. Distinct from the type
    /// graph: a reference can be declared and never used, which is exactly what makes it worth
    /// showing — the rail line with no trains is one you can delete.
    /// </summary>
    public List<string> ProjectReferences { get; set; } = new();
    /// <summary>External NuGet package ids. Each district's airport is sized by these.</summary>
    public List<string> PackageReferences { get; set; } = new();
}

public enum TypeKind
{
    Class,
    StaticClass,
    AbstractClass,
    Interface,
    Struct,
    Record,
    Enum,
    Delegate,
}

/// <summary>One building. Every property here maps to exactly one visual channel.</summary>
public sealed class TypeNode
{
    /// <summary>Fully-qualified name. Stable identity, and the RNG seed for deterministic jitter.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }

    public TypeKind Kind { get; set; }
    public bool IsPartial { get; set; }
    public bool IsPublic { get; set; }
    /// <summary>Sealed types get a capped roof: finished, nothing more will be built on top.</summary>
    public bool IsSealed { get; set; }
    /// <summary>Implements IDisposable/IAsyncDisposable. Gets a fire escape — a defined way out.</summary>
    public bool IsDisposable { get; set; }
    /// <summary>Declared events. Become loudspeakers on the roof: this type broadcasts.</summary>
    public int EventCount { get; set; }

    // --- size channels ---
    /// <summary>Lines of code of the type declaration itself.</summary>
    public int Loc { get; set; }
    /// <summary>Lines of code of the whole containing file. Feeds the "spills past its lot" smell.</summary>
    public int FileLoc { get; set; }
    public int FieldCount { get; set; }
    public int PropertyCount { get; set; }
    public int PublicCtorCount { get; set; }
    public int EnumMemberCount { get; set; }

    /// <summary>One floor per method, in declaration order.</summary>
    public List<MethodNode> Methods { get; set; } = new();

    // --- structural channels ---
    public List<string> Interfaces { get; set; } = new();
    public string? BaseType { get; set; }
    /// <summary>Depth below object/ValueType. Becomes the height of the stilts under the building.</summary>
    public int InheritanceDepth { get; set; }

    // --- quality channels ---
    public double AvgComplexity { get; set; }
    public int MaxComplexity { get; set; }
    public int MaxNesting { get; set; }

    // --- graph channels ---
    public int FanIn { get; set; }
    public int FanOut { get; set; }
    /// <summary>References arriving from other projects. Highest in a project becomes the depot.</summary>
    public int CrossProjectFanIn { get; set; }

    // --- civic channels: what makes a type the town hall, the cathedral, the hospital ---
    /// <summary>Namespace-qualified name without the project prefix, for matching base types.</summary>
    public string FullName { get; set; } = "";
    /// <summary>Types naming this one as their base. Drives the school.</summary>
    public int DerivedCount { get; set; }
    /// <summary>Types implementing this interface. Drives the cathedral's spire.</summary>
    public int ImplementorCount { get; set; }
    /// <summary>`throw` statements. Drives the courthouse.</summary>
    public int ThrowCount { get; set; }
    /// <summary>`catch` clauses. Drives the hospital.</summary>
    public int CatchCount { get; set; }
    /// <summary>Methods returning a newly constructed object. Drives the factory.</summary>
    public int FactoryReturnCount { get; set; }
    public int PublicMemberCount { get; set; }
    public bool IsObsolete { get; set; }

    // --- compiler diagnostics: what the build itself complains about, per type ---
    /// <summary>Nullable-reference warnings (CS86xx). Becomes broken glass on the facade.</summary>
    public int NullWarnings { get; set; }
    /// <summary>Unused, unreachable or unassigned code. Becomes rubbish piled on the lot.</summary>
    public int UnusedWarnings { get; set; }
    /// <summary>Use of `[Obsolete]` API. Becomes a condemned notice and cracked render.</summary>
    public int ObsoleteWarnings { get; set; }
    /// <summary>Compile errors. The building is actively ablaze.</summary>
    public int CompileErrors { get; set; }
    public int OtherWarnings { get; set; }
    /// <summary>Third-party analyzer rule hits (Sonar and friends), when that pass is enabled.</summary>
    public int AnalyzerWarnings { get; set; }
    /// <summary>Security hotspots — weak crypto, weak RNG, unbounded regex. Becomes a crime scene.</summary>
    public int SecurityFindings { get; set; }
    /// <summary>Undisposed or mis-implemented IDisposable. Becomes a burst main and an ambulance.</summary>
    public int LeakFindings { get; set; }

    // --- the conditions layer: the quiet majority of findings, read in aggregate ---
    /// <summary>Unused locals, fields, members, dead stores. Becomes refuse on the lot.</summary>
    public int ClutterFindings { get; set; }
    /// <summary>Commented-out code and deprecated API. Becomes peeling fly-posters.</summary>
    public int StaleFindings { get; set; }
    /// <summary>Redundant casts, jumps, initializers, null-forgiving operators. Becomes patched render.</summary>
    public int RedundancyFindings { get; set; }
    /// <summary>Empty classes, methods and blocks. Becomes vacant units with letting boards.</summary>
    public int EmptyFindings { get; set; }
    /// <summary>Missing CancellationToken overloads: work nobody can stop. Becomes idling plant.</summary>
    public int UncancellableFindings { get; set; }
    /// <summary>`goto`. Becomes a zipline off the side of the building.</summary>
    public int GotoFindings { get; set; }

    // --- history channels: what the repository remembers, which static analysis cannot see ---
    //
    // All four are file-level facts, not type-level ones. Several types routinely share one file,
    // and a partial type only records the path of the file it was first seen in, so two types in
    // the same file will always report identical history. That is a real limitation and not worth
    // hiding: the alternative is per-line blame, which costs orders of magnitude more for a signal
    // that is about "is this area of the codebase moving", not about individual declarations.
    /// <summary>Commits touching this type's file inside the recent window. Drives the crane.</summary>
    public int Commits { get; set; }
    /// <summary>Distinct authors over all history. One is a bus factor; a dozen is a contested file.</summary>
    public int Authors { get; set; }
    /// <summary>Lines added plus deleted inside the window. Volume of change, not frequency.</summary>
    public int LinesChanged { get; set; }
    /// <summary>Days since the file last changed. -1 when there is no history for it.</summary>
    public int DaysSinceChange { get; set; } = -1;

    public List<Smell> Smells { get; set; } = new();

    public int MemberCount => Methods.Count + FieldCount + PropertyCount;
}

public sealed class MethodNode
{
    public string Name { get; set; } = "";
    /// <summary>Return type as written in source, shortened for signage (e.g. "Task&lt;int&gt;").</summary>
    public string ReturnType { get; set; } = "void";
    public int Loc { get; set; }
    public int ParameterCount { get; set; }
    public bool IsPublic { get; set; }
    /// <summary>Async methods get an external lift shaft on their storey: you wait for them.</summary>
    public bool IsAsync { get; set; }
    public int Complexity { get; set; }
    public int MaxNesting { get; set; }
}

public enum SmellKind
{
    GodClass,
    LongMethod,
    LongParameterList,
    DeadCode,
    TodoComment,
    CommentedOutCode,
    EmptyCatch,
    NotImplemented,
    PublicMutableField,
    StaticMutableState,
    RegionAbuse,
    OversizedFile,
    CircularDependency,
}

public sealed class Smell
{
    public SmellKind Kind { get; set; }
    /// <summary>How many instances. Drives how many props get scattered on the lot.</summary>
    public int Count { get; set; } = 1;
    public string Detail { get; set; } = "";
    public int Line { get; set; }
}

public sealed class DependencyEdge
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    /// <summary>Number of reference sites. Drives traffic volume on the road.</summary>
    public int Weight { get; set; } = 1;
    /// <summary>True when the two types live in different projects: renders as a highway overpass.</summary>
    public bool CrossProject { get; set; }
}
