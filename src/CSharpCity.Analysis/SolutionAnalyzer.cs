using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpCity.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using ModelTypeKind = CSharpCity.Model.TypeKind;
using RoslynTypeKind = Microsoft.CodeAnalysis.TypeKind;

namespace CSharpCity.Analysis;

/// <summary>Loads a solution with Roslyn and measures every type into a <see cref="CityModel"/>.</summary>
public sealed class SolutionAnalyzer
{
    public IProgress<string>? Progress { get; init; }

    public async Task<CityModel> AnalyzeAsync(string solutionPath, CancellationToken ct = default)
    {
        MsBuildBootstrap.Register();

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            // Missing analyzers and unresolved package refs are noise; genuine load failures are not.
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Progress?.Report($"  ! {e.Diagnostic.Message}");
        };

        Progress?.Report($"Loading {Path.GetFileName(solutionPath)}...");
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        var model = new CityModel
        {
            SolutionName = Path.GetFileNameWithoutExtension(solutionPath),
            SolutionPath = Path.GetFullPath(solutionPath),
        };

        // Maps a type symbol to the node we built for it, so the second pass can resolve
        // references into edges without re-walking the syntax.
        var nodesBySymbol = new Dictionary<ISymbol, TypeNode>(SymbolEqualityComparer.Default);
        var projectOfNode = new Dictionary<TypeNode, string>();
        var references = new Dictionary<(TypeNode From, ISymbol To), int>();

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;

            Progress?.Report($"Analyzing {project.Name}...");
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            var projectNode = new ProjectNode
            {
                Name = project.Name,
                Path = project.FilePath ?? "",
                IsTestProject = LooksLikeTestProject(project),
                ProjectReferences = project.ProjectReferences
                    .Select(r => solution.GetProject(r.ProjectId)?.Name)
                    .Where(n => n is { Length: > 0 })
                    .Select(n => n!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                PackageReferences = ReadPackageReferences(project.FilePath),
            };

            foreach (var document in project.Documents)
            {
                if (document.FilePath is null || !document.SupportsSyntaxTree) continue;
                if (IsGenerated(document.FilePath)) continue;

                var tree = await document.GetSyntaxTreeAsync(ct);
                if (tree is null) continue;
                var root = await tree.GetRootAsync(ct);
                var semanticModel = compilation.GetSemanticModel(tree);
                int fileLoc = tree.GetText(ct).Lines.Count;

                foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                             .Cast<SyntaxNode>()
                             .Concat(root.DescendantNodes().OfType<DelegateDeclarationSyntax>()))
                {
                    if (semanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol) continue;

                    // Partial types produce one declaration per file; merge into a single building.
                    if (!nodesBySymbol.TryGetValue(symbol, out var node))
                    {
                        node = CreateNode(symbol, document.FilePath, project.Name);
                        nodesBySymbol[symbol] = node;
                        projectOfNode[node] = project.Name;
                        projectNode.Types.Add(node);
                    }
                    else
                    {
                        node.IsPartial = true;
                    }

                    node.FileLoc = Math.Max(node.FileLoc, fileLoc);
                    node.Loc += SyntaxMetrics.LineCount(declaration);
                    MeasureDeclaration(node, declaration, semanticModel, ct);
                    CollectReferences(node, declaration, semanticModel, symbol, references, ct);
                }

                MeasureTopLevelStatements(root, compilation, semanticModel, projectNode, document.FilePath,
                    fileLoc, nodesBySymbol, projectOfNode, project.Name, references, ct);
            }

            CollectDiagnostics(compilation, nodesBySymbol, ct);
            if (RunAnalyzers)
                Attribute(compilation, await AnalyzerHost.RunAsync(compilation, ct), nodesBySymbol, ct);
            model.Projects.Add(projectNode);
        }

        foreach (var (rule, count) in RuleTally) model.RuleTally[rule] = count;

        BuildEdges(model, nodesBySymbol, projectOfNode, references);
        DetectTypeLevelSmells(model);

        // History last: it needs every type's file path already recorded, and it is the one pass
        // that asks the repository rather than the compiler.
        if (ReadHistory) ReportHistory(GitHistory.Apply(model));

        return model;
    }

    /// <summary>
    /// Top-level statements compile into a synthesized entry-point type that has no declaration
    /// syntax of its own. Without this the whole Program.cs body is invisible to the analyzer, and
    /// everything it is the sole consumer of gets falsely boarded up as dead code.
    /// </summary>
    static void MeasureTopLevelStatements(SyntaxNode root, Compilation compilation,
        SemanticModel semanticModel, ProjectNode projectNode, string filePath, int fileLoc,
        Dictionary<ISymbol, TypeNode> nodesBySymbol, Dictionary<TypeNode, string> projectOfNode,
        string projectName, Dictionary<(TypeNode, ISymbol), int> references, CancellationToken ct)
    {
        if (root is not CompilationUnitSyntax unit) return;
        var globals = unit.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count == 0) return;

        if (compilation.GetEntryPoint(ct)?.ContainingType is not { } entrySymbol) return;
        if (!nodesBySymbol.TryGetValue(entrySymbol, out var node))
        {
            node = CreateNode(entrySymbol, filePath, projectName);
            node.Name = "Program";
            node.IsPublic = true;
            nodesBySymbol[entrySymbol] = node;
            projectOfNode[node] = projectName;
            projectNode.Types.Add(node);
        }

        node.FileLoc = Math.Max(node.FileLoc, fileLoc);
        node.Loc += globals.Sum(SyntaxMetrics.LineCount);

        // The entry point really is one method, so give it one floor sized to the whole body.
        int complexity = globals.Sum(g => SyntaxMetrics.CyclomaticComplexity(g) - 1) + 1;
        node.Methods.Add(new MethodNode
        {
            Name = "<top-level>",
            Loc = node.Loc,
            IsPublic = true,
            Complexity = complexity,
            MaxNesting = globals.Max(SyntaxMetrics.MaxNestingDepth),
        });
        node.AvgComplexity = node.Methods.Average(m => m.Complexity);
        node.MaxComplexity = node.Methods.Max(m => m.Complexity);
        node.MaxNesting = node.Methods.Max(m => m.MaxNesting);

        foreach (var statement in globals)
            CollectReferences(node, statement, semanticModel, entrySymbol, references, ct);
    }

    /// <summary>
    /// Machine-written files. XAML code-behind, designer files and everything under <c>obj\</c> are
    /// build output, not code anyone maintains — giving them buildings fills a WPF city with
    /// structures nobody wrote and which no one can act on.
    /// </summary>
    static bool IsGenerated(string filePath)
    {
        var name = Path.GetFileName(filePath.AsSpan());

        return filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
               || name.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
               || name.Equals("GlobalUsings.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attributes every compiler warning and error to the type it was reported in.
    /// </summary>
    /// <remarks>
    /// These are the real build diagnostics, not hand-rolled heuristics — the same list the compiler
    /// prints. That makes them the most trustworthy quality signal available, and it costs nothing
    /// extra in dependencies. A codebase that is genuinely null-safe simply has no broken windows.
    /// </remarks>
    /// <summary>
    /// Whether to run third-party analyzer rules as well as the compiler's own. Off by default:
    /// hundreds of extra rules across a large solution is a real time cost, and the fast path is
    /// what you want while iterating on the renderer.
    /// </summary>
    public static bool RunAnalyzers { get; set; }

    /// <summary>
    /// Whether to ask the repository what has been changing. Off by default for the same reason as
    /// the analyzers: it is another external tool, and a solution outside a working tree simply has
    /// no answer to give.
    /// </summary>
    public static bool ReadHistory { get; set; }

    /// <summary>
    /// Says what the repository did or didn't yield.
    /// </summary>
    /// <remarks>
    /// A silent absence would be the worst outcome here: every crane in the city would quietly
    /// vanish and the city would read as a codebase nobody is working on, which is a much stronger
    /// claim than "there was no git here".
    /// </remarks>
    static void ReportHistory(GitHistory.Result history)
    {
        if (!history.Available)
        {
            Console.Error.WriteLine($"warning: no repository history ({history.Reason}); " +
                "the city will show nothing under construction.");
            return;
        }

        Console.Error.WriteLine(
            $"note: {history.TypesTouched:n0} type(s) matched to {history.FilesWithHistory:n0} " +
            $"file(s) of history over the last {GitHistory.WindowDays} days; busiest is " +
            $"{history.BusiestFile} with {history.BusiestCommits} commit(s).");
    }

    /// <summary>Counts of each analyzer rule that fired, for reporting what a solution looks like.</summary>
    public static Dictionary<string, int> RuleTally { get; } = new(StringComparer.Ordinal);

    /// <summary>Attributes analyzer diagnostics to the type each was reported in.</summary>
    static void Attribute(Compilation compilation, ImmutableArray<Diagnostic> diagnostics,
        Dictionary<ISymbol, TypeNode> nodesBySymbol, CancellationToken ct)
    {
        var models = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var diagnostic in diagnostics)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree?.FilePath is null || IsGenerated(tree.FilePath)) continue;

            RuleTally[diagnostic.Id] = RuleTally.GetValueOrDefault(diagnostic.Id) + 1;

            var node = tree.GetRoot(ct).FindNode(diagnostic.Location.SourceSpan,
                getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() is not { } declaration) continue;

            if (!models.TryGetValue(tree, out var model))
                models[tree] = model = compilation.GetSemanticModel(tree);

            if (model.GetDeclaredSymbol(declaration, ct) is not { } symbol) continue;
            if (!nodesBySymbol.TryGetValue(symbol, out var type)) continue;

            type.AnalyzerWarnings++;
            CategorizeAnalyzerRule(type, diagnostic.Id);
        }
    }

    /// <summary>Sorts an analyzer rule into the kind of urban condition it produces.</summary>
    static void CategorizeAnalyzerRule(TypeNode type, string id)
    {
        if (SecurityRules.Contains(id)) { type.SecurityFindings++; return; }
        if (LeakRules.Contains(id)) { type.LeakFindings++; return; }

        switch (id)
        {
            // Things left lying around: locals, fields and members nobody reads.
            case "S1481" or "S1854" or "S1144" or "S4487" or "S1172" or "S3264" or "S1450":
                type.ClutterFindings++;
                break;

            // Code that's been superseded but never taken down.
            case "S125" or "S1133":
                type.StaleFindings++;
                break;

            // Work that adds nothing: redundant casts, jumps, initializers, null-forgiving `!`.
            case "S8969" or "S3604" or "S3626" or "S1905" or "S2971" or "S3267"
                 or "S1125" or "S1066" or "S3878" or "S1116" or "S1155" or "S4201" or "S1939":
                type.RedundancyFindings++;
                break;

            case "S2094" or "S1186" or "S108" or "S3237":
                type.EmptyFindings++;
                break;

            // "The overload accepting a 'CancellationToken' should be used" — work with no stop button.
            case "S8949":
                type.UncancellableFindings++;
                break;

            case "S907":
                type.GotoFindings++;
                break;
        }
    }

    /// <summary>
    /// Sonar rules that describe a security weakness rather than untidiness. Deliberately narrow:
    /// these become police cordons, and a cordon is only meaningful if it's rare.
    /// </summary>
    static readonly HashSet<string> SecurityRules = new(StringComparer.Ordinal)
    {
        "S2245",  // insecure pseudorandom number generator
        "S4426",  // cryptographic key size too small
        "S6444",  // regex without a timeout (ReDoS)
        "S4790",  // weak hashing algorithm
        "S5542",  // weak cipher mode / padding
        "S4830",  // server certificate validation disabled
        "S2076",  // OS command injection
        "S2083",  // path injection
        "S3649",  // SQL injection
        "S5443",  // insecure temporary file
        "S2053",  // hard-coded salt
        "S4507",  // debug features in production
        "S5122",  // permissive CORS
        "S2115",  // database without password
        "S4423",  // weak TLS protocol
    };

    /// <summary>Resource lifetime failures: something was opened and never closed.</summary>
    static readonly HashSet<string> LeakRules = new(StringComparer.Ordinal)
    {
        "S3881",  // IDisposable implemented incorrectly
        "S2930",  // IDisposable not disposed
        "S2952",  // disposed in the wrong place
        "S3966",  // disposed twice
    };

    static void CollectDiagnostics(Compilation compilation, Dictionary<ISymbol, TypeNode> nodesBySymbol,
        CancellationToken ct)
    {
        // One semantic model per tree, reused: GetSemanticModel builds a fresh one on every call.
        var models = new Dictionary<SyntaxTree, SemanticModel>();

        foreach (var diagnostic in compilation.GetDiagnostics(ct))
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Warning or DiagnosticSeverity.Error))
                continue;

            var tree = diagnostic.Location.SourceTree;
            if (tree?.FilePath is null || IsGenerated(tree.FilePath)) continue;

            var node = tree.GetRoot(ct).FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() is not { } declaration) continue;

            if (!models.TryGetValue(tree, out var model))
                models[tree] = model = compilation.GetSemanticModel(tree);

            if (model.GetDeclaredSymbol(declaration, ct) is not { } symbol) continue;
            if (!nodesBySymbol.TryGetValue(symbol, out var type)) continue;

            if (diagnostic.Severity == DiagnosticSeverity.Error) type.CompileErrors++;
            else Categorize(type, diagnostic.Id);
        }
    }

    /// <summary>Sorts a warning id into the kind of decay it produces.</summary>
    static void Categorize(TypeNode type, string id)
    {
        // CS86xx is the whole nullable-reference family: possible null dereference, null literal
        // assignment, non-nullable field left unassigned, and so on.
        if (id.StartsWith("CS86", StringComparison.Ordinal)) { type.NullWarnings++; return; }

        switch (id)
        {
            case "CS0168":   // declared but never used
            case "CS0169":   // never used
            case "CS0219":   // assigned but value never used
            case "CS0162":   // unreachable code
            case "CS0414":   // assigned but never used
            case "CS8321":   // local function never used
                type.UnusedWarnings++;
                break;

            case "CS0612":   // obsolete, no message
            case "CS0618":   // obsolete, with message
            case "CS0672":
                type.ObsoleteWarnings++;
                break;

            default:
                type.OtherWarnings++;
                break;
        }
    }

    /// <summary>
    /// Package ids straight from the project file.
    /// </summary>
    /// <remarks>
    /// Roslyn's <c>Project.MetadataReferences</c> lists resolved assembly paths — the whole
    /// transitive closure, including the framework — which would make every district's airport look
    /// identical. The declared &lt;PackageReference&gt; set is what the team actually chose to take
    /// on, so that's what gets shown.
    /// </remarks>
    static List<string> ReadPackageReferences(string? projectPath)
    {
        if (projectPath is null || !File.Exists(projectPath)) return new List<string>();

        try
        {
            return XDocument.Load(projectPath)
                .Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value)
                .Where(id => id is { Length: > 0 })
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // A malformed or locked csproj costs this district its airport, not the whole run.
            return new List<string>();
        }
    }

    static bool LooksLikeTestProject(Project project) =>
        project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)
        || project.Name.Contains("Spec", StringComparison.OrdinalIgnoreCase)
        || project.MetadataReferences.Any(r =>
            r.Display?.Contains("xunit", StringComparison.OrdinalIgnoreCase) == true
            || r.Display?.Contains("nunit", StringComparison.OrdinalIgnoreCase) == true
            || r.Display?.Contains("MSTest", StringComparison.OrdinalIgnoreCase) == true);

    /// <remarks>
    /// The id is qualified by project because a fully-qualified type name is <em>not</em> unique
    /// across a solution. WPF emits <c>XamlGeneratedNamespace.GeneratedInternalTypeHelper</c> into
    /// every WPF project, and linked source files produce the same type in several projects too.
    /// </remarks>
    static TypeNode CreateNode(INamedTypeSymbol symbol, string filePath, string projectName) => new()
    {
        Id = $"{projectName}!{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}",
        // Without the global:: prefix, so it matches how base types and interfaces are recorded.
        FullName = symbol.ToDisplayString(),
        Name = symbol.Name,
        IsObsolete = symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute"),
        PublicMemberCount = symbol.GetMembers().Count(m =>
            m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared),
        Namespace = symbol.ContainingNamespace.IsGlobalNamespace
            ? "<global>"
            : symbol.ContainingNamespace.ToDisplayString(),
        FilePath = filePath,
        Kind = MapKind(symbol),
        IsPublic = symbol.DeclaredAccessibility == Accessibility.Public,
        IsSealed = symbol.IsSealed && symbol.TypeKind == RoslynTypeKind.Class,
        IsDisposable = symbol.AllInterfaces.Any(i =>
            i.Name is "IDisposable" or "IAsyncDisposable"),
        InheritanceDepth = InheritanceDepth(symbol),
        BaseType = symbol.BaseType?.ToDisplayString(),
        // Full names, not bare ones: they're matched against FullName to count implementors, and
        // "IHandler" collides across namespaces in any solution this size.
        Interfaces = symbol.Interfaces.Select(i => i.ToDisplayString()).ToList(),
        EnumMemberCount = symbol.TypeKind == RoslynTypeKind.Enum
            ? symbol.GetMembers().Count(m => m.Kind == SymbolKind.Field)
            : 0,
    };

    static ModelTypeKind MapKind(INamedTypeSymbol symbol) => symbol.TypeKind switch
    {
        RoslynTypeKind.Interface => ModelTypeKind.Interface,
        RoslynTypeKind.Enum => ModelTypeKind.Enum,
        RoslynTypeKind.Delegate => ModelTypeKind.Delegate,
        RoslynTypeKind.Struct => ModelTypeKind.Struct,
        RoslynTypeKind.Class when symbol.IsRecord => ModelTypeKind.Record,
        // Static and abstract get their own silhouettes, so they must be checked before plain class.
        RoslynTypeKind.Class when symbol.IsStatic => ModelTypeKind.StaticClass,
        RoslynTypeKind.Class when symbol.IsAbstract => ModelTypeKind.AbstractClass,
        _ => ModelTypeKind.Class,
    };

    /// <summary>Levels below the root of the hierarchy. Becomes the stilt height under the building.</summary>
    static int InheritanceDepth(INamedTypeSymbol symbol)
    {
        int depth = 0;
        for (var b = symbol.BaseType; b is not null && b.SpecialType != SpecialType.System_Object
                                                    && b.SpecialType != SpecialType.System_ValueType; b = b.BaseType)
            depth++;
        return depth;
    }

    /// <summary>Walks one declaration, accumulating member counts, complexity and syntax-level smells.</summary>
    static void MeasureDeclaration(TypeNode node, SyntaxNode declaration, SemanticModel semanticModel,
        CancellationToken ct)
    {
        if (declaration.Modifiers().Any(SyntaxKind.PartialKeyword)) node.IsPartial = true;

        // Civic metrics: where a type throws judgement, and where it treats the wounded.
        node.ThrowCount += declaration.DescendantNodes().OfType<ThrowStatementSyntax>().Count()
                           + declaration.DescendantNodes().OfType<ThrowExpressionSyntax>().Count();
        node.CatchCount += declaration.DescendantNodes().OfType<CatchClauseSyntax>().Count();
        node.FactoryReturnCount += declaration.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Count(r => r.Expression is ObjectCreationExpressionSyntax
                                     or ImplicitObjectCreationExpressionSyntax);

        foreach (var member in declaration.ChildNodes())
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    node.FieldCount += field.Declaration.Variables.Count;
                    RecordFieldSmells(node, field);
                    break;

                case PropertyDeclarationSyntax:
                    node.PropertyCount++;
                    break;

                case ConstructorDeclarationSyntax ctor:
                    if (ctor.Modifiers.Any(SyntaxKind.PublicKeyword)) node.PublicCtorCount++;
                    break;

                case EventDeclarationSyntax:
                    node.EventCount++;
                    break;

                case EventFieldDeclarationSyntax eventField:
                    node.EventCount += eventField.Declaration.Variables.Count;
                    break;

                case MethodDeclarationSyntax method:
                    node.Methods.Add(MeasureMethod(node, method));
                    break;
            }
        }

        // Records with a primary constructor have no ConstructorDeclarationSyntax at all.
        if (node.PublicCtorCount == 0 && node.Kind is ModelTypeKind.Class or ModelTypeKind.Record
                                      or ModelTypeKind.Struct)
            node.PublicCtorCount = 1;

        RecordTriviaSmells(node, declaration);
        RecordBodySmells(node, declaration);

        if (node.Methods.Count > 0)
        {
            node.AvgComplexity = node.Methods.Average(m => m.Complexity);
            node.MaxComplexity = node.Methods.Max(m => m.Complexity);
            node.MaxNesting = node.Methods.Max(m => m.MaxNesting);
        }
    }

    /// <summary>
    /// Trims a source-written type to what fits on a floor sign: namespaces dropped, whitespace
    /// collapsed. "System.Threading.Tasks.Task&lt;int&gt;" becomes "Task&lt;int&gt;".
    /// </summary>
    static string ShortTypeName(string written)
    {
        var trimmed = NamespaceQualifier.Replace(written, "");
        trimmed = Whitespace.Replace(trimmed, "");
        return trimmed.Length > 28 ? trimmed[..27] + "…" : trimmed;
    }

    static readonly Regex NamespaceQualifier = new(@"\b[A-Za-z_]\w*\s*\.", RegexOptions.Compiled);
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    static MethodNode MeasureMethod(TypeNode node, MethodDeclarationSyntax method)
    {
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        var result = new MethodNode
        {
            Name = method.Identifier.Text,
            ReturnType = ShortTypeName(method.ReturnType.ToString()),
            Loc = SyntaxMetrics.LineCount(method),
            ParameterCount = method.ParameterList.Parameters.Count,
            IsPublic = method.Modifiers.Any(SyntaxKind.PublicKeyword),
            IsAsync = method.Modifiers.Any(SyntaxKind.AsyncKeyword),
            Complexity = body is null ? 1 : SyntaxMetrics.CyclomaticComplexity(body),
            MaxNesting = body is null ? 0 : SyntaxMetrics.MaxNestingDepth(body),
        };

        if (result.Loc > 60)
            node.Smells.Add(new Smell
            {
                Kind = SmellKind.LongMethod,
                Detail = $"{result.Name} is {result.Loc} lines",
                Line = SyntaxMetrics.StartLine(method),
            });

        if (result.ParameterCount > 5)
            node.Smells.Add(new Smell
            {
                // Counts offending methods, not parameters, so collapsing stays meaningful.
                Kind = SmellKind.LongParameterList,
                Detail = $"{result.Name} takes {result.ParameterCount} parameters",
                Line = SyntaxMetrics.StartLine(method),
            });

        return result;
    }

    static void RecordFieldSmells(TypeNode node, FieldDeclarationSyntax field)
    {
        bool isPublic = field.Modifiers.Any(SyntaxKind.PublicKeyword);
        bool isReadonly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                          || field.Modifiers.Any(SyntaxKind.ConstKeyword);
        bool isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword);
        int count = field.Declaration.Variables.Count;

        if (isPublic && !isReadonly)
            node.Smells.Add(new Smell
            {
                Kind = SmellKind.PublicMutableField,
                Count = count,
                Detail = string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text)),
                Line = SyntaxMetrics.StartLine(field),
            });

        if (isStatic && !isReadonly)
            node.Smells.Add(new Smell
            {
                Kind = SmellKind.StaticMutableState,
                Count = count,
                Detail = string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text)),
                Line = SyntaxMetrics.StartLine(field),
            });
    }

    /// <summary>TODO markers, commented-out code and #region abuse all live in trivia.</summary>
    static void RecordTriviaSmells(TypeNode node, SyntaxNode declaration)
    {
        int todos = 0, deadComments = 0, regions = 0;

        foreach (var trivia in declaration.DescendantTrivia())
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineCommentTrivia:
                case SyntaxKind.MultiLineCommentTrivia:
                    var text = trivia.ToString();
                    if (text.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("HACK", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("FIXME", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("XXX", StringComparison.Ordinal))
                        todos++;
                    else if (SyntaxMetrics.LooksLikeCode(text))
                        deadComments++;
                    break;

                case SyntaxKind.RegionDirectiveTrivia:
                    regions++;
                    break;
            }
        }

        if (todos > 0)
            node.Smells.Add(new Smell { Kind = SmellKind.TodoComment, Count = todos });
        if (deadComments > 0)
            node.Smells.Add(new Smell { Kind = SmellKind.CommentedOutCode, Count = deadComments });
        if (regions > 3)
            node.Smells.Add(new Smell { Kind = SmellKind.RegionAbuse, Count = regions });
    }

    static void RecordBodySmells(TypeNode node, SyntaxNode declaration)
    {
        foreach (var catchClause in declaration.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            // A catch that neither handles nor rethrows silently eats the failure.
            if (catchClause.Block.Statements.Count == 0)
                node.Smells.Add(new Smell
                {
                    Kind = SmellKind.EmptyCatch,
                    Detail = "exception swallowed",
                    Line = SyntaxMetrics.StartLine(catchClause),
                });
        }

        foreach (var creation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.Type is IdentifierNameSyntax { Identifier.Text: "NotImplementedException" }
                or QualifiedNameSyntax { Right.Identifier.Text: "NotImplementedException" })
                node.Smells.Add(new Smell
                {
                    Kind = SmellKind.NotImplemented,
                    Line = SyntaxMetrics.StartLine(creation),
                });
        }
    }

    /// <summary>
    /// Records which other types this one mentions. Aggregated later into weighted road edges.
    /// </summary>
    static void CollectReferences(TypeNode node, SyntaxNode declaration, SemanticModel semanticModel,
        INamedTypeSymbol self, Dictionary<(TypeNode, ISymbol), int> references, CancellationToken ct)
    {
        foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var referenced = semanticModel.GetSymbolInfo(identifier, ct).Symbol;
            var target = referenced as INamedTypeSymbol ?? referenced?.ContainingType;
            if (target is null) continue;

            target = target.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(target, self)) continue;

            var key = (node, (ISymbol)target);
            references[key] = references.TryGetValue(key, out int existing) ? existing + 1 : 1;
        }
    }

    static void BuildEdges(CityModel model, Dictionary<ISymbol, TypeNode> nodesBySymbol,
        Dictionary<TypeNode, string> projectOfNode, Dictionary<(TypeNode From, ISymbol To), int> references)
    {
        // Fallback index for symbols that aren't reference-equal to the ones we declared.
        var byFullName = new Dictionary<string, List<TypeNode>>(StringComparer.Ordinal);
        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            if (type.FullName.Length == 0) continue;
            if (!byFullName.TryGetValue(type.FullName, out var list))
                byFullName[type.FullName] = list = new List<TypeNode>();
            list.Add(type);
        }

        foreach (var ((from, toSymbol), weight) in references)
        {
            // Only intra-solution references become roads; framework types aren't in the city.
            if (!nodesBySymbol.TryGetValue(toSymbol, out var to))
            {
                to = ResolveByName(toSymbol, byFullName, projectOfNode);
                if (to is null) continue;
            }

            model.Edges.Add(new DependencyEdge
            {
                FromId = from.Id,
                ToId = to.Id,
                Weight = weight,
                CrossProject = projectOfNode[from] != projectOfNode[to],
            });

            from.FanOut++;
            to.FanIn++;
        }
    }

    /// <summary>
    /// Matches a symbol to a node by fully-qualified name when reference equality fails.
    /// </summary>
    /// <remarks>
    /// Roslyn only hands out the <em>same</em> symbol instance across projects when it links them
    /// with a compilation reference. Where it falls back to the compiled assembly — which it does
    /// whenever a project doesn't fully evaluate — the referenced type arrives as a different
    /// symbol and the edge was being dropped on the floor. On a solution where several projects
    /// don't fully evaluate, that made more than half the solution's projects look as though they
    /// used nothing they referenced.
    /// </remarks>
    static TypeNode? ResolveByName(ISymbol symbol, Dictionary<string, List<TypeNode>> byFullName,
        Dictionary<TypeNode, string> projectOfNode)
    {
        if (!byFullName.TryGetValue(symbol.ToDisplayString(), out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0];

        // Same type name in several projects: the assembly it came from names the right one.
        var assembly = symbol.ContainingAssembly?.Name;
        return candidates.FirstOrDefault(c =>
            string.Equals(projectOfNode[c], assembly, StringComparison.Ordinal));
    }

    /// <summary>Smells that need whole-solution context: size totals and the dependency graph.</summary>
    static void DetectTypeLevelSmells(CityModel model)
    {
        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            if (type.Methods.Count > 40 || type.Loc > 500)
                type.Smells.Add(new Smell
                {
                    Kind = SmellKind.GodClass,
                    Detail = $"{type.Methods.Count} methods, {type.Loc} LOC",
                });

            if (type.FileLoc > 1000)
                type.Smells.Add(new Smell
                {
                    Kind = SmellKind.OversizedFile,
                    Detail = $"{type.FileLoc} lines in {Path.GetFileName(type.FilePath)}",
                });

            // Nothing in the solution references it. Public API of a library is the false positive
            // here, so only non-public types get boarded up.
            if (type.FanIn == 0 && !type.IsPublic && type.Kind != ModelTypeKind.Enum)
                type.Smells.Add(new Smell { Kind = SmellKind.DeadCode, Detail = "no references in solution" });
        }

        MarkCircularDependencies(model);
        ComputeCivicMetrics(model);
        CollapseSmells(model);
    }

    /// <summary>
    /// Fills in the counts that can only be known once every type is in hand: who inherits from
    /// whom, who implements what, and which references cross a project boundary.
    /// </summary>
    static void ComputeCivicMetrics(CityModel model)
    {
        var types = model.Projects.SelectMany(p => p.Types).ToList();

        var byFullName = new Dictionary<string, List<TypeNode>>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (type.FullName.Length == 0) continue;
            if (!byFullName.TryGetValue(type.FullName, out var list))
                byFullName[type.FullName] = list = new List<TypeNode>();
            list.Add(type);
        }

        foreach (var type in types)
        {
            if (type.BaseType is { Length: > 0 } baseName
                && byFullName.TryGetValue(baseName, out var bases))
                foreach (var b in bases) b.DerivedCount++;

            foreach (var contract in type.Interfaces)
                if (byFullName.TryGetValue(contract, out var interfaces))
                    foreach (var i in interfaces) i.ImplementorCount++;
        }

        var byId = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var type in types) byId.TryAdd(type.Id, type);

        foreach (var edge in model.Edges)
            if (edge.CrossProject && byId.TryGetValue(edge.ToId, out var target))
                target.CrossProjectFanIn += edge.Weight;
    }

    /// <summary>
    /// Folds repeated smells of the same kind into one entry carrying the total count. The renderer
    /// scatters that many props on the lot, so a struct with seven public fields gets seven open
    /// windows rather than seven separate smell records saying the same thing.
    /// </summary>
    static void CollapseSmells(CityModel model)
    {
        foreach (var type in model.Projects.SelectMany(p => p.Types))
        {
            if (type.Smells.Count < 2) continue;

            type.Smells = type.Smells
                .GroupBy(s => s.Kind)
                .Select(group => new Smell
                {
                    Kind = group.Key,
                    Count = group.Sum(s => s.Count),
                    Line = group.Min(s => s.Line),
                    Detail = string.Join("; ", group.Select(s => s.Detail)
                        .Where(d => d.Length > 0).Take(3)),
                })
                .OrderByDescending(s => s.Count)
                .ToList();
        }
    }

    /// <summary>Flags every type that sits on a two-node reference cycle: the roundabouts.</summary>
    static void MarkCircularDependencies(CityModel model)
    {
        // Not ToDictionary: ids are meant to be unique, but a analyzer bug or an exotic project
        // layout shouldn't take the whole run down over a duplicate key.
        var byId = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var type in model.Projects.SelectMany(p => p.Types))
            byId.TryAdd(type.Id, type);
        var pairs = new HashSet<(string, string)>(model.Edges.Select(e => (e.FromId, e.ToId)));

        foreach (var edge in model.Edges)
        {
            if (string.CompareOrdinal(edge.FromId, edge.ToId) >= 0) continue;
            if (!pairs.Contains((edge.ToId, edge.FromId))) continue;

            foreach (var id in new[] { edge.FromId, edge.ToId })
                if (byId.TryGetValue(id, out var type))
                    type.Smells.Add(new Smell
                    {
                        Kind = SmellKind.CircularDependency,
                        Detail = $"{Short(edge.FromId)} <-> {Short(edge.ToId)}",
                    });
        }

        static string Short(string id) => id[(id.LastIndexOf('.') + 1)..];
    }
}

file static class SyntaxNodeExtensions
{
    /// <summary>Modifier list for whichever declaration shape we're looking at.</summary>
    public static SyntaxTokenList Modifiers(this SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Modifiers,
        DelegateDeclarationSyntax del => del.Modifiers,
        _ => default,
    };
}
