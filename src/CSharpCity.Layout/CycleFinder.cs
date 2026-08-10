using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Tarjan's strongly-connected components over the type-reference graph. Any component with more
/// than one member is a set of types that can all reach each other — a circular dependency.
/// </summary>
internal static class CycleFinder
{
    /// <remarks>
    /// Iterative rather than recursive: a deep reference chain in a large solution would otherwise
    /// recurse once per type and blow the stack.
    /// </remarks>
    public static List<List<string>> Find(List<DependencyEdge> edges, IEnumerable<string> nodes)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes) adjacency[node] = new List<string>();
        foreach (var edge in edges) adjacency[edge.FromId].Add(edge.ToId);

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var components = new List<List<string>>();
        int nextIndex = 0;

        // Explicit work stack: each frame is a node plus how far through its neighbours we got.
        var work = new Stack<(string Node, int Next)>();

        foreach (var root in adjacency.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (index.ContainsKey(root)) continue;

            work.Push((root, 0));
            while (work.Count > 0)
            {
                var (node, next) = work.Pop();

                if (next == 0)
                {
                    index[node] = lowLink[node] = nextIndex++;
                    stack.Push(node);
                    onStack.Add(node);
                }

                var neighbours = adjacency[node];
                bool descended = false;

                while (next < neighbours.Count)
                {
                    var neighbour = neighbours[next++];
                    if (!index.ContainsKey(neighbour))
                    {
                        work.Push((node, next));      // resume here once the child finishes
                        work.Push((neighbour, 0));
                        descended = true;
                        break;
                    }
                    if (onStack.Contains(neighbour))
                        lowLink[node] = Math.Min(lowLink[node], index[neighbour]);
                }

                if (descended) continue;

                if (lowLink[node] == index[node])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    } while (!string.Equals(member, node, StringComparison.Ordinal));

                    if (component.Count > 1) components.Add(component);
                }

                // Fold this node's low-link into its parent, which sits directly below on the stack.
                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                }
            }
        }

        return components;
    }
}
