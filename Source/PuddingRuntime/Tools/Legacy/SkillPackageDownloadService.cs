using System.Formats.Tar;
using System.IO.Compression;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Skill 包下载与解压服务。
/// 将 Skill 包下载并解压到宿主机上的固定目录，供上下文组装和工具执行读取。
/// </summary>
public sealed class SkillPackageDownloadService(
    IHttpClientFactory httpClientFactory,
    ILogger<SkillPackageDownloadService> logger)
{
    // 宿主机上的 Skill 包存储根目录。
    private const string SkillsRootDir = "/pudding-skills";

    /// <summary>
    /// 确保列表中的所有 Skill 包已下载并解压到 <c>/pudding-skills/&lt;skillPackageId&gt;/</c>。
    /// 若目录已存在则跳过（幂等）。
    /// </summary>
    public async Task EnsureDownloadedAsync(
        IReadOnlyList<SkillPackageInfo> packages,
        CancellationToken ct = default)
    {
        foreach (var pkg in packages)
        {
            var destDir = Path.Combine(SkillsRootDir, pkg.SkillPackageId);
            if (Directory.Exists(destDir) && Directory.EnumerateFileSystemEntries(destDir).Any())
            {
                logger.LogDebug("[SkillDL] Skip (already extracted): {Pkg} → {Dir}", pkg.SkillPackageId, destDir);
                continue;
            }

            logger.LogInformation("[SkillDL] Downloading {Pkg} v{Ver}", pkg.SkillPackageId, pkg.Version);

            try
            {
                await DownloadAndExtractAsync(pkg, destDir, ct);
                logger.LogInformation("[SkillDL] Done {Pkg} → {Dir}", pkg.SkillPackageId, destDir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SkillDL] Failed to download/extract {Pkg}", pkg.SkillPackageId);
                // 下载失败时清理半成品目录，避免下次被误认为已完成
                try { Directory.Delete(destDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private async Task DownloadAndExtractAsync(SkillPackageInfo pkg, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        var tmpFile = Path.Combine(Path.GetTempPath(), $"skill_{pkg.SkillPackageId}_{Guid.NewGuid():N}");

        try
        {
            // 1. 下载到临时文件
            var http = httpClientFactory.CreateClient("SkillPackageDL");
            using var resp = await http.GetAsync(pkg.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var fs = File.Create(tmpFile))
            await using (var body = await resp.Content.ReadAsStreamAsync(ct))
                await body.CopyToAsync(fs, ct);

            logger.LogDebug("[SkillDL] Downloaded {Pkg} ({Bytes} bytes)", pkg.SkillPackageId, new FileInfo(tmpFile).Length);

            // 2. 解压
            var lower = pkg.SkillPackageId + "_" + Path.GetExtension(tmpFile);  // 只用于判断格式
            // 以 DownloadUrl 末段文件名判断格式
            var originalName = Uri.TryCreate(pkg.DownloadUrl, UriKind.Absolute, out var uri)
                ? Path.GetFileName(uri.LocalPath).ToLowerInvariant()
                : string.Empty;

            if (originalName.EndsWith(".zip"))
                ZipFile.ExtractToDirectory(tmpFile, destDir, overwriteFiles: true);
            else if (originalName.EndsWith(".tar.gz") || originalName.EndsWith(".tgz"))
                await ExtractTarGzAsync(tmpFile, destDir, ct);
            else
            {
                // 兜底：尝试 zip，否则 tar.gz
                try { ZipFile.ExtractToDirectory(tmpFile, destDir, overwriteFiles: true); }
                catch { await ExtractTarGzAsync(tmpFile, destDir, ct); }
            }
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* ignore */ }
        }
    }

    private static async Task ExtractTarGzAsync(string tarGzPath, string destDir, CancellationToken ct)
    {
        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        // System.Formats.Tar 是 .NET 7+ 内置 API
        await TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true, cancellationToken: ct);
    }
}
