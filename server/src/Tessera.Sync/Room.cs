using System.Threading.Channels;
using Tessera.Crdt;

namespace Tessera.Sync;

/// <summary>
/// One live board. Every message for it flows through a single channel consumer that owns the
/// replica, so ordering is deterministic and no lock is needed. Concurrency lives between rooms.
/// </summary>
public sealed class Room : IAsyncDisposable
{
    private abstract record Command
    {
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record JoinCommand(IRoomSubscriber Subscriber, VersionVector Have) : Command;
    private sealed record LeaveCommand(ReplicaId Replica) : Command;
    private sealed record PushCommand(ReplicaId From, IReadOnlyList<Operation> Operations) : Command;
    private sealed record PresenceCommand(ReplicaId From, PresenceState State) : Command;

    private readonly Channel<Command> _inbox =
        Channel.CreateUnbounded<Command>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    private readonly Dictionary<ReplicaId, IRoomSubscriber> _subscribers = [];
    private readonly Dictionary<ReplicaId, PresenceState> _presence = [];
    private readonly BoardReplica _replica = new();
    private readonly VersionVector _seen = new();
    private readonly List<Operation> _log = [];

    private readonly IBoardRepository _repository;
    private readonly HybridLogicalClock _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    public BoardId Board { get; }

    private Room(BoardId board, IBoardRepository repository, ReplicaId serverReplica)
    {
        Board = board;
        _repository = repository;
        _clock = new HybridLogicalClock(serverReplica);
        _pump = Task.Run(ConsumeAsync);
    }

    public static async Task<Room> OpenAsync(
        BoardId board,
        IBoardRepository repository,
        ReplicaId? serverReplica = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var room = new Room(board, repository, serverReplica ?? ReplicaId.New());
        var history = await repository.LoadAsync(board, cancellationToken).ConfigureAwait(false);

        room._log.AddRange(history);
        room._replica.ApplyAll(history);
        room._seen.ObserveAll(history);

        return room;
    }

    /// <summary>
    /// The merged document. Racy while a command is in flight, so callers needing a consistent view
    /// must await the command that produced it.
    /// </summary>
    public IReadOnlyList<Shape> Shapes => _replica.Shapes;

    public int SubscriberCount => _subscribers.Count;

    public Task JoinAsync(IRoomSubscriber subscriber, VersionVector have)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        return SubmitAsync(new JoinCommand(subscriber, have ?? new VersionVector()));
    }

    public Task LeaveAsync(ReplicaId replica) => SubmitAsync(new LeaveCommand(replica));

    public Task PushAsync(ReplicaId from, IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        return SubmitAsync(new PushCommand(from, operations));
    }

    public Task UpdatePresenceAsync(ReplicaId from, PresenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return SubmitAsync(new PresenceCommand(from, state));
    }

    private Task SubmitAsync(Command command)
    {
        if (!_inbox.Writer.TryWrite(command))
            return Task.FromException(new ObjectDisposedException(nameof(Room)));

        return command.Completed.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var command in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(command).ConfigureAwait(false);
                command.Completed.TrySetResult();
            }
            catch (Exception ex)
            {
                // One bad command must not take the room down with everyone else's work in it.
                command.Completed.TrySetException(ex);
            }
        }
    }

    private Task HandleAsync(Command command) => command switch
    {
        JoinCommand join => HandleJoinAsync(join),
        LeaveCommand leave => HandleLeaveAsync(leave),
        PushCommand push => HandlePushAsync(push),
        PresenceCommand presence => HandlePresenceAsync(presence),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private async Task HandleJoinAsync(JoinCommand join)
    {
        _subscribers[join.Subscriber.Replica] = join.Subscriber;

        var missing = join.Have.Missing(_log);
        var peers = _presence.Values.Where(p => p.Replica != join.Subscriber.Replica).ToList();

        await SendAsync(join.Subscriber, new ServerMessage.Welcome(Board, missing, peers))
            .ConfigureAwait(false);
    }

    private async Task HandleLeaveAsync(LeaveCommand leave)
    {
        _subscribers.Remove(leave.Replica);
        _presence.Remove(leave.Replica);

        await BroadcastAsync(new ServerMessage.PeerLeft(leave.Replica), except: leave.Replica)
            .ConfigureAwait(false);
    }

    private async Task HandlePushAsync(PushCommand push)
    {
        var accepted = new List<Operation>(push.Operations.Count);

        foreach (var operation in push.Operations)
        {
            // A client may only submit operations stamped with its own replica id. Otherwise
            // anyone could forge writes as another participant, or fabricate a future timestamp
            // under their identity that none of their genuine edits could outrank.
            if (operation.At.Replica != push.From)
            {
                await Reject(push.From, $"Operation {operation.At} is not from replica {push.From}.")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                _clock.Observe(operation.At);
            }
            catch (ClockDriftException ex)
            {
                await Reject(push.From, ex.Message).ConfigureAwait(false);
                return;
            }

            if (_seen.Covers(operation.At)) continue;

            accepted.Add(operation);
        }

        if (accepted.Count == 0)
        {
            await AckAsync(push.From, []).ConfigureAwait(false);
            return;
        }

        // Persisted before acknowledging and before broadcasting, so nobody is told work is safe
        // that was then lost.
        var stored = await _repository
            .AppendAsync(Board, accepted, _shutdown.Token)
            .ConfigureAwait(false);

        foreach (var operation in stored)
        {
            _replica.Apply(operation);
            _seen.Observe(operation.At);
            _log.Add(operation);
        }

        await AckAsync(push.From, stored.Select(o => o.At).ToList()).ConfigureAwait(false);
        await BroadcastAsync(new ServerMessage.Broadcast(stored), except: push.From)
            .ConfigureAwait(false);
    }

    private async Task HandlePresenceAsync(PresenceCommand presence)
    {
        if (presence.State.Replica != presence.From)
        {
            await Reject(presence.From, "Presence must be for the sending replica.")
                .ConfigureAwait(false);
            return;
        }

        _presence[presence.From] = presence.State;

        await BroadcastAsync(new ServerMessage.PeerPresence(presence.State), except: presence.From)
            .ConfigureAwait(false);
    }

    private Task AckAsync(ReplicaId replica, IReadOnlyList<Hlc> accepted) =>
        _subscribers.TryGetValue(replica, out var subscriber)
            ? SendAsync(subscriber, new ServerMessage.Ack(accepted))
            : Task.CompletedTask;

    private Task Reject(ReplicaId replica, string reason) =>
        _subscribers.TryGetValue(replica, out var subscriber)
            ? SendAsync(subscriber, new ServerMessage.Rejected(reason))
            : Task.CompletedTask;

    private async Task BroadcastAsync(ServerMessage message, ReplicaId except)
    {
        foreach (var subscriber in _subscribers.Values.ToList())
        {
            if (subscriber.Replica == except) continue;

            await SendAsync(subscriber, message).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(IRoomSubscriber subscriber, ServerMessage message)
    {
        try
        {
            await subscriber.SendAsync(message, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dead connection is routine. Drop it; the client resyncs from its vector on return.
            _subscribers.Remove(subscriber.Replica);
            _presence.Remove(subscriber.Replica);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
