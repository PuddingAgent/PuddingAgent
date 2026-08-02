using System.Text;
using System.Text.Json;

namespace PuddingBrowser.Protocol;

public static class BrowserBridgeSerializer
{
    public static BrowserBridgeEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > BrowserBridgeProtocol.MaxMessageBytes)
        {
            throw new BrowserBridgeProtocolException(
                BrowserBridgeErrorCodes.BrowserInvalidCommand,
                $"Message exceeds maximum size of {BrowserBridgeProtocol.MaxMessageBytes} bytes");
        }

        var envelope = JsonSerializer.Deserialize(utf8Json, BrowserBridgeJsonSerializerContext.Default.BrowserBridgeEnvelope);
        if (envelope is null)
        {
            throw new BrowserBridgeProtocolException(
                BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "Failed to deserialize envelope");
        }

        if (envelope.ProtocolVersion != BrowserBridgeProtocol.CurrentVersion)
        {
            throw new BrowserBridgeProtocolException(
                BrowserBridgeErrorCodes.BrowserProtocolMismatch,
                $"Unsupported protocol version: {envelope.ProtocolVersion}");
        }

        return envelope;
    }

    public static ReadOnlyMemory<byte> Serialize(BrowserBridgeEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, BrowserBridgeJsonSerializerContext.Default.BrowserBridgeEnvelope);
        return Encoding.UTF8.GetBytes(json);
    }

    public static T DeserializePayload<T>(BrowserBridgeEnvelope envelope) where T : notnull
    {
        var result = JsonSerializer.Deserialize<T>(envelope.Payload.GetRawText(), BrowserBridgeJsonSerializerContext.Default.Options);
        if (result is null)
        {
            throw new BrowserBridgeProtocolException(
                BrowserBridgeErrorCodes.BrowserInvalidCommand,
                $"Failed to deserialize payload as {typeof(T).Name}");
        }
        return result;
    }
}

public sealed class BrowserBridgeProtocolException : Exception
{
    public string ErrorCode { get; }

    public BrowserBridgeProtocolException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
