using Afc.Core.Analysis;
using Afc.Core.Model;

namespace Afc.Core.Parsing;

public sealed class ParseOptions
{
    public int MaxNodes { get; init; } = 100_000;
    public int MaxWorkItems { get; init; } = 200_000;
    public int MaxPackedPerNode { get; init; } = 10_000;
}

public enum ParseStatus { Accepted, Rejected, BudgetExhausted }

/// <summary>dotted item 的出现：chart[j] 中 (产生式, dot, 起点 i) -> 中间 SPPF 节点 id 列表。</summary>
internal sealed record ItemKey(string ProductionId, int Dot, int Start);

public sealed record ParseResult(
    ParseStatus Status,
    Forest Forest,
    SppfNode? Root,
    IReadOnlyList<AnalyzerEvent> Events,
    int NodesCreated,
    int WorkItemsProcessed,
    bool EmptyInput);

/// <summary>
/// 带 SPPF 的 Earley 解析器，work-list 求不动点：
/// 预测(PREDICT)、完成(COMPLETE)、扫描(SCAN) 全部去重，左递归与可空产生式
/// 由“已处理 item / 已入队工作项”备忘显式终止，不会重复展开同一 item。
/// 预算耗尽立即停机，保留已构造的部分森林与诊断。
/// </summary>
public sealed class EarleyParser
{
    private readonly GrammarDoc _g;
    private readonly TokenDoc _t;
    private readonly ParseOptions _opt;
    private readonly Provenance _prov;
    private readonly Forest _forest = new();
    private readonly List<AnalyzerEvent> _events = new();

    // chart[j]: ItemKey -> 中间节点 id 集合（不同推导产生不同中间结果）
    private readonly List<Dictionary<ItemKey, HashSet<string>>> _chart = new();
    // 已处理 item（completion memo）
    private readonly List<HashSet<ItemKey>> _processed = new();
    // 待完成符号节点：chart[j] 中某 item 的下一个符号 X，被 sn:(X,k,j) 满足时推进
    private readonly List<Dictionary<string, List<(ItemKey item, string interId)>>> _wait = new();

    private readonly Queue<Work> _queue = new();
    private int _nodes;
    private int _workItems;
    private long _seq;
    private bool _budgetHit;

    private enum WorkType { Predict, Complete, Scan }
    private record Work(WorkType Type, int ChartIndex, ItemKey? Item, string? InterId, string? SymbolNodeId);

    public EarleyParser(GrammarDoc grammar, TokenDoc tokens, ParseOptions options, Provenance provenance)
    {
        _g = grammar; _t = tokens; _opt = options; _prov = provenance;
    }

    public ParseResult Parse()
    {
        var n = _t.Tokens.Count;
        for (var j = 0; j <= n; j++)
        {
            _chart.Add(new());
            _processed.Add(new());
            _wait.Add(new());
        }

        var emptyInput = n == 0;
        if (emptyInput)
            AddEvent(EventKind.EmptyInput, EventLevel.Info, "EMPTY-INPUT",
                "token 流为空：解析器只运行预测/完成不动点",
                new[] { "tokens=0", "input=empty", "adopted=true" }, Array.Empty<string>());

        // 种子：start ::= • α，起点 0
        SeedStart();

        while (_queue.Count > 0 && !_budgetHit)
        {
            var w = _queue.Dequeue();
            _workItems++;
            if (_workItems > _opt.MaxWorkItems) { Exhausted("work-items", _opt.MaxWorkItems); break; }
            switch (w.Type)
            {
                case WorkType.Predict: DoPredict(w); break;
                case WorkType.Complete: DoComplete(w); break;
                case WorkType.Scan: DoScan(w); break;
            }
        }

        SppfNode? root = null;
        var status = ParseStatus.Accepted;
        if (_budgetHit) status = ParseStatus.BudgetExhausted;

        if (!emptyInput || status == ParseStatus.Accepted)
        {
            root = _forest.Nodes.Values
                .FirstOrDefault(x => x.Kind == SppfNodeKind.Symbol
                    && x.Symbol == _g.StartSymbol && x.Left == 0 && x.Right == n);
        }
        if (status == ParseStatus.Accepted && root is null) status = ParseStatus.Rejected;

        EmitAmbiguityEvents();
        return new ParseResult(status, _forest, root, _events, _nodes, _workItems, emptyInput);
    }

    private void SeedStart()
    {
        foreach (var p in _g.For(_g.StartSymbol))
            AddItem(p.Id, 0, 0, interId: null);
    }

    private string InterId(string productionId, int dot, int start, int end, string lastChildId)
        => $"in:({productionId},{dot},{start},{end}:{lastChildId})";

    private void AddItem(string prodId, int dot, int start, string? interId)
    {
        var key = new ItemKey(prodId, dot, start);
        if (!_chart[start + dot].TryGetValue(key, out var set))
        {
            set = new HashSet<string>();
            _chart[start + dot][key] = set;
        }

        var p = _g.Productions.First(x => x.Id == prodId);
        string? nodeId;
        if (dot == 0)
        {
            nodeId = $"in:({prodId},0,{start},{start})";
            if (set.Add(nodeId))
                EnqueuePredictOrComplete(key, nodeId, start + dot);
        }
        else
        {
            nodeId = interId!;
            if (set.Add(nodeId))
                EnqueuePredictOrComplete(key, nodeId, start + dot);
        }
    }

    private void EnqueuePredictOrComplete(ItemKey key, string interId, int j)
    {
        var p = _g.Productions.First(x => x.Id == key.ProductionId);
        if (key.Dot >= p.Rhs.Count)
        {
            _queue.Enqueue(new Work(WorkType.Complete, j, key, interId, null));
        }
        else
        {
            _queue.Enqueue(new Work(WorkType.Predict, j, key, interId, null));
        }
    }

    private void DoPredict(Work w)
    {
        var key = w.Item!;
        var j = w.ChartIndex;
        var p = _g.Productions.First(x => x.Id == key.ProductionId);
        var sym = p.Rhs[key.Dot];

        if (sym.StartsWith('"') || sym.StartsWith('$'))
        {
            _queue.Enqueue(new Work(WorkType.Scan, j, key, w.InterId, null));
            return;
        }

        // 注册等待：当 sn:(sym,k,j) 出现时推进本 item
        if (!_wait[j].TryGetValue(sym, out var list))
        {
            list = new List<(ItemKey, string)>();
            _wait[j][sym] = list;
        }
        list.Add((key, w.InterId!));

        foreach (var q in _g.For(sym))
            AddItem(q.Id, 0, j, null);
    }

    private void DoScan(Work w)
    {
        var key = w.Item!;
        var j = w.ChartIndex;
        if (j >= _t.Tokens.Count) return;
        var p = _g.Productions.First(x => x.Id == key.ProductionId);
        var sym = p.Rhs[key.Dot];
        var tok = _t.Tokens[j];
        if (!TerminalMatches(sym, tok)) return;

        var tn = _forest.Terminal(TerminalName(sym), tok.Id, j);
        RegisterNode(tn);

        var newInter = key.Dot + 1 == p.Rhs.Count
            ? $"in:({p.Id},{key.Dot + 1},{key.Start},{j + 1}:{tn.Id})"
            : InterId(p.Id, key.Dot + 1, key.Start, j + 1, tn.Id);
        AddItem(p.Id, key.Dot + 1, key.Start, newInter);

        // 记录中间节点的构造链，便于完成时重建 children
        _interChildren[newInter] = (w.InterId, tn.Id, key.Dot);
    }

    private readonly Dictionary<string, (string? prev, string child, int position)> _interChildren = new();

    private void DoComplete(Work w)
    {
        var key = w.Item!;
        var j = w.ChartIndex;
        var p = _g.Productions.First(x => x.Id == key.ProductionId);

        // 每个完成 item 只处理一次（左递归/可空循环终止的关键备忘）
        if (!_processed[j].Add(key)) return;

        var interIds = _chart[j][key];
        foreach (var interId in interIds)
        {
            var symbolNode = _forest.Symbol(p.Lhs, key.Start, j);
            RegisterNode(symbolNode);

            var children = ReconstructChildren(p.Id, key.Start, j, interId, out var pivot);
            _forest.AddPacked(symbolNode, p.Id, pivot, children, _prov.RuleVersion);

            // 满足等待者：chart[j] 中下一个符号为 p.Lhs 的 item
            AdvanceWaiters(j, p.Lhs, symbolNode.Id);
        }
    }

    private void AdvanceWaiters(int j, string symbol, string symbolNodeId)
    {
        if (!_wait[j].TryGetValue(symbol, out var list)) return;
        // 快照，避免边遍历边注册（可空递归同一 chart 内推进）
        foreach (var (waiter, prevInter) in list.ToArray())
        {
            var p = _g.Productions.First(x => x.Id == waiter.ProductionId);
            var newDot = waiter.Dot + 1;
            var newEnd = j; // 同一 chart（可空）或后续 chart；右界由 waiter.Start+newDot 决定
            var newInter = $"in:({p.Id},{newDot},{waiter.Start},{j}:{symbolNodeId})";
            AddItem(p.Id, newDot, waiter.Start, newInter);
            _interChildren[newInter] = (prevInter, symbolNodeId, waiter.Dot);
        }
    }

    private List<string> ReconstructChildren(string prodId, int start, int end, string interId, out int pivot)
    {
        var children = new List<string>();
        var cur = interId;
        pivot = -1;
        while (_interChildren.TryGetValue(cur, out var link))
        {
            children.Add(link.child);
            cur = link.prev ?? "";
            if (cur.Length == 0) break;
        }
        children.Reverse();
        // pivot：首个符号节点右界；简化为中间边界（完成时按首个非终结符子节点）
        pivot = children.Count > 0 ? start : end;
        return children;
    }

    private void RegisterNode(SppfNode node)
    {
        if (!_forest.Nodes.ContainsKey(node.Id)) { _nodes++; }
        if (_nodes > _opt.MaxNodes) Exhausted("nodes", _opt.MaxNodes);
        if (node.Packed.Count > _opt.MaxPackedPerNode) Exhausted("packed-per-node", _opt.MaxPackedPerNode);
    }

    private void Exhausted(string budget, int limit)
    {
        if (_budgetHit) return;
        _budgetHit = true;
        AddEvent(EventKind.BudgetExhausted, EventLevel.Error, "BUDGET-EXHAUSTED",
            $"森林预算耗尽（{budget} 上限 {limit}）：停机并保留已构造的部分森林",
            new[] { $"budget={budget}", $"limit={limit}", "partial-forest=retained", "adopted=true" },
            Array.Empty<string>());
    }

    private void EmitAmbiguityEvents()
    {
        foreach (var node in _forest.Nodes.Values.Where(n => n.Kind == SppfNodeKind.Symbol && n.Packed.Count > 1))
        {
            var prods = node.Packed.Select(p => p.ProductionId).Distinct().ToList();
            AddEvent(EventKind.Ambiguity, EventLevel.Warning, "AMBIGUITY",
                $"局部歧义：{node.Symbol} 在 token [{node.Left},{node.Right}) 有 {node.Packed.Count} 个 packed candidate",
                new[]
                {
                    $"node={node.Id}", $"range=[{node.Left},{node.Right})",
                    $"candidates={node.Packed.Count}", $"productions=[{string.Join(",", prods)}]",
                    "all-candidates-retained=true", "adopted=true"
                },
                new[] { node.Id }.Concat(prods.Select(p => $"prod:{p}")).ToList());
        }
    }

    private static bool TerminalMatches(string sym, Token tok)
    {
        if (sym.StartsWith('"'))
        {
            var lit = sym[1..^1];
            return tok.RawText == lit;
        }
        if (sym.StartsWith('$')) return tok.Kind == sym[1..];
        return false;
    }

    private static string TerminalName(string sym) => sym.StartsWith('$') ? sym[1..] : sym[1..^1];

    private void AddEvent(EventKind kind, EventLevel level, string code, string msg,
        string[] facts, List<string> subjects)
    {
        _events.Add(new AnalyzerEvent(
            $"ev-{code.ToLowerInvariant()}-{_seq}", _seq++, kind, level, code, msg,
            facts, subjects, _prov, null));
    }
}
