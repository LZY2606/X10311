using System.Collections.Generic;
using System.Linq;

namespace SppfLab.Core.Models;

/// <summary>
/// A single production with a stable, user-supplied identity. Duplicate
/// productions (same left/right side) are kept as distinct identities so
/// original grammar evidence is never collapsed.
/// </summary>
public sealed class Production
{
    public Production(string id, string nonterminal, IReadOnlyList<Symbol> rhs, string? originLine = null)
    {
        Id = id;
        Nonterminal = nonterminal;
        RHS = rhs;
        OriginLine = originLine;
    }

    /// <summary>Stable identity, e.g. "p3". Preserved across imports/exports.</summary>
    public string Id { get; }

    public string Nonterminal { get; }

    public IReadOnlyList<Symbol> RHS { get; }

    /// <summary>Original textual line as imported; never normalized away.</summary>
    public string? OriginLine { get; }

    public bool IsEpsilon => RHS.Count == 0;

    public override string ToString() =>
        $"{Id}:{Nonterminal} -> {(RHS.Count == 0 ? "ε" : string.Join(" ", RHS))}";
}
