using System.Collections.Concurrent;
using Tessera.Crdt;

namespace Tessera.Sync;

/// <summary>Operation log held in process, for tests and single-node development runs.</summary>
public sealed class InMemoryBoardRepository : IBoardRepository
{
    private sealed class Log
    {
        public readonly List<Operation> Operations = [];
        public readonly HashSet<Hlc> Seen = [];
    }

    private readonly ConcurrentDictionary<BoardId, Log> _boards = new();

    public Task<IReadOnlyList<Operation>> LoadAsync(
        BoardId board, CancellationToken cancellationToken = default)
    {
        if (!_boards.TryGetValue(board, out var log))
            return Task.FromResult<IReadOnlyList<Operation>>([]);

        lock (log)
        {
            return Task.FromResult<IReadOnlyList<Operation>>(log.Operations.ToList());
        }
    }

    public Task<IReadOnlyList<Operation>> AppendAsync(
        BoardId board,
        IReadOnlyList<Operation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var log = _boards.GetOrAdd(board, _ => new Log());

        lock (log)
        {
            var accepted = new List<Operation>(operations.Count);

            foreach (var operation in operations)
            {
                if (!log.Seen.Add(operation.At)) continue;

                log.Operations.Add(operation);
                accepted.Add(operation);
            }

            return Task.FromResult<IReadOnlyList<Operation>>(accepted);
        }
    }
}
