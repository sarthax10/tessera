using Tessera.Crdt;

namespace Tessera.Sync;

/// <summary>
/// Durable storage for a board's operation log. The log is the source of truth; board state is a
/// fold over it.
/// </summary>
public interface IBoardRepository
{
    Task<IReadOnlyList<Operation>> LoadAsync(
        BoardId board, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends operations, ignoring ones already stored, and returns those that were new. Must be
    /// idempotent: a client reconnecting mid-flush resends operations already committed.
    /// </summary>
    Task<IReadOnlyList<Operation>> AppendAsync(
        BoardId board,
        IReadOnlyList<Operation> operations,
        CancellationToken cancellationToken = default);
}
