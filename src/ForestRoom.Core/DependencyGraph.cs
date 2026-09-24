namespace ForestRoom.Core;

/// <summary>依赖图传播重算：源数据改动只使受影响的后代失效，
/// 未受影响的结果保持有效，并且两者都能给出机器可读的理由。</summary>
public sealed class DependencyGraph
{
    public sealed class Artifact
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public int Version { get; set; } = 1;
        public HashSet<string> DependsOn { get; } = new();
        public bool Dirty { get; set; }
        public string? DirtyReason { get; set; }
    }

    private readonly Dictionary<string, Artifact> _artifacts = new();
    public IReadOnlyDictionary<string, Artifact> Artifacts => _artifacts;

    public void Register(string id, string kind, params string[] dependsOn)
    {
        if (!_artifacts.TryGetValue(id, out var a))
        {
            a = new Artifact { Id = id, Kind = kind };
            _artifacts[id] = a;
        }
        a.DependsOn.UnionWith(dependsOn);
    }

    public void BumpVersion(string id)
    {
        if (_artifacts.TryGetValue(id, out var a)) a.Version++;
    }

    /// <summary>从变更源沿反向边传播，只标记受影响节点；返回失效与保持有效两类事件。</summary>
    public List<ForestEvent> Invalidate(string changedId, string changeDescription)
    {
        var events = new List<ForestEvent>();
        long seq = 0;
        var affected = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(changedId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var a in _artifacts.Values)
            {
                if (a.DependsOn.Contains(cur) && affected.Add(a.Id))
                {
                    a.Dirty = true;
                    a.DirtyReason = $"{changedId} changed: {changeDescription}";
                    queue.Enqueue(a.Id);
                    events.Add(ForestEvent.Of(++seq, EventKinds.DependencyInvalidated, a.Id,
                        $"artifact {a.Id} ({a.Kind}) invalidated: depends on {cur}; {changeDescription}",
                        ("changedSource", changedId), ("via", cur)));
                }
            }
        }
        foreach (var a in _artifacts.Values.Where(a => !affected.Contains(a.Id) && a.Id != changedId))
        {
            if (!a.Dirty)
                events.Add(ForestEvent.Of(++seq, EventKinds.DependencyKeptValid, a.Id,
                    $"artifact {a.Id} ({a.Kind}) still valid: no dependency path from {changedId}",
                    ("changedSource", changedId)));
        }
        return events;
    }

    public string Explain(string id)
    {
        if (!_artifacts.TryGetValue(id, out var a)) return $"{id}: unknown artifact";
        return a.Dirty
            ? $"{id} ({a.Kind} v{a.Version}): NEEDS RECOMPUTE — {a.DirtyReason}"
            : $"{id} ({a.Kind} v{a.Version}): VALID — all dependencies unchanged";
    }
}
