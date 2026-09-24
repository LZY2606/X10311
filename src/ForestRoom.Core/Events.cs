namespace ForestRoom.Core;

/// <summary>机器可读取的判定事件。所有"为什么"都以事件落盘。</summary>
public sealed record ForestEvent(
    string EventId,
    long Seq,
    string Kind,
    string SubjectId,
    string Detail,
    IReadOnlyDictionary<string, string> Attributes)
{
    public static ForestEvent Of(long seq, string kind, string subject, string detail,
        params (string, string)[] attrs) =>
        new($"ev-{seq:D6}", seq, kind, subject, detail,
            attrs.ToDictionary(a => a.Item1, a => a.Item2));
}

public static class EventKinds
{
    public const string NullableCycleTerminated = "nullable-cycle-terminated";
    public const string LeftRecursionGuard = "left-recursion-guard";
    public const string BudgetExhausted = "forest-budget-exhausted";
    public const string CandidateFiltered = "candidate-filtered";
    public const string RuleConflict = "priority-rule-conflict";
    public const string AmbiguityDetected = "ambiguity-detected";
    public const string EmptyInputAccepted = "empty-input-accepted";
    public const string DuplicateProductionKept = "duplicate-production-kept";
    public const string QuantityDiagnostic = "quantity-diagnostic";
    public const string DependencyInvalidated = "dependency-invalidated";
    public const string DependencyKeptValid = "dependency-kept-valid";
    public const string UserChoice = "user-choice";
    public const string ChoiceSuperseded = "choice-superseded";
    public const string VersionCommitted = "version-committed";
    public const string ConflictDetected = "save-conflict-detected";
    public const string ConflictResolved = "save-conflict-resolved";
    public const string Recompute = "recompute";
}
