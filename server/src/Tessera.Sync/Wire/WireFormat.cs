using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tessera.Sync.Wire;

/// <summary>
/// The encoding shared by the server and the browser client, and by the conformance vectors.
/// Confined to this class so swapping to a binary format touches no caller.
/// </summary>
public static class WireFormat
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new HlcJsonConverter());
        options.Converters.Add(new ReplicaIdJsonConverter());
        options.Converters.Add(new ShapeIdJsonConverter());
        options.Converters.Add(new BoardIdJsonConverter());
        options.Converters.Add(new PropertyValueJsonConverter());
        options.Converters.Add(new OperationJsonConverter());
        options.Converters.Add(new VersionVectorJsonConverter());

        // Frozen because these are a process-wide singleton.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options);

    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
