namespace PuddingDesktop.Foundation;

/// <summary>One legacy skill package row. The card lists .tgz, but Core accepts .zip and .tar.gz only.</summary>
public sealed record SkillPackageSummary(
    int Id, string SkillPackageId, string Name, string Description, string Version,
    string FileName, long FileSizeBytes, bool IsEnabled, int SortOrder,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string SizeText => SkillPackageText.FormatBytes(FileSizeBytes);
    public string StateText => IsEnabled ? "已启用" : "已停用";
}

public sealed record SkillPackageMetaEdit(
    string SkillPackageId, string Name, string Description, bool IsEnabled, int SortOrder);

/// <summary>A file chosen from disk. The path is read by the store adapter, never by the shell.</summary>
public sealed record SkillPackageUploadEdit(
    string SkillPackageId, string Name, string Description, string Version, int SortOrder, string FilePath);

public sealed record SkillPackageFileEdit(string SkillPackageId, string Version, string FilePath);

/// <summary>
/// Task-shaped operations for the legacy skill-package page, implemented in Composition against the
/// shared SkillPackageService. Upload and download depend on object storage being configured; when it is
/// not, the real Core error must be shown instead of a fake success.
/// </summary>
public interface ISkillPackageSettings
{
    Task<IReadOnlyList<SkillPackageSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveMetaAsync(SkillPackageMetaEdit edit, CancellationToken cancellationToken = default);
    Task DeleteAsync(string skillPackageId, CancellationToken cancellationToken = default);
    Task UploadAsync(SkillPackageUploadEdit upload, CancellationToken cancellationToken = default);
    Task ReplaceFileAsync(SkillPackageFileEdit edit, CancellationToken cancellationToken = default);
    Task<string> GetDownloadUrlAsync(string skillPackageId, CancellationToken cancellationToken = default);
}

public static class SkillPackageText
{
    /// <summary>Mirrors SkillPackageService.AllowedExtensions; the shell must not widen it.</summary>
    public static IReadOnlyList<string> AllowedExtensions { get; } = [".zip", ".tar.gz"];

    public static IReadOnlyList<int> SortOrderSuggestions { get; } = [10, 50, 100, 200, 500];

    public static bool IsAllowedFile(string? fileName)
    {
        var lower = (fileName ?? "").ToLowerInvariant();
        return AllowedExtensions.Any(extension => lower.EndsWith(extension, StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> Validate(SkillPackageMetaEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.SkillPackageId)) errors.Add("请先选择技能包。");
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("名称不能为空。");
        if (edit.SortOrder < 0) errors.Add("排序不能为负数。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(SkillPackageUploadEdit upload)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(upload.SkillPackageId)) errors.Add("技能包 ID 不能为空。");
        else if (upload.SkillPackageId.Length > 80
                 || upload.SkillPackageId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                 || upload.SkillPackageId.Any(char.IsAsciiLetterUpper))
            errors.Add("技能包 ID 只允许小写字母、数字和连字符。");
        if (string.IsNullOrWhiteSpace(upload.Name)) errors.Add("名称不能为空。");
        if (string.IsNullOrWhiteSpace(upload.FilePath)) errors.Add("请选择要上传的包文件。");
        else if (!IsAllowedFile(upload.FilePath))
            errors.Add($"仅支持 {string.Join(" 或 ", AllowedExtensions)} 格式。");
        if (upload.SortOrder < 0) errors.Add("排序不能为负数。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(SkillPackageFileEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.SkillPackageId)) errors.Add("请先选择技能包。");
        if (string.IsNullOrWhiteSpace(edit.Version)) errors.Add("版本号不能为空。");
        if (string.IsNullOrWhiteSpace(edit.FilePath)) errors.Add("请选择要上传的包文件。");
        else if (!IsAllowedFile(edit.FilePath))
            errors.Add($"仅支持 {string.Join(" 或 ", AllowedExtensions)} 格式。");
        return errors;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 0 => "未知大小",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
