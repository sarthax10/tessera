using System.Collections.Immutable;
using System.Text.Json;
using Tessera.Crdt;
using Tessera.Sync.Wire;

namespace Tessera.Sync.Tests;

public class WireFormatTests
{
    private static readonly Hlc Stamp = new(1_700_000_000_000, 3, new ReplicaId(0xABCDEF));

    private static T RoundTrip<T>(T value) => WireFormat.FromJson<T>(WireFormat.ToJson(value))!;

    [Fact]
    public void A_timestamp_survives_a_round_trip()
    {
        Assert.Equal(Stamp, Hlc.Parse(Stamp.ToString()));
    }

    [Theory]
    [InlineData("not a timestamp")]
    [InlineData("a.b")]
    [InlineData("")]
    public void Malformed_timestamps_are_rejected_rather_than_guessed_at(string text)
    {
        Assert.False(Hlc.TryParse(text, out _));
    }

    [Fact]
    public void A_set_operation_survives_a_round_trip()
    {
        var op = new SetProperty(
            Stamp, new ShapeId("s1"), ShapeProperty.Fill, PropertyValue.Of("#f00"));

        Assert.Equal(op, Assert.IsType<SetProperty>(RoundTrip<Operation>(op)));
    }

    [Fact]
    public void A_delete_operation_survives_a_round_trip()
    {
        var op = new DeleteShape(Stamp, new ShapeId("s1"));

        Assert.Equal(op, Assert.IsType<DeleteShape>(RoundTrip<Operation>(op)));
    }

    public static TheoryData<PropertyValue> Values => new()
    {
        PropertyValue.Null,
        PropertyValue.Of(42.5),
        PropertyValue.Of(-0.125),
        PropertyValue.Of("hello"),
        PropertyValue.Of(""),
        PropertyValue.Of(true),
        PropertyValue.Of(false),
        PropertyValue.Of(ImmutableArray.Create(new Point(1, 2), new Point(-3.5, 4))),
        PropertyValue.Of(ImmutableArray<Point>.Empty),
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Every_property_value_kind_survives_a_round_trip(PropertyValue value)
    {
        var op = new SetProperty(Stamp, new ShapeId("s1"), "p", value);

        var back = Assert.IsType<SetProperty>(RoundTrip<Operation>(op));
        Assert.Equal(value, back.Value);
        Assert.Equal(value.Kind, back.Value.Kind);
    }

    [Fact]
    public void Property_values_encode_as_plain_json_rather_than_tagged_envelopes()
    {
        var op = new SetProperty(Stamp, new ShapeId("s1"), ShapeProperty.X, PropertyValue.Of(7));

        var json = WireFormat.ToJson<Operation>(op);

        Assert.Contains("\"value\":7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("kind", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_join_message_survives_a_round_trip()
    {
        var have = new VersionVector();
        have.Observe(Stamp);
        var message = new ClientMessage.Join(new BoardId("b1"), new ReplicaId(7), have);

        var back = Assert.IsType<ClientMessage.Join>(RoundTrip<ClientMessage>(message));

        Assert.Equal(message.Board, back.Board);
        Assert.Equal(message.Replica, back.Replica);
        Assert.True(back.Have.Covers(Stamp));
    }

    [Fact]
    public void A_push_message_survives_a_round_trip()
    {
        var message = new ClientMessage.Push([
            new SetProperty(Stamp, new ShapeId("s1"), ShapeProperty.X, PropertyValue.Of(1)),
            new DeleteShape(Stamp, new ShapeId("s2")),
        ]);

        var back = Assert.IsType<ClientMessage.Push>(RoundTrip<ClientMessage>(message));

        Assert.Equal(message.Operations, back.Operations);
    }

    [Fact]
    public void A_presence_message_survives_a_round_trip()
    {
        var message = new ClientMessage.Presence(
            new PresenceState(new ReplicaId(7), "Alice", "#f0f", 1.5, -2.5, [new ShapeId("s1")]));

        var back = Assert.IsType<ClientMessage.Presence>(RoundTrip<ClientMessage>(message));

        Assert.Equal(message.State, back.State);
    }

    [Fact]
    public void Every_server_message_survives_a_round_trip()
    {
        var op = new SetProperty(Stamp, new ShapeId("s1"), ShapeProperty.X, PropertyValue.Of(1));
        var presence = new PresenceState(new ReplicaId(7), "Alice", "#f0f", 0, 0, []);

        ServerMessage[] messages =
        [
            new ServerMessage.Welcome(new BoardId("b1"), [op], [presence]),
            new ServerMessage.Ack([Stamp]),
            new ServerMessage.Broadcast([op]),
            new ServerMessage.PeerPresence(presence),
            new ServerMessage.PeerLeft(new ReplicaId(7)),
            new ServerMessage.Rejected("nope"),
        ];

        // Compared as encoded text: these DTOs compare their list members by reference, and what
        // has to hold is that re-encoding a decoded message reproduces the original bytes.
        foreach (var message in messages)
            Assert.Equal(WireFormat.ToJson(message), WireFormat.ToJson(RoundTrip(message)));
    }

    [Fact]
    public void Messages_carry_a_type_discriminator()
    {
        var json = WireFormat.ToJson<ServerMessage>(new ServerMessage.Ack([Stamp]));

        Assert.Contains("\"type\":\"ack\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_operation_kind_is_rejected()
    {
        var json = $$"""{"op":"teleport","at":"{{Stamp}}","shape":"s1"}""";

        Assert.Throws<JsonException>(() => WireFormat.FromJson<Operation>(json));
    }

    [Fact]
    public void An_operation_missing_a_required_field_is_rejected()
    {
        var json = $$"""{"op":"set","at":"{{Stamp}}","shape":"s1"}""";

        Assert.Throws<JsonException>(() => WireFormat.FromJson<Operation>(json));
    }
}
