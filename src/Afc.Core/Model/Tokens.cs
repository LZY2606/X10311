namespace Afc.Core.Model;

/// <summary>坐标基准：偏移量按 UTF-16 代码单元计，line/col 从 1 开始。</summary>
public sealed record Position(int Offset, int Line, int Column)
{
    public const string OffsetBasis = "utf16-codeunit";
}

/// <summary>
/// 导入层 token：RawText/Kind 保留原始表示；NumericValue 等规范化结果只在派生层产生，
/// 无法转换时保留原值并在 Diagnostics 给出诊断，绝不就地丢弃。
/// </summary>
public sealed record Token(
    string Id,
    int Index,
    string Kind,
    string RawText,
    Position Start,
    Position End,
    string? Unit,
    double? NumericValue,
    IReadOnlyList<string> Diagnostics)
{
    public string CoordinateBasis => Position.OffsetBasis;
}

public sealed record TokenDoc(
    string DocId,
    string Version,
    IReadOnlyList<Token> Tokens,
    string RawText,
    IReadOnlyList<string> ImportDiagnostics);
