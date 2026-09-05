using Tessera.Crdt;

namespace Tessera.Sync;

/// <summary>
/// What a replica has seen, as the highest timestamp observed from each peer. One entry per replica
/// is enough because an HLC only increases on the replica that issued it, so knowing the highest
/// implies having seen everything before it.
/// </summary>
public sealed class VersionVector
{
    private readonly Dictionary<ReplicaId, Hlc> _frontier;

    public VersionVector() => _frontier = [];

    public VersionVector(IEnumerable<KeyValuePair<ReplicaId, Hlc>> entries) =>
        _frontier = new Dictionary<ReplicaId, Hlc>(entries);

    public IReadOnlyDictionary<ReplicaId, Hlc> Entries => _frontier;

    public bool Covers(Hlc timestamp) =>
        _frontier.TryGetValue(timestamp.Replica, out var seen) && timestamp <= seen;

    /// <summary>Records a timestamp as seen. Returns false if it was already covered.</summary>
    public bool Observe(Hlc timestamp)
    {
        if (Covers(timestamp)) return false;

        _frontier[timestamp.Replica] = timestamp;
        return true;
    }

    public void ObserveAll(IEnumerable<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        foreach (var operation in operations) Observe(operation.At);
    }

    public IReadOnlyList<Operation> Missing(IEnumerable<Operation> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates.Where(operation => !Covers(operation.At)).ToList();
    }
}
