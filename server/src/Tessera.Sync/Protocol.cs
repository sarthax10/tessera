using System.Text.Json.Serialization;
using Tessera.Crdt;

namespace Tessera.Sync;

/// <summary>
/// Client traffic. Document messages are durable and ordered; presence is ephemeral and droppable.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Join), "join")]
[JsonDerivedType(typeof(Push), "push")]
[JsonDerivedType(typeof(Presence), "presence")]
public abstract record ClientMessage
{
    private ClientMessage() { }

    public sealed record Join(BoardId Board, ReplicaId Replica, VersionVector Have) : ClientMessage;

    public sealed record Push(IReadOnlyList<Operation> Operations) : ClientMessage;

    public sealed record Presence(PresenceState State) : ClientMessage;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Ack), "ack")]
[JsonDerivedType(typeof(Broadcast), "broadcast")]
[JsonDerivedType(typeof(PeerPresence), "peerPresence")]
[JsonDerivedType(typeof(PeerLeft), "peerLeft")]
[JsonDerivedType(typeof(Rejected), "rejected")]
public abstract record ServerMessage
{
    private ServerMessage() { }

    /// <summary>Answers a join with exactly the operations the client lacks.</summary>
    public sealed record Welcome(
        BoardId Board,
        IReadOnlyList<Operation> Missing,
        IReadOnlyList<PresenceState> Peers) : ServerMessage;

    /// <summary>
    /// Confirms operations are durable so the client can drop them from its outbox. Separate from
    /// <see cref="Broadcast"/> because authors do not receive their own operations back.
    /// </summary>
    public sealed record Ack(IReadOnlyList<Hlc> Accepted) : ServerMessage;

    public sealed record Broadcast(IReadOnlyList<Operation> Operations) : ServerMessage;

    public sealed record PeerPresence(PresenceState State) : ServerMessage;

    public sealed record PeerLeft(ReplicaId Replica) : ServerMessage;

    public sealed record Rejected(string Reason) : ServerMessage;
}

public sealed record PresenceState(
    ReplicaId Replica,
    string DisplayName,
    string Colour,
    double CursorX,
    double CursorY,
    IReadOnlyList<ShapeId> Selection)
{
    // Hand-written: generated equality would compare Selection by reference, so dropping
    // unchanged presence updates would silently never fire.
    public bool Equals(PresenceState? other) =>
        other is not null
        && Replica == other.Replica
        && DisplayName == other.DisplayName
        && Colour == other.Colour
        && CursorX.Equals(other.CursorX)
        && CursorY.Equals(other.CursorY)
        && Selection.SequenceEqual(other.Selection);

    public override int GetHashCode() =>
        HashCode.Combine(Replica, DisplayName, Colour, CursorX, CursorY, Selection.Count);
}
