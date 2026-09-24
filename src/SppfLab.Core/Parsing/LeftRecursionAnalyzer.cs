using System.Collections.Generic;
using System.Linq;
using SppfLab.Core.Diagnostics;
using SppfLab.Core.Models;

namespace SppfLab.Core.Parsing;

/// <summary>
/// Detects left recursion through the "nullable first" relation:
/// edge A→B exists when some A production has B after an all-nullable
/// prefix. A reaches itself iff it is left recursive (direct or indirect).
/// Termination: bounded reachability DFS over a finite nonterminal set.
/// The Earley work-list is the runtime termination strategy; this analysis
/// just records the evidence for the UI.
/// </summary>
public static class LeftRecursionAnalyzer
{
    public sealed class Result
    {
        public required IReadOnlySet<string> LeftRecursive { get; init; }
        public required IReadOnlyList<List<string>> Cycles { get; init; }
        public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    }

    public static Result Analyze(Grammar grammar, NullableAnalysis nullable)
    {
        var edges = new Dictionary<string, List<(string Target, string ViaProduction)>>();

        foreach (var production in grammar.Productions)
        {
            foreach (var symbol in production.RHS)
            {
                if (symbol.IsTerminal)
                    break; // a terminal blocks the nullable prefix
                if (!nullable.IsNullable(symbol.Name) && symbol.Name != production.Nonterminal)
                {
                    // First non-nullable nonterminal is reachable; then stop.
                    Add(edges, production.Nonterminal, (symbol.Name, production.Id));
                    break;
                }
                Add(edges, production.Nonterminal, (symbol.Name, production.Id));
                if (!nullable.IsNullable(symbol.Name))
                    break;
            }
        }

        var recursive = new HashSet<string>();
        var cycles = new List<List<string>>();
        var diagnostics = new List<Diagnostic>();

        foreach (var start in grammar.Productions.Select(p => p.Nonterminal).Distinct().OrderBy(x => x))
        {
            // DFS for a path back to start; bound depth by number of nodes.
            var path = new List<string> { start };
            var onPath = new HashSet<string> { start };
            Dfs(start, start, edges, path, onPath, recursive, cycles, diagnostics);
        }

        return new Result
        {
            LeftRecursive = recursive,
            Cycles = cycles,
            Diagnostics = diagnostics
        };
    }

    private static void Add(
        Dictionary<string, List<(string, string)>> edges,
        string from, (string Target, string Prod) edge)
    {
        if (!edges.TryGetValue(from, out var list))
            edges[from] = list = new List<(string, string)>();
        if (!list.Any(e => e.Item1 == edge.Target && e.Item2 == edge.Prod))
            list.Add(edge);
    }

    private static void Dfs(
        string start, string current,
        Dictionary<string, List<(string Target, string ViaProduction)>> edges,
        List<string> path, HashSet<string> onPath,
        HashSet<string> recursive, List<List<string>> cycles,
        List<Diagnostic> diagnostics)
    {
        if (path.Count > edges.Count + 2)
            return; // explicit bound
        if (!edges.TryGetValue(current, out var nexts))
            return;

        foreach (var (target, via) in nexts)
        {
            if (target == start && path.Count >= 1)
            {
                var cycle = new List<string>(path);
                recursive.UnionWith(cycle);
                var key = string.Join(",", cycle.OrderBy(x => x)) + "|" + via;
                if (!cycles.Any(c => string.Join(",", c) == string.Join(",", cycle)))
                {
                    cycles.Add(cycle);
                    diagnostics.Add(new Diagnostic(
                        DiagKind.LeftRecursionDetected, DiagSeverity.Warning,
                        "Left recursion: " + string.Join(" -> ", cycle) + " -> " + start,
                        productionId: via,
                        data: new Dictionary<string, string>
                        {
                            ["cycle"] = string.Join(",", cycle),
                            ["viaProduction"] = via,
                            ["termination"] = "earley-worklist-fixpoint"
                        }));
                }
                continue;
            }
            if (onPath.Contains(target))
                continue;
            onPath.Add(target);
            path.Add(target);
            Dfs(start, target, edges, path, onPath, recursive, cycles, diagnostics);
            path.RemoveAt(path.Count - 1);
            onPath.Remove(target);
        }
    }
}
