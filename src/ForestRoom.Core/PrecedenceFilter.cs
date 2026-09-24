namespace ForestRoom.Core;

public sealed class FilterResult
{
    public List<ForestEvent> Events { get; } = new();
    public int FilteredCount { get; set; }
    public bool HasConflict { get; set; }
}

/// <summary>
/// 优先/结合规则过滤器。语义红线：只过滤适用候选，绝不改写原文法证据——
/// 被过滤的 packed 候选保留在森林中并附过滤理由；规则冲突时不静默取舍，
/// 全部候选保持 active 并记录冲突事件。
/// </summary>
public static class PrecedenceFilter
{
    private sealed record Proposal(PackedNode Target, PriorityRule Rule, string Reason);

    public static FilterResult Apply(Forest forest, RuleSet rules, GrammarVersion grammar)
    {
        var result = new FilterResult();
        long seq = 0;
        var prods = grammar.Productions.ToDictionary(p => p.Id);

        foreach (var node in forest.Nodes.Values.Where(n => n.Alternatives.Count > 1))
        {
            var proposals = new List<Proposal>();
            foreach (var rule in rules.Rules)
            {
                switch (rule.Kind)
                {
                    case RuleKind.Precedence:
                    {
                        var high = node.Alternatives.Where(a => a.Status == PackedStatus.Active
                            && a.ProductionId == rule.HigherProductionId).ToList();
                        var low = node.Alternatives.Where(a => a.Status == PackedStatus.Active
                            && a.ProductionId == rule.LowerProductionId).ToList();
                        if (high.Count > 0)
                            foreach (var l in low)
                                proposals.Add(new Proposal(l, rule,
                                    $"rule {rule.RuleId} v{rule.RuleVersion}: {rule.HigherProductionId} > {rule.LowerProductionId}"));
                        break;
                    }
                    case RuleKind.LeftAssoc:
                    case RuleKind.RightAssoc:
                    case RuleKind.NonAssoc:
                    {
                        if (!prods.TryGetValue(rule.HigherProductionId, out var p)) break;
                        var recPositions = p.Rhs.Select((s, i) => (s, i))
                            .Where(x => x.s == p.Lhs).Select(x => x.i).ToList();
                        if (recPositions.Count == 0) break;
                        foreach (var alt in node.Alternatives.Where(a =>
                                     a.Status == PackedStatus.Active && a.ProductionId == p.Id))
                        {
                            // 二值化 SPPF：packed 的最后一个子节点承载刚匹配的符号，
                            // 递归位置通过子节点 label 判断
                            var children = alt.ChildIds.Select(id => forest.Nodes[id]).ToList();
                            bool nested = rule.Kind switch
                            {
                                RuleKind.LeftAssoc => NestedAt(children, p, recPositions.Max(), forest),
                                RuleKind.RightAssoc => NestedAt(children, p, recPositions.Min(), forest),
                                _ => recPositions.Any(pos => NestedAt(children, p, pos, forest))
                            };
                            if (nested)
                                proposals.Add(new Proposal(alt, rule,
                                    $"rule {rule.RuleId} v{rule.RuleVersion}: {rule.Kind} on {p.Id}, nested same-production child rejected"));
                        }
                        break;
                    }
                }
            }

            // 冲突检测：同一节点上互相矛盾的规则同时适用 → 都不执行
            var conflictingRuleIds = FindConflicts(proposals);
            foreach (var group in conflictingRuleIds)
            {
                result.HasConflict = true;
                var involved = proposals.Where(pr => group.Contains(pr.Rule.RuleId)).ToList();
                result.Events.Add(ForestEvent.Of(++seq, EventKinds.RuleConflict, node.Id,
                    $"conflicting priority rules [{string.Join(", ", group)}] both applicable at node {node.Id} span {node.Span}; no candidate filtered, all kept active",
                    ("rules", string.Join(",", group)), ("span", node.Span)));
                proposals.RemoveAll(pr => group.Contains(pr.Rule.RuleId));
            }

            foreach (var pr in proposals)
            {
                if (pr.Target.Status != PackedStatus.Active) continue;
                pr.Target.Status = PackedStatus.Filtered;
                pr.Target.FilterReason = pr.Reason;
                result.FilteredCount++;
                result.Events.Add(ForestEvent.Of(++seq, EventKinds.CandidateFiltered, pr.Target.Id,
                    $"candidate {pr.Target.Id} (production {pr.Target.ProductionId}) filtered at node {node.Id} span {node.Span}: {pr.Reason}",
                    ("rule", pr.Rule.RuleId), ("ruleVersion", pr.Rule.RuleVersion.ToString()),
                    ("production", pr.Target.ProductionId ?? ""), ("span", node.Span)));
            }
        }
        return result;
    }

    private static bool NestedAt(List<ForestNode> children, Production p, int recPos, Forest forest)
    {
        // 二值化结构下，递归子节点是 label 等于 LHS 的 Symbol 子节点；
        // 简化判定：任一 Symbol 子节点 label==LHS 且其自身有同产生式 active 候选
        foreach (var c in children)
        {
            if (c.Kind == NodeKind.Symbol && c.Label == p.Lhs
                && c.Alternatives.Any(a => a.Status == PackedStatus.Active && a.ProductionId == p.Id))
                return true;
            // 中间节点继续向下看一层
            if (c.Kind == NodeKind.Intermediate)
            {
                foreach (var pack in c.Alternatives)
                {
                    var grand = pack.ChildIds.Select(id => forest.Nodes[id]).ToList();
                    if (NestedAt(grand, p, recPos, forest)) return true;
                }
            }
        }
        return false;
    }

    private static List<HashSet<string>> FindConflicts(List<Proposal> proposals)
    {
        var conflicts = new List<HashSet<string>>();
        var byRule = proposals.GroupBy(p => p.Rule.RuleId)
            .ToDictionary(g => g.Key, g => g.First().Rule);
        foreach (var a in byRule.Values)
        {
            foreach (var b in byRule.Values)
            {
                if (a.RuleId == b.RuleId) continue;
                bool clash =
                    (a.Kind == RuleKind.Precedence && b.Kind == RuleKind.Precedence
                        && a.HigherProductionId == b.LowerProductionId
                        && a.LowerProductionId == b.HigherProductionId)
                    || (a.Kind == RuleKind.LeftAssoc && b.Kind == RuleKind.RightAssoc
                        && a.HigherProductionId == b.HigherProductionId)
                    || (a.Kind == RuleKind.NonAssoc
                        && (b.Kind == RuleKind.LeftAssoc || b.Kind == RuleKind.RightAssoc)
                        && a.HigherProductionId == b.HigherProductionId);
                if (clash)
                {
                    var set = new HashSet<string> { a.RuleId, b.RuleId };
                    if (!conflicts.Any(c => c.SetEquals(set))) conflicts.Add(set);
                }
            }
        }
        return conflicts;
    }
}
