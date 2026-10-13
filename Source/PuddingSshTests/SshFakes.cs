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

internal sealed class FakeSession : ISshTransportSession, ISshTransportSessionDiagnostics
{
    private int _executeCount;
    private int _submissionEvidence;
    private int _abortRequested;
    private int _closed;
    private int _disposeCallCount;

    public Func<string, TimeSpan, int, CancellationToken, Task<SshExecuteResult>> OnExecute { get; set; } =
        (_, _, _, _) => Task.FromResult(FakeResults.Completed());

    /// <summary>释放延迟：用于验证「释放本身也有界」。</summary>
    public TimeSpan DisposeDelay { get; set; }

    /// <summary>释放是否永不返回（模拟不合作的清理）。</summary>
    public bool DisposeNeverCompletes { get; set; }

    /// <summary>释放方法**同步阻塞**的时长（模拟「返回 ValueTask 之前的同步入口」）。</summary>
    public TimeSpan SynchronousBlockBeforeDispose { get; set; }

    /// <summary>释放被调用了几次：单次关闭状态机的门禁断言。</summary>
    public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

    /// <summary>最近一次带预算释放收到的额度（L3 预算合同门禁）。</summary>
    public TimeSpan? LastCloseBudget { get; private set; }

    /// <summary>
    /// exec 调用**同步阻塞**的时长（模拟「方法返回 Task 之前的同步启动段」）。
    /// 协调器必须把它放在有界 worker 上，业务等待不得因此被卡住。
    /// </summary>
    public TimeSpan SynchronousBlockBeforeExecute { get; set; }

    /// <summary>是否在 exec 调用时推进提交事实（真实适配器就是这样做的）。</summary>
    public bool MarkSubmissionAttempted { get; set; } = true;

    /// <summary>测试直接设定的提交事实（在 <see cref="MarkSubmissionAttempted"/> 为 false 时生效）。</summary>
    public SshSubmissionEvidence SubmissionEvidenceOverride { get; set; } = SshSubmissionEvidence.NotSent;

    public int ExecuteCount => Volatile.Read(ref _executeCount);

    public bool Disposed { get; private set; }

    public string? LastCommand { get; private set; }

    public int? LastOutputBudget { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    public string VerifiedHostKeySha256 { get; set; } = "SHA256:fake-verified-key";

    public string HostKeyAlgorithm { get; set; } = "ssh-ed25519";

    public bool IsConnected => !Disposed;

    public SshSubmissionEvidence SubmissionEvidence =>
        Volatile.Read(ref _submissionEvidence) == 1 ? SshSubmissionEvidence.SubmissionAttempted : SubmissionEvidenceOverride;

    public int PendingReaders => 0;

    public bool AbortRequested => Volatile.Read(ref _abortRequested) == 1;

    public bool Closed => Volatile.Read(ref _closed) == 1 || Disposed;

    public void RequestAbort() => Volatile.Write(ref _abortRequested, 1);

    public Task<SshExecuteResult> ExecuteAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executeCount);
        LastCommand = command;
        LastTimeout = timeout;
        LastOutputBudget = maxOutputBytes;

        if (MarkSubmissionAttempted)
        {
            Volatile.Write(ref _submissionEvidence, 1);
        }

        if (SynchronousBlockBeforeExecute > TimeSpan.Zero)
        {
            Thread.Sleep(SynchronousBlockBeforeExecute);
        }

        return OnExecute(command, timeout, maxOutputBytes, cancellationToken);
    }

    public ValueTask DisposeAsync(TimeSpan budget)
    {
        LastCloseBudget = budget;
        return DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCallCount);
        Disposed = true;

        if (SynchronousBlockBeforeDispose > TimeSpan.Zero)
        {
            Thread.Sleep(SynchronousBlockBeforeDispose);
        }

        if (DisposeNeverCompletes)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }

        if (DisposeDelay > TimeSpan.Zero)
        {
            await Task.Delay(DisposeDelay).ConfigureAwait(false);
        }

        Volatile.Write(ref _closed, 1);
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
        SshTrustSnapshot? trust = null,
        SshOperationLimits? limits = null) => new(
        target ?? Target(),
        limits ?? new SshOperationLimits
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

/// <summary>
/// 确定性假文件系统（切片 B 路径策略门禁）：reparse point / 普通文件 / 目录可以精确编排，
/// 不依赖 Windows 符号链接权限。真实文件系统路径由集成测试与探针覆盖。
/// </summary>
internal sealed class FakeLocalFileSystem : PuddingSsh.Validation.ISshLocalFileSystem
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(bool IsDirectory, bool IsRegularFile, bool IsReparsePoint, long Length);

    public FakeLocalFileSystem Root(string path) => Directory(path);

    public FakeLocalFileSystem Directory(string path)
    {
        EnsureAncestors(path);
        _entries[Canonical(path)] = new Entry(true, false, false, 0);
        return this;
    }

    public FakeLocalFileSystem File(string path, long length = 0)
    {
        EnsureAncestors(path);
        _entries[Canonical(path)] = new Entry(false, true, false, length);
        return this;
    }

    public FakeLocalFileSystem ReparseDirectory(string path)
    {
        EnsureAncestors(path);
        _entries[Canonical(path)] = new Entry(true, false, true, 0);
        return this;
    }

    public FakeLocalFileSystem NonRegular(string path)
    {
        EnsureAncestors(path);
        _entries[Canonical(path)] = new Entry(false, false, false, 0);
        return this;
    }

    public bool Exists(string path) => _entries.ContainsKey(Canonical(path));

    public bool IsDirectory(string path) => _entries.TryGetValue(Canonical(path), out var entry) && entry.IsDirectory;

    public bool IsRegularFile(string path) => _entries.TryGetValue(Canonical(path), out var entry) && entry.IsRegularFile;

    public bool IsReparsePoint(string path) => _entries.TryGetValue(Canonical(path), out var entry) && entry.IsReparsePoint;

    public long GetLength(string path) => _entries.TryGetValue(Canonical(path), out var entry) ? entry.Length : 0;

    public string GetFullPath(string path) => Canonical(path);

    private void EnsureAncestors(string path)
    {
        var parent = ParentOf(Canonical(path));
        while (parent is not null and not "")
        {
            if (!_entries.ContainsKey(parent))
            {
                _entries[parent] = new Entry(true, false, false, 0);
            }

            parent = ParentOf(parent);
        }
    }

    private static string? ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? null : path[..index];
    }

    /// <summary>POSIX 风格规范化：统一分隔符、消解 <c>.</c>/<c>..</c>（保持测试跨平台确定性）。</summary>
    private static string Canonical(string path)
    {
        var replaced = path.Replace('\\', '/');
        var absolute = replaced.StartsWith('/') || (replaced.Length > 1 && replaced[1] == ':');
        var segments = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(segment);
        }

        var body = string.Join('/', stack);
        return absolute ? "/" + body : body;
    }
}