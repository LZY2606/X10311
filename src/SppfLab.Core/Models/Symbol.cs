namespace SppfLab.Core.Models;

/// <summary>
/// A grammar symbol. Symbols are value-equal on their spelling; terminals
/// match token <see cref="Token.Kind"/> values.
/// </summary>
public readonly record struct Symbol(string Name, bool IsTerminal)
{
    public static Symbol T(string name) => new(name, true);
    public static Symbol N(string name) => new(name, false);

    public override string ToString() => IsTerminal ? $"'{Name}'" : Name;
}
