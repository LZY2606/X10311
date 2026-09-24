using System.Text.Json.Serialization;

namespace Afc.Core.Model;

public enum EventLevel
{
    Info,
    Warning,
    Error
}

public enum EventKind
{
    Import,
    NullableFact,
    NullableCycleCut,
    LeftRecursion,
    PackedNode,
    Ambiguity,
    DuplicateProduction,
    EmptyInput,
    BudgetExhausted,
    RuleApplied,
    RuleConflict,
    RuleFiltered,
    Recompute,
    UserDecision,
    Conflict,
    Parse
}

/// <summary>
/// 机器可读事件。判断依据（命题）写入 Facts，引用对象写入 Subjects（稳定身份），
/// 来源材料/规则版本/决定写入 Provenance。所有引用均不可改写历史。
/// </summary>
public sealed record AnalyzerEvent(
    string Id,
    long Seq,
    EventKind Kind,
    EventLevel Level,
    string Code,
    string Message,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Subjects,
    Provenance Provenance,
    string? RelatedEventId)
{
    [JsonIgnore]
    public bool IsAdoptedHint => Facts.Contains("adopted=true");
}

/// <summary>来源三件套：材料版本、规则版本、触发事件（如用户决定）。</summary>
public sealed record Provenance(
    string GrammarVersion,
    string TokenVersion,
    string? RuleVersion,
    string? DerivationId,
    string? CausedByEventId,
    string? CaseStepId)
{
    public static Provenance Baseline(string gv, string tv, string? rv = null, string? step = null) =>
        new(gv, tv, rv, null, null, step);
}
