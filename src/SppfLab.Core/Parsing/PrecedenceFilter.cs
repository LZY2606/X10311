using System.Collections.Generic;
using System.Linq;
using SppfLab.Core.Diagnostics;
using SppfLab.Core.Models;

namespace SppfLab.Core.Parsing;

/// <summary>
/// Applies an optional precedence/associativity overlay to a built forest.
/// The filter only attaches <see cref="FilterVerdict"/>s to packed nodes:
///  * original packed alternatives are never removed or rewritten;
///  * a conflicting rule declaration filters nothing (ConflictNoFilter);
///  * when the operator precedence is unresolvable, every candidate stays.
/// Filtered candidates are excluded from the "surviving" derivations but
/// remain in the DAG and in "include filtered" views.
/// </summary>
public sealed class PrecedenceFilter
{
    private readonly Grammar _grammar;
    private readonly PrecedencePolicy _policy;
    private readonly List<Diagnostic> _diagnostics = new();

    public PrecedenceFilter(Grammar grammar, PrecedencePolicy policy)
    {
        _grammar = grammar;
        _policy = policy;
    }

    public IReadOnlyList<Diagnostic> Apply(ParseResult parse)
    {
        foreach (var terminal in _policy.DuplicateTerminals)
        {
            _diagnostics.Add(new Diagnostic(
                DiagKind.PrecedenceConflict, DiagSeverity.Warning,
                $"Operator '{terminal}' has conflicting precedence declarations in policy v{_policy.Version}; " +
                "no candidate is filtered for this operator.",
                data: new Dictionary<string, string>
                {
                    ["terminal"] = terminal,
                    ["policyVersion"] = _policy.Version.ToString()
                }));
        }

        if (parse.Root is null)
            return _diagnostics;

        // Compare sibling packed alternatives under ambiguous symbol nodes
        // that represent a binary expression: label candidates by the
        // operator terminal visible at their top-level RHS, then resolve
        // nesting shape with associativity.
        foreach (var node in parse.Nodes.Values)
        {
            if (node.Packed.Count <= 1)
                continue;
            ResolveNode(node);
        }

        return _diagnostics;
    }

    private void ResolveNode(SppfNode node)
    {
        foreach (var packed in node.Packed)
        {
            var production = _grammar.Find(packed.ProductionId);
            if (production is null)
                continue;

            // Find the "operator" terminal of this production (the first
            // terminal declared in the policy that appears in its RHS).
            string? op = production.RHS
                .Where(s => s.IsTerminal)
                .Select(s => s.Name)
                .FirstOrDefault(t => _policy.ByTerminal.ContainsKey(t) || _policy.DuplicateTerminals.Contains(t));

            if (op is null)
            {
                packed.Filters.Add(new FilterVerdict(_policy.Version, FilterOutcome.Kept,
                    "No declared operator in production; outside policy scope."));
                continue;
            }

            if (_policy.HasConflict(op))
            {
                packed.Filters.Add(new FilterVerdict(_policy.Version, FilterOutcome.ConflictNoFilter,
                    $"Conflicting declarations for '{op}'; precedence unresolved, candidate retained.", op));
                continue;
            }

            var rule = _policy.ByTerminal[op];
            var nesting = ClassifyNesting(node, packed, op);

            switch (nesting)
            {
                case Nesting.OuterVsInner when TryGetSiblingRule(node, packed, op, out var siblingRule):
                    if (siblingRule!.Level < rule.Level)
                    {
                        packed.Filters.Add(new FilterVerdict(_policy.Version, FilterOutcome.Kept,
                            $"Outer '{op}' (level {rule.Level}) binds over level {siblingRule.Level}.", op));
                    }
                    else
                    {
                        packed.Filters.Add(Mark(op, rule, siblingRule, "outer"));
                    }
                    break;

                case Nesting.InnerSameOp:
                    // Inner nesting of the same operator: allowed for left assoc only.
                    if (rule.Assoc == Assoc.Left)
                        packed.Filters.Add(new FilterVerdict(_policy.Version, FilterOutcome.Kept,
                            $"Left-associative nesting of '{op}' retained.", op));
                    else
                        packed.Filters.Add(new FilterVerdict(_policy.Version, FilterOutcome.Filtered,
                            rule.Assoc == Assoc.NonAssoc
                                ? $"'{op}' is non-associative; nested candidate filtered (evidence retained)."
                                : $"Right/non-associative nesting of '{op}' filtered by assoc={rule.Assoc}.",
                            op));
                    break;

                default:
                    packed.Filters.Add(new FilterVerdict(_policy.Version, FilterOutcome.Kept,
                        $"Candidate compatible with '{op}' level {rule.Level} ({rule.Assoc}).", op));
                    break;
            }
        }

        foreach (var filtered in node.Packed.Where(p => p.IsFilteredOut))
        {
            _diagnostics.Add(new Diagnostic(DiagKind.PrecedenceFiltered, DiagSeverity.Info,
                $"Packed node {filtered.Id} filtered by policy v{_policy.Version}; remains in the forest as unchosen evidence.",
                productionId: filtered.ProductionId,
                tokenStart: node.LeftExtent, tokenEnd: node.RightExtent, nodeId: node.Id,
                data: new Dictionary<string, string>
                {
                    ["packedId"] = filtered.Id,
                    ["policyVersion"] = _policy.Version.ToString()
                }));
        }
    }

    private enum Nesting { Flat, InnerSameOp, OuterVsInner }

    private Nesting ClassifyNesting(SppfNode parent, PackedNode packed, string op)
    {
        // If the right subtree root is a symbol node that itself expands with
        // the same operator, this is a right-nesting of that operator.
        var right = packed.Right;
        if (right is not null && SpansOp(right, op))
            return Nesting.InnerSameOp;
        if (packed.Left is not null && SpansOp(packed.Left, op) && parent.Packed.Count > 1)
            return Nesting.OuterVsInner;
        return Nesting.Flat;
    }

    private bool SpansOp(SppfNode node, string op)
    {
        if (node is IntermediateNode inter)
        {
            var production = _grammar.Find(inter.ProductionId);
            return production is not null
                && production.RHS.Any(s => s.IsTerminal && s.Name == op);
        }
        if (node is SymbolNode symbol)
        {
            return symbol.Packed.Any(p =>
            {
                var production = _grammar.Find(p.ProductionId);
                return production is not null
                    && production.RHS.Any(s => s.IsTerminal && s.Name == op);
            });
        }
        return false;
    }

    private bool TryGetSiblingRule(SppfNode node, PackedNode packed, string op, out OpRule? rule)
    {
        rule = null;
        foreach (var sibling in node.Packed)
        {
            if (sibling == packed)
                continue;
            var production = _grammar.Find(sibling.ProductionId);
            var otherOp = production?.RHS.Where(s => s.IsTerminal)
                .Select(s => s.Name).FirstOrDefault(t => _policy.ByTerminal.ContainsKey(t));
            if (otherOp is not null && _policy.ByTerminal.TryGetValue(otherOp, out var found))
            {
                rule = found;
                return true;
            }
        }
        return false;
    }

    private FilterVerdict Mark(string op, OpRule rule, OpRule sibling, string shape) =>
        new(_policy.Version, FilterOutcome.Filtered,
            $"'{op}' level {rule.Level} ({rule.Assoc}) loses to level {sibling.Level} in {shape} shape; filtered but retained as evidence.",
            op);
}
