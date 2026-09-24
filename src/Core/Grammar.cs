#nullable enable
using System.Text.Json.Nodes;

namespace AmbiguityForestLab.Core;

public sealed record GrammarSymbol(string Name, bool IsTerminal);

public sealed record Production(
    string Id,
    string Lhs,
    IReadOnlyList<string> Rhs,
    int Order,
    string? RawText,
    string SourceRef,
    string SourceVersion);

public sealed class Grammar
{
    public required string Start { get; init; }
    public required IReadOnlyList<Production> Productions { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    private readonly Lazy<HashSet<string>> _nonTerminals;

    public Grammar()
    {
        _nonTerminals = new Lazy<HashSet<string>>(() => Productions.Select(p => p.Lhs).ToHashSet());
    }

    public bool IsTerminal(string symbol) => !_nonTerminals.Value.Contains(symbol);

    public IEnumerable<Production> ProductionsOf(string lhs) =>
        Productions.Where(p => p.Lhs == lhs);

    public string StructureKey(Production p) =>
        p.Lhs + " ::= " + string.Join(" ", p.Rhs);
}

public sealed record RawQuantity(string? Raw, string? Normalized, string? Unit, string? Basis, string? Diagnostic)
{
    public JsonObject ToJson() => new()
    {
        ["raw"] = Raw,
        ["normalized"] = Normalized,
        ["unit"] = Unit,
        ["basis"] = Basis,
        ["diagnostic"] = Diagnostic
    };

    public static RawQuantity FromJson(JsonNode? node)
    {
        var o = node?.AsObject();
        return new RawQuantity(
            (string?)o?["raw"], (string?)o?["normalized"], (string?)o?["unit"],
            (string?)o?["basis"], (string?)o?["diagnostic"]);
    }
}

public sealed class Token
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public required int Order { get; init; }
    public required RawQuantity Start { get; init; }
    public required RawQuantity Length { get; init; }
    public int? StartChar { get; init; }
    public int? EndChar { get; init; }
    public string? CoordinateDiagnostic { get; init; }
}

public sealed class TokenStream
{
    public required IReadOnlyList<Token> Tokens { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public string CoordinateBasis { get; init; } = "token-index";
}
