using System.Collections.Generic;
using SppfLab.Core.Diagnostics;

namespace SppfLab.Core.Parsing;

public enum ParseStatus { Parsed, Ambiguous, Rejected, BudgetExhausted }

public sealed class ForestStats
{
    public int SymbolNodes { get; set; }
    public int IntermediateNodes { get; set; }
    public int TerminalNodes { get; set; }
    public int PackedNodes { get; set; }
    public int AmbiguousNodes { get; set; }
    public int DerivationCount { get; set; }
    public bool DerivationsTruncated { get; set; }
    public int TotalNodes => SymbolNodes + IntermediateNodes + TerminalNodes;
}

public sealed class ParseResult
{
    public ParseResult(
        ParseStatus status,
        SppfNode? root,
        IReadOnlyDictionary<string, SppfNode> nodes,
        IReadOnlyList<Diagnostic> diagnostics,
        ForestStats stats,
        IReadOnlyList<DerivationTree> derivations)
    {
        Status = status;
        Root = root;
        Nodes = nodes;
        Diagnostics = diagnostics;
        Stats = stats;
        Derivations = derivations;
    }

    public ParseStatus Status { get; }
    public SppfNode? Root { get; }
    public IReadOnlyDictionary<string, SppfNode> Nodes { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public ForestStats Stats { get; }
    public IReadOnlyList<DerivationTree> Derivations { get; }
}
