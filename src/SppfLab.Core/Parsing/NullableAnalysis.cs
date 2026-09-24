using System.Collections.Generic;
using System.Linq;
using SppfLab.Core.Diagnostics;
using SppfLab.Core.Models;

namespace SppfLab.Core.Parsing;

/// <summary>Result of the nullable fixed-point computation, with cycle evidence.</summary>
public sealed class NullableAnalysis
{
    public NullableAnalysis(
        IReadOnlySet<string> nullable,
        IReadOnlyDictionary<string, IReadOnlyList<string>> witnesses,
        IReadOnlyList<List<string>> cycles,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Nullable = nullable;
        Witnesses = witnesses;
        Cycles = cycles;
        Diagnostics = diagnostics;
    }

    public IReadOnlySet<string> Nullable { get; }

    /// <summary>Nullable nonterminal -> one epsilon-producing production id witness.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Witnesses { get; }

    /// <summary>Cycles of nullable nonterminals (derives epsilon through each other).</summary>
    public IReadOnlyList<List<string>> Cycles { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool IsNullable(string symbol) => Nullable.Contains(symbol);
}

/// <summary>
/// Computes nullable nonterminals with an explicit monotone fixed point.
/// Termination strategy: the set only grows over a finite universe of
/// nonterminals, so iteration count is bounded by |N|; afterwards a DFG
/// search records every nullable cycle instead of looping on it.
/// </summary>
public static class NullableAnalyzer
{
    public static NullableAnalysis Analyze(Grammar grammar)
    {
        var nullable = new HashSet<string>();
        var witnesses = new Dictionary<string, List<string>>();

        // Monotone fixed point, bounded iterations.
        bool changed = true;
        int guard = 0;
        int universe = grammar.Productions.Select(p => p.Nonterminal).Distinct().Count();
        while (changed)
        {
            if (guard++ > universe + 1)
                throw new InvalidOperationException("nullable fixed point failed to terminate");
            changed = false;
            foreach (var production in grammar.Productions)
            {
                if (nullable.Contains(production.Nonterminal))
                    continue;
                if (production.RHS.All(s => s.IsTerminal == false && nullable.Contains(s.Name)))
                {
                    nullable.Add(production.Nonterminal);
                    changed = true;
                    if (!witnesses.TryGetValue(production.Nonterminal, out var list))
                        witnesses[production.Nonterminal] = list = new List<string>();
                    if (!list.Contains(production.Id))
                        list.Add(production.Id);
                }
            }
        }

        var cycles = FindNullableCycles(grammar, nullable);
        var diagnostics = new List<Diagnostic>();
        foreach (var cycle in cycles)
        {
            diagnostics.Add(new Diagnostic(
                DiagKind.NullableCycleDetected,
                DiagSeverity.Warning,
                "Nullable cycle: " + string.Join(" -> ", cycle) + " -> " + cycle[0],
                data: new Dictionary<string, string>
                {
                    ["cycle"] = string.Join(",", cycle),
                    ["termination"] = "monotone-fixedpoint+bounded-dfg"
                }));
        }

        return new NullableAnalysis(
            nullable,
            witnesses.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            cycles,
            diagnostics);
    }

    /// <summary>
    /// Enumerates simple cycles in the graph restricted to nullable
    /// nonterminals (edge A->B when some A production has B in RHS). Search
    /// depth is bounded by the number of nullable nonterminals.
    /// </summary>
    private static List<List<string>> FindNullableCycles(Grammar grammar, HashSet<string> nullable)
    {
        var edges = new Dictionary<string, List<string>>();
        foreach (var production in grammar.Productions)
        {
            if (!nullable.Contains(production.Nonterminal))
                continue;
            foreach (var symbol in production.RHS)
            {
                if (!symbol.IsTerminal && nullable.Contains(symbol.Name) && symbol.Name != production.Nonterminal)
                {
                    if (!edges.TryGetValue(production.Nonterminal, out var list))
                        edges[production.Nonterminal] = list = new List<string>();
                    if (!list.Contains(symbol.Name))
                        list.Add(symbol.Name);
                }
            }
        }

        var cycles = new List<List<string>>();
        var seen = new HashSet<string>();

        void Dfs(string start, string current, List<string> path, HashSet<string> onPath)
        {
            if (path.Count > nullable.Count + 1)
                return; // explicit cycle search bound
            if (!edges.TryGetValue(current, out var nexts))
                return;
            foreach (var next in nexts)
            {
                if (next == start && path.Count >= 1)
                {
                    var key = string.Join(",", path.OrderBy(x => x));
                    if (seen.Add(key))
                        cycles.Add(new List<string>(path));
                    continue;
                }
                if (onPath.Contains(next))
                    continue;
                onPath.Add(next);
                path.Add(next);
                Dfs(start, next, path, onPath);
                path.RemoveAt(path.Count - 1);
                onPath.Remove(next);
            }
        }

        foreach (var node in nullable.OrderBy(x => x))
            Dfs(node, node, new List<string> { node }, new HashSet<string> { node });

        return cycles;
    }
}
