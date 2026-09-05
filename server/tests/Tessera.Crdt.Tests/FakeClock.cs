namespace Tessera.Crdt.Tests;

/// <summary>
/// A wall clock the test drives by hand. The interesting cases — two events in one millisecond, an
/// NTP correction moving time backwards, a peer minutes ahead — cannot be reproduced otherwise.
/// </summary>
internal sealed class FakeClock(long start = 1_000_000)
{
    public long Now { get; set; } = start;

    public long Read() => Now;

    public void Advance(long millis) => Now += millis;
}
