namespace ForestRoom.Core;

/// <summary>
/// Earley 识别器 + 二值化 SPPF 构建（Scott/Johnstone 风格）。
/// 显式终止策略：
///  - 左递归：chart item 集合唯一，重复 item 直接抑制 → 保证终止；
///  - nullable cycle：空节点按 (symbol, position) 记忆化，重入返回在建节点，
///    该 derivation 记为 CycleTerminated 且只记一次；
///  - 森林预算：节点数超预算即停止扩展，保留已建部分森林并记录事件。
/// </summary>
public sealed class EarleyParser
{
    private readonly GrammarVersion _grammar;
    private readonly TokenStream _tokens;
    private readonly int _budget;
    private readonly ParseResult _result;
    private readonly Dictionary<string, ForestNode> _nodeByKey = new();
    private readonly HashSet<string> _packedKeys = new();
    private readonly Dictionary<(string sym, int pos), ForestNode> _emptyNodes = new();
    private readonly HashSet<(string sym, int pos)> _emptyInProgress = new();
    private readonly HashSet<(string sym, int pos)> _cycleReported = new();
    private int _nodeSeq;
    private int _packedSeq;
    private long _eventSeq;
    private bool _budgetHit;

    private sealed record Item(Production Prod, int Dot, int Origin, ForestNode? Node);

    private readonly List<HashSet<string>> _seen = new();
    private readonly List<List<Item>> _chart = new();
    private readonly List<Queue<Item>> _queues = new();

    public EarleyParser(GrammarVersion grammar, TokenStream tokens, int budget, string runId)
    {
        _grammar = grammar;
        _tokens = tokens;
        _budget = budget;
        _result = new ParseResult { RunId = runId };
    }

    public ParseResult Parse()
    {
        var nullable = NullableAnalysis.Compute(_grammar, null);
        _result.Nullable = nullable;
        foreach (var cycle in nullable.DetectedCycles)
            Emit(EventKinds.NullableCycleTerminated, string.Join("->", cycle),
                $"nullable cycle [{string.Join(" -> ", cycle)}] terminated by monotone fixpoint + memoized empty nodes",
                ("strategy", "monotone-fixpoint+memoized-empty-node"));

        EmitLeftRecursionGuards();
        EmitDuplicateProductions();
        EmitQuantityDiagnostics();

        int n = _tokens.Tokens.Count;
        for (int i = 0; i <= n; i++)
        {
            _seen.Add(new HashSet<string>());
            _chart.Add(new List<Item>());
            _queues.Add(new Queue<Item>());
        }

        foreach (var p in _grammar.For(_grammar.StartSymbol))
            AddItem(0, new Item(p, 0, 0, null));

        for (int j = 0; j <= n; j++)
        {
            while (_queues[j].Count > 0)
            {
                if (_budgetHit) break;
                var item = _queues[j].Dequeue();
                if (item.Dot < item.Prod.Rhs.Count)
                {
                    var next = item.Prod.Rhs[item.Dot];
                    if (_grammar.NonTerminals.Contains(next))
                    {
                        Predict(j, next);
                        if (nullable.NullableNonTerminals.Contains(next))
                        {
                            // nullable 完成：B 可空 ⇒ 立即让等待 B 的 item 越过 B
                            var empty = GetEmptyNode(next, j);
                            if (empty != null)
                                AddItem(j, new Item(item.Prod, item.Dot + 1, item.Origin,
                                    MakeNode(item.Prod, item.Dot + 1, item.Origin, j, j, item.Node, empty)));
                        }
                    }
                    else
                    {
                        Scan(j, item, next);
                    }
                }
                else
                {
                    Complete(j, item);
                }
            }
            if (_budgetHit) break;
        }

        if (_budgetHit)
        {
            _result.Status = ParseStatus.BudgetExhausted;
            Emit(EventKinds.BudgetExhausted, _result.RunId,
                $"forest budget {_budget} nodes exhausted at token position boundary; partial forest kept",
                ("budget", _budget.ToString()), ("nodes", _result.Forest.NodeCount.ToString()));
            return _result;
        }

        // 接受条件：E_n 中存在 (S->α·, 0)
        ForestNode? root = null;
        foreach (var item in _chart[n])
        {
            if (item.Origin == 0 && item.Dot == item.Prod.Rhs.Count
                && item.Prod.Lhs == _grammar.StartSymbol && item.Node != null)
            {
                root = item.Node;
                break;
            }
        }
        if (root != null)
        {
            _result.Status = ParseStatus.Accepted;
            _result.RootNodeId = root.Id;
            if (n == 0)
                Emit(EventKinds.EmptyInputAccepted, _result.RunId,
                    "empty token stream accepted: start symbol is nullable",
                    ("start", _grammar.StartSymbol));
        }
        else
        {
            _result.Status = ParseStatus.Rejected;
        }

        foreach (var node in _result.Forest.AmbiguousNodes())
            Emit(EventKinds.AmbiguityDetected, node.Id,
                $"symbol node {node.Label} over span {node.Span} has {node.Alternatives.Count} packed alternatives",
                ("span", node.Span), ("label", node.Label));

        return _result;
    }

    private void Predict(int j, string nonTerminal)
    {
        foreach (var p in _grammar.For(nonTerminal))
            AddItem(j, new Item(p, 0, j, null));
    }

    private void Scan(int j, Item item, string terminal)
    {
        if (j >= _tokens.Tokens.Count) return;
        var tok = _tokens.Tokens[j];
        if (tok.Symbol != terminal) return;
        var tNode = GetOrCreateNode(NodeKind.Terminal, terminal, j, j + 1);
        if (tNode == null) return;
        var node = MakeNode(item.Prod, item.Dot + 1, item.Origin, item.Dot == 0 ? j : item.Origin, j + 1, item.Node, tNode);
        AddItem(j + 1, new Item(item.Prod, item.Dot + 1, item.Origin, node));
    }

    private void Complete(int j, Item item)
    {
        var y = CompletedNode(item, j); // ε 产生式在此物化空节点
        if (y == null) return;
        foreach (var waiting in _chart[item.Origin].ToList())
        {
            if (waiting.Dot < waiting.Prod.Rhs.Count
                && waiting.Prod.Rhs[waiting.Dot] == item.Prod.Lhs)
            {
                var node = MakeNode(waiting.Prod, waiting.Dot + 1, waiting.Origin,
                    waiting.Origin, j, waiting.Node, y);
                AddItem(j, new Item(waiting.Prod, waiting.Dot + 1, waiting.Origin, node));
            }
        }
    }

    private void AddItem(int j, Item item)
    {
        var key = $"{item.Prod.Id}:{item.Dot}:{item.Origin}:{item.Node?.Id ?? "-"}";
        if (!_seen[j].Add(key)) return;
        _chart[j].Add(item);
        _queues[j].Enqueue(item);
        // 完成回放：若 item 等待的非终结符已在 E_j 中以 origin=j 完成（晚到的等待者），
        // 立即越过它，否则该推导会丢失（nullable 场景必需）。
        if (item.Dot < item.Prod.Rhs.Count)
        {
            var next = item.Prod.Rhs[item.Dot];
            if (_grammar.NonTerminals.Contains(next))
            {
                foreach (var c in _chart[j].ToList())
                {
                    if (c.Dot == c.Prod.Rhs.Count && c.Origin == j && c.Prod.Lhs == next)
                    {
                        var y = CompletedNode(c, j);
                        if (y == null) continue;
                        AddItem(j, new Item(item.Prod, item.Dot + 1, item.Origin,
                            MakeNode(item.Prod, item.Dot + 1, item.Origin, item.Origin, j, item.Node, y)));
                    }
                }
            }
        }
    }

    private ForestNode? CompletedNode(Item c, int j)
        => c.Node ?? MakeNode(c.Prod, c.Dot, c.Origin, j, j, null, null);

    /// <summary>二值化 SPPF 节点构造。w 覆盖已匹配前缀，v 覆盖刚匹配的符号。</summary>
    private ForestNode? MakeNode(Production prod, int dot, int origin, int start, int end,
        ForestNode? w, ForestNode? v)
    {
        bool complete = dot == prod.Rhs.Count;
        string label = complete ? prod.Lhs : IntermediateLabel(prod, dot);
        int spanStart = w == null ? (v?.Start ?? start) : w.Start;
        var node = GetOrCreateNode(complete ? NodeKind.Symbol : NodeKind.Intermediate, label, spanStart, end);
        if (node == null) return null;
        var children = new List<string>();
        if (w != null) children.Add(w.Id);
        if (v != null) children.Add(v.Id);
        AddPacked(node, prod.Id, children, PackedStatus.Active, null);
        return node;
    }

    private static string IntermediateLabel(Production p, int dot)
    {
        var parts = new List<string>();
        for (int i = 0; i < p.Rhs.Count; i++)
        {
            if (i == dot) parts.Add("·");
            parts.Add(p.Rhs[i]);
        }
        if (dot == p.Rhs.Count) parts.Add("·");
        return $"{p.Lhs} -> {string.Join(' ', parts)}";
    }

    private ForestNode? GetOrCreateNode(NodeKind kind, string label, int start, int end)
    {
        var key = $"{kind}|{label}|{start}|{end}";
        if (_nodeByKey.TryGetValue(key, out var existing)) return existing;
        if (_result.Forest.NodeCount >= _budget)
        {
            _budgetHit = true;
            return null;
        }
        var node = new ForestNode
        {
            Id = $"n{++_nodeSeq:D5}",
            Kind = kind,
            Label = label,
            Start = start,
            End = end
        };
        _nodeByKey[key] = node;
        _result.Forest.Nodes[node.Id] = node;
        return node;
    }

    private void AddPacked(ForestNode parent, string? productionId, List<string> children,
        PackedStatus status, string? reason)
    {
        var key = $"{parent.Id}|{productionId}|{string.Join(",", children)}|{status}";
        if (!_packedKeys.Add(key)) return; // 共享：同一 derivation 只存一次，不丢失
        parent.Alternatives.Add(new PackedNode
        {
            Id = $"p{++_packedSeq:D5}",
            ParentNodeId = parent.Id,
            Ordinal = parent.Alternatives.Count,
            ProductionId = productionId,
            ChildIds = children,
            Status = status,
            FilterReason = reason
        });
    }

    /// <summary>nullable 非终结符在 pos 处的空节点。记忆化 + 重入保护：
    /// 重入时返回在建节点并把该 derivation 记为 CycleTerminated（显式终止）。</summary>
    private ForestNode? GetEmptyNode(string symbol, int pos)
    {
        if (_emptyNodes.TryGetValue((symbol, pos), out var memo)) return memo;
        if (!_emptyInProgress.Add((symbol, pos)))
        {
            // 重入：nullable cycle。返回 null，由外层已建节点承载；记录一次。
            if (_cycleReported.Add((symbol, pos)))
                Emit(EventKinds.NullableCycleTerminated, $"{symbol}@{pos}",
                    $"nullable cycle at {symbol}@{pos}: re-entrant empty derivation terminated, recorded once",
                    ("strategy", "memoized-empty-node"), ("symbol", symbol));
            return null;
        }
        try
        {
            var node = GetOrCreateNode(NodeKind.Symbol, symbol, pos, pos);
            if (node == null) return null;
            foreach (var p in _grammar.For(symbol))
            {
                if (p.Rhs.Count == 0)
                {
                    AddPacked(node, p.Id, new List<string>(), PackedStatus.Active, null);
                }
                else if (p.Rhs.All(s => _grammar.NonTerminals.Contains(s)
                            && _result.Nullable!.NullableNonTerminals.Contains(s)))
                {
                    // 全 nullable RHS：递归构造子空节点；环在此被重入保护截断
                    var children = new List<string>();
                    var ok = true;
                    foreach (var s in p.Rhs)
                    {
                        var child = GetEmptyNode(s, pos);
                        if (child == null) { ok = false; break; }
                        children.Add(child.Id);
                    }
                    if (ok)
                    {
                        var status = p.Rhs.Any(s => _cycleReported.Contains((s, pos)))
                            ? PackedStatus.CycleTerminated : PackedStatus.Active;
                        AddPacked(node, p.Id, children, status,
                            status == PackedStatus.CycleTerminated
                                ? "nullable cycle: derivation recorded once, not expanded" : null);
                    }
                    else if (_cycleReported.Contains((symbol, pos)) || p.Rhs.Any(s => _cycleReported.Contains((s, pos))))
                    {
                        AddPacked(node, p.Id, children, PackedStatus.CycleTerminated,
                            "nullable cycle: derivation recorded once, not expanded");
                    }
                }
            }
            _emptyNodes[(symbol, pos)] = node;
            return node;
        }
        finally
        {
            _emptyInProgress.Remove((symbol, pos));
        }
    }

    private void EmitLeftRecursionGuards()
    {
        // 静态检测 A =>+ A（首位置左递归），记录显式终止策略
        var first = new Dictionary<string, HashSet<string>>();
        foreach (var nt in _grammar.NonTerminals) first[nt] = new();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in _grammar.Productions)
            {
                foreach (var s in p.Rhs)
                {
                    if (!_grammar.NonTerminals.Contains(s)) break;
                    foreach (var f in first[s])
                        if (first[p.Lhs].Add(f)) changed = true;
                    if (first[p.Lhs].Add(s)) changed = true;
                    if (!(_result.Nullable?.NullableNonTerminals.Contains(s) ?? false)) break;
                }
            }
        }
        foreach (var nt in _grammar.NonTerminals.Where(nt => first[nt].Contains(nt)))
            Emit(EventKinds.LeftRecursionGuard, nt,
                $"left-recursive nonterminal {nt}: termination via chart item-set uniqueness",
                ("strategy", "chart-set-uniqueness"), ("symbol", nt));
    }

    private void EmitDuplicateProductions()
    {
        foreach (var g in _grammar.Productions
                     .GroupBy(p => p.Lhs + "->" + string.Join(" ", p.Rhs))
                     .Where(g => g.Count() > 1))
            Emit(EventKinds.DuplicateProductionKept, g.Key,
                $"duplicate production shape {g.Key} kept as distinct identities: {string.Join(", ", g.Select(p => p.Id))}",
                ("productions", string.Join(",", g.Select(p => p.Id))));
    }

    private void EmitQuantityDiagnostics()
    {
        foreach (var t in _tokens.Tokens.Where(t => t.Position.Diagnostic != null))
            Emit(EventKinds.QuantityDiagnostic, t.Id,
                $"token {t.Id} position '{t.Position.Raw}' ({t.Position.Unit}/{t.Position.Basis}) not convertible: {t.Position.Diagnostic}; raw value kept in place",
                ("raw", t.Position.Raw), ("unit", t.Position.Unit));
    }

    private void Emit(string kind, string subject, string detail, params (string, string)[] attrs)
        => _result.Events.Add(ForestEvent.Of(++_eventSeq, kind, subject, detail, attrs));
}
