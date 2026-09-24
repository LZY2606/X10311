using System.Collections.Generic;
using System.Linq;

namespace SppfLab.Core.Models;

/// <summary>
/// An imported context-free grammar. Productions keep their original
/// identities and order. The grammar is nullable/left-recursion agnostic
/// here; those relations are derived (and versioned) by the analysis layer.
/// </summary>
public sealed class Grammar
{
    public Grammar(string startSymbol, IReadOnlyList<Production> productions, string? sourceText = null)
    {
        StartSymbol = startSymbol;
        Productions = productions;
        SourceText = sourceText;
    }

    public string StartSymbol { get; }

    public IReadOnlyList<Production> Productions { get; }

    /// <summary>Original grammar text exactly as imported.</summary>
    public string? SourceText { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<Production>> ByHead { get; } =
        productions
            .GroupBy(p => p.Nonterminal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Production>)g.ToList());

    public Production? Find(string id) => Productions.FirstOrDefault(p => p.Id == id);
}
