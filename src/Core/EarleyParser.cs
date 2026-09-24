#nullable enable
using System.Text;

namespace AmbiguityForestLab.Core;

public sealed class ParseResult
{
    public required bool Success { get; init; }
    public required ForestGraph Forest { get; init; }
    public SppfNode? Root { get; init; }
    public required IReadOnlyList<string> ChartSummaries { get; init; }
    public bool EmptyInput { get; init; }
    public bool BudgetExhausted => Forest.BudgetExhausted;
    public string? BudgetError { get; init; }
    public required IReadOnlyList<string> NullableCycleEvents { get; init; }
    public int NullableRotations { get; init; }
}

internal sealed class ChartItem
{
    public required Production Production { get; init; }
    public required int Dot { get; init; }
    public required int Start { get; init; }
    public string? IntermediateNodeId { get; set; }
    public string? SymbolNodeId { get; set; }
    public string Key => $"{Production.Id}:{Dot}:{Start}";
    public bool IsComplete => Dot == Production.Rhs.Count;
}

public sealed class EarleyParser
{
    private readonly Grammar _grammar;
    private readonly TokenStream _tokens;
    private readonly NullableAnalysis _analysis;
    private readonly ForestGraph _forest;
    private readonly List<string> _cycleEvents = new();
    private readonly Dictionary<string, int> _rotationByWaiter = new();
    private int _nullableRotations;

    public int NullableCycleRotationLimit { get; init; } = 1;

    public EarleyParser(Grammar grammar, TokenStream tokens, NullableAnalysis analysis, ForestGraph forest)
    {
        _grammar = grammar;
        _tokens = tokens;
        _analysis = analysis;
        _forest = forest;
    }

    public ParseResult Parse()
    {
        var empty = _tokens.Tokens.Count == 0;
        var charts = Enumerable.Range(0, _tokens.Tokens.Count + 1)
            .Select(i => new ChartSet(i)).ToArray();
        var summaries = new List<string>();

        charts[0].Add(new ChartItem
        {
            Production = new Production("__seed__", "__root__", new[] { _grammar.Start }, -1, null, "seed", "seed"),
            Dot = 0,
            Start = 0
        });

        string? budgetError = null;
        try
        {
            for (var j = 0; j < charts.Length; j++)
            {
                CloseChart(charts, j);
                summaries.Add($"chart[{j}] items={charts[j].Items.Count} packed={_forest.PackedCount}");
            }
        }
        catch (ForestBudgetException ex)
        {
            budgetError = ex.Message;
            summaries.Add($"aborted: {ex.Message}");
        }

        var n = _tokens.Tokens.Count;
        SppfNode? root = null;
        var success = false;
        if (budgetError is null)
        {
            success = charts[n].Items
                .Any(it => it.Production.Id == "__seed__" && it.Dot == 1 && it.Start == 0);
            var rootKey = $"S:{_grammar.Start}@0:{n}";
            root = success && _forest.Nodes.TryGetValue(rootKey, out var rs) ? rs : null;
        }

        return new ParseResult
        {
            Success = success,
            Forest = _forest,
            Root = root,
            ChartSummaries = summaries,
            EmptyInput = empty,
            BudgetError = budgetError,
            NullableCycleEvents = _cycleEvents,
            NullableRotations = _nullableRotations
        };
    }

    private void CloseChart(ChartSet[] charts, int j)
    {
        var chart = charts[j];
        var zeroWidthEdges = new HashSet<string>();
        while (chart.TryDequeue(out var item))
        {
            if (!item.IsComplete)
            {
                var symbol = item.Production.Rhs[item.Dot];
                if (_grammar.IsTerminal(symbol))
                {
                    if (j < _tokens.Tokens.Count && _tokens.Tokens[j].Kind == symbol)
                        Scan(charts[j + 1], item, j);
                }
                else
                {
                    Predict(chart, symbol, j);
                    foreach (var completed in chart.Items)
                        TryComplete(chart, item, completed, j, zeroWidthEdges);
                }
            }
            else
            {
                foreach (var waiter in chart.Items.ToList())
                {
                    if (!waiter.IsComplete && waiter.Production.Rhs[waiter.Dot] == item.Production.Lhs)
                    {
                        if (TryComplete(chart, waiter, item, j, zeroWidthEdges))
                            chart.ReEnqueue(waiter);
                    }
                }
            }
        }
    }

    private bool TryComplete(ChartSet chart, ChartItem waiter, ChartItem completed, int j, HashSet<string> zeroWidthEdges)
    {
        if (completed.IsComplete == false || completed.SymbolNodeId is null) return false;
        if (waiter.Production.Rhs[waiter.Dot] != completed.Production.Lhs) return false;

        if (completed.Start == j)
        {
            var edgeKey = waiter.Key + "->" + completed.Key;
            if (!zeroWidthEdges.Add(edgeKey)) return false;
            if (_analysis.IsNullable(completed.Production.Lhs))
            {
                var rotationKey = waiter.Key;
                var rotation = _rotationByWaiter.TryGetValue(rotationKey, out var rc) ? rc + 1 : 1;
                _rotationByWaiter[rotationKey] = rotation;
                _nullableRotations++;
                if (rotation > NullableCycleRotationLimit && InNullableCycle(waiter))
                {
                    _cycleEvents.Add(
                        $"nullable-cycle termination: suppressed zero-width edge {edgeKey} at chart[{j}] " +
                        $"(rotation {rotation} > limit {NullableCycleRotationLimit}; strategy=fixpoint-with-edge-guard)");
                    return false;
                }
                _cycleEvents.Add(
                    $"nullable-cycle rotation {rotation} retained: {edgeKey} at chart[{j}] " +
                    $"(derivation family preserved, re-entrant node shared)");
            }
        }

        var prod = waiter.Production;
        var newDot = waiter.Dot + 1;
        var intermediate = IntermediateNode(prod, newDot, waiter.Start, j,
            waiter.IntermediateNodeId, completed.SymbolNodeId,
            pivot: waiter.IntermediateNodeId is null ? completed.Start : (int?)null);
        var newItem = new ChartItem
        {
            Production = prod,
            Dot = newDot,
            Start = waiter.Start,
            IntermediateNodeId = intermediate
        };
        if (newDot == prod.Rhs.Count)
            newItem.SymbolNodeId = SymbolNode(prod.Lhs, waiter.Start, j, prod.Id, intermediate, j);
        chart.Add(newItem);
        return true;
    }

    private bool InNullableCycle(ChartItem waiter) =>
        _analysis.NullableCycles.Any(c => c.Any(e => e.ProductionId == waiter.Production.Id));

    private void Predict(ChartSet chart, string nonTerminal, int position)
    {
        foreach (var p in _grammar.ProductionsOf(nonTerminal))
        {
            var fresh = new ChartItem { Production = p, Dot = 0, Start = position };
            if (p.Rhs.Count == 0)
            {
                var epsilonId = EpsilonNode(p, position);
                fresh.IntermediateNodeId = epsilonId;
                fresh.SymbolNodeId = SymbolNode(p.Lhs, position, position, p.Id, epsilonId, position);
            }
            chart.Add(fresh);
        }
    }

    private void Scan(ChartSet nextChart, ChartItem item, int j)
    {
        var token = _tokens.Tokens[j];
        var tNode = TerminalNode(token, j);
        var prod = item.Production;
        var newDot = item.Dot + 1;
        var intermediate = IntermediateNode(prod, newDot, item.Start, j + 1,
            item.IntermediateNodeId, tNode,
            pivot: item.IntermediateNodeId is null ? j : (int?)null);
        var newItem = new ChartItem
        {
            Production = prod,
            Dot = newDot,
            Start = item.Start,
            IntermediateNodeId = intermediate
        };
        if (newDot == prod.Rhs.Count)
            newItem.SymbolNodeId = SymbolNode(prod.Lhs, item.Start, j + 1, prod.Id, intermediate, j + 1);
        nextChart.Add(newItem);
    }

    private string EpsilonNode(Production p, int pos)
    {
        var id = $"I:{p.Id}@{pos}:{pos}#d0";
        var node = _forest.GetOrAdd(new SppfNode
        {
            Id = id,
            Kind = SppfNodeKind.Intermediate,
            ProductionId = p.Id,
            Dot = 0,
            LeftToken = pos,
            RightToken = pos
        });
        var packedId = $"P:{id}#epsilon:{p.Id}";
        if (node.Packed.All(pk => pk.Id != packedId))
        {
            _forest.AddPacked(node, new PackedNode
            {
                Id = packedId,
                ParentId = id,
                ProductionId = p.Id,
                Pivot = pos,
                ChildIds = Array.Empty<string>()
            });
        }
        return id;
    }

    private string IntermediateNode(Production p, int dot, int left, int right,
        string? previousId, string childId, int? pivot)
    {
        var id = $"I:{p.Id}@{left}:{right}#d{dot}";
        var node = _forest.GetOrAdd(new SppfNode
        {
            Id = id,
            Kind = SppfNodeKind.Intermediate,
            ProductionId = p.Id,
            Dot = dot,
            LeftToken = left,
            RightToken = right
        });
        var actualPivot = pivot ?? _forest.Nodes[previousId!].RightToken;
        var childIds = previousId is null ? new[] { childId } : new[] { previousId, childId };
        var packedId = $"P:{id}#k{actualPivot}@{DeterministicString(childIds)}";
        if (node.Packed.All(pk => pk.Id != packedId))
        {
            _forest.AddPacked(node, new PackedNode
            {
                Id = packedId,
                ParentId = id,
                ProductionId = p.Id,
                Pivot = actualPivot,
                ChildIds = childIds
            });
        }
        return id;
    }

    private string SymbolNode(string symbol, int left, int right, string productionId, string intermediateId, int pivot)
    {
        var id = $"S:{symbol}@{left}:{right}";
        var node = _forest.GetOrAdd(new SppfNode
        {
            Id = id,
            Kind = SppfNodeKind.Symbol,
            Symbol = symbol,
            LeftToken = left,
            RightToken = right
        });
        var packedId = $"P:{id}#{productionId}@{intermediateId}";
        if (node.Packed.All(pk => pk.Id != packedId))
        {
            _forest.AddPacked(node, new PackedNode
            {
                Id = packedId,
                ParentId = id,
                ProductionId = productionId,
                Pivot = pivot,
                ChildIds = new[] { intermediateId }
            });
        }
        return id;
    }

    private string TerminalNode(Token token, int index)
    {
        var id = $"T:{token.Kind}@{index}:{index + 1}";
        _forest.GetOrAdd(new SppfNode
        {
            Id = id,
            Kind = SppfNodeKind.Terminal,
            Symbol = token.Kind,
            TokenId = token.Id,
            LeftToken = index,
            RightToken = index + 1
        });
        return id;
    }

    internal static string DeterministicString(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items) sb.Append(i).Append(';');
        return sb.ToString();
    }
}

internal sealed class ChartSet
{
    public int Index { get; }
    private readonly Dictionary<string, ChartItem> _byKey = new();
    public List<ChartItem> Items { get; } = new();
    private readonly Queue<ChartItem> _pending = new();
    private readonly HashSet<string> _queued = new();

    public ChartSet(int index) => Index = index;

    public void Add(ChartItem item)
    {
        if (_byKey.TryGetValue(item.Key, out var existing))
        {
            Merge(existing, item);
            return;
        }
        _byKey[item.Key] = item;
        Items.Add(item);
        if (_queued.Add(item.Key)) _pending.Enqueue(item);
    }

    public void ReEnqueue(ChartItem item)
    {
        if (_queued.Add(item.Key)) _pending.Enqueue(item);
    }

    public bool TryDequeue(out ChartItem item)
    {
        if (_pending.Count == 0) { item = null!; return false; }
        item = _pending.Dequeue();
        _queued.Remove(item.Key);
        return true;
    }

    private static void Merge(ChartItem existing, ChartItem incoming)
    {
        if (existing.IntermediateNodeId is null && incoming.IntermediateNodeId is not null)
            existing.IntermediateNodeId = incoming.IntermediateNodeId;
        if (existing.SymbolNodeId is null && incoming.SymbolNodeId is not null)
            existing.SymbolNodeId = incoming.SymbolNodeId;
    }
}
