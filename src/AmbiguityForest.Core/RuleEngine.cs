namespace AmbiguityForest.Core;

/// <summary>
/// Precedence/associativity rules. Rules only filter applicable candidates;
/// the forest (grammar evidence) is never rewritten. Filtered candidates stay
/// in the forest with their filter marks. If applying rules would filter every
/// alternative at a node, that is a rule conflict: all candidates are kept and
/// marked conflicted rather than silently collapsing to zero or one answer.
/// </summary>
public static class RuleEngine
{
    public static List<PendingEvent> Apply(Grammar g, Forest forest, IReadOnlyList<PrecedenceRule> rules)
    {
        var events = new List<PendingEvent>();
        forest.Reindex();
        foreach (var node in forest.Symbols.Where(s => s.PackedIds.Count >= 2))
        {
            var packs = node.PackedIds.Select(forest.PackedOf).ToList();
            var applicable = rules.Where(r => r.AppliesTo == "*" || r.AppliesTo == node.Symbol).ToList();
            foreach (var rule in applicable)
            {
                foreach (var p in packs)
                {
                    var reason = MatchReason(g, forest, rule, p);
                    if (reason == null) continue;
                    if (p.FilteredBy.Any(m => m.RuleId == rule.Id)) continue;
                    p.FilteredBy.Add(new FilterMark { RuleId = rule.Id, RuleVersion = rule.Version, Reason = reason });
                    events.Add(new PendingEvent("rule-filtered", new()
                    {
                        ["ruleId"] = rule.Id,
                        ["ruleVersion"] = rule.Version,
                        ["packedId"] = p.Id,
                        ["nodeId"] = node.Id,
                        ["reason"] = reason
                    }));
                }
            }

            // Contradictory rule pairs applicable at the same node.
            foreach (var a in applicable.Where(r => r.Kind == "precedence"))
            foreach (var b in applicable.Where(r => r.Kind == "precedence"))
            {
                if (a.Id != b.Id && a.Higher == b.Lower && a.Lower == b.Higher)
                {
                    events.Add(new PendingEvent("rule-conflict", new()
                    {
                        ["nodeId"] = node.Id,
                        ["rules"] = new[]
                        {
                            new Dictionary<string, object?> { ["id"] = a.Id, ["version"] = a.Version },
                            new Dictionary<string, object?> { ["id"] = b.Id, ["version"] = b.Version }
                        },
                        ["reason"] = $"contradictory precedence: {a.Id} says {a.Higher}>{a.Lower}, {b.Id} says {b.Higher}>{b.Lower}",
                        ["resolution"] = "candidates-preserved"
                    }));
                }
            }

            // If every candidate is filtered, uncertainty would be swallowed.
            // Keep all candidates and mark the node conflicted instead.
            if (packs.All(p => p.FilteredBy.Count > 0))
            {
                foreach (var p in packs) p.Conflicted = true;
                events.Add(new PendingEvent("rule-conflict", new()
                {
                    ["nodeId"] = node.Id,
                    ["rules"] = packs.SelectMany(p => p.FilteredBy)
                        .GroupBy(m => m.RuleId)
                        .Select(gr => new Dictionary<string, object?> { ["id"] = gr.Key, ["version"] = gr.First().RuleVersion })
                        .ToArray(),
                    ["reason"] = "all alternatives would be filtered; refusing to collapse to zero candidates",
                    ["resolution"] = "candidates-preserved"
                }));
            }
        }
        return events;
    }

    private static string? MatchReason(Grammar g, Forest forest, PrecedenceRule rule, PackedNode p)
    {
        if (rule.Kind == "precedence")
        {
            // Higher-precedence operator binds tighter: a packed node built with
            // the higher production may not have a child whose alternative root
            // is the lower production.
            if (p.ProductionId != rule.Higher) return null;
            foreach (var childId in p.ChildIds)
            {
                var child = forest.Symbols.FirstOrDefault(s => s.Id == childId);
                if (child == null) continue;
                var hit = child.PackedIds.Select(forest.PackedOf).FirstOrDefault(c => c.ProductionId == rule.Lower);
                if (hit != null)
                    return $"precedence {rule.Higher}>{rule.Lower}: child {child.Id} offers lower-production alternative {hit.Id}";
            }
            return null;
        }
        if (rule.Kind == "associativity")
        {
            if (p.ProductionId != rule.Production) return null;
            var childSymbols = p.ChildIds
                .Select(id => forest.Symbols.FirstOrDefault(s => s.Id == id))
                .Where(s => s != null).Cast<SymbolNode>().ToList();
            if (childSymbols.Count == 0) return null;
            SymbolNode? suspect = rule.Assoc == "left" ? childSymbols.Last() : childSymbols.First();
            var nested = suspect.PackedIds.Select(forest.PackedOf).FirstOrDefault(c => c.ProductionId == rule.Production);
            if (nested != null)
                return $"{rule.Assoc}-associativity of {rule.Production}: nested same-production alternative {nested.Id} on the {(rule.Assoc == "left" ? "right" : "left")} spine";
            return null;
        }
        return null;
    }
}

/// <summary>One ambiguous (or rule-resolved) interval of the token stream.</summary>
public sealed class AmbiguityEntry
{
    public string NodeId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public string SpanUnit { get; set; } = "token-index";
    public string Status { get; set; } = ""; // "ambiguous" | "resolved-by-rules" | "conflicted"
    public List<AlternativeEntry> Alternatives { get; set; } = new();
}

public sealed class AlternativeEntry
{
    public string PackedId { get; set; } = "";
    public string ProductionId { get; set; } = "";
    public bool Filtered { get; set; }
    public bool Conflicted { get; set; }
    public List<FilterMark> FilterReasons { get; set; } = new();
}

public sealed class AmbiguityReport
{
    public string ForestId { get; set; } = "";
    public List<AmbiguityEntry> Entries { get; set; } = new();

    public static AmbiguityReport Build(Forest forest)
    {
        forest.Reindex();
        var report = new AmbiguityReport { ForestId = forest.Id };
        foreach (var node in forest.Symbols.Where(s => s.PackedIds.Count > 1).OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            var packs = node.PackedIds.Select(forest.PackedOf).ToList();
            var surviving = packs.Where(p => !(p.FilteredBy.Count > 0 && !p.Conflicted)).ToList();
            var entry = new AmbiguityEntry
            {
                NodeId = node.Id,
                Symbol = node.Symbol,
                Start = node.Start,
                End = node.End,
                Status = packs.Any(p => p.Conflicted) ? "conflicted"
                    : surviving.Count > 1 ? "ambiguous"
                    : "resolved-by-rules",
                Alternatives = packs.Select(p => new AlternativeEntry
                {
                    PackedId = p.Id,
                    ProductionId = p.ProductionId,
                    Filtered = p.FilteredBy.Count > 0 && !p.Conflicted,
                    Conflicted = p.Conflicted,
                    FilterReasons = p.FilteredBy
                }).ToList()
            };
            report.Entries.Add(entry);
        }
        return report;
    }
}
