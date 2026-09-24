using System.Text.Json;
using System.Text.Json.Serialization;

namespace GsbParser.Core;

/// <summary>符号：终端/非终端。终端名带单引号；非终端不带。</summary>
public readonly record struct Sym(string Name, bool IsTerminal)
{
    public static Sym Nt(string n) => new(n, false);
    public static Sym T(string n) => new(n, true);
    public override string ToString() => IsTerminal ? $"'{Name}'" : Name;
}

/// <summary>一个 token；位置以 token 索引为坐标基准，charStart/charEnd 是原始字符坐标。</summary>
public sealed class Token
{
    public int Index { get; init; }
    public string Kind { get; init; } = "";
    public string? Text { get; init; }
    public int CharStart { get; init; }
    public int CharEnd { get; init; }
    /// <summary>无法规范化的原始表示留在原处并附诊断（例如未识别单位/大小写）。</summary>
    public string? RawRepresentation { get; init; }
    public string? Diagnostic { get; set; }
}

/// <summary>
/// 原产生式身份：即使右部完全相同（重复产生式）也保留独立 Ordinal/Identity。
/// </summary>
public sealed class Production
{
    public required int Ordinal { get; init; }
    public required string Identity { get; init; }
    public required string Lhs { get; init; }
    public required IReadOnlyList<Sym> Rhs { get; init; }
    public required string SourceText { get; init; }
    public required int SourceLine { get; init; }
    public bool IsEpsilon => Rhs.Count == 0;
    public override string ToString() => $"{Lhs} -> {(Rhs.Count == 0 ? "ε" : string.Join(" ", Rhs))}";
}

public sealed class Grammar
{
    public required string GrammarId { get; init; }
    public required string RawText { get; init; }
    public required IReadOnlyList<Production> Productions { get; init; }
    public required string StartSymbol { get; init; }
    private Dictionary<string, List<Production>>? _byLhs;
    public IReadOnlyList<Production> For(string lhs) => ByLhs.GetValueOrDefault(lhs) ?? EmptyList.Value;
    private Dictionary<string, List<Production>> ByLhs => _byLhs ??= Build();
    private Dictionary<string, List<Production>> Build()
    {
        var d = new Dictionary<string, List<Production>>();
        foreach (var p in Productions)
        {
            if (!d.TryGetValue(p.Lhs, out var l)) d[p.Lhs] = l = new();
            l.Add(p);
        }
        return d;
    }
    private static class EmptyList { public static readonly List<Production> Value = new(); }
}

/// <summary>一条优先/结合规则，有独立身份与版本。</summary>
public sealed class PrecedenceRule
{
    public required string Identity { get; init; }
    public required string OperatorTerminal { get; init; }
    public required int Precedence { get; init; }
    /// <summary>left / right / nonassoc</summary>
    public required string Associativity { get; init; }
    public required string RuleVersion { get; init; }
    public required string SourceText { get; init; }
}

public sealed class PrecedenceTable
{
    public required string TableVersion { get; init; }
    public required IReadOnlyList<PrecedenceRule> Rules { get; init; }
    public PrecedenceRule? ForOperator(string op)
    {
        foreach (var r in Rules) if (r.OperatorTerminal == op) return r;
        return null;
    }
}
