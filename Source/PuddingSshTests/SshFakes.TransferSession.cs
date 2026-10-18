using PuddingSsh.Contracts;

namespace PuddingSshTests;

/// <summary>假传输工厂：连接可被 gate 住，用于验证迟到连接/取消/释放路径。</summary>
internal sealed class FakeTransferFactory : ISshTransferFactory
{
    private int _connectCount;

    public Func<SshTransportConnectRequest, CancellationToken, Task<ISshTransferSession>> OnConnect { get; set; } =
        (_, _) => Task.FromResult<ISshTransferSession>(new FakeTransferSession());

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public List<SshTransportConnectRequest> Requests { get; } = [];

    public List<string> ScratchDirectories { get; } = [];

    public Task<ISshTransferSession> ConnectTransferAsync(
        SshTransportConnectRequest request,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectCount);
        Requests.Add(request);
        ScratchDirectories.Add(scratchDirectory);
        return OnConnect(request, cancellationToken);
    }
}

/// <summary>假传输会话：可编排上传/下载结果、释放行为与能力。</summary>
internal sealed class FakeTransferSession : ISshTransferSession
{
    private int _uploadCount;
    private int _downloadCount;
    private int _abortRequested;
    private int _capabilityProbeCount;
    private int _closed;

    public Func<SshUploadPlan, TimeSpan, CancellationToken, Task<SshTransferOutcome>> OnUpload { get; set; } =
        (plan, _, _) => Task.FromResult(SshTransferOutcome.Completed(new FileInfo(plan.LocalPath).Length));

    public Func<SshDownloadPlan, TimeSpan, CancellationToken, Task<SshTransferOutcome>> OnDownload { get; set; } =
        (_, _, _) => Task.FromResult(SshTransferOutcome.Completed(7));

    /// <summary>能力快照：探测成功后由会话持有（结果阶段只读它）。</summary>
    public SshTransferCapabilities? Capabilities { get; private set; }

    /// <summary>探测钩子（默认立即返回 <see cref="CapabilityResult"/>）；可用来挂起/抛错。</summary>
    public Func<CancellationToken, Task<SshTransferCapabilities>>? OnGetCapabilities { get; set; }

    /// <summary>探测返回的能力。</summary>
    public SshTransferCapabilities CapabilityResult { get; set; } = new(true, false, true);

    /// <summary>能力探测被调用次数。</summary>
    public int CapabilityProbeCount => Volatile.Read(ref _capabilityProbeCount);

    public bool DisposeNeverCompletes { get; set; }

    public TimeSpan DisposeDelay { get; set; }

    public int UploadCount => Volatile.Read(ref _uploadCount);

    public int DownloadCount => Volatile.Read(ref _downloadCount);

    public bool Disposed { get; private set; }

    public string VerifiedHostKeySha256 => "SHA256:fake-transfer-key";

    public bool Closed => Volatile.Read(ref _closed) == 1 || Disposed;

    public bool AbortRequested => Volatile.Read(ref _abortRequested) == 1;

    public void RequestAbort() => Volatile.Write(ref _abortRequested, 1);

    public async Task<SshTransferCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _capabilityProbeCount);
        Capabilities = OnGetCapabilities is { } hook
            ? await hook(cancellationToken).ConfigureAwait(false)
            : CapabilityResult;
        return Capabilities;
    }

    public Task<SshTransferOutcome> UploadAsync(SshUploadPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _uploadCount);
        return OnUpload(plan, timeout, cancellationToken);
    }

    public Task<SshTransferOutcome> DownloadAsync(SshDownloadPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _downloadCount);
        return OnDownload(plan, timeout, cancellationToken);
    }

    public ValueTask DisposeAsync(TimeSpan budget)
    {
        _ = budget;
        return DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
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
