#nullable enable
namespace AmbiguityForestLab.Core;

public sealed record NullableEdge(string From, string To, string ProductionId, int RhsIndex);

public sealed class NullableAnalysis
{
    public required IReadOnlySet<string> Nullable { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Reasons { get; init; }
    public required IReadOnlyList<IReadOnlyList<NullableEdge>> NullableCycles { get; init; }
    public required IReadOnlySet<string> LeftRecursive { get; init; }
    public required IReadOnlyList<IReadOnlyList<NullableEdge>> LeftRecursionCycles { get; init; }
    public required IReadOnlyList<NullableEdge> FirstEdges { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> DuplicateStructures { get; init; }

    public bool IsNullable(string symbol) => Nullable.Contains(symbol);
}

public static class GrammarAnalysis
{
    public static NullableAnalysis Analyze(Grammar g)
    {
        var nt = g.Productions.Select(p => p.Lhs).ToHashSet();
        bool IsNt(string s) => nt.Contains(s);

        var nullable = new HashSet<string>();
        var reasons = new Dictionary<string, List<string>>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in g.Productions)
            {
                if (nullable.Contains(p.Lhs)) continue;
                if (p.Rhs.All(s => !IsNt(s) ? false : nullable.Contains(s)))
                {
                    if (p.Rhs.Count == 0 || p.Rhs.All(s => nullable.Contains(s)))
                    {
                        nullable.Add(p.Lhs);
                        reasons.GetOrAdd(p.Lhs).Add(p.Id);
                        changed = true;
                    }
                }
            }
        }

        var nullEdges = new List<NullableEdge>();
        foreach (var p in g.Productions)
        {
            if (p.Rhs.Count > 0 && p.Rhs.All(s => nullable.Contains(s)))
            {
                for (var i = 0; i < p.Rhs.Count; i++)
                {
                    if (IsNt(p.Rhs[i]))
                        nullEdges.Add(new NullableEdge(p.Lhs, p.Rhs[i], p.Id, i));
                }
            }
        }
        var nullableCycles = FindCycles(nullEdges, nt);

        var firstEdges = new List<NullableEdge>();
        foreach (var p in g.Productions)
        {
            for (var i = 0; i < p.Rhs.Count; i++)
            {
                var prefixNullable = p.Rhs.Take(i).All(s => nullable.Contains(s));
                if (!prefixNullable) break;
                if (IsNt(p.Rhs[i]))
                    firstEdges.Add(new NullableEdge(p.Lhs, p.Rhs[i], p.Id, i));
            }
        }
        var lrCycles = FindCycles(firstEdges, nt);
        var lr = lrCycles.SelectMany(c => c.Select(e => e.From)).ToHashSet();
        foreach (var c in lrCycles)
        {
            foreach (var e in c) { lr.Add(e.From); lr.Add(e.To); }
        }

        var duplicates = g.Productions
            .GroupBy(g.StructureKey)
            .Where(grp => grp.Select(p => p.Id).Distinct().Count() > 1)
            .ToDictionary(grp => grp.Key, grp => (IReadOnlyList<string>)grp.Select(p => p.Id).ToList());

        return new NullableAnalysis
        {
            Nullable = nullable,
            Reasons = reasons.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            NullableCycles = nullableCycles,
            LeftRecursive = lr,
            LeftRecursionCycles = lrCycles,
            FirstEdges = firstEdges,
            DuplicateStructures = duplicates
        };
    }

    internal static List<IReadOnlyList<NullableEdge>> FindCycles(IReadOnlyList<NullableEdge> edges, HashSet<string> nodes)
    {
        var adj = nodes.ToDictionary(n => n, _ => new List<NullableEdge>());
        foreach (var e in edges)
            if (adj.TryGetValue(e.From, out var list)) list.Add(e);

        var result = new List<IReadOnlyList<NullableEdge>>();
        var seenSignatures = new HashSet<string>();
        var stack = new List<NullableEdge>();
        var onStack = new HashSet<string>();
        var color = nodes.ToDictionary(n => n, _ => 0);

        void Dfs(string u)
        {
            color[u] = 1;
            onStack.Add(u);
            foreach (var e in adj[u])
            {
                if (!color.TryGetValue(e.To, out var c)) continue;
                if (c == 0)
                {
                    stack.Add(e);
                    Dfs(e.To);
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (c == 1 && onStack.Contains(e.To))
                {
                    var start = stack.FindIndex(x => x.From == e.To);
                    var cyc = start >= 0 ? stack.Skip(start).ToList() : new List<NullableEdge> { e };
                    var sig = string.Join("|", cyc.Select(x => x.ProductionId + ":" + x.RhsIndex).OrderBy(x => x));
                    if (seenSignatures.Add(sig)) result.Add(cyc);
                }
            }
            onStack.Remove(u);
            color[u] = 2;
        }

        foreach (var n in nodes.OrderBy(x => x))
            if (color[n] == 0) Dfs(n);
        return result;
    }
}

internal static class CollectionExtensions
{
    public static List<T> GetOrAdd<T>(this Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<T>();
            map[key] = list;
        }
        return list;
    }
}
