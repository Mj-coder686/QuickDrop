using System.Buffers.Binary;
using System.Text.Json;

namespace QuickDrop.Network.Protocol;

public enum FrameType : byte
{
    Control = 1,
    Data = 2
}

public readonly record struct FrameHeader(FrameType Type, int Length);

public sealed record IncomingControl(string Type, JsonElement Payload)
{
    public T GetPayload<T>() =>
        Payload.Deserialize<T>(FrameProtocol.JsonOptions)
        ?? throw new InvalidDataException($"Control message '{Type}' has no payload.");
}

public static class FrameProtocol
{
    public const int ProtocolVersion = 1;
    public const int ChunkSize = 1024 * 1024;
    private const int MaxControlSize = 4 * 1024 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static async ValueTask WriteControlAsync<T>(Stream stream, string type, T payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new OutgoingControl<T>(type, payload), JsonOptions);
        if (bytes.Length > MaxControlSize)
        {
            throw new InvalidDataException("Control frame is too large.");
        }

        await WriteHeaderAsync(stream, FrameType.Control, bytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteDataAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length <= 0 || bytes.Length > ChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        await WriteHeaderAsync(stream, FrameType.Data, bytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<FrameHeader> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var type = (FrameType)header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var maxLength = type == FrameType.Control ? MaxControlSize : ChunkSize;
        if (type is not (FrameType.Control or FrameType.Data) || length <= 0 || length > maxLength)
        {
            throw new InvalidDataException("Invalid protocol frame header.");
        }

        return new FrameHeader(type, length);
    }

    public static async ValueTask<IncomingControl> ReadControlAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (header.Type != FrameType.Control)
        {
            throw new InvalidDataException("Expected a control frame.");
        }

        var payload = new byte[header.Length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IncomingControl>(payload, JsonOptions)
            ?? throw new InvalidDataException("Invalid control frame.");
    }

    public static async ValueTask ReadDataBodyAsync(
        Stream stream,
        FrameHeader header,
        Stream destination,
        System.Security.Cryptography.IncrementalHash hash,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (header.Type != FrameType.Data || header.Length > buffer.Length)
        {
            throw new InvalidDataException("Invalid data frame.");
        }

        var remaining = header.Length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The sender disconnected during a file block.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }

    private static async ValueTask WriteHeaderAsync(Stream stream, FrameType type, int length, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    private sealed record OutgoingControl<T>(string Type, T Payload);
}
