namespace Tessera.Crdt.Tests;

public class BoardReplicaTests
{
    private static readonly ShapeId Box = new("shape-1");

    private sealed class Peer(ulong id, FakeClock clock)
    {
        public HybridLogicalClock Clock { get; } = new(new ReplicaId(id), clock.Read);
        public BoardReplica Replica { get; } = new();

        public SetProperty Set(ShapeId shape, string property, PropertyValue value)
        {
            var op = new SetProperty(Clock.Tick(), shape, property, value);
            Replica.Apply(op);
            return op;
        }

        public DeleteShape Delete(ShapeId shape)
        {
            var op = new DeleteShape(Clock.Tick(), shape);
            Replica.Apply(op);
            return op;
        }

        public void Receive(params Operation[] ops)
        {
            foreach (var op in ops)
            {
                Clock.Observe(op.At);
                Replica.Apply(op);
            }
        }
    }

    private static (Peer A, Peer B) TwoPeers()
    {
        var clock = new FakeClock();
        return (new Peer(1, clock), new Peer(2, clock));
    }

    [Fact]
    public void Setting_properties_creates_a_visible_shape()
    {
        var (a, _) = TwoPeers();

        a.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));
        a.Set(Box, ShapeProperty.X, PropertyValue.Of(10));

        var shape = a.Replica.Get(Box);
        Assert.NotNull(shape);
        Assert.Equal("rect", shape.Text(ShapeProperty.Kind));
        Assert.Equal(10, shape.Number(ShapeProperty.X));
    }

    [Fact]
    public void Unset_properties_read_as_null_rather_than_throwing()
    {
        var (a, _) = TwoPeers();
        a.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));

        var shape = a.Replica.Get(Box)!;

        Assert.Equal(PropertyValue.Null, shape[ShapeProperty.Fill]);
        Assert.Equal(0, shape.Number(ShapeProperty.X));
    }

    [Fact]
    public void Applying_the_same_operation_twice_changes_nothing()
    {
        var (a, _) = TwoPeers();
        var op = new SetProperty(a.Clock.Tick(), Box, ShapeProperty.X, PropertyValue.Of(5));

        Assert.True(a.Replica.Apply(op));
        Assert.False(a.Replica.Apply(op));
        Assert.False(a.Replica.Apply(op));

        Assert.Equal(5, a.Replica.Get(Box)!.Number(ShapeProperty.X));
    }

    [Fact]
    public void An_older_write_never_overwrites_a_newer_one()
    {
        var (a, b) = TwoPeers();
        var early = new SetProperty(a.Clock.Tick(), Box, ShapeProperty.X, PropertyValue.Of(1));
        var late = new SetProperty(a.Clock.Tick(), Box, ShapeProperty.X, PropertyValue.Of(2));

        b.Receive(late, early);

        Assert.Equal(2, b.Replica.Get(Box)!.Number(ShapeProperty.X));
    }

    [Fact]
    public void Concurrent_edits_to_different_properties_both_survive()
    {
        // One user drags while another recolours. Losing either is "my changes disappeared".
        var (a, b) = TwoPeers();
        var seed = a.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));
        b.Receive(seed);

        var moved = a.Set(Box, ShapeProperty.X, PropertyValue.Of(100));
        var recoloured = b.Set(Box, ShapeProperty.Fill, PropertyValue.Of("#ff0000"));

        a.Receive(recoloured);
        b.Receive(moved);

        foreach (var peer in new[] { a, b })
        {
            var shape = peer.Replica.Get(Box)!;
            Assert.Equal(100, shape.Number(ShapeProperty.X));
            Assert.Equal("#ff0000", shape.Text(ShapeProperty.Fill));
        }
    }

    [Fact]
    public void Concurrent_edits_to_the_same_property_converge_on_one_value()
    {
        var (a, b) = TwoPeers();
        var seed = a.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));
        b.Receive(seed);

        var fromA = a.Set(Box, ShapeProperty.Fill, PropertyValue.Of("#aaa"));
        var fromB = b.Set(Box, ShapeProperty.Fill, PropertyValue.Of("#bbb"));

        a.Receive(fromB);
        b.Receive(fromA);

        Assert.Equal(a.Replica.StateHash(), b.Replica.StateHash());
    }

    [Fact]
    public void A_delete_concurrent_with_an_edit_keeps_the_shape()
    {
        var (a, b) = TwoPeers();
        var seed = a.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));
        b.Receive(seed);

        var deleted = a.Delete(Box);
        var edited = b.Set(Box, ShapeProperty.X, PropertyValue.Of(42));

        a.Receive(edited);
        b.Receive(deleted);

        Assert.True(a.Replica.Contains(Box));
        Assert.True(b.Replica.Contains(Box));
        Assert.Equal(a.Replica.StateHash(), b.Replica.StateHash());
    }

    [Fact]
    public void A_delete_that_causally_follows_every_edit_removes_the_shape()
    {
        var (a, b) = TwoPeers();
        var seed = a.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));
        var edit = a.Set(Box, ShapeProperty.X, PropertyValue.Of(42));
        b.Receive(seed, edit);

        var deleted = b.Delete(Box);
        a.Receive(deleted);

        Assert.False(a.Replica.Contains(Box));
        Assert.False(b.Replica.Contains(Box));
        Assert.Null(a.Replica.Get(Box));
    }

    [Fact]
    public void A_delete_arriving_before_the_shape_exists_is_still_honoured()
    {
        var clock = new FakeClock();
        var author = new Peer(1, clock);
        var late = new Peer(2, clock);

        var seed = author.Set(Box, ShapeProperty.Kind, PropertyValue.Of("rect"));
        var edit = author.Set(Box, ShapeProperty.X, PropertyValue.Of(1));
        var deleted = author.Delete(Box);

        late.Receive(deleted, seed, edit);

        Assert.False(late.Replica.Contains(Box));
        Assert.Equal(author.Replica.StateHash(), late.Replica.StateHash());
    }

    [Fact]
    public void Shapes_are_returned_in_fractional_index_order()
    {
        var (a, _) = TwoPeers();
        var first = FractionalIndex.First();
        var third = FractionalIndex.Between(first, null);
        var second = FractionalIndex.Between(first, third);

        foreach (var (id, index) in new[] { ("c", third), ("a", first), ("b", second) })
        {
            var shape = new ShapeId(id);
            a.Set(shape, ShapeProperty.Kind, PropertyValue.Of("rect"));
            a.Set(shape, ShapeProperty.Index, PropertyValue.Of(index));
        }

        Assert.Equal(["a", "b", "c"], a.Replica.Shapes.Select(s => s.Id.Value));
    }

    [Fact]
    public void Any_delivery_order_of_the_same_operations_produces_the_same_document()
    {
        var operations = BuildBusySession(seed: 20260905);
        var rng = new Random(1234);
        var reference = Replay(operations);

        for (var trial = 0; trial < 200; trial++)
        {
            var shuffled = operations.OrderBy(_ => rng.Next()).ToList();
            Assert.Equal(reference.StateHash(), Replay(shuffled).StateHash());
        }
    }

    [Fact]
    public void Redelivering_every_operation_changes_nothing()
    {
        var operations = BuildBusySession(seed: 77);
        var replica = Replay(operations);
        var before = replica.StateHash();

        replica.ApplyAll(operations);
        replica.ApplyAll(operations);

        Assert.Equal(before, replica.StateHash());
    }

    [Fact]
    public void Splitting_operations_across_replicas_and_merging_converges()
    {
        // What an offline client rejoining actually looks like.
        var operations = BuildBusySession(seed: 31337);
        var half = operations.Count / 2;

        var left = Replay(operations.Take(half));
        var right = Replay(operations.Skip(half));

        left.ApplyAll(operations.Skip(half));
        right.ApplyAll(operations.Take(half));

        Assert.Equal(left.StateHash(), right.StateHash());
        Assert.Equal(Replay(operations).StateHash(), left.StateHash());
    }

    private static BoardReplica Replay(IEnumerable<Operation> operations)
    {
        var replica = new BoardReplica();
        replica.ApplyAll(operations);
        return replica;
    }

    /// <summary>
    /// An editing session across three replicas with overlapping clocks, so genuinely concurrent
    /// operations occur rather than trivially ordered ones.
    /// </summary>
    private static List<Operation> BuildBusySession(int seed)
    {
        var rng = new Random(seed);
        var clock = new FakeClock();
        var clocks = Enumerable.Range(1, 3)
            .Select(i => new HybridLogicalClock(new ReplicaId((ulong)i), clock.Read))
            .ToArray();

        var operations = new List<Operation>();
        var shapes = new List<ShapeId>();
        var indices = new List<string>();

        for (var step = 0; step < 400; step++)
        {
            if (rng.Next(3) == 0) clock.Advance(1);
            var author = clocks[rng.Next(clocks.Length)];

            if (shapes.Count == 0 || rng.Next(100) < 25)
            {
                var id = ShapeId.For(new ReplicaId((ulong)rng.Next(1, 4)), step);
                var index = indices.Count == 0
                    ? FractionalIndex.First()
                    : FractionalIndex.Between(indices[^1], null);

                indices.Add(index);
                shapes.Add(id);

                operations.Add(new SetProperty(
                    author.Tick(), id, ShapeProperty.Kind, PropertyValue.Of("rect")));
                operations.Add(new SetProperty(
                    author.Tick(), id, ShapeProperty.Index, PropertyValue.Of(index)));
                continue;
            }

            var target = shapes[rng.Next(shapes.Count)];

            operations.Add(rng.Next(100) switch
            {
                < 10 => new DeleteShape(author.Tick(), target),
                < 55 => new SetProperty(
                    author.Tick(), target, ShapeProperty.X, PropertyValue.Of(rng.Next(1000))),
                < 80 => new SetProperty(
                    author.Tick(), target, ShapeProperty.Y, PropertyValue.Of(rng.Next(1000))),
                _ => new SetProperty(
                    author.Tick(), target, ShapeProperty.Fill,
                    PropertyValue.Of($"#{rng.Next(0x1000000):x6}")),
            });
        }

        return operations;
    }
}
