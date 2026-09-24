using System.Globalization;

namespace ForestChamber.Core;

public sealed class GrammarImporter
{
    private const string StartDirective = "start";

    public GrammarDoc Import(string rawText)
    {
        var productions = new List<ProductionDef>();
        var diagnostics = new List<Diagnostic>();
        string? start = null;
        var lineNo = 0;
        var seen = new Dictionary<string, int>(); // lhs|RhsSymbols -> ordinal of first

        foreach (var rawLine in rawText.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = rawLine;
            var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0) line = line[..commentIdx];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("%start", StringComparison.OrdinalIgnoreCase))
            {
                start = trimmed["%start".Length..].Trim();
                if (start.Length == 0)
                    diagnostics.Add(new Diagnostic("G002", "error", "empty %start directive", null,
                        new Dictionary<string, string> { ["unit"] = "source-line", ["line"] = lineNo.ToString(CultureInfo.InvariantCulture) }));
                continue;
            }

            var parts = trimmed.Split("->", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                diagnostics.Add(new Diagnostic("G100", "error", $"unparseable production: {trimmed}", null,
                    new Dictionary<string, string> { ["unit"] = "source-line", ["line"] = lineNo.ToString(CultureInfo.InvariantCulture) }));
                continue;
            }

            var lhs = parts[0];
            var altOrdinal = 0;
            foreach (var altRaw in parts[1].Split('|'))
            {
                var alt = altRaw.Trim();
                var symbols = TokenizeRhs(alt);
                var rhss = symbols.Select(s => (GrammarSymbol)(s == "EPS" ? new TerminalSym("EPS") : char.IsUpper(s[0]) ? new NonterminalSym(s) : new TerminalSym(s))).ToList();
                var ordinal = productions.Count(p => p.Lhs == lhs);
                var key = lhs + "|" + string.Join(" ", symbols);
                var dup = seen.TryGetValue(key, out var firstOrdinal);
                var prod = new ProductionDef
                {
                    ProdId = $"P{lhs}.{ordinal}.L{lineNo}A{altOrdinal}",
                    Lhs = lhs,
                    Rhs = rhss,
                    SourceLine = lineNo,
                    AlternativeOrdinal = altOrdinal,
                    SourceText = trimmed,
                    IsDuplicate = dup,
                    DuplicateOfProdId = dup ? productions[firstOrdinal].ProdId : null
                };
                if (!dup) seen[key] = productions.Count;
                productions.Add(prod);
                altOrdinal++;
            }
        }

        start ??= productions.FirstOrDefault()?.Lhs ?? throw new ArgumentException("grammar has no productions");
        var nonterminals = productions.Select(p => p.Lhs).ToHashSet();
        var terminals = new HashSet<string>();
        foreach (var p in productions)
            foreach (var s in p.Rhs.OfType<TerminalSym>().Select(t => t.Name))
                if (s != "EPS") terminals.Add(s);

        return new GrammarDoc
        {
            Productions = productions,
            StartSymbol = start,
            RawText = rawText,
            Nonterminals = nonterminals,
            Terminals = terminals
        };
    }

    private static List<string> TokenizeRhs(string alt)
    {
        if (alt.Length == 0 || alt == "EPS") return new List<string> { "EPS" };
        return alt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}

public sealed class PrecedenceImporter
{
    // %precedence <level:int> <left|right|nonassoc> <operator-terminal>
    public PrecedenceDoc Import(string rawText)
    {
        var rules = new List<PrecedenceRule>();
        var conflicts = new List<PrecedenceConflict>();
        var lineNo = 0;
        var opToRules = new Dictionary<string, List<PrecedenceRule>>();

        foreach (var rawLine in rawText.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = rawLine;
            var c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) line = line[..c];
            var t = line.Trim();
            if (t.Length == 0) continue;
            var bits = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length != 4 || bits[0] != "%precedence")
            {
                conflicts.Add(new PrecedenceConflict($"PREC.L{lineNo}", "PREC.L0", t,
                    "rule must be: %precedence <level> <left|right|nonassoc> <operator>"));
                continue;
            }
            if (!int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ||
                bits[2] is not ("left" or "right" or "nonassoc"))
            {
                conflicts.Add(new PrecedenceConflict($"PREC.L{lineNo}", "PREC.L0", t,
                    "bad level or associativity"));
                continue;
            }
            var rule = new PrecedenceRule
            {
                RuleId = $"R-PREC-L{lineNo}",
                SourceLine = lineNo,
                Level = level,
                Associativity = bits[2],
                OperatorTerminal = bits[3],
                SourceText = t
            };
            if (opToRules.TryGetValue(rule.OperatorTerminal, out var earlier))
            {
                foreach (var e in earlier)
                    conflicts.Add(new PrecedenceConflict(e.RuleId, rule.RuleId, rule.OperatorTerminal,
                        "operator declared in two precedence levels; both versions retained as rule conflict"));
                earlier.Add(rule);
            }
            else
            {
                opToRules[rule.OperatorTerminal] = new List<PrecedenceRule> { rule };
            }
            rules.Add(rule);
        }

        return new PrecedenceDoc { Rules = rules, RawText = rawText, Conflicts = conflicts };
    }
}
