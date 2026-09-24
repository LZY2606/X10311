namespace ForestRoom.Core;

/// <summary>坐标基准：领域量必须声明自己的单位/基准，导入时保留原始表示。</summary>
public enum CoordBasis { TokenIndex, CharOffset, LineColumn, Unknown }

/// <summary>带单位/坐标基准的领域量。Raw 为导入时的原始表示，永不改写；
/// 规范化只发生在派生层。无法转换时 Normalized 为 null 并附诊断。</summary>
public sealed record Quantity(double? Normalized, string Unit, CoordBasis Basis, string Raw, string? Diagnostic)
{
    public static Quantity Of(double value, string unit, CoordBasis basis) =>
        new(value, unit, basis, value.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

    public static Quantity Unconvertible(string raw, string unit, CoordBasis basis, string diagnostic) =>
        new(null, unit, basis, raw, diagnostic);
}

public sealed record Symbol(string Name, bool IsTerminal)
{
    public static Symbol T(string name) => new(name, true);
    public static Symbol N(string name) => new(name, false);
    public override string ToString() => IsTerminal ? $"'{Name}'" : Name;
}

/// <summary>产生式。Id 是稳定身份，导入/导出/重放后保持不变。</summary>
public sealed record Production(string Id, string Lhs, IReadOnlyList<string> Rhs, int Ordinal)
{
    public override string ToString() => $"{Lhs} -> {(Rhs.Count == 0 ? "ε" : string.Join(' ', Rhs))}";
}

public sealed record GrammarVersion(
    string GrammarId,
    int Version,
    IReadOnlyList<Production> Productions,
    string StartSymbol)
{
    public IEnumerable<Production> For(string lhs) => Productions.Where(p => p.Lhs == lhs);
    public HashSet<string> NonTerminals => Productions.Select(p => p.Lhs).ToHashSet();
}

/// <summary>Token。Position 携带坐标基准；Raw 保留原始文本。</summary>
public sealed record Token(string Id, string Symbol, Quantity Position, string Raw);

public sealed record TokenStream(string StreamId, int Version, IReadOnlyList<Token> Tokens);

/// <summary>优先/结合规则。只用于过滤候选，绝不改写文法。</summary>
public enum RuleKind { Precedence, LeftAssoc, RightAssoc, NonAssoc }

public sealed record PriorityRule(
    string RuleId,
    int RuleVersion,
    RuleKind Kind,
    string HigherProductionId,
    string? LowerProductionId)
{
    public override string ToString() => Kind switch
    {
        RuleKind.Precedence => $"{HigherProductionId} > {LowerProductionId} (v{RuleVersion})",
        RuleKind.LeftAssoc => $"{HigherProductionId} left-assoc (v{RuleVersion})",
        RuleKind.RightAssoc => $"{HigherProductionId} right-assoc (v{RuleVersion})",
        _ => $"{HigherProductionId} non-assoc (v{RuleVersion})"
    };
}

public sealed record RuleSet(string RuleSetId, int Version, IReadOnlyList<PriorityRule> Rules);
