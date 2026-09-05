using System.Globalization;

namespace Tessera.Crdt;

/// <summary>Identifies a replica. Generated client-side so offline clients can keep working.</summary>
public readonly record struct ReplicaId(ulong Value) : IComparable<ReplicaId>
{
    public static readonly ReplicaId None = new(0);

    public static ReplicaId New()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt64(bytes);
        return new ReplicaId(value == 0 ? 1 : value);
    }

    public int CompareTo(ReplicaId other) => Value.CompareTo(other.Value);

    public static bool operator <(ReplicaId a, ReplicaId b) => a.Value < b.Value;
    public static bool operator >(ReplicaId a, ReplicaId b) => a.Value > b.Value;
    public static bool operator <=(ReplicaId a, ReplicaId b) => a.Value <= b.Value;
    public static bool operator >=(ReplicaId a, ReplicaId b) => a.Value >= b.Value;

    public override string ToString() => Value.ToString("x16", CultureInfo.InvariantCulture);

    public static ReplicaId Parse(string s) =>
        new(ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
