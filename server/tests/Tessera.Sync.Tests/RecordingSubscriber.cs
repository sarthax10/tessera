using System.Collections.Concurrent;
using Tessera.Crdt;

namespace Tessera.Sync.Tests;

/// <summary>A participant that keeps everything the room sends it.</summary>
internal sealed class RecordingSubscriber(ulong replica) : IRoomSubscriber
{
    private readonly ConcurrentQueue<ServerMessage> _received = new();

    public ReplicaId Replica { get; } = new(replica);

    /// <summary>Makes every send fail, standing in for a dead connection.</summary>
    public bool Broken { get; set; }

    public IReadOnlyList<ServerMessage> Received => _received.ToList();

    public ValueTask SendAsync(ServerMessage message, CancellationToken cancellationToken = default)
    {
        if (Broken) throw new IOException("connection reset");

        _received.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    public IEnumerable<T> OfKind<T>() where T : ServerMessage => Received.OfType<T>();

    public T Single<T>() where T : ServerMessage => OfKind<T>().Single();

    public bool Any<T>() where T : ServerMessage => OfKind<T>().Any();

    public IReadOnlyList<Operation> BroadcastOperations =>
        OfKind<ServerMessage.Broadcast>().SelectMany(b => b.Operations).ToList();
}
