using System.Collections.Generic;
using System.Linq;

namespace SppfLab.Core.Models;

public enum Assoc { Left, Right, NonAssoc }

/// <summary>One operator declaration in an optional precedence/associativity policy.</summary>
public sealed class OpRule
{
    public OpRule(string terminal, int level, Assoc assoc, string originLine)
    {
        Terminal = terminal;
        Level = level;
        Assoc = assoc;
        OriginLine = originLine;
    }

    public string Terminal { get; }
    public int Level { get; }
    public Assoc Assoc { get; }
    public string OriginLine { get; }
}

/// <summary>
/// A versioned, optional precedence/associativity overlay. It can only
/// *filter* packed-node candidates; it never rewrites grammar productions.
/// When the same terminal is declared with inconsistent level/assoc the
/// policy is in conflict for that terminal and applies no filtering to it.
/// </summary>
public sealed class PrecedencePolicy
{
    public PrecedencePolicy(int version, IReadOnlyList<OpRule> rules, string? sourceText = null)
    {
        Version = version;
        Rules = rules;
        SourceText = sourceText;

        var conflicting = new HashSet<string>(
            rules.GroupBy(r => r.Terminal)
                 .Where(g => g.Select(r => (r.Level, r.Assoc)).Distinct().Count() > 1)
                 .Select(g => g.Key));
        DuplicateTerminals = conflicting.ToList();

        var byTerminal = new Dictionary<string, OpRule>();
        foreach (var rule in rules)
        {
            if (conflicting.Contains(rule.Terminal))
                continue; // conflicting declarations filter nothing
            byTerminal[rule.Terminal] = rule;
        }
        ByTerminal = byTerminal;
    }

    public int Version { get; }
    public IReadOnlyList<OpRule> Rules { get; }
    public string? SourceText { get; }

    /// <summary>Terminals with conflicting declarations -> policy does not apply to them.</summary>
    public IReadOnlyList<string> DuplicateTerminals { get; }

    public IReadOnlyDictionary<string, OpRule> ByTerminal { get; }

    public bool HasConflict(string terminal) => DuplicateTerminals.Contains(terminal);
}
