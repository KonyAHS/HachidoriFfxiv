namespace HachidoriFfxiv.ArchCheck;

/// <summary>
/// Directed-graph helpers: Tarjan strongly connected components and shortest
/// representative cycle extraction for diagnostics.
/// </summary>
internal static class Graph
{
    public static List<List<int>> StronglyConnectedComponents(IReadOnlyList<IReadOnlyList<int>> adjacency)
    {
        var count = adjacency.Count;
        var index = 0;
        var indices = new int[count];
        Array.Fill(indices, -1);
        var lowlink = new int[count];
        var onStack = new bool[count];
        var stack = new Stack<int>();
        var result = new List<List<int>>();

        void StrongConnect(int v)
        {
            indices[v] = index;
            lowlink[v] = index;
            index++;
            stack.Push(v);
            onStack[v] = true;

            foreach (var w in adjacency[v])
            {
                if (indices[w] == -1)
                {
                    StrongConnect(w);
                    lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                }
                else if (onStack[w])
                {
                    lowlink[v] = Math.Min(lowlink[v], indices[w]);
                }
            }

            if (lowlink[v] != indices[v])
            {
                return;
            }

            var component = new List<int>();
            while (true)
            {
                var w = stack.Pop();
                onStack[w] = false;
                component.Add(w);
                if (w == v)
                {
                    break;
                }
            }

            if (component.Count >= 2)
            {
                component.Sort();
                result.Add(component);
            }
        }

        for (var v = 0; v < count; v++)
        {
            if (indices[v] == -1)
            {
                StrongConnect(v);
            }
        }

        return result;
    }
}
