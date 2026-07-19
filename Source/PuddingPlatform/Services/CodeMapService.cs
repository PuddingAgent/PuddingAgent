using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services;

/// <summary>
/// 从 code_map.md 加载并缓存 Tier1 内容（顶层目录 + 核心类索引，约 2-5KB）。
/// Tier1 收集逻辑：读取文件 → 按行解析 → 遇到 "## 🔑" 二级标题时进入 Tier1 收集模式 →
/// 收集表格行 → 遇到下一个 "## " 标题时停止 → 拼接为紧凑文本。
/// 同时收集 "顶层目录结构" 节。
/// </summary>
public interface ICodeMapService
{
    /// <summary>获取 Tier1 代码地图内容（线程安全，首次加载后缓存）。</summary>
    string GetTier1Content();
}

public sealed class CodeMapService : ICodeMapService
{
    private readonly string _codeMapPath;
    private readonly ILogger<CodeMapService> _logger;
    private string? _cachedTier1;
    private readonly object _lock = new();

    public CodeMapService(string codeMapPath, ILogger<CodeMapService> logger)
    {
        _codeMapPath = codeMapPath;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string GetTier1Content()
    {
        if (_cachedTier1 is not null)
            return _cachedTier1;

        lock (_lock)
        {
            if (_cachedTier1 is not null)
                return _cachedTier1;

            try
            {
                if (!File.Exists(_codeMapPath))
                {
                    _logger.LogWarning("[CodeMapService] code_map.md not found at {Path}", _codeMapPath);
                    _cachedTier1 = string.Empty;
                    return _cachedTier1;
                }

                var lines = File.ReadAllLines(_codeMapPath);
                _cachedTier1 = ParseTier1Content(lines);
                _logger.LogInformation(
                    "[CodeMapService] Tier1 code map loaded, size={Size} chars, path={Path}",
                    _cachedTier1.Length, _codeMapPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CodeMapService] Failed to load code_map.md from {Path}", _codeMapPath);
                _cachedTier1 = string.Empty;
            }

            return _cachedTier1;
        }
    }

    /// <summary>
    /// 解析 Tier1 内容：
    /// 1. 收集 "顶层目录结构" 节（ASCII 目录树）
    /// 2. 收集所有 "## 🔑" 二级标题及其下属表格行
    /// </summary>
    private static string ParseTier1Content(string[] lines)
    {
        var sb = new System.Text.StringBuilder();

        bool inTopDir = false;
        bool inKeySection = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // ── 收集 "## 顶层目录结构" 节 ──
            if (line.StartsWith("## 顶层目录结构"))
            {
                inTopDir = true;
                sb.AppendLine("## 顶层目录结构");
                continue;
            }

            if (inTopDir)
            {
                // 遇到下一个 "---" 或 "## " 时结束顶层目录收集
                if (line.StartsWith("---"))
                {
                    inTopDir = false;
                    sb.AppendLine("---");
                    continue;
                }

                if (line.StartsWith("## "))
                {
                    inTopDir = false;
                    // 继续处理当前行（可能是 🔑 标题）
                    goto processHeading;
                }

                // 保留目录树内容，但跳过空行和代码块标记
                if (!string.IsNullOrWhiteSpace(line) && line != "```")
                {
                    sb.AppendLine(line);
                }

                continue;
            }

            processHeading:

            // ── 收集 "## 🔑" Tier1 章节 ──
            if (line.StartsWith("## 🔑"))
            {
                inKeySection = true;
                sb.AppendLine();
                sb.AppendLine(line);
                continue;
            }

            if (inKeySection)
            {
                // 遇到下一个 "## " 或 "---" 时结束
                if (line.StartsWith("## ") || line.StartsWith("---"))
                {
                    inKeySection = false;

                    // 如果是新的 ## 🔑 标题，重新进入收集模式
                    if (line.StartsWith("## 🔑"))
                    {
                        inKeySection = true;
                        sb.AppendLine();
                        sb.AppendLine(line);
                    }

                    continue;
                }

                // 收集表格行（排除表头分隔行如 |------|------|）
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("|") && !trimmed.StartsWith("|---") && !trimmed.StartsWith("|:-"))
                {
                    // 跳过纯表头行（仅含 | **xxx** | **xxx** | 且不包含文件路径特征）
                    if (trimmed.Contains("| `") || trimmed.Contains("` |") || trimmed.Contains("| **"))
                    {
                        sb.AppendLine(line);
                    }
                }
            }
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? string.Empty : result;
    }
}
