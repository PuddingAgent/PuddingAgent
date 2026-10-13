using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// B1 门禁（设计 §11.1）：传输契约、分别建模的本地/远端路径策略与字节限额。
/// 纯单元矩阵，不连服务器；真实 SFTP 行为由 B2 的会话测试与 B4 的探针覆盖。
/// </summary>
[TestClass]
public sealed class SshTransferValidationTests
{
    private static SshTransferLimits Limits(
        string localRoot = "/workspace",
        string uploadRoot = "/srv/upload",
        string downloadRoot = "/srv/download",
        long maxBytes = SshTransferLimits.DefaultMaxBytes) => new()
        {
            LocalRoot = localRoot,
            UploadRoot = uploadRoot,
            DownloadRoot = downloadRoot,
            MaxBytes = maxBytes,
        };

    private static SshUploadRequest Upload(string local, string remote, SshOverwritePolicy overwrite = SshOverwritePolicy.Fail, long? maxBytes = null) =>
        new(FakeSsh.Target(), local, remote, overwrite, maxBytes);

    private static SshDownloadRequest Download(string remote, string local, SshOverwritePolicy overwrite = SshOverwritePolicy.Fail, long? maxBytes = null) =>
        new(FakeSsh.Target(), remote, local, overwrite, maxBytes);

    // ---------- 限额 ----------

    [TestMethod]
    public void TransferLimits_DefaultAndHardCaps_AreTheDesignValues()
    {
        // 行为化钉住（避免恒真断言）：默认上限 = 64 MiB；配置到硬上限时请求 1 GiB 被夹到 256 MiB。
        Assert.AreEqual(64L * 1024 * 1024, Limits().ResolveMaxBytes(null));

        var atHardCap = Limits(maxBytes: 256L * 1024 * 1024);
        Assert.AreEqual(256L * 1024 * 1024, atHardCap.ResolveMaxBytes(1024L * 1024 * 1024));
        atHardCap.Validate();

        var limits = Limits();
        limits.Validate();
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(256L * 1024 * 1024 + 1)]
    public void TransferLimits_OutOfRange_AreRejectedWithConfigInvalid(long maxBytes)
    {
        var error = Assert.ThrowsExactly<SshComponentException>(() => Limits(maxBytes: maxBytes).Validate());

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
    }

    [TestMethod]
    [DataRow("", "transfer.upload_root")]
    [DataRow("relative/path", "transfer.upload_root")]
    public void TransferLimits_InvalidRemoteRoot_IsRejected(string uploadRoot, string field)
    {
        var error = Assert.ThrowsExactly<SshComponentException>(() => Limits(uploadRoot: uploadRoot).Validate());

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, field);
    }

    [TestMethod]
    public void TransferLimits_FileSystemRoot_IsNotAValidBoundary()
    {
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshRemotePathPolicy.NormalizeRoot("/", "transfer.upload_root"));

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
    }

    [TestMethod]
    public void TransferLimits_LocalRootMustBeAbsolute()
    {
        var error = Assert.ThrowsExactly<SshComponentException>(() => Limits(localRoot: "workspace").Validate());

        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "transfer.local_root");
    }

    [TestMethod]
    public void RequestBytes_AboveHardCap_AreRejected_AndNarrowingIsHonoured()
    {
        var limits = Limits(maxBytes: 8 * 1024 * 1024);

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshTransferValidator.ValidateDownload(
                Download("/srv/download/a.bin", "out.bin", maxBytes: SshTransferLimits.HardMaxBytes + 1),
                limits,
                new FakeLocalFileSystem()));
        Assert.AreEqual(SshErrorCodes.TransferTooLarge, error.ErrorCode);

        // 请求收窄有效：请求 1 MiB < 配置 8 MiB。
        var plan = SshTransferValidator.ValidateDownload(
            Download("/srv/download/a.bin", "out.bin", maxBytes: 1024 * 1024),
            Limits(),
            new FakeLocalFileSystem().Root("/workspace"));
        Assert.AreEqual(1024 * 1024L, plan.MaxBytes);
    }

    [TestMethod]
    public void RequestBytes_NonPositive_IsRequestInvalid()
    {
        foreach (var value in new long[] { 0, -5 })
        {
            var error = Assert.ThrowsExactly<SshComponentException>(
                () => SshTransferValidator.ValidateDownload(Download("/srv/download/a.bin", "out.bin", maxBytes: value), Limits(), new FakeLocalFileSystem()));
            Assert.AreEqual(SshErrorCodes.RequestInvalid, error.ErrorCode);
        }
    }

    // ---------- 远端路径策略（POSIX） ----------

    [TestMethod]
    [DataRow("relative/a.txt", "not_absolute")]
    [DataRow("/srv/upload/a\0b", "control_chars")]
    [DataRow("/srv/upload/dir/", "directory_marker")]
    [DataRow("/srv/download/../etc/passwd", "outside_root")]
    [DataRow("/", "directory_marker")]
    [DataRow("", "empty")]
    [DataRow("/../etc/passwd", "escapes_root")]
    public void RemotePath_InvalidForms_AreRejected(string path, string expectedReason)
    {
        var root = path.StartsWith("/srv/download") ? "/srv/download" : "/srv/upload";
        var error = Assert.ThrowsExactly<SshComponentException>(() =>
        {
            var normalized = SshRemotePathPolicy.Normalize(path);
            SshRemotePathPolicy.EnsureWithinRoot(root, normalized);
        });

        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, $"reason={expectedReason}");
    }

    [TestMethod]
    public void RemotePath_OutsideRoot_IsRejected_AndInsideIsNormalized()
    {
        var error = Assert.ThrowsExactly<SshComponentException>(() =>
            SshRemotePathPolicy.EnsureWithinRoot("/srv/upload", SshRemotePathPolicy.Normalize("/srv/uploads/a.txt")));
        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=outside_root");

        // 词法消解：/srv/upload/./sub/../a.txt → /srv/upload/a.txt
        var normalized = SshRemotePathPolicy.Normalize("/srv/upload/./sub/../a.txt");
        Assert.AreEqual("/srv/upload/a.txt", normalized);
        SshRemotePathPolicy.EnsureWithinRoot("/srv/upload", normalized);
    }

    [TestMethod]
    public void RemotePath_SymlinkEscape_IsRejectedByRealParentCheck()
    {
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshRemotePathPolicy.EnsureRealParentWithinRoot("/srv/upload", "/srv/elsewhere"));

        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=symlink_escape");

        SshRemotePathPolicy.EnsureRealParentWithinRoot("/srv/upload", "/srv/upload/sub");
        SshRemotePathPolicy.EnsureRealParentWithinRoot("/srv/upload", "/srv/upload");
    }

    [TestMethod]
    public void RemotePath_TemporarySibling_IsInTheSameDirectoryAndHidden()
    {
        var temp = SshRemotePathPolicy.TemporarySiblingOf("/srv/upload/sub/a.txt", "abc123");

        Assert.AreEqual("/srv/upload/sub/.a.txt.pudding-tmp-abc123", temp);
        Assert.AreEqual("/srv/upload/sub", SshRemotePathPolicy.ParentOf(temp));
    }

    [TestMethod]
    public void LocalPath_NestedInsideRoot_IsAccepted()
    {
        // 正向对照：根内嵌套路径必须放行（否则一个"全部拒绝"的实现也能骗过反向边界断言）。
        var fs = new FakeLocalFileSystem().Root("/workspace").Directory("/workspace/sub").File("/workspace/sub/a.txt", 3);

        var resolved = SshLocalPathPolicy.ResolveUploadSource("/workspace", "sub/a.txt", 1024, fs);

        Assert.AreEqual("/workspace/sub/a.txt", resolved);
    }

    [TestMethod]
    public void LocalPath_PrefixSibling_IsNotInsideRoot()
    {
        // 反向边界：/workspace-evil 只是**字符串前缀**相同，不是根内路径。
        // 朴素 StartsWith 实现会放进来的东西，必须被 GetRelativePath 包含判定拒绝。
        var fs = new FakeLocalFileSystem().Root("/workspace").Root("/workspace-evil").File("/workspace-evil/secret.txt");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveUploadSource("/workspace", "/workspace-evil/secret.txt", 1024, fs));

        Assert.AreEqual(SshErrorCodes.LocalPathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=outside_root");
    }

    // ---------- 本地路径策略 ----------

    [TestMethod]
    public void LocalUploadSource_OutsideRoot_IsRejected()
    {
        var fs = new FakeLocalFileSystem().Root("/workspace").File("/workspace/ok.txt");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveUploadSource("/workspace", "../escape.txt", 1024, fs));

        Assert.AreEqual(SshErrorCodes.LocalPathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=outside_root");
    }

    [TestMethod]
    public void LocalUploadSource_ReparsePointParent_IsRejected()
    {
        // 字符串上仍在根内，但父目录是重解析点（例如 junction/符号链接）⇒ 必须拒绝。
        var fs = new FakeLocalFileSystem().Root("/workspace").ReparseDirectory("/workspace/link").File("/workspace/link/secret.txt");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveUploadSource("/workspace", "link/secret.txt", 1024, fs));

        Assert.AreEqual(SshErrorCodes.LocalPathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=reparse_point");
    }

    [TestMethod]
    public void LocalUploadSource_DirectoryAndDeviceTargets_AreRejected()
    {
        var directory = new FakeLocalFileSystem().Root("/workspace").Directory("/workspace/dir");
        var dirError = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveUploadSource("/workspace", "dir", 1024, directory));
        StringAssert.Contains(dirError.Message, "reason=directory");

        var device = new FakeLocalFileSystem().Root("/workspace").NonRegular("/workspace/dev0");
        var deviceError = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveUploadSource("/workspace", "dev0", 1024, device));
        StringAssert.Contains(deviceError.Message, "reason=not_regular_file");
    }

    [TestMethod]
    public void LocalUploadSource_LargerThanBudget_IsTransferTooLarge()
    {
        var fs = new FakeLocalFileSystem().Root("/workspace").File("/workspace/big.bin", 4096);

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveUploadSource("/workspace", "big.bin", 1024, fs));

        Assert.AreEqual(SshErrorCodes.TransferTooLarge, error.ErrorCode);
    }

    [TestMethod]
    public void LocalDownloadTarget_MissingParent_IsRejected()
    {
        var fs = new FakeLocalFileSystem().Root("/workspace");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveDownloadTarget("/workspace", "sub/out.bin", fs));

        Assert.AreEqual(SshErrorCodes.LocalPathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=parent_missing");
    }

    [TestMethod]
    public void LocalDownloadTarget_ExistingDirectory_IsRejected()
    {
        var fs = new FakeLocalFileSystem().Root("/workspace").Directory("/workspace/dir");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshLocalPathPolicy.ResolveDownloadTarget("/workspace", "dir", fs));

        StringAssert.Contains(error.Message, "reason=directory");
    }

    // ---------- 计划 ----------

    [TestMethod]
    public void UploadPlan_ContainsExactPathsAndTempSibling()
    {
        var fs = new FakeLocalFileSystem().Root("/workspace").File("/workspace/src.bin", 10);

        var plan = SshTransferValidator.ValidateUpload(Upload("src.bin", "/srv/upload/dest.bin"), Limits(), fs);

        Assert.AreEqual("/workspace/src.bin", plan.LocalPath.Replace('\\', '/'));
        Assert.AreEqual("/srv/upload/dest.bin", plan.RemotePath);
        Assert.AreEqual("/srv/upload", SshRemotePathPolicy.ParentOf(plan.RemoteTempPath));
        StringAssert.Contains(SshRemotePathPolicy.FileNameOf(plan.RemoteTempPath), ".dest.bin.pudding-tmp-");
        Assert.AreEqual(SshOverwritePolicy.Fail, plan.Overwrite);
    }

    [TestMethod]
    public void DownloadPlan_ContainsExactPathsAndLocalTempSibling()
    {
        var fs = new FakeLocalFileSystem().Root("/workspace");

        var plan = SshTransferValidator.ValidateDownload(Download("/srv/download/src.bin", "dest.bin"), Limits(), fs);

        Assert.AreEqual("/srv/download/src.bin", plan.RemotePath);
        Assert.AreEqual(SshRemotePathPolicy.ParentOf(plan.LocalTempPath.Replace('\\', '/')), System.IO.Path.GetDirectoryName(plan.LocalPath)!.Replace('\\', '/'));
        StringAssert.Contains(System.IO.Path.GetFileName(plan.LocalTempPath), ".dest.bin.pudding-tmp-");
    }

    [TestMethod]
    public void UploadRequest_WriteRootAndReadRoot_AreSeparate()
    {
        // 上传根之外的路径即使落在下载根内也必须拒绝。
        var fs = new FakeLocalFileSystem().Root("/workspace").File("/workspace/src.bin");

        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshTransferValidator.ValidateUpload(Upload("src.bin", "/srv/download/a.bin"), Limits(), fs));

        Assert.AreEqual(SshErrorCodes.RemotePathInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "reason=outside_root");
    }
}
