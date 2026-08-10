namespace CSharpCity.Layout;

/// <summary>
/// Recursive binary-split treemap. Each split cuts along the rectangle's longer axis at the point
/// nearest to half the total weight, which keeps blocks close to square without the bookkeeping of
/// a full squarified treemap — and stays perfectly deterministic, which matters more here: the same
/// solution must produce the same city every run so you can build a mental map of it.
/// </summary>
public static class Treemap
{
    /// <summary>
    /// The line along which one region was divided — the shared boundary between two neighbours,
    /// and therefore the centreline of the street between them.
    /// </summary>
    /// <param name="Vertical">True when the line runs along Z, dividing the region in X.</param>
    /// <param name="Position">X of a vertical cut, Z of a horizontal one.</param>
    public readonly record struct Cut(bool Vertical, float Position, float SpanStart, float SpanEnd);

    /// <remarks>
    /// <paramref name="onCut"/> reports every division exactly once. That's what makes a shared
    /// street network possible: each cut is one road serving both sides, rather than each block
    /// drawing its own ring and leaving two parallel roads with a seam between them.
    /// </remarks>
    public static void Layout<T>(IReadOnlyList<(T Item, float Weight)> items, Bounds2 bounds,
        Action<T, Bounds2> emit, Action<Cut>? onCut = null)
    {
        if (items.Count == 0) return;
        if (items.Count == 1)
        {
            emit(items[0].Item, bounds);
            return;
        }

        float total = 0f;
        foreach (var (_, weight) in items) total += MathF.Max(weight, 0.0001f);

        // Walk until we've accumulated half the weight; that's the split point.
        float accumulated = 0f;
        int split = 1;
        for (int i = 0; i < items.Count - 1; i++)
        {
            accumulated += MathF.Max(items[i].Weight, 0.0001f);
            split = i + 1;
            if (accumulated >= total * 0.5f) break;
        }

        float fraction = Math.Clamp(accumulated / total, 0.05f, 0.95f);

        Bounds2 first, second;
        if (bounds.Width >= bounds.Depth)
        {
            float x = bounds.X + bounds.Width * fraction;
            first = new Bounds2(bounds.X, bounds.Z, bounds.Width * fraction, bounds.Depth);
            second = new Bounds2(x, bounds.Z, bounds.Width * (1 - fraction), bounds.Depth);
            onCut?.Invoke(new Cut(true, x, bounds.Z, bounds.Z + bounds.Depth));
        }
        else
        {
            float z = bounds.Z + bounds.Depth * fraction;
            first = new Bounds2(bounds.X, bounds.Z, bounds.Width, bounds.Depth * fraction);
            second = new Bounds2(bounds.X, z, bounds.Width, bounds.Depth * (1 - fraction));
            onCut?.Invoke(new Cut(false, z, bounds.X, bounds.X + bounds.Width));
        }

        Layout(items.Take(split).ToList(), first, emit, onCut);
        Layout(items.Skip(split).ToList(), second, emit, onCut);
    }
}

/// <summary>A node in the namespace trie: nested namespaces become nested city blocks.</summary>
public sealed class NamespaceNode
{
    public string Segment { get; init; } = "";
    /// <summary>
    /// What the street sign says. After <see cref="Collapse"/> this is the whole folded chain
    /// ("Foo.Bar.Baz"), because the intermediate levels no longer exist as blocks of their own.
    /// </summary>
    public string DisplayName { get; init; } = "";
    public Dictionary<string, NamespaceNode> Children { get; } = new(StringComparer.Ordinal);
    public List<(string Id, float Weight)> Leaves { get; } = new();
    public float Weight { get; set; }

    public NamespaceNode Descend(string segment)
    {
        if (!Children.TryGetValue(segment, out var child))
            Children[segment] = child = new NamespaceNode { Segment = segment };
        return child;
    }

    /// <summary>Sums leaf weights up the trie. Call once after all leaves are added.</summary>
    public float Accumulate()
    {
        Weight = Leaves.Sum(l => l.Weight) + Children.Values.Sum(c => c.Accumulate());
        return Weight;
    }

    /// <summary>
    /// Folds away pass-through levels — a namespace with no types of its own and a single
    /// sub-namespace. Without this, a solution where everything lives under one root namespace
    /// spends a full street's worth of margin on every shared prefix and the blocks starve.
    /// </summary>
    public NamespaceNode Collapse()
    {
        var node = this;
        var folded = new List<string>();
        if (node.Segment.Length > 0) folded.Add(node.Segment);

        while (node.Leaves.Count == 0 && node.Children.Count == 1)
        {
            node = node.Children.Values.First();
            if (node.Segment.Length > 0) folded.Add(node.Segment);
        }

        var collapsed = new NamespaceNode
        {
            Segment = node.Segment,
            DisplayName = string.Join('.', folded),
            Weight = node.Weight,
        };
        collapsed.Leaves.AddRange(node.Leaves);
        foreach (var child in node.Children.Values)
        {
            var collapsedChild = child.Collapse();
            // Keyed by the folded chain, not the final segment. Two siblings can fold to the same
            // last segment ("A.Core.Common" and "B.Util.Common" both end in "Common"), and keying on
            // that silently overwrites one subtree — losing every type inside it.
            collapsed.Children[collapsedChild.DisplayName] = collapsedChild;
        }
        return collapsed;
    }
}
