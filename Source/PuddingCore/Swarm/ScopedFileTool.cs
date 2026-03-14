using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// FileTool 的包装器，强制执行 Worker 作用域限制。
/// 在蜂群模式中，Worker Agent 只能在其分配的作用域内操作文件，
/// 此工具确保 Worker 无法修改作用域外的文件。
/// </summary>
public sealed class ScopedFileTool : ITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ITool _inner;
    private readonly WorkerScope _scope;

    /// <summary>
    /// 初始化 ScopedFileTool 的新实例。
    /// </summary>
    /// <param name="inner">内部 FileTool 实例，负责实际的文件操作。</param>
    /// <param name="scope">Worker 作用域，定义允许访问的路径和符号。</param>
    public ScopedFileTool(ITool inner, WorkerScope scope)
    {
        _inner = inner;
        _scope = scope;
    }

    public string Name => _inner.Name;
    public string Description => _inner.Description + " (scope-restricted)";
    public ToolParameterSchema Parameters => _inner.Parameters;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        // 解析参数以提取 path 和 action
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("path", out var pathProp))
            return "Error: path is required";

        var path = pathProp.GetString();
        if (string.IsNullOrEmpty(path))
            return "Error: path cannot be empty";

        var action = root.TryGetProperty("action", out var actionProp)
            ? actionProp.GetString()?.ToLower()
            : null;

        // 只对 write 和 list 操作进行作用域检查
        // read 操作通常不需要严格限制（但可以根据需要添加）
        if (action is "write" or "list")
        {
            var allowedPath = IsPathAllowed(path);
            if (!allowedPath)
            {
                var scopePaths = string.Join(", ", _scope.AllowedPaths);
                return $"Error: your scope is [{scopePaths}], cannot modify {path}";
            }
        }

        // 作用域内则委托给内部 FileTool 执行
        return await _inner.ExecuteAsync(argumentsJson, ct);
    }

    /// <summary>
    /// 检查给定路径是否在允许的作用域内。
    /// 使用 Path.GetFullPath() 解析绝对路径，然后检查是否以任何 AllowedPath 开头。
    /// </summary>
    /// <param name="path">要检查的文件或目录路径。</param>
    /// <returns>如果路径在作用域内返回 true，否则返回 false。</returns>
    public bool IsPathAllowed(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // 解析为绝对路径
        var fullPath = Path.GetFullPath(path);

        // 检查是否匹配任何允许的路径
        foreach (var allowedPattern in _scope.AllowedPaths)
        {
            if (IsPathMatch(fullPath, allowedPattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 检查路径是否匹配允许的模式（支持通配符 *）。
    /// </summary>
    private static bool IsPathMatch(string fullPath, string pattern)
    {
        // 处理通配符模式
        if (pattern.Contains("*"))
        {
            // 将通配符模式转换为前缀检查
            var prefix = pattern.Substring(0, pattern.IndexOf('*'));
            var prefixPath = Path.GetFullPath(prefix);
            return fullPath.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // 直接路径匹配
            var allowedPath = Path.GetFullPath(pattern);
            return fullPath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
