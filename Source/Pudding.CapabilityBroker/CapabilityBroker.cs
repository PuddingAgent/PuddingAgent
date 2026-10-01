using System.Collections.Concurrent;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker;

public sealed record CapabilityBrokerOptions
{
    /// <summary>
    /// 本机期望的 Desktop 身份（来自可信配置/认证上下文）。握手里的 desktop_id 必须与它一致：
    /// 不允许对端自称身份（计划 §7：不让网页决定管道名或远端地址）。
    /// </summary>
    public required DesktopInstanceId HostDesktopId { get; init; }

    public required DesktopCapabilityPolicy Policy { get; init; }

    /// <summary>Core 进程实例标识：Desktop 用它识别「Core 是否换了实例」（换实例 ⇒ 旧世代失效）。</summary>
    public string CoreInstanceId { get; init; } = $"core-{Environment.ProcessId}";

    /// <summary>同一 Desktop 只允许一个活动能力传输（计划 §8 切片 D）。</summary>
    public bool EnforceSingleActiveTransport { get; init; } = true;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public IDesktopCapabilityAuditSink AuditSink { get; init; } = NullDesktopCapabilityAuditSink.Instance;
}

/// <summary>
/// Core 侧能力 Broker：接受 Desktop 主动拨入的双向流，完成握手协商，并作为
/// 「已连接 Desktop 的注册表 + 能力调用入口」。
///
/// 平台无关：ASP.NET Core 的 gRPC 服务基类只负责把服务端流适配成 <see cref="ICoreDesktopChannel"/>
/// 并调用 <see cref="AcceptAsync"/>，业务语义全部落在本组件与 <see cref="DesktopSession"/>。
/// </summary>
public sealed class CapabilityBroker : IAsyncDisposable
{
    private readonly CapabilityBrokerOptions _options;
    private readonly IDesktopCapabilityAuthorizer _authorizer;
    private readonly ConcurrentDictionary<string, DesktopSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);
    private int _disposed;

    public CapabilityBroker(
        CapabilityBrokerOptions options,
        IDesktopCapabilityAuthorizer? authorizer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Policy.Validate();

        // 默认拒绝：RPC 可达不等于获得桌面操作授权（计划 §7）。
        _authorizer = authorizer ?? DenyAllDesktopCapabilities.Instance;
    }

    public DesktopInstanceId HostDesktopId => _options.HostDesktopId;

    public string CoreInstanceId => _options.CoreInstanceId;

    public IReadOnlyList<DesktopSession> Sessions => _sessions.Values.ToArray();

    public DesktopSession? Find(DesktopInstanceId desktopId) =>
        _sessions.TryGetValue(desktopId.Value, out var session) ? session : null;

    /// <summary>接受一条已认证的连接：读 hello → 协商 → 回 ack → 启动会话循环。</summary>
    public async Task<CapabilityResult<DesktopSession>> AcceptAsync(
        ICoreDesktopChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_options.Policy.HandshakeTimeout);

        Proto.DesktopFrame? first;
        try
        {
            first = await channel.ReadAsync(handshake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false));
        }
        catch (Exception ex)
        {
            return Failure(DesktopCapabilityError.NotConnected($"handshake read failed ({ex.GetType().Name})"));
        }

        if (first is null)
        {
            return Failure(DesktopCapabilityError.NotConnected("desktop closed the stream before the hello"));
        }

        if (first.FrameCase != Proto.DesktopFrame.FrameOneofCase.Hello || first.Hello is null)
        {
            return Failure(DesktopCapabilityError.InvalidRequest(
                $"the first desktop frame must be hello, received {first.FrameCase}"));
        }

        var hello = first.Hello;

        if (!string.Equals(hello.DesktopId, _options.HostDesktopId.Value, StringComparison.Ordinal))
        {
            return Failure(DesktopCapabilityError.Unauthorized(
                "desktop identity does not match the expected desktop for this endpoint"));
        }

        if (!DesktopProcessInstanceId.IsValid(hello.ProcessInstanceId))
        {
            return Failure(DesktopCapabilityError.InvalidRequest("hello.process_instance_id is missing or invalid"));
        }

        if (hello.SupportedVersions is null)
        {
            return Failure(DesktopCapabilityError.InvalidRequest("hello.supported_versions is missing"));
        }

        if (hello.SupportedVersions.Minimum > int.MaxValue || hello.SupportedVersions.Maximum > int.MaxValue
            || !DesktopProtocolVersion.TryNegotiate(
                (int)hello.SupportedVersions.Minimum, (int)hello.SupportedVersions.Maximum, out var negotiatedVersion))
        {
            return Failure(DesktopCapabilityError.UnsupportedCapability("protocol version"));
        }

        var declared = new List<DesktopCapabilityDeclaration>(hello.Capabilities.Count);
        foreach (var declaration in hello.Capabilities)
        {
            if (declaration.Version > int.MaxValue)
            {
                return Failure(DesktopCapabilityError.InvalidRequest("capability version is out of range"));
            }

            var capability = DesktopCapabilities.TryGetByName(declaration.Capability, out var known)
                ? known.Capability
                : DesktopCapability.None;
            declared.Add(new DesktopCapabilityDeclaration(capability, declaration.Capability, (int)declaration.Version));
        }

        var grantable = DesktopCapability.None;
        foreach (var declaration in declared)
        {
            if (declaration.Capability != DesktopCapability.None
                && _options.Policy.Grantable.HasFlag(declaration.Capability))
            {
                grantable |= declaration.Capability;
            }
        }

        var granted = DesktopCapabilities.DeclareFor(grantable);
        var negotiationError = DesktopCapabilityNegotiation.Validate(declared, granted, out var negotiated, out _);
        if (negotiationError is not null)
        {
            return Failure(negotiationError);
        }

        if (_options.EnforceSingleActiveTransport)
        {
            lock (_sessions)
            {
                if (_sessions.TryGetValue(_options.HostDesktopId.Value, out var active)
                    && active.State is DesktopLinkState.Ready or DesktopLinkState.Handshaking)
                {
                    return Failure(DesktopCapabilityError.InvalidRequest(
                        "another capability transport is already active for this desktop"));
                }
            }
        }

        var generation = NextGeneration(_options.HostDesktopId);
        var ack = new Proto.CoreHelloAck
        {
            ConnectionId = $"{_options.CoreInstanceId}-{generation.Value}",
            Generation = (ulong)generation.Value,
            NegotiatedVersion = new Proto.ProtocolRange
            {
                Minimum = (uint)DesktopProtocolVersion.Minimum,
                Maximum = (uint)negotiatedVersion,
            },
            Limits = _options.Policy.ToWireLimits(),
            CoreInstanceId = _options.CoreInstanceId,
        };

        foreach (var declaration in granted)
        {
            ack.Capabilities.Add(new Proto.CapabilityDeclaration
            {
                Capability = declaration.Name,
                Version = (uint)declaration.Version,
            });
        }

        try
        {
            await channel.SendAsync(new Proto.CoreFrame { HelloAck = ack }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(DesktopCapabilityError.NotConnected($"hello_ack could not be sent ({ex.GetType().Name})"));
        }

        var session = new DesktopSession(
            _options.HostDesktopId,
            ack.ConnectionId,
            generation,
            ack,
            negotiated,
            channel,
            _options.Policy,
            _options.TimeProvider,
            _authorizer,
            _options.AuditSink);

        _sessions[_options.HostDesktopId.Value] = session;
        session.Start();

        // 会话结束时从注册表移除（只移除自己，避免把后来者顶掉）。
        _ = session.Completion.ContinueWith(
            completed =>
            {
                _ = completed;
                if (_sessions.TryGetValue(_options.HostDesktopId.Value, out var current)
                    && ReferenceEquals(current, session))
                {
                    _sessions.TryRemove(_options.HostDesktopId.Value, out _);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return CapabilityResult<DesktopSession>.Success(session);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            try
            {
                await session.DisconnectAsync(DesktopCapabilityError.NotConnected("core is shutting down"))
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 收尾不因单个会话失败而中断。
            }
        }

        _sessions.Clear();
    }

    private ConnectionGeneration NextGeneration(DesktopInstanceId desktopId) =>
        ConnectionGeneration.Require(_generations.AddOrUpdate(desktopId.Value, 1, static (_, current) => current + 1));

    private static CapabilityResult<DesktopSession> Failure(DesktopCapabilityError error) =>
        CapabilityResult<DesktopSession>.Failure(error);
}
