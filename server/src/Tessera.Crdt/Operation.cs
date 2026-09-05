namespace Tessera.Crdt;

/// <summary>
/// A change to a board. The timestamp doubles as the operation's identity: an HLC already carries
/// the originating replica plus a counter that only increases there, so it is unique system-wide.
/// </summary>
public abstract record Operation(Hlc At, ShapeId Shape);

public sealed record SetProperty(Hlc At, ShapeId Shape, string Property, PropertyValue Value)
    : Operation(At, Shape);

public sealed record DeleteShape(Hlc At, ShapeId Shape)
    : Operation(At, Shape);
