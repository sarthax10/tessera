using System.Collections.Concurrent;
using Tessera.Sync;

namespace Tessera.Server;

/// <summary>
/// Keeps exactly one live <see cref="Room"/> per board. Two rooms would mean two replicas and two
/// orderings of the same document.
/// </summary>
public sealed class RoomRegistry(IBoardRepository repository) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<BoardId, Lazy<Task<Room>>> _rooms = new();

    // Lazy over the open task, because opening replays the whole log and a plain GetOrAdd with an
    // async factory would run it on every racing caller and keep only one result.
    public Task<Room> OpenAsync(BoardId board, CancellationToken cancellationToken = default) =>
        _rooms.GetOrAdd(
            board,
            key => new Lazy<Task<Room>>(
                () => Room.OpenAsync(key, repository, cancellationToken: cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _rooms.Values)
        {
            if (!entry.IsValueCreated) continue;

            try
            {
                await (await entry.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One room failing to close must not strand the others.
            }
        }

        _rooms.Clear();
    }
}
