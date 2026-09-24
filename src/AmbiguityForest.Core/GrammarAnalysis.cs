namespace AmbiguityForest.Core;

/// <summary>
/// Nullable computation and recursion analysis. Both use explicit, bounded
/// termination strategies; every judgment is emitted as a machine-readable event.
/// </summary>
public static class GrammarAnalysis
{
    public static HashSet<string> Nonterminals(Grammar g) =>
        g.Productions.Select(p => p.Lhs).ToHashSet();

    /// <summary>
    /// Fixed-point nullable computation. Termination strategy: the set grows
    /// monotonically and is bounded by the nonterminal count, so at most
    /// |NT|+1 iterations run. Nullable cycles are detected afterwards on the
    /// unit-production graph (SCC with a cycle) and reported, not silently
    /// unfolded.
    /// </summary>
    public static (HashSet<string> Nullable, List<PendingEvent> Events) ComputeNullable(Grammar g)
    {
        var events = new List<PendingEvent>();
        var nullable = new HashSet<string>();
        var nonterminals = Nonterminals(g);
        var terminals = g.Terminals.ToHashSet();
        int iterations = 0;
        bool changed = true;
        while (changed)
        {
            if (iterations > nonterminals.Count + 1)
                throw new InvalidOperationException("nullable fixed-point bound exceeded");
            changed = false;
            iterations++;
            foreach (var p in g.Productions)
            {
                if (nullable.Contains(p.Lhs)) continue;
                if (p.Rhs.All(s => !terminals.Contains(s) && nullable.Contains(s)))
                {
                    nullable.Add(p.Lhs);
                    changed = true;
                }
            }
        }

        // Nullable cycle detection on the unit-production graph restricted to nullable NTs.
        var unitEdges = g.Productions
            .Where(p => p.Rhs.Length == 1 && nonterminals.Contains(p.Rhs[0]))
            .GroupBy(p => p.Lhs)
            .ToDictionary(gr => gr.Key, gr => gr.Select(p => p.Rhs[0]).ToList());
        foreach (var cycle in FindCycles(nullable, unitEdges))
        {
            events.Add(new PendingEvent("nullable-cycle", new()
            {
                ["members"] = cycle,
                ["terminated"] = true,
                ["strategy"] = "fixed-point-bound:|NT|+1-iterations; cycle reported as SCC on unit-production graph; forest extraction emits cycle node instead of unfolding"
            }));
        }

        events.Add(new PendingEvent("nullable-computed", new()
        {
            ["nullable"] = nullable.OrderBy(x => x).ToArray(),
            ["iterations"] = iterations,
            ["bound"] = nonterminals.Count + 1
        }));
        return (nullable, events);
    }

    /// <summary>
    /// Left-recursion detection via the left-corner graph (A -> B when some
    /// production A => B... has only nullable symbols before B). Termination:
    /// reachability over a finite graph. Parsing left recursion is safe because
    /// the Earley chart is a finite fixed-point and forest extraction only
    /// recurses on strictly smaller spans (zero-width recursion hits the cycle guard).
    /// </summary>
    public static List<PendingEvent> DetectLeftRecursion(Grammar g, HashSet<string> nullable)
    {
        var events = new List<PendingEvent>();
        var nonterminals = Nonterminals(g);
        var leftCorner = nonterminals.ToDictionary(x => x, _ => new HashSet<string>());
        foreach (var p in g.Productions)
        {
            foreach (var s in p.Rhs)
            {
                if (!nonterminals.Contains(s)) break;
                leftCorner[p.Lhs].Add(s);
                if (!nullable.Contains(s)) break;
            }
        }
        var cyclic = new List<string>();
        foreach (var nt in nonterminals)
        {
            // BFS from nt; left-recursive iff nt reaches itself.
            var seen = new HashSet<string>();
            var queue = new Queue<string>(leftCorner[nt]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (cur == nt) { cyclic.Add(nt); break; }
                if (!seen.Add(cur)) continue;
                foreach (var next in leftCorner[cur]) queue.Enqueue(next);
            }
        }
        if (cyclic.Count > 0)
        {
            events.Add(new PendingEvent("left-recursion", new()
            {
                ["nonterminals"] = cyclic.OrderBy(x => x).ToArray(),
                ["terminated"] = true,
                ["strategy"] = "chart fixed-point over finite state set; forest extraction recurses only on strictly smaller spans; zero-width recursion terminates via on-stack cycle guard"
            }));
        }
        return events;
    }

    private static IEnumerable<string[]> FindCycles(HashSet<string> candidates, Dictionary<string, List<string>> edges)
    {
        // Tarjan SCC, iterative.
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<string>();
        int counter = 0;
        foreach (var start in candidates.OrderBy(x => x))
        {
            if (index.ContainsKey(start)) continue;
            var work = new Stack<(string Node, int Child)>();
            work.Push((start, 0));
            while (work.Count > 0)
            {
                var (node, child) = work.Peek();
                if (child == 0)
                {
                    index[node] = low[node] = counter++;
                    stack.Push(node);
                    onStack.Add(node);
                }
                var succ = edges.TryGetValue(node, out var list) ? list : new List<string>();
                if (child < succ.Count)
                {
                    work.Pop(); work.Push((node, child + 1));
                    var next = succ[child];
                    if (!candidates.Contains(next)) continue;
                    if (!index.ContainsKey(next)) work.Push((next, 0));
                    else if (onStack.Contains(next)) low[node] = Math.Min(low[node], index[next]);
                }
                else
                {
                    work.Pop();
                    if (work.Count > 0)
                    {
                        var parent = work.Peek().Node;
                        low[parent] = Math.Min(low[parent], low[node]);
                    }
                    if (low[node] == index[node])
                    {
                        var scc = new List<string>();
                        string w;
                        do { w = stack.Pop(); onStack.Remove(w); scc.Add(w); } while (w != node);
                        bool isCycle = scc.Count > 1 ||
                            (edges.TryGetValue(node, out var self) && self.Contains(node));
                        if (isCycle) yield return scc.OrderBy(x => x).ToArray();
                    }
                }
            }
        }
    }
}
