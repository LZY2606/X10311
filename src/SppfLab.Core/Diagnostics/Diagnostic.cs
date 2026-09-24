namespace SppfLab.Core.Diagnostics;

public enum DiagSeverity { Info, Warning, Error }

public enum DiagKind
{
    LeftRecursionDetected,
    NullableCycleDetected,
    DuplicateProduction,
    Ambiguity,
    PrecedenceConflict,
    PrecedenceFiltered,
    EmptyInput,
    BudgetExhausted,
    UnconvertibleValue,
    UnexpectedToken,
    NoParse,
    MergeConflict,
    WriteInterrupted
}

/// <summary>
/// A machine-readable diagnostic carrying stable coordinates. Every value
/// references original identities so the UI can jump to source evidence.
/// </summary>
public sealed class Diagnostic
{
    public Diagnostic(
        DiagKind kind,
        DiagSeverity severity,
        string message,
        string? productionId = null,
        int? tokenStart = null,
        int? tokenEnd = null,
        string? nodeId = null,
        IReadOnlyDictionary<string, string>? data = null)
    {
        Kind = kind;
        Severity = severity;
        Message = message;
        ProductionId = productionId;
        TokenStart = tokenStart;
        TokenEnd = tokenEnd;
        NodeId = nodeId;
        Data = data ?? new Dictionary<string, string>();
    }

    public DiagKind Kind { get; }
    public DiagSeverity Severity { get; }
    public string Message { get; }
    public string? ProductionId { get; }
    public int? TokenStart { get; }
    public int? TokenEnd { get; }
    public string? NodeId { get; }
    public IReadOnlyDictionary<string, string> Data { get; }
}
