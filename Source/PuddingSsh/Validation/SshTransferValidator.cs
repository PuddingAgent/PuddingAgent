using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>
/// 传输请求校验（切片 B §11.1）：限额先校验、请求 0/负数一律拒绝、路径按**分别建模**的本地/远端策略解析，
/// 并给出「同目录唯一临时文件 + 发布」的计划。审批看到的必须是这份计划里的精确路径。
/// </summary>
public static class SshTransferValidator
{
    /// <summary>校验上传请求并给出上传计划。</summary>
    public static SshUploadPlan ValidateUpload(
        SshUploadRequest request,
        SshTransferLimits limits,
        ISshLocalFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateLimitsAndRequest(limits, request.MaxBytes);
        SshRequestValidator.ValidateTarget(request.Target);

        var maxBytes = limits.ResolveMaxBytes(request.MaxBytes);
        var localPath = SshLocalPathPolicy.ResolveUploadSource(limits.LocalRoot, request.LocalPath, maxBytes, fileSystem);

        var uploadRoot = SshRemotePathPolicy.NormalizeRoot(limits.UploadRoot, "transfer.upload_root");
        var remotePath = SshRemotePathPolicy.Normalize(request.RemotePath);
        SshRemotePathPolicy.EnsureWithinRoot(uploadRoot, remotePath);

        var tempPath = SshRemotePathPolicy.TemporarySiblingOf(remotePath, Guid.NewGuid().ToString("N")[..12]);
        return new SshUploadPlan(localPath, remotePath, tempPath, uploadRoot, request.Overwrite, maxBytes);
    }

    /// <summary>校验下载请求并给出下载计划。</summary>
    public static SshDownloadPlan ValidateDownload(
        SshDownloadRequest request,
        SshTransferLimits limits,
        ISshLocalFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateLimitsAndRequest(limits, request.MaxBytes);
        SshRequestValidator.ValidateTarget(request.Target);

        var maxBytes = limits.ResolveMaxBytes(request.MaxBytes);
        var downloadRoot = SshRemotePathPolicy.NormalizeRoot(limits.DownloadRoot, "transfer.download_root");
        var remotePath = SshRemotePathPolicy.Normalize(request.RemotePath);
        SshRemotePathPolicy.EnsureWithinRoot(downloadRoot, remotePath);

        var localPath = SshLocalPathPolicy.ResolveDownloadTarget(limits.LocalRoot, request.LocalPath, fileSystem);
        var tempPath = TemporaryLocalSiblingOf(localPath);

        return new SshDownloadPlan(remotePath, localPath, tempPath, downloadRoot, request.Overwrite, maxBytes);
    }

    /// <summary>本地目标的同目录唯一临时文件。</summary>
    public static string TemporaryLocalSiblingOf(string localPath)
    {
        var directory = System.IO.Path.GetDirectoryName(localPath) ?? ".";
        var name = System.IO.Path.GetFileName(localPath);
        return System.IO.Path.Combine(directory, "." + name + ".pudding-tmp-" + Guid.NewGuid().ToString("N")[..12]);
    }

    private static void ValidateLimitsAndRequest(SshTransferLimits limits, long? requestedBytes)
    {
        limits.Validate();

        if (requestedBytes is { } requested && requested <= 0)
        {
            throw new SshComponentException(
                SshErrorCodes.RequestInvalid,
                "max_bytes 必须是正整数；要收窄只能传更小的合法值。");
        }

        if (requestedBytes is { } tooLarge && tooLarge > SshTransferLimits.HardMaxBytes)
        {
            throw new SshComponentException(
                SshErrorCodes.TransferTooLarge,
                $"max_bytes={tooLarge} 超过硬上限 {SshTransferLimits.HardMaxBytes}（配置只能在其内收窄）。");
        }
    }
}
