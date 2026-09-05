namespace Tessera.Crdt;

/// <summary>A read-only view of one shape's merged properties. Projected, never stored.</summary>
public sealed class Shape(ShapeId id, IReadOnlyDictionary<string, PropertyValue> properties)
{
    public ShapeId Id { get; } = id;

    public IReadOnlyDictionary<string, PropertyValue> Properties { get; } = properties;

    /// <summary>
    /// Missing properties read as null rather than throwing. A replica can legitimately hold a
    /// shape whose creating operations have not all arrived, and the renderer has to draw it.
    /// </summary>
    public PropertyValue this[string property] =>
        Properties.TryGetValue(property, out var value) ? value : PropertyValue.Null;

    public double Number(string property, double fallback = 0) =>
        this[property] is { Kind: PropertyKind.Number } v ? v.Number : fallback;

    public string? Text(string property) =>
        this[property] is { Kind: PropertyKind.Text } v ? v.Text : null;

    public string Index => Text(ShapeProperty.Index) ?? string.Empty;

    public override string ToString() => $"{Id} [{Properties.Count} props]";
}
