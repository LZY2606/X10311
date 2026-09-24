#nullable enable
namespace AmbiguityForestLab.Core;

public enum SppfNodeKind { Symbol, Intermediate, Terminal }

public sealed class SppfNode
{
    public required string Id { get; init; }
    public required SppfNodeKind Kind { get; init; }
    public string? Symbol { get; init; }
    public int LeftToken { get; init; }
    public int RightToken { get; init; }
    public string? ProductionId { get; init; }
    public int Dot { get; init; }
    public string? TokenId { get; init; }
    public List<PackedNode> Packed { get; } = new();
}

public sealed class PackedNode
{
    public required string Id { get; init; }
    public required string ParentId { get; init; }
    public required string ProductionId { get; init; }
    public required int Pivot { get; init; }
    public required IReadOnlyList<string> ChildIds { get; init; }
    public bool Filtered { get; set; }
    public string? FilterReason { get; set; }
    public string? PrecedenceRuleId { get; set; }
    public string? ChosenTopOperator { get; set; }
    public string? LeftTopOperator { get; set; }
    public string? RightTopOperator { get; set; }
}

public sealed class ForestGraph
{
    public readonly Dictionary<string, SppfNode> Nodes = new();
    public readonly List<SppfNode> InOrder = new();
    public int NodeBudget { get; set; } = 100_000;
    public int PackedBudget { get; set; } = 100_000;
    public int PackedCount { get; private set; }
    public bool BudgetExhausted { get; set; }

    public SppfNode GetOrAdd(SppfNode node)
    {
        if (Nodes.TryGetValue(node.Id, out var existing)) return existing;
        if (InOrder.Count >= NodeBudget)
        {
            BudgetExhausted = true;
            throw new ForestBudgetException($"forest node budget {NodeBudget} exhausted at {node.Id}");
        }
        Nodes[node.Id] = node;
        InOrder.Add(node);
        return node;
    }

    public PackedNode AddPacked(SppfNode parent, PackedNode packed)
    {
        if (PackedCount >= PackedBudget)
        {
            BudgetExhausted = true;
            throw new ForestBudgetException($"packed node budget {PackedBudget} exhausted at {parent.Id}");
        }
        PackedCount++;
        parent.Packed.Add(packed);
        return packed;
    }

    public IEnumerable<SppfNode> Roots(string startSymbol, int length)
    {
        var key = $"S:{startSymbol}@0:{length}";
        if (Nodes.TryGetValue(key, out var root)) yield return root;
    }
}

public sealed class ForestBudgetException(string message) : Exception(message);

public sealed class ParseRange(int Left, int Right)
{
    public int Left { get; } = Left;
    public int Right { get; } = Right;
    public int Width => Right - Left;
    public override string ToString() => $"[{Left},{Right})";
}
