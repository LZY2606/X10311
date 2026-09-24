using Afc.Core.Model;

namespace Afc.Core.Analysis;

/// <summary>
/// 左递归静态分析（显式终止策略的静态部分）：
/// 以 left-corner 关系建图（前导可空符号被跳过），Tarjan 求 SCC。
/// SCC 大小 &gt; 1 或自环即为左递归；解析层另有 item 访问备忘保证停机。
/// 分析只产生证据，不改变文法。
/// </summary>
public static class LeftRecursionAnalyzer
{
    public static IReadOnlyList<AnalyzerEvent> Analyze(
        GrammarDoc grammar, NullableResult nullable, Provenance provenance, long seqStart = 0)
    {
        var nts = grammar.Productions.Select(p => p.Lhs).Distinct().ToList();
        var edges = new Dictionary<string, List<(string nt, string prod)>>();
        foreach (var n in nts) edges[n] = new List<(string, string)>();

        foreach (var p in grammar.Productions)
        {
            foreach (var sym in p.Rhs)
            {
                if (sym.StartsWith('"') || sym.StartsWith('$')) break; // 终结符阻断 left corner
                edges[p.Lhs].Add((sym, p.Id));
                if (!nullable.Nullable.Contains(sym)) break;
            }
        }

        var sccs = Tarjan(nts, edges);
        var events = new List<AnalyzerEvent>();
        var seq = seqStart;
        foreach (var scc in sccs)
        {
            if (scc.Count == 1)
            {
                var only = scc[0];
                if (!edges[only].Any(e => e.nt == only)) continue;
            }

            var kind = scc.Count == 1 ? "direct" : "indirect";
            var involved = scc.OrderBy(x => x).ToList();
            var prods = scc.SelectMany(a => edges[a].Where(e => scc.Contains(e.nt)))
                .Select(e => e.prod).Distinct().OrderBy(x => x).ToList();
            events.Add(new AnalyzerEvent(
                $"ev-leftrec-{string.Join("-", involved)}", seq,
                EventKind.LeftRecursion, EventLevel.Warning,
                "LEFT-RECURSION",
                $"{kind} 左递归：{string.Join(" -> ", involved)}（涉及 {string.Join(", ", prods)}）",
                new[]
                {
                    $"kind={kind}", "termination=earley-item-memoization",
                    $"scc=[{string.Join(",", involved)}]", "grammar-evidence=preserved", "adopted=true"
                },
                involved.Select(i => $"sym:{i}").Concat(prods.Select(p => $"prod:{p}")).ToList(),
                provenance, null));
            seq++;
        }
        return events;
    }

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
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], indices[w]);
                }
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
