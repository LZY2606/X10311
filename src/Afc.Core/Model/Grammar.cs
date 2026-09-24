namespace Afc.Core.Model;

/// <summary>
/// 文法符号。终结符以词面量文本（如 "+"）或 token 类别引用（如 "$NUM"）表示；
/// 非终结符为裸标识符。身份即名称本身，跨版本保持稳定。
/// </summary>
public sealed record SymbolRef(string Name, bool IsTerminal)
{
    public const string Epsilon = "ε";
    public bool IsEpsilon => Name is "" or Epsilon;

    public override string ToString() => IsTerminal ? $"\"{Name}\"" : Name;
}

/// <summary>
/// 一条产生式的原始身份记录：Id 来自导入文本中的显式标签或稳定行号，
/// OriginalText 保留导入时的原始表示，Order 保留源顺序。规范化只发生在派生层。
/// </summary>
public sealed record Production(
    string Id,
    string Lhs,
    IReadOnlyList<string> Rhs,
    string OriginalText,
    int Order,
    IReadOnlyDictionary<string, string> Annotations)
{
    public bool IsEpsilon => Rhs.Count == 0;
    public string Display => $"{Lhs} ::= {(IsEpsilon ? SymbolRef.Epsilon : string.Join(" ", Rhs.Select(r => r.StartsWith('"') ? r : r)))}";
}

public sealed class GrammarDoc
{
    public required string DocId { get; init; }
    public required string Version { get; init; }
    public required string StartSymbol { get; init; }
    public required IReadOnlyList<Production> Productions { get; init; }
    public required string RawText { get; init; }

    public ILookup<string, Production> ByLhs =>
        Productions.ToLookup(p => p.Lhs);

    public IEnumerable<Production> For(string nt) => ByLhs[nt];
}
