using System.Buffers.Binary;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Protocol;

public sealed record PluginFrame(string Kind, string RequestId, string ActivationId, JsonElement Payload,
    string? ReplyTo = null, int ProtocolVersion = 1, DateTimeOffset? Deadline = null);
public sealed record InitializePayload(PluginManifest Manifest, string Directory, PluginScope Scope);

public static class PluginWire
{
    public const int MaxFrameBytes = 1024 * 1024;
    public static async Task WriteAsync(Stream stream, PluginFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, PluginJson.Options);
        if (bytes.Length > MaxFrameBytes) throw new PluginException("plugin.output_limit", "protocol", "RPC frame exceeds 1 MiB.", "unknown");
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<PluginFrame> ReadAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[4]; await stream.ReadExactlyAsync(length, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 or > MaxFrameBytes) throw new PluginException("plugin.protocol_invalid", "protocol", "Invalid RPC frame length.", "unknown");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, ct);
        var frame = JsonSerializer.Deserialize<PluginFrame>(bytes, PluginJson.Options)
            ?? throw new PluginException("plugin.protocol_invalid", "protocol", "Empty RPC frame.");
        if (frame.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(frame.RequestId))
            throw new PluginException("plugin.protocol_invalid", "protocol", "Unsupported RPC envelope.");
        return frame;
    }
}
