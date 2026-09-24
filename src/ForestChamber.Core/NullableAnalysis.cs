namespace ForestChamber.Core;

public sealed record NullableReport(
    IReadOnlySet<string> NullableNonterminals,
    IReadOnlyList<(string From, string To, string ProdId)> Edges,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    int Iterations,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record LeftRecursionReport(
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class GrammarAnalysis
{
    // Explicit fixed-point termination: iterate at most |N|+1 rounds; if membership
    // still moves, that is a violation surfaced as a diagnostic rather than a hang.
    public static NullableReport AnalyzeNullable(GrammarDoc grammar)
    {
        var nullable = new HashSet<string>();
        var diagnostics = new List<Diagnostic>();
        var edges = new List<(string, string, string)>();
        var limit = grammar.Nonterminals.Count + 1;
        var iterations = 0;
        var changed = true;

        while (changed && iterations < limit)
        {
            iterations++;
            changed = false;
            foreach (var prod in grammar.Productions)
            {
                if (nullable.Contains(prod.Lhs)) continue;
                var allNullable = prod.Rhs.All(s => s is TerminalSym t ? t.Name == "EPS" : nullable.Contains(((NonterminalSym)s).Name));
                if (allNullable)
                {
                    nullable.Add(prod.Lhs);
                    changed = true;
                    foreach (var s in prod.Rhs.OfType<NonterminalSym>())
                        edges.Add((prod.Lhs, s.Name, prod.ProdId));
                }
            }
        }

        if (changed)
            diagnostics.Add(new Diagnostic("N200", "error", "nullable fixed-point failed to terminate within bound", null,
                new Dictionary<string, string> { ["strategy"] = "bounded-fixed-point", ["bound"] = limit.ToString() }));

        var dependencyGraph = new Dictionary<string, List<string>>();
        foreach (var (from, to, prodId) in edges)
        {
            dependencyGraph.TryAdd(from, new List<string>());
            dependencyGraph[from].Add(to);
        }

        var cycles = FindCycles(dependencyGraph);
        foreach (var cyc in cycles)
        {
            var inNullable = cyc.All(nullable.Contains);
            diagnostics.Add(new Diagnostic(inNullable ? "N100" : "N101", inNullable ? "warning" : "info",
                (inNullable ? "nullable " : "non-nullable ") + "cycle: " + string.Join(" -> ", cyc) + " -> " + cyc[0],
                null, new Dictionary<string, string>
                {
                    ["strategy"] = "worklist-dedup",
                    ["cycle"] = string.Join(">", cyc),
                    ["nullable"] = inNullable.ToString().ToLowerInvariant(),
                    ["unit"] = "nonterminal-edge"
                }));
        }

        return new NullableReport(nullable, edges, cycles, iterations, diagnostics);
    }

    public static LeftRecursionReport AnalyzeLeftRecursion(GrammarDoc grammar)
    {
        // Graph: A -> B when B starts a production body of A (after nullable prefix
        // symbols, because those can derive epsilon).
        var nullable = AnalyzeNullable(grammar).NullableNonterminals;
        var graph = new Dictionary<string, List<string>>();
        foreach (var prod in grammar.Productions)
        {
            graph.TryAdd(prod.Lhs, new List<string>());
            foreach (var s in prod.Rhs)
            {
                if (s is TerminalSym) break;
                var nt = ((NonterminalSym)s).Name;
                graph[prod.Lhs].Add(nt);
                if (!nullable.Contains(nt)) break;
            }
        }
        var cycles = FindCycles(graph);
        var diagnostics = cycles.Select(cyc => new Diagnostic("L100", "info",
            "left-recursive cycle handled with explicit worklist termination: " + string.Join(" -> ", cyc) + " -> " + cyc[0],
            null, new Dictionary<string, string>
            {
                ["strategy"] = "earley-column-worklist-dedup",
                ["cycle"] = string.Join(">", cyc),
                ["unit"] = "nonterminal-edge"
            })).ToList();
        return new LeftRecursionReport(cycles, diagnostics);
    }

    private static List<List<string>> FindCycles(Dictionary<string, List<string>> graph)
    {
        var result = new List<List<string>>();
        var seenSignatures = new HashSet<string>();
        var state = new Dictionary<string, int>();
        var stack = new List<string>();

        void Dfs(string node)
        {
            state[node] = 1;
            stack.Add(node);
            if (graph.TryGetValue(node, out var nexts))
            {
                foreach (var next in nexts.Distinct())
                {
                    if (!state.TryGetValue(next, out var st)) Dfs(next);
                    else if (st == 1)
                    {
                        var idx = stack.IndexOf(next);
                        var cyc = stack.Skip(idx).ToList();
                        var sig = string.Join(">", cyc.OrderBy(x => x, StringComparer.Ordinal)) + "|" + cyc.Count;
                        if (seenSignatures.Add(sig)) result.Add(cyc);
                    }
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var n in graph.Keys.OrderBy(x => x, StringComparer.Ordinal))
            if (!state.ContainsKey(n)) Dfs(n);
        return result;
    }
}
