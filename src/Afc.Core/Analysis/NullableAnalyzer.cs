using Afc.Core.Model;

namespace Afc.Core.Analysis;

public sealed record NullableResult(
    IReadOnlySet<string> Nullable,
    IReadOnlyDictionary<string, string> WitnessProduction,
    IReadOnlyList<AnalyzerEvent> Events);

/// <summary>
/// 最小不动点 nullable 分析，保留 witness 产生式（显式终止策略的依据）：
/// - 平凡终止：A ::= ε
/// - 合成终止：A ::= X1..Xn 且每个 Xi 均可空
/// - nullable cycle 裁剪：在“剩余依赖图”上做 SCC。任何自环或多节点 SCC 都意味着
///   某符号只能经由自身达到可空；这些符号保持不可空并发出 NullableCycleCut 事件，
///   绝不无限展开。原文法证据（产生式）原样保留。
/// </summary>
public static class NullableAnalyzer
{
    public static NullableResult Analyze(GrammarDoc grammar, Provenance provenance, long seqStart = 0)
    {
        var nullable = new HashSet<string>();
        var witness = new Dictionary<string, string>();
        var events = new List<AnalyzerEvent>();
        var seq = seqStart;

        bool Changed()
        {
            foreach (var p in grammar.Productions)
            {
                if (nullable.Contains(p.Lhs)) continue;
                if (p.Rhs.All(s => !IsTerminal(s) && nullable.Contains(s)))
                {
                    nullable.Add(p.Lhs);
                    witness[p.Lhs] = p.Id;
                    events.Add(new AnalyzerEvent(
                        $"ev-null-{p.Lhs}-{p.Id}", seq++, EventKind.NullableFact, EventLevel.Info,
                        "NULLABLE-WITNESS",
                        $"{p.Lhs} 可空：witness 产生式 {p.Id}（{p.Display}）",
                        new[] { $"symbol={p.Lhs}", "nullable=true", $"witness={p.Id}", "adopted=true" },
                        new[] { $"sym:{p.Lhs}", $"prod:{p.Id}" },
                        provenance, null));
                    return true;
                }
            }
            return false;
        }

        while (Changed()) { }

        var remaining = grammar.Productions.Select(p => p.Lhs)
            .Where(nt => !nullable.Contains(nt)).Distinct().ToList();
        var edges = new Dictionary<string, List<(string to, string prod)>>();
        foreach (var nt in remaining) edges[nt] = new List<(string, string)>();
        foreach (var p in grammar.Productions)
        {
            if (nullable.Contains(p.Lhs)) continue;
            foreach (var s in p.Rhs)
            {
                if (IsTerminal(s) || nullable.Contains(s)) continue;
                if (edges.ContainsKey(s)) edges[p.Lhs].Add((s, p.Id));
            }
        }

        var sccs = Tarjan(remaining, edges);
        foreach (var scc in sccs)
        {
            var cyclic = scc.Count > 1 || edges[scc[0]].Any(e => e.to == scc[0]);
            if (!cyclic) continue;
            var involved = scc.OrderBy(x => x).ToList();
            var prods = scc.SelectMany(a => edges[a].Where(e => scc.Contains(e.to)))
                .Select(e => e.prod).Distinct().OrderBy(x => x).ToList();
            events.Add(new AnalyzerEvent(
                $"ev-nullcycle-{string.Join("-", involved)}", seq++,
                EventKind.NullableCycleCut, EventLevel.Warning,
                "NULLABLE-CYCLE-CUT",
                $"nullable cycle 裁剪：[{string.Join(", ", involved)}] 只有经由自身的 ε 路径，保持不可空",
                new[]
                {
                    $"scc=[{string.Join(",", involved)}]", "nullable=false",
                    "termination=least-fixpoint+scc-cut", "grammar-evidence=preserved", "adopted=true"
                },
                involved.Select(i => $"sym:{i}").Concat(prods.Select(p => $"prod:{p}")).ToList(),
                provenance, null));
        }

        return new NullableResult(nullable, witness, events);
    }

    internal static bool IsTerminal(string sym) => sym.StartsWith('"') || sym.StartsWith('$');

    private static List<List<string>> Tarjan(
        List<string> nodes, Dictionary<string, List<(string nt, string prod)>> edges)
    {
        var index = 0;
        var indices = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<string>();
        var result = new List<List<string>>();

        void StrongConnect(string v)
        {
            indices[v] = low[v] = index++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var (w, _) in edges.GetValueOrDefault(v, new()))
            {
                if (!edges.ContainsKey(w)) continue;
                if (!indices.ContainsKey(w)) { StrongConnect(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], indices[w]);
            }
            if (low[v] == indices[v])
            {
                var scc = new List<string>();
                while (true)
                {
                    var w = stack.Pop();
                    onStack.Remove(w);
                    scc.Add(w);
                    if (w == v) break;
                }
                result.Add(scc);
            }
        }

        foreach (var n in nodes) if (!indices.ContainsKey(n)) StrongConnect(n);
        return result;
    }
}
