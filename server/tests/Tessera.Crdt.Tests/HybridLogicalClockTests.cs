namespace Tessera.Crdt.Tests;

public class HybridLogicalClockTests
{
    private static (HybridLogicalClock, FakeClock) Make(ulong replica = 7, long start = 1_000_000)
    {
        var fake = new FakeClock(start);
        return (new HybridLogicalClock(new ReplicaId(replica), fake.Read), fake);
    }

    [Fact]
    public void Tick_within_one_millisecond_advances_the_logical_counter()
    {
        var (clock, _) = Make();

        var a = clock.Tick();
        var b = clock.Tick();
        var c = clock.Tick();

        Assert.Equal(a.WallMillis, b.WallMillis);
        Assert.Equal(0, a.Logical);
        Assert.Equal(1, b.Logical);
        Assert.Equal(2, c.Logical);
        Assert.True(a < b && b < c);
    }

    [Fact]
    public void Tick_resets_the_logical_counter_once_physical_time_moves()
    {
        var (clock, fake) = Make();

        clock.Tick();
        clock.Tick();
        Assert.Equal(1, clock.Current.Logical);

        fake.Advance(1);
        var after = clock.Tick();

        Assert.Equal(0, after.Logical);
        Assert.Equal(fake.Now, after.WallMillis);
    }

    [Fact]
    public void Tick_stays_monotonic_when_the_local_clock_jumps_backwards()
    {
        // NTP corrections and VM suspend/resume both move the system clock backwards.
        var (clock, fake) = Make();

        var before = clock.Tick();
        fake.Advance(-5_000);
        var after = clock.Tick();

        Assert.True(after > before);
        Assert.Equal(before.WallMillis, after.WallMillis);
        Assert.Equal(before.Logical + 1, after.Logical);
    }

    [Fact]
    public void Observe_orders_the_result_after_both_local_state_and_the_remote_timestamp()
    {
        var (clock, _) = Make();
        var local = clock.Tick();
        var remote = new Hlc(local.WallMillis + 500, 3, new ReplicaId(99));

        var merged = clock.Observe(remote);

        Assert.True(merged > local);
        Assert.True(merged > remote);
    }

    [Fact]
    public void Observe_adopts_a_remote_wall_time_that_is_ahead_of_local()
    {
        var (clock, fake) = Make();
        clock.Tick();
        var remote = new Hlc(fake.Now + 250, 0, new ReplicaId(99));

        var merged = clock.Observe(remote);

        Assert.Equal(remote.WallMillis, merged.WallMillis);
        Assert.Equal(remote.Logical + 1, merged.Logical);
    }

    [Fact]
    public void Observe_keeps_the_logical_counter_bounded_when_physical_time_leads()
    {
        var (clock, fake) = Make();
        for (var i = 0; i < 10; i++) clock.Tick();
        Assert.Equal(9, clock.Current.Logical);

        fake.Advance(1_000);
        var merged = clock.Observe(new Hlc(fake.Now - 500, 4, new ReplicaId(99)));

        Assert.Equal(0, merged.Logical);
        Assert.Equal(fake.Now, merged.WallMillis);
    }

    [Fact]
    public void Causally_ordered_events_across_replicas_are_ordered_by_timestamp()
    {
        // Every step is inside one millisecond, so the ordering rests entirely on the counter.
        var shared = new FakeClock();
        var a = new HybridLogicalClock(new ReplicaId(1), shared.Read);
        var b = new HybridLogicalClock(new ReplicaId(2), shared.Read);

        var sent = a.Tick();
        var received = b.Observe(sent);
        var replied = b.Tick();
        var acked = a.Observe(replied);

        Assert.True(sent < received);
        Assert.True(received < replied);
        Assert.True(replied < acked);
    }

    [Fact]
    public void Concurrent_events_in_the_same_millisecond_are_ordered_by_replica_id()
    {
        var shared = new FakeClock();
        var low = new HybridLogicalClock(new ReplicaId(1), shared.Read).Tick();
        var high = new HybridLogicalClock(new ReplicaId(2), shared.Read).Tick();

        Assert.Equal(low.WallMillis, high.WallMillis);
        Assert.Equal(low.Logical, high.Logical);
        Assert.True(low < high);
    }

    [Fact]
    public void Observe_rejects_a_remote_clock_beyond_the_drift_limit()
    {
        var (clock, fake) = Make();
        var wild = new Hlc(
            fake.Now + HybridLogicalClock.DefaultMaxDriftMillis + 1, 0, new ReplicaId(99));

        var ex = Assert.Throws<ClockDriftException>(() => clock.Observe(wild));

        Assert.Equal(wild, ex.Remote);
        Assert.Equal(0, clock.Current.WallMillis);
    }

    [Fact]
    public void Observe_accepts_drift_up_to_the_limit()
    {
        var (clock, fake) = Make();
        var edge = new Hlc(
            fake.Now + HybridLogicalClock.DefaultMaxDriftMillis, 0, new ReplicaId(99));

        var merged = clock.Observe(edge);

        Assert.Equal(edge.WallMillis, merged.WallMillis);
    }

    [Fact]
    public void Text_form_sorts_identically_to_the_comparison_operator()
    {
        // Postgres range-scans these as text; disagreeing orders would return the wrong rows.
        var stamps = new List<Hlc>();
        var shared = new FakeClock();
        var clock = new HybridLogicalClock(new ReplicaId(0xABCD), shared.Read);

        for (var i = 0; i < 200; i++)
        {
            if (i % 3 == 0) shared.Advance(1);
            stamps.Add(clock.Tick());
        }

        stamps.Add(new Hlc(shared.Now, 0, new ReplicaId(1)));
        stamps.Add(new Hlc(shared.Now, 0, new ReplicaId(ulong.MaxValue)));

        Assert.Equal(
            stamps.OrderBy(x => x).ToList(),
            stamps.OrderBy(x => x.ToString(), StringComparer.Ordinal).ToList());
    }
}
