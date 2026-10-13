using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Transport;

namespace PuddingSshTests;

/// <summary>
/// B2 门禁（设计 §11.1）：发布语义必须由**实测能力**支撑，不支持即返回不支持；
/// 失败/取消不得留下部分目标；清理失败必须明确报告残留。
/// </summary>
[TestClass]
public sealed class SshTransferSessionTests
{
    private string _workspace = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "pudding-b2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不影响门禁结论。
        }
    }

    private string LocalFile(string name, string content)
    {
        var path = Path.Combine(_workspace, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static SshUploadPlan UploadPlan(string localPath, string remotePath = "/srv/upload/target.bin", SshOverwritePolicy overwrite = SshOverwritePolicy.Fail, long maxBytes = 1024 * 1024) =>
        new(localPath, remotePath, "/srv/upload/.target.bin.pudding-tmp-test", "/srv/upload", overwrite, maxBytes);

    private static SshDownloadPlan DownloadPlan(string remotePath, string localPath, SshOverwritePolicy overwrite = SshOverwritePolicy.Fail, long maxBytes = 1024 * 1024) =>
        new(remotePath, localPath, Path.Combine(Path.GetDirectoryName(localPath)!, "." + Path.GetFileName(localPath) + ".pudding-tmp-test"), "/srv/download", overwrite, maxBytes);

    private static SshSftpTransferSession Session(FakeSftpChannel channel) =>
        new(channel, "SHA256:test", "/srv/download");

    // ---------- 能力实测 ----------

    [TestMethod]
    public async Task Capabilities_AreMeasuredOnceAndCached()
    {
        var channel = new FakeSftpChannel
        {
            SupportsAtomicReplace = true,
            StandardRenameOverwrites = true,
            SupportsExclusiveCreate = true,
        };
        var session = Session(channel);

        var first = await session.GetCapabilitiesAsync(CancellationToken.None);
        var uploadsAfterProbe = channel.UploadCount;
        var second = await session.GetCapabilitiesAsync(CancellationToken.None);

        Assert.IsTrue(first.AtomicReplace, "posix-rename 可用 ⇒ 原子替换支持");
        Assert.IsTrue(first.ExclusiveCreate, "排他创建可用");
        Assert.IsFalse(first.RenameNoOverwrite, "OpenSSH 的标准 RENAME 实测为覆盖 ⇒ 不能宣称不覆盖");
        Assert.AreEqual(first, second);
        Assert.AreEqual(uploadsAfterProbe, channel.UploadCount, "能力只实测一次（探测用的 scratch 不重复创建）");
    }

    [TestMethod]
    public async Task Capabilities_UnsupportedServer_ReportsAllFalse()
    {
        var channel = new FakeSftpChannel
        {
            SupportsAtomicReplace = false,
            StandardRenameOverwrites = true,
            SupportsExclusiveCreate = false,
        };

        var capabilities = await Session(channel).GetCapabilitiesAsync(CancellationToken.None);

        Assert.IsFalse(capabilities.AtomicReplace);
        Assert.IsFalse(capabilities.RenameNoOverwrite);
        Assert.IsFalse(capabilities.ExclusiveCreate);
    }

    // ---------- 上传：overwrite = Fail（排他创建原子占位） ----------

    [TestMethod]
    public async Task Upload_NoOverwrite_ReservesTargetAtomically_WithoutTempFile()
    {
        var channel = new FakeSftpChannel().Seed("/srv/download/keep", "keep");
        var session = Session(channel);
        var local = LocalFile("a.bin", "hello");

        // 先让能力探测发生，之后只比较"发布本身"的调用次数。
        _ = await session.GetCapabilitiesAsync(CancellationToken.None);
        var renamesBefore = channel.PosixRenameCount;

        var outcome = await session.UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, outcome.Status, outcome.ErrorCode ?? "null");
        Assert.AreEqual(5L, outcome.BytesTransferred);
        Assert.AreEqual("hello", channel.ContentOf("/srv/upload/target.bin"));
        Assert.IsNull(channel.ContentOf("/srv/upload/.target.bin.pudding-tmp-test"), "不覆盖路径不得留下临时文件");
        Assert.AreEqual(renamesBefore, channel.PosixRenameCount, "不覆盖发布不依赖 rename");
        Assert.IsFalse(outcome.TemporaryLeftBehind);
    }

    [TestMethod]
    public async Task Upload_NoOverwrite_ExistingTarget_IsRemotePathExists_AndTargetUntouched()
    {
        var channel = new FakeSftpChannel().Seed("/srv/upload/target.bin", "original");
        var local = LocalFile("a.bin", "hello");

        var outcome = await Session(channel).UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.RemotePathExists, outcome.ErrorCode);
        Assert.AreEqual("original", channel.ContentOf("/srv/upload/target.bin"));
        Assert.AreEqual(0, channel.UploadCount, "存在性拒绝发生在写入之前");
    }

    [TestMethod]
    public async Task Upload_NoOverwrite_WithoutExclusiveCreate_IsUnsupported_AndWritesNothing()
    {
        var channel = new FakeSftpChannel { SupportsExclusiveCreate = false };
        var local = LocalFile("a.bin", "hello");

        var outcome = await Session(channel).UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferUnsupported, outcome.ErrorCode);
        StringAssert.Contains(outcome.Diagnostic!, "exclusive_create=False");
        Assert.IsNull(channel.ContentOf("/srv/upload/target.bin"), "不支持时绝不能退化到竞态写入");
        Assert.AreEqual(0, channel.UploadCount);
    }

    // ---------- 上传：overwrite = Overwrite（临时文件 + 原子替换） ----------

    [TestMethod]
    public async Task Upload_Overwrite_UsesTempThenAtomicReplace()
    {
        var channel = new FakeSftpChannel().Seed("/srv/upload/target.bin", "original");
        var session = Session(channel);
        var local = LocalFile("a.bin", "replacement");

        _ = await session.GetCapabilitiesAsync(CancellationToken.None);
        var renamesBefore = channel.PosixRenameCount;

        var outcome = await session.UploadAsync(
            UploadPlan(local, overwrite: SshOverwritePolicy.Overwrite),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, outcome.Status, outcome.ErrorCode ?? "null");
        Assert.AreEqual("replacement", channel.ContentOf("/srv/upload/target.bin"));
        Assert.AreEqual(renamesBefore + 1, channel.PosixRenameCount, "覆盖发布必须走 posix-rename 原子替换");
        Assert.IsNull(channel.ContentOf("/srv/upload/.target.bin.pudding-tmp-test"), "发布后不得残留临时文件");
    }

    [TestMethod]
    public async Task Upload_Overwrite_WithoutAtomicReplace_IsUnsupported_AndLeavesTargetIntact()
    {
        var channel = new FakeSftpChannel { SupportsAtomicReplace = false }.Seed("/srv/upload/target.bin", "original");
        var local = LocalFile("a.bin", "replacement");

        var outcome = await Session(channel).UploadAsync(
            UploadPlan(local, overwrite: SshOverwritePolicy.Overwrite),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferUnsupported, outcome.ErrorCode);
        Assert.AreEqual("original", channel.ContentOf("/srv/upload/target.bin"), "不支持时不得用竞态实现覆盖");
        Assert.AreEqual(0, channel.UploadCount);
    }

    // ---------- 上传：限额、取消、清理失败 ----------

    [TestMethod]
    public async Task Upload_ExceedingLimit_AbortsWithoutPartialTarget()
    {
        var channel = new FakeSftpChannel();
        var local = LocalFile("big.bin", new string('x', 4096));

        var outcome = await Session(channel).UploadAsync(
            UploadPlan(local, maxBytes: 1024),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferTooLarge, outcome.ErrorCode);
        Assert.IsNull(channel.ContentOf("/srv/upload/target.bin"), "超限必须中止且不留下部分目标");
        Assert.IsNull(channel.ContentOf("/srv/upload/.target.bin.pudding-tmp-test"));
    }

    [TestMethod]
    public async Task Upload_Cancelled_DeletesTheReservedTarget()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeSftpChannel { OnBeforeUploadCommit = cts.Cancel };
        var local = LocalFile("a.bin", "hello");

        var outcome = await Session(channel).UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), cts.Token);

        Assert.AreEqual(SshExecutionStatus.Cancelled, outcome.Status);
        Assert.AreEqual(SshErrorCodes.Cancelled, outcome.ErrorCode);
        Assert.IsNull(channel.ContentOf("/srv/upload/target.bin"), "取消后不得留下未完成的目标");
        Assert.IsFalse(outcome.TemporaryLeftBehind);
    }

    [TestMethod]
    public async Task Upload_CleanupFailure_ReportsTemporaryLeftBehind()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeSftpChannel { OnBeforeUploadCommit = cts.Cancel, FailDelete = true }
            // 覆盖发布路径：临时文件已经落地，取消后清理它时服务端拒绝删除。
            .Seed("/srv/upload/.target.bin.pudding-tmp-test", "partial");
        var local = LocalFile("a.bin", "hello");

        var outcome = await Session(channel).UploadAsync(
            UploadPlan(local, overwrite: SshOverwritePolicy.Overwrite),
            TimeSpan.FromSeconds(5),
            cts.Token);

        Assert.AreEqual(SshExecutionStatus.Cancelled, outcome.Status);
        Assert.IsTrue(outcome.TemporaryLeftBehind, "清理失败必须明确报告残留");
        StringAssert.Contains(outcome.Diagnostic!, "残留");
        Assert.AreEqual("/srv/upload/.target.bin.pudding-tmp-test", outcome.TemporaryPath);
    }

    [TestMethod]
    public async Task Upload_ParentRealPathEscapesRoot_IsRejectedBeforeWriting()
    {
        var channel = new FakeSftpChannel { RealPathOverride = "/srv/elsewhere" };
        var local = LocalFile("a.bin", "hello");

        var outcome = await Session(channel).UploadAsync(UploadPlan(local), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, outcome.ErrorCode);
        StringAssert.Contains(outcome.Diagnostic!, "symlink_escape");
        Assert.AreEqual(0, channel.UploadCount, "父目录 realpath 越界时不得开始写入");
    }

    // ---------- 下载 ----------

    [TestMethod]
    public async Task Download_PublishesLocalTempThenMoves()
    {
        var channel = new FakeSftpChannel().Seed("/srv/download/src.bin", "payload");
        var target = Path.Combine(_workspace, "out.bin");

        var outcome = await Session(channel).DownloadAsync(DownloadPlan("/srv/download/src.bin", target), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshExecutionStatus.Completed, outcome.Status, outcome.ErrorCode ?? "null");
        Assert.AreEqual(7L, outcome.BytesTransferred);
        Assert.AreEqual("payload", File.ReadAllText(target));
        Assert.IsFalse(File.Exists(Path.Combine(_workspace, ".out.bin.pudding-tmp-test")), "发布后不得残留本地临时文件");
    }

    [TestMethod]
    public async Task Download_LocalTargetExists_IsLocalPathExists_AndFileUntouched()
    {
        var channel = new FakeSftpChannel().Seed("/srv/download/src.bin", "payload");
        var target = Path.Combine(_workspace, "out.bin");
        File.WriteAllText(target, "existing");

        var outcome = await Session(channel).DownloadAsync(DownloadPlan("/srv/download/src.bin", target), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.LocalPathExists, outcome.ErrorCode);
        Assert.AreEqual("existing", File.ReadAllText(target));
    }

    [TestMethod]
    public async Task Download_RemoteTooLarge_IsTransferTooLarge()
    {
        var channel = new FakeSftpChannel().Seed("/srv/download/src.bin", new string('y', 2048));
        var target = Path.Combine(_workspace, "out.bin");

        var outcome = await Session(channel).DownloadAsync(
            DownloadPlan("/srv/download/src.bin", target, maxBytes: 1024),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.AreEqual(SshErrorCodes.TransferTooLarge, outcome.ErrorCode);
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public async Task Download_RemoteMissingOrDirectory_IsRemotePathInvalid()
    {
        var channel = new FakeSftpChannel();
        var target = Path.Combine(_workspace, "out.bin");

        var missing = await Session(channel).DownloadAsync(DownloadPlan("/srv/download/none.bin", target), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, missing.ErrorCode);

        // 目录（不存在于文件表 ⇒ 非普通文件）同样拒绝。
        var directory = await Session(channel).DownloadAsync(DownloadPlan("/srv/download", target), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, directory.ErrorCode);
    }

    // ---------- 释放 ----------

    [TestMethod]
    public async Task DisposeWithBudget_ClosesChannelAndReleasesOwnedResources()
    {
        var channel = new FakeSftpChannel();
        var released = 0;
        var session = new SshSftpTransferSession(channel, "SHA256:test", "/srv/download", localFileSystem: null, releaseOwnedResources: () => released++);

        await session.DisposeAsync(TimeSpan.FromMilliseconds(50));

        Assert.IsTrue(channel.Disposed);
        Assert.AreEqual(1, released);
    }
}
