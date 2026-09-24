using Afc.Core.Model;

namespace Afc.Core.Parsing;

public enum SppfNodeKind { Terminal, Symbol }

/// <summary>
/// SPPF 节点。身份确定性派生自 (符号, 左token位置, 右token位置)：
///   Terminal:  tn:[kind]@i
///   Symbol:    sn:(A,i,j)
/// 同一范围同一符号永远共享同一节点（forward sharing）。
/// </summary>
public sealed class SppfNode
{
    public required string Id { get; init; }
    public required SppfNodeKind Kind { get; init; }
    public required string Symbol { get; init; }
    public required int Left { get; init; }
    public required int Right { get; init; }
    public string? TokenId { get; init; }
    public string? TerminalKind { get; init; }
    public List<PackedNode> Packed { get; } = new();

    public bool IsAmbiguous => Packed.Count > 1;
}

/// <summary>
/// packed node：一次具体产生式应用（或扫描步），身份确定性派生自
/// (pivot, 产生式Id, 父节点Id)。Children 为左到右的符号/终结符节点，pivot
/// 记录相邻子节点边界，供优先级裁决与 lineage 追溯。
/// </summary>
public sealed class PackedNode
{
    public required string Id { get; init; }
    public required string? ProductionId { get; init; }
    public required int Pivot { get; init; }
    public required IReadOnlyList<string> Children { get; init; }
    public bool Filtered { get; set; }
    public string? FilterReason { get; set; }
    public string? RuleVersion { get; set; }
    public bool Selected { get; set; }
}

public sealed class Forest
{
    private readonly Dictionary<string, SppfNode> _nodes = new();
    public IReadOnlyDictionary<string, SppfNode> Nodes => _nodes;

    public SppfNode Terminal(string kind, string tokenId, int pos)
    {
        var id = $"tn:{kind}@{pos}";
        if (_nodes.TryGetValue(id, out var existing)) return existing;
        var node = new SppfNode
        {
            Id = id, Kind = SppfNodeKind.Terminal, Symbol = kind,
            Left = pos, Right = pos + 1, TokenId = tokenId, TerminalKind = kind
        };
        _nodes[id] = node;
        return node;
    }

    public SppfNode Symbol(string symbol, int left, int right)
    {
        var id = $"sn:({symbol},{left},{right})";
        if (_nodes.TryGetValue(id, out var existing)) return existing;
        var node = new SppfNode
        {
            Id = id, Kind = SppfNodeKind.Symbol, Symbol = symbol, Left = left, Right = right
        };
        _nodes[id] = node;
        return node;
    }

    public void AddPacked(SppfNode parent, string? productionId, int pivot, IEnumerable<string> children,
        string? ruleVersion = null)
    {
        var childList = children.ToList();
        var id = $"pk:{pivot}:{productionId ?? "scan"}>{parent.Id}";
        if (parent.Packed.Any(p => p.Id == id)) return;
        parent.Packed.Add(new PackedNode
        {
            Id = id,
            ProductionId = productionId,
            Pivot = pivot,
            Children = childList,
            RuleVersion = ruleVersion
        });
    }

    public IEnumerable<SppfNode> Ambiguous() => _nodes.Values.Where(n => n.IsAmbiguous);
}
