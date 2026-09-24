using System.Security.Cryptography;
using System.Text;

namespace AmbiguityForest.Core;

/// <summary>
/// Earley chart parser (Aycock-Horspool nullable handling) plus shared packed
/// parse forest extraction.
///
/// Termination strategies (explicit, recorded as events):
///  - chart construction is a fixed-point over a finite state set
///    (|productions| x (maxRhs+1) x (n+1) origins per cell), so left recursion
///    cannot loop;
///  - forest extraction recurses only on strictly smaller spans; zero-width
///    recursion (nullable cycles) hits an on-stack guard and yields a
///    CycleNode instead of unfolding forever;
///  - node creation is bounded by a budget measured in forest-nodes; on
///    exhaustion the partial forest is kept and the status is BudgetExhausted.
/// </summary>
public sealed class EarleyParser
{
    private readonly record struct State(int Prod, int Dot, int Origin);

    private sealed class BudgetExceeded : Exception { }

    private Grammar _g = null!;
    private List<Production> _prods = null!; // index 0: augmented start
    private Dictionary<string, List<int>> _byLhs = null!;
    private HashSet<string> _nonterminals = null!;
    private HashSet<string> _terminals = null!;
    private HashSet<string> _nullable = null!;
    private IReadOnlyList<Token> _tokens = null!;
    private int _budget;
    private Forest _forest = null!;
    private List<PendingEvent> _events = null!;
    private readonly Dictionary<string, SymbolNode> _symbolMemo = new();
    private readonly Dictionary<string, PackedNode> _packedMemo = new();
    private readonly Dictionary<string, TokenNode> _tokenMemo = new();
    private readonly Dictionary<string, EpsilonNode> _epsilonMemo = new();
    private readonly HashSet<(string Sym, int I, int J)> _onStack = new();
    private readonly HashSet<string> _cyclesReported = new();
    private HashSet<(string Sym, int I, int J)> _completed = null!;

    public const string AugmentedLhs = "__accept__";

    public ParseResult Parse(Grammar grammar, IReadOnlyList<Token> tokens, string startSymbol, int budget, string scenario)
    {
        _g = grammar;
        _tokens = tokens;
        _budget = budget;
        _events = new List<PendingEvent>();
        _nonterminals = GrammarAnalysis.Nonterminals(grammar);
        _terminals = grammar.Terminals.ToHashSet();
        (_nullable, var nullableEvents) = GrammarAnalysis.ComputeNullable(grammar);
        _events.AddRange(nullableEvents);
        _events.AddRange(GrammarAnalysis.DetectLeftRecursion(grammar, _nullable));

        _prods = new List<Production> { new Production("__aug__", AugmentedLhs, new[] { startSymbol }, 1, "internal") };
        _prods.AddRange(grammar.Productions);
        _byLhs = _prods.Select((p, i) => (p, i)).GroupBy(x => x.p.Lhs)
            .ToDictionary(gr => gr.Key, gr => gr.Select(x => x.i).ToList());

        _forest = new Forest
        {
            Scenario = scenario,
            Budget = budget,
            Id = ForestId(grammar, tokens, startSymbol, budget, scenario)
        };

        var n = tokens.Count;
        var chart = new List<HashSet<State>>(n + 1);
        var queues = new List<Queue<State>>(n + 1);
        for (int i = 0; i <= n; i++) { chart.Add(new HashSet<State>()); queues.Add(new Queue<State>()); }

        void Add(int cell, State s)
        {
            if (chart[cell].Add(s)) queues[cell].Enqueue(s);
        }

        Add(0, new State(0, 0, 0));
        for (int i = 0; i <= n; i++)
        {
            while (queues[i].Count > 0)
            {
                var st = queues[i].Dequeue();
                var prod = _prods[st.Prod];
                if (st.Dot == prod.Rhs.Length)
                {
                    // complete: advance states in chart[st.Origin] waiting on prod.Lhs
                    foreach (var waiting in chart[st.Origin])
                    {
                        var wp = _prods[waiting.Prod];
                        if (waiting.Dot < wp.Rhs.Length && wp.Rhs[waiting.Dot] == prod.Lhs)
                            Add(i, new State(waiting.Prod, waiting.Dot + 1, waiting.Origin));
                    }
                }
                else
                {
                    var sym = prod.Rhs[st.Dot];
                    if (_terminals.Contains(sym))
                    {
                        if (i < n && tokens[i].Kind == sym)
                            Add(i + 1, new State(st.Prod, st.Dot + 1, st.Origin));
                    }
                    else if (_nonterminals.Contains(sym))
                    {
                        foreach (var pi in _byLhs[sym]) Add(i, new State(pi, 0, i));
                        if (_nullable.Contains(sym))
                            Add(i, new State(st.Prod, st.Dot + 1, st.Origin)); // nullable skip
                    }
                }
            }
        }

        _completed = new HashSet<(string, int, int)>();
        for (int i = 0; i <= n; i++)
            foreach (var st in chart[i])
            {
                var prod = _prods[st.Prod];
                if (st.Dot == prod.Rhs.Length && prod.Lhs != AugmentedLhs)
                    _completed.Add((prod.Lhs, st.Origin, i));
            }

        bool accepted = chart[n].Contains(new State(0, 1, 0));
        if (!accepted)
        {
            _forest.Status = ForestStatus.NoParse;
            _events.Add(new PendingEvent("parse-completed", new()
            {
                ["scenario"] = scenario, ["status"] = "no-parse",
                ["tokens"] = n, ["budget"] = budget, ["budgetUnit"] = "forest-node"
            }));
            return new ParseResult(_forest, _events);
        }

        try
        {
            var root = BuildSymbol(startSymbol, 0, n);
            _forest.RootId = root.Id;
            _forest.Status = ForestStatus.Complete;
        }
        catch (BudgetExceeded)
        {
            _forest.Status = ForestStatus.BudgetExhausted;
            _events.Add(new PendingEvent("budget-exhausted", new()
            {
                ["scenario"] = scenario,
                ["budget"] = budget,
                ["budgetUnit"] = "forest-node",
                ["consumed"] = _forest.BudgetUnitConsumed,
                ["frontier"] = _onStack.Select(s => $"{s.Sym}[{s.I},{s.J})").OrderBy(x => x).ToArray(),
                ["partialForestKept"] = true
            }));
        }

        _events.Add(new PendingEvent("parse-completed", new()
        {
            ["scenario"] = scenario,
            ["status"] = _forest.Status.ToString(),
            ["rootId"] = _forest.RootId,
            ["symbolNodes"] = _forest.Symbols.Count,
            ["packedNodes"] = _forest.Packed.Count,
            ["tokens"] = n,
            ["budget"] = budget,
            ["budgetUnit"] = "forest-node"
        }));
        return new ParseResult(_forest, _events);
    }

    private bool Derivative(string sym, int i, int j) =>
        _completed.Contains((sym, i, j)) || (i == j && _nonterminals.Contains(sym) && _nullable.Contains(sym));

    private SymbolNode BuildSymbol(string sym, int i, int j)
    {
        var key = $"{sym}:{i}:{j}";
        if (_symbolMemo.TryGetValue(key, out var existing)) return existing;

        if (_onStack.Contains((sym, i, j)))
        {
            // Explicit termination for zero-width (nullable) cycles: emit a cycle
            // node instead of unfolding. Positive-width recursion cannot reach
            // here because child spans are strictly smaller.
            var cycle = new CycleNode
            {
                Id = $"cyc:{sym}:{i}",
                Symbol = sym,
                Pos = i,
                Reason = "nullable-cycle terminated by on-stack guard"
            };
            if (_cyclesReported.Add(cycle.Id))
            {
                CountNode();
                _forest.Cycles.Add(cycle);
                _events.Add(new PendingEvent("nullable-cycle-terminated", new()
                {
                    ["symbol"] = sym, ["pos"] = i, ["cycleNodeId"] = cycle.Id,
                    ["strategy"] = "on-stack guard; derivation recorded as cycle node, forest sharing keeps other derivations intact"
                }));
            }
            // Return a synthetic symbol node with no packed alternatives; the
            // cycle node itself is the visible evidence.
            var stub = new SymbolNode { Id = $"sym:{sym}:{i}:{j}:cycle", Symbol = sym, Start = i, End = j };
            CountNode();
            _forest.Symbols.Add(stub);
            _symbolMemo[key] = stub;
            return stub;
        }

        _onStack.Add((sym, i, j));
        var node = new SymbolNode { Id = $"sym:{sym}:{i}:{j}", Symbol = sym, Start = i, End = j };
        CountNode();
        _forest.Symbols.Add(node);
        _symbolMemo[key] = node; // memoize before recursion: sharing, no derivation loss

        foreach (var prod in _g.Productions.Where(p => p.Lhs == sym))
        {
            if (prod.Rhs.Length == 0)
            {
                var eps = GetOrAddEpsilon(sym, i);
                var packed0 = GetOrAddPacked(prod, i, j, new List<string> { eps.Id });
                if (!node.PackedIds.Contains(packed0.Id)) node.PackedIds.Add(packed0.Id);
                continue;
            }
            foreach (var children in Splits(prod.Rhs, 0, i, j))
            {
                var childIds = children.Select(c => c.Id).ToList();
                var packed = GetOrAddPacked(prod, i, j, childIds);
                if (!node.PackedIds.Contains(packed.Id)) node.PackedIds.Add(packed.Id);
            }
        }
        _onStack.Remove((sym, i, j));
        return node;
    }

    private IEnumerable<List<object>> Splits(string[] rhs, int pos, int i, int j)
    {
        if (pos == rhs.Length)
        {
            if (i == j) yield return new List<object>();
            yield break;
        }
        var sym = rhs[pos];
        if (_terminals.Contains(sym))
        {
            if (i < j && i < _tokens.Count && _tokens[i].Kind == sym)
            {
                var tokNode = GetOrAddToken(_tokens[i], i);
                foreach (var rest in Splits(rhs, pos + 1, i + 1, j))
                {
                    var list = new List<object> { tokNode };
                    list.AddRange(rest);
                    yield return list;
                }
            }
            yield break;
        }
        if (!_nonterminals.Contains(sym)) yield break;
        for (int k = i; k <= j; k++)
        {
            if (!Derivative(sym, i, k)) continue;
            foreach (var rest in Splits(rhs, pos + 1, k, j))
            {
                var child = BuildSymbol(sym, i, k);
                var list = new List<object> { child };
                list.AddRange(rest);
                yield return list;
            }
        }
    }

    private EpsilonNode GetOrAddEpsilon(string sym, int pos)
    {
        var key = $"{sym}:{pos}";
        if (_epsilonMemo.TryGetValue(key, out var existing)) return existing;
        var node = new EpsilonNode { Id = $"eps:{sym}:{pos}", Symbol = sym, Pos = pos };
        CountNode();
        _forest.Epsilons.Add(node);
        _epsilonMemo[key] = node;
        return node;
    }

    private TokenNode GetOrAddToken(Token token, int pos)
    {
        if (_tokenMemo.TryGetValue(token.Id, out var existing)) return existing;
        var node = new TokenNode { Id = $"tok:{token.Id}", TokenId = token.Id, Start = pos, End = pos + 1 };
        CountNode();
        _forest.Tokens.Add(node);
        _tokenMemo[token.Id] = node;
        return node;
    }

    private PackedNode GetOrAddPacked(Production prod, int i, int j, List<string> childIds)
    {
        var childKey = string.Join(",", childIds);
        var id = $"pk:{prod.Id}:{i}:{j}:{ShortHash(childKey)}";
        if (_packedMemo.TryGetValue(id, out var existing)) return existing;
        var node = new PackedNode { Id = id, ProductionId = prod.Id, Start = i, End = j, ChildIds = childIds };
        CountNode();
        _forest.Packed.Add(node);
        _packedMemo[id] = node;
        return node;
    }

    private void CountNode()
    {
        _forest.BudgetUnitConsumed++;
        if (_forest.BudgetUnitConsumed > _budget) throw new BudgetExceeded();
    }

    internal static string ShortHash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string ForestId(Grammar g, IReadOnlyList<Token> tokens, string start, int budget, string scenario)
    {
        var basis = string.Join("|", g.Id, g.Version, start, budget, scenario,
            string.Join(",", tokens.Select(t => $"{t.Id}:{t.Kind}:{t.Span.Start}:{t.Span.End}")));
        return $"forest:{ShortHash(basis)}";
    }
}

public sealed record ParseResult(Forest Forest, List<PendingEvent> Events);
