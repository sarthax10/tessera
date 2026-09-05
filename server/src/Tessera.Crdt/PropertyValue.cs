using System.Collections.Immutable;
using System.Globalization;

namespace Tessera.Crdt;

public readonly record struct Point(double X, double Y);

public enum PropertyKind : byte
{
    Null = 0,
    Number = 1,
    Text = 2,
    Flag = 3,
    Points = 4,
}

/// <summary>A value held in one shape property.</summary>
public readonly struct PropertyValue : IEquatable<PropertyValue>
{
    public PropertyKind Kind { get; }
    public double Number { get; }
    public string? Text { get; }
    public ImmutableArray<Point> Points { get; }

    private PropertyValue(PropertyKind kind, double number, string? text, ImmutableArray<Point> points)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Points = points;
    }

    public static readonly PropertyValue Null =
        new(PropertyKind.Null, 0, null, ImmutableArray<Point>.Empty);

    public static PropertyValue Of(double value) =>
        new(PropertyKind.Number, value, null, ImmutableArray<Point>.Empty);

    public static PropertyValue Of(string value) =>
        new(PropertyKind.Text, 0, value, ImmutableArray<Point>.Empty);

    public static PropertyValue Of(bool value) =>
        new(PropertyKind.Flag, value ? 1 : 0, null, ImmutableArray<Point>.Empty);

    public static PropertyValue Of(ImmutableArray<Point> value) =>
        new(PropertyKind.Points, 0, null, value);

    public bool AsBool => Kind == PropertyKind.Flag && Number != 0;

    // Hand-written: generated equality would compare Points by underlying array reference.
    public bool Equals(PropertyValue other)
    {
        if (Kind != other.Kind) return false;

        return Kind switch
        {
            PropertyKind.Null => true,
            PropertyKind.Number or PropertyKind.Flag => Number.Equals(other.Number),
            PropertyKind.Text => Text == other.Text,
            PropertyKind.Points => Points.AsSpan().SequenceEqual(other.Points.AsSpan()),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is PropertyValue v && Equals(v);

    public override int GetHashCode() => Kind switch
    {
        PropertyKind.Number or PropertyKind.Flag => HashCode.Combine(Kind, Number),
        PropertyKind.Text => HashCode.Combine(Kind, Text),
        PropertyKind.Points => Points.Aggregate(
            HashCode.Combine(Kind, Points.Length), HashCode.Combine),
        _ => (int)Kind,
    };

    public static bool operator ==(PropertyValue a, PropertyValue b) => a.Equals(b);
    public static bool operator !=(PropertyValue a, PropertyValue b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        PropertyKind.Null => "null",
        PropertyKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        PropertyKind.Text => $"\"{Text}\"",
        PropertyKind.Flag => AsBool ? "true" : "false",
        PropertyKind.Points => $"[{Points.Length} points]",
        _ => "?",
    };
}
