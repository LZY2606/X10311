using System;
using System.Collections.Generic;
using System.Linq;
using SppfLab.Core.Diagnostics;
using SppfLab.Core.Models;

namespace SppfLab.Core.Parsing;

/// <summary>
/// Earley recognizer with a binarized Shared Packed Parse Forest
/// (Scott &amp; Johnstone style). Termination is explicit:
///  * each Earley set uses a work-list with identity-based de-duplication,
///    and the number of distinct item identities is finite;
///  * the main loop processes n+1 sets for n tokens.
/// Left recursion and nullable (epsilon) productions terminate through the
/// fixed-point work-list closure. A forest budget bounds node/packed growth
/// and is reported as a status instead of silently dropping derivations.
/// </summary>
public sealed class EarleyParser
{
    private readonly Grammar _grammar;
    private readonly ParseOptions _options;
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly Dictionary<string, SppfNode> _nodes = new();
    private readonly Dictionary<string, TerminalNode> _terminals = new();

    // Node registries per kind keyed by content identity.
    private readonly Dictionary<string, SymbolNode> _symbolNodes = new();
    private readonly Dictionary<string, IntermediateNode> _interNodes = new();

    private int _packedCount;
    private bool _budgetHit;

    private sealed class Item
    {
        public Production Production = null!;
        public int Dot;
        public int Origin;
        public SppfNode? Node;
        public string Key = "";
    }

    public EarleyParser(Grammar grammar, ParseOptions? options = null)
    {
        _grammar = grammar;
        _options = options ?? new ParseOptions();
    }

    public ParseResult Parse(IReadOnlyList<Token> tokens)
    {
        _diagnostics.Clear();
        if (tokens.Count == 0)
        {
            _diagnostics.Add(new Diagnostic(DiagKind.EmptyInput, DiagSeverity.Info,
                "Empty token stream: only epsilon/nullable derivations can succeed.",
                tokenStart: 0, tokenEnd: 0));
        }

        var sets = new List<HashSet<Item>> { new() };
        var queues = new List<Queue<Item>> { new() };

        // Seed S0 with start productions.
        foreach (var production in Head(_grammar.StartSymbol))
            AddPredict(sets[0], queues[0], production, 0);

        for (int k = 0; k <= tokens.Count; k++)
        {
            // Closure of set k (predict + complete) until the work-list drains.
            DrainClosure(sets[k], queues[k], sets, k);

            if (k == tokens.Count)
                break;

            // Scanner: set k -> set k+1 over the next terminal.
            sets.Add(new HashSet<Item>());
            queues.Add(new Queue<Item>());
            Scan(sets[k], queues[k + 1], tokens[k]);
        }

        int last = tokens.Count;
        var root = _symbolNodes.Values
            .FirstOrDefault(n => n.Symbol == _grammar.StartSymbol
                && n.LeftExtent == 0 && n.RightExtent == last
                && n.Packed.Count > 0);

        ComputeAmbiguityAndStats(tokens, out var stats, out var derivations);

        ParseStatus status;
        if (_budgetHit)
            status = ParseStatus.BudgetExhausted;
        else if (root is null)
        {
            status = ParseStatus.Rejected;
            _diagnostics.Add(new Diagnostic(DiagKind.NoParse, DiagSeverity.Error,
                tokens.Count == 0
                    ? "Start symbol has no derivation of the empty input."
                    : $"No complete parse of {tokens.Count} tokens.",
                tokenStart: 0, tokenEnd: tokens.Count));
        }
        else if (stats.AmbiguousNodes > 0 || stats.DerivationCount > 1)
            status = ParseStatus.Ambiguous;
        else
            status = ParseStatus.Parsed;

        if (tokens.Count > 0 && root is null)
        {
            var expected = sets[last].SelectMany(i => i.Dot < i.Production.RHS.Count
                ? new[] { i.Production.RHS[i.Dot].ToString()! }
                : Array.Empty<string>()).Distinct().Take(8);
            _diagnostics.Add(new Diagnostic(DiagKind.UnexpectedToken, DiagSeverity.Error,
                $"At token {last}: expected {string.Join(", ", expected.DefaultIfEmpty("end-of-input"))}.",
                tokenStart: last, tokenEnd: last + 1,
                data: new Dictionary<string, string> { ["set"] = last.ToString() }));
        }

        return new ParseResult(status, root, _nodes, _diagnostics, stats, derivations);
    }

    private void DrainClosure(HashSet<Item> set, Queue<Item> queue, List<HashSet<Item>> sets, int k)
    {
        int seen = 0;
        while (queue.Count > 0)
        {
            if (++seen > _options.ItemBudget)
            {
                HitBudget("item", k);
                return;
            }

            var item = queue.Dequeue();
            var rhs = item.Production.RHS;

            if (item.Dot < rhs.Count && !rhs[item.Dot].IsTerminal)
            {
                // PREDICT
                var symbol = rhs[item.Dot].Name;
                foreach (var production in Head(symbol))
                    AddPredict(set, queue, production, k);
            }
            else if (item.Dot == rhs.Count)
            {
                // COMPLETE
                Complete(set, queue, sets, item, k);
            }
        }
    }

    private void AddPredict(HashSet<Item> set, Queue<Item> queue, Production production, int k)
    {
        var item = new Item
        {
            Production = production,
            Dot = 0,
            Origin = k,
            Node = null,
            Key = $"{production.Id}|0|{k}"
        };
        if (set.Add(item))
            queue.Enqueue(item);
    }

    private void Complete(
        HashSet<Item> set, Queue<Item> queue,
        List<HashSet<Item>> sets, Item finished, int k)
    {
        // finished: X ::= alpha .  (origin, k), node is the parse of alpha
        var head = finished.Production.Nonterminal;
        var symbolNode = GetOrCreateSymbolNode(head, finished.Origin, k, productionForWitness: finished.Production);

        // Packed alternative: symbol node <- alpha via production.
        // Alpha is already accumulated in finished.Node (intermediate/terminal/null).
        AddPacked(symbolNode, finished.Production.Id, finished.Origin,
            left: null, right: finished.Node);

        var origin = sets[finished.Origin];
        // Advance every item in the origin set waiting on this head.
        foreach (var waiter in origin.ToList())
        {
            var wrhs = waiter.Production.RHS;
            if (waiter.Dot >= wrhs.Count || wrhs[waiter.Dot].IsTerminal || wrhs[waiter.Dot].Name != head)
                continue;

            int newDot = waiter.Dot + 1;
            SppfNode? newNode;

            if (newDot == wrhs.Count)
            {
                // Final symbol: parse of whole RHS = waiter.Node + X
                if (waiter.Node is null)
                    newNode = symbolNode;
                else
                {
                    var inter = GetOrCreateIntermediate(waiter.Production, newDot, waiter.Origin, k);
                    AddPacked(inter, waiter.Production.Id, finished.Origin, waiter.Node, symbolNode);
                    newNode = inter;
                }
            }
            else
            {
                var inter = GetOrCreateIntermediate(waiter.Production, newDot, waiter.Origin, k);
                AddPacked(inter, waiter.Production.Id, finished.Origin, waiter.Node, symbolNode);
                newNode = inter;
            }

            var next = new Item
            {
                Production = waiter.Production,
                Dot = newDot,
                Origin = waiter.Origin,
                Node = newNode,
                Key = $"{waiter.Production.Id}|{newDot}|{waiter.Origin}"
            };
            if (set.Add(next))
                queue.Enqueue(next);
        }
    }

    private void Scan(HashSet<Item> from, Queue<Item> nextQueue, Token token)
    {
        var terminalNode = GetOrCreateTerminal(token);
        foreach (var item in from)
        {
            var rhs = item.Production.RHS;
            if (item.Dot >= rhs.Count || !rhs[item.Dot].IsTerminal || rhs[item.Dot].Name != token.Kind)
                continue;

            int newDot = item.Dot + 1;
            SppfNode newNode;
            if (newDot == rhs.Count && item.Node is null)
            {
                newNode = terminalNode;
            }
            else
            {
                var inter = GetOrCreateIntermediate(item.Production, newDot, item.Origin, token.Index + 1);
                AddPacked(inter, item.Production.Id, token.Index, item.Node, terminalNode);
                newNode = inter;
            }

            nextQueue.Enqueue(new Item
            {
                Production = item.Production,
                Dot = newDot,
                Origin = item.Origin,
                Node = newNode,
                Key = $"{item.Production.Id}|{newDot}|{item.Origin}"
            });
        }
    }

    private List<Production> Head(string symbol)
    {
        var list = new List<Production>();
        if (_grammar.ByHead.TryGetValue(symbol, out var prods))
        {
            list.AddRange(prods);
            if (prods.Count > 1)
            {
                _diagnostics.Add(new Diagnostic(DiagKind.DuplicateProduction, DiagSeverity.Info,
                    $"Nonterminal '{symbol}' has {prods.Count} productions: {string.Join(", ", prods.Select(p => p.Id))}.",
                    data: new Dictionary<string, string>
                    {
                        ["nonterminal"] = symbol,
                        ["productionIds"] = string.Join(",", prods.Select(p => p.Id))
                    }));
            }
        }
        return list;
    }

    private void AddPacked(SppfNode parent, string productionId, int pivot, SppfNode? left, SppfNode? right)
    {
        string id = $"pk:{parent.Id}:{productionId}@{pivot}";
        if (parent.Packed.Any(p => p.Id == id))
            return;
        if (_packedCount >= _options.PackedBudget)
        {
            HitBudget("packed", parent.RightExtent);
            return;
        }
        var packed = new PackedNode(id, productionId, pivot, left, right);
        parent.Packed.Add(packed);
        _packedCount++;
    }

    private SymbolNode GetOrCreateSymbolNode(string symbol, int i, int j, Production? productionForWitness = null)
    {
        string id = $"sym:{symbol}[{i},{j}]";
        if (_symbolNodes.TryGetValue(id, out var existing))
            return existing;
        if (_nodes.Count >= _options.NodeBudget)
        {
            HitBudget("node", j);
            // Return a transient, unregistered node so the algorithm proceeds;
            // budget status makes the result non-authoritative.
            var transient = new SymbolNode(id, symbol, i, j);
            return transient;
        }
        var node = new SymbolNode(id, symbol, i, j);
        _symbolNodes[id] = node;
        _nodes[id] = node;
        return node;
    }

    private IntermediateNode GetOrCreateIntermediate(Production production, int dot, int i, int j)
    {
        string id = $"int:{production.Id}:{dot}[{i},{j}]";
        if (_interNodes.TryGetValue(id, out var existing))
            return existing;
        if (_nodes.Count >= _options.NodeBudget)
        {
            HitBudget("node", j);
            return new IntermediateNode(id, production.Id, dot, i, j);
        }
        var node = new IntermediateNode(id, production.Id, dot, i, j);
        _interNodes[id] = node;
        _nodes[id] = node;
        return node;
    }

    private TerminalNode GetOrCreateTerminal(Token token)
    {
        string id = $"term:{token.Kind}[{token.Index}]";
        if (_terminals.TryGetValue(id, out var existing))
            return existing;
        var node = new TerminalNode(id, token.Kind, token.Index);
        _terminals[id] = node;
        _nodes[id] = node;
        return node;
    }

    private void HitBudget(string which, int position)
    {
        if (_budgetHit)
            return;
        _budgetHit = true;
        _diagnostics.Add(new Diagnostic(DiagKind.BudgetExhausted, DiagSeverity.Error,
            $"Forest {which} budget exhausted at token set {position}; derivations may be missing. " +
            "The partial forest is retained rather than silently truncated into a single answer.",
            tokenStart: position, tokenEnd: position + 1,
            data: new Dictionary<string, string>
            {
                ["resource"] = which,
                ["nodeBudget"] = _options.NodeBudget.ToString(),
                ["packedBudget"] = _options.PackedBudget.ToString()
            }));
    }

    private void ComputeAmbiguityAndStats(IReadOnlyList<Token> tokens, out ForestStats stats,
        out List<DerivationTree> derivations)
    {
        stats = new ForestStats
        {
            SymbolNodes = _symbolNodes.Count,
            IntermediateNodes = _interNodes.Count,
            TerminalNodes = _terminals.Count,
            PackedNodes = _packedCount,
        };

        foreach (var node in _nodes.Values)
        {
            if (node.Packed.Count > 1)
            {
                stats.AmbiguousNodes++;
                _diagnostics.Add(new Diagnostic(DiagKind.Ambiguity, DiagSeverity.Warning,
                    $"Ambiguous {node.Kind.ToString().ToLowerInvariant()} node spans tokens [{node.LeftExtent},{node.RightExtent}] " +
                    $"with {node.Packed.Count} packed alternatives: {string.Join(", ", node.Packed.Select(p => p.ProductionId))}.",
                    tokenStart: node.LeftExtent, tokenEnd: node.RightExtent, nodeId: node.Id,
                    data: new Dictionary<string, string>
                    {
                        ["nodeId"] = node.Id,
                        ["packedIds"] = string.Join(",", node.Packed.Select(p => p.Id)),
                        ["productionIds"] = string.Join(",", node.Packed.Select(p => p.ProductionId))
                    }));
            }
        }

        var roots = _symbolNodes.Values
            .Where(n => n.Symbol == _grammar.StartSymbol && n.LeftExtent == 0 && n.RightExtent == tokens.Count
                        && n.Packed.Count > 0)
            .ToList();
        var root = roots.FirstOrDefault();

        derivations = new List<DerivationTree>();
        if (root is not null)
        {
            var (trees, truncated) = DerivationEnumerator.Enumerate(root, _options.DerivationBudget);
            derivations = trees;
            stats.DerivationsTruncated = truncated;
            stats.DerivationCount = truncated ? _options.DerivationBudget : trees.Count;
        }
    }
}
