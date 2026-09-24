using System.Text.Json.Serialization;

namespace ForestChamber.Core;

/// <summary>带原产生式身份的 CFG 产生式：ordinal 是导入顺序身份，Key 是内容去重键。</summary>
public sealed class Production
{
    public string Id { get; set; } = "";
    public int Ordinal { get; set; }
    public string Lhs { get; set; } = "";
    public List<string> Rhs { get; set; } = new();
    /// <summary>原始表示（规范化/去空白前的原文），永久保留。</summary>
    public string RawText { get; set; } = "";
    public bool DuplicateOfEarlier { get; set; }

    public string RhsKey => string.Join(" ", Rhs);
    public bool IsEpsilon => Rhs.Count == 0;

    public string Display => $"{Lhs} → {(Rhs.Count == 0 ? "ε" : string.Join(" ", Rhs))}";
}

/// <summary>原始 token：保留导入原文与坐标；领域量/单位信息留在派生层处理。</summary>
public sealed class TokenInfo
{
    public int Index { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public TokenSpan Span { get; set; } = new(0, 0);
    public string RawRepresentation { get; set; } = "";
    public DomainQuantity? Quantity { get; set; }
}

public enum Assoc { Left, Right, NonAssoc }

/// <summary>二元算子优先级规则（一个版本）。低 Level = 低优先级。</summary>
public sealed class PrecedenceRule
{
    public string Id { get; set; } = "";
    public string Operator { get; set; } = "";
    public int Level { get; set; }
    public Assoc Assoc { get; set; } = Assoc.Left;
    public int RuleOrdinal { get; set; }
    /// <summary>承载该算子的产生式身份，例如 E → E op E。</summary>
    public string? CarrierProductionId { get; set; }
}

/// <summary>一次导入的不可变文法包：产生式 + token 流 + 可选优先/结合规则。</summary>
public sealed class GrammarDocument
{
    public string Id { get; set; } = "";
    public string StartSymbol { get; set; } = "";
    public List<Production> Productions { get; set; } = new();
    public List<TokenInfo> Tokens { get; set; } = new();
    public List<PrecedenceRule> Rules { get; set; } = new();
    public string CoordinateBasis { get; set; } = "code-unit offsets; intervals [start,end)";
    public List<string> Diagnostics { get; set; } = new();

    [JsonIgnore]
    public ILookup<string, Production> ByLhs => Productions.ToLookup(p => p.Lhs);

    [JsonIgnore]
    public ILookup<string, Production> Terminals =>
        Productions.Where(p => p.Rhs.Count == 1 && !IsNonTerminal(p.Rhs[0]))
            .ToLookup(p => p.Rhs[0]);

    public bool IsNonTerminal(string symbol) => Productions.Any(p => p.Lhs == symbol);
}

/// <summary>优先规则版本：规则修订产生新版本，旧版本永不被覆盖。</summary>
public sealed class RuleRevision
{
    public string VersionId { get; set; } = "";
    public int VersionNumber { get; set; }
    public List<PrecedenceRule> Rules { get; set; } = new();
    public string Note { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
