using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// Binds the legacy skill-package page to the shared SkillPackageService (the operation the Web
/// controller also uses). Upload and download go through the configured object store; if it is not
/// reachable the real error is surfaced rather than reported as success.
/// </summary>
internal sealed class DesktopSkillPackageSettings(IDesktopKernel kernel) : ISkillPackageSettings
{
    private Task<T> Packages<T>(string operationId,
        Func<SkillPackageService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<SkillPackageService>(), token), cancellationToken);

    public Task<IReadOnlyList<SkillPackageSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Packages("skillPackages.list", async (service, token) =>
        {
            var packages = await service.ListAsync(token);
            return (IReadOnlyList<SkillPackageSummary>)packages.Select(Map).ToArray();
        }, cancellationToken);

    public Task SaveMetaAsync(SkillPackageMetaEdit edit, CancellationToken cancellationToken = default)
        => Packages("skillPackages.meta", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.UpdateMetaAsync(edit.SkillPackageId,
                new UpdateSkillPackageRequest(edit.Name, edit.Description, edit.IsEnabled, edit.SortOrder), token));
            return true;
        }, cancellationToken);

    public Task DeleteAsync(string skillPackageId, CancellationToken cancellationToken = default)
        => Packages("skillPackages.delete", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.DeleteAsync(skillPackageId, token));
            return true;
        }, cancellationToken);

    public Task UploadAsync(SkillPackageUploadEdit upload, CancellationToken cancellationToken = default)
        => Packages("skillPackages.upload", async (service, token) =>
        {
            await using var stream = OpenRead(upload.FilePath);
            DesktopSkillHubSettings.Require(await service.UploadAsync(
                new SkillPackageUpload(upload.SkillPackageId, upload.Name, upload.Description, upload.Version, upload.SortOrder),
                new SkillPackageFile(Path.GetFileName(upload.FilePath), stream.Length, "application/octet-stream", stream), token));
            return true;
        }, cancellationToken);

    public Task ReplaceFileAsync(SkillPackageFileEdit edit, CancellationToken cancellationToken = default)
        => Packages("skillPackages.replaceFile", async (service, token) =>
        {
            await using var stream = OpenRead(edit.FilePath);
            DesktopSkillHubSettings.Require(await service.UpdateFileAsync(edit.SkillPackageId, edit.Version,
                new SkillPackageFile(Path.GetFileName(edit.FilePath), stream.Length, "application/octet-stream", stream), token));
            return true;
        }, cancellationToken);

    public Task<string> GetDownloadUrlAsync(string skillPackageId, CancellationToken cancellationToken = default)
        => Packages("skillPackages.downloadUrl", async (service, token) =>
        {
            var result = await service.GetDownloadUrlAsync(skillPackageId, 86400, token);
            DesktopSkillHubSettings.Require(result);
            return result.Value!;
        }, cancellationToken);

    /// <summary>Opens the chosen file before any Core call, so a bad path fails without side effects.</summary>
    private static FileStream OpenRead(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"找不到包文件：{fullPath}", fullPath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static SkillPackageSummary Map(SkillPackageDto package) => new(
        package.Id, package.SkillPackageId, package.Name, package.Description ?? "", package.Version,
        package.FileName, package.FileSizeBytes, package.IsEnabled, package.SortOrder,
        package.CreatedAt, package.UpdatedAt);
}
