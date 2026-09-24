namespace ForestRoom.Core;

public enum NodeKind { Symbol, Intermediate, Terminal }

/// <summary>SPPF 节点。Id 稳定；共享通过 (Label,Start,End) 唯一性实现，
/// 共享不丢失 derivation：每个 derivation 是一个 packed alternative。</summary>
public sealed class ForestNode
{
    public required string Id { get; init; }
    public required NodeKind Kind { get; init; }
    public required string Label { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
    public List<PackedNode> Alternatives { get; } = new();
    public string Span => $"[{Start},{End})";
    public bool IsAmbiguous => Alternatives.Count(a => a.Status == PackedStatus.Active) > 1
                            || Alternatives.Count > 1;
}

public enum PackedStatus { Active, Filtered, CycleTerminated }

/// <summary>Packed node = 一个 derivation 候选。被过滤的候选保留在森林中。</summary>
public sealed class PackedNode
{
    public required string Id { get; init; }
    public required string ParentNodeId { get; init; }
    public required int Ordinal { get; init; }
    public string? ProductionId { get; init; }
    public List<string> ChildIds { get; init; } = new();
    public PackedStatus Status { get; set; } = PackedStatus.Active;
    public string? FilterReason { get; set; }
}

public sealed class Forest
{
    public Dictionary<string, ForestNode> Nodes { get; } = new();
    public int NodeCount => Nodes.Count;

    public IEnumerable<ForestNode> AmbiguousNodes() => Nodes.Values.Where(n => n.IsAmbiguous);
}

public enum ParseStatus { Accepted, Rejected, BudgetExhausted }

public sealed class ParseResult
{
    public required string RunId { get; init; }
    public ParseStatus Status { get; set; }
    public Forest Forest { get; } = new();
    public List<ForestEvent> Events { get; } = new();
    public string? RootNodeId { get; set; }
    public NullableAnalysis? Nullable { get; set; }
}
