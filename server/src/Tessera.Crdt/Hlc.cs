using System.Globalization;

namespace Tessera.Crdt;

/// <summary>
/// Hybrid logical clock timestamp. Orders events causally while staying close to wall time.
/// </summary>
public readonly record struct Hlc(long WallMillis, int Logical, ReplicaId Replica)
    : IComparable<Hlc>
{
    public static readonly Hlc MinValue = new(0, 0, ReplicaId.None);

    public int CompareTo(Hlc other)
    {
        var c = WallMillis.CompareTo(other.WallMillis);
        if (c != 0) return c;

        c = Logical.CompareTo(other.Logical);
        return c != 0 ? c : Replica.CompareTo(other.Replica);
    }

    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;

    /// <summary>Fixed-width hex, so lexicographic order matches <see cref="CompareTo"/>.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{WallMillis:x12}.{Logical:x4}.{Replica}");

    public static Hlc Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parts = text.Split('.');
        if (parts.Length != 3) throw new FormatException($"'{text}' is not a timestamp.");

        return new Hlc(
            long.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ReplicaId.Parse(parts[2]));
    }

    public static bool TryParse(string? text, out Hlc value)
    {
        try
        {
            value = text is null ? default : Parse(text);
            return text is not null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            value = default;
            return false;
        }
    }
}
