using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AmbiguityForest.Core;

public sealed record ForestEvent(long Seq, string Id, string VersionId, string Kind, string PayloadJson, string CreatedAt);

public sealed class ArtifactRecord
{
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
    public Dictionary<string, string> Inputs { get; set; } = new(); // input artifact id -> hash used
    public string Status { get; set; } = "valid"; // "valid" | "stale"
}

public sealed class ChosenAlternative
{
    public string PackedId { get; set; } = "";
    public string User { get; set; } = "";
    public string DecisionId { get; set; } = "";
    public List<string> KeptAlternatives { get; set; } = new(); // unchosen candidates are retained
}

public sealed class Selection
{
    public Dictionary<string, ChosenAlternative> Choices { get; set; } = new();
}

/// <summary>
/// The workspace holds sources (grammar, tokens, rules, params), derived
/// artifacts (nullable, forest, report, selection) and the dependency records
/// that explain why each derived value is still valid or must be recomputed.
/// </summary>
public sealed class Workspace
{
    public Grammar? Grammar;
    public List<Token> Tokens = new();
    public List<PrecedenceRule> Rules = new();
    public int Budget = 128;
    public string StartSymbol = "";
    public string Scenario = "main";

    public HashSet<string>? Nullable;
    public Forest? Forest;
    public AmbiguityReport? Report;
    public Selection Selection = new();
    public List<Decision> Decisions = new();

    public Dictionary<string, ArtifactRecord> Artifacts = new();
    public List<PendingEvent> PendingEvents = new();

    public static readonly Dictionary<string, string[]> Dependencies = new()
    {
        ["nullable"] = new[] { "grammar" },
        ["forest"] = new[] { "grammar", "tokens", "nullable", "params" },
        ["report"] = new[] { "forest", "rules" },
        ["selection"] = new[] { "report", "decisions" },
    };

    private static readonly string[] RecomputeOrder = { "nullable", "forest", "report", "selection" };

    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string HashOf(object value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json)))[..12].ToLowerInvariant();
    }

    private object SourceSnapshot(string artifactId) => artifactId switch
    {
        "grammar" => Grammar!,
        "tokens" => Tokens,
        "rules" => Rules,
        "params" => new { Budget, StartSymbol, Scenario },
        "decisions" => Decisions,
        "nullable" => Nullable!.OrderBy(x => x).ToArray(),
        "forest" => Forest!,
        "report" => Report!,
        "selection" => Selection,
        _ => throw new ArgumentOutOfRangeException(nameof(artifactId))
    };

    public string HashOfArtifact(string artifactId) => HashOf(SourceSnapshot(artifactId));

    public void MarkStaleFrom(string sourceId)
    {
        var reverse = new Dictionary<string, List<string>>();
        foreach (var (artifact, inputs) in Dependencies)
            foreach (var input in inputs)
                (reverse.TryGetValue(input, out var l) ? l : reverse[input] = new List<string>()).Add(artifact);
        var queue = new Queue<string>(reverse.GetValueOrDefault(sourceId) ?? new List<string>());
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!Artifacts.TryGetValue(cur, out var rec) || rec.Status == "stale") continue;
            rec.Status = "stale";
            foreach (var next in reverse.GetValueOrDefault(cur) ?? new List<string>()) queue.Enqueue(next);
        }
    }

    public void SetGrammar(Grammar g)
    {
        Grammar = g;
        Artifacts["grammar"] = new ArtifactRecord { Id = "grammar", Hash = HashOfArtifact("grammar") };
        MarkStaleFrom("grammar");
        PendingEvents.Add(new PendingEvent("grammar-imported", new()
        {
            ["grammarId"] = g.Id, ["version"] = g.Version,
            ["productionIds"] = g.Productions.Select(p => p.Id).ToArray(),
            ["hash"] = Artifacts["grammar"].Hash
        }));
    }

    public void SetTokens(IEnumerable<Token> tokens, string scenario, string startSymbol)
    {
        Tokens = tokens.ToList();
        Scenario = scenario;
        StartSymbol = startSymbol;
        Artifacts["tokens"] = new ArtifactRecord { Id = "tokens", Hash = HashOfArtifact("tokens") };
        Artifacts["params"] = new ArtifactRecord { Id = "params", Hash = HashOfArtifact("params") };
        MarkStaleFrom("tokens");
        MarkStaleFrom("params");
        PendingEvents.Add(new PendingEvent("tokens-imported", new()
        {
            ["scenario"] = scenario,
            ["startSymbol"] = startSymbol,
            ["count"] = Tokens.Count,
            ["spanUnit"] = "utf16-code-unit",
            ["keptRaw"] = Tokens.Where(t => t.Diagnostic != null).Select(t => t.Id).ToArray()
        }));
    }

    public void SetRules(IEnumerable<PrecedenceRule> rules)
    {
        Rules = rules.ToList();
        Artifacts["rules"] = new ArtifactRecord { Id = "rules", Hash = HashOfArtifact("rules") };
        MarkStaleFrom("rules");
        PendingEvents.Add(new PendingEvent("rules-imported", new()
        {
            ["rules"] = Rules.Select(r => new Dictionary<string, object?> { ["id"] = r.Id, ["version"] = r.Version, ["kind"] = r.Kind }).ToArray()
        }));
    }

    public void SetBudget(int budget)
    {
        Budget = budget;
        Artifacts["params"] = new ArtifactRecord { Id = "params", Hash = HashOfArtifact("params") };
        MarkStaleFrom("params");
    }

    /// <summary>Recompute stale artifacts only, emitting machine-readable reasons.</summary>
    public void Recompute()
    {
        foreach (var artifact in RecomputeOrder)
        {
            bool stale = !Artifacts.TryGetValue(artifact, out var rec) || rec.Status == "stale";
            if (!stale)
            {
                if (PendingEvents.Count > 0 || Artifacts.ContainsKey(artifact))
                    PendingEvents.Add(new PendingEvent("artifact-reused", new()
                    {
                        ["artifact"] = artifact,
                        ["reason"] = "inputs unchanged: " + string.Join(", ", InputsWithCurrentHashes(artifact).Select(kv => $"{kv.Key}@{kv.Value}"))
                    }));
                continue;
            }
            var changedInputs = ChangedInputs(artifact);
            switch (artifact)
            {
                case "nullable":
                {
                    var (nullable, events) = GrammarAnalysis.ComputeNullable(Grammar!);
                    Nullable = nullable;
                    PendingEvents.AddRange(events);
                    break;
                }
                case "forest":
                {
                    var result = new EarleyParser().Parse(Grammar!, Tokens, StartSymbol, Budget, Scenario);
                    Forest = result.Forest;
                    // parser events already cover nullable; drop duplicate nullable-computed
                    PendingEvents.AddRange(result.Events);
                    break;
                }
                case "report":
                {
                    Forest!.Reindex();
                    foreach (var p in Forest.Packed) { p.FilteredBy.Clear(); p.Conflicted = false; }
                    PendingEvents.AddRange(RuleEngine.Apply(Grammar!, Forest, Rules));
                    Report = AmbiguityReport.Build(Forest);
                    break;
                }
                case "selection":
                {
                    RebuildSelection();
                    break;
                }
            }
            Artifacts[artifact] = new ArtifactRecord
            {
                Id = artifact,
                Hash = HashOfArtifact(artifact),
                Inputs = InputsWithCurrentHashes(artifact),
                Status = "valid"
            };
            PendingEvents.Add(new PendingEvent("recompute", new()
            {
                ["artifact"] = artifact,
                ["reason"] = changedInputs.Count == 0 ? "initial computation" : "inputs changed: " + string.Join(", ", changedInputs),
                ["hash"] = Artifacts[artifact].Hash
            }));
        }
    }

    private Dictionary<string, string> InputsWithCurrentHashes(string artifact) =>
        Dependencies.GetValueOrDefault(artifact)?.ToDictionary(i => i, i => HashOfArtifact(i)) ?? new();

    private List<string> ChangedInputs(string artifact)
    {
        if (!Artifacts.TryGetValue(artifact, out var rec)) return new List<string>();
        var changed = new List<string>();
        foreach (var (input, usedHash) in rec.Inputs)
            if (HashOfArtifact(input) != usedHash) changed.Add(input);
        foreach (var input in Dependencies.GetValueOrDefault(artifact) ?? Array.Empty<string>())
            if (!rec.Inputs.ContainsKey(input)) changed.Add(input);
        return changed;
    }

    /// <summary>Explain why an artifact is still valid or would be recomputed.</summary>
    public Dictionary<string, object?> Explain(string artifact)
    {
        var exists = Artifacts.TryGetValue(artifact, out var rec);
        var inputs = (Dependencies.GetValueOrDefault(artifact) ?? Array.Empty<string>()).Select(input =>
        {
            var current = HashOfArtifact(input);
            var used = exists && rec!.Inputs.TryGetValue(input, out var h) ? h : null;
            return new Dictionary<string, object?>
            {
                ["input"] = input,
                ["recordedHash"] = used,
                ["currentHash"] = current,
                ["verdict"] = used == current ? "unchanged" : "changed"
            };
        }).ToArray();
        return new Dictionary<string, object?>
        {
            ["artifact"] = artifact,
            ["status"] = !exists ? "missing" : rec!.Status,
            ["hash"] = exists ? rec!.Hash : null,
            ["inputs"] = inputs,
            ["conclusion"] = !exists ? "needs computation"
                : rec!.Status == "valid" && inputs.All(i => (string)i["verdict"]! == "unchanged")
                    ? "still valid: all inputs unchanged"
                    : "needs recompute: " + string.Join(", ", inputs.Where(i => (string)i["verdict"]! == "changed").Select(i => (string)i["input"]!))
        };
    }

    public Decision Decide(string nodeId, string packedId, string action, string user, string baseVersion)
    {
        var decision = new Decision(
            $"dec:{EarleyParser.ShortHash($"{nodeId}|{packedId}|{action}|{user}|{Decisions.Count}")}",
            nodeId, packedId, action, user, baseVersion);
        Decisions.Add(decision);
        Artifacts["decisions"] = new ArtifactRecord { Id = "decisions", Hash = HashOfArtifact("decisions") };
        MarkStaleFrom("decisions");
        Recompute();
        var kept = Forest!.Symbols.First(s => s.Id == nodeId).PackedIds.Where(id => id != packedId).ToArray();
        PendingEvents.Add(new PendingEvent(action == "accept" ? "candidate-selected" : "candidate-rejected", new()
        {
            ["decisionId"] = decision.Id,
            ["nodeId"] = nodeId,
            ["packedId"] = packedId,
            ["user"] = user,
            ["baseVersion"] = baseVersion,
            ["keptAlternatives"] = kept,
            ["irreversible"] = false
        }));
        return decision;
    }

    private void RebuildSelection()
    {
        Selection = new Selection();
        foreach (var d in Decisions)
        {
            if (d.Action != "accept") continue;
            var node = Forest!.Symbols.FirstOrDefault(s => s.Id == d.NodeId);
            if (node == null) continue;
            Selection.Choices[d.NodeId] = new ChosenAlternative
            {
                PackedId = d.PackedId,
                User = d.User,
                DecisionId = d.Id,
                KeptAlternatives = node.PackedIds.Where(id => id != d.PackedId).ToList()
            };
        }
    }
}
