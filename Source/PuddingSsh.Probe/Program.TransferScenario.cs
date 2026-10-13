using System.Diagnostics;
using System.Security.Cryptography;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Execution;
using PuddingSsh.Transport;
using PuddingSsh.Validation;

namespace PuddingSsh.Probe;

/// <summary>
/// 切片 B 的真实 OpenSSH 传输场景：往返字节一致、覆盖策略、超限中止、路径拒绝、取消收敛、
/// 临时文件不残留，并落盘**实测能力**。服务端证据由运行器另行出带核对（sha256sum + 目录清点）。
/// </summary>
internal static partial class Program
{
    private static async Task RunTransferAsync(ProbeOptions options, ProbeLog log)
    {
        const string scenario = "transfer";
        var factory = new SshNetTransportFactory();
        var identities = new ProbeIdentityProvider();
        var uploadRoot = string.IsNullOrWhiteSpace(options.UploadRoot)
            ? options.RemoteTempDirectory.TrimEnd('/') + "/transfer/upload"
            : options.UploadRoot;
        var downloadRoot = string.IsNullOrWhiteSpace(options.DownloadRoot)
            ? options.RemoteTempDirectory.TrimEnd('/') + "/transfer/download"
            : options.DownloadRoot;
        var localRoot = Path.Combine(Path.GetTempPath(), "pudding-transfer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localRoot);

        var connections = 0;
        var operations = 0;

        try
        {
            // 0) 远端目录准备（由探针自己创建允许根；权限沿用登录账号）。
            connections++;
            await using (var prep = await factory.ConnectAsync(
                ConnectRequest(options, new ProbeHostKeyVerifier(options.ExpectedFingerprint)),
                CancellationToken.None))
            {
                operations++;
                var prepared = await prep.ExecuteAsync(
                    $"mkdir -p {uploadRoot} {downloadRoot}; rm -f {uploadRoot}/* {downloadRoot}/*; echo PREP_OK",
                    TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                    options.OutputBudgetBytes,
                    CancellationToken.None);
                log.Assert(scenario, "prepared", prepared.Stdout.Contains("PREP_OK", StringComparison.Ordinal), prepared.Stdout.Trim());
            }

            // 1) 本地样本文件（1 MiB 伪随机，便于字节比对）。
            var payload = new byte[1024 * 1024];
            RandomNumberGenerator.Fill(payload);
            var sourcePath = Path.Combine(localRoot, "payload.bin");
            await File.WriteAllBytesAsync(sourcePath, payload);
            var expectedSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            var limits = new SshTransferLimits
            {
                LocalRoot = localRoot,
                UploadRoot = uploadRoot,
                DownloadRoot = downloadRoot,
                MaxBytes = 8L * 1024 * 1024,
            };

            await using var coordinator = new SshOperationCoordinator(factory, identities);

            // 2) 能力实测（通过协调器连接真正的 SFTP 通道）。
            var targetRemote = uploadRoot.TrimEnd('/') + "/payload.bin";
            var upload = await coordinator.UploadAsync(
                options.HostFor(transfer: limits),
                new SshUploadRequest(options.Target, "payload.bin", targetRemote),
                CancellationToken.None);
            connections++;
            operations++;

            log.Fact(scenario, "capabilities", upload.Capabilities is { } caps
                ? $"atomic_replace={caps.AtomicReplace} rename_no_overwrite={caps.RenameNoOverwrite} exclusive_create={caps.ExclusiveCreate}"
                : "unknown");
            log.Fact(scenario, "upload_status", upload.Status.ToString());
            log.Fact(scenario, "upload_phase", upload.Phase.ToString());
            log.Fact(scenario, "upload_diagnostic", upload.Diagnostic ?? "null");
            log.Fact(scenario, "upload_error", upload.ErrorCode ?? "null");
            log.Fact(scenario, "upload_bytes", upload.BytesTransferred);
            log.Fact(scenario, "upload_sha256_local", expectedSha);
            log.Fact(scenario, "upload_cleanup_state", upload.CleanupState.ToString());

            log.Assert(scenario, "upload_completed", upload.Status == SshExecutionStatus.Completed, upload.ErrorCode ?? upload.Status.ToString());
            log.Assert(scenario, "upload_bytes_exact", upload.BytesTransferred == payload.Length, $"{upload.BytesTransferred}/{payload.Length}");
            log.Assert(scenario, "upload_cleanup_completed", upload.CleanupState == SshCleanupState.Completed, upload.CleanupState.ToString());
            log.Assert(
                scenario,
                "capabilities_measured",
                upload.Capabilities is { ExclusiveCreate: true },
                upload.Capabilities?.ToString() ?? "unknown");

            // 3) 默认不覆盖：目标已存在必须被拒绝，且远端内容不变（服务端 sha 由运行器核对）。
            var second = await coordinator.UploadAsync(
                options.HostFor(transfer: limits),
                new SshUploadRequest(options.Target, "payload.bin", targetRemote),
                CancellationToken.None);
            connections++;
            operations++;
            log.Fact(scenario, "no_overwrite_error", second.ErrorCode ?? "null");
            log.Assert(
                scenario,
                "no_overwrite_rejected",
                second.ErrorCode == SshErrorCodes.RemotePathExists,
                second.ErrorCode ?? second.Status.ToString());

            // 4) 显式覆盖：替换成功（新内容 = 另一份样本）。
            var replacement = new byte[256 * 1024];
            RandomNumberGenerator.Fill(replacement);
            var replacementPath = Path.Combine(localRoot, "replacement.bin");
            await File.WriteAllBytesAsync(replacementPath, replacement);
            var replacementSha = Convert.ToHexString(SHA256.HashData(replacement)).ToLowerInvariant();

            var overwrite = await coordinator.UploadAsync(
                options.HostFor(transfer: limits),
                new SshUploadRequest(options.Target, "replacement.bin", targetRemote, SshOverwritePolicy.Overwrite),
                CancellationToken.None);
            connections++;
            operations++;
            log.Fact(scenario, "overwrite_phase", overwrite.Phase.ToString());
            log.Fact(scenario, "overwrite_diagnostic", overwrite.Diagnostic ?? "null");
            log.Fact(scenario, "overwrite_status", overwrite.Status.ToString());
            log.Fact(scenario, "overwrite_error", overwrite.ErrorCode ?? "null");
            log.Fact(scenario, "overwrite_sha256_local", replacementSha);
            log.Assert(scenario, "overwrite_completed", overwrite.Status == SshExecutionStatus.Completed, overwrite.ErrorCode ?? "null");

            // 5) 超限中止：请求上限小于文件 ⇒ transfer_too_large 且不留下目标。
            var tooLargeTarget = uploadRoot.TrimEnd('/') + "/too-large.bin";
            var tooLarge = await coordinator.UploadAsync(
                options.HostFor(transfer: limits),
                new SshUploadRequest(options.Target, "payload.bin", tooLargeTarget, SshOverwritePolicy.Fail, MaxBytes: 64 * 1024),
                CancellationToken.None);
            connections++;
            operations++;
            log.Fact(scenario, "too_large_error", tooLarge.ErrorCode ?? "null");
            log.Fact(scenario, "too_large_left_behind", tooLarge.TemporaryLeftBehind);
            log.Assert(scenario, "too_large_rejected", tooLarge.ErrorCode == SshErrorCodes.TransferTooLarge, tooLarge.ErrorCode ?? "null");
            log.Assert(scenario, "too_large_no_leftover", !tooLarge.TemporaryLeftBehind, $"left_behind={tooLarge.TemporaryLeftBehind}");

            // 6) 路径拒绝：远端越出允许根、本地越出工作区根，都必须零副作用。
            var outsideRemote = await coordinator.UploadAsync(
                options.HostFor(transfer: limits),
                new SshUploadRequest(options.Target, "payload.bin", "/etc/pudding-should-not-exist.bin"),
                CancellationToken.None);
            operations++;
            log.Fact(scenario, "remote_outside_root_error", outsideRemote.ErrorCode ?? "null");
            log.Assert(
                scenario,
                "remote_outside_root_rejected",
                outsideRemote.ErrorCode == SshErrorCodes.RemotePathInvalid,
                outsideRemote.ErrorCode ?? "null");

            var outsideLocal = await coordinator.UploadAsync(
                options.HostFor(transfer: limits),
                new SshUploadRequest(options.Target, "../escape.bin", uploadRoot.TrimEnd('/') + "/escape.bin"),
                CancellationToken.None);
            operations++;
            log.Fact(scenario, "local_outside_root_error", outsideLocal.ErrorCode ?? "null");
            log.Assert(
                scenario,
                "local_outside_root_rejected",
                outsideLocal.ErrorCode == SshErrorCodes.LocalPathInvalid,
                outsideLocal.ErrorCode ?? "null");

            // 7) 下载往返：字节完全一致。
            // 单独的下载限额：本用例验证**同一文件**的往返一致性，因此下载根指向上传根；
            // 配置里"上传/下载根分离"由下面的跨根拒绝断言单独验证。
            var roundTripLimits = limits with { DownloadRoot = uploadRoot };
            var downloadLocal = Path.Combine(localRoot, "roundtrip.bin");
            var download = await coordinator.DownloadAsync(
                options.HostFor(transfer: roundTripLimits),
                new SshDownloadRequest(options.Target, targetRemote, downloadLocal),
                CancellationToken.None);
            connections++;
            operations++;

            var downloaded = File.Exists(downloadLocal) ? await File.ReadAllBytesAsync(downloadLocal) : [];
            var downloadedSha = downloaded.Length > 0 ? Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant() : "missing";
            log.Fact(scenario, "download_status", download.Status.ToString());
            log.Fact(scenario, "download_error", download.ErrorCode ?? "null");
            log.Fact(scenario, "download_bytes", download.BytesTransferred);
            log.Fact(scenario, "download_sha256_local", downloadedSha);
            log.Assert(scenario, "download_completed", download.Status == SshExecutionStatus.Completed, download.ErrorCode ?? "null");
            log.Assert(
                scenario,
                "roundtrip_bytes_equal",
                downloadedSha == replacementSha && download.BytesTransferred == replacement.Length,
                $"expected={replacementSha} actual={downloadedSha}");

            // 7b) 跨根拒绝：允许上传根里的文件，在配置的下载根不同的情况下必须被拒绝（零副作用）。
            var crossRoot = await coordinator.DownloadAsync(
                options.HostFor(transfer: limits),
                new SshDownloadRequest(options.Target, targetRemote, Path.Combine(localRoot, "cross-root.bin")),
                CancellationToken.None);
            operations++;
            log.Fact(scenario, "cross_root_error", crossRoot.ErrorCode ?? "null");
            log.Assert(
                scenario,
                "cross_root_rejected",
                crossRoot.ErrorCode == SshErrorCodes.RemotePathInvalid,
                crossRoot.ErrorCode ?? crossRoot.Status.ToString());

            // 8) 取消收敛：大文件 + 极短宿主 deadline ⇒ 本端终止，且不留临时/部分目标。
            var bigPath = Path.Combine(localRoot, "big.bin");
            var bigPayload = new byte[128 * 1024 * 1024];
            await File.WriteAllBytesAsync(bigPath, bigPayload);
            var bigSha = Convert.ToHexString(SHA256.HashData(bigPayload)).ToLowerInvariant();
            var cancelled = await coordinator.UploadAsync(
                options.HostFor(
                    transfer: new SshTransferLimits
                    {
                        LocalRoot = localRoot,
                        UploadRoot = uploadRoot,
                        DownloadRoot = downloadRoot,
                        MaxBytes = 256L * 1024 * 1024,
                    },
                    deadline: DateTimeOffset.UtcNow.AddMilliseconds(250)),
                new SshUploadRequest(options.Target, "big.bin", uploadRoot.TrimEnd('/') + "/big.bin"),
                CancellationToken.None);
            connections++;
            operations++;
            log.Fact(scenario, "cancel_status", cancelled.Status.ToString());
            log.Fact(scenario, "cancel_error", cancelled.ErrorCode ?? "null");
            log.Fact(scenario, "cancel_cleanup_state", cancelled.CleanupState.ToString());
            log.Fact(scenario, "cancel_left_behind", cancelled.TemporaryLeftBehind);
            log.Fact(scenario, "cancel_big_sha256_local", bigSha);
            log.Fact(scenario, "cancel_big_target", uploadRoot.TrimEnd('/') + "/big.bin");
            log.Assert(
                scenario,
                "cancel_is_termination",
                cancelled.Status is SshExecutionStatus.Cancelled or SshExecutionStatus.TimedOut,
                cancelled.Status.ToString());
            log.Assert(
                scenario,
                "cancel_no_leftover",
                !cancelled.TemporaryLeftBehind,
                $"left_behind={cancelled.TemporaryLeftBehind} path={cancelled.TemporaryPath ?? "null"}");

            log.Fact(scenario, "operations", operations);
            log.Fact(scenario, "connections", connections);
            log.Fact(scenario, "upload_root", uploadRoot);
            log.Fact(scenario, "upload_target", targetRemote);
            log.Fact(scenario, "verdict", "sftp transfer: measured capabilities, byte-exact round trip, no partial target");
        }
        finally
        {
            try
            {
                Directory.Delete(localRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
