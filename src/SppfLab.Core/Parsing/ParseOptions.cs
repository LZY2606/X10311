namespace SppfLab.Core.Parsing;

/// <summary>Budgets bound the forest so runaway ambiguity is reported, not hidden.</summary>
public sealed class ParseOptions
{
    public int NodeBudget { get; init; } = 100_000;
    public int PackedBudget { get; init; } = 200_000;
    public int ItemBudget { get; init; } = 1_000_000;

    /// <summary>Maximum derivation trees enumerated per ambiguous node subtree.</summary>
    public int DerivationBudget { get; init; } = 100;

    public static ParseOptions Tiny => new()
    {
        NodeBudget = 12,
        PackedBudget = 12,
        ItemBudget = 100,
        DerivationBudget = 10
    };
}
