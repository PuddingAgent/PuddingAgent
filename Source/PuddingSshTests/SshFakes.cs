using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// 假传输（A3 判据「假传输故障矩阵全绿」的载体）：**不连网、不装 Core**，
/// 让协调器的准入、deadline、清理与结果映射可以在纯组件进程里被逐一取红。
/// </summary>
internal sealed class FakeTransportFactory : ISshTransportFactory
{
    private int _connectCount;

    /// <summary>建连行为；默认直接返回可用的 <see cref="FakeSession"/>。</summary>
    public Func<SshTransportConnectRequest, CancellationToken, Task<ISshTransportSession>> OnConnect { get; set; } =
        (_, _) => Task.FromResult<ISshTransportSession>(new FakeSession());

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public List<SshTransportConnectRequest> Requests { get; } = [];

    public List<ISshTransportSession> Sessions { get; } = [];

    public int MaxObservedConcurrency { get; private set; }

    private int _concurrency;

    public async Task<ISshTransportSession> ConnectAsync(SshTransportConnectRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectCount);
        lock (Requests)
        {
            Requests.Add(request);
        }

        var current = Interlocked.Increment(ref _concurrency);
        lock (Requests)
        {
            if (current > MaxObservedConcurrency)
            {
                MaxObservedConcurrency = current;
            }
        }

        try
        {
            var session = await OnConnect(request, cancellationToken).ConfigureAwait(false);
            lock (Requests)
            {
                Sessions.Add(session);
            }

            return session;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrency);
        }
    }
}

internal sealed class FakeSession : ISshTransportSession
{
    private int _executeCount;

    public Func<string, TimeSpan, int, CancellationToken, Task<SshExecuteResult>> OnExecute { get; set; } =
        (_, _, _, _) => Task.FromResult(FakeResults.Completed());

    /// <summary>释放延迟：用于验证「释放本身也有界」。</summary>
    public TimeSpan DisposeDelay { get; set; }

    public int ExecuteCount => Volatile.Read(ref _executeCount);

    public bool Disposed { get; private set; }

    public string? LastCommand { get; private set; }

    public int? LastOutputBudget { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    public string VerifiedHostKeySha256 { get; set; } = "SHA256:fake-verified-key";

    public string HostKeyAlgorithm { get; set; } = "ssh-ed25519";

    public bool IsConnected => !Disposed;

    public Task<SshExecuteResult> ExecuteAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executeCount);
        LastCommand = command;
        LastTimeout = timeout;
        LastOutputBudget = maxOutputBytes;
        return OnExecute(command, timeout, maxOutputBytes, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        if (DisposeDelay > TimeSpan.Zero)
        {
            await Task.Delay(DisposeDelay).ConfigureAwait(false);
        }
    }
}

internal sealed class FakeIdentityProvider : ISshIdentityProvider
{
    private int _calls;

    public Func<SshIdentityRequest, SshIdentityMaterial> OnGet { get; set; } = _ => FakeSsh.Material();

    /// <summary>设置后优先于 <see cref="OnGet"/>：用于「忽略 token / 晚返回 / 晚失败」的身份端口。</summary>
    public Func<SshIdentityRequest, CancellationToken, Task<SshIdentityMaterial>>? OnGetAsync { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<SshIdentityMaterial> GetIdentityAsync(SshIdentityRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (OnGetAsync is { } async)
        {
            return new ValueTask<SshIdentityMaterial>(async(request, cancellationToken));
        }

        return ValueTask.FromResult(OnGet(request));
    }
}

internal static class FakeResults
{
    public static SshExecuteResult Completed(int exitCode = 0, string stdout = "ok") => new()
    {
        Status = SshExecutionStatus.Completed,
        ExecutionState = SshExecutionState.Exited,
        HostId = "dev-linux",
        HostRevision = "rev-1",
        VerifiedHostKeySha256 = "SHA256:fake-verified-key",
        ExitCode = exitCode,
        Stdout = stdout,
        CapturedBytes = stdout.Length,
        ReceivedBytes = stdout.Length,
        Phase = SshPhase.Completed,
        DurationMs = 1,
    };
}

/// <summary>构造冻结主机上下文与请求的小工具（测试里的「引用方」替身）。</summary>
internal static class FakeSsh
{
    public const string HostId = "dev-linux";
    public const string Revision = "rev-1";
    public const string Trust = "trust-1";
    public const string HostName = "192.0.2.10";
    public const int Port = 22;
    public const string User = "deploy";

    public static SshTargetRef Target(
        string hostId = HostId,
        string revision = Revision,
        string host = HostName,
        int port = Port,
        string user = User,
        string trust = Trust) => new(hostId, revision, host, port, user, trust);

    public static SshIdentityMaterial Material() => new("/tmp/fake-ssh", "id_ed25519", new byte[32]);

    public static SshHostContext Host(
        SshTargetRef? target = null,
        int operationTimeoutSeconds = 60,
        int maxOperationTimeoutSeconds = 600,
        int maxOutputBytes = 64 * 1024,
        int maxConcurrentOperations = 2,
        int connectTimeoutSeconds = 15,
        string? defaultCwd = null,
        DateTimeOffset? deadline = null,
        SshTrustSnapshot? trust = null) => new(
        target ?? Target(),
        new SshOperationLimits
        {
            OperationTimeoutSeconds = operationTimeoutSeconds,
            MaxOperationTimeoutSeconds = maxOperationTimeoutSeconds,
            MaxOutputBytes = maxOutputBytes,
            MaxConcurrentOperations = maxConcurrentOperations,
            ConnectTimeoutSeconds = connectTimeoutSeconds,
        },
        new SshIdentityRequest("/tmp/fake-ssh", "id_ed25519"),
        trust ?? SshTrustSnapshot.FromPin("ssh-ed25519", "SHA256:fake-verified-key"),
        defaultCwd,
        deadline);

    public static SshExecuteRequest Execute(
        SshTargetRef? target = null,
        string command = "uname -a",
        string? cwd = null,
        int? timeoutSeconds = null,
        int? maxOutputBytes = null) => new(target ?? Target(), command)
    {
        Cwd = cwd,
        TimeoutSeconds = timeoutSeconds,
        MaxOutputBytes = maxOutputBytes,
    };

    public static SshTestRequest Test(SshTargetRef? target = null) => new(target ?? Target());

    /// <summary>轮询等待条件成立（测试里避免固定 sleep 造成的抖动）。</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }
}
