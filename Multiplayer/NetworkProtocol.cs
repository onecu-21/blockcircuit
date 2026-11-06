using System.Buffers.Binary;
using System.Text.Json;

namespace BlockCircuit;

/// <summary>Wire envelope. Payload DTOs are decoded by the session on its owner thread.</summary>
public sealed record NetMessage(string Type, long Sequence, JsonElement Data)
{
    public static NetMessage Create<T>(string type, T payload, long sequence = 0)
    {
        var result = new NetMessage(type, sequence, JsonSerializer.SerializeToElement(payload, NetworkProtocol.JsonOptions));
        NetworkProtocol.Validate(result);
        return result;
    }

    public T Read<T>()
    {
        NetworkProtocol.Validate(this);
        return Data.Deserialize<T>(NetworkProtocol.JsonOptions)
            ?? throw new InvalidDataException("Missing network payload.");
    }
}

/// <summary>UTF-8 JSON framed by a four-byte unsigned big-endian body length.</summary>
public static class NetworkProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const int MaximumQueuedMessages = 128;
    public const int MaximumQueuedBytes = 16 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            MaxDepth = 32,
            PropertyNameCaseInsensitive = false
        };
        options = GameJson.Options(options);
        options.MakeReadOnly();
        return options;
    }

    public static void Validate(NetMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type is null || message.Type.Length is < 1 or > 64 ||
            message.Type.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ':')) ||
            message.Sequence < 0 || message.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidDataException("Invalid network envelope.");
    }

    public static byte[] EncodeFrame(NetMessage message)
    {
        Validate(message);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is < 1 or > MaximumFrameBytes)
            throw new InvalidDataException("Network frame exceeds the 8 MiB limit.");
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static NetMessage Decode(ReadOnlyMemory<byte> body)
    {
        if (body.Length is < 1 or > MaximumFrameBytes)
            throw new InvalidDataException("Invalid network frame length.");
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected a network envelope.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (field.Name is not ("Type" or "Sequence" or "Data") || !names.Add(field.Name))
                throw new InvalidDataException("Duplicate or unknown envelope field.");
        if (names.Count != 3)
            throw new InvalidDataException("Incomplete network envelope.");
        var result = root.Deserialize<NetMessage>(JsonOptions)
            ?? throw new InvalidDataException("Empty network envelope.");
        Validate(result);
        // JsonElement produced by Deserialize has independent lifetime from this document.
        return result;
    }
}
