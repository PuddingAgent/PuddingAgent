using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Models;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SearchCodebaseTool — 代码库全文搜索 Skill。
/// 在工作目录的代码文件中搜索指定文本，支持正则表达式。
/// 不需要沙箱容器，由 Runtime 宿主直接使用 File.ReadAllText 读取文件。
/// V1 限制：跳过 > 1MB 的文件。
/// PermissionLevel: Low（只读）。
/// </summary>
public sealed class SearchCodebaseTool : IAgentSkill
{
    private readonly ILogger<SearchCodebaseTool> _logger;

    private const int DefaultMaxResults = 30;
    private const long MaxFileSizeBytes = 1 * 1024 * 1024; // 1MB — 大于此大小跳过
    private const int ContextLines = 1; // 匹配行前后各显示的行数

    // 默认搜索的代码文件扩展名
    private static readonly string[] DefaultCodeExtensions =
    [
        ".cs", ".ts", ".vue", ".py", ".js", ".jsx", ".tsx",
        ".json", ".md", ".yaml", ".yml", ".sql",
        ".csproj", ".sln", ".slnx", ".props", ".targets",
        ".html", ".css", ".scss", ".less",
        ".xml", ".config", ".txt", ".toml", ".ini", ".cfg",
    ];

    public SearchCodebaseTool(ILogger<SearchCodebaseTool> logger)
    {
        _logger = logger;
    }

    public string SkillId => "search_codebase";
    public string Name => "代码搜索";
    public string Description =>
        "在工作目录的代码文件中搜索指定文本。支持正则表达式。";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

    public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var query = request.Input?.Trim();
        if (string.IsNullOrEmpty(query))
            return Task.FromResult(Fail("Query is required."));

        // 也支持从 parameters 中获取 query
        if (request.Parameters.TryGetValue("query", out var q) && q.Length > 0)
            query = q;

        var caseSensitive = request.Parameters.TryGetValue("case_sensitive", out var cs)
                            && cs.Equals("true", StringComparison.OrdinalIgnoreCase);
        var maxResults = request.Parameters.TryGetValue("max_results", out var mr)
                         && int.TryParse(mr, out var parsed) ? parsed : DefaultMaxResults;

        // 解析文件 glob 过滤
        var patternStr = request.Parameters.TryGetValue("pattern", out var pat) && pat.Length > 0
            ? pat : string.Join(";", DefaultCodeExtensions.Select(e => $"*{e}"));

        _logger.LogInformation("[SearchCodebaseTool] agent={Agent} query={Query} pattern={Pattern} caseSensitive={CS}",
            request.AgentInstanceId, query, patternStr, caseSensitive);

        try
        {
            var result = SearchInCodebase(query, patternStr, caseSensitive, maxResults, ct);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchCodebaseTool] Search failed agent={Agent}", request.AgentInstanceId);
            return Task.FromResult(Fail(ex.Message));
        }
    }

    // ── 搜索逻辑 ───────────────────────────────────────────────────────

    private SkillResult SearchInCodebase(string query, string filePattern, bool caseSensitive,
        int maxResults, CancellationToken ct)
    {
        var cwd = Directory.GetCurrentDirectory();
        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(query, options | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        catch (RegexParseException ex)
        {
            return Fail($"Invalid regex pattern: {ex.Message}");
        }

        // 如果是普通文本而非正则，使用 Contains 加速
        var isPlainText = IsPlainText(query);

        // 收集文件
        var filePatterns = filePattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allFiles = new List<string>();
        foreach (var fp in filePatterns)
        {
            try
            {
                allFiles.AddRange(Directory.GetFiles(cwd, fp.Trim(), SearchOption.AllDirectories));
            }
            catch (DirectoryNotFoundException) { /* skip */ }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Search results for: \"{query}\" (case_sensitive={caseSensitive})");
        sb.AppendLine();

        int totalMatches = 0;
        foreach (var file in allFiles)
        {
            if (ct.IsCancellationRequested) break;
            if (totalMatches >= maxResults) break;

            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.Length > MaxFileSizeBytes)
                {
                    _logger.LogDebug("[SearchCodebaseTool] Skipping large file: {File} ({Size} bytes)",
                        file, fileInfo.Length);
                    continue;
                }

                var lines = File.ReadAllLines(file);
                var relativePath = Path.GetRelativePath(cwd, file);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (totalMatches >= maxResults) break;

                    bool matched;
                    if (isPlainText)
                    {
                        matched = caseSensitive
                            ? lines[i].Contains(query, StringComparison.Ordinal)
                            : lines[i].Contains(query, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        matched = regex.IsMatch(lines[i]);
                    }

                    if (!matched) continue;

                    // 输出匹配行及上下文
                    if (totalMatches > 0) sb.AppendLine();
                    sb.AppendLine($"{relativePath}:{i + 1}");

                    var contextStart = Math.Max(0, i - ContextLines);
                    var contextEnd = Math.Min(lines.Length - 1, i + ContextLines);

                    for (int ctx = contextStart; ctx <= contextEnd; ctx++)
                    {
                        var marker = ctx == i ? ">" : " ";
                        sb.AppendLine($"  {marker}{ctx + 1,4}: {lines[ctx]}");
                    }

                    totalMatches++;
                }
            }
            catch (UnauthorizedAccessException) { /* skip */ }
            catch (IOException) { /* skip */ }
        }

        if (totalMatches == 0)
            sb.AppendLine("(no matches found)");
        else if (totalMatches >= maxResults)
            sb.AppendLine($"\n... results truncated at {maxResults}");

        return Ok(sb.ToString());
    }

    // ── 辅助 ───────────────────────────────────────────────────────────

    /// <summary>判断查询字符串是否更像普通文本而非正则表达式。</summary>
    private static bool IsPlainText(string query)
    {
        // 如果包含正则特殊字符，则视为正则
        var regexSpecialChars = new[] { '\\', '^', '$', '.', '|', '?', '*', '+', '(', ')', '[', '{' };
        return !query.Any(c => regexSpecialChars.Contains(c));
    }

    private static SkillResult Ok(string output) =>
        new() { Success = true, Output = output, ExitCode = 0 };

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };
}
