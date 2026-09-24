using System.Text.Json.Serialization;

namespace ForestChamber.Core;

public sealed record Diagnostic(
    string Code,
    string Severity,
    string Message,
    string? SubjectId = null,
    IReadOnlyDictionary<string, string>? Coords = null);

public sealed record TokenRef(
    int Index,
    string Kind,
    string Text,
    int StartOffset,
    int EndOffset,
    string RawRepresentation,
    string OffsetUnit = "utf16-codeunit",
    IReadOnlyList<Diagnostic>? Diagnostics = null);

public abstract record GrammarSymbol(string Name);
public sealed record NonterminalSym(string Name) : GrammarSymbol(Name);
public sealed record TerminalSym(string Name) : GrammarSymbol(Name);

public sealed class ProductionDef
{
    public required string ProdId { get; init; }
    public required string Lhs { get; init; }
    public required IReadOnlyList<GrammarSymbol> Rhs { get; init; }
    public required int SourceLine { get; init; }
    public required int AlternativeOrdinal { get; init; }
    public required string SourceText { get; init; }
    public bool IsDuplicate { get; set; }
    public string? DuplicateOfProdId { get; set; }
}

public sealed class GrammarDoc
{
    public required IReadOnlyList<ProductionDef> Productions { get; init; }
    public required string StartSymbol { get; init; }
    public required string RawText { get; init; }
    public IReadOnlySet<string> Nonterminals { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Terminals { get; init; } = new HashSet<string>();
}

public sealed class PrecedenceRule
{
    public required string RuleId { get; init; }
    public required int SourceLine { get; init; }
    public required int Level { get; init; }
    public required string Associativity { get; init; } // left | right | nonassoc
    public required string OperatorTerminal { get; init; }
    public required string SourceText { get; init; }
}

public sealed class PrecedenceDoc
{
    public required IReadOnlyList<PrecedenceRule> Rules { get; init; }
    public required string RawText { get; init; }
    public required IReadOnlyList<PrecedenceConflict> Conflicts { get; init; }
}

public sealed record PrecedenceConflict(string RuleIdA, string RuleIdB, string OperatorTerminal, string Reason);

public enum SppfNodeKind { Terminal, Symbol, Interim }

public sealed class PackedNode
{
    public required int PackedId { get; init; }
    public required int ParentNodeId { get; init; }
    public required int ProductionIndex { get; init; }
    public required int Dot { get; init; }
    public required int Pivot { get; init; }
    public int? LeftChildNodeId { get; init; }
    public int? RightChildNodeId { get; init; }
    public bool CycleMark { get; set; }
    public string? CycleReason { get; set; }
}

public abstract class SppfNode
{
    public required int NodeId { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
    public required string CoordinateBasis { get; init; } // token-cursor index basis
    public abstract SppfNodeKind Kind { get; }
}

public sealed class TerminalSppfNode : SppfNode
{
    public required int TokenIndex { get; init; }
    public required string Terminal { get; init; }
    public override SppfNodeKind Kind => SppfNodeKind.Terminal;
}

public sealed class SymbolSppfNode : SppfNode
{
    public required string Symbol { get; init; }
    public required bool IsNonterminal { get; init; }
    public List<PackedNode> Families { get; } = new();
    public override SppfNodeKind Kind => SppfNodeKind.Symbol;
}

public sealed class InterimSppfNode : SppfNode
{
    public required int ProductionIndex { get; init; }
    public required int Dot { get; init; }
    public required string Label { get; init; }
    public List<PackedNode> Families { get; } = new();
    public override SppfNodeKind Kind => SppfNodeKind.Interim;
}

public sealed class SppfForest
{
    public List<SppfNode> Nodes { get; } = new();
    public List<PackedNode> PackedNodes { get; } = new();
    public int? RootNodeId { get; set; }
    public required string StartSymbol { get; init; }
    public required string CoordinateBasis { get; init; }
}

public sealed class DerivationTree
{
    public required string TreeId { get; init; }
    public required int RootNodeId { get; init; }
    public required TreeLabel Root { get; init; }
    public bool CycleCut { get; init; }
}

public sealed class TreeLabel
{
    public required string Text { get; init; }
    public required int NodeId { get; init; }
    public int? PackedId { get; init; }
    public int? ProductionIndex { get; init; }
    public bool IsInterim { get; init; }
    public List<TreeLabel> Children { get; } = new();
    public bool CycleCut { get; init; }
    public IReadOnlyList<(int TokenIndex, int StartOffset, int EndOffset, string Unit)> Span { get; init; }
        = Array.Empty<(int, int, int, string)>();
}

public enum CandidateVerdict { Accepted, Rejected, UnresolvedConflict, NotApplicableRules }

public sealed class CandidateReview
{
    public required string TreeId { get; init; }
    public required bool Kept { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
    public string? RuleId { get; init; }
    public string? RuleVersionId { get; init; }
}

public enum RunStatus { Success, Ambiguous, BudgetExhausted, ParseError, EmptyAccepted, Truncated }

public sealed class ParseRunResult
{
    public required string RunId { get; init; }
    public required RunStatus Status { get; init; }
    public SppfForest? Forest { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<DerivationTree> Trees { get; init; }
    public required IReadOnlyList<CandidateReview> Reviews { get; init; }
    public required IReadOnlyDictionary<string, string> Metrics { get; init; }
}
