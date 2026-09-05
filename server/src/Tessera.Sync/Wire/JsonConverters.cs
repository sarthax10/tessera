using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tessera.Crdt;

namespace Tessera.Sync.Wire;

public sealed class HlcJsonConverter : JsonConverter<Hlc>
{
    public override Hlc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Hlc.Parse(reader.GetString() ?? throw new JsonException("Expected a timestamp."));

    public override void Write(Utf8JsonWriter writer, Hlc value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString());
    }
}

public sealed class ReplicaIdJsonConverter : JsonConverter<ReplicaId>
{
    public override ReplicaId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReplicaId.Parse(reader.GetString() ?? throw new JsonException("Expected a replica id."));

    public override void Write(Utf8JsonWriter writer, ReplicaId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString());
    }
}

public sealed class ShapeIdJsonConverter : JsonConverter<ShapeId>
{
    public override ShapeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Expected a shape id."));

    public override void Write(Utf8JsonWriter writer, ShapeId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.Value);
    }
}

public sealed class BoardIdJsonConverter : JsonConverter<BoardId>
{
    public override BoardId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Expected a board id."));

    public override void Write(Utf8JsonWriter writer, BoardId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Encodes a value as its natural JSON counterpart rather than a tagged envelope, so the client
/// needs nothing beyond JSON.parse.
/// </summary>
public sealed class PropertyValueJsonConverter : JsonConverter<PropertyValue>
{
    public override PropertyValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => PropertyValue.Null,
            JsonTokenType.Number => PropertyValue.Of(reader.GetDouble()),
            JsonTokenType.String => PropertyValue.Of(reader.GetString()!),
            JsonTokenType.True or JsonTokenType.False => PropertyValue.Of(reader.GetBoolean()),
            JsonTokenType.StartArray => PropertyValue.Of(ReadPoints(ref reader)),
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for a property value."),
        };

    private static ImmutableArray<Point> ReadPoints(ref Utf8JsonReader reader)
    {
        var points = ImmutableArray.CreateBuilder<Point>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected a [x, y] pair.");

            reader.Read();
            var x = reader.GetDouble();
            reader.Read();
            var y = reader.GetDouble();
            reader.Read();

            points.Add(new Point(x, y));
        }

        return points.ToImmutable();
    }

    public override void Write(Utf8JsonWriter writer, PropertyValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (value.Kind)
        {
            case PropertyKind.Null:
                writer.WriteNullValue();
                break;

            case PropertyKind.Number:
                writer.WriteNumberValue(value.Number);
                break;

            case PropertyKind.Text:
                writer.WriteStringValue(value.Text);
                break;

            case PropertyKind.Flag:
                writer.WriteBooleanValue(value.AsBool);
                break;

            case PropertyKind.Points:
                writer.WriteStartArray();

                foreach (var point in value.Points)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(point.X);
                    writer.WriteNumberValue(point.Y);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;

            default:
                throw new JsonException($"Unknown property kind {value.Kind}.");
        }
    }
}

public sealed class OperationJsonConverter : JsonConverter<Operation>
{
    public override Operation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var at = Hlc.Parse(Required(root, "at").GetString()!);
        var shape = new ShapeId(Required(root, "shape").GetString()!);

        return Required(root, "op").GetString() switch
        {
            "set" => new SetProperty(
                at,
                shape,
                Required(root, "prop").GetString()!,
                Required(root, "value").Deserialize<PropertyValue>(options)),
            "del" => new DeleteShape(at, shape),
            var kind => throw new JsonException($"Unknown operation '{kind}'."),
        };
    }

    private static JsonElement Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value
            : throw new JsonException($"Operation is missing '{name}'.");

    public override void Write(Utf8JsonWriter writer, Operation value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        switch (value)
        {
            case SetProperty set:
                writer.WriteString("op", "set");
                writer.WriteString("at", set.At.ToString());
                writer.WriteString("shape", set.Shape.Value);
                writer.WriteString("prop", set.Property);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, set.Value, options);
                break;

            case DeleteShape delete:
                writer.WriteString("op", "del");
                writer.WriteString("at", delete.At.ToString());
                writer.WriteString("shape", delete.Shape.Value);
                break;

            default:
                throw new JsonException($"Unknown operation type {value.GetType().Name}.");
        }

        writer.WriteEndObject();
    }
}

public sealed class VersionVectorJsonConverter : JsonConverter<VersionVector>
{
    public override VersionVector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) ?? [];

        return new VersionVector(
            entries.Select(entry =>
                KeyValuePair.Create(ReplicaId.Parse(entry.Key), Hlc.Parse(entry.Value))));
    }

    public override void Write(Utf8JsonWriter writer, VersionVector value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        foreach (var (replica, timestamp) in value.Entries)
            writer.WriteString(replica.ToString(), timestamp.ToString());

        writer.WriteEndObject();
    }
}
