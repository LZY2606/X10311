namespace AmbiguityForest.Core;

/// <summary>A token span. Unit declares the coordinate basis (e.g. "utf16-code-unit").</summary>
public sealed record TokenSpan(int Start, int End, string Unit, int Line, int Col);

public sealed record Token(
    string Id,
    string Kind,
    string Text,
    TokenSpan Span,
    string? RawRepresentation = null,
    string? Diagnostic = null);

public sealed record Production(string Id, string Lhs, string[] Rhs, int Version, string Origin);

public sealed record Grammar(string Id, string Start, Production[] Productions, string[] Terminals, int Version);

/// <summary>
/// A precedence/associativity rule. Rules only filter applicable candidates;
/// they never rewrite grammar evidence. Version is bumped on every revision.
/// </summary>
public sealed record PrecedenceRule(
    string Id,
    int Version,
    string Kind,          // "precedence" | "associativity"
    string? Higher,       // production id (precedence)
    string? Lower,        // production id (precedence)
    string? Production,   // production id (associativity)
    string? Assoc,        // "left" | "right"
    string AppliesTo,     // symbol or "*"
    string Origin);

public sealed class FilterMark
{
    public string RuleId { get; set; } = "";
    public int RuleVersion { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class SymbolNode
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public List<string> PackedIds { get; set; } = new();
}

public sealed class PackedNode
{
    public string Id { get; set; } = "";
    public string ProductionId { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public List<string> ChildIds { get; set; } = new();
    public List<FilterMark> FilteredBy { get; set; } = new();
    public bool Conflicted { get; set; }
}

public sealed class TokenNode
{
    public string Id { get; set; } = "";
    public string TokenId { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
}

public sealed class EpsilonNode
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Pos { get; set; }
}

public sealed class CycleNode
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Pos { get; set; }
    public string Reason { get; set; } = "";
}

public enum ForestStatus { Complete, BudgetExhausted, NoParse }

public sealed class Forest
{
    public string Id { get; set; } = "";
    public string Scenario { get; set; } = "";
    public ForestStatus Status { get; set; } = ForestStatus.Complete;
    public int Budget { get; set; }
    public int BudgetUnitConsumed { get; set; }
    public string? RootId { get; set; }
    public List<SymbolNode> Symbols { get; set; } = new();
    public List<PackedNode> Packed { get; set; } = new();
    public List<TokenNode> Tokens { get; set; } = new();
    public List<EpsilonNode> Epsilons { get; set; } = new();
    public List<CycleNode> Cycles { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();

    private Dictionary<string, SymbolNode>? _symbolIndex;
    private Dictionary<string, PackedNode>? _packedIndex;

    public void Reindex()
    {
        _symbolIndex = Symbols.ToDictionary(s => s.Id);
        _packedIndex = Packed.ToDictionary(p => p.Id);
    }

    public SymbolNode Symbol(string id) => (_symbolIndex ??= Symbols.ToDictionary(s => s.Id))[id];
    public PackedNode PackedOf(string id) => (_packedIndex ??= Packed.ToDictionary(p => p.Id))[id];
    public IEnumerable<PackedNode> AlternativesOf(SymbolNode node) => node.PackedIds.Select(PackedOf);
    public bool IsFiltered(PackedNode p) => p.FilteredBy.Count > 0 && !p.Conflicted;
    public IEnumerable<PackedNode> Surviving(SymbolNode node) => AlternativesOf(node).Where(p => !IsFiltered(p));
}

public sealed record PendingEvent(string Kind, Dictionary<string, object?> Payload);

public sealed record Decision(
    string Id,
    string NodeId,
    string PackedId,
    string Action,   // "accept" | "reject"
    string User,
    string BaseVersion);
