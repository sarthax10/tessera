using Tessera.Crdt;

namespace Tessera.Sync;

/// <summary>
/// One connected participant. The room talks to this rather than to a socket, so the sync protocol
/// is testable without a network.
/// </summary>
public interface IRoomSubscriber
{
    ReplicaId Replica { get; }

    /// <summary>
    /// Must not block: the room's consumer is shared by everyone on the board, so one slow socket
    /// would stall every other participant's edits.
    /// </summary>
    ValueTask SendAsync(ServerMessage message, CancellationToken cancellationToken = default);
}
