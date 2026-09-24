namespace ForestRoom.Core;

/// <summary>nullable 关系分析。显式终止策略：不动点迭代 + 轮次上限，
/// 每轮只加入新事实；nullable cycle（A=>B, B=>A, 均不可空）不会导致发散，
/// 因为集合单调增长且有界（≤ 非终结符数）。cycle 被显式记录。</summary>
public sealed class NullableAnalysis
{
    public HashSet<string> NullableNonTerminals { get; } = new();
    public List<(string ProductionId, string Reason)> NullableProductions { get; } = new();
    public List<List<string>> DetectedCycles { get; } = new();
    public int Rounds { get; private set; }

    public static NullableAnalysis Compute(GrammarVersion grammar, List<ForestEvent>? events = null)
    {
        var analysis = new NullableAnalysis();
        var maxRounds = grammar.NonTerminals.Count + 1; // 显式上界
        var changed = true;
        while (changed && analysis.Rounds < maxRounds)
        {
            changed = false;
            analysis.Rounds++;
            foreach (var p in grammar.Productions)
            {
                if (analysis.NullableNonTerminals.Contains(p.Lhs)) continue;
                if (p.Rhs.All(s => grammar.NonTerminals.Contains(s) == false || analysis.NullableNonTerminals.Contains(s))
                    && p.Rhs.All(s => !grammar.NonTerminals.Contains(s) || analysis.NullableNonTerminals.Contains(s)))
                {
                    // 终结符不可空；仅当 RHS 全为非终结符且均可空，或 RHS 为空
                    if (p.Rhs.Any(s => !grammar.NonTerminals.Contains(s))) continue;
                    analysis.NullableNonTerminals.Add(p.Lhs);
                    analysis.NullableProductions.Add((p.Id,
                        p.Rhs.Count == 0 ? "empty-rhs" : "all-rhs-nullable"));
                    changed = true;
                }
            }
        }
        analysis.DetectCycles(grammar, events);
        return analysis;
    }

    private void DetectCycles(GrammarVersion grammar, List<ForestEvent>? events)
    {
        // 在"仅由可空非终结符组成的一元/可空推导"图上找环：A ∈ nullable 且 A =>+ A
        var edges = new Dictionary<string, List<string>>();
        foreach (var p in grammar.Productions)
        {
            if (p.Rhs.Count == 1 && grammar.NonTerminals.Contains(p.Rhs[0]))
            {
                if (!edges.TryGetValue(p.Lhs, out var list)) edges[p.Lhs] = list = new();
                list.Add(p.Rhs[0]);
            }
        }
        var seen = new HashSet<string>();
        foreach (var start in edges.Keys)
        {
            var path = new List<string>();
            var visiting = new HashSet<string>();
            var stack = new Stack<(string node, IEnumerator<string> it)>();
            if (visiting.Contains(start) || seen.Contains(start)) continue;
            // 简单 DFS 找环
            var onPath = new HashSet<string>();
            Dfs(start, edges, onPath, seen, path, DetectedCycles);
        }
        foreach (var cycle in DetectedCycles)
        {
            events?.Add(ForestEvent.Of(0, EventKinds.NullableCycleTerminated,
                string.Join("->", cycle),
                $"nullable cycle detected among [{string.Join(", ", cycle)}]; terminated by monotone fixpoint bound",
                ("strategy", "monotone-fixpoint"), ("bound", "nonterminal-count")));
        }
    }

    private static void Dfs(string node, Dictionary<string, List<string>> edges,
        HashSet<string> onPath, HashSet<string> seen, List<string> path, List<List<string>> cycles)
    {
        if (onPath.Contains(node))
        {
            var idx = path.IndexOf(node);
            cycles.Add(path.Skip(idx).Concat(new[] { node }).ToList());
            return;
        }
        if (!edges.ContainsKey(node) || seen.Contains(node)) return;
        onPath.Add(node);
        path.Add(node);
        foreach (var next in edges[node]) Dfs(next, edges, onPath, seen, path, cycles);
        path.RemoveAt(path.Count - 1);
        onPath.Remove(node);
        seen.Add(node);
    }

    public bool IsNullable(string symbol, GrammarVersion grammar) =>
        grammar.NonTerminals.Contains(symbol) && NullableNonTerminals.Contains(symbol);
}
