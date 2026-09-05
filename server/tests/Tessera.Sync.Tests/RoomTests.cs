using Tessera.Crdt;

namespace Tessera.Sync.Tests;

public class RoomTests
{
    private static readonly BoardId Board = new("board-1");

    private static SetProperty Op(HybridLogicalClock clock, string shape, string property, string value) =>
        new(clock.Tick(), new ShapeId(shape), property, PropertyValue.Of(value));

    private static HybridLogicalClock ClockFor(ulong replica) => new(new ReplicaId(replica));

    [Fact]
    public async Task Joining_an_empty_board_returns_nothing_to_catch_up_on()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);

        await room.JoinAsync(alice, new VersionVector());

        var welcome = alice.Single<ServerMessage.Welcome>();
        Assert.Equal(Board, welcome.Board);
        Assert.Empty(welcome.Missing);
        Assert.Empty(welcome.Peers);
    }

    [Fact]
    public async Task Pushed_operations_are_persisted_acknowledged_and_applied()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var clock = ClockFor(1);

        await room.JoinAsync(alice, new VersionVector());
        var op = Op(clock, "s1", ShapeProperty.Kind, "rect");
        await room.PushAsync(alice.Replica, [op]);

        Assert.Equal([op.At], alice.Single<ServerMessage.Ack>().Accepted);
        Assert.Single(await repository.LoadAsync(Board));
        Assert.Single(room.Shapes);
    }

    [Fact]
    public async Task Operations_reach_other_participants_but_not_their_author()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);
        var clock = ClockFor(1);

        await room.JoinAsync(alice, new VersionVector());
        await room.JoinAsync(bob, new VersionVector());
        var op = Op(clock, "s1", ShapeProperty.Fill, "#f00");
        await room.PushAsync(alice.Replica, [op]);

        Assert.Equal([op], bob.BroadcastOperations);
        Assert.False(alice.Any<ServerMessage.Broadcast>());
    }

    [Fact]
    public async Task Resending_an_operation_neither_duplicates_it_nor_rebroadcasts_it()
    {
        // A client reconnecting mid-flush resends work already committed.
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);
        var clock = ClockFor(1);

        await room.JoinAsync(alice, new VersionVector());
        await room.JoinAsync(bob, new VersionVector());
        var op = Op(clock, "s1", ShapeProperty.Kind, "rect");

        await room.PushAsync(alice.Replica, [op]);
        await room.PushAsync(alice.Replica, [op]);
        await room.PushAsync(alice.Replica, [op]);

        Assert.Single(await repository.LoadAsync(Board));
        Assert.Single(bob.BroadcastOperations);
    }

    [Fact]
    public async Task An_operation_stamped_with_another_replicas_id_is_rejected()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var forged = Op(ClockFor(2), "s1", ShapeProperty.Kind, "rect");

        await room.JoinAsync(alice, new VersionVector());
        await room.PushAsync(alice.Replica, [forged]);

        Assert.True(alice.Any<ServerMessage.Rejected>());
        Assert.Empty(await repository.LoadAsync(Board));
        Assert.Empty(room.Shapes);
    }

    [Fact]
    public async Task A_returning_client_receives_only_what_it_missed()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var clock = ClockFor(1);

        await room.JoinAsync(alice, new VersionVector());
        var early = Op(clock, "s1", ShapeProperty.Kind, "rect");
        await room.PushAsync(alice.Replica, [early]);

        var have = new VersionVector();
        have.Observe(early.At);

        var late = Op(clock, "s2", ShapeProperty.Kind, "ellipse");
        await room.PushAsync(alice.Replica, [late]);

        var bob = new RecordingSubscriber(2);
        await room.JoinAsync(bob, have);

        Assert.Equal([late], bob.Single<ServerMessage.Welcome>().Missing);
    }

    [Fact]
    public async Task A_board_reopened_from_storage_replays_its_history()
    {
        var repository = new InMemoryBoardRepository();
        var clock = ClockFor(1);
        var alice = new RecordingSubscriber(1);

        await using (var first = await Room.OpenAsync(Board, repository))
        {
            await first.JoinAsync(alice, new VersionVector());
            await first.PushAsync(alice.Replica, [
                Op(clock, "s1", ShapeProperty.Kind, "rect"),
                Op(clock, "s1", ShapeProperty.Fill, "#0f0"),
            ]);
        }

        await using var reopened = await Room.OpenAsync(Board, repository);

        var shape = Assert.Single(reopened.Shapes);
        Assert.Equal("rect", shape.Text(ShapeProperty.Kind));
        Assert.Equal("#0f0", shape.Text(ShapeProperty.Fill));
    }

    [Fact]
    public async Task Two_clients_editing_concurrently_converge()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);
        var aliceClock = ClockFor(1);
        var bobClock = ClockFor(2);

        await room.JoinAsync(alice, new VersionVector());
        await room.JoinAsync(bob, new VersionVector());

        await room.PushAsync(alice.Replica, [
            new SetProperty(
                aliceClock.Tick(), new ShapeId("s1"), ShapeProperty.X, PropertyValue.Of(10)),
        ]);
        await room.PushAsync(bob.Replica, [
            new SetProperty(
                bobClock.Tick(), new ShapeId("s1"), ShapeProperty.Fill, PropertyValue.Of("#00f")),
        ]);

        var server = new BoardReplica();
        server.ApplyAll(await repository.LoadAsync(Board));

        var shape = Assert.Single(server.Shapes);
        Assert.Equal(10, shape.Number(ShapeProperty.X));
        Assert.Equal("#00f", shape.Text(ShapeProperty.Fill));
    }

    [Fact]
    public async Task Presence_reaches_peers_and_is_never_written_to_the_log()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);

        await room.JoinAsync(alice, new VersionVector());
        await room.JoinAsync(bob, new VersionVector());

        var state = new PresenceState(alice.Replica, "Alice", "#f0f", 12, 34, []);
        await room.UpdatePresenceAsync(alice.Replica, state);

        Assert.Equal(state, bob.Single<ServerMessage.PeerPresence>().State);
        Assert.False(alice.Any<ServerMessage.PeerPresence>());
        Assert.Empty(await repository.LoadAsync(Board));
    }

    [Fact]
    public async Task Presence_claiming_another_replica_is_rejected()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);

        await room.JoinAsync(alice, new VersionVector());
        await room.UpdatePresenceAsync(
            alice.Replica,
            new PresenceState(new ReplicaId(99), "Not Alice", "#000", 0, 0, []));

        Assert.True(alice.Any<ServerMessage.Rejected>());
    }

    [Fact]
    public async Task A_client_that_joins_late_is_told_who_is_already_here()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);

        await room.JoinAsync(alice, new VersionVector());
        await room.UpdatePresenceAsync(
            alice.Replica, new PresenceState(alice.Replica, "Alice", "#f0f", 1, 2, []));
        await room.JoinAsync(bob, new VersionVector());

        var peer = Assert.Single(bob.Single<ServerMessage.Welcome>().Peers);
        Assert.Equal("Alice", peer.DisplayName);
    }

    [Fact]
    public async Task Leaving_notifies_the_remaining_participants()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);

        await room.JoinAsync(alice, new VersionVector());
        await room.JoinAsync(bob, new VersionVector());
        await room.LeaveAsync(alice.Replica);

        Assert.Equal(alice.Replica, bob.Single<ServerMessage.PeerLeft>().Replica);
        Assert.Equal(1, room.SubscriberCount);
    }

    [Fact]
    public async Task A_dead_connection_is_dropped_without_disturbing_anyone_else()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);
        var alice = new RecordingSubscriber(1);
        var bob = new RecordingSubscriber(2);
        var clock = ClockFor(1);

        await room.JoinAsync(alice, new VersionVector());
        await room.JoinAsync(bob, new VersionVector());

        bob.Broken = true;
        await room.PushAsync(alice.Replica, [Op(clock, "s1", ShapeProperty.Kind, "rect")]);

        Assert.Equal(1, room.SubscriberCount);
        Assert.True(alice.Any<ServerMessage.Ack>());
        Assert.Single(await repository.LoadAsync(Board));
    }

    [Fact]
    public async Task Commands_from_many_connections_at_once_are_all_applied_exactly_once()
    {
        var repository = new InMemoryBoardRepository();
        await using var room = await Room.OpenAsync(Board, repository);

        var clients = Enumerable.Range(1, 8)
            .Select(i => (Subscriber: new RecordingSubscriber((ulong)i), Clock: ClockFor((ulong)i)))
            .ToList();

        foreach (var client in clients)
            await room.JoinAsync(client.Subscriber, new VersionVector());

        await Task.WhenAll(clients.Select(client => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await room.PushAsync(client.Subscriber.Replica, [
                    new SetProperty(
                        client.Clock.Tick(),
                        new ShapeId($"s{i}"),
                        ShapeProperty.X,
                        PropertyValue.Of(i)),
                ]);
            }
        })));

        var stored = await repository.LoadAsync(Board);
        Assert.Equal(8 * 50, stored.Count);
        Assert.Equal(stored.Count, stored.Select(o => o.At).Distinct().Count());
        Assert.Equal(50, room.Shapes.Count);
    }
}
