using System.Globalization;

namespace Tessera.Crdt;

public readonly record struct ShapeId(string Value) : IComparable<ShapeId>
{
    public static ShapeId For(ReplicaId replica, long counter) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{replica}-{counter:x}"));

    public int CompareTo(ShapeId other) => string.CompareOrdinal(Value, other.Value);

    public static bool operator <(ShapeId a, ShapeId b) => a.CompareTo(b) < 0;
    public static bool operator >(ShapeId a, ShapeId b) => a.CompareTo(b) > 0;
    public static bool operator <=(ShapeId a, ShapeId b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ShapeId a, ShapeId b) => a.CompareTo(b) >= 0;

    public override string ToString() => Value;
}

/// <summary>
/// Property names are strings, not an enum, so a client can add a shape attribute without a server
/// deployment. Last-writer-wins does not need to understand what it is ordering.
/// </summary>
public static class ShapeProperty
{
    public const string Kind = "kind";
    public const string Parent = "parent";
    public const string Index = "index";
    public const string X = "x";
    public const string Y = "y";
    public const string Width = "w";
    public const string Height = "h";
    public const string Rotation = "rotation";
    public const string Fill = "fill";
    public const string Stroke = "stroke";
    public const string StrokeWidth = "strokeWidth";
    public const string Opacity = "opacity";
    public const string Points = "points";
    public const string Text = "text";
}
