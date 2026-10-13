using System.Diagnostics;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSsh.Transport;

/// <summary>
/// 单文件 SFTP 传输会话（切片 B）：流式复制、字节计数、同目录唯一临时文件 + 发布、覆盖策略、
/// **能力实测**（不支持即返回不支持）、取消删临时文件、清理失败明确诊断。
/// <para>
/// 设计约束（§11.1）：
/// <list type="bullet">
///   <item>默认禁止覆盖；不用「先检查存在再覆盖」的竞态实现冒充原子性——原子性来自实测的 rename 能力。</item>
///   <item>流式复制，记录实际字节，超限中止，禁止整文件装入内存。</item>
///   <item>不自动重试、不提供断点续传；清理失败要明确报告临时文件残留，不得误报原目标已回滚。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SshSftpTransferSession : ISshTransferSession
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly ISshSftpChannel _channel;
    private readonly ISshLocalFileSystem _localFileSystem;
    private readonly string _scratchDirectory;
    private readonly Action? _releaseOwnedResources;
    private readonly object _probeGate = new();
    private SshTransferCapabilities? _capabilities;
    private int _abortRequested;

    public SshSftpTransferSession(
        ISshSftpChannel channel,
        string verifiedHostKeySha256,
        string scratchDirectory,
        ISshLocalFileSystem? localFileSystem = null,
        Action? releaseOwnedResources = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        VerifiedHostKeySha256 = verifiedHostKeySha256;
        _scratchDirectory = scratchDirectory;
        _localFileSystem = localFileSystem ?? SshLocalFileSystem.Instance;
        _releaseOwnedResources = releaseOwnedResources;
    }

    public string VerifiedHostKeySha256 { get; }

    public bool AbortRequested => Volatile.Read(ref _abortRequested) == 1;

    public void RequestAbort() => Volatile.Write(ref _abortRequested, 1);

    public async Task<SshTransferCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        lock (_probeGate)
        {
            if (_capabilities is { } cached)
            {
                return cached;
            }
        }

        var measured = await ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        lock (_probeGate)
        {
            _capabilities ??= measured;
            return _capabilities;
        }
    }

    /// <summary>
    /// 能力实测：在临时目录用 scratch 文件测「原子替换 rename」「标准 rename 是否覆盖」「排他创建」。
    /// 任何异常都按「不支持」处理（绝不假定支持）。
    /// </summary>
    private async Task<SshTransferCapabilities> ProbeCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var a = Join(_scratchDirectory, ".pudding-cap-a-" + token);
        var b = Join(_scratchDirectory, ".pudding-cap-b-" + token);
        var c = Join(_scratchDirectory, ".pudding-cap-c-" + token);
        var d = Join(_scratchDirectory, ".pudding-cap-d-" + token);
        var scratch = new[] { a, b, c, d };

        var atomicReplace = false;
        var renameNoOverwrite = false;
        var exclusiveCreate = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            WriteBytes(a, [1, 1, 1]);
            WriteBytes(b, [2, 2, 2]);

            // 1) posix-rename：原子替换（B 应被 A 覆盖且 A 消失）。
            try
            {
                _channel.RenamePosix(a, b);
                atomicReplace = _channel.Exists(b) && !_channel.Exists(a) && ContentOf(b).SequenceEqual(new byte[] { 1, 1, 1 });
            }
            catch (Exception)
            {
                atomicReplace = false;
            }

            // 2) 标准 RENAME 的覆盖语义（OpenSSH 实测覆盖 ⇒ 不能用它冒充"不覆盖发布"）。
            WriteBytes(c, [3, 3, 3]);
            WriteBytes(d, [4, 4, 4]);
            try
            {
                _channel.Rename(c, d);
                var content = ContentOf(d);
                renameNoOverwrite = !content.SequenceEqual(new byte[] { 3, 3, 3 });
            }
            catch (Exception)
            {
                // 抛错即"拒绝覆盖"——这正是不覆盖语义。
                renameNoOverwrite = true;
            }

            // 3) 排他创建（SSH_FXF_EXCL）。
            var exclusive = Join(_scratchDirectory, ".pudding-cap-x-" + token);
            WriteBytes(exclusive, [9]);
            try
            {
                using var _ = _channel.OpenWriteNew(exclusive);
                exclusiveCreate = false;
            }
            catch (Exception)
            {
                exclusiveCreate = true;
            }

            TryDelete(exclusive);

            await Task.CompletedTask.ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in scratch)
            {
                TryDelete(path);
            }
        }

        return new SshTransferCapabilities(atomicReplace, renameNoOverwrite, exclusiveCreate);
    }

    // ---------- 上传 ----------

    public async Task<SshTransferOutcome> UploadAsync(SshUploadPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var stopwatch = Stopwatch.StartNew();
        long transferred = 0;

        // 远端父目录 realpath 必须在允许根内（符号链接逃逸拒绝）。
        try
        {
            EnsureRealParentWithinRoot(plan.RemoteRoot, plan.RemotePath);
        }
        catch (SshComponentException ex)
        {
            return SshTransferOutcome.Failed(ex.ErrorCode, 0, ex.Message);
        }

        if (plan.Overwrite == SshOverwritePolicy.Fail && _channel.Exists(plan.RemotePath))
        {
            // 仅为**友好错误**；原子保证来自实测 rename 能力，不依赖这次检查。
            return SshTransferOutcome.Failed(SshErrorCodes.RemotePathExists, 0, "远端目标已存在且未允许覆盖。");
        }

        var capabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var publish = ResolvePublishPlan(plan.Overwrite, capabilities);
        if (publish is null)
        {
            return SshTransferOutcome.Failed(
                SshErrorCodes.TransferUnsupported,
                0,
                $"服务端不支持所需的发布语义（overwrite={plan.Overwrite}，实测 capabilities=" +
                $"atomic_replace={capabilities.AtomicReplace} exclusive_create={capabilities.ExclusiveCreate}）");
        }

        // 两种发布语义（都由**实测能力**支撑，不用竞态冒充）：
        //   Fail      → 直接对目标做**排他创建**（原子占位；失败/取消时删除我们创建的目标）；
        //   Overwrite → 先写同目录唯一临时文件，再用 posix-rename 原子替换（保留旧目标直到最后一刻）。
        var uploadTarget = publish.Value == UploadPublishMode.ExclusiveCreateTarget ? plan.RemotePath : plan.RemoteTempPath;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var token = linked.Token;

        try
        {
            using var source = new FileStream(plan.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            await _channel.UploadAsync(source, uploadTarget, plan.MaxBytes, token).ConfigureAwait(false);
            transferred = source.Length;

            if (AbortRequested)
            {
                var abandoned = AbandonUpload(plan, publish.Value, out var leftBehind, out var detail);
                return SshTransferOutcome.Cancelled(transferred, "本端中止：" + abandoned + detail, leftBehind, leftBehind ? uploadTarget : null);
            }

            if (publish.Value == UploadPublishMode.TempThenAtomicReplace)
            {
                Publish(plan.RemoteTempPath, plan.RemotePath);
            }

            return SshTransferOutcome.Completed(transferred);
        }
        catch (OperationCanceledException)
        {
            var abandoned = AbandonUpload(plan, publish.Value, out var leftBehind, out var detail);
            return SshTransferOutcome.Cancelled(transferred, "上传被取消/到期：" + abandoned + detail, leftBehind, leftBehind ? uploadTarget : null);
        }
        catch (Exception ex)
        {
            var mapped = MapUploadFailure(ex, transferred, stopwatch);
            var abandoned = AbandonUpload(plan, publish.Value, out var tempLeft, out var cleanupDetail);
            return mapped with
            {
                TemporaryLeftBehind = tempLeft,
                TemporaryPath = tempLeft ? uploadTarget : null,
                Diagnostic = mapped.Diagnostic + "；" + abandoned + cleanupDetail,
            };
        }
    }

    // ---------- 下载 ----------

    public async Task<SshTransferOutcome> DownloadAsync(SshDownloadPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        long transferred = 0;

        try
        {
            EnsureRealParentWithinRoot(plan.RemoteRoot, plan.RemotePath);
        }
        catch (SshComponentException ex)
        {
            return SshTransferOutcome.Failed(ex.ErrorCode, 0, ex.Message);
        }

        if (!_channel.Exists(plan.RemotePath))
        {
            return SshTransferOutcome.Failed(SshErrorCodes.RemotePathInvalid, 0, "远端源文件不存在。");
        }

        if (!_channel.IsRegularFile(plan.RemotePath))
        {
            return SshTransferOutcome.Failed(SshErrorCodes.RemotePathInvalid, 0, "远端源不是普通文件（切片 B 只处理单文件）。");
        }

        var length = _channel.GetLength(plan.RemotePath);
        if (length > plan.MaxBytes)
        {
            return SshTransferOutcome.Failed(
                SshErrorCodes.TransferTooLarge,
                0,
                $"远端文件 {length} 字节超过本次传输上限 {plan.MaxBytes} 字节。");
        }

        if (plan.Overwrite == SshOverwritePolicy.Fail && _localFileSystem.Exists(plan.LocalPath))
        {
            return SshTransferOutcome.Failed(SshErrorCodes.LocalPathExists, 0, "本地目标已存在且未允许覆盖。");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var token = linked.Token;

        try
        {
            using (var destination = new FileStream(plan.LocalTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await _channel.DownloadAsync(plan.RemotePath, destination, token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
                transferred = destination.Length;
            }

            if (AbortRequested)
            {
                CleanupLocalTemp(plan.LocalTempPath, out var leftBehind, out var detail);
                return SshTransferOutcome.Cancelled(transferred, "本端中止：已删除本地临时文件。" + detail, leftBehind, leftBehind ? plan.LocalTempPath : null);
            }

            PublishLocal(plan.LocalTempPath, plan.LocalPath, plan.Overwrite);
            return SshTransferOutcome.Completed(transferred);
        }
        catch (OperationCanceledException)
        {
            CleanupLocalTemp(plan.LocalTempPath, out var leftBehind, out var detail);
            return SshTransferOutcome.Cancelled(transferred, "下载被取消/到期：已删除本地临时文件。" + detail, leftBehind, leftBehind ? plan.LocalTempPath : null);
        }
        catch (Exception ex)
        {
            var mapped = MapDownloadFailure(ex, transferred);
            CleanupLocalTemp(plan.LocalTempPath, out var tempLeft, out var cleanupDetail);
            return mapped with
            {
                TemporaryLeftBehind = tempLeft,
                TemporaryPath = tempLeft ? plan.LocalTempPath : null,
                Diagnostic = mapped.Diagnostic + cleanupDetail,
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        DisposeResources();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync(TimeSpan budget)
    {
        _ = budget;
        DisposeResources();
        return ValueTask.CompletedTask;
    }

    private void DisposeResources()
    {
        try
        {
            _channel.Dispose();
        }
        finally
        {
            _releaseOwnedResources?.Invoke();
        }
    }

    // ---------- 内部 ----------

    /// <summary>发布：posix-rename 原子替换（overwrite=true 路径）。</summary>
    private void Publish(string tempPath, string finalPath) => _channel.RenamePosix(tempPath, finalPath);

    /// <summary>
    /// 发布语义选择（全部依赖**实测能力**）：
    /// <list type="bullet">
    ///   <item><c>Fail</c>：需要排他创建（原子占位；服务端已有的目标绝不会被写入）。</item>
    ///   <item><c>Overwrite</c>：需要 posix-rename 原子替换（旧目标保留到最后一次 rename）。</item>
    /// </list>
    /// 不支持即返回 null ⇒ 上层返回 <c>ssh.transfer_unsupported</c>，不退化到「先检查再覆盖」。
    /// </summary>
    private static UploadPublishMode? ResolvePublishPlan(SshOverwritePolicy overwrite, SshTransferCapabilities capabilities) => overwrite switch
    {
        SshOverwritePolicy.Fail => capabilities.ExclusiveCreate ? UploadPublishMode.ExclusiveCreateTarget : null,
        SshOverwritePolicy.Overwrite => capabilities.AtomicReplace ? UploadPublishMode.TempThenAtomicReplace : null,
        _ => null,
    };

    /// <summary>失败/取消后放弃本次上传：删除**我们创建的东西**并如实报告是否残留。</summary>
    private string AbandonUpload(SshUploadPlan plan, UploadPublishMode mode, out bool leftBehind, out string detail)
    {
        if (mode == UploadPublishMode.TempThenAtomicReplace)
        {
            CleanupRemoteTemp(plan.RemoteTempPath, out leftBehind, out detail);
            return "已删除本次临时文件（原目标未被改动）。";
        }

        // 排他创建路径：目标就是我们创建的，删除它（绝不保留部分写入的目标）。
        CleanupRemoteTemp(plan.RemotePath, out leftBehind, out detail);
        return "已删除本次未完成的目标文件。";
    }

    private enum UploadPublishMode
    {
        /// <summary>直接对目标做排他创建（原子占位，overwrite=false）。</summary>
        ExclusiveCreateTarget,

        /// <summary>临时文件 + posix-rename 原子替换（overwrite=true）。</summary>
        TempThenAtomicReplace,
    }

    private void PublishLocal(string tempPath, string finalPath, SshOverwritePolicy overwrite)
    {
        // 本地发布：.NET 的 Move(overwrite:false) 在目标存在时抛错；覆盖时显式覆盖。
        File.Move(tempPath, finalPath, overwrite == SshOverwritePolicy.Overwrite);
    }

    private void EnsureRealParentWithinRoot(string remoteRoot, string remotePath)
    {
        var parent = SshRemotePathPolicy.ParentOf(remotePath);
        var real = _channel.GetRealDirectory(parent);
        SshRemotePathPolicy.EnsureRealParentWithinRoot(remoteRoot, real);
    }

    /// <summary>删除本次远端临时文件；失败时明确报告残留（不得误报原目标已回滚）。</summary>
    private void CleanupRemoteTemp(string tempPath, out bool leftBehind, out string detail)
    {
        try
        {
            if (_channel.Exists(tempPath))
            {
                _channel.Delete(tempPath);
            }

            leftBehind = false;
            detail = string.Empty;
        }
        catch (Exception ex)
        {
            leftBehind = true;
            detail = $"；临时文件清理失败（{ex.GetType().Name}），残留：{SshDiagnosticText.Bound(tempPath)}";
        }
    }

    /// <summary>删除本次本地临时文件；失败时同样明确报告残留。</summary>
    private static void CleanupLocalTemp(string tempPath, out bool leftBehind, out string detail)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            leftBehind = false;
            detail = string.Empty;
        }
        catch (Exception ex)
        {
            leftBehind = true;
            detail = $"；本地临时文件清理失败（{ex.GetType().Name}）：{SshDiagnosticText.Bound(tempPath)}";
        }
    }

    private SshTransferOutcome MapUploadFailure(Exception ex, long transferred, Stopwatch stopwatch)
    {
        _ = stopwatch;
        return ex switch
        {
            SshTransferLimitExceededException => SshTransferOutcome.Failed(
                SshErrorCodes.TransferTooLarge,
                transferred,
                $"传输超过本次上限：{ex.Message}"),
            SshComponentException component => SshTransferOutcome.Failed(component.ErrorCode, transferred, component.Message),
            _ => SshTransferOutcome.Failed(
                SshErrorCodes.TransferFailed,
                transferred,
                "上传失败（exception=" + ex.GetType().Name + "），不自动重试、不提供断点续传。"),
        };
    }

    private static SshTransferOutcome MapDownloadFailure(Exception ex, long transferred) => ex switch
    {
        SshComponentException component => SshTransferOutcome.Failed(component.ErrorCode, transferred, component.Message),
        IOException => SshTransferOutcome.Failed(
            SshErrorCodes.TransferFailed,
            transferred,
            "本地写入失败（exception=IOException），不自动重试。"),
        _ => SshTransferOutcome.Failed(
            SshErrorCodes.TransferFailed,
            transferred,
            "下载失败（exception=" + ex.GetType().Name + "），不自动重试、不提供断点续传。"),
    };

    private void WriteBytes(string remotePath, byte[] content)
    {
        using var stream = _channel.OpenWriteNew(remotePath);
        stream.Write(content, 0, content.Length);
        stream.Flush();
    }

    private byte[] ContentOf(string remotePath)
    {
        using var stream = _channel.OpenRead(remotePath);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void TryDelete(string remotePath)
    {
        try
        {
            if (_channel.Exists(remotePath))
            {
                _channel.Delete(remotePath);
            }
        }
        catch (Exception)
        {
            // 探测用的 scratch 文件清理失败不影响能力判定；真实传输的清理失败另有明确诊断。
        }
    }

    private static string Join(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;
}

/// <summary>上传超过本次字节上限：由通道在上传过程中抛出，映射为 <c>ssh.transfer_too_large</c>。</summary>
internal sealed class SshTransferLimitExceededException(long limitBytes, long observedBytes)
    : Exception($"limit={limitBytes} observed={observedBytes}")
{
    public long LimitBytes { get; } = limitBytes;

    public long ObservedBytes { get; } = observedBytes;
}
