namespace Tessera.Crdt;

public sealed class ClockDriftException(Hlc remote, long localWallMillis, long maxDriftMillis)
    : Exception(
        $"Remote timestamp {remote} is {remote.WallMillis - localWallMillis}ms ahead of local " +
        $"time, exceeding the {maxDriftMillis}ms limit.")
{
    public Hlc Remote { get; } = remote;
    public long LocalWallMillis { get; } = localWallMillis;
    public long MaxDriftMillis { get; } = maxDriftMillis;
}

/// <summary>
/// Issues <see cref="Hlc"/> timestamps for one replica. Not thread-safe: each room actor owns one.
/// </summary>
public sealed class HybridLogicalClock
{
    public const long DefaultMaxDriftMillis = 5 * 60 * 1000;

    private readonly ReplicaId _replica;
    private readonly Func<long> _wallClock;
    private readonly long _maxDrift;

    private long _wall;
    private int _logical;

    public HybridLogicalClock(
        ReplicaId replica,
        Func<long>? wallClock = null,
        long maxDriftMillis = DefaultMaxDriftMillis)
    {
        _replica = replica;
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _maxDrift = maxDriftMillis;
    }

    public Hlc Current => new(_wall, _logical, _replica);

    /// <summary>Timestamps a local event, strictly after everything issued or observed so far.</summary>
    public Hlc Tick()
    {
        var now = _wallClock();

        if (now > _wall)
        {
            _wall = now;
            _logical = 0;
        }
        else
        {
            _logical++;
        }

        return Current;
    }

    /// <summary>
    /// Merges a peer's timestamp and issues one ordered after both it and the local state.
    /// </summary>
    public Hlc Observe(Hlc remote)
    {
        var now = _wallClock();

        // Bounded so one machine with a badly wrong clock cannot drag every replica it syncs
        // with into the future permanently.
        if (remote.WallMillis - now > _maxDrift)
            throw new ClockDriftException(remote, now, _maxDrift);

        var previousWall = _wall;
        var wall = Math.Max(Math.Max(previousWall, remote.WallMillis), now);

        _logical = (wall == previousWall, wall == remote.WallMillis) switch
        {
            (true, true) => Math.Max(_logical, remote.Logical) + 1,
            (true, false) => _logical + 1,
            (false, true) => remote.Logical + 1,
            (false, false) => 0,
        };

        _wall = wall;
        return Current;
    }
}
