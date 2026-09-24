namespace SppfLab.Core.Models;

/// <summary>
/// One input token. <see cref="Start"/>:<see cref="End"/> are character
/// coordinates (zero-based, half-open, UTF-16 code unit basis).
/// </summary>
public sealed class Token
{
    public Token(int index, string kind, string text, int start, int end, string? rawLexeme = null)
    {
        Index = index;
        Kind = kind;
        Text = text;
        Start = start;
        End = end;
        RawLexeme = rawLexeme ?? text;
    }

    /// <summary>Position in the token stream (0-based). Stable identity.</summary>
    public int Index { get; }

    /// <summary>Terminal spelling matched against grammar terminals.</summary>
    public string Kind { get; }

    /// <summary>Presentation text (normalized layer may derive this).</summary>
    public string Text { get; }

    /// <summary>Original lexeme exactly as imported. Preserved for evidence.</summary>
    public string RawLexeme { get; }

    /// <summary>Inclusive start character coordinate (char offset basis).</summary>
    public int Start { get; }

    /// <summary>Exclusive end character coordinate (char offset basis).</summary>
    public int End { get; }

    public override string ToString() => $"#{Index}:{Kind}@{Start}-{End}({Text})";
}
