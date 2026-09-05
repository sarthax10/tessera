namespace Tessera.Crdt.Tests;

public class FractionalIndexTests
{
    private static int Cmp(string a, string b) => string.CompareOrdinal(a, b);

    [Fact]
    public void First_produces_a_canonical_key()
    {
        Assert.True(FractionalIndex.IsValid(FractionalIndex.First()));
    }

    [Fact]
    public void A_key_generated_after_another_sorts_after_it()
    {
        var a = FractionalIndex.First();
        var b = FractionalIndex.Between(a, null);

        Assert.True(Cmp(a, b) < 0);
    }

    [Fact]
    public void A_key_generated_before_another_sorts_before_it()
    {
        var b = FractionalIndex.First();
        var a = FractionalIndex.Between(null, b);

        Assert.True(Cmp(a, b) < 0);
    }

    [Fact]
    public void A_key_generated_between_two_others_sorts_between_them()
    {
        var a = FractionalIndex.First();
        var c = FractionalIndex.Between(a, null);
        var b = FractionalIndex.Between(a, c);

        Assert.True(Cmp(a, b) < 0);
        Assert.True(Cmp(b, c) < 0);
    }

    [Fact]
    public void Repeated_insertion_into_the_same_gap_stays_ordered()
    {
        var low = FractionalIndex.First();
        var high = FractionalIndex.Between(low, null);
        var inserted = new List<string>();

        for (var i = 0; i < 500; i++)
        {
            var mid = FractionalIndex.Between(low, high);

            Assert.True(Cmp(low, mid) < 0, $"iteration {i}: '{low}' !< '{mid}'");
            Assert.True(Cmp(mid, high) < 0, $"iteration {i}: '{mid}' !< '{high}'");
            Assert.True(FractionalIndex.IsValid(mid), $"iteration {i}: '{mid}' is not canonical");

            inserted.Add(mid);
            high = mid;
        }

        Assert.Equal(inserted.Count, inserted.Distinct().Count());
    }

    [Fact]
    public void Appending_repeatedly_keeps_keys_short()
    {
        // Every new shape appends, so growth here would make key length track document age.
        string? previous = null;
        var keys = new List<string>();

        for (var i = 0; i < 1_000; i++)
        {
            previous = FractionalIndex.Between(previous, null);
            keys.Add(previous);
        }

        Assert.Equal(keys, keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(keys[^1].Length <= 4, $"append-only growth reached length {keys[^1].Length}");
    }

    [Fact]
    public void Appending_past_a_digit_rollover_stays_ordered()
    {
        // A document only has to reach 62 shapes to widen the integer part.
        string? previous = null;
        var keys = new List<string>();

        for (var i = 0; i < 200; i++)
        {
            previous = FractionalIndex.Between(previous, null);
            keys.Add(previous);
        }

        Assert.Equal(keys, keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(keys, k => Assert.True(FractionalIndex.IsValid(k), $"'{k}' is not canonical"));
        Assert.Contains(keys, k => k.StartsWith('b'));
    }

    [Fact]
    public void Prepending_repeatedly_keeps_keys_ordered()
    {
        string? next = null;
        var keys = new List<string>();

        for (var i = 0; i < 500; i++)
        {
            next = FractionalIndex.Between(null, next);
            Assert.True(FractionalIndex.IsValid(next));
            keys.Insert(0, next);
        }

        Assert.Equal(keys, keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Random_insertions_preserve_the_intended_order()
    {
        var rng = new Random(20260905);
        var keys = new List<string> { FractionalIndex.First() };

        for (var i = 0; i < 800; i++)
        {
            var at = rng.Next(keys.Count + 1);
            var before = at > 0 ? keys[at - 1] : null;
            var after = at < keys.Count ? keys[at] : null;

            keys.Insert(at, FractionalIndex.Between(before, after));
        }

        Assert.Equal(keys, keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.True(FractionalIndex.IsValid(k)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("a")]
    [InlineData("a00")]
    [InlineData("a-")]
    [InlineData("a b")]
    [InlineData("A00000000000000000000000000")]
    public void Invalid_keys_are_rejected(string key)
    {
        Assert.False(FractionalIndex.IsValid(key));
        Assert.Throws<ArgumentException>(() => FractionalIndex.Between(key, null));
    }

    [Fact]
    public void Bounds_in_the_wrong_order_are_rejected()
    {
        var a = FractionalIndex.First();
        var b = FractionalIndex.Between(a, null);

        Assert.Throws<ArgumentException>(() => FractionalIndex.Between(b, a));
    }

    [Fact]
    public void Identical_bounds_are_rejected()
    {
        var a = FractionalIndex.First();

        Assert.Throws<ArgumentException>(() => FractionalIndex.Between(a, a));
    }

    [Fact]
    public void Every_digit_in_the_alphabet_is_in_ascii_order()
    {
        // The scheme rests on lexicographic byte comparison matching digit value.
        var digits = FractionalIndex.Digits;

        Assert.Equal(62, digits.Length);
        Assert.Equal(digits.Length, digits.Distinct().Count());

        for (var i = 1; i < digits.Length; i++)
            Assert.True(digits[i - 1] < digits[i], $"digit {i} breaks ASCII order");
    }
}
