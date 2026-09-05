namespace Tessera.Crdt;

/// <summary>
/// Order keys for z-ordering. A key can be generated between any two others, so reordering a shape
/// rewrites one key instead of renumbering everything above it.
/// </summary>
/// <remarks>
/// A key is an integer part followed by an optional fraction. The leading character encodes the
/// integer part's sign and length: 'a'..'z' are positive lengths 2..27, 'Z'..'A' negative. Appending
/// increments the integer part rather than extending the string, which keeps the common case at
/// constant length. Fractions still grow when shapes are repeatedly dropped into the same gap;
/// keys are rebalanced during snapshot compaction.
/// </remarks>
public static class FractionalIndex
{
    /// <summary>Base-62 digits in ASCII order, so string comparison is digit comparison.</summary>
    public const string Digits =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static char Zero => Digits[0];
    private static char Max => Digits[^1];

    private static readonly string SmallestInteger = "A" + new string(Digits[0], 26);

    public static string First() => $"a{Zero}";

    /// <summary>
    /// A key ordered strictly between the bounds. A null bound means no neighbour on that side.
    /// </summary>
    public static string Between(string? before, string? after)
    {
        if (before is not null) Validate(before, nameof(before));
        if (after is not null) Validate(after, nameof(after));

        if (before is not null && after is not null && string.CompareOrdinal(before, after) >= 0)
            throw new ArgumentException(
                $"Bounds must be strictly ordered, but '{before}' >= '{after}'.", nameof(before));

        if (before is null)
            return after is null ? First() : KeyBelow(after);

        return after is null ? KeyAbove(before) : KeyBetween(before, after);
    }

    private static string KeyBelow(string after)
    {
        var integer = IntegerPart(after);

        if (integer == SmallestInteger)
            return integer + Midpoint("", after[integer.Length..]);

        if (string.CompareOrdinal(integer, after) < 0) return integer;

        return DecrementInteger(integer)
               ?? throw new InvalidOperationException(
                   "Order key space exhausted below; the document needs rebalancing.");
    }

    private static string KeyAbove(string before)
    {
        var integer = IntegerPart(before);

        return IncrementInteger(integer) ?? integer + Midpoint(before[integer.Length..], null);
    }

    private static string KeyBetween(string before, string after)
    {
        var integerBefore = IntegerPart(before);
        var integerAfter = IntegerPart(after);

        if (integerBefore == integerAfter)
        {
            return integerBefore
                   + Midpoint(before[integerBefore.Length..], after[integerAfter.Length..]);
        }

        var incremented = IncrementInteger(integerBefore)
            ?? throw new InvalidOperationException(
                "Order key space exhausted above; the document needs rebalancing.");

        return string.CompareOrdinal(incremented, after) < 0
            ? incremented
            : integerBefore + Midpoint(before[integerBefore.Length..], null);
    }

    /// <summary>
    /// Canonical means base-62 throughout with no trailing zero, so byte equality and value
    /// equality are the same thing.
    /// </summary>
    public static bool IsValid(string key)
    {
        if (key.Length == 0 || key == SmallestInteger) return false;

        var integerLength = IntegerLengthOrZero(key[0]);
        if (integerLength == 0 || key.Length < integerLength) return false;

        for (var i = 1; i < key.Length; i++)
            if (!Digits.Contains(key[i])) return false;

        var fraction = key[integerLength..];
        return fraction.Length == 0 || fraction[^1] != Zero;
    }

    private static void Validate(string key, string paramName)
    {
        if (!IsValid(key))
            throw new ArgumentException($"'{key}' is not a canonical fractional index.", paramName);
    }

    private static int IntegerLengthOrZero(char head) => head switch
    {
        >= 'a' and <= 'z' => head - 'a' + 2,
        >= 'A' and <= 'Z' => 'Z' - head + 2,
        _ => 0,
    };

    private static string IntegerPart(string key) => key[..IntegerLengthOrZero(key[0])];

    private static string? IncrementInteger(string integer)
    {
        var head = integer[0];
        var digits = integer[1..].ToCharArray();

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var next = Digits.IndexOf(digits[i]) + 1;
            if (next < Digits.Length)
            {
                digits[i] = Digits[next];
                return head + new string(digits);
            }

            digits[i] = Zero;
        }

        if (head == 'z') return null;
        if (head == 'Z') return $"a{Zero}";

        var wider = (char)(head + 1);

        return wider > 'a'
            ? wider + new string(digits) + Zero
            : wider + new string(digits[..^1]);
    }

    private static string? DecrementInteger(string integer)
    {
        var head = integer[0];
        var digits = integer[1..].ToCharArray();

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var next = Digits.IndexOf(digits[i]) - 1;
            if (next >= 0)
            {
                digits[i] = Digits[next];
                return head + new string(digits);
            }

            digits[i] = Max;
        }

        if (head == 'A') return null;
        if (head == 'a') return $"Z{Max}";

        var wider = (char)(head - 1);

        return wider < 'Z'
            ? wider + new string(digits) + Max
            : wider + new string(digits[..^1]);
    }

    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : Zero) == b[n]) n++;

            if (n > 0)
                return string.Concat(b.AsSpan(0, n), Midpoint(n < a.Length ? a[n..] : "", b[n..]));
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0]) : Digits.Length;

        // (x + y + 1) / 2 matches JavaScript's Math.round((x + y) / 2) for integers; .NET's
        // Math.Round would disagree on exact halves and the two ports must emit identical keys.
        if (digitB - digitA > 1) return Digits[(digitA + digitB + 1) / 2].ToString();

        if (b is not null && b.Length > 1) return b[..1];

        return Digits[digitA] + Midpoint(a.Length > 0 ? a[1..] : "", null);
    }
}
