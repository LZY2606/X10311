namespace ForestChamber.Core;

public sealed class ForestBudget
{
    public int MaxItems { get; init; } = 20_000;
    public int MaxNodes { get; init; } = 10_000;
    public int MaxPacked { get; init; } = 30_000;
    public int MaxTrees { get; init; } = 50;
}

internal sealed class EarleyItem
{
    public required int ProductionIndex;
    public required int Dot;
    public required int Start;
    public required InterimSppfNode Prefix;
    public string Key => $"{ProductionIndex}:{Dot}:{Start}:{Prefix.NodeId}";
}

internal sealed class Column
{
    public required int Index;
    public Dictionary<string, EarleyItem> ByKey { get; } = new();
    public List<EarleyItem> Items { get; } = new();
    public Queue<EarleyItem> Worklist { get; } = new();
    public Dictionary<string, List<EarleyItem>> Pending { get; } = new(); // expecting nonterminal
    public Dictionary<string, List<EarleyItem>> Scans { get; } = new();   // expecting terminal
    public Dictionary<(string Sym, int Start), SymbolSppfNode> Completed { get; } = new();
}

internal sealed class EarleyParser
{
    private readonly GrammarDoc _grammar;
    private readonly IReadOnlyList<TokenRef> _tokens;
    private readonly ForestBudget _budget;
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly List<Column> _columns = new();
    private readonly SppfForest _forest;

    private int _nextNodeId;
    private int _nextPackedId;
    private int _itemsCreated;
    private int _packedCreated;
    private bool _budgetExhausted;
    private string? _budgetSubject;

    public EarleyParser(GrammarDoc grammar, IReadOnlyList<TokenRef> tokens, ForestBudget budget)
    {
        _grammar = grammar;
        _tokens = tokens;
        _budget = budget;
        _forest = new SppfForest
        {
            StartSymbol = grammar.StartSymbol,
            CoordinateBasis = "token-cursor[0..n]"
        };
    }

    public (SppfForest Forest, bool BudgetExhausted, string? BudgetSubject, IReadOnlyList<Diagnostic> Diagnostics) Parse()
    {
        for (var c = 0; c <= _tokens.Count; c++)
            _columns.Add(new Column { Index = c });

        // Seed: start symbol predictions in column 0.
        Predict(_columns[0], _grammar.StartSymbol, 0);
        ProcessClosure(_columns[0]);

        for (var k = 0; k < _tokens.Count; k++)
        {
            if (_budgetExhausted) break;
            DoScans(_columns[k], k);
            ProcessClosure(_columns[k + 1]);
        }

        MarkCycles();

        if (!_budgetExhausted && _columns[^1].Completed.TryGetValue((_grammar.StartSymbol, 0), out var root))
            _forest.RootNodeId = root.NodeId;

        if (!_budgetExhausted && _forest.RootNodeId is null)
        {
            var expected = new SortedSet<string>(_columns[^1].Scans.Keys);
            _diagnostics.Add(new Diagnostic("P400", "error",
                $"no parse of {_grammar.StartSymbol} spanning token-cursor [0..{_tokens.Count}]",
                null, new Dictionary<string, string>
                {
                    ["coordinate-basis"] = "token-cursor[0..n]",
                    ["expected-terminals"] = expected.Count == 0 ? "<none-in-column>" : string.Join(",", expected),
                    ["column"] = _tokens.Count.ToString()
                }));
        }

        if (_budgetExhausted)
            _diagnostics.Add(new Diagnostic("P300", "error",
                "forest budget exhausted; partial forest retained and no result was silently collapsed",
                _budgetSubject, new Dictionary<string, string>
                {
                    ["budget.items"] = _budget.MaxItems.ToString(),
                    ["budget.nodes"] = _budget.MaxNodes.ToString(),
                    ["budget.packed"] = _budget.MaxPacked.ToString(),
                    ["used.items"] = _itemsCreated.ToString(),
                    ["used.nodes"] = _nextNodeId.ToString(),
                    ["used.packed"] = _packedCreated.ToString(),
                    ["unit"] = "count"
                }));

        return (_forest, _budgetExhausted, _budgetSubject, _diagnostics);
    }

    private void ProcessClosure(Column col)
    {
        while (col.Worklist.Count > 0)
        {
            if (_budgetExhausted) return;
            var item = col.Worklist.Dequeue();
            var prod = _grammar.Productions[item.ProductionIndex];
            if (item.Dot == prod.Rhs.Count)
            {
                CompleteItem(col, item, prod);
                continue;
            }
            var next = prod.Rhs[item.Dot];
            if (next is NonterminalSym nt)
            {
                col.Pending.TryAdd(nt.Name, new List<EarleyItem>());
                col.Pending[nt.Name].Add(item);
                // A completed node for this nonterminal may already exist in this
                // column (nullable completion produced earlier in the same closure).
                foreach (var kv in col.Completed)
                    if (kv.Key.Sym == nt.Name)
                        AdvanceWith(col, item, kv.Value);
                Predict(col, nt.Name, col.Index);
            }
            else
            {
                var term = ((TerminalSym)next).Name;
                col.Scans.TryAdd(term, new List<EarleyItem>());
                col.Scans[term].Add(item);
            }
        }
    }

    private void Predict(Column col, string nonterminal, int start)
    {
        for (var p = 0; p < _grammar.Productions.Count; p++)
        {
            var prod = _grammar.Productions[p];
            if (prod.Lhs != nonterminal) continue;
            var baseInterim = GetOrCreateInterim(p, 0, start, start);
            if (prod.Rhs.Count == 1 && prod.Rhs[0] is TerminalSym eps && eps.Name == "EPS")
            {
                AddFamily(baseInterim, p, 0, pivot: start, left: null, right: null);
            }
            AddItem(col, new EarleyItem
            {
                ProductionIndex = p,
                Dot = 0,
                Start = start,
                Prefix = baseInterim
            });
        }
    }

    private void DoScans(Column col, int k)
    {
        var token = _tokens[k];
        foreach (var (term, items) in col.Scans)
        {
            if (term != token.Kind) continue;
            var terminalNode = GetOrCreateTerminal(k, term);
            foreach (var expecting in items)
            {
                var newDot = expecting.Dot + 1;
                var interim = GetOrCreateInterim(expecting.ProductionIndex, newDot, expecting.Start, k + 1);
                AddFamily(interim, expecting.ProductionIndex, newDot, k,
                    expecting.Dot == 0 ? null : expecting.Prefix, terminalNode);
                AddItem(_columns[k + 1], new EarleyItem
                {
                    ProductionIndex = expecting.ProductionIndex,
                    Dot = newDot,
                    Start = expecting.Start,
                    Prefix = interim
                });
            }
        }
    }

    private void CompleteItem(Column col, EarleyItem item, ProductionDef prod)
    {
        var node = GetOrCreateSymbol(prod.Lhs, item.Start, col.Index);
        var added = AddFamily(node, item.ProductionIndex, prod.Rhs.Count, item.Start, null, item.Prefix);
        if (added && item.Start < _columns.Count)
        {
            var origin = _columns[item.Start];
            if (origin.Pending.TryGetValue(prod.Lhs, out var waiting))
                foreach (var expecting in waiting.ToList())
                    AdvanceWith(col, expecting, node);
        }
    }

    private void AdvanceWith(Column endCol, EarleyItem expecting, SymbolSppfNode completedNode)
    {
        // Prefix interim of the expecting item ends at the column that triggered the
        // completion; the completed symbol must begin there and end at endCol.
        if (completedNode.End != endCol.Index || completedNode.Start != expecting.Prefix.End)
            return;
        var newDot = expecting.Dot + 1;
        var interim = GetOrCreateInterim(expecting.ProductionIndex, newDot, expecting.Start, endCol.Index);
        AddFamily(interim, expecting.ProductionIndex, newDot, completedNode.Start,
            expecting.Dot == 0 ? null : expecting.Prefix, completedNode);
        AddItem(endCol, new EarleyItem
        {
            ProductionIndex = expecting.ProductionIndex,
            Dot = newDot,
            Start = expecting.Start,
            Prefix = interim
        });
    }

    private void AddItem(Column col, EarleyItem item)
    {
        if (col.ByKey.ContainsKey(item.Key)) return;
        _itemsCreated++;
        if (_itemsCreated > _budget.MaxItems)
        {
            _budgetExhausted = true;
            _budgetSubject ??= $"item:{item.Key}";
            return;
        }
        col.ByKey[item.Key] = item;
        col.Items.Add(item);
        col.Worklist.Enqueue(item);
    }

    private TerminalSppfNode GetOrCreateTerminal(int tokenIndex, string term)
    {
        foreach (var n in _forest.Nodes)
            if (n is TerminalSppfNode t && t.TokenIndex == tokenIndex && t.Terminal == term)
                return t;
        var node = new TerminalSppfNode
        {
            NodeId = _nextNodeId++,
            Start = tokenIndex,
            End = tokenIndex + 1,
            CoordinateBasis = "token-cursor[0..n]",
            TokenIndex = tokenIndex,
            Terminal = term
        };
        RegisterNode(node);
        return node;
    }

    private SymbolSppfNode GetOrCreateSymbol(string sym, int start, int end)
    {
        var col = _columns[end];
        if (col.Completed.TryGetValue((sym, start), out var existing)) return existing;
        var node = new SymbolSppfNode
        {
            NodeId = _nextNodeId++,
            Start = start,
            End = end,
            CoordinateBasis = "token-cursor[0..n]",
            Symbol = sym,
            IsNonterminal = _grammar.Nonterminals.Contains(sym)
        };
        RegisterNode(node);
        col.Completed[(sym, start)] = node;
        return node;
    }

    private InterimSppfNode GetOrCreateInterim(int prodIndex, int dot, int start, int end)
    {
        foreach (var n in _forest.Nodes)
            if (n is InterimSppfNode i && i.ProductionIndex == prodIndex && i.Dot == dot && i.Start == start && i.End == end)
                return i;
        var prod = _grammar.Productions[prodIndex];
        var label = prod.Lhs + " ::= " + string.Join(" ", prod.Rhs.Take(dot).Select(SymName)) + " • " +
                    string.Join(" ", prod.Rhs.Skip(dot).Select(SymName));
        var node = new InterimSppfNode
        {
            NodeId = _nextNodeId++,
            Start = start,
            End = end,
            CoordinateBasis = "token-cursor[0..n]",
            ProductionIndex = prodIndex,
            Dot = dot,
            Label = label.Replace("  ", " ").Trim()
        };
        RegisterNode(node);
        return node;
    }

    private void RegisterNode(SppfNode node)
    {
        _nextNodeId = node.NodeId + 1 > _nextNodeId ? node.NodeId + 1 : _nextNodeId;
        _forest.Nodes.Add(node);
        if (_forest.Nodes.Count > _budget.MaxNodes)
        {
            _budgetExhausted = true;
            _budgetSubject ??= $"node:N{node.NodeId}";
        }
    }

    private static string SymName(GrammarSymbol s) => s is NonterminalSym n ? n.Name : ((TerminalSym)s).Name;

    private bool AddFamily(SppfNode parent, int prodIndex, int dot, int pivot, SppfNode? left, SppfNode right)
    {
        var families = parent switch
        {
            SymbolSppfNode s => s.Families,
            InterimSppfNode i => i.Families,
            _ => throw new InvalidOperationException("terminal nodes cannot carry packed nodes")
        };
        var leftId = left?.NodeId;
        var rightId = right?.NodeId;
        if (families.Any(f => f.ProductionIndex == prodIndex && f.Dot == dot && f.Pivot == pivot &&
                              f.LeftChildNodeId == leftId && f.RightChildNodeId == rightId))
            return false;
        if (_packedCreated + 1 > _budget.MaxPacked)
        {
            _budgetExhausted = true;
            _budgetSubject ??= $"packed:P{prodIndex}d{dot}@N{parent.NodeId}";
            return false;
        }
        var packed = new PackedNode
        {
            PackedId = _nextPackedId++,
            ParentNodeId = parent.NodeId,
            ProductionIndex = prodIndex,
            Dot = dot,
            Pivot = pivot,
            LeftChildNodeId = leftId,
            RightChildNodeId = rightId
        };
        families.Add(packed);
        _forest.PackedNodes.Add(packed);
        _packedCreated++;
        return true;
    }

    private void MarkCycles()
    {
        var byId = _forest.Nodes.ToDictionary(n => n.NodeId);
        var edges = new Dictionary<int, List<int>>();
        var packedOfEdge = new Dictionary<(int, int), int>();
        foreach (var p in _forest.PackedNodes)
        {
            foreach (var child in new[] { p.LeftChildNodeId, p.RightChildNodeId })
            {
                if (child is null) continue;
                edges.TryAdd(p.ParentNodeId, new List<int>());
                edges[p.ParentNodeId].Add(child.Value);
                packedOfEdge[(p.ParentNodeId, child.Value)] = p.PackedId;
            }
        }
        var state = new Dictionary<int, int>();
        var stack = new Stack<int>();
        void Dfs(int node)
        {
            state[node] = 1;
            stack.Push(node);
            if (edges.TryGetValue(node, out var nexts))
                foreach (var next in nexts.Distinct())
                {
                    if (!state.TryGetValue(next, out var st)) Dfs(next);
                    else if (st == 1 && packedOfEdge.TryGetValue((node, next), out var packedId))
                    {
                        var packed = _forest.PackedNodes.First(pn => pn.PackedId == packedId);
                        packed.CycleMark = true;
                        packed.CycleReason = "edge closes a derivation cycle; traversal terminates via per-column worklist dedup";
                    }
                }
            stack.Pop();
            state[node] = 2;
        }
        foreach (var id in byId.Keys.OrderBy(x => x))
            if (!state.ContainsKey(id)) Dfs(id);
    }
}
