using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpCity.Analysis;

/// <summary>Pure syntax-level measurements. No semantic model needed, so these are cheap.</summary>
public static class SyntaxMetrics
{
    /// <summary>
    /// Cyclomatic complexity: one, plus one for every independent branch point.
    /// Short-circuit operators count because each one is a path the reader must hold in their head.
    /// </summary>
    public static int CyclomaticComplexity(SyntaxNode body)
    {
        int complexity = 1;
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                case ConditionalAccessExpressionSyntax:
                    complexity++;
                    break;
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.LogicalAndExpression)
                      || binary.IsKind(SyntaxKind.LogicalOrExpression)
                      || binary.IsKind(SyntaxKind.CoalesceExpression):
                    complexity++;
                    break;
            }
        }
        return complexity;
    }

    /// <summary>Deepest run of nested control-flow constructs. Spaghetti, quantified.</summary>
    public static int MaxNestingDepth(SyntaxNode body)
    {
        int max = 0;
        foreach (var node in body.DescendantNodes())
        {
            if (!IsNestingConstruct(node)) continue;
            int depth = 0;
            for (var parent = node.Parent; parent is not null && parent != body.Parent; parent = parent.Parent)
                if (IsNestingConstruct(parent)) depth++;
            max = Math.Max(max, depth + 1);
        }
        return max;
    }

    static bool IsNestingConstruct(SyntaxNode node) => node
        is IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax
        or ForStatementSyntax or ForEachStatementSyntax or SwitchStatementSyntax
        or TryStatementSyntax or LockStatementSyntax or UsingStatementSyntax;

    /// <summary>Physical line span, which is what a reader actually scrolls through.</summary>
    public static int LineCount(SyntaxNode node)
    {
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    public static int StartLine(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    public static int EndLine(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).EndLinePosition.Line + 1;

    /// <summary>
    /// Heuristic for commented-out code: a comment whose text is shaped like a C# statement.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. Looser signals like a bare "=" or "=>" match ordinary prose
    /// ("0 = day, 1 = night") and flood the city with graffiti that isn't there, so terminal
    /// punctuation or a statement keyword is required.
    /// </remarks>
    public static bool LooksLikeCode(string commentText)
    {
        var text = commentText.Trim().TrimStart('/', '*', ' ', '\t').TrimEnd('*', '/', ' ', '\t');
        if (text.Length < 6) return false;

        // Prose ending in a full stop is not code, even if it happens to contain symbols.
        if (text.EndsWith(';') || text.EndsWith('{') || text.EndsWith('}'))
            return true;

        return text.StartsWith("if (") || text.StartsWith("for (") || text.StartsWith("foreach (")
            || text.StartsWith("while (") || text.StartsWith("switch (")
            || text.StartsWith("return ") || text.StartsWith("var ") || text.StartsWith("await ")
            || text.StartsWith("using ") || text.StartsWith("public ") || text.StartsWith("private ");
    }
}
