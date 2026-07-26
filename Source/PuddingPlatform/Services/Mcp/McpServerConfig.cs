using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;

namespace PuddingPlatform.Services.Mcp;

/// <summary>Validated V1 configuration for one workspace MCP server.</summary>
public sealed record McpServerConfig
{
    public string Endpoint { get; init; } = string.Empty;
    public string Transport { get; init; } = "streamable_http";
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool AllowPrivateNetwork { get; init; }
    public int ConnectionTimeoutSeconds { get; init; } = 15;
    public int CallTimeoutSeconds { get; init; } = 60;
    public int MaxResultChars { get; init; } = 262_144;
    public int MaxReconnectionAttempts { get; init; } = 5;
    public int ShutdownTimeoutSeconds { get; init; } = 5;
    public string? BearerTokenSecretId { get; init; }

    [JsonIgnore]
    public Uri EndpointUri => new(Endpoint, UriKind.Absolute);

    [JsonIgnore]
    public HttpTransportMode TransportMode => Transport switch
    {
        "streamable_http" => HttpTransportMode.StreamableHttp,
        "sse" => HttpTransportMode.Sse,
        "auto" => HttpTransportMode.AutoDetect,
        _ => throw new InvalidOperationException($"Unsupported MCP transport '{Transport}'."),
    };

    public static bool TryParse(string? configJson, out McpServerConfig? config, out string? error)
    {
        config = null;
        error = null;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            error = "MCP configJson is required.";
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize<McpServerConfig>(configJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid MCP config JSON: {ex.Message}";
            return false;
        }

        if (config is null)
        {
            error = "MCP configJson cannot be null.";
            return false;
        }

        config = config with
        {
            Endpoint = config.Endpoint?.Trim() ?? string.Empty,
            Transport = config.Transport?.Trim().ToLowerInvariant() ?? string.Empty,
            Command = config.Command?.Trim() ?? string.Empty,
            Arguments = config.Arguments ?? [],
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? null
                : config.WorkingDirectory.Trim(),
        };
        return config.Validate(out error);
    }

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, JsonOptions);

    public bool Validate(out string? error)
    {
        error = null;

        if (Transport is not ("streamable_http" or "sse" or "auto" or "stdio"))
        {
            error = "MCP transport must be one of: streamable_http, sse, auto, stdio.";
            return false;
        }

        if (ConnectionTimeoutSeconds is < 1 or > 120)
        {
            error = "connectionTimeoutSeconds must be between 1 and 120.";
            return false;
        }

        if (CallTimeoutSeconds is < 1 or > 86_400)
        {
            error = "callTimeoutSeconds must be between 1 and 86400.";
            return false;
        }

        if (MaxResultChars is < 1_024 or > 4_194_304)
        {
            error = "maxResultChars must be between 1024 and 4194304.";
            return false;
        }

        if (MaxReconnectionAttempts is < 0 or > 50)
        {
            error = "maxReconnectionAttempts must be between 0 and 50.";
            return false;
        }

        if (ShutdownTimeoutSeconds is < 1 or > 30)
        {
            error = "shutdownTimeoutSeconds must be between 1 and 30.";
            return false;
        }

        return Transport == "stdio"
            ? ValidateStdio(out error)
            : ValidateHttp(out error);
    }

    private bool ValidateStdio(out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(Endpoint))
        {
            error = "MCP endpoint must be empty when transport is stdio.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(BearerTokenSecretId))
        {
            error = "bearerTokenSecretId is only supported by HTTP MCP transports.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Command))
        {
            error = "MCP command is required when transport is stdio.";
            return false;
        }

        if (ContainsProtocolBreakingControlCharacter(Command))
        {
            error = "MCP command must not contain NUL or newline characters.";
            return false;
        }

        if (!Path.IsPathRooted(Command)
            && (Command.Contains(Path.DirectorySeparatorChar)
                || Command.Contains(Path.AltDirectorySeparatorChar)
                || Command.Any(char.IsWhiteSpace)))
        {
            error = "Relative MCP command must be a bare executable name without directories or whitespace.";
            return false;
        }

        if (Arguments.Count > 64)
        {
            error = "MCP stdio arguments must contain at most 64 entries.";
            return false;
        }

        foreach (var argument in Arguments)
        {
            if (argument is null || argument.Length > 4_096 || ContainsProtocolBreakingControlCharacter(argument))
            {
                error = "Each MCP stdio argument must be non-null, at most 4096 characters, and contain no NUL or newline characters.";
                return false;
            }
        }

        if (WorkingDirectory is not null && !Path.IsPathFullyQualified(WorkingDirectory))
        {
            error = "MCP stdio workingDirectory must be an absolute path.";
            return false;
        }

        return true;
    }

    private bool ValidateHttp(out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(Command) || Arguments.Count > 0 || WorkingDirectory is not null)
        {
            error = "command, arguments and workingDirectory are only supported when transport is stdio.";
            return false;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            error = "MCP endpoint must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            error = "MCP endpoint must not contain embedded credentials; use bearerTokenSecretId.";
            return false;
        }

        if (!AllowPrivateNetwork)
        {
            if (endpoint.Scheme != Uri.UriSchemeHttps)
            {
                error = "Public MCP endpoints must use HTTPS. Set allowPrivateNetwork=true only for trusted local development servers.";
                return false;
            }

            if (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(endpoint.Host, out var address) && !McpNetworkPolicy.IsPublicAddress(address)))
            {
                error = "Private, loopback, link-local and reserved MCP endpoints require allowPrivateNetwork=true.";
                return false;
            }
        }

        return true;
    }

    private static bool ContainsProtocolBreakingControlCharacter(string value) =>
        value.IndexOfAny(['\0', '\r', '\n']) >= 0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

internal static class McpNetworkPolicy
{
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetwork)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        if (!allowPrivateNetwork)
            handler.ConnectCallback = ConnectPublicAsync;

        return handler;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var address = addresses.FirstOrDefault(IsPublicAddress)
            ?? throw new HttpRequestException(
                $"MCP endpoint '{context.DnsEndPoint.Host}' resolved only to private or reserved addresses.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unique local addresses fc00::/7.
            return (bytes[0] & 0xFE) != 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 0 => false,
            192 when bytes[1] == 168 => false,
            198 when bytes[1] is 18 or 19 => false,
            198 when bytes[1] == 51 && bytes[2] == 100 => false,
            203 when bytes[1] == 0 && bytes[2] == 113 => false,
            >= 224 => false,
            _ => true,
        };
    }
}
