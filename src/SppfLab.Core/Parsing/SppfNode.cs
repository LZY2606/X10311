using System.Collections.Generic;
using System.Linq;

namespace SppfLab.Core.Parsing;

public enum SppfNodeKind { Symbol, Intermediate, Terminal }

/// <summary>
/// Base class for Shared Packed Parse Forest nodes. Node identities are
/// content-addressed by the parser (kind/symbol-or-dot/(i,j)) and stay
/// stable across replays of the same inputs, so a forest node reliably
/// names the same piece of syntactic evidence.
/// </summary>
public abstract class SppfNode
{
    protected SppfNode(string id, int leftExtent, int rightExtent)
    {
        Id = id;
        LeftExtent = leftExtent;
        RightExtent = rightExtent;
    }

    public string Id { get; }
    public abstract SppfNodeKind Kind { get; }
    public int LeftExtent { get; }
    public int RightExtent { get; }

    /// <summary>Packed alternatives (ambiguity = more than one child slot).</summary>
    public List<PackedNode> Packed { get; } = new();

    public bool IsAmbiguous => Packed.Count > 1;
}

/// <summary>A symbol node: a nonterminal spanning [i,j].</summary>
public sealed class SymbolNode : SppfNode
{
    public SymbolNode(string id, string symbol, int i, int j) : base(id, i, j)
        => Symbol = symbol;

    public string Symbol { get; }
    public override SppfNodeKind Kind => SppfNodeKind.Symbol;
}

/// <summary>A terminal node spanning exactly one token [i,i+1].</summary>
public sealed class TerminalNode : SppfNode
{
    public TerminalNode(string id, string kind, int index) : base(id, index, index + 1)
    {
        KindName = kind;
        Index = index;
    }

    public string KindName { get; }
    public int Index { get; }
    public override SppfNodeKind Kind => SppfNodeKind.Terminal;
}

/// <summary>
/// An intermediate/dotted-rule node (production head, dot position, span).
/// Identity uses the production id plus dot position, preserving the
/// original production identity through binarization.
/// </summary>
public sealed class IntermediateNode : SppfNode
{
    public IntermediateNode(string id, string productionId, int dot, int i, int j)
        : base(id, i, j)
    {
        ProductionId = productionId;
        Dot = dot;
    }

    public string ProductionId { get; }
    public int Dot { get; }
    public override SppfNodeKind Kind => SppfNodeKind.Intermediate;
}

/// <summary>
/// One packed alternative under a forest node. A packed node corresponds to
/// one way of deriving its parent, named by production identity (and pivot
/// for intermediates). Its two children are the shared left/right pieces.
/// A null child means an epsilon/missing piece (no forest node created for
/// zero-width prefixes).
/// </summary>
public sealed class PackedNode
{
    public PackedNode(string id, string productionId, int pivot, SppfNode? left, SppfNode? right)
    {
        Id = id;
        ProductionId = productionId;
        Pivot = pivot;
        Left = left;
        Right = right;
    }

    public string Id { get; }
    public string ProductionId { get; }
    public int Pivot { get; }
    public SppfNode? Left { get; }
    public SppfNode? Right { get; }

    /// <summary>Filter verdicts from precedence passes; node stays regardless.</summary>
    public List<FilterVerdict> Filters { get; } = new();

    public bool IsFilteredOut => Filters.Count > 0 && Filters.All(f => f.Outcome == FilterOutcome.Filtered);
    public bool HasConflictFilter => Filters.Any(f => f.Outcome == FilterOutcome.ConflictNoFilter);

    public IEnumerable<SppfNode> Children =>
        new[] { Left, Right }.Where(c => c is not null)!;
}

public enum FilterOutcome { Kept, Filtered, ConflictNoFilter }

/// <summary>Machine-readable explanation of why a candidate was/was not filtered.</summary>
public sealed class FilterVerdict
{
    public FilterVerdict(int policyVersion, FilterOutcome outcome, string reason, string? opTerminal = null)
    {
        PolicyVersion = policyVersion;
        Outcome = outcome;
        Reason = reason;
        OpTerminal = opTerminal;
    }

    public int PolicyVersion { get; }
    public FilterOutcome Outcome { get; }
    public string Reason { get; }
    public string? OpTerminal { get; }
}
