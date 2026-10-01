using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Pudding.Contracts;
using CapabilityBrokerHost = Pudding.CapabilityBroker.CapabilityBroker;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker.AspNetCore;

/// <summary>能力通道的宿主装配选项（全部来自配置，凭据不在其中）。</summary>
public sealed record CapabilityChannelOptions
{
    /// <summary>本机期望的 Desktop 身份（来自可信配置；握手时校验，不接受对端自称）。</summary>
    public required DesktopInstanceId ExpectedDesktopId { get; init; }

    /// <summary>本机允许授予的能力上限。</summary>
    public required DesktopCapability Grantable { get; init; }

    /// <summary>Core 进程实例 ID（Desktop 用它识别 Core 是否换了实例）。</summary>
    public string CoreInstanceId { get; init; } = $"core-{Environment.ProcessId}";

    public int MaxInFlightPerConnection { get; init; } = 8;

    public int MaxQueuedFrames { get; init; } = 128;

    public int MaxQueuedBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>单条 gRPC 消息上限（默认 1 MiB；大载荷应走限额文件/分块能力）。</summary>
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    public bool EnforceSingleActiveTransport { get; init; } = true;

    /// <summary>Named Pipe 名（产品默认传输）。<c>null</c> = 不监听管道。</summary>
    public string? NamedPipeName { get; init; }

    /// <summary>调试备用：Loopback h2c 端口。<c>null</c> = 不监听。</summary>
    public int? LoopbackPort { get; init; }

    public IDesktopCapabilityAuditSink AuditSink { get; init; } = NullDesktopCapabilityAuditSink.Instance;

    internal DesktopCapabilityPolicy ToPolicy() => new()
    {
        Grantable = Grantable,
        MaxInFlightPerConnection = MaxInFlightPerConnection,
        MaxQueuedFrames = MaxQueuedFrames,
        MaxQueuedBytes = MaxQueuedBytes,
        HandshakeTimeout = HandshakeTimeout,
        HeartbeatInterval = HeartbeatInterval,
        HeartbeatTimeout = HeartbeatTimeout,
    };

    internal void Validate()
    {
        ToPolicy().Validate();

        if (NamedPipeName is { Length: > 0 } pipe && !DesktopCapabilityEndpoint.IsValidPipeName(pipe))
        {
            throw new ArgumentException($"Named pipe name '{pipe}' is not a valid capability pipe name.", nameof(NamedPipeName));
        }

        if (LoopbackPort is { } port && port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(LoopbackPort), port, "Loopback port must be in [1, 65535].");
        }

        if (MaxMessageBytes < 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes), MaxMessageBytes, "Message limit must be at least 64 KiB.");
        }
    }
}

/// <summary>
/// Desktop 能力服务（Core 侧唯一 gRPC 服务）：认证 → 交给 Broker → 驱动整条流。
/// 认证先于握手；Broker 负责协商、会话与全部操作语义。
/// </summary>
internal sealed class DesktopCapabilityService : Proto.DesktopCapability.DesktopCapabilityBase
{
    private readonly CapabilityBrokerHost _broker;
    private readonly ICoreCapabilityAuthenticator _authenticator;

    public DesktopCapabilityService(
        CapabilityBrokerHost broker, ICoreCapabilityAuthenticator authenticator)
    {
        _broker = broker;
        _authenticator = authenticator;
    }

    public override async Task Connect(
        IAsyncStreamReader<Proto.DesktopFrame> requestStream,
        IServerStreamWriter<Proto.CoreFrame> responseStream,
        ServerCallContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.RequestHeaders)
        {
            headers[entry.Key] = entry.Value;
        }

        var denial = await _authenticator.AuthenticateAsync(headers, context.CancellationToken).ConfigureAwait(false);
        if (denial is not null)
        {
            // 认证失败不进入握手，也不产生任何会话。
            throw new RpcException(new Status(StatusCode.Unauthenticated, denial.WireCode));
        }

        var accepted = await _broker.AcceptAsync(
            new ServerStreamDesktopChannel(requestStream, responseStream), context.CancellationToken).ConfigureAwait(false);

        if (!accepted.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, accepted.Error.WireCode));
        }

        await accepted.Value.Completion.ConfigureAwait(false);
    }
}

/// <summary>
/// 能力通道的宿主装配助手：注册 Broker/认证器/gRPC，映射服务，并按选项监听端点。
/// 「不改既有 REST 绑定」是硬要求：只在显式调用 <see cref="ListenForCapabilityChannel"/> 时才新增端点。
/// </summary>
public static class CapabilityChannelHostExtensions
{
    public static IServiceCollection AddCapabilityChannel(
        this IServiceCollection services,
        CapabilityChannelOptions options,
        ICoreCapabilityAuthenticator? authenticator = null,
        IDesktopCapabilityAuthorizer? authorizer = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var broker = new CapabilityBrokerHost(
            new CapabilityBrokerOptions
            {
                HostDesktopId = options.ExpectedDesktopId,
                Policy = options.ToPolicy(),
                CoreInstanceId = options.CoreInstanceId,
                EnforceSingleActiveTransport = options.EnforceSingleActiveTransport,
                AuditSink = options.AuditSink,
            },
            // 默认拒绝：未装配授权器时不得开放任何能力（RPC 可达 ≠ 获得桌面操作授权）。
            authorizer);

        services.AddSingleton(broker);
        services.AddSingleton(options);
        services.AddSingleton(authenticator ?? RejectAllCapabilityAuthenticator.Instance);
        services.AddSingleton<DesktopCapabilityService>();
        services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = options.MaxMessageBytes;
            grpc.MaxSendMessageSize = options.MaxMessageBytes;
        });

        return services;
    }

    public static WebApplication MapCapabilityChannel(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGrpcService<DesktopCapabilityService>();
        return app;
    }

    /// <summary>
    /// 为能力通道新增 Kestrel 端点（Named Pipe 或 Loopback h2c）。
    /// 必须与既有 REST 绑定<b>并存</b>：调用方需自行确认（本组件提供测试形态的验证方法）。
    /// </summary>
    public static void ListenForCapabilityChannel(this KestrelServerOptions kestrel, CapabilityChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var configured = false;

        if (options.NamedPipeName is { Length: > 0 } pipeName)
        {
            kestrel.ListenNamedPipe(pipeName, listen => listen.Protocols = HttpProtocols.Http2);
            configured = true;
        }

        if (options.LoopbackPort is { } port)
        {
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http2);
            configured = true;
        }

        if (!configured)
        {
            throw new InvalidOperationException(
                "Capability channel requires at least one endpoint (named pipe or loopback port).");
        }
    }
}
