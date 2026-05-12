using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Models;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SearchFilesTool — 文件搜索/目录列表 Skill。
/// 使用 System.IO 在宿主机文件系统直接操作，不做沙箱隔离。
/// 支持 glob 模式匹配（如 *.cs, **/*.json）和目录列表（pattern 以 / 结尾）。
/// PermissionLevel: Low（只读）。
/// </summary>
public sealed class SearchFilesTool : IAgentSkill
{
    private readonly ILogger<SearchFilesTool> _logger;

    private const int DefaultMaxResults = 50;
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB — 仅用于大小显示，不过滤

    public SearchFilesTool(ILogger<SearchFilesTool> logger)
    {
        _logger = logger;
    }

    public string SkillId => "search_files";
    public string Name => "文件搜索";
    public string Description =>
        "搜索工作目录中的文件。支持 glob 模式匹配（如 *.cs, **/*.json）和目录列表。";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

    public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var pattern    = request.Parameters.TryGetValue("pattern", out var p) && p.Length > 0 ? p : "*";
        var recursive  = !request.Parameters.TryGetValue("recursive", out var r) || !r.Equals("false", StringComparison.OrdinalIgnoreCase);
        var maxResults = request.Parameters.TryGetValue("max_results", out var mr)
                         && int.TryParse(mr, out var parsed) ? parsed : DefaultMaxResults;

        // 确定搜索根目录
        var rootDir = ResolveAndValidateRoot(request.Parameters.TryGetValue("directory", out var d) ? d : null);
        if (rootDir == null)
            return Task.FromResult(Fail("Directory access denied or path not allowed."));

        _logger.LogInformation("[SearchFilesTool] agent={Agent} pattern={Pattern} root={Root} recursive={Recursive}",
            request.AgentInstanceId, pattern, rootDir, recursive);

        try
        {
            // pattern 以 / 结尾 → 列出目录内容
            if (pattern.EndsWith('/'))
                return Task.FromResult(ListDirectory(rootDir, pattern.TrimEnd('/'), maxResults));

            return Task.FromResult(SearchFiles(rootDir, pattern, recursive, maxResults));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[SearchFilesTool] Access denied agent={Agent} root={Root}",
                request.AgentInstanceId, rootDir);
            return Task.FromResult(Fail($"Access denied: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchFilesTool] Search failed agent={Agent} root={Root}",
                request.AgentInstanceId, rootDir);
            return Task.FromResult(Fail(ex.Message));
        }
    }

    // ── 目录列表 ───────────────────────────────────────────────────────

    private static SkillResult ListDirectory(string rootDir, string subPattern, int maxResults)
    {
        var searchDir = rootDir;
        // 如果 subPattern 非空且不是 "."，尝试作为子路径
        if (subPattern.Length > 0 && subPattern != ".")
            searchDir = Path.GetFullPath(Path.Combine(rootDir, subPattern));

        // 确保搜索目录仍在 rootDir 内
        if (!IsPathUnder(searchDir, rootDir))
            return Fail("Directory path escapes allowed root.");

        if (!Directory.Exists(searchDir))
            return Fail($"Directory not found: {searchDir}");

        var sb = new StringBuilder();
        sb.AppendLine($"Directory listing for: {searchDir}");
        sb.AppendLine();

        // 子目录
        var dirs = Directory.GetDirectories(searchDir).Take(maxResults).ToArray();
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            sb.AppendLine($"[DIR]  {name}/");
        }

        // 文件
        var files = Directory.GetFiles(searchDir).Take(maxResults - dirs.Length).ToArray();
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            sb.AppendLine($"[FILE] {info.Name}  ({FormatSize(info.Length)})  {info.LastWriteTime:yyyy-MM-dd HH:mm}");
        }

        var total = dirs.Length + files.Length;
        if (total >= maxResults)
            sb.AppendLine($"\n... results truncated at {maxResults}");

        return Ok(sb.ToString());
    }

    // ── Glob 文件搜索 ──────────────────────────────────────────────────

    private static SkillResult SearchFiles(string rootDir, string pattern, bool recursive, int maxResults)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var sb = new StringBuilder();

        // 将 glob 转换为 Windows 兼容的搜索模式
        // ** → 递归匹配任意目录（Directory.GetFiles 的 SearchOption.AllDirectories 已覆盖）
        // 将 **/*.cs 简化为 *.cs（因为 recursive 已经由 searchOption 控制）
        // * 本身 Directroy.GetFiles 也支持
        var searchPattern = SimplifyGlobToPattern(pattern);

        sb.AppendLine($"Search: {pattern} in {rootDir} (recursive={recursive})");
        sb.AppendLine();

        try
        {
            var files = Directory.GetFiles(rootDir, searchPattern, searchOption)
                .Take(maxResults + 1)
                .ToArray();

            int count = 0;
            foreach (var file in files)
            {
                if (count >= maxResults) break;

                // 安全检查：确保结果文件仍在 rootDir 内
                if (!IsPathUnder(file, rootDir)) continue;

                var info = new FileInfo(file);
                var relativePath = Path.GetRelativePath(rootDir, file);
                sb.AppendLine($"{relativePath}  ({FormatSize(info.Length)})  {info.LastWriteTime:yyyy-MM-dd HH:mm}");
                count++;
            }

            if (files.Length > maxResults)
                sb.AppendLine($"\n... results truncated at {maxResults} (more files exist)");

            if (count == 0)
                sb.AppendLine("(no matching files)");
        }
        catch (DirectoryNotFoundException)
        {
            return Fail($"Directory not found: {rootDir}");
        }

        return Ok(sb.ToString());
    }

    // ── 辅助 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 解析并验证搜索根目录。必须在允许的路径内，防止目录穿越。
    /// 默认使用当前工作目录。
    /// </summary>
    private static string? ResolveAndValidateRoot(string? requestedDir)
    {
        var cwd = Directory.GetCurrentDirectory();
        var root = cwd;

        if (!string.IsNullOrWhiteSpace(requestedDir))
        {
            try
            {
                root = Path.GetFullPath(requestedDir);
            }
            catch
            {
                return null;
            }
        }

        // 安全校验：必须是绝对路径且在允许范围内
        // V1: 允许访问 CWD 及其子目录，以及常见的项目目录
        var allowedRoots = new[]
        {
            cwd,
            Path.GetPathRoot(cwd) ?? string.Empty,
        };

        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCwd = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 路径必须在 CWD 子树内
        if (!normalized.StartsWith(normalizedCwd, StringComparison.OrdinalIgnoreCase))
            return null;

        return root;
    }

    /// <summary>检查 candidate 路径是否在 root 路径之下。</summary>
    private static bool IsPathUnder(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
               && (fullCandidate.Length == fullRoot.Length
                   || fullCandidate[fullRoot.Length] == Path.DirectorySeparatorChar
                   || fullCandidate[fullRoot.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>将 glob 简化为 Directory.GetFiles 兼容的搜索模式。</summary>
    private static string SimplifyGlobToPattern(string glob)
    {
        // **/*.cs → *.cs（recursive 由 searchOption 处理）
        // **/sub/*.cs → sub\*.cs（相对子目录匹配）
        var result = glob;

        // 移除前导 **/ 或 **\
        result = Regex.Replace(result, @"^\*\*[/\\]", "");

        // 如果结果为空，默认 *
        if (string.IsNullOrWhiteSpace(result))
            result = "*";

        return result;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
    }

    private static SkillResult Ok(string output) =>
        new() { Success = true, Output = output, ExitCode = 0 };

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };
}
