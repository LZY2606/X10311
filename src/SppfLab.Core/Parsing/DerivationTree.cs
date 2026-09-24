using System.Collections.Generic;
using System.Linq;

namespace SppfLab.Core.Parsing;

/// <summary>
/// A fully expanded parse tree read out of the shared forest. Every tree
/// node records the forest node and packed alternative it came from, so a
/// tree is a stable provenance path back into the DAG.
/// </summary>
public sealed class DerivationTree
{
    public DerivationTree(
        string id,
        SppfNode forestNode,
        PackedNode? via,
        IReadOnlyList<DerivationTree> children,
        string? terminalText = null,
        int? tokenIndex = null)
    {
        Id = id;
        ForestNode = forestNode;
        Via = via;
        Children = children;
        TerminalText = terminalText;
        TokenIndex = tokenIndex;
    }

    /// <summary>Stable tree id built from the packed-node choices on its path.</summary>
    public string Id { get; }
    public SppfNode ForestNode { get; }
    public PackedNode? Via { get; }
    public IReadOnlyList<DerivationTree> Children { get; }
    public string? TerminalText { get; }
    public int? TokenIndex { get; }

    public string Label => ForestNode switch
    {
        SymbolNode s => s.Symbol,
        IntermediateNode i => $"•{i.ProductionId}:{i.Dot}",
        TerminalNode t => $"'{t.KindName}'",
        _ => "?"
    };

    public int Left => ForestNode.LeftExtent;
    public int Right => ForestNode.RightExtent;
}

/// <summary>Enumerates complete derivation trees from a forest node.</summary>
public static class DerivationEnumerator
{
    private const int DepthLimit = 1000;

    public static (List<DerivationTree> Trees, bool Truncated) Enumerate(
        SppfNode node,
        int budget,
        bool includeFiltered = false)
    {
        bool truncated = false;
        var trees = Expand(node, "T", budget, includeFiltered, 0, ref truncated);
        return (trees, truncated);
    }

    private static List<DerivationTree> Expand(
        SppfNode current,
        string path,
        int budget,
        bool includeFiltered,
        int depth,
        ref bool truncated)
    {
        var output = new List<DerivationTree>();
        if (depth > DepthLimit)
            return output;

        if (current is TerminalNode terminal)
        {
            output.Add(new DerivationTree(
                path + $"/t{terminal.Index}", terminal, null,
                System.Array.Empty<DerivationTree>(),
                terminalText: terminal.KindName, tokenIndex: terminal.Index));
            return output;
        }

        var alternatives = current.Packed
            .Where(p => includeFiltered || !p.IsFilteredOut)
            .ToList();

        if (alternatives.Count == 0)
        {
            output.Add(new DerivationTree(path + "/dead", current, null,
                System.Array.Empty<DerivationTree>()));
            return output;
        }

        foreach (var packed in alternatives)
        {
            var childExpansions = new List<List<DerivationTree>>();
            bool failed = false;

            foreach (var child in packed.Children)
            {
                var kids = Expand(child, path + ">" + packed.Id, budget,
                    includeFiltered, depth + 1, ref truncated);
                if (kids.Count == 0)
                {
                    failed = true;
                    break;
                }
                childExpansions.Add(kids);
            }

            if (failed)
                continue;

            foreach (var combo in Cartesian(childExpansions))
            {
                if (output.Count >= budget)
                {
                    truncated = true;
                    return output;
                }
                output.Add(new DerivationTree(path + ">" + packed.Id, current, packed, combo));
            }
        }

        return output;
    }

    private static IEnumerable<List<T>> Cartesian<T>(List<List<T>> lists)
    {
        IEnumerable<List<T>> products = new List<List<T>> { new() };
        foreach (var list in lists)
        {
            var captured = list;
            products = products.SelectMany(
                acc => captured.Select(item => acc.Concat(new[] { item }).ToList()));
        }
        return products;
    }
}
