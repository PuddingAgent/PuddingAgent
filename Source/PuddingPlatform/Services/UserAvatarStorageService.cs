using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services;

/// <summary>
/// 上传结果：返回对外访问 URL、磁盘文件名、原始 MIME 与字节大小。
/// </summary>
public sealed record UserAvatarSaveResult(
    string UrlPath,
    string FileName,
    string MimeType,
    long Length);

/// <summary>
/// 把用户上传的头像文件落到 wwwroot/user-avatars/ 下，
/// 由静态文件中间件对外提供服务。文件名以 userId 前缀防穿越。
/// </summary>
public sealed class UserAvatarStorageService
{
    private const string SubFolder = "user-avatars";
    private const string UrlPrefix = "/user-avatars/";

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif",
    };

    private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<UserAvatarStorageService> _logger;

    public UserAvatarStorageService(
        IWebHostEnvironment env,
        ILogger<UserAvatarStorageService> logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// 落盘上传流并返回对外可访问的 URL 路径。
    /// 调用方应在得到结果后再将 URL 写入数据库。
    /// </summary>
    public async Task<UserAvatarSaveResult> SaveAsync(
        string userId,
        Stream content,
        string? contentType,
        long? declaredLength,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId 不能为空", nameof(userId));
        if (content is null || !content.CanRead)
            throw new ArgumentException("上传内容不可读", nameof(content));

        var mime = NormalizeContentType(contentType);
        var ext = MimeToExtension[mime];

        var root = ResolveRoot();
        Directory.CreateDirectory(root);

        // 文件名形如 alice-3f9c0a1b2d4e5f6a7b8c9d0e1f2a3b4c.png
        // userId 仅允许字母数字 - _ 以防路径穿越；WebRootPath 之外不可被写入。
        var safeUserSegment = SanitizeUserId(userId);
        var fileName = $"{safeUserSegment}-{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(root, fileName);
        var fullRoot = Path.GetFullPath(root);
        var fullTarget = Path.GetFullPath(fullPath);
        if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("头像落盘路径越界");

        // 原子写：先写临时文件再 move，防止覆盖时半写文件被静态中间件读到。
        var tmpPath = fullPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var file = new FileStream(
                tmpPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                useAsync: true))
            {
                await content.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
            }

            File.Move(tmpPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "[UserAvatar] 临时文件清理失败 tmp={Path}", tmpPath); }
            }
        }

        var length = declaredLength ?? new FileInfo(fullPath).Length;
        _logger.LogInformation(
            "[UserAvatar] Saved user={UserId} file={FileName} mime={Mime} bytes={Length}",
            userId,
            fileName,
            mime,
            length);

        return new UserAvatarSaveResult(
            UrlPath: UrlPrefix + fileName,
            FileName: fileName,
            MimeType: mime,
            Length: length);
    }

    /// <summary>
    /// 尝试删除指定的头像文件（按文件名）。
    /// 仅删除 wwwroot/user-avatars/ 内的文件，避免误删其他位置。
    /// </summary>
    public bool TryDelete(string? urlOrFileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(urlOrFileName))
            return false;

        var fileName = urlOrFileName!;
        // 允许传 URL 路径或裸文件名
        if (fileName.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase))
            fileName = fileName[UrlPrefix.Length..];
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var root = Path.GetFullPath(ResolveRoot());
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(target))
            return false;

        try
        {
            File.Delete(target);
            _logger.LogInformation("[UserAvatar] Deleted file={FileName}", fileName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserAvatar] 删除失败 file={FileName}", fileName);
            return false;
        }
    }

    private string ResolveRoot()
    {
        var webRoot = !string.IsNullOrWhiteSpace(_env.WebRootPath)
            ? _env.WebRootPath
            : Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Path.Combine(webRoot, SubFolder);
    }

    private static string NormalizeContentType(string? contentType)
    {
        var normalized = string.IsNullOrWhiteSpace(contentType)
            ? string.Empty
            : contentType.Trim().ToLowerInvariant();
        // 允许带 charset 等参数
        var end = normalized.IndexOf(';');
        if (end > 0)
            normalized = normalized[..end].Trim();

        // image/jpg 非标准、兼容
        if (normalized == "image/jpg")
            normalized = "image/jpeg";

        if (!AllowedMimeTypes.Contains(normalized))
            throw new UnsupportedUserAvatarMediaTypeException(contentType);

        return normalized;
    }

    private static string SanitizeUserId(string userId)
    {
        var sanitized = new System.Text.StringBuilder(userId.Length);
        foreach (var ch in userId)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                sanitized.Append(ch);
            else
                sanitized.Append('_');
        }
        return sanitized.Length == 0 ? "user" : sanitized.ToString();
    }
}

/// <summary>上传的图片 MIME 不在允许列表时抛出。</summary>
public sealed class UnsupportedUserAvatarMediaTypeException(string? mimeType)
    : InvalidOperationException(
        $"不支持的用户头像 MIME 类型 '{mimeType}'。允许：image/png、image/jpeg、image/webp、image/gif。")
{
    public string? MimeType { get; } = mimeType;
}