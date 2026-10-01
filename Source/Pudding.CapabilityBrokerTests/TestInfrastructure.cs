using System.Collections.Concurrent;
using System.Threading.Channels;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerTests;

internal static class TestWait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? Default);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                throw new TimeoutException($"Condition was not met within the test budget: {because}");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>假 Desktop 通道：测试扮演 Desktop（推送结果/事件、观察 Core 写出的帧）。</summary>
internal sealed class FakeDesktopChannel : ICoreDesktopChannel
{
    private readonly Channel<Proto.DesktopFrame> _inbound = Channel.CreateUnbounded<Proto.DesktopFrame>();
    private readonly List<Proto.CoreFrame> _sent = [];
    private readonly object _sync = new();
    private readonly TaskCompletionSource _sendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>为真时 <see cref="SendAsync"/> 会阻塞，用于制造「出站队列满」。</summary>
    public bool BlockSends { get; set; }

    public IReadOnlyList<Proto.CoreFrame> Sent
    {
        get
        {
            lock (_sync)
            {
                return _sent.ToArray();
            }
        }
    }

    public Proto.CoreFrame? LastSent
    {
        get
        {
            lock (_sync)
            {
                return _sent.Count == 0 ? null : _sent[^1];
            }
        }
    }

    /// <summary>Core 写出去的命令帧。</summary>
    public IEnumerable<Proto.CapabilityCommand> Commands =>
        Sent.Where(frame => frame.Command is not null).Select(frame => frame.Command);

    public void Push(Proto.DesktopFrame frame) => _inbound.Writer.TryWrite(frame);

    public void PushClose() => _inbound.Writer.TryComplete();

    /// <summary>以异常结束读循环（模拟底层管道/传输故障）——会立即打断正在等待的读取。</summary>
    public void PushFault(Exception exception) => _inbound.Writer.TryComplete(exception);

    public void ReleaseSends() => _sendGate.TrySetResult();

    public async ValueTask SendAsync(Proto.CoreFrame frame, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _sent.Add(frame);
        }

        if (BlockSends)
        {
            await _sendGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<Proto.DesktopFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                ? await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}

internal sealed class RecordingAuditSink : IDesktopCapabilityAuditSink
{
    private readonly ConcurrentQueue<DesktopCapabilityAuditRecord> _records = new();

    public IReadOnlyList<DesktopCapabilityAuditRecord> Records => _records.ToArray();

    public IReadOnlyList<DesktopCapabilityAuditRecord> ForOperation(OperationId operationId) =>
        Records.Where(record => record.OperationId == operationId).ToArray();

    public void Record(DesktopCapabilityAuditRecord record) => _records.Enqueue(record);
}

internal sealed class HarnessOptions
{
    public DesktopCapability Declared { get; set; } =
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.ShellNotification;

    public DesktopCapability Grantable { get; set; } =
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.ShellNotification;

    public int MaxInFlightPerConnection { get; set; } = 8;

    public int MaxQueuedFrames { get; set; } = 128;

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool EnforceSingleActiveTransport { get; set; } = true;

    public IDesktopCapabilityAuthorizer? Authorizer { get; set; } = AllowAllDesktopCapabilities.Instance;

    public string DesktopId { get; set; } = "desk-1";
}

/// <summary>已握手的会话 + 假通道 + 审计出口。</summary>
internal sealed class BrokerHarness : IAsyncDisposable
{
    private BrokerHarness(
        HarnessOptions options,
        FakeDesktopChannel channel,
        CapabilityBroker broker,
        DesktopSession session,
        RecordingAuditSink audit)
    {
        Options = options;
        Channel = channel;
        Broker = broker;
        Session = session;
        Audit = audit;
    }

    public HarnessOptions Options { get; }

    public FakeDesktopChannel Channel { get; }

    public CapabilityBroker Broker { get; }

    public DesktopSession Session { get; }

    public RecordingAuditSink Audit { get; }

    public static async Task<BrokerHarness> StartAsync(
        HarnessOptions? options = null, FakeDesktopChannel? channel = null)
    {
        options ??= new HarnessOptions();
        channel ??= new FakeDesktopChannel();
        var audit = new RecordingAuditSink();

        var broker = new CapabilityBroker(
            new CapabilityBrokerOptions
            {
                HostDesktopId = new DesktopInstanceId(options.DesktopId),
                Policy = new DesktopCapabilityPolicy
                {
                    Grantable = options.Grantable,
                    MaxInFlightPerConnection = options.MaxInFlightPerConnection,
                    MaxQueuedFrames = options.MaxQueuedFrames,
                    HandshakeTimeout = options.HandshakeTimeout,
                },
                EnforceSingleActiveTransport = options.EnforceSingleActiveTransport,
                AuditSink = audit,
            },
            options.Authorizer);

        channel.Push(Hello(options));
        var accepted = await broker.AcceptAsync(channel);

        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException($"handshake failed: {accepted.Error}");
        }

        return new BrokerHarness(options, channel, broker, accepted.Value, audit);
    }

    public static Proto.DesktopFrame Hello(HarnessOptions options)
    {
        var hello = new Proto.DesktopHello
        {
            DesktopId = options.DesktopId,
            ProcessInstanceId = "proc-1",
            SupportedVersions = new Proto.ProtocolRange { Minimum = 1, Maximum = 1 },
        };

        foreach (var declaration in DesktopCapabilities.DeclareFor(options.Declared))
        {
            hello.Capabilities.Add(new Proto.CapabilityDeclaration
            {
                Capability = declaration.Name,
                Version = (uint)declaration.Version,
            });
        }

        return new Proto.DesktopFrame { Hello = hello };
    }

    public DesktopCallContext Call(string operationId = "op-1", TimeSpan? deadline = null) =>
        new(
            new DesktopInstanceId(Options.DesktopId),
            new OperationId(operationId),
            DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(30)));

    public const string ContextId = "ctx-1";

    public const string PageId = "page-1";

    public static DesktopPageTarget Target => new(ContextId, PageId);

    public static NavigateRequest Navigate(string url = "https://example.com/a") => new(Target, new Uri(url));

    public static JavascriptRequest Javascript(string script = "return 1;") => new(Target, script);

    public static DesktopNotificationRequest Notification() => new("标题", "内容");

    public static Proto.CapabilityCommand? CommandFor(IEnumerable<Proto.CoreFrame> frames, string operationId) =>
        frames.Where(frame => frame.Command is not null)
            .Select(frame => frame.Command)
            .FirstOrDefault(command => command.OperationId == operationId);

    public Task<Proto.CapabilityCommand?> WaitForCommandAsync(string operationId, TimeSpan? timeout = null) =>
        WaitForAsync(
            () => CommandFor(Channel.Sent, operationId),
            $"command for operation {operationId}",
            timeout);

    public Task<Proto.CoreFrame?> WaitForCancelAsync(string operationId, TimeSpan? timeout = null) =>
        WaitForAsync(
            () => Channel.Sent.FirstOrDefault(frame =>
                frame.Cancel is not null && frame.Cancel.OperationId == operationId),
            $"cancel frame for operation {operationId}",
            timeout);

    private async Task<T?> WaitForAsync<T>(Func<T?> probe, string because, TimeSpan? timeout)
        where T : class
    {
        var limit = DateTime.UtcNow + (timeout ?? TestWait.Default);
        while (DateTime.UtcNow < limit)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected {because} but it never appeared.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Session.DisconnectAsync(DesktopCapabilityError.NotConnected("test teardown"));
        }
        catch (Exception)
        {
            // 测试收尾。
        }

        await Broker.DisposeAsync();
    }
}

/// <summary>构造 Desktop → Core 的帧（测试扮演 Desktop）。</summary>
internal static class DesktopFrames
{
    public static Proto.DesktopFrame Result(Proto.OperationResult result) => new() { Result = result };

    public static Proto.OperationResult NavigateOk(
        string operationId, ulong generation, string url = "https://example.com/done", long pageVersion = 3,
        Proto.NavigateDisposition disposition = Proto.NavigateDisposition.Completed) =>
        new()
        {
            OperationId = operationId,
            Generation = generation,
            Navigate = new Proto.NavigateOutcome { Disposition = disposition, CurrentUrl = url, PageVersion = pageVersion },
        };

    public static Proto.OperationResult JavascriptOk(
        string operationId, ulong generation, string json = "\"ok\"", bool truncated = false) =>
        new()
        {
            OperationId = operationId,
            Generation = generation,
            ExecuteJavascript = new Proto.JavascriptOutcome
            {
                Kind = Proto.JavascriptValueKind.String,
                JsonValue = json,
                Truncated = truncated,
            },
        };

    public static Proto.OperationResult PageStateOk(
        string operationId, ulong generation, string url = "https://example.com/state", long pageVersion = 5,
        string readiness = "complete") =>
        new()
        {
            OperationId = operationId,
            Generation = generation,
            PageState = new Proto.PageStateOutcome { Url = url, PageVersion = pageVersion, Readiness = readiness },
        };

    public static Proto.OperationResult NotificationOk(string operationId, ulong generation, bool shown = true) =>
        new()
        {
            OperationId = operationId,
            Generation = generation,
            ShowNotification = new Proto.NotificationOutcome { Shown = shown, NotificationId = "n-1" },
        };

    public static Proto.OperationResult Error(
        string operationId,
        ulong generation,
        string code = "invalid_target",
        string message = "nope",
        bool retryable = true,
        bool mayHaveSideEffects = false) =>
        new()
        {
            OperationId = operationId,
            Generation = generation,
            Error = new Proto.ErrorOutcome
            {
                Code = code,
                Message = message,
                Retryable = retryable,
                MayHaveSideEffects = mayHaveSideEffects,
            },
        };

    public static Proto.DesktopFrame Event(string state = "complete") =>
        new()
        {
            Event = new Proto.DesktopEvent
            {
                EventId = "evt-1",
                PageState = new Proto.PageStateChanged
                {
                    Target = new Proto.CommandTarget { ContextId = BrokerHarness.ContextId, PageId = BrokerHarness.PageId },
                    State = state,
                    PageVersion = 4,
                },
            },
        };
}

internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuddingAgentNetwork.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (PuddingAgentNetwork.slnx) not found above {AppContext.BaseDirectory}.");
    }
}
